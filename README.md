# ParametricFamilyForge

**ParametricFamilyForge** is a production-grade Autodesk Revit (2024, 2025 & 2026) add-in powered by Google Gemini Vision and a deterministic AEC procedural rule engine. It transforms manufacturer cut-sheet PDFs, orthographic architectural drawings, and natural language prompts into high-LOD, production-ready parametric Revit Families (`.rfa`) with organic 3D curvature, boolean void carvings, nested sub-components, multi-type catalogs, and engineering MEP connectors.

---

## What Sets It Apart

Unlike basic AI add-ins that generate blocky, faceted approximations or crash when flexing dimensions, **ParametricFamilyForge** operates on a multi-stage engineering pipeline:

* **Strictly Constrained Extraction → Deterministic C# Archetype Rigs**:
  * Strips the LLM of any authority over raw 3D polygon/mesh topology. Constrains Gemini strictly to cut-sheet parameter extraction into an `ExtractedCutSheetSpec` where every measurement carries its datum anchor (`Floor`, `WallFace`, `RimLevel`, `Centerline`, `Countertop`), source page, and confidence rating.
  * 100% of 3D BIM modeling is performed by pre-engineered, mathematically watertight deterministic C# archetype rigs (`SemiPedestalBasinRig`, `CountertopBasinRig`, `WaterClosetRig`, `SingleDoorRig`, `CaseworkCabinetRig`, `BaseplateJointRig`).
* **Direct Vector PDF Linework Extraction (`PdfPig`)**:
  * Parses native PostScript CAD vector curves (splines, arcs, lines) directly from PDF streams without raster OCR guessing.
  * Calibrates scale from dimension callout annotations ($S = \text{DimMm} / \text{PtSpan}$) and feeds true manufacturer profiles directly into Revit `CurveArray` sketches. Falls back gracefully to multimodal vision only on raster-only scans.
* **Closed-Loop In-Revit BRep Geometry Verification & Auto-Healing**:
  * Directly audits real Revit `Solid` BRep bounding boxes and reference plane distances via the Revit API post-generation.
  * Calculates deviation $\Delta = |\text{Modeled} - \text{Spec}|$. If any feature is off by more than $2.0\text{ mm}$, an automated self-healing loop adjusts parameter bounds in `FamilyManager`, regenerates the document, and verifies convergence before committing.
* **Native Revit Dockable Panel (`IDockablePaneProvider`)**: Sits right inside your Revit workspace as a native dockable pane with single-click end-to-end procedural execution.
* **Organic 3D Curvature (Revolves & 3D Sweeps)**:
  * Uses native Revit `NewRevolution` to carve smooth $360^\circ$ dished bowl cavities, spherical contours, and axisymmetric cylinders.
  * Uses native `NewSweep` to extrude circular profiles along 3D spatial guide curves for mixer taps, spouts, and bottle traps.
* **Natively Hollow Concentric Sketch Profiles**:
  * Bakes outer perimeter and inner cavity loops directly into the extrusion sketch (`HollowDShape`), guaranteeing that basin bowls and sinks are hollowed out at birth—eliminating flat solid lids.
* **Monolithic Volumetric Modeling (Zero Air Gaps)**:
  * Generates unified full-depth ceramic bodies (e.g. $178\text{ mm}$ wall height), solid dish floors, and overlapping half-pedestal shrouds that eliminate floating geometry or gaps.
* **Live 3D Viewport Sync & Focus**:
  * Upon generation, the add-in automatically opens the family in Revit's 3D Family Editor (`uiApp.OpenAndActivateDocument`), switches to the 3D isometric view, and frames the camera on the geometry.
* **Automated Multi-Type Parametric Flex-Test Rig**:
  * Built-in flex test engine iterates through all defined catalog types (e.g. *Standard*, *Compact*, *Executive*), flexing dimensional parameters in `FamilyManager` and validating constraint satisfaction within sub-transactions.
