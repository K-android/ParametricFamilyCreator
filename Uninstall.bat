@echo off
setlocal
title Parametric Family Forge - Add-in Uninstaller
echo =====================================================================
echo         PARAMETRIC FAMILY FORGE - ADD-IN UNINSTALLER
echo =====================================================================
echo.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\Uninstall-ParametricFamilyForge.ps1"
echo.
pause
