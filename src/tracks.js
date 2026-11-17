'use strict';
const { runPSJson, psLiteral } = require('./util');
const { record } = require('./history');

const TARGETS = {
  browsers: [
    { id: 'chrome', label: 'Chrome history / cache / cookies', paths: [
      '$env:LOCALAPPDATA\\Google\\Chrome\\User Data\\Default\\History',
      '$env:LOCALAPPDATA\\Google\\Chrome\\User Data\\Default\\Cookies',
      '$env:LOCALAPPDATA\\Google\\Chrome\\User Data\\Default\\Cache',
      '$env:LOCALAPPDATA\\Google\\Chrome\\User Data\\Default\\Network\\Cookies',
    ]},
    { id: 'edge', label: 'Edge history / cache / cookies', paths: [
      '$env:LOCALAPPDATA\\Microsoft\\Edge\\User Data\\Default\\History',
      '$env:LOCALAPPDATA\\Microsoft\\Edge\\User Data\\Default\\Cookies',
      '$env:LOCALAPPDATA\\Microsoft\\Edge\\User Data\\Default\\Cache',
      '$env:LOCALAPPDATA\\Microsoft\\Edge\\User Data\\Default\\Network\\Cookies',
    ]},
    { id: 'brave', label: 'Brave history / cache / cookies', paths: [
      '$env:LOCALAPPDATA\\BraveSoftware\\Brave-Browser\\User Data\\Default\\History',
      '$env:LOCALAPPDATA\\BraveSoftware\\Brave-Browser\\User Data\\Default\\Cache',
      '$env:LOCALAPPDATA\\BraveSoftware\\Brave-Browser\\User Data\\Default\\Network\\Cookies',
    ]},
    { id: 'firefox', label: 'Firefox places.sqlite / cache', paths: [
      '$env:APPDATA\\Mozilla\\Firefox\\Profiles\\*\\places.sqlite',
      '$env:LOCALAPPDATA\\Mozilla\\Firefox\\Profiles\\*\\cache2',
    ]},
  ],
  office: [
    { id: 'office-mru', label: 'Office recent documents (Word/Excel/PPT)', registry: [
      'HKCU:\\Software\\Microsoft\\Office\\*\\Word\\User MRU',
      'HKCU:\\Software\\Microsoft\\Office\\*\\Word\\File MRU',
      'HKCU:\\Software\\Microsoft\\Office\\*\\Excel\\User MRU',
      'HKCU:\\Software\\Microsoft\\Office\\*\\Excel\\File MRU',
      'HKCU:\\Software\\Microsoft\\Office\\*\\PowerPoint\\User MRU',
    ]},
  ],
  windows: [
    { id: 'run-mru', label: 'Start Menu Run dialog history (RunMRU)', registry: ['HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\RunMRU'] },
    { id: 'recent-docs', label: 'Recent documents (*.lnk)', paths: ['$env:APPDATA\\Microsoft\\Windows\\Recent\\*.lnk'] },
    { id: 'regedit-last', label: 'Regedit Last Opened Key', registry: ['HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Applets\\Regedit'] },
    { id: 'thumbs', label: 'Explorer Thumbnail Cache', paths: ['$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer\\thumbcache_*.db'] },
    { id: 'temp', label: 'User temp files', paths: ['$env:TEMP\\*'] },
    { id: 'clipboard', label: 'Windows Clipboard', special: 'clipboard' },
    { id: 'recycle-bin', label: 'Empty Recycle Bin', special: 'recycle-bin' },
  ],
};

