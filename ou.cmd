@echo off
setlocal
set "OU_DIR=%~dp0"
if "%OU_DIR:~-1%"=="\" set "OU_DIR=%OU_DIR:~0,-1%"
node "%OU_DIR%\bin\ou.js" %*
endlocal
