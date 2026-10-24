# ParametricFamilyForge

**ParametricFamilyForge** is a production-ready Autodesk Revit (2024, 2025 & 2026) add-in powered by Google Gemini Vision API and an AEC procedural rule engine. It translates natural language prompts, cut-sheet sketch images, and built-in architectural presets into complete, parametric Revit Families (`.rfa`) with nested sub-components, multi-type catalogs, material associations, and linked parametric constraints.

---

## Key Features

- **Generative AI Vision & Prompting**: Supports `gemini-3.8-flash`, `gemini-4-argon`, `gemini-3.1-flash-lite`, and multimodal visual reasoning with Structured JSON Outputs.
- **Architectural Preset Library**: Quick one-click generation of production-ready components:
  - 🪑 **Ergonomic Office Workstation** (Desktop slab, modesty panel, 4 nested steel tubular legs, 4 footprint sizes)
  - 🚪 **Commercial Flush Door** (Jamb frame, leaf slab, vision lite glazing, nested lever hardware, 3 ADA/standard sizes)
  - 🗄️ **Casework Base Cabinet** (Carcass box, toe kick, solid surface countertop, nested hardware pull)
  - 🏛️ **Architectural Column** (Stepped plinth base, cylindrical shaft, abacus capital, 3 height/diameter variants)
  - 👥 **Executive Conference Table** (Beveled top slab, dual pedestals, wire raceway, 3 executive size types)
- **Multi-Type Catalog Generation (`family_types`)**:
  - Automatically synthesizes all defined family types (`FamilyType`) in the Revit Family Manager with dimensional overrides.
  - Export standard Revit Type Catalog schedules (`.txt` / `.csv`) for automatic type selection prompts in project environments.
- **Expanded Revit API Spec & Group Engine**:
  - Full support for `Length`, `Angle`, `Area`, `Volume`, `Integer`, `Number`, `Boolean.YesNo`, `String.Text`, `String.Url`, and `Reference.Material`.
  - Automatic `GroupTypeId` assignment (`Geometry`, `Materials`, `Visibility`, `IdentityData`, `Data`, `Constraints`).
  - Direct parameter association to Extrusion Material (`BuiltInParameter.MATERIAL_ID_PARAM`) and Extrusion Visibility (`BuiltInParameter.IS_VISIBLE_PARAM`).
- **Live Schema Validation & Diagnostic Pre-Check**:
  - Static analysis engine verifies bounding boxes, coordinates, formula parenthesis balance, and parameter mappings before raising Revit API execution.
  - Interactive validation banner with color-coded status badges and detailed diagnostics.
- **JSON Schema Export & Import**:
  - Save custom synthesized schemas to disk and import previously exported family specifications.
- **Multi-Version Revit API Architecture**:
  - Full compatibility with **Revit 2024 (.NET Framework 4.8)**, **Revit 2025 (.NET 8.0)**, and **Revit 2026 (.NET 8.0)**.
  - Multi-targeted SDK (`net8.0-windows` & `net48`).
  - Utilizes modern `ForgeTypeId`, `SpecTypeId`, `GroupTypeId`, and `UnitTypeId.Millimeters` (zero deprecated enums).
- **Background Multi-Document Pipeline**:
  - Dynamically synthesizes sub-components in memory using `NewFamilyDocument()`.
  - Strictly follows the Revit API **`Regenerate()` Rule** after reference plane creation to prevent alignment failure exceptions.
  - Automatically loads nested `.rfa`s into the Master Family and links parameters via `famMgr.AssociateElementParameterToFamilyParameter()`.
  - Automatically loads the resulting Master Family directly into your open project using custom `IFamilyLoadOptions`.

---

## One-Click Installation

To install on your company workstation without requiring administrative rights or Visual Studio:

1. Clone or download this repository.
2. Double-click **`Install.bat`** (or right-click `Installer\Install-ParametricFamilyForge.ps1` and select **Run with PowerShell**).
3. The installer will:
   - Detect installed Revit 2024/2025/2026 instances.
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
├── ParametricFamilyForge.csproj            # Multi-targeted .NET 8.0 / .NET 4.8 SDK project
├── Install.bat                             # One-click Windows batch installer
├── Uninstall.bat                           # Clean uninstaller script
│
├── Commands/
│   └── LaunchForgeCommand.cs               # IExternalCommand: Launches modeless WPF window
│
├── Models/
│   ├── FamilySchema.cs                     # Strongly typed JSON bridge & Type Catalog schema
│   └── ForgeSettings.cs                    # Persistent configuration settings
│
├── Services/
│   ├── GeminiVisionService.cs              # Gemini Vision API client + deterministic fallback
│   ├── PresetService.cs                    # Built-in presets, JSON export/import, CSV catalog
│   ├── SchemaValidator.cs                  # Static analysis & pre-generation diagnostic check
│   └── SettingsService.cs                  # %APPDATA% JSON config persistence
│
├── RevitEngine/
│   ├── FamilyGenerationHandler.cs          # IExternalEventHandler thread bridge
│   ├── NestedFamilyBuilder.cs              # Sub-component family compiler
│   ├── MasterFamilyBuilder.cs              # Master host compiler, geometry, multi-type catalog & loading
│   └── TemplateResolver.cs                 # Intelligent Revit family template locator
│
├── UI/
│   ├── ForgeWindow.xaml                    # Modeless WPF GUI with Preset bar & validation banner
│   └── ForgeWindow.xaml.cs                 # WPF code-behind with import/export & live diagnostics
│
└── Installer/
    ├── Install-ParametricFamilyForge.ps1   # PowerShell installation engine
    └── Uninstall-ParametricFamilyForge.ps1 # PowerShell removal engine
```

---

## Configuration

Click **"⚙ AI Engine Settings"** in the top bar of the add-in to customize:
- **Gemini API Key**: Your Google Gemini API Key.
- **AI Model Engine**: Switch between `gemini-3.8-flash`, `gemini-4-argon`, `gemini-3.1-flash-lite`, etc.
- **Temperature**: Precision control (default `0.20` for dimensional accuracy).
- **Template Directory**: Path to your Revit Family Templates (`.rft`), defaults to standard English templates.
- **Output Directory**: Target directory where compiled `.rfa` files will be saved.
