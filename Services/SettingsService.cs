using System;
using System.IO;
using System.Text.Json;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    /// <summary>
    /// Service to load and persist add-in configuration settings to %APPDATA%/Autodesk/Revit/Addins/ParametricFamilyForge/config.json.
    /// </summary>
    public static class SettingsService
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Autodesk", "Revit", "Addins", "ParametricFamilyForge"
        );

        private static readonly string SettingsFilePath = Path.Combine(SettingsFolder, "config.json");

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public static ForgeSettings LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                {
                    string json = File.ReadAllText(SettingsFilePath);
                    var settings = JsonSerializer.Deserialize<ForgeSettings>(json, JsonOptions);
                    if (settings != null)
                    {
                        if (string.IsNullOrWhiteSpace(settings.TemplateDirectory) || !Directory.Exists(settings.TemplateDirectory))
                        {
                            settings.TemplateDirectory = RevitEngine.TemplateResolver.GetBestDefaultTemplateDirectory();
                        }
                        if (string.IsNullOrWhiteSpace(settings.ModelEngine) || settings.ModelEngine.Equals("gemini-3.8", StringComparison.OrdinalIgnoreCase) || settings.ModelEngine.Contains("3.6") || settings.ModelEngine.Contains("1.5"))
                        {
                            settings.ModelEngine = "gemini-2.5-flash";
                        }
                        return settings;
                    }
                }
            }
            catch
            {
                // Return default fallback on any deserialization failure
            }

            var defaultSettings = new ForgeSettings();
            defaultSettings.TemplateDirectory = RevitEngine.TemplateResolver.GetBestDefaultTemplateDirectory();
            SaveSettings(defaultSettings);
            return defaultSettings;
        }

        public static void SaveSettings(ForgeSettings settings)
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                {
                    Directory.CreateDirectory(SettingsFolder);
                }

                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Silently ignore if file permission issues arise
            }
        }
    }
}
