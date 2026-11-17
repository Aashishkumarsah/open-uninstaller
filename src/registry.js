'use strict';
const fs = require('fs');
const path = require('path');
const { runPSJson, runPS, psLiteral, isProtectedRegKey } = require('./util');
const { BACKUP_DIR } = require('./backup');
const { record } = require('./history');

// Registry optimizer: detect stale Uninstall entries, broken Autorun paths, and broken App Paths.
function scanInvalid() {
  const rows = runPSJson(`
    $paths = @(
      'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall',
      'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall'
    )
    foreach($p in $paths){
      if(-not (Test-Path -LiteralPath $p)){ continue }
      foreach($k in Get-ChildItem -LiteralPath $p){
        $i = Get-ItemProperty -LiteralPath $k.PSPath
        if($i.DisplayName -and $i.InstallLocation -and $i.InstallLocation -match '^[A-Za-z]:\\\\'){
          if(-not (Test-Path -LiteralPath $i.InstallLocation)){
            [pscustomobject]@{ Issue='Stale Uninstall'; Name=$i.DisplayName; Key=$k.PSChildName; MissingPath=$i.InstallLocation; HivePath=$p }
          }
        }
      }
    }
    foreach($rk in @(
      'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run',
      'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run'
    )){
      if(-not (Test-Path -LiteralPath $rk)){ continue }
      $pr = Get-ItemProperty -LiteralPath $rk
      foreach($prop in $pr.PSObject.Properties){
        if($prop.Name -like 'PS*' -or -not $prop.Value){ continue }
        $cmd = [string]$prop.Value
        $exe = $cmd.Trim()
        if($exe.StartsWith('"')){ $e = $exe.IndexOf('"',1); if($e -gt 1){ $exe = $exe.Substring(1,$e-1) } }
        elseif($exe.ToLowerInvariant().Contains('.exe')){ $idx = $exe.ToLowerInvariant().IndexOf('.exe'); $exe = $exe.Substring(0,$idx+4) }
        $exe = [Environment]::ExpandEnvironmentVariables($exe.Trim())
        if($exe -match '^[A-Za-z]:\\\\' -and -not (Test-Path -LiteralPath $exe)){
          [pscustomobject]@{ Issue='Broken Startup'; Name=$prop.Name; Key=$prop.Name; MissingPath=$exe; HivePath=$rk }
        }
      }
    }
    $appPaths = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\App Paths'
    if(Test-Path -LiteralPath $appPaths){
      foreach($ak in Get-ChildItem -LiteralPath $appPaths){
        $def = [string](Get-ItemProperty -LiteralPath $ak.PSPath).'(default)'
        $def = [Environment]::ExpandEnvironmentVariables($def.Trim().Trim('"'))
        if($def -match '^[A-Za-z]:\\\\' -and -not (Test-Path -LiteralPath $def)){
          [pscustomobject]@{ Issue='Missing App Path'; Name=$ak.PSChildName; Key=$ak.PSChildName; MissingPath=$def; HivePath=$appPaths }
        }
      }
    }
  `, { timeout: 60000 });
  record('registry-scan', 'invalid-entries', `count=${rows.length}`);
  return rows;
}

function cleanInvalid(items = []) {
  if (!Array.isArray(items) || !items.length) return { ok: true, removed: 0, errors: [] };
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const bkDir = path.join(BACKUP_DIR, `regopt-${stamp}`);
  fs.mkdirSync(bkDir, { recursive: true });
  let removed = 0;
  const errors = [];

  for (let i = 0; i < items.length; i++) {
    const it = items[i];
    try {
      const regHive = String(it.HivePath || '').replace(/^HKLM:\\/i, 'HKLM\\').replace(/^HKCU:\\/i, 'HKCU\\');
      if (it.Issue === 'Broken Startup') {
        runPS(`& reg.exe export ${psLiteral(regHive)} ${psLiteral(path.join(bkDir, `run_${i}.reg`))} /y`);
        runPS(`Remove-ItemProperty -Path ${psLiteral(it.HivePath)} -Name ${psLiteral(it.Key)} -Force -ErrorAction Stop`);
        removed++;
      } else {
        const fullKey = `${regHive}\\${it.Key}`;
        if (isProtectedRegKey(fullKey)) throw new Error('Protected key');
        runPS(`& reg.exe export ${psLiteral(fullKey)} ${psLiteral(path.join(bkDir, `key_${i}.reg`))} /y`);
        runPS(`& reg.exe delete ${psLiteral(fullKey)} /f`);
        removed++;
      }
    } catch (e) {
      errors.push({ key: it.Key, error: e.message });
    }
  }
  record('registry-clean', `removed=${removed}`, `backup=${bkDir}`, errors.length ? 'partial' : 'ok');
  return { ok: errors.length === 0, removed, backupDir: bkDir, errors };
}

function exportKey(key, file) {
  try {
    const out = runPS(`& reg.exe export ${psLiteral(key)} ${psLiteral(file)} /y 2>&1`, { timeout: 300000 });
    return { ok: true, output: out.slice(0, 300) };
  } catch (e) {
    return { ok: false, error: e.message };
  }
}

module.exports = { scanInvalid, cleanInvalid, exportKey };