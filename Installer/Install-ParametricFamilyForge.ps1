<#
.SYNOPSIS
    ParametricFamilyForge One-Click Installer for Autodesk Revit 2025 & 2026.
.DESCRIPTION
    Compiles (or stages), deploys, and configures the ParametricFamilyForge add-in
    into the current user's Revit Addins directory without requiring administrative rights.
#>

[CmdletBinding()]
param(
    [string]$TargetVersion = "Auto"
)

$Host.UI.RawUI.WindowTitle = "ParametricFamilyForge Installer"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "        PARAMETRIC FAMILY FORGE - ONE-CLICK INSTALLER           " -ForegroundColor White
Write-Host "             Autodesk Revit 2024 / 2025 / 2026 Add-in           " -ForegroundColor Gray
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host ""

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$ProjectDir = Split-Path -Parent $ScriptDir
$AppdataAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins"

# 1. Detect Installed Revit Versions
$SupportedYears = @("2026", "2025", "2024")
$DetectedYears = @()

foreach ($year in $SupportedYears) {
    $revitExePath = "C:\Program Files\Autodesk\Revit $year\Revit.exe"
    $addinYearDir = Join-Path $AppdataAddins $year
    if ((Test-Path $revitExePath) -or (Test-Path $addinYearDir)) {
        $DetectedYears += $year
    }
}

if ($DetectedYears.Count -eq 0) {
    Write-Host "[WARNING] No existing Revit 2025/2026 directories detected." -ForegroundColor Yellow
    Write-Host "Defaulting installation targets to: 2025 and 2026." -ForegroundColor Yellow
    $DetectedYears = @("2025", "2026")
} else {
    Write-Host "[OK] Detected active Revit versions: $($DetectedYears -join ', ')" -ForegroundColor Green
}

# 2. Build the Project if dotnet is available
$CsprojPath = Join-Path $ProjectDir "ParametricFamilyForge.csproj"
$BuiltSuccessfully = $false

if (Get-Command "dotnet" -ErrorAction SilentlyContinue) {
    Write-Host "`n[STEP 1/3] Compiling solution using .NET 8.0 SDK..." -ForegroundColor Cyan
    
    foreach ($year in $DetectedYears) {
        $revitDir = "C:\Program Files\Autodesk\Revit $year"
        if (Test-Path $revitDir) {
            Write-Host "Building for Revit $year via '$revitDir'..." -ForegroundColor Gray
            & dotnet build "$CsprojPath" -c Release -p:RevitVersion=$year -p:RevitInstallPath="$revitDir"
            if ($LASTEXITCODE -eq 0) {
                $BuiltSuccessfully = $true
                break
            }
        }
    }

    if (-not $BuiltSuccessfully) {
        Write-Host "Standard build attempt with default paths..." -ForegroundColor Gray
        & dotnet build "$CsprojPath" -c Release
        if ($LASTEXITCODE -eq 0) {
            $BuiltSuccessfully = $true
        }
    }
} else {
    Write-Host "[NOTE] 'dotnet' CLI not detected on PATH. Checking for pre-compiled binaries..." -ForegroundColor Yellow
}

# 3. Locate Binaries
$BinarySources = @(
    (Join-Path $ProjectDir "bin\Release\net8.0-windows\ParametricFamilyForge.dll"),
    (Join-Path $ProjectDir "bin\Debug\net8.0-windows\ParametricFamilyForge.dll"),
    (Join-Path $ProjectDir "ParametricFamilyForge.dll")
)

$SourceDll = $null
foreach ($path in $BinarySources) {
    if (Test-Path $path) {
        $SourceDll = $path
        break
    }
}

$SourceDir = if ($SourceDll) { Split-Path -Parent $SourceDll } else { $ProjectDir }

# 4. Deploy into each Revit Year Addins directory
Write-Host "`n[STEP 2/3] Deploying Add-in Assemblies and Manifest..." -ForegroundColor Cyan