* **Conversational Geometry Refiner**:
  * Allows one-click refinement chips (*"+25mm Bowl Depth"*, *"Add Overflow Hole"*, *"Wall-Hung Pedestal"*, *"Scale Width 800mm"*) or conversational prompt adjustments without regenerating from scratch.

---

## Supported BIM Categories

| Category | Reconstructed Features from PDF | Native Revit Primitives |
| :--- | :--- | :--- |
| **Plumbing Fixtures** | Wash basins, pedestal sinks, vessel bowls, bathtubs, faucets, overflow holes, waste traps | `HostRevolves`, `HostExtrusions`, `HostSweeps`, `HostBlends`, MEP Connectors |
| **Doors & Openings** | Jamb frame, panel leaf, vision lite glazing void cutouts, nested hardware, 2D plan swings | `HostExtrusions`, `HostOpenings`, `SymbolicCurves`, `NestedFamilies` |
| **Windows** | Outer frame, operable sash, double glazing infill, sill projection, wall opening cuts | `HostExtrusions`, `HostOpenings`, `HostBlends` |
| **Furniture & FF&E** | Office desks, ergonomic task chairs, conference tables, cable grommet void cutouts | `HostExtrusions`, `HostSweeps`, `HostBlends`, Parameters |
| **Casework & Millwork** | Base cabinets, cupboards, solid surface countertops, adjustable shelving, hardware pulls | `HostExtrusions`, `NestedFamilies`, Subcategories |
| **Mechanical & MEP** | Water pumps, air chillers, fans, valves, air terminals, system attachments | `HostExtrusions`, `HostRevolves`, `MepConnectors` (Pipe, Duct, Electrical) |
| **Structural & Joints** | Column baseplates, anchor bolt hole voids, stiffener gusset plates, column stubs | `HostExtrusions`, Boolean Void Cuts, Constraints |
| **Specialty Equipment** | Entrance canopies, handrails, grab bars, escalators, architectural screens | `HostSweeps`, `HostExtrusions`, `HostBlends` |

---

## 6-Stage Procedural Geometry Pipeline

When you click **"⚡ Generate High-LOD Parametric Family"**, the engine executes:

```
[1] Specs Intake & Orthographic Analysis (Plan X-Y, Elevation X-Z, Section Y-Z)
                       │
                       ▼
[2] Skeleton & Parametric Constraints (Reference Planes, Width/Depth/Height params)
                       │
                       ▼
[3] Primary Solids (Rim Deck, Pedestal Shroud, Revolve Solids)
                       │
                       ▼
[4] Boolean Cavity Carving (Dished Bowl Revolve, Drain Hole, Overflow Cutout)
                       │
                       ▼
[5] High Detail & MEP (Chrome Mixer Tap, 3D Gooseneck Spout, Pipe Connectors)
                       │
                       ▼
[6] Parametric Flex Rig & PDF Rescan Audit (Constraint verification & compliance table)
```

---

## Installation

### Method A: One-Click Executable Installer (`Setup.exe`)
1. Download or clone this repository.
2. Double-click **`Setup.exe`**.
3. The installer automatically detects all installed Revit instances (2024, 2025, 2026), deploys the appropriate binaries, registers the `.addin` manifest, and creates desktop shortcuts.

### Method B: Batch / PowerShell Installer
* Run **`Install.bat`** (or right-click `Installer\Install-ParametricFamilyForge.ps1` and select **Run with PowerShell**).

To remove the add-in at any time, run **`Uninstall.bat`**.

---

## Configuration

In the top bar of the **AI Family Forge** dockable panel:
* **API Key**: Enter your Google Gemini API key (stored securely in `%APPDATA%`).
* **Model Selection**: Switch between `gemini-2.5-flash` (Ultra Fast default), `gemini-2.0-flash`, `gemini-1.5-flash`, `gemini-2.5-pro`, or **Offline AEC Rules** (100% functional without an internet connection or API key).
* **BIM Category / Rig**: Select an explicit archetype or leave on **Auto-Detect from Cut-Sheet**.

---

## Repository Structure

