'use strict';
const fs = require('fs');
const path = require('path');
const { DatabaseSync } = require('node:sqlite');
const { runPSJson, psLiteral } = require('./util');
const { record, DB_PATH } = require('./history');

const DATA_DIR = path.join(__dirname, '..', 'data');
fs.mkdirSync(DATA_DIR, { recursive: true });
const ACTIVE_FILE = path.join(DATA_DIR, 'active-trace.json');
const EXPORT_DIR = path.join(DATA_DIR, 'exports');

let db;
function getDb() {
  if (!db) {
    db = new DatabaseSync(DB_PATH);
    db.exec(`
      PRAGMA journal_mode = WAL;
      PRAGMA busy_timeout = 5000;
      PRAGMA foreign_keys = ON;
      CREATE TABLE IF NOT EXISTS traced_logs (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        name TEXT NOT NULL,
        installer_path TEXT,
        started_at TEXT NOT NULL,
        stopped_at TEXT,
        status TEXT NOT NULL DEFAULT 'completed',
        items_count INTEGER NOT NULL DEFAULT 0,
        uninstall_string TEXT,
        install_location TEXT
      );
      CREATE TABLE IF NOT EXISTS traced_items (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        trace_id INTEGER NOT NULL,
        kind TEXT NOT NULL,
        path TEXT NOT NULL,
        detail TEXT,
        FOREIGN KEY (trace_id) REFERENCES traced_logs(id) ON DELETE CASCADE
      );
      CREATE INDEX IF NOT EXISTS idx_traced_items_trace ON traced_items(trace_id);
    `);
  }
  return db;
}

const NOISE_REGEX = /\\(Temp|Tmp|INetCache|CrashDumps|WER|Cache|Code Cache|GPUCache|DawnCache|D3DSCache|Service Worker|ShaderCache|GrShaderCache|ToastNotification|PushNotifications|Recent|CryptnetUrlCache|Diagnostics|Packages|Microsoft\\Windows\\Explorer|Microsoft\\Edge\\User Data|Google\\Chrome\\User Data)(\\|$)/i;

function captureSnapshot() {
  const script = `
    function Enum-Dirs {
      param([string]$Root, [int]$Depth)
      if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) { return }
      Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $_.FullName
        if ($Depth -gt 1) {
          Enum-Dirs -Root $_.FullName -Depth ($Depth - 1)
        }
      }
    }

    function Enum-Files {
      param([string]$Root, [int]$Depth)
      if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) { return }
      Get-ChildItem -LiteralPath $Root -File -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
      if ($Depth -gt 0) {
        Get-ChildItem -LiteralPath $Root -Directory -ErrorAction SilentlyContinue | ForEach-Object {
          Enum-Files -Root $_.FullName -Depth ($Depth - 1)
        }
      }
    }

    $dirRoots = @(
      @{ r = "$env:SystemDrive\\"; d = 1 },
      @{ r = $env:ProgramFiles; d = 2 },
      @{ r = \${env:ProgramFiles(x86)}; d = 2 },
      @{ r = $env:ProgramData; d = 2 },
      @{ r = $env:LOCALAPPDATA; d = 2 },
      @{ r = $env:APPDATA; d = 2 }
    )
    $dirs = @(foreach ($cfg in $dirRoots) { Enum-Dirs -Root $cfg.r -Depth $cfg.d }) | Sort-Object -Unique

    $fileRoots = @(
      @{ r = "$env:USERPROFILE\\Desktop"; d = 1 },
      @{ r = "$env:PUBLIC\\Desktop"; d = 1 },
      @{ r = "$env:APPDATA\\Microsoft\\Windows\\Start Menu\\Programs"; d = 2 },
      @{ r = "$env:ProgramData\\Microsoft\\Windows\\Start Menu\\Programs"; d = 2 }
    )
    $files = @(foreach ($cfg in $fileRoots) { Enum-Files -Root $cfg.r -Depth $cfg.d }) | Sort-Object -Unique

    $regKeys = @(foreach ($rk in @('HKCU:\\Software', 'HKLM:\\SOFTWARE', 'HKLM:\\SOFTWARE\\WOW6432Node')) {
      if (Test-Path -LiteralPath $rk) {
        Get-ChildItem -LiteralPath $rk -ErrorAction SilentlyContinue | ForEach-Object {
          $_.Name
          Get-ChildItem -LiteralPath $_.PSPath -ErrorAction SilentlyContinue | ForEach-Object {
            $_.Name
          }
        }
      }
    }) | Sort-Object -Unique

    $regValues = @(foreach ($ak in @(
      'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run',
      'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run'
    )) {
      if (Test-Path -LiteralPath $ak) {
        $item = Get-ItemProperty -LiteralPath $ak -ErrorAction SilentlyContinue
        if ($item) {
          foreach ($prop in $item.PSObject.Properties) {
            if ($prop.Name -notlike 'PS*') {
              ($ak -replace ':','') + '\\' + $prop.Name
            }
          }
        }
      }
    }) | Sort-Object -Unique

    $uninstall = @(foreach ($uk in @(
      'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
      'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall'
    )) {
      if (Test-Path -LiteralPath $uk) {
        Get-ChildItem -LiteralPath $uk -ErrorAction SilentlyContinue | ForEach-Object {
          $p = Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
          [pscustomobject]@{
            Key = $_.Name
            DisplayName = if ($p.DisplayName) { [string]$p.DisplayName } else { '' }
            UninstallString = if ($p.UninstallString) { [string]$p.UninstallString } else { '' }
            InstallLocation = if ($p.InstallLocation) { [string]$p.InstallLocation } else { '' }
          }
        }
      }
    })

    [pscustomobject]@{
      dirs = @($dirs)
      files = @($files)
      regKeys = @($regKeys)
      regValues = @($regValues)
      uninstall = @($uninstall)
    }
  `;
  const res = runPSJson(script, { timeout: 60000 });
  return res[0] || { dirs: [], files: [], regKeys: [], regValues: [], uninstall: [] };
}

