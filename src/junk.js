'use strict';
const { runPSJson, psLiteral, isProtectedPath } = require('./util');
const { record } = require('./history');

const JUNK_PATTERNS = [
  '$env:TEMP\\*',
  '$env:WINDIR\\Temp\\*',
  'C:\\Windows\\Prefetch\\*',
  '$env:LOCALAPPDATA\\Microsoft\\Windows\\INetCache\\*',
  '$env:LOCALAPPDATA\\CrashDumps\\*',
  '$env:LOCALAPPDATA\\Microsoft\\Windows\\WER\\ReportArchive\\*',
];

const CUSTOM_JUNK_EXTS = ['.tmp', '.temp', '.log', '.bak', '.old', '.dmp', '.chk', '.gid', '.~tmp'];

function scanJunk() {
  const paths = JUNK_PATTERNS.map(psLiteral).join(',');
  const script = `
    $paths = @(${paths})
    foreach($p in $paths){
      $resolved = $ExecutionContext.InvokeCommand.ExpandString($p)
      $resolved = $resolved -replace '\\\\\\*',''
      if(-not (Test-Path -LiteralPath $resolved)){ continue }
      $items = Get-ChildItem -LiteralPath $resolved -File -Recurse -ErrorAction SilentlyContinue
      $filtered = $items | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) }
      $sum = ($filtered | Measure-Object -Property Length -Sum).Sum
      [pscustomobject]@{
        Path   = $resolved
        Files  = @($filtered).Count
        Bytes  = [int64]$sum
        SizeMB = [math]::Round(([double]$sum) / 1MB, 2)
      }
    }
  `;
  return runPSJson(script, { timeout: 120000 });
}

function scanJunkDetailed(opts = {}) {
  const customDir = String(opts.customDir || '').trim();
  const includeRecent = !!opts.includeRecent;
  const limit = Math.max(1, Math.min(Number(opts.limit || 400), 1500));

  if (customDir && isProtectedPath(customDir)) {
    throw new Error(`Refusing to scan protected system directory: ${customDir}`);
  }

  const rootExprs = customDir && opts.customOnly ? [] : JUNK_PATTERNS.map(psLiteral);
  const customLit = customDir ? psLiteral(customDir) : "''";
  const extArray = CUSTOM_JUNK_EXTS.map(psLiteral).join(',');
  const ageMinutes = includeRecent ? 0 : 1440;

  const script = `
    $cutoff = (Get-Date).AddMinutes(-${ageMinutes})
    $summary = @()
    $allFiles = @()
    $junkExts = @(${extArray})
    $defaultRoots = @(${rootExprs.join(',')})
    $customDir = ${customLit}

    foreach ($p in $defaultRoots) {
      $resolved = $ExecutionContext.InvokeCommand.ExpandString($p)
      $resolved = $resolved -replace '\\\\\\*',''
      if (-not (Test-Path -LiteralPath $resolved)) { continue }
      $items = Get-ChildItem -LiteralPath $resolved -File -Recurse -ErrorAction SilentlyContinue
      if (${ageMinutes} -gt 0) {
        $items = $items | Where-Object { $_.LastWriteTime -lt $cutoff }
      }
      $arr = @($items)
      $sum = ($arr | Measure-Object -Property Length -Sum).Sum
      $summary += [pscustomobject]@{
        Path = $resolved
        Files = $arr.Count
        Bytes = [int64]$sum
        SizeMB = [math]::Round(([double]$sum) / 1MB, 2)
      }
      $allFiles += $arr
    }

    if ($customDir -and (Test-Path -LiteralPath $customDir)) {
      $items = Get-ChildItem -LiteralPath $customDir -File -Recurse -ErrorAction SilentlyContinue | Where-Object {
        ($junkExts -contains $_.Extension.ToLowerInvariant()) -or ($_.Name -like '~*')
      }
      if (${ageMinutes} -gt 0) {
        $items = $items | Where-Object { $_.LastWriteTime -lt $cutoff }
      }
      $arr = @($items)
      $sum = ($arr | Measure-Object -Property Length -Sum).Sum
      $summary += [pscustomobject]@{
        Path = $customDir
        Files = $arr.Count
        Bytes = [int64]$sum
        SizeMB = [math]::Round(([double]$sum) / 1MB, 2)
      }
      $allFiles += $arr
    }

    $files = @($allFiles | Sort-Object Length -Descending | Select-Object -First ${limit} | ForEach-Object {
      [pscustomobject]@{
        Name = $_.Name
        SizeKB = [math]::Round(([double]$_.Length) / 1KB, 2)
        Bytes = [int64]$_.Length
        LastModified = $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm')
        Directory = $_.DirectoryName
        Path = $_.FullName
      }
    })

    [pscustomobject]@{
      summary = @($summary)
      files = @($files)
    }
  `;
  const res = runPSJson(script, { timeout: 120000 });
  const row = res[0] || { summary: [], files: [] };
  return {
    summary: Array.isArray(row.summary) ? row.summary : (row.summary ? [row.summary] : []),
    files: Array.isArray(row.files) ? row.files : (row.files ? [row.files] : []),
    includeRecent,
    customDir: customDir || null,
  };
}

