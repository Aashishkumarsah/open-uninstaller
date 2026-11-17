@echo off
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set "DIR=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "SRC=%~dp0native\OpenUninstaller.cs"
set "OUT=%~dp0OpenUninstaller.exe"
echo Building native Windows app (WinForms)...
"%CSC%" /nologo /target:winexe /out:"%OUT%" ^
 /r:"%DIR%\System.Windows.Forms.dll" ^
 /r:"%DIR%\System.Drawing.dll" ^
 /r:"%DIR%\System.dll" ^
 /r:"%DIR%\System.Core.dll" ^
 /r:"%DIR%\System.Data.dll" ^
 /r:"%DIR%\System.Web.Extensions.dll" ^
 "%SRC%"
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
echo Built: %OUT%
endlocal