function collectFilesInNewDirs(newDirs, limit = 300) {
  if (!newDirs.length) return [];
  const filtered = newDirs.filter(d => !NOISE_REGEX.test(d)).slice(0, 30);
  if (!filtered.length) return [];
  const list = filtered.map(psLiteral).join(',');
  const script = `
    $collected = @(foreach ($d in @(${list})) {
      if (Test-Path -LiteralPath $d) {
        Get-ChildItem -LiteralPath $d -File -Recurse -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
      }
    })
    $collected | Select-Object -First ${Number(limit)} | ForEach-Object { [pscustomobject]@{ Path = $_ } }
  `;
  return runPSJson(script, { timeout: 30000 }).map(x => x.Path).filter(Boolean);
}

function getActiveTrace() {
  if (!fs.existsSync(ACTIVE_FILE)) return null;
  try {
    const raw = JSON.parse(fs.readFileSync(ACTIVE_FILE, 'utf8'));
    return {
      active: true,
      name: raw.name,
      installerPath: raw.installerPath || '',
      startedAt: raw.startedAt,
      baselineDirs: (raw.snapshot && raw.snapshot.dirs ? raw.snapshot.dirs.length : 0),
      baselineRegKeys: (raw.snapshot && raw.snapshot.regKeys ? raw.snapshot.regKeys.length : 0),
    };
  } catch {
    return null;
  }
}

function startTrace(name, installerPath = '') {
  if (fs.existsSync(ACTIVE_FILE)) {
    const cur = getActiveTrace();
    if (cur) throw new Error(`Trace already active for "${cur.name}" (started ${cur.startedAt}). Stop it before starting another.`);
  }
  const cleanName = String(name || '').trim() || (installerPath ? path.basename(installerPath, path.extname(installerPath)) : 'Tracked Installation');
  const snapshot = captureSnapshot();
  const payload = {
    name: cleanName,
    installerPath: String(installerPath || ''),
    startedAt: new Date().toISOString(),
    snapshot,
  };
  fs.writeFileSync(ACTIVE_FILE, JSON.stringify(payload), 'utf8');
  record('trace-start', cleanName, `baseline dirs=${snapshot.dirs.length} reg=${snapshot.regKeys.length}`);
  return {
    active: true,
    name: payload.name,
    installerPath: payload.installerPath,
    startedAt: payload.startedAt,
    baselineDirs: snapshot.dirs.length,
    baselineFiles: snapshot.files.length,
    baselineRegKeys: snapshot.regKeys.length,
    baselineUninstall: snapshot.uninstall.length,
  };
}

