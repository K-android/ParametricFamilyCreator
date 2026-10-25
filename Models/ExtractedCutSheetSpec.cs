using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Dimension datum anchor references defining where dimensions originate from.
    /// </summary>
    public static class DimensionDatums
    {
        public const string Floor = "Floor";
        public const string WallFace = "WallFace";
        public const string RimLevel = "RimLevel";
        public const string Centerline = "Centerline";
        public const string Countertop = "Countertop";
        public const string Sill = "Sill";
        public const string RoughOpening = "RoughOpening";
    }

    /// <summary>
    /// Strongly-typed individual dimensional parameter extracted from an architectural cut-sheet.
    /// Carries datum anchor, source page, confidence score, and optional engineering notes.
    /// </summary>
    public class ParameterValueSpec
    {
        [JsonPropertyName("value_mm")]
        public double ValueMm { get; set; } = 0.0;

        [JsonPropertyName("datum")]
        public string Datum { get; set; } = DimensionDatums.Floor;

        [JsonPropertyName("source_page")]
        public int SourcePage { get; set; } = 1;

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; } = 1.0;

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }

        public ParameterValueSpec() { }

        public ParameterValueSpec(double valueMm, string datum = DimensionDatums.Floor, double confidence = 1.0, int page = 1, string? notes = null)
        {
            ValueMm = valueMm;
            Datum = datum;
            Confidence = Math.Max(0.0, Math.Min(1.0, confidence));
            SourcePage = Math.Max(1, page);
            Notes = notes;
        }
    }

    /// <summary>
    /// Industry-grade strictly-constrained cut-sheet extraction schema.
    /// Gemini is restricted to extracting ONLY this schema; 100% of the 3D BIM geometry
    /// decisions are delegated to deterministic C# archetype rigs.
    /// </summary>
    public class ExtractedCutSheetSpec
    {
        [JsonPropertyName("archetype")]
        public string Archetype { get; set; } = "SemiPedestalBasin";

        [JsonPropertyName("manufacturer")]
        public string? Manufacturer { get; set; }

        [JsonPropertyName("model_series")]
        public string? ModelSeries { get; set; }

        [JsonPropertyName("product_code")]
        public string? ProductCode { get; set; }

        [JsonPropertyName("revit_category")]
        public string RevitCategory { get; set; } = "Plumbing Fixtures";

        [JsonPropertyName("overall_width_mm")]
        public double OverallWidthMm { get; set; } = 550.0;

        [JsonPropertyName("overall_depth_mm")]
        public double OverallDepthMm { get; set; } = 420.0;

        [JsonPropertyName("overall_height_mm")]
        public double OverallHeightMm { get; set; } = 850.0;

        [JsonPropertyName("parameters")]
        public Dictionary<string, ParameterValueSpec> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("raw_extracted_text")]
        public string? RawExtractedText { get; set; }

        public ExtractedCutSheetSpec() { }

        /// <summary>
        /// Retrieves a parameter's value in millimeters with a fallback default.
        /// </summary>
        public double GetDim(string paramName, double fallback = 0.0)
        {
            if (string.IsNullOrWhiteSpace(paramName)) return fallback;

            // Direct check in Parameters dictionary
            if (Parameters != null && Parameters.TryGetValue(paramName, out var spec) && spec.ValueMm > 0)
            {
                return spec.ValueMm;
            }

            // Standard shorthand dimensional mappings
            string lower = paramName.ToLowerInvariant();
            if (lower.Contains("width") || lower.Contains("length") || lower == "w")
            {
                if (OverallWidthMm > 0) return OverallWidthMm;
            }
            else if (lower.Contains("depth") || lower.Contains("projection") || lower == "d")
            {
                if (OverallDepthMm > 0) return OverallDepthMm;
            }
            else if (lower.Contains("height") || lower == "h")
            {
                if (OverallHeightMm > 0) return OverallHeightMm;
            }

            // Loose match across dictionary keys
            if (Parameters != null)
            {
                foreach (var kvp in Parameters)
                {
                    if (kvp.Key.IndexOf(paramName, StringComparison.OrdinalIgnoreCase) >= 0 && kvp.Value.ValueMm > 0)
                    {
                        return kvp.Value.ValueMm;
                    }
                }
            }

            return fallback;
        }

        public bool HasDim(string paramName)
        {
            if (Parameters == null) return false;
            return Parameters.ContainsKey(paramName);
        }

        public string GetDatum(string paramName, string fallback = DimensionDatums.Floor)
        {
            if (Parameters != null && Parameters.TryGetValue(paramName, out var spec) && !string.IsNullOrWhiteSpace(spec.Datum))
            {
                return spec.Datum;
            }
            return fallback;
        }

        public double GetConfidence(string paramName)
        {
            if (Parameters != null && Parameters.TryGetValue(paramName, out var spec))
            {
                return spec.Confidence;
            }
            return 1.0;
        }

        public void SetParam(string name, double valueMm, string datum = DimensionDatums.Floor, double confidence = 1.0, int page = 1, string? notes = null)
        {
            Parameters[name] = new ParameterValueSpec(valueMm, datum, confidence, page, notes);
        }
    }
}
