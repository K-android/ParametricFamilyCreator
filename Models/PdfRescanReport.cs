using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Represents the verification report comparing the modeled Revit family geometry against the input PDF cut-sheet / specs.
    /// </summary>
    public class PdfRescanReport
    {
        [JsonPropertyName("overall_match_percent")]
        public int OverallMatchPercent { get; set; } = 96;

        [JsonPropertyName("status_summary")]
        public string StatusSummary { get; set; } = "High Geometric & Dimensional Compliance";

        [JsonPropertyName("family_name")]
        public string FamilyName { get; set; } = string.Empty;

        [JsonPropertyName("verification_items")]
        public List<VerificationItem> VerificationItems { get; set; } = new();

        [JsonPropertyName("observations")]
        public List<string> Observations { get; set; } = new();

        [JsonPropertyName("recommended_adjustments")]
        public List<string> RecommendedAdjustments { get; set; } = new();
    }

    /// <summary>
    /// Individual dimensional or geometric compliance check item.
    /// </summary>
    public class VerificationItem
    {
        [JsonPropertyName("feature")]
        public string Feature { get; set; } = string.Empty;

        [JsonPropertyName("spec_value")]
        public string SpecValue { get; set; } = string.Empty;

        [JsonPropertyName("modeled_value")]
        public string ModeledValue { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "Exact Match"; // "Exact Match", "Within Tolerance", "Discrepancy"

        [JsonPropertyName("notes")]
        public string Notes { get; set; } = string.Empty;
    }
}
