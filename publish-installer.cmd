@echo off
setlocal EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%publish-installer.ps1"

echo.
echo [publish-installer] Launching publish-installer.ps1
echo     %PS_SCRIPT%
echo.

where pwsh >nul 2>nul
if %ERRORLEVEL%==0 (
    pwsh -NoLogo -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
    set "EXIT_CODE=!ERRORLEVEL!"
) else (
    powershell -NoLogo -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
    set "EXIT_CODE=!ERRORLEVEL!"
)

echo.
if not "%EXIT_CODE%"=="0" (
    echo Publish installer failed with exit code %EXIT_CODE%.
    pause
    exit /b %EXIT_CODE%
)

echo Publish installer finished successfully.
pause
exit /b 0