```
ParametricFamilyForge/
│
├── App.cs                                  # IExternalApplication: Ribbon Tab & Dockable Pane registration
├── ParametricFamilyForge.addin             # Revit add-in manifest
├── ParametricFamilyForge.csproj            # Dual-targeted SDK project (net8.0-windows & net48)
├── Setup.exe                               # Standalone Windows GUI installer executable
├── Install.bat                             # One-click batch installation script
├── Uninstall.bat                           # Clean uninstaller script
│
├── Commands/
│   └── LaunchForgeCommand.cs               # Toggles the native Revit Dockable Panel
│
├── Models/
│   ├── ExtractedCutSheetSpec.cs            # Strict parameter extraction schema (Datums, Pages, Confidence)
│   ├── FamilySchema.cs                     # Root procedural schema (Extrusions, Blends, Revolves, Sweeps)
│   ├── ForgeSettings.cs                    # Configuration settings data model
│   └── PdfRescanReport.cs                  # Spec verification & dimensional compliance report models
│
├── Services/
│   ├── GeminiVisionService.cs              # Vision API, orthographic parsing, rescan audit & offline heuristics
│   ├── PdfVectorExtractor.cs               # Native PDF PostScript vector curve & dimension scale extractor
│   ├── PresetService.cs                    # Architectural presets & type catalog export
│   ├── SchemaValidator.cs                  # Pre-generation geometric sanity & diagnostic checks
│   ├── SettingsService.cs                  # Configuration persistence
│   └── SharedParameterService.cs          # Revit Shared Parameter file exporter (.txt)
│
├── RevitEngine/
│   ├── Archetypes/                         # Deterministic kinematic C# archetype modeling rigs
│   │   ├── IArchetypeRig.cs                # Archetype contract & verification rule specification
│   │   ├── SemiPedestalBasinRig.cs         # Wall-hung semi-pedestal washbasin rig
│   │   ├── CountertopBasinRig.cs           # Countertop vessel basin rig
│   │   ├── WaterClosetRig.cs               # Wall-hung & floor-mounted toilet rig
│   │   ├── SingleDoorRig.cs                # Single architectural door with frame, leaf & swing
│   │   ├── CaseworkCabinetRig.cs           # Base & wall casework millwork rig
│   │   ├── BaseplateJointRig.cs            # Structural steel column baseplate joint rig
│   │   ├── UniversalArchetypeRig.cs        # Guaranteed watertight generic component rig
│   │   └── ArchetypeRigRegistry.cs         # Central factory and dispatch registry
│   ├── FamilyGenerationHandler.cs          # Modeless thread-safe IExternalEventHandler bridge & viewport sync
│   ├── GeometryVerifier.cs                 # Closed-loop Revit API BRep solid verifier & self-healing loop
│   ├── MasterFamilyBuilder.cs              # Core builder (Extrusions, Blends, Revolves, Sweeps, Boolean cuts, Flex Rig)
│   ├── NestedFamilyBuilder.cs              # Sub-component family compiler
│   └── TemplateResolver.cs                 # Multi-language & multi-version family template locator
│
├── UI/
│   ├── ForgeDockablePanel.xaml             # Native Revit Dockable Panel interface
│   ├── ForgeDockablePanel.xaml.cs          # Dockable panel pipeline execution & rescan logic
│   ├── ForgeWindow.xaml                    # Standalone modal window with interactive 2D preview canvas
│   └── ForgeWindow.xaml.cs                 # Standalone window code-behind
│
└── Installer/
    ├── SetupProgram.cs                     # C# source for compiled Setup.exe
    ├── Install-ParametricFamilyForge.ps1   # PowerShell installation script
    └── Uninstall-ParametricFamilyForge.ps1 # PowerShell removal script
```

---

## Requirements & Compatibility

* **Autodesk Revit**: 2024, 2025, or 2026.
* **Target Frameworks**:
  * `.NET Framework 4.8` for Revit 2024.
  * `.NET 8.0-windows` for Revit 2025 & Revit 2026.
* **Operating System**: Windows 10 / 11 (x64).
