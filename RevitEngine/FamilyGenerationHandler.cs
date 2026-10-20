using System;
using System.Collections.Generic;
using System.IO;
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
        public string TemplateDirectory { get; set; } = @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English";
        public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

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
                OnLog?.Invoke($"[REVIT THREAD] Initiating family generation for '{CurrentSchema.MasterFamilyName}'...");

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
                    OutputDirectory
                );

                OnLog?.Invoke($"[REVIT SUCCESS] Master Family successfully saved to '{finalMasterPath}' and loaded into current project!");
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
