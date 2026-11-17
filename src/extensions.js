'use strict';
const fs = require('fs');
const path = require('path');
const { runPSJson } = require('./util');
const { record } = require('./history');

function resolveMsgName(extVerDir, rawName, defaultLocale = 'en') {
  if (!rawName || typeof rawName !== 'string') return '(unknown)';
  const m = rawName.match(/^__MSG_(.+)__$/);
  if (!m) return rawName;
  const key = m[1];
  for (const loc of [defaultLocale, 'en', 'en_US', 'en_GB']) {
    const locFile = path.join(extVerDir, '_locales', loc, 'messages.json');
    if (!fs.existsSync(locFile)) continue;
    try {
      const json = JSON.parse(fs.readFileSync(locFile, 'utf8'));
      const entry = json[key] || json[key.toLowerCase()];
      if (entry && entry.message) return entry.message;
    } catch (_) {}
  }
  return rawName;
}

function chromiumExtensions(browserName, browserPath) {
  const out = [];
  if (!fs.existsSync(browserPath)) return out;
  for (const profile of fs.readdirSync(browserPath, { withFileTypes: true })) {
    if (!profile.isDirectory()) continue;
    if (profile.name !== 'Default' && !profile.name.startsWith('Profile ')) continue;
    const extDir = path.join(browserPath, profile.name, 'Extensions');
    if (!fs.existsSync(extDir)) continue;
    for (const id of fs.readdirSync(extDir, { withFileTypes: true })) {
      if (!id.isDirectory()) continue;
      let versions = [];
      try { versions = fs.readdirSync(path.join(extDir, id.name), { withFileTypes: true }); } catch (_) { continue; }
      for (const v of versions) {
        if (!v.isDirectory()) continue;
        const verPath = path.join(extDir, id.name, v.name);
        const manifestPath = path.join(verPath, 'manifest.json');
        if (!fs.existsSync(manifestPath)) continue;
        let name = id.name, version = v.name, description = '';
        try {
          const m = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
          name = resolveMsgName(verPath, m.name || id.name, m.default_locale || 'en');
          version = m.version || version;
          description = resolveMsgName(verPath, m.description || '', m.default_locale || 'en');
        } catch (_) {}
        out.push({
          browser: browserName,
          profile: profile.name,
          id: id.name,
          name,
          version,
          status: id.name.endsWith('.disabled') || verPath.endsWith('.disabled') ? 'disabled' : 'enabled',
          description,
          path: verPath,
        });
      }
    }
  }
  return out;
}

function firefoxExtensions() {
  const appData = process.env.APPDATA || '';
  const root = path.join(appData, 'Mozilla', 'Firefox', 'Profiles');
  const out = [];
  if (!fs.existsSync(root)) return out;
  for (const prof of fs.readdirSync(root, { withFileTypes: true })) {
    if (!prof.isDirectory()) continue;
    const extJson = path.join(root, prof.name, 'extensions.json');
    if (!fs.existsSync(extJson)) continue;
    try {
      const data = JSON.parse(fs.readFileSync(extJson, 'utf8'));
      for (const a of (data.addons || [])) {
        if (a.location === 'app-builtin' || a.location === 'app-system-defaults') continue;
        out.push({
          browser: 'Firefox',
          profile: prof.name,
          id: a.id,
          name: (a.defaultLocale && a.defaultLocale.name) || a.id,
          version: a.version || '',
          status: a.active ? 'enabled' : 'disabled',
          description: (a.defaultLocale && a.defaultLocale.description) || '',
          path: a.path || path.join(root, prof.name, 'extensions', a.id + '.xpi'),
        });
      }
    } catch (_) {}
  }
  return out;
}

function registryExtensions() {
  const rows = runPSJson(`
    foreach($rp in @(
      'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Browser Helper Objects',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Browser Helper Objects',
      'HKLM:\\SOFTWARE\\Google\\Chrome\\Extensions',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Google\\Chrome\\Extensions',
      'HKLM:\\SOFTWARE\\Microsoft\\Edge\\Extensions',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Edge\\Extensions'
    )){
      if(-not (Test-Path -LiteralPath $rp)){ continue }
      $browser = if($rp -like '*Chrome*'){ 'Chrome (Policy/Reg)' } elseif($rp -like '*Edge*'){ 'Edge (Policy/Reg)' } else { 'IE BHO' }
      foreach($k in Get-ChildItem -LiteralPath $rp){
        $props = Get-ItemProperty -LiteralPath $k.PSPath
        $nm = [string]($props.'(default)')
        if(-not $nm){ $nm = $k.PSChildName }
        [pscustomobject]@{
          browser     = $browser
          profile     = 'System'
          id          = $k.PSChildName
          name        = $nm
          version     = [string]$props.version
          status      = 'enabled'
          description = 'Registry-registered extension / BHO'
          path        = $k.Name
        }
      }
    }
  `);
  return rows;
}

function scan() {
  const local = process.env.LOCALAPPDATA || '';
  const browsers = [
    ['Chrome', path.join(local, 'Google', 'Chrome', 'User Data')],
    ['Edge', path.join(local, 'Microsoft', 'Edge', 'User Data')],
    ['Brave', path.join(local, 'BraveSoftware', 'Brave-Browser', 'User Data')],
  ];
  let all = [];
  for (const [name, p] of browsers) all = all.concat(chromiumExtensions(name, p));
  all = all.concat(firefoxExtensions(), registryExtensions());
  return all;
}

function setEnabled(extPath, enabled) {
  if (!extPath || extPath.startsWith('HKEY_') || extPath.startsWith('HKLM') || extPath.startsWith('HKCU')) {
    return { ok: false, error: 'Registry policy extensions cannot be toggled by folder rename.' };
  }
  const target = extPath.replace(/\.disabled$/, '');
  const desired = enabled ? target : target + '.disabled';
  try {
    if (fs.existsSync(target) && desired !== target) fs.renameSync(target, desired);
    else if (fs.existsSync(desired) && desired === target) fs.renameSync(desired, target);
    record('extension', path.basename(extPath), enabled ? 'enabled' : 'disabled');
    return { ok: true, path: desired, enabled };
  } catch (e) {
    record('extension', path.basename(extPath), 'error: ' + e.message, 'error');
    return { ok: false, error: e.message };
  }
}

module.exports = { scan, setEnabled };