'use strict';
const { runPSJson, runPS, psLiteral } = require('./util');
const { record } = require('./history');

// Store/UWP apps: combine Get-AppxPackage with direct AppModel Repository registry
// enumeration so apps still appear when the AppX service is slow or unavailable.
function listWinApps() {
  const script = `
    $seen = @{}
    $rows = @()
    try {
      Get-AppxPackage | Where-Object { -not $_.IsFramework } | ForEach-Object {
        $seen[$_.PackageFullName] = $true
        $rows += [pscustomobject]@{
          Name              = [string]$_.Name
          DisplayName       = [string]$_.Name
          Version           = [string]$_.Version
          Publisher         = [string]$_.Publisher
          PackageFamilyName = [string]$_.PackageFamilyName
          PackageFullName   = [string]$_.PackageFullName
          InstallLocation   = [string]$_.InstallLocation
          NonRemovable      = [bool]$_.NonRemovable
        }
      }
    } catch {}

    $repo = 'HKCU:\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages'
    if (Test-Path -LiteralPath $repo) {
      Get-ChildItem -LiteralPath $repo | ForEach-Object {
        $full = $_.PSChildName
        if ($seen.ContainsKey($full)) { return }
        $item = Get-ItemProperty -LiteralPath $_.PSPath
        $root = [string]$item.PackageRootFolder
        if ($root -like '*\\SystemApps\\*') { return }
        $dn = [string]$item.DisplayName
        $parts = $full -split '_'
        $shortName = $parts[0]
        $ver = if ($parts.Length -ge 2) { $parts[1] } else { '' }
        $fam = if ($parts.Length -ge 5) { $parts[0] + '_' + $parts[$parts.Length - 1] } else { $shortName }
        if ($dn -and -not $dn.StartsWith('@{') -and -not $dn.StartsWith('ms-resource:')) {
          $label = $dn
        } else {
          $label = $shortName
        }
        $rows += [pscustomobject]@{
          Name              = $shortName
          DisplayName       = $label
          Version           = $ver
          Publisher         = ''
          PackageFamilyName = $fam
          PackageFullName   = $full
          InstallLocation   = $root
          NonRemovable      = $false
        }
      }
    }
    $rows | Sort-Object DisplayName
  `;
  return runPSJson(script, { timeout: 60000 });
}

function removeWinApp(packageFullName) {
  if (!packageFullName) return { ok: false, error: 'PackageFullName required' };
  try {
    runPS(`Remove-AppxPackage -Package ${psLiteral(packageFullName)} -ErrorAction Stop`);
    record('winapp-remove', packageFullName, 'removed');
    return { ok: true, packageFullName };
  } catch (e) {
    record('winapp-remove', packageFullName, e.message, 'error');
    return { ok: false, error: e.message };
  }
}

function resetWinApp(packageFullName, packageFamilyName = '') {
  if (!packageFullName && !packageFamilyName) {
    return { ok: false, error: 'PackageFullName or PackageFamilyName required' };
  }
  const fam = packageFamilyName || (String(packageFullName || '').split('_').length >= 5
    ? `${String(packageFullName).split('_')[0]}_${String(packageFullName).split('_').pop()}`
    : '');
  const script = `
    $resetCmd = Get-Command Reset-AppxPackage -ErrorAction SilentlyContinue
    $usedCmdlet = $false
    if ($resetCmd -and ${psLiteral(packageFullName || '')}) {
      try {
        Reset-AppxPackage -Package ${psLiteral(packageFullName)} -ErrorAction Stop
        $usedCmdlet = $true
      } catch {}
    }
    $cleared = 0
    $famName = ${psLiteral(fam)}
    if ($famName) {
      $pkgRoot = Join-Path $env:LOCALAPPDATA ("Packages\\" + $famName)
      foreach ($sub in @('TempState', 'LocalCache', 'AC\\INetCache')) {
        $dir = Join-Path $pkgRoot $sub
        if (Test-Path -LiteralPath $dir) {
          Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue | ForEach-Object {
            try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop; $cleared++ } catch {}
          }
        }
      }
    }
    [pscustomobject]@{ usedCmdlet = $usedCmdlet; clearedItems = $cleared }
  `;
  try {
    const res = runPSJson(script, { timeout: 60000 })[0] || { usedCmdlet: false, clearedItems: 0 };
    record('winapp-reset', packageFullName || fam, `cmdlet=${res.usedCmdlet} cleared=${res.clearedItems}`);
    return { ok: true, packageFullName, packageFamilyName: fam, usedCmdlet: !!res.usedCmdlet, clearedItems: Number(res.clearedItems || 0) };
  } catch (e) {
    record('winapp-reset', packageFullName || fam, e.message, 'error');
    return { ok: false, error: e.message };
  }
}

module.exports = { listWinApps, removeWinApp, resetWinApp };
