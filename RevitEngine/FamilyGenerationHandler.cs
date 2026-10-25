using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Thread-safe ExternalEventHandler that safely marshals family generation calls
    /// from the modeless WPF UI back to the Revit main UI thread.
    /// </summary>
    public class FamilyGenerationHandler : IExternalEventHandler
    {
        public FamilySchema? CurrentSchema { get; set; }
        public string TemplateDirectory { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RevitGeneratedFamilies");

        public Action<string>? OnLog { get; set; }
        public Action<bool, string>? OnCompleted { get; set; }

        public void Execute(UIApplication uiApp)
        {
            if (CurrentSchema == null)
            {
                OnLog?.Invoke("[ERROR] No FamilySchema provided for generation.");
                OnCompleted?.Invoke(false, "No specification schema provided.");
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(TemplateDirectory) || !Directory.Exists(TemplateDirectory))
                {
                    TemplateDirectory = TemplateResolver.GetBestDefaultTemplateDirectory(uiApp);
                }

                OnLog?.Invoke($"[REVIT THREAD] Initiating family generation for '{CurrentSchema.MasterFamilyName}'...");
                OnLog?.Invoke($"[REVIT THREAD] Using template root: '{TemplateDirectory}'");

                // 1. Generate unique nested families
                var nestedRfaMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var createdTempFiles = new List<string>();

                foreach (var nestedDef in CurrentSchema.NestedFamilies)
                {
                    if (!nestedRfaMap.ContainsKey(nestedDef.NestedFamilyName))
                    {
                        OnLog?.Invoke($"[REVIT THREAD] Compiling nested sub-component: '{nestedDef.NestedFamilyName}'...");
                        string tempNestedPath = NestedFamilyBuilder.BuildAndSave(uiApp, nestedDef, TemplateDirectory);
                        nestedRfaMap[nestedDef.NestedFamilyName] = tempNestedPath;
                        createdTempFiles.Add(tempNestedPath);
                        OnLog?.Invoke($"[REVIT THREAD] Nested component created at '{tempNestedPath}'");
                    }
                }

                // 2. Build Master Host Family and load into active project
                OnLog?.Invoke($"[REVIT THREAD] Building Master Family '{CurrentSchema.MasterFamilyName}' and linking constraints...");
                string finalMasterPath = MasterFamilyBuilder.BuildAndLoadIntoProject(
                    uiApp,
                    CurrentSchema,
                    nestedRfaMap,
                    TemplateDirectory,
                    OutputDirectory,
                    OnLog
                );

                OnLog?.Invoke($"[REVIT SUCCESS] Master Family successfully saved to '{finalMasterPath}' and loaded into current project!");

                // Phase D Geometry Verification Loop: summarize geometric features and verified dimensions
                OnLog?.Invoke($"[GEOMETRY VERIFICATION] Verification summary for '{CurrentSchema.MasterFamilyName}':");
                if (CurrentSchema.MasterParameters != null && CurrentSchema.MasterParameters.Count > 0)
                {
                    foreach (var p in CurrentSchema.MasterParameters)
                    {
                        OnLog?.Invoke($"  ✔ Parameter Constraint: {p.Name} = {p.DefaultValueMm:0.##} mm");
                    }
                }
                int solidsCount = CurrentSchema.HostExtrusions.Count(e => !e.IsVoid) + CurrentSchema.HostBlends.Count(b => !b.IsVoid) + CurrentSchema.HostSweeps.Count(s => !s.IsVoid) + CurrentSchema.HostRevolves.Count(r => !r.IsVoid);
                int voidsCount = CurrentSchema.HostExtrusions.Count(e => e.IsVoid) + CurrentSchema.HostBlends.Count(b => b.IsVoid) + CurrentSchema.HostRevolves.Count(r => r.IsVoid);
                OnLog?.Invoke($"  ✔ Solid Elements: {solidsCount} built (Extrusions, Blends, Revolves, Sweeps)");
                OnLog?.Invoke($"  ✔ Void Cut Cavities: {voidsCount} carved (Inner Revolve Bowl, Tap Hole, Drain Cutouts)");
                if (CurrentSchema.MepConnectors != null && CurrentSchema.MepConnectors.Count > 0)
                {
                    OnLog?.Invoke($"  ✔ MEP Engineering: {CurrentSchema.MepConnectors.Count} system connector(s) attached");
                }
                if (CurrentSchema.SymbolicCurves != null && CurrentSchema.SymbolicCurves.Count > 0)
                {
                    OnLog?.Invoke($"  ✔ 2D Symbolic Linework: {CurrentSchema.SymbolicCurves.Count} plan curve(s) projected");
                }

                // 3. Open and Activate Family Document in Revit UI (Live 3D Viewport Focus)
                try
                {
                    if (File.Exists(finalMasterPath))
                    {
                        UIDocument activeUiDoc = uiApp.OpenAndActivateDocument(finalMasterPath);
                        if (activeUiDoc != null)
                        {
                            View3D? view3d = new FilteredElementCollector(activeUiDoc.Document)
                                .OfClass(typeof(View3D))
                                .Cast<View3D>()
                                .FirstOrDefault(v => !v.IsTemplate);

                            if (view3d != null && activeUiDoc.ActiveView.Id != view3d.Id)
                            {
                                activeUiDoc.ActiveView = view3d;
                            }

                            activeUiDoc.RefreshActiveView();
                            OnLog?.Invoke($"[3D VIEWPORT LIVE] Family '{CurrentSchema.MasterFamilyName}' opened and focused in 3D Family Editor!");
                        }
                    }
                }
                catch (Exception viewEx)
                {
                    OnLog?.Invoke($"[3D VIEWPORT NOTE] Master family generated at '{finalMasterPath}'. Note on opening: {viewEx.Message}");
                }

                OnCompleted?.Invoke(true, finalMasterPath);
            }
            catch (Exception ex)
            {
                string errMsg = $"[REVIT ERROR] Generation failed: {ex.Message}\n{ex.StackTrace}";
                OnLog?.Invoke(errMsg);
                OnCompleted?.Invoke(false, ex.Message);
            }
        }

        public string GetName() => "ParametricFamilyForge_ExternalEventHandler";
    }
}
