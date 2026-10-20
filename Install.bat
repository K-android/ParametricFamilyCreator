@echo off
setlocal
title Parametric Family Forge - One-Click Installer
echo =====================================================================
echo           PARAMETRIC FAMILY FORGE - ONE-CLICK INSTALLER
echo                 Autodesk Revit 2025 / 2026 Add-in
echo =====================================================================
echo.
echo Launching installer engine...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\Install-ParametricFamilyForge.ps1"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Installation exited with error code %ERRORLEVEL%.
) else (
    echo.
    echo [SUCCESS] Parametric Family Forge is ready to use in Revit!
)

echo.
pause
