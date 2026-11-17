'use strict';
const fs = require('fs');
const path = require('path');
const { runPSJson, runPS, psLiteral, resolveShortcutTarget, isProtectedRegKey } = require('./util');
const { record } = require('./history');
const { BACKUP_DIR } = require('./backup');

const EXPORT_DIR = path.join(__dirname, '..', 'data', 'exports');

function formatInstallDate(raw) {
  const s = String(raw || '').trim();
  if (/^\d{8}$/.test(s)) {
    return `${s.slice(0, 4)}-${s.slice(4, 6)}-${s.slice(6, 8)}`;
  }
  return s;
}

function classifyProgram(p) {
  const text = `${p.DisplayName || ''} ${p.Publisher || ''}`.toLowerCase();
  if (/(visual c\+\+|redistributable|\.net|runtime|driver|nvidia|amd |intel|realtek|vulkan|directx|webview2|edgewebview|windows sdk)/i.test(text)) {
    return 'System & Runtimes';
  }
  if (/(chrome|firefox|edge|opera|brave|vivaldi|browser|discord|zoom|slack|telegram|whatsapp|skype|teams|putty|winscp|filezilla|qbittorrent|utorrent|onedrive|dropbox|vpn|thunderbird)/i.test(text)) {
    return 'Internet';
  }
  if (/(steam|epic games|ubisoft|ea app|gog galaxy|battle\.net|riot|xbox|roblox|minecraft|game)/i.test(text)) {
    return 'Games';
  }
  if (/(vlc|obs|audacity|gimp|photoshop|premiere|spotify|itunes|k-lite|codec|ffmpeg|handbrake|blender|davinci|media player|kodi|music|audio|video)/i.test(text)) {
    return 'Multimedia';
  }
  if (/(7-zip|winrar|notepad\+\+|visual studio|vs code|vscode|git|python|node\.js|openjdk|java|powertoys|everything|sharex|keepass|bitwarden|terminal|openuninstaller|cmake|wireshark|fiddler)/i.test(text)) {
    return 'Utilities';
  }
  return 'General';
}

const UNINSTALL_PATHS = [
  'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
  'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
  'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
];

const PROPS = [
  'DisplayName', 'DisplayVersion', 'Publisher', 'InstallDate', 'InstallLocation',
  'UninstallString', 'QuietUninstallString', 'ModifyPath', 'EstimatedSize', 'DisplayIcon',
  'NoRepair', 'NoModify', 'SystemComponent', 'ParentKeyName', 'ReleaseType',
  'URLInfoAbout', 'URLUpdateInfo', 'HelpLink', 'Comments', 'InstallSource', 'WindowsInstaller',
];

function isRecentInstall(formattedDate) {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(String(formattedDate || ''))) return false;
  const t = Date.parse(formattedDate + 'T00:00:00Z');
  if (!Number.isFinite(t)) return false;
  const diff = Date.now() - t;
  return diff >= -86400000 && diff <= 30 * 86400000;
}

function getInstalledPrograms(opts = {}) {
  const paths = UNINSTALL_PATHS.map(psLiteral).join(',');
  const props = PROPS.map(psLiteral).join(',');
  const includeSystem = !!opts.includeSystem;
  const script = `
    $paths = @(${paths})
    $props = @(${props})
    $includeSys = $${includeSystem ? 'true' : 'false'}
    foreach($path in $paths){
      if(-not (Test-Path -LiteralPath $path)){ continue }
      $arch = if($path -like '*WOW6432Node*') { '32-bit' } elseif($path -like 'HKCU:*') { 'User' } else { '64-bit' }
      foreach($k in Get-ChildItem -LiteralPath $path -ErrorAction SilentlyContinue){
        $item = Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue
        if(-not $item.DisplayName){ continue }
        if(-not $includeSys -and $item.SystemComponent -eq 1){ continue }
        if(-not $includeSys -and $item.ParentKeyName){ continue }
        $sizeMB = if($item.EstimatedSize){ [math]::Round(([double]$item.EstimatedSize) / 1024, 1) } else { 0 }
        $o = [ordered]@{
          DisplayName = [string]$item.DisplayName
          DisplayVersion = [string]$item.DisplayVersion
          Publisher = [string]$item.Publisher
          Architecture = $arch
          SizeMB = $sizeMB
          InstallDate = [string]$item.InstallDate
          InstallLocation = [string]$item.InstallLocation
          UninstallString = [string]$item.UninstallString
          QuietUninstallString = [string]$item.QuietUninstallString
          RegistryKey = $k.PSChildName
          HivePath = $path
          WindowsInstaller = ($item.WindowsInstaller -eq 1)
          EstimatedSize = if($item.EstimatedSize){ [int64]$item.EstimatedSize } else { 0 }
          DisplayIcon = [string]$item.DisplayIcon
          URLInfoAbout = [string]$item.URLInfoAbout
          URLUpdateInfo = [string]$item.URLUpdateInfo
          HelpLink = [string]$item.HelpLink
          Comments = [string]$item.Comments
          InstallSource = [string]$item.InstallSource
          ModifyPath = [string]$item.ModifyPath
          NoModify = ($item.NoModify -eq 1)
          NoRepair = ($item.NoRepair -eq 1)
        }
        [pscustomobject]$o
      }
    }
  `;
  const rows = runPSJson(script);
  const seen = new Set();
  return rows.filter(r => {
    const key = (r.DisplayName || '').toLowerCase() + '|' + (r.DisplayVersion || '') + '|' + (r.Architecture || '');
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  }).map(r => {
    const instDate = formatInstallDate(r.InstallDate);
    return Object.assign({}, r, {
      InstallDate: instDate,
      Category: classifyProgram(r),
      RecentInstall: isRecentInstall(instDate),
    });
  }).sort((a, b) => String(a.DisplayName || '').localeCompare(String(b.DisplayName || '')));
}