function stopTrace(overrideName = '') {
  if (!fs.existsSync(ACTIVE_FILE)) {
    throw new Error('No active installation trace found.');
  }
  const active = JSON.parse(fs.readFileSync(ACTIVE_FILE, 'utf8'));
  const before = active.snapshot || { dirs: [], files: [], regKeys: [], regValues: [], uninstall: [] };
  const after = captureSnapshot();
  const stoppedAt = new Date().toISOString();

  const beforeDirs = new Set((before.dirs || []).map(x => x.toLowerCase()));
  const beforeFiles = new Set((before.files || []).map(x => x.toLowerCase()));
  const beforeRegKeys = new Set((before.regKeys || []).map(x => x.toLowerCase()));
  const beforeRegValues = new Set((before.regValues || []).map(x => x.toLowerCase()));
  const beforeUninstall = new Set((before.uninstall || []).map(u => String(u.Key || '').toLowerCase()));

  const newDirs = (after.dirs || []).filter(d => !beforeDirs.has(d.toLowerCase()) && !NOISE_REGEX.test(d));
  const newFiles = (after.files || []).filter(f => !beforeFiles.has(f.toLowerCase()) && !NOISE_REGEX.test(f));
  const newRegKeys = (after.regKeys || []).filter(k => !beforeRegKeys.has(k.toLowerCase()));
  const newRegValues = (after.regValues || []).filter(v => !beforeRegValues.has(v.toLowerCase()));
  const newUninstall = (after.uninstall || []).filter(u => !beforeUninstall.has(String(u.Key || '').toLowerCase()));

  // Pick top-most created directories to scan inner files cleanly
  const sortedNewDirs = [...newDirs].sort((a, b) => a.length - b.length);
  const topNewDirs = sortedNewDirs.filter((d, i) => !sortedNewDirs.slice(0, i).some(p => d.toLowerCase().startsWith(p.toLowerCase() + '\\')));
  const nestedFiles = collectFilesInNewDirs(topNewDirs, 300);

  const items = [];
  const seenItems = new Set();
  const pushItem = (kind, itemPath, detail = '') => {
    const key = kind + '|' + String(itemPath).toLowerCase();
    if (!itemPath || seenItems.has(key)) return;
    seenItems.add(key);
    items.push({ kind, path: itemPath, detail });
  };

  for (const d of newDirs) pushItem('Dir', d, 'Created directory');
  for (const f of newFiles) pushItem('File', f, 'Created shortcut/file');
  for (const f of nestedFiles) pushItem('File', f, 'Installed file');
  for (const u of newUninstall) pushItem('RegKey', u.Key, `Uninstall entry: ${u.DisplayName || ''}`);
  for (const k of newRegKeys) pushItem('RegKey', k, 'Created registry key');
  for (const v of newRegValues) pushItem('RegValue', v, 'Created autorun value');

  const detectedUninstall = newUninstall.find(u => u.DisplayName) || newUninstall[0] || null;
  const finalName = String(overrideName || '').trim()
    || (detectedUninstall && detectedUninstall.DisplayName)
    || active.name
    || 'Tracked Program';
  const uninstallString = detectedUninstall ? detectedUninstall.UninstallString : '';
  const installLocation = (detectedUninstall && detectedUninstall.InstallLocation)
    || (topNewDirs.find(d => /program files/i.test(d)) || topNewDirs[0] || '');

  const conn = getDb();
  const ins = conn.prepare(`
    INSERT INTO traced_logs (name, installer_path, started_at, stopped_at, status, items_count, uninstall_string, install_location)
    VALUES (?, ?, ?, ?, 'completed', ?, ?, ?)
  `).run(finalName, active.installerPath || '', active.startedAt, stoppedAt, items.length, uninstallString, installLocation);
  const traceId = Number(ins.lastInsertRowid);

  const insItem = conn.prepare('INSERT INTO traced_items (trace_id, kind, path, detail) VALUES (?, ?, ?, ?)');
  conn.exec('BEGIN');
  try {
    for (const it of items) {
      insItem.run(traceId, it.kind, it.path, it.detail);
    }
    conn.exec('COMMIT');
  } catch (e) {
    conn.exec('ROLLBACK');
    throw e;
  }

  try { fs.unlinkSync(ACTIVE_FILE); } catch {}
  record('trace-stop', finalName, `id=${traceId} items=${items.length}`);

  return {
    id: traceId,
    name: finalName,
    installerPath: active.installerPath || '',
    startedAt: active.startedAt,
    stoppedAt,
    itemsCount: items.length,
    uninstallString,
    installLocation,
    items: items.slice(0, 200),
  };
}

function listTraces() {
  const conn = getDb();
  return conn.prepare(`
    SELECT id, name, installer_path AS InstallerPath, started_at AS StartedAt, stopped_at AS StoppedAt,
           status AS Status, items_count AS ItemsCount, uninstall_string AS UninstallString, install_location AS InstallLocation
    FROM traced_logs
    ORDER BY id DESC
  `).all();
}

function getTraceItems(traceId) {
  const id = Number(traceId);
  if (!id) return { trace: null, items: [] };
  const conn = getDb();
  const trace = conn.prepare(`
    SELECT id, name, installer_path AS InstallerPath, started_at AS StartedAt, stopped_at AS StoppedAt,
           status AS Status, items_count AS ItemsCount, uninstall_string AS UninstallString, install_location AS InstallLocation
    FROM traced_logs WHERE id = ?
  `).get(id) || null;
  const items = conn.prepare(`
    SELECT id, kind AS Kind, path AS Path, detail AS Detail
    FROM traced_items WHERE trace_id = ? ORDER BY id ASC
  `).all(id);
  return { trace, items };
}