function cleanJunkFiles(filePaths = []) {
  const safe = (Array.isArray(filePaths) ? filePaths : [])
    .map(p => String(p || '').trim())
    .filter(p => p && !isProtectedPath(p));
  if (!safe.length) return { Removed: 0, FreedBytes: 0, Failed: 0, Items: [] };

  const list = safe.map(psLiteral).join(',');
  const script = `
    $removed = 0; $freed = [int64]0; $failed = 0
    foreach ($f in @(${list})) {
      if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { $failed++; continue }
      try {
        $len = [int64](Get-Item -LiteralPath $f -ErrorAction Stop).Length
        Remove-Item -LiteralPath $f -Force -ErrorAction Stop
        $removed++
        $freed += $len
      } catch {
        $failed++
      }
    }
    [pscustomobject]@{
      Path = 'Selected Junk Files'
      Removed = $removed
      FreedBytes = $freed
      Failed = $failed
    }
  `;
  const results = runPSJson(script, { timeout: 180000 });
  const r = results[0] || { Path: 'Selected Junk Files', Removed: 0, FreedBytes: 0, Failed: 0 };
  record('junk-clean-files', `${safe.length} selected`, `removed=${r.Removed} freed=${r.FreedBytes} failed=${r.Failed}`);
  return results;
}

function cleanJunk(input = []) {
  if (input && !Array.isArray(input) && Array.isArray(input.files) && input.files.length) {
    return cleanJunkFiles(input.files);
  }
  const targetPaths = Array.isArray(input) ? input : (input && Array.isArray(input.paths) ? input.paths : []);
  const safePaths = targetPaths.filter(p => p && !isProtectedPath(p));
  if (!safePaths.length) return [];
  const list = safePaths.map(psLiteral).join(',');
  const script = `
    foreach($dir in @(${list})){
      $removed = 0; $freed = [int64]0; $failed = 0
      if(Test-Path -LiteralPath $dir){
        Get-ChildItem -LiteralPath $dir -File -Recurse -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -lt (Get-Date).AddDays(-1) } | ForEach-Object {
          $len = [int64]$_.Length
          try {
            Remove-Item -LiteralPath $_.FullName -Force -ErrorAction Stop
            $removed++
            $freed += $len
          } catch { $failed++ }
        }
      }
      [pscustomobject]@{ Path=$dir; Removed=$removed; FreedBytes=$freed; Failed=$failed }
    }
  `;
  const results = runPSJson(script, { timeout: 180000 });
  for (const r of results) record('junk-clean', r.Path, `removed=${r.Removed} freed=${r.FreedBytes}`);
  return results;
}

module.exports = { scanJunk, scanJunkDetailed, cleanJunk, cleanJunkFiles, JUNK_PATTERNS, CUSTOM_JUNK_EXTS };
