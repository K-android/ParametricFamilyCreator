using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    public class ParameterSnapshot
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("data_type")]
        public string DataType { get; set; } = "Length";

        [JsonPropertyName("value_string")]
        public string ValueString { get; set; } = string.Empty;

        [JsonPropertyName("value_numeric")]
        public double? ValueNumeric { get; set; }

        [JsonPropertyName("formula")]
        public string? Formula { get; set; }

        [JsonPropertyName("is_instance")]
        public bool IsInstance { get; set; }

        [JsonPropertyName("is_read_only")]
        public bool IsReadOnly { get; set; }
    }

    public class ReferencePlaneSnapshot
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("bubble_end_x")]
        public double BubbleEndX { get; set; }

        [JsonPropertyName("bubble_end_y")]
        public double BubbleEndY { get; set; }

        [JsonPropertyName("bubble_end_z")]
        public double BubbleEndZ { get; set; }
    }

    public class SolidSnapshot
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("is_void")]
        public bool IsVoid { get; set; }

        [JsonPropertyName("volume_cu_ft")]
        public double Volume { get; set; }

        [JsonPropertyName("min_x_mm")]
        public double MinX_mm { get; set; }

        [JsonPropertyName("max_x_mm")]
        public double MaxX_mm { get; set; }

        [JsonPropertyName("min_y_mm")]
        public double MinY_mm { get; set; }

        [JsonPropertyName("max_y_mm")]
        public double MaxY_mm { get; set; }

        [JsonPropertyName("min_z_mm")]
        public double MinZ_mm { get; set; }

        [JsonPropertyName("max_z_mm")]
        public double MaxZ_mm { get; set; }

        [JsonPropertyName("material_parameter")]
        public string? MaterialParameter { get; set; }

        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }
    }

    public class DimensionSnapshot
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("label_parameter")]
        public string? LabelParameter { get; set; }

        [JsonPropertyName("value_mm")]
        public double? ValueMm { get; set; }

        [JsonPropertyName("is_locked")]
        public bool IsLocked { get; set; }
    }

    /// <summary>
    /// Authoritative snapshot of the active Revit Family document.
    /// </summary>
    public class FamilyState
    {
        [JsonPropertyName("is_family_document")]
        public bool IsFamilyDocument { get; set; } = false;

        [JsonPropertyName("family_name")]
        public string FamilyName { get; set; } = string.Empty;

        [JsonPropertyName("family_category")]
        public string FamilyCategory { get; set; } = string.Empty;

        [JsonPropertyName("current_type_name")]
        public string CurrentTypeName { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public List<ParameterSnapshot> Parameters { get; set; } = new();

        [JsonPropertyName("reference_planes")]
        public List<ReferencePlaneSnapshot> ReferencePlanes { get; set; } = new();

        [JsonPropertyName("solids")]
        public List<SolidSnapshot> Solids { get; set; } = new();

        [JsonPropertyName("dimensions")]
        public List<DimensionSnapshot> Dimensions { get; set; } = new();

        [JsonPropertyName("nested_families")]
        public List<string> NestedFamilies { get; set; } = new();

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}