function deleteTrace(traceId) {
  const id = Number(traceId);
  if (!id) throw new Error('traceId is required');
  const conn = getDb();
  const existing = conn.prepare('SELECT name FROM traced_logs WHERE id = ?').get(id);
  conn.prepare('DELETE FROM traced_items WHERE trace_id = ?').run(id);
  const res = conn.prepare('DELETE FROM traced_logs WHERE id = ?').run(id);
  if (existing) record('trace-delete', existing.name, `id=${id}`);
  return { deleted: Number(res.changes || 0) > 0, id };
}

function exportTrace(traceId, format = 'json') {
  const { trace, items } = getTraceItems(traceId);
  if (!trace) throw new Error('Trace not found: ' + traceId);
  fs.mkdirSync(EXPORT_DIR, { recursive: true });
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const fmt = String(format || 'json').toLowerCase() === 'html' ? 'html' : 'json';
  const file = path.join(EXPORT_DIR, `trace-${trace.id}-${stamp}.${fmt}`);

  if (fmt === 'html') {
    const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
    const rowsHtml = items.map(it => `<tr><td>${esc(it.Kind)}</td><td>${esc(it.Path)}</td><td>${esc(it.Detail)}</td></tr>`).join('\n');
    const html = `<!DOCTYPE html><html><head><meta charset="utf-8"><title>Trace: ${esc(trace.name)}</title><style>body{font-family:Segoe UI,sans-serif;margin:20px}table{border-collapse:collapse;width:100%}th,td{border:1px solid #ddd;padding:6px 8px;text-align:left;font-size:12px}th{background:#f2f4f8}</style></head><body><h2>Traced Program: ${esc(trace.name)}</h2><p>Started: ${esc(trace.StartedAt)} | Stopped: ${esc(trace.StoppedAt)} | InstallLocation: ${esc(trace.InstallLocation)} | Items: ${items.length}</p><table><thead><tr><th>Kind</th><th>Path</th><th>Detail</th></tr></thead><tbody>${rowsHtml}</tbody></table></body></html>`;
    fs.writeFileSync(file, html, 'utf8');
  } else {
    fs.writeFileSync(file, JSON.stringify({ version: 1, trace, items }, null, 2), 'utf8');
  }
  record('trace-export', trace.name, `format=${fmt} file=${file}`);
  return { ok: true, id: trace.id, format: fmt, file, count: items.length };
}

function importTrace(filePath) {
  const full = String(filePath || '').trim();
  if (!full || !fs.existsSync(full)) throw new Error('Trace export file not found');
  const parsed = JSON.parse(fs.readFileSync(full, 'utf8').replace(/^\uFEFF/, ''));
  if (!parsed || !parsed.trace || !Array.isArray(parsed.items)) {
    throw new Error('Invalid trace export JSON format');
  }
  const t = parsed.trace;
  const name = String(t.name || path.basename(full, '.json'));
  const conn = getDb();
  const ins = conn.prepare(`
    INSERT INTO traced_logs (name, installer_path, started_at, stopped_at, status, items_count, uninstall_string, install_location)
    VALUES (?, ?, ?, ?, 'imported', ?, ?, ?)
  `).run(
    name,
    String(t.InstallerPath || ''),
    String(t.StartedAt || new Date().toISOString()),
    String(t.StoppedAt || new Date().toISOString()),
    parsed.items.length,
    String(t.UninstallString || ''),
    String(t.InstallLocation || '')
  );
  const newId = Number(ins.lastInsertRowid);
  const insItem = conn.prepare('INSERT INTO traced_items (trace_id, kind, path, detail) VALUES (?, ?, ?, ?)');
  conn.exec('BEGIN');
  try {
    for (const it of parsed.items) {
      insItem.run(newId, String(it.Kind || it.kind || 'File'), String(it.Path || it.path || ''), String(it.Detail || it.detail || 'Imported'));
    }
    conn.exec('COMMIT');
  } catch (e) {
    conn.exec('ROLLBACK');
    throw e;
  }
  record('trace-import', name, `id=${newId} items=${parsed.items.length}`);
  return { ok: true, id: newId, name, itemsCount: parsed.items.length };
}

module.exports = {
  startTrace,
  stopTrace,
  getActiveTrace,
  listTraces,
  getTraceItems,
  deleteTrace,
  exportTrace,
  importTrace,
};