foreach ($year in $DetectedYears) {
    $targetAddinDir = Join-Path $AppdataAddins $year
    if (-not (Test-Path $targetAddinDir)) {
        New-Item -ItemType Directory -Path $targetAddinDir -Force | Out-Null
    }

    $targetPackageDir = Join-Path $targetAddinDir "ParametricFamilyForge"
    if (-not (Test-Path $targetPackageDir)) {
        New-Item -ItemType Directory -Path $targetPackageDir -Force | Out-Null
    }

    # Determine framework target: net48 for Revit 2024, net8.0-windows for 2025+
    $tfm = if ($year -eq "2024") { "net48" } else { "net8.0-windows" }
    $relDirName = if ($year -eq "2024") { "Revit_2024" } else { "Revit_2025_2026" }

    $yearSourceDir = Join-Path $ProjectDir "Release_Binaries\$relDirName"
    if (-not (Test-Path $yearSourceDir)) {
        $yearSourceDir = Join-Path $ProjectDir "bin\Release\$tfm"
    }
    if (-not (Test-Path $yearSourceDir)) {
        $yearSourceDir = Join-Path $ProjectDir "bin\Debug\$tfm"
    }
    if (-not (Test-Path $yearSourceDir)) {
        $yearSourceDir = $SourceDir
    }

    # Copy DLLs
    if (Test-Path $yearSourceDir) {
        Get-ChildItem -Path $yearSourceDir -Filter "*.dll" -Recurse | ForEach-Object {
            Copy-Item -Path $_.FullName -Destination $targetPackageDir -Force
            Unblock-File -Path (Join-Path $targetPackageDir $_.Name) -ErrorAction SilentlyContinue
        }
    }

    # Copy Bundled Templates
    $tplSourceDir = Join-Path $ProjectDir "Templates"
    if (Test-Path $tplSourceDir) {
        $pkgTplDir = Join-Path $targetPackageDir "Templates"
        if (-not (Test-Path $pkgTplDir)) { New-Item -ItemType Directory -Path $pkgTplDir -Force | Out-Null }
        Copy-Item -Path "$tplSourceDir\*" -Destination $pkgTplDir -Force
    }

    # Write Manifest with configured Assembly path
    $manifestPath = Join-Path $targetAddinDir "ParametricFamilyForge.addin"
    $installedAssemblyPath = Join-Path $targetPackageDir "ParametricFamilyForge.dll"

    $xmlLines = New-Object System.Collections.Generic.List[string]
    $xmlLines.Add('<?xml version="1.0" encoding="utf-8" standalone="no"?>')
    $xmlLines.Add('<RevitAddIns>')
    $xmlLines.Add('  <AddIn Type="Application">')
    $xmlLines.Add('    <Name>ParametricFamilyForge</Name>')
    $xmlLines.Add('    <Assembly>' + $installedAssemblyPath + '</Assembly>')
    $xmlLines.Add('    <AddInId>8B145E9F-349C-4D2A-9447-0F89E88E4B17</AddInId>')
    $xmlLines.Add('    <FullClassName>ParametricFamilyForge.App</FullClassName>')
    $xmlLines.Add('    <VendorId>AECFORGE</VendorId>')
    $xmlLines.Add('    <VendorDescription>Parametric Family Forge - AI-Driven Revit Family Generator</VendorDescription>')
    $xmlLines.Add('  </AddIn>')
    $xmlLines.Add('</RevitAddIns>')

    [System.IO.File]::WriteAllLines($manifestPath, $xmlLines, [System.Text.Encoding]::UTF8)
    Unblock-File -Path $manifestPath -ErrorAction SilentlyContinue

    Write-Host "  -> Installed successfully for Revit $year" -ForegroundColor Green
    Write-Host "     Manifest: $manifestPath" -ForegroundColor DarkGray
    Write-Host "     Assembly: $installedAssemblyPath" -ForegroundColor DarkGray
}

# 5. Initialize Configuration File in %APPDATA%
Write-Host "`n[STEP 3/3] Setting up configuration profile..." -ForegroundColor Cyan

$configDir = Join-Path $env:APPDATA "Autodesk\Revit\Addins\ParametricFamilyForge"
if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
}

$globalTplDir = Join-Path $configDir "Templates"
if (-not (Test-Path $globalTplDir)) {
    New-Item -ItemType Directory -Path $globalTplDir -Force | Out-Null
}
$tplSourceDir = Join-Path $ProjectDir "Templates"
if (Test-Path $tplSourceDir) {
    Copy-Item -Path "$tplSourceDir\*" -Destination $globalTplDir -Force
    Write-Host "  -> Installed bundled templates to: $globalTplDir" -ForegroundColor Green
}

$configFile = Join-Path $configDir "config.json"
if (-not (Test-Path $configFile)) {
    $cfgObject = [ordered]@{
        api_key = ""
        model_engine = "gemini-3.8"
        temperature = 0.2
        template_directory = $globalTplDir
        output_directory = (Join-Path $env:USERPROFILE 'Desktop\RevitGeneratedFamilies')
        auto_load_into_project = $true
    }
    $defaultConfig = $cfgObject | ConvertTo-Json
    [System.IO.File]::WriteAllText($configFile, $defaultConfig, [System.Text.Encoding]::UTF8)
    Write-Host "  -> Created default config at: $configFile" -ForegroundColor Green
} else {
    Write-Host "  -> Preserved existing config at: $configFile" -ForegroundColor Green
}

Write-Host ""
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "              INSTALLATION SUCCESSFULLY COMPLETED!               " -ForegroundColor White
Write-Host "=================================================================" -ForegroundColor Green
Write-Host "Open Autodesk Revit, click the 'AI Family Forge' tab on the ribbon,"
Write-Host "and click 'Parametric Family Forge' to start creating families!"
Write-Host ""