function scan() {
  const flatTargets = [];
  for (const [cat, list] of Object.entries(TARGETS)) {
    for (const t of list) flatTargets.push({ category: cat, ...t });
  }

  const script = `
    $specs = ${psLiteral(JSON.stringify(flatTargets))} | ConvertFrom-Json
    foreach($t in $specs){
      $count = 0
      $bytes = [int64]0
      $paths = @()
      if($t.paths){
        foreach($p in $t.paths){
          $r = $ExecutionContext.InvokeCommand.ExpandString([string]$p)
          foreach($x in (Get-Item -Path $r)){
            $paths += [pscustomobject]@{ Path = $x.FullName; Bytes = if($x.PSIsContainer){ 0 } else { [int64]$x.Length } }
            $count++
            if(-not $x.PSIsContainer){ $bytes += [int64]$x.Length }
          }
        }
      }
      if($t.registry){
        foreach($k in $t.registry){
          foreach($x in (Get-Item -Path $k)){
            $paths += [pscustomobject]@{ Path = $x.Name; Bytes = 0 }
            $count++
          }
        }
      }
      [pscustomobject]@{
        id       = $t.id
        category = $t.category
        label    = $t.label
        items    = $count
        bytes    = $bytes
        sizeMB   = [math]::Round($bytes / 1MB, 2)
        targets  = $paths
      }
    }
  `;
  return runPSJson(script, { timeout: 60000 });
}

function clean(ids) {
  const idsLiteral = (Array.isArray(ids) ? ids : []).map(psLiteral).join(',');
  if (!idsLiteral) return [];
  const script = `
    $ids = @(${idsLiteral})
    foreach($id in $ids){
      $removed = 0; $failed = 0
      switch($id){
        'temp' {
          Get-ChildItem -LiteralPath $env:TEMP -File | Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-1) } | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
          }
        }
        'recent-docs' {
          Get-ChildItem -LiteralPath "$env:APPDATA\\Microsoft\\Windows\\Recent" -Filter *.lnk | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
          }
        }
        'thumbs' {
          Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\\Microsoft\\Windows\\Explorer" -Filter "thumbcache_*.db" | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
          }
        }
        'run-mru' {
          try { Remove-Item -LiteralPath 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\RunMRU' -Recurse -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
        }
        'regedit-last' {
          try { Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Applets\\Regedit' -Name 'LastKey' -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
        }
        'clipboard' {
          try { Set-Clipboard -Value $null; $removed++ } catch { $failed++ }
        }
        'recycle-bin' {
          try { Clear-RecycleBin -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
        }
        'chrome' {
          foreach($f in @('History','Cookies','Cache','Network\\Cookies')){
            $p = "$env:LOCALAPPDATA\\Google\\Chrome\\User Data\\Default\\$f"
            if(Test-Path -LiteralPath $p){ try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop; $removed++ } catch { $failed++ } }
          }
        }
        'edge' {
          foreach($f in @('History','Cookies','Cache','Network\\Cookies')){
            $p = "$env:LOCALAPPDATA\\Microsoft\\Edge\\User Data\\Default\\$f"
            if(Test-Path -LiteralPath $p){ try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop; $removed++ } catch { $failed++ } }
          }
        }
        'brave' {
          foreach($f in @('History','Cache','Network\\Cookies')){
            $p = "$env:LOCALAPPDATA\\BraveSoftware\\Brave-Browser\\User Data\\Default\\$f"
            if(Test-Path -LiteralPath $p){ try { Remove-Item -LiteralPath $p -Recurse -Force -ErrorAction Stop; $removed++ } catch { $failed++ } }
          }
        }
        'firefox' {
          Get-ChildItem -Path "$env:APPDATA\\Mozilla\\Firefox\\Profiles" -Directory | ForEach-Object {
            $pl = "$($_.FullName)\\places.sqlite"
            if(Test-Path -LiteralPath $pl){ try { Remove-Item -LiteralPath $pl -Force -ErrorAction Stop; $removed++ } catch { $failed++ } }
          }
        }
        'office-mru' {
          Get-ChildItem -Path 'HKCU:\\Software\\Microsoft\\Office' -Recurse | Where-Object { $_.PSChildName -match '(Recent Files|File MRU|User MRU)' } | ForEach-Object {
            try { Remove-Item -LiteralPath $_.PSPath -Recurse -Force -ErrorAction Stop; $removed++ } catch { $failed++ }
          }
        }
      }
      [pscustomobject]@{ Id=$id; Removed=$removed; Failed=$failed }
    }
  `;
  const results = runPSJson(script, { timeout: 300000 });
  for (const r of results) record('tracks-clean', r.Id, `removed=${r.Removed} failed=${r.Failed}`);
  return results;
}

module.exports = { scan, clean, TARGETS };