using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.UI;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Intelligently resolves Revit Family Template (.rft) files across all Revit versions,
    /// language packs (English, en-US, English_I), and regional naming conventions (Metric vs Imperial).
    /// </summary>
    public static class TemplateResolver
    {
        public static string Resolve(UIApplication uiApp, string templateName, string? userSpecifiedDirectory = null)
        {
            // 1. Direct path check if templateName is already an existing absolute path
            if (File.Exists(templateName))
            {
                return templateName;
            }

            string cleanName = Path.GetFileName(templateName);
            string revitVersion = uiApp.Application.VersionNumber ?? "2025";

            // 2. Candidate directories
            var searchDirectories = new System.Collections.Generic.List<string>();

            if (!string.IsNullOrWhiteSpace(userSpecifiedDirectory) && Directory.Exists(userSpecifiedDirectory))
            {
                searchDirectories.Add(userSpecifiedDirectory);
            }

            // Check Revit's own configured family template path from Revit Options
            try
            {
                string revitAppTemplatePath = uiApp.Application.FamilyTemplatePath;
                if (!string.IsNullOrWhiteSpace(revitAppTemplatePath) && Directory.Exists(revitAppTemplatePath))
                {
                    searchDirectories.Add(revitAppTemplatePath);
                }
            }
            catch { }

            // Standard Autodesk ProgramData locations
            string[] candidateYears = new string[] { revitVersion, "2025", "2024", "2026" };
            string[] candidateLangs = new string[] { "English", "en-US", "English_I", "English-Imperial", "" };

            foreach (var yr in candidateYears)
            {
                foreach (var lang in candidateLangs)
                {
                    string path = string.IsNullOrEmpty(lang)
                        ? $@"C:\ProgramData\Autodesk\RVT {yr}\Family Templates"
                        : $@"C:\ProgramData\Autodesk\RVT {yr}\Family Templates\{lang}";

                    if (Directory.Exists(path) && !searchDirectories.Contains(path, StringComparer.OrdinalIgnoreCase))
                    {
                        searchDirectories.Add(path);
                    }
                }
            }

            // 3. Candidate template file names (Metric vs Imperial aliases)
            var candidateNames = new System.Collections.Generic.List<string> { cleanName };

            if (cleanName.StartsWith("Metric ", StringComparison.OrdinalIgnoreCase))
            {
                // Also search for non-metric name (e.g. "Generic Model.rft" vs "Metric Generic Model.rft")
                candidateNames.Add(cleanName.Substring(7));
            }
            else
            {
                // Also search for metric prefix
                candidateNames.Add("Metric " + cleanName);
            }

            // Fallback generic names
            if (cleanName.Contains("Furniture", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add("Metric Furniture.rft");
                candidateNames.Add("Furniture.rft");
                candidateNames.Add("Metric Generic Model.rft");
                candidateNames.Add("Generic Model.rft");
            }
            else if (cleanName.Contains("Door", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add("Metric Door.rft");
                candidateNames.Add("Door.rft");
                candidateNames.Add("Metric Generic Model.rft");
                candidateNames.Add("Generic Model.rft");
            }
            else if (cleanName.Contains("Window", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add("Metric Window.rft");
                candidateNames.Add("Window.rft");
                candidateNames.Add("Metric Generic Model.rft");
                candidateNames.Add("Generic Model.rft");
            }
            else
            {
                candidateNames.Add("Metric Generic Model.rft");
                candidateNames.Add("Generic Model.rft");
            }

            // 4. Search in candidate directories
            foreach (var dir in searchDirectories)
            {
                foreach (var name in candidateNames)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }

                // Recursive search in this template directory
                try
                {
                    foreach (var name in candidateNames)
                    {
                        var found = Directory.GetFiles(dir, name, SearchOption.AllDirectories).FirstOrDefault();
                        if (!string.IsNullOrEmpty(found) && File.Exists(found))
                        {
                            return found;
                        }
                    }
                }
                catch { }
            }

            // 5. Last resort: Any .rft file in candidate directories
            foreach (var dir in searchDirectories)
            {
                try
                {
                    var anyRft = Directory.GetFiles(dir, "*Generic Model*.rft", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(dir, "*.rft", SearchOption.AllDirectories).FirstOrDefault();

                    if (!string.IsNullOrEmpty(anyRft) && File.Exists(anyRft))
                    {
                        return anyRft;
                    }
                }
                catch { }
            }

            throw new FileNotFoundException(
                $"Could not locate a Revit Family Template (.rft) for '{templateName}'.\n\n" +
                $"Looked in:\n - " + string.Join("\n - ", searchDirectories) + "\n\n" +
                "Tip: Ensure Revit Content Libraries are installed, or click '...' in the add-in to select your templates folder."
            );
        }
    }
}
