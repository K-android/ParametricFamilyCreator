using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Persistent configuration settings for ParametricFamilyForge,
    /// stored securely in the user's Local Application Data folder.
    /// </summary>
    public class ForgeSettings
    {
        [JsonPropertyName("api_key")]
        public string ApiKey { get; set; } = string.Empty;

        [JsonPropertyName("model_engine")]
        public string ModelEngine { get; set; } = "gemini-3.8-flash";

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.2;

        [JsonPropertyName("template_directory")]
        public string TemplateDirectory { get; set; } = string.Empty;

        [JsonPropertyName("output_directory")]
        public string OutputDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RevitGeneratedFamilies");

        [JsonPropertyName("auto_load_into_project")]
        public bool AutoLoadIntoProject { get; set; } = true;
    }
}
