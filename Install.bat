@echo off
setlocal
title Parametric Family Forge Installer
echo =====================================================================
echo           PARAMETRIC FAMILY FORGE - ONE-CLICK INSTALLER
echo                Autodesk Revit 2024 / 2025 / 2026
echo =====================================================================
echo.

:: Automatically unblock all downloaded files in this folder to bypass Mark-of-the-Web
powershell.exe -NoProfile -Command "Get-ChildItem -Path '%~dp0' -Recurse | Unblock-File -ErrorAction SilentlyContinue" >nul 2>&1

:: If standalone Setup.exe is present, launch it directly (No PowerShell execution policy required!)
if exist "%~dp0Setup.exe" (
    echo Launching native Windows Setup Wizard...
    start "" "%~dp0Setup.exe"
    exit /b 0
)

:: Fallback to running PowerShell script with execution policy bypass and unblock
echo Launching installer script...
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Installer\Install-ParametricFamilyForge.ps1"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Installation exited with error code %ERRORLEVEL%.
    pause
)
