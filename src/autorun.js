'use strict';
const fs = require('fs');
const { runPSJson, runPS, psLiteral } = require('./util');
const { record } = require('./history');

const RUN_KEYS = [
  'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run',
  'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce',
  'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run',
  'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce',
  'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Run',
];

function listAutoruns() {
  const keys = RUN_KEYS.map(psLiteral).join(',');
  const script = `
    $keys = @(${keys})
    $running = @{}
    Get-Process | ForEach-Object {
      try { if($_.Path){ $running[$_.Path.ToLowerInvariant()] = [int]$_.Id } } catch {}
    }
    $disabled = @{}
    foreach($ap in @(
      'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run',
      'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run',
      'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run'
    )){
      if(Test-Path -LiteralPath $ap){
        $pr = Get-ItemProperty -LiteralPath $ap
        foreach($p in $pr.PSObject.Properties){
          if($p.Name -like 'PS*'){ continue }
          if($p.Value -is [byte[]] -and $p.Value.Length -ge 1 -and ($p.Value[0] -eq 3 -or $p.Value[0] -eq 1)){
            $disabled[$p.Name.ToLowerInvariant()] = $true
          }
        }
      }
    }
    foreach($path in $keys){
      if(-not (Test-Path -LiteralPath $path)){ continue }
      $item = Get-ItemProperty -LiteralPath $path
      if(-not $item){ continue }
      $item.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' -and $_.Value } | ForEach-Object {
        $cmd = [string]$_.Value
        $exe = $cmd.Trim()
        if($exe.StartsWith('"')){
          $end = $exe.IndexOf('"', 1)
          if($end -gt 1){ $exe = $exe.Substring(1, $end - 1) }
        } elseif($exe.ToLowerInvariant().Contains('.exe')) {
          $idx = $exe.ToLowerInvariant().IndexOf('.exe')
          $exe = $exe.Substring(0, $idx + 4)
        }
        $exe = [Environment]::ExpandEnvironmentVariables($exe.Trim())
        $exists = ($exe -and (Test-Path -LiteralPath $exe))
        $pidVal = if($exists -and $running.ContainsKey($exe.ToLowerInvariant())) { $running[$exe.ToLowerInvariant()] } else { $null }
        $isEnabled = -not $disabled.ContainsKey($_.Name.ToLowerInvariant())
        $state = if(-not $exists){ 'INVALID' } elseif($pidVal){ 'RUNNING' } elseif(-not $isEnabled){ 'DISABLED' } else { 'NOT RUNNING' }
        [pscustomobject]@{
          Name     = $_.Name
          Status   = $state
          Enabled  = $isEnabled
          Kind     = analyzeKind($cmd)
          HivePath = $path
          Command  = $cmd
          PID      = $pidVal
        }
      }
    }
    foreach($sf in @(
      @{ Dir = [Environment]::GetFolderPath('Startup'); Label = 'Startup (Current User)' },
      @{ Dir = [Environment]::GetFolderPath('CommonStartup'); Label = 'Startup (All Users)' }
    )){
      if($sf.Dir -and (Test-Path -LiteralPath $sf.Dir)){
        Get-ChildItem -LiteralPath $sf.Dir -File | Where-Object { $_.Name -ine 'desktop.ini' } | ForEach-Object {
          [pscustomobject]@{
            Name     = $_.BaseName
            Status   = 'NOT RUNNING'
            Enabled  = $true
            Kind     = 'startup-folder'
            HivePath = $sf.Label
            Command  = $_.FullName
            PID      = $null
          }
        }
      }
    }
  `;
  const fullScript = `
    function analyzeKind($c){
      if($c -match 'rundll32|regsvr32|powershell|cmd\\.exe|mshta|wscript|cscript'){ return 'script' }
      if($c -match '\\.exe'){ return 'executable' }
      return 'other'
    }
    ${script}
  `;
  return runPSJson(fullScript);
}

function setAutorunEnabled({ name, hivePath, enabled }) {
  const isLm = String(hivePath || '').toUpperCase().startsWith('HKLM');
  const isWow = String(hivePath || '').toUpperCase().includes('WOW6432');
  const approvedPath = isLm
    ? (isWow
      ? 'HKLM:\\SOFTWARE\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run'
      : 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run')
    : 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run';
  const flag = enabled ? 2 : 3;
  runPS(`
    if(-not (Test-Path -LiteralPath ${psLiteral(approvedPath)})){ New-Item -Path ${psLiteral(approvedPath)} -Force | Out-Null }
    $bytes = [byte[]]@(${flag},0,0,0,0,0,0,0,0,0,0,0)
    Set-ItemProperty -Path ${psLiteral(approvedPath)} -Name ${psLiteral(name)} -Value $bytes -Type Binary
  `);
  record('autorun-toggle', name, enabled ? 'enabled' : 'disabled');
  return { ok: true, name, enabled };
}

function addAutorun({ name, command, scope = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' }) {
  if (!name || !command) return { ok: false, error: 'Name and command required' };
  const s = String(scope || '').trim();
  let psPath = s;
  if (s === 'HKCU') psPath = 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run';
  else if (s === 'HKCU_RunOnce') psPath = 'HKCU:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\RunOnce';
  else if (s === 'HKLM') psPath = 'HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run';
  else psPath = s.replace(/^HKCU\\/i, 'HKCU:\\').replace(/^HKLM\\/i, 'HKLM:\\');

  runPS(`Set-ItemProperty -Path ${psLiteral(psPath)} -Name ${psLiteral(name)} -Value ${psLiteral(command)} -Type String`);
  record('autorun-add', name, command);
  return { ok: true, name, command, hivePath: psPath };
}

function removeAutorun({ name, hivePath, command }) {
  if (String(hivePath || '').startsWith('Startup')) {
    if (command && fs.existsSync(command)) fs.unlinkSync(command);
    record('autorun-remove', name, command);
    return { ok: true, name };
  }
  runPS(`Remove-ItemProperty -Path ${psLiteral(hivePath)} -Name ${psLiteral(name)} -Force -ErrorAction Stop`);
  record('autorun-remove', name, hivePath);
  return { ok: true, name };
}

function removeInvalidAutoruns() {
  const invalid = listAutoruns().filter(r => r.Status === 'INVALID');
  let removed = 0;
  const errors = [];
  for (const r of invalid) {
    try {
      removeAutorun({ name: r.Name, hivePath: r.HivePath, command: r.Command });
      removed++;
    } catch (e) {
      errors.push({ name: r.Name, error: e.message });
    }
  }
  record('autorun-clean-invalid', `${removed} entries`, `errors=${errors.length}`, errors.length ? 'partial' : 'ok');
  return { ok: errors.length === 0, found: invalid.length, removed, errors };
}

module.exports = { listAutoruns, setAutorunEnabled, addAutorun, removeAutorun, removeInvalidAutoruns, RUN_KEYS };