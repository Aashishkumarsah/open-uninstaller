# Open Uninstaller — undo the user-level install. Source tree is left untouched.
$ErrorActionPreference = 'SilentlyContinue'

$installDir = Join-Path $env:LOCALAPPDATA 'OpenUninstaller'
$shortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Open Uninstaller.lnk'

# 1. Remove from user PATH.
$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath) {
  $entries = $userPath -split ';' | Where-Object { $_ -and $_ -ne $installDir }
  [Environment]::SetEnvironmentVariable('Path', ($entries -join ';'), 'User')
  Write-Host "Removed from user PATH."
}

# 2. Remove shortcut and install dir.
Remove-Item -LiteralPath $shortcut -Force
Remove-Item -LiteralPath $installDir -Recurse -Force
Write-Host "Removed Start Menu shortcut and $installDir"
Write-Host "`nUninstall complete. The source folder is untouched."