function wildcardToRegex(pattern) {
  const escaped = pattern.replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*/g, '.*')
    .replace(/\?/g, '.');
  return new RegExp('^' + escaped + '$', 'i');
}

function findPrograms(pattern, { wildcard = false, includeSystem = false } = {}) {
  const all = getInstalledPrograms({ includeSystem });
  const raw = String(pattern || '').trim();
  if (!raw) return all;

  const info = resolveShortcutTarget(raw);
  const terms = [raw];
  if (info.stem && info.stem !== raw) terms.push(info.stem);
  if (info.targetPath) terms.push(path.basename(info.targetPath, path.extname(info.targetPath)));

  const matchers = terms.map(t =>
    (wildcard || /[*?]/.test(t))
      ? wildcardToRegex(t)
      : new RegExp(t.replace(/[.+^${}()|[\]\\]/g, '\\$&'), 'i')
  );

  return all.filter(p =>
    matchers.some(re =>
      re.test(p.DisplayName || '') ||
      re.test(p.Publisher || '') ||
      re.test(p.RegistryKey || '') ||
      re.test(p.InstallLocation || '') ||
      re.test(p.UninstallString || '')
    )
  );
}

function exportPrograms(format = 'csv', opts = {}) {
  const fmt = String(format || 'csv').toLowerCase();
  const rows = getInstalledPrograms(opts);
  fs.mkdirSync(EXPORT_DIR, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const ext = fmt === 'html' ? 'html' : fmt === 'txt' ? 'txt' : 'csv';
  const file = path.join(EXPORT_DIR, `installed-programs-${stamp}.${ext}`);

  let content = '';
  if (ext === 'html') {
    const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const body = rows.map(r => `<tr><td>${esc(r.DisplayName)}</td><td>${esc(r.DisplayVersion)}</td><td>${esc(r.Publisher)}</td><td>${esc(r.SizeMB)}</td><td>${esc(r.InstallDate)}</td><td>${esc(r.Architecture)}</td><td>${esc(r.Category)}</td><td>${esc(r.InstallLocation)}</td></tr>`).join('\n');
    content = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Installed Programs</title><style>body{font-family:Segoe UI,sans-serif;margin:20px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ddd;padding:6px 8px;text-align:left;font-size:12px}th{background:#f2f4f8}</style></head><body><h2>Installed Programs (${rows.length})</h2><table><thead><tr><th>Name</th><th>Version</th><th>Publisher</th><th>Size (MB)</th><th>Install Date</th><th>Arch</th><th>Category</th><th>Install Location</th></tr></thead><tbody>${body}</tbody></table></body></html>`;
  } else if (ext === 'txt') {
    content = rows.map(r => `${r.DisplayName}\t${r.DisplayVersion || ''}\t${r.Publisher || ''}\t${r.SizeMB || 0} MB\t${r.InstallDate || ''}\t${r.Architecture || ''}\t${r.Category || ''}\t${r.InstallLocation || ''}`).join('\n');
  } else {
    const csvCell = v => `"${String(v == null ? '' : v).replace(/"/g, '""')}"`;
    const header = ['DisplayName', 'DisplayVersion', 'Publisher', 'SizeMB', 'InstallDate', 'Architecture', 'Category', 'InstallLocation', 'UninstallString', 'RegistryKey'].map(csvCell).join(',');
    const lines = rows.map(r => [r.DisplayName, r.DisplayVersion, r.Publisher, r.SizeMB, r.InstallDate, r.Architecture, r.Category, r.InstallLocation, r.UninstallString, r.RegistryKey].map(csvCell).join(','));
    content = [header, ...lines].join('\r\n');
  }

  fs.writeFileSync(file, content, 'utf8');
  record('export-programs', file, `format=${ext} count=${rows.length}`);
  return { ok: true, format: ext, count: rows.length, file };
}

function removeProgramEntry({ hivePath, registryKey, name }) {
  const hive = String(hivePath || '').trim()
    .replace(/^HKEY_LOCAL_MACHINE/i, 'HKLM')
    .replace(/^HKEY_CURRENT_USER/i, 'HKCU')
    .replace(/^HKLM:\\/i, 'HKLM\\')
    .replace(/^HKCU:\\/i, 'HKCU\\');
  const key = String(registryKey || '').trim();
  if (!hive || !key) return { ok: false, error: 'HivePath and RegistryKey required' };
  const fullKey = `${hive}\\${key}`;
  if (isProtectedRegKey(fullKey)) return { ok: false, error: 'Refusing to delete protected registry key: ' + fullKey };

  fs.mkdirSync(BACKUP_DIR, { recursive: true });
  const safe = String(name || key).replace(/[^a-zA-Z0-9._-]+/g, '_').slice(0, 40);
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const backupFile = path.join(BACKUP_DIR, `entry-${safe}-${stamp}.reg`);
  runPS(`& reg.exe export ${psLiteral(fullKey)} ${psLiteral(backupFile)} /y 2>&1`);
  runPS(`& reg.exe delete ${psLiteral(fullKey)} /f 2>&1`);
  record('remove-entry', name || key, fullKey);
  return { ok: true, key: fullKey, backupFile };
}

module.exports = { getInstalledPrograms, findPrograms, exportPrograms, removeProgramEntry, classifyProgram, formatInstallDate, isRecentInstall, UNINSTALL_PATHS, EXPORT_DIR };