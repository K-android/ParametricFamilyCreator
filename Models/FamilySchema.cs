using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Root specification for procedural and AI-generated Revit parametric families.
    /// Deserialized from Gemini Structured JSON or deterministic rule engines.
    /// </summary>
    public class FamilySchema
    {
        [JsonPropertyName("master_family_name")]
        public string MasterFamilyName { get; set; } = "ParametricHostFamily";

        [JsonPropertyName("template_name")]
        public string TemplateName { get; set; } = "Metric Generic Model.rft";

        [JsonPropertyName("master_parameters")]
        public List<MasterParameterDef> MasterParameters { get; set; } = new();

        [JsonPropertyName("reference_planes")]
        public List<ReferencePlaneDef> ReferencePlanes { get; set; } = new();

        [JsonPropertyName("host_extrusions")]
        public List<ExtrusionDef> HostExtrusions { get; set; } = new();

        [JsonPropertyName("nested_families")]
        public List<NestedFamilyDef> NestedFamilies { get; set; } = new();
    }

    public class MasterParameterDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// "Length", "YesNo", or "Material"
        /// </summary>
        [JsonPropertyName("data_type")]
        public string DataType { get; set; } = "Length";

        [JsonPropertyName("default_value_mm")]
        public double DefaultValueMm { get; set; } = 0.0;

        [JsonPropertyName("is_instance")]
        public bool IsInstance { get; set; } = false;

        [JsonPropertyName("formula")]
        public string? Formula { get; set; }
    }

    public class ReferencePlaneDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// "X" (Left/Right parallel to Y), "Y" (Front/Back parallel to X), or "Z" (Elevation)
        /// </summary>
        [JsonPropertyName("orientation")]
        public string Orientation { get; set; } = "X";

        [JsonPropertyName("offset_from_origin_mm")]
        public double OffsetFromOriginMm { get; set; } = 0.0;

        [JsonPropertyName("driven_by_parameter")]
        public string? DrivenByParameter { get; set; }
    }

    public class ExtrusionDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "SolidExtrusion";

        /// <summary>
        /// "Rectangle", "Cylinder", or "HollowBox"
        /// </summary>
        [JsonPropertyName("profile_type")]
        public string ProfileType { get; set; } = "Rectangle";

        [JsonPropertyName("min_x_mm")]
        public double MinX_mm { get; set; }

        [JsonPropertyName("min_y_mm")]
        public double MinY_mm { get; set; }

        [JsonPropertyName("max_x_mm")]
        public double MaxX_mm { get; set; }

        [JsonPropertyName("max_y_mm")]
        public double MaxY_mm { get; set; }

        [JsonPropertyName("radius_mm")]
        public double Radius_mm { get; set; } = 0.0;

        [JsonPropertyName("wall_thickness_mm")]
        public double WallThickness_mm { get; set; } = 0.0;

        [JsonPropertyName("start_z_mm")]
        public double StartZ_mm { get; set; } = 0.0;

        [JsonPropertyName("end_z_mm")]
        public double EndZ_mm { get; set; } = 50.0;

        [JsonPropertyName("height_parameter")]
        public string? HeightParameter { get; set; }

        [JsonPropertyName("width_parameter")]
        public string? WidthParameter { get; set; }

        [JsonPropertyName("depth_parameter")]
        public string? DepthParameter { get; set; }
    }

    public class NestedFamilyDef
    {
        [JsonPropertyName("nested_family_name")]
        public string NestedFamilyName { get; set; } = "SubComponent";

        [JsonPropertyName("template_name")]
        public string TemplateName { get; set; } = "Metric Generic Model.rft";

        /// <summary>
        /// When true, the nested component can be scheduled and tagged independently in Revit projects.
        /// </summary>
        [JsonPropertyName("is_shared")]
        public bool IsShared { get; set; } = false;

        /// <summary>
        /// Optional master Yes/No parameter name to control instance visibility (e.g. 'Show_Lever_Handle').
        /// </summary>
        [JsonPropertyName("visibility_parameter")]
        public string? VisibilityParameter { get; set; }

        [JsonPropertyName("sub_parameters")]
        public List<MasterParameterDef> SubParameters { get; set; } = new();

        [JsonPropertyName("sub_extrusions")]
        public List<ExtrusionDef> SubExtrusions { get; set; } = new();

        [JsonPropertyName("placement_offset_mm")]
        public VectorOffsetDef PlacementOffsetMm { get; set; } = new();

        /// <summary>
        /// Key = Nested Sub-Parameter Name, Value = Master Parameter Name
        /// </summary>
        [JsonPropertyName("parameter_bindings")]
        public Dictionary<string, string> ParameterBindings { get; set; } = new();
    }

    public class VectorOffsetDef
    {
        [JsonPropertyName("x")]
        public double X { get; set; } = 0.0;

        [JsonPropertyName("y")]
        public double Y { get; set; } = 0.0;

        [JsonPropertyName("z")]
        public double Z { get; set; } = 0.0;

        public VectorOffsetDef() { }

        public VectorOffsetDef(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }
}
