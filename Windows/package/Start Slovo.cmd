@echo off
setlocal
set "SLOVO_DATA_DIR=%~dp0Data"
if "%~1"=="" (
    start "" "%~dp0Slovo.exe"
    exit /b
)
"%~dp0Slovo.exe" %*
exit /b %errorlevel%
