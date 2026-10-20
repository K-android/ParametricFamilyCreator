<#
.SYNOPSIS
    ParametricFamilyForge Add-in Uninstaller.
#>

$Host.UI.RawUI.WindowTitle = "ParametricFamilyForge Uninstaller"

Write-Host "=================================================================" -ForegroundColor Yellow
Write-Host "              PARAMETRIC FAMILY FORGE UNINSTALLER                " -ForegroundColor White
Write-Host "=================================================================" -ForegroundColor Yellow
Write-Host ""

$AppdataAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins"
$Years = @("2024", "2025", "2026")

foreach ($year in $Years) {
    $manifestPath = Join-Path $AppdataAddins "$year\ParametricFamilyForge.addin"
    $packageDir = Join-Path $AppdataAddins "$year\ParametricFamilyForge"

    if (Test-Path $manifestPath) {
        Remove-Item -Path $manifestPath -Force
        Write-Host "[REMOVED] $manifestPath" -ForegroundColor Red
    }
    if (Test-Path $packageDir) {
        Remove-Item -Path $packageDir -Recurse -Force
        Write-Host "[REMOVED] $packageDir" -ForegroundColor Red
    }
}

Write-Host "`nAdd-in successfully uninstalled from Revit." -ForegroundColor Green
