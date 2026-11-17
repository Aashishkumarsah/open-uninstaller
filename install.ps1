# Open Uninstaller — user-level install (no administrator required).
# Reversible: run uninstall.ps1 to undo everything.
$ErrorActionPreference = 'Stop'

$sourceDir = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'OpenUninstaller'
$wrapper = Join-Path $installDir 'ou.cmd'
$shortcutName = 'Open Uninstaller.lnk'
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$shortcut = Join-Path $startMenu $shortcutName

Write-Host "Installing Open Uninstaller from: $sourceDir"
Write-Host "Install location: $installDir"

# 1. Create the install directory and a launcher that points at the source tree.
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$nativeExe = Join-Path $sourceDir 'OpenUninstaller.exe'
if (Test-Path -LiteralPath $nativeExe) {
  # Native Windows desktop app - no browser involved.
  @"
@echo off
start "" "$nativeExe" %*
"@ | Set-Content -LiteralPath $wrapper -Encoding ASCII
} else {
  @"
@echo off
setlocal
node "$($sourceDir)\bin\ou.js" %*
endlocal
"@ | Set-Content -LiteralPath $wrapper -Encoding ASCII
}

# 2. Add the install dir to the current user's PATH (persistent, HKCU only).
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (-not $userPath) { $userPath = '' }
$entries = $userPath -split ';' | Where-Object { $_ -ne '' }
if ($entries -notcontains $installDir) {
  $entries += $installDir
  [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
  Write-Host "Added to user PATH: $installDir"
} else {
  Write-Host "Already on user PATH."
}

# 3. Start Menu shortcut.
$WshShell = New-Object -ComObject WScript.Shell
$sc = $WshShell.CreateShortcut($shortcut)
$sc.TargetPath = $wrapper
$sc.WorkingDirectory = $sourceDir
$sc.Description = 'Open Uninstaller - native Windows desktop app'
$sc.Save()
Write-Host "Start Menu shortcut: $shortcut"

# 4. Make `ou` available in this shell too.
$env:Path += ";$installDir"

Write-Host "`nInstall complete. Open a NEW terminal and run:  ou help"
