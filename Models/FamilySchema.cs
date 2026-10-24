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

        [JsonPropertyName("family_category")]
        public string FamilyCategory { get; set; } = "Generic Models";

        [JsonPropertyName("template_name")]
        public string TemplateName { get; set; } = "Metric Generic Model.rft";

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("manufacturer")]
        public string? Manufacturer { get; set; }

        [JsonPropertyName("master_parameters")]
        public List<MasterParameterDef> MasterParameters { get; set; } = new();

        /// <summary>
        /// Optional list of predefined family types (Type Catalog).
        /// When provided, each type is created with custom dimension & parameter overrides.
        /// </summary>
        [JsonPropertyName("family_types")]
        public List<FamilyTypeDef> FamilyTypes { get; set; } = new();

        [JsonPropertyName("reference_planes")]
        public List<ReferencePlaneDef> ReferencePlanes { get; set; } = new();

        [JsonPropertyName("host_extrusions")]
        public List<ExtrusionDef> HostExtrusions { get; set; } = new();

        [JsonPropertyName("nested_families")]
        public List<NestedFamilyDef> NestedFamilies { get; set; } = new();
    }

    /// <summary>
    /// Defines a Revit family type (variant) in the family's Type Catalog.
    /// </summary>
    public class FamilyTypeDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Standard";

        /// <summary>
        /// Parameter name to value mapping for this type.
        /// Values can be double (mm for length, degrees for angle), int, bool, or string.
        /// </summary>
        [JsonPropertyName("parameter_values")]
        public Dictionary<string, object> ParameterValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class MasterParameterDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Data type: "Length", "YesNo", "Material", "Angle", "Area", "Volume", "Integer", "Number", "Text", "Url"
        /// </summary>
        [JsonPropertyName("data_type")]
        public string DataType { get; set; } = "Length";

        [JsonPropertyName("default_value_mm")]
        public double DefaultValueMm { get; set; } = 0.0;

        /// <summary>
        /// General numeric default value (for Angle, Integer, Number, Area, Volume).
        /// </summary>
        [JsonPropertyName("default_value")]
        public double DefaultValue { get; set; } = 0.0;

        /// <summary>
        /// Default text string (for Text and Url parameters).
        /// </summary>
        [JsonPropertyName("default_text")]
        public string? DefaultText { get; set; }

        /// <summary>
        /// Target parameter group: "Dimensions", "Geometry", "Materials", "Visibility", "IdentityData", "Data", "General".
        /// </summary>
        [JsonPropertyName("group_type")]
        public string? GroupType { get; set; }

        [JsonPropertyName("is_instance")]
        public bool IsInstance { get; set; } = false;

        [JsonPropertyName("formula")]
        public string? Formula { get; set; }

        /// <summary>
        /// Gets the effective numeric value in mm or unit value.
        /// </summary>
        public double GetEffectiveNumericValue()
        {
            if (DefaultValueMm != 0.0) return DefaultValueMm;
            return DefaultValue;
        }
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

        /// <summary>
        /// Corner fillet radius in mm for "RoundedRectangle" or "Fillet" profile types.
        /// </summary>
        [JsonPropertyName("corner_radius_mm")]
        public double CornerRadius_mm { get; set; } = 0.0;

        /// <summary>
        /// When true, creates a void extrusion that cuts intersecting solid geometry
        /// (e.g. inner basin bowl cavity, tap holes, waste drain holes, door vision lites).
        /// </summary>
        [JsonPropertyName("is_void")]
        public bool IsVoid { get; set; } = false;

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

        /// <summary>
        /// Optional material parameter name to link to extrusion's Material property.
        /// </summary>
        [JsonPropertyName("material_parameter")]
        public string? MaterialParameter { get; set; }

        /// <summary>
        /// Optional Yes/No parameter name to link to extrusion's Visible property.
        /// </summary>
        [JsonPropertyName("visibility_parameter")]
        public string? VisibilityParameter { get; set; }
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
