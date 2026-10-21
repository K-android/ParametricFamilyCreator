@echo off
setlocal
title Parametric Family Forge - Native Uninstaller
echo =====================================================================
echo         PARAMETRIC FAMILY FORGE - ADD-IN UNINSTALLER
echo =====================================================================
echo.

set "ADDINS_BASE=%APPDATA%\Autodesk\Revit\Addins"
set "YEARS=2024 2025 2026"

for %%Y in (%YEARS%) do (
    set "MANIFEST=%ADDINS_BASE%\%%Y\ParametricFamilyForge.addin"
    set "PACKAGE=%ADDINS_BASE%\%%Y\ParametricFamilyForge"

    if exist "%ADDINS_BASE%\%%Y\ParametricFamilyForge.addin" (
        del /f /q "%ADDINS_BASE%\%%Y\ParametricFamilyForge.addin" >nul 2>&1
        echo [REMOVED] %ADDINS_BASE%\%%Y\ParametricFamilyForge.addin
    )
    if exist "%ADDINS_BASE%\%%Y\ParametricFamilyForge" (
        rmdir /s /q "%ADDINS_BASE%\%%Y\ParametricFamilyForge" >nul 2>&1
        echo [REMOVED] %ADDINS_BASE%\%%Y\ParametricFamilyForge
    )
)

echo.
echo [SUCCESS] Add-in successfully uninstalled from Revit 2024, 2025, and 2026.
echo.
pause
