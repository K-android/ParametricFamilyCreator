using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Intelligently resolves Revit Family Template (.rft) and Seed (.rfa) files across all Revit versions,
    /// language packs (English, en-US, English_I), regional naming conventions (Metric vs Imperial),
    /// and bundled add-in fallback templates.
    /// </summary>
    public static class TemplateResolver
    {
        /// <summary>
        /// Discovers the best existing template directory on the machine to populate the UI.
        /// </summary>
        public static string GetBestDefaultTemplateDirectory(UIApplication? uiApp = null)
        {
            var candidates = new List<string>();

            // 1. Add-in's own bundled Templates folder
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmDir))
                {
                    candidates.Add(Path.Combine(asmDir, "Templates"));
                }
            }
            catch { }

            // 2. Global add-in Templates folder in %APPDATA%
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            candidates.Add(Path.Combine(appData, "Autodesk", "Revit", "Addins", "ParametricFamilyForge", "Templates"));

            string revitVersion = uiApp?.Application?.VersionNumber ?? "2025";
            candidates.Add(Path.Combine(appData, "Autodesk", "Revit", "Addins", revitVersion, "ParametricFamilyForge", "Templates"));

            // 3. Revit Application configured FamilyTemplatePath
            try
            {
                string? revitAppPath = uiApp?.Application?.FamilyTemplatePath;
                if (!string.IsNullOrWhiteSpace(revitAppPath))
                {
                    candidates.Add(revitAppPath!);
                }
            }
            catch { }

            // 4. Forma templates directory
            string[] years = new string[] { revitVersion, "2026", "2025", "2024" };
            foreach (var yr in years)
            {
                candidates.Add($@"C:\ProgramData\Autodesk\Revit\Addins\{yr}\Forma\Templates");
            }

            // 5. Standard Autodesk ProgramData RVT directories
            string[] langs = new string[] { "English", "en-US", "English_I", "English-Imperial", "" };
            foreach (var yr in years)
            {
                foreach (var lang in langs)
                {
                    candidates.Add(string.IsNullOrEmpty(lang)
                        ? $@"C:\ProgramData\Autodesk\RVT {yr}\Family Templates"
                        : $@"C:\ProgramData\Autodesk\RVT {yr}\Family Templates\{lang}");
                }
            }

            // Return first directory that exists and has files
            foreach (var dir in candidates)
            {
                try
                {
                    if (Directory.Exists(dir) && Directory.GetFiles(dir, "*.*").Length > 0)
                    {
                        return dir;
                    }
                }
                catch { }
            }

            // Return first directory that exists even if empty
            foreach (var dir in candidates)
            {
                try
                {
                    if (Directory.Exists(dir))
                    {
                        return dir;
                    }
                }
                catch { }
            }

            // Fallback default
            return Path.Combine(appData, "Autodesk", "Revit", "Addins", "ParametricFamilyForge", "Templates");
        }

        /// <summary>
        /// Locates a valid .rft or .rfa family template on disk.
        /// </summary>
        public static string Resolve(UIApplication uiApp, string templateName, string? userSpecifiedDirectory = null)
        {
            // 1. Direct path check if templateName is already an existing absolute path
            if (File.Exists(templateName))
            {
                return templateName;
            }

            string cleanName = Path.GetFileName(templateName);
            string revitVersion = uiApp.Application.VersionNumber ?? "2025";

            // 2. Candidate search directories in priority order
            var searchDirectories = new List<string>();

            void AddDir(string? path)
            {
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && !searchDirectories.Contains(path!, StringComparer.OrdinalIgnoreCase))
                {
                    searchDirectories.Add(path!);
                }
            }

            // A. User-specified directory from the UI
            AddDir(userSpecifiedDirectory);

            // B. Add-in's own executing directory Templates folder
            try
            {
                string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
                if (!string.IsNullOrEmpty(asmDir))
                {
                    AddDir(Path.Combine(asmDir, "Templates"));
                    AddDir(asmDir);
                }
            }
            catch { }

            // C. Global %APPDATA% add-in Templates folder
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddDir(Path.Combine(appData, "Autodesk", "Revit", "Addins", "ParametricFamilyForge", "Templates"));
            AddDir(Path.Combine(appData, "Autodesk", "Revit", "Addins", revitVersion, "ParametricFamilyForge", "Templates"));

            // D. Revit application configured path from Revit Options
            try
            {
                string? revitAppTemplatePath = uiApp?.Application?.FamilyTemplatePath;
                if (!string.IsNullOrWhiteSpace(revitAppTemplatePath))
                {
                    AddDir(revitAppTemplatePath);
                }
            }
            catch { }

            // E. Discovered paths from Revit.ini files (e.g. D:\revit\Family Templates\)
            string[] years = new string[] { revitVersion, "2026", "2025", "2024" };
            foreach (var yr in years)
            {
                try
                {
                    string iniPath = Path.Combine(appData, "Autodesk", "Revit", $"Autodesk Revit {yr}", "Revit.ini");
                    if (File.Exists(iniPath))
                    {
                        foreach (var line in File.ReadAllLines(iniPath))
                        {
                            if (line.StartsWith("FamilyTemplatePath=", StringComparison.OrdinalIgnoreCase))
                            {
                                string val = line.Substring("FamilyTemplatePath=".Length).Trim();
                                AddDir(val);
                                AddDir(Path.Combine(val, "English"));
                                AddDir(Path.Combine(val, "en-US"));
                            }
                        }
                    }
                }
                catch { }
            }

            // F. Autodesk Forma Templates directories (contains Mass.rft, Metric Mass.rft, etc.)
            foreach (var yr in years)
            {
                AddDir($@"C:\ProgramData\Autodesk\Revit\Addins\{yr}\Forma\Templates");
            }

            // G. Standard Autodesk ProgramData RVT locations
            string[] candidateLangs = new string[] { "English", "en-US", "English_I", "English-Imperial", "" };
            foreach (var yr in years)
            {
                foreach (var lang in candidateLangs)
                {
                    string path = string.IsNullOrEmpty(lang)
                        ? $@"C:\ProgramData\Autodesk\RVT {yr}\Family Templates"
                        : $@"C:\ProgramData\Autodesk\RVT {yr}\Family Templates\{lang}";
                    AddDir(path);
                }
            }

            // 3. Build candidate template file names (Metric vs Imperial aliases)
            var candidateNames = new List<string> { cleanName };

            if (cleanName.StartsWith("Metric ", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add(cleanName.Substring(7));
            }
            else
            {
                candidateNames.Add("Metric " + cleanName);
            }

            // Category-specific fallbacks
            if (cleanName.Contains("Door", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add("Metric Door.rft");
                candidateNames.Add("Door.rft");
            }
            else if (cleanName.Contains("Window", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add("Metric Window.rft");
                candidateNames.Add("Window.rft");
            }
            else if (cleanName.Contains("Furniture", StringComparison.OrdinalIgnoreCase))
            {
                candidateNames.Add("Metric Furniture.rft");
                candidateNames.Add("Furniture.rft");
            }

            // Generic fallbacks
            candidateNames.Add("Metric Generic Model.rft");
            candidateNames.Add("Generic Model.rft");
            candidateNames.Add("Metric Mass.rft");
            candidateNames.Add("Mass.rft");
            candidateNames.Add("Metric Generic Model.rfa");
            candidateNames.Add("Generic Model.rfa");

            // 4. Exact filename search across candidate directories
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

            // 5. Broad wildcard fallback: Any .rft file in candidate directories
            foreach (var dir in searchDirectories)
            {
                try
                {
                    var anyRft = Directory.GetFiles(dir, "*Generic Model*.rft", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(dir, "*Mass*.rft", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(dir, "*.rft", SearchOption.AllDirectories).FirstOrDefault();

                    if (!string.IsNullOrEmpty(anyRft) && File.Exists(anyRft))
                    {
                        return anyRft;
                    }
                }
                catch { }
            }

            // 6. Broad seed fallback: Any .rfa file in candidate directories
            foreach (var dir in searchDirectories)
            {
                try
                {
                    var anyRfa = Directory.GetFiles(dir, "*Generic Model*.rfa", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(dir, "*.rfa", SearchOption.AllDirectories).FirstOrDefault();

                    if (!string.IsNullOrEmpty(anyRfa) && File.Exists(anyRfa))
                    {
                        return anyRfa;
                    }
                }
                catch { }
            }

            throw new FileNotFoundException(
                $"Could not locate a Revit Family Template (.rft or .rfa) for '{templateName}'.\n\n" +
                $"Looked in:\n - " + string.Join("\n - ", searchDirectories) + "\n\n" +
                "Tip: Ensure Revit Content Libraries are installed, or click '...' in the add-in to select your templates folder."
            );
        }
    }
}
