# ParametricFamilyForge

**ParametricFamilyForge** is a production-ready Autodesk Revit (2025 & 2026) add-in powered by Google Gemini Vision API and an AEC procedural rule engine. It translates natural language prompts and cut-sheet sketch images into complete, parametric Revit Families (`.rfa`) with nested sub-components and linked parametric constraints.

---

## Key Features

- **Generative AI Vision & Prompting**: Supports `gemini-2.5-flash`, `gemini-2.0-flash`, `gemini-1.5-pro`, and `gemini-1.5-flash` with Structured JSON Outputs.
- **Offline Deterministic Fallback**: Includes a built-in architectural rule engine for procedural generation of workstations, doors, windows, and casework without an internet connection or API key.
- **Modern Revit API Architecture**:
  - Full compatibility with Revit 2025 and 2026 (`net8.0-windows`).
  - Utilizes modern `ForgeTypeId`, `SpecTypeId`, `GroupTypeId`, and `UnitTypeId.Millimeters` (no deprecated enums).
- **Background Multi-Document Pipeline**:
  - Dynamically synthesizes sub-components in memory using `NewFamilyDocument()`.
  - Strictly follows the Revit API **`Regenerate()` Rule** after reference plane creation to prevent alignment failure exceptions.
  - Automatically loads nested `.rfa`s into the Master Family and links parameters via `famMgr.AssociateElementParameterToFamilyParameter()`.
  - Automatically loads the resulting Master Family directly into your open project using custom `IFamilyLoadOptions`.
- **Modeless WPF Interface**:
  - Safe Revit API thread marshaling via `IExternalEventHandler` and `ExternalEvent`.
  - Live JSON schema editor and real-time execution log console.
  - Persistent configuration settings stored in `%APPDATA%\Autodesk\Revit\Addins\ParametricFamilyForge\config.json`.

---

## One-Click Installation

To install on your company workstation without requiring administrative rights or Visual Studio:

1. Clone or download this repository.
2. Double-click **`Install.bat`** (or right-click `Installer\Install-ParametricFamilyForge.ps1` and select **Run with PowerShell**).
3. The installer will:
   - Detect installed Revit 2025/2026 instances.
   - Stage assemblies into `%APPDATA%\Autodesk\Revit\Addins\<Year>\ParametricFamilyForge\`.
   - Configure the `.addin` manifest.
   - Initialize default settings in `%APPDATA%`.
4. Open Autodesk Revit, navigate to the **"AI Family Forge"** ribbon tab, and click **"Parametric Family Forge"**.

To uninstall at any time, run **`Uninstall.bat`**.

---

## Repository Structure

```
ParametricFamilyForge/
│
├── App.cs                                  # IExternalApplication: Ribbon Tab & PushButton setup
├── ParametricFamilyForge.addin             # Revit add-in manifest
├── ParametricFamilyForge.csproj            # .NET 8.0 WPF project file
├── Install.bat                             # One-click Windows batch installer
├── Uninstall.bat                           # Clean uninstaller script
│
├── Commands/
│   └── LaunchForgeCommand.cs               # IExternalCommand: Launches modeless WPF window
│
├── Models/
│   ├── FamilySchema.cs                     # Strongly typed JSON bridge
│   └── ForgeSettings.cs                    # Persistent configuration settings
│
├── Services/
│   ├── GeminiVisionService.cs              # Gemini Vision API client + deterministic fallback
│   └── SettingsService.cs                  # %APPDATA% JSON config persistence
│
├── RevitEngine/
│   ├── FamilyGenerationHandler.cs          # IExternalEventHandler thread bridge
│   ├── NestedFamilyBuilder.cs              # Sub-component family compiler
│   └── MasterFamilyBuilder.cs              # Master host compiler, geometry, alignment & loading
│
├── UI/
│   ├── ForgeWindow.xaml                    # WPF GUI
│   └── ForgeWindow.xaml.cs                 # WPF code-behind
│
└── Installer/
    ├── Install-ParametricFamilyForge.ps1   # PowerShell installation engine
    └── Uninstall-ParametricFamilyForge.ps1 # PowerShell removal engine
```

---

## Configuration

Click **"⚙ AI Engine Settings"** in the top bar of the add-in to customize:
- **Gemini API Key**: Your Google Gemini API Key.
- **AI Model Engine**: Switch between `gemini-2.5-flash`, `gemini-2.0-flash`, `gemini-1.5-pro`, etc.
- **Temperature**: Precision control (default `0.20` for dimensional accuracy).
- **Template Directory**: Path to your Revit Family Templates (`.rft`), defaults to standard English templates.
- **Output Directory**: Target directory where compiled `.rfa` files will be saved.
