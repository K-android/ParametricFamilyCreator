using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;
using ParametricFamilyForge.Services;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Thread-safe ExternalEventHandler that safely marshals family generation and conversational copilot operations
    /// from the modeless WPF UI back to the Revit main UI thread.
    /// </summary>
    public class FamilyGenerationHandler : IExternalEventHandler
    {
        public FamilySchema? CurrentSchema { get; set; }
        public ExecutionPlan? CurrentPlan { get; set; }
        public bool RequestInspectionOnly { get; set; } = false;

        public string TemplateDirectory { get; set; } = string.Empty;
        public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RevitGeneratedFamilies");

        public ExecutionResult? LastExecutionResult { get; private set; }
        public ValidationResult? LastValidationResult { get; private set; }
        public FamilyState? LastFamilyState { get; private set; }

        public Action<string>? OnLog { get; set; }
        public Action<bool, string>? OnCompleted { get; set; }
        public Action<ExecutionResult, ValidationResult>? OnPlanCompleted { get; set; }
        public Action<FamilyState>? OnInspectionCompleted { get; set; }

        public void Execute(UIApplication uiApp)
        {
            // Inspection Only Mode
            if (RequestInspectionOnly)
            {
                RequestInspectionOnly = false;
                Document? activeDoc = uiApp.ActiveUIDocument?.Document;
                var state = FamilyInspectionService.Inspect(activeDoc);
                LastFamilyState = state;
                OnLog?.Invoke($"[COPILOT INSPECTOR] Inspected active document: '{state.FamilyName}' ({state.FamilyCategory}), {state.Parameters.Count} parameter(s), {state.Solids.Count} solid/void form(s).");
                OnInspectionCompleted?.Invoke(state);
                return;
            }

            // Conversational Execution Plan Mode (Deterministic Copilot)
            if (CurrentPlan != null)
            {
                ExecuteConversationalPlan(uiApp);
                return;
            }

            // Legacy Full FamilySchema Builder Mode
            ExecuteSchemaBuilder(uiApp);
        }

        private void ExecuteConversationalPlan(UIApplication uiApp)
        {
            if (CurrentPlan == null) return;

            try
            {
                if (string.IsNullOrWhiteSpace(TemplateDirectory) || !Directory.Exists(TemplateDirectory))
                {
                    TemplateDirectory = TemplateResolver.GetBestDefaultTemplateDirectory(uiApp);
                }

                Document? targetDoc = uiApp.ActiveUIDocument?.Document;
                bool createdNew = false;
                string? finalMasterPath = null;

                if (targetDoc == null || !targetDoc.IsFamilyDocument)
                {
                    OnLog?.Invoke("[COPILOT REVIT THREAD] No active family document open. Spawning new metric family from template...");
                    string templatePath = TemplateResolver.Resolve(uiApp, "Metric Generic Model.rft", TemplateDirectory);
                    targetDoc = uiApp.Application.NewFamilyDocument(templatePath);
                    createdNew = true;
                }
                else
                {
                    OnLog?.Invoke($"[COPILOT REVIT THREAD] Modifying currently open family document '{targetDoc.Title}' in-place...");
                }

                // 1. Execute plan operations deterministically
                var execResult = FamilyOperationExecutor.ExecutePlan(targetDoc, CurrentPlan, OnLog);
                LastExecutionResult = execResult;

                // 2. Validate design intent against live Revit BRep geometry and flex tests
                var valResult = PostExecutionValidator.ValidateExecution(targetDoc, CurrentPlan, execResult, OnLog);
                LastValidationResult = valResult;

                // 3. Inspect updated family state
                LastFamilyState = FamilyInspectionService.Inspect(targetDoc);

                if (createdNew)
                {
                    if (!Directory.Exists(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);
                    string famName = "Parametric_Copilot_Window";
                    finalMasterPath = Path.Combine(OutputDirectory, $"{famName}.rfa");
                    var saveOpts = new SaveAsOptions { OverwriteExistingFile = true, Compact = true };
                    targetDoc.SaveAs(finalMasterPath, saveOpts);
                    targetDoc.Close(false);

                    // Open and focus in Revit UI viewport
                    try
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
                            OnLog?.Invoke($"[3D VIEWPORT LIVE] Copilot family opened and focused in 3D Family Editor!");
                        }
                    }
                    catch (Exception ex)
                    {
                        OnLog?.Invoke($"[3D VIEWPORT NOTE] {ex.Message}");
                    }
                }
                else
                {
                    // Refresh view in existing open document
                    try
                    {
                        uiApp.ActiveUIDocument?.RefreshActiveView();
                    }
                    catch { }
                }

                OnPlanCompleted?.Invoke(execResult, valResult);
                OnCompleted?.Invoke(execResult.ApiSuccess, finalMasterPath ?? "Active Family Updated");
            }
            catch (Exception ex)
            {
                string errMsg = $"[COPILOT ERROR] Plan execution failed: {ex.Message}";
                OnLog?.Invoke(errMsg);
                OnCompleted?.Invoke(false, ex.Message);
            }
            finally
            {
                CurrentPlan = null;
            }
        }

        private void ExecuteSchemaBuilder(UIApplication uiApp)
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

                int solidsCount = CurrentSchema.HostExtrusions.Count(e => !e.IsVoid) + CurrentSchema.HostBlends.Count(b => !b.IsVoid) + CurrentSchema.HostSweeps.Count(s => !s.IsVoid) + CurrentSchema.HostRevolves.Count(r => !r.IsVoid);
                int voidsCount = CurrentSchema.HostExtrusions.Count(e => e.IsVoid) + CurrentSchema.HostBlends.Count(b => b.IsVoid) + CurrentSchema.HostRevolves.Count(r => r.IsVoid);
                OnLog?.Invoke($"  ✔ Solid Elements: {solidsCount} built (Extrusions, Blends, Revolves, Sweeps)");
                OnLog?.Invoke($"  ✔ Void Cut Cavities: {voidsCount} carved (Inner Revolve Bowl, Tap Hole, Drain Cutouts)");

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
            finally
            {
                CurrentSchema = null;
            }
        }

        public string GetName() => "ParametricFamilyForge_ExternalEventHandler";
    }
}
