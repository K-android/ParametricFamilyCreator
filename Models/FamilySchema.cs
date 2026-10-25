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

        /// <summary>
        /// Optional Revit Subcategories to organize geometry with independent line weights and visibility.
        /// </summary>
        [JsonPropertyName("subcategories")]
        public List<string> Subcategories { get; set; } = new();

        [JsonPropertyName("reference_planes")]
        public List<ReferencePlaneDef> ReferencePlanes { get; set; } = new();

        [JsonPropertyName("host_extrusions")]
        public List<ExtrusionDef> HostExtrusions { get; set; } = new();

        /// <summary>
        /// Host Blends transitioning smoothly between top and bottom profiles (tapered pedestals, cones, pyramids).
        /// </summary>
        [JsonPropertyName("host_blends")]
        public List<BlendDef> HostBlends { get; set; } = new();

        /// <summary>
        /// Host Revolutions rotating a 2D profile around a central axis (round bowls, dished basin cavities, bottle traps, circular columns).
        /// </summary>
        [JsonPropertyName("host_revolves")]
        public List<RevolveDef> HostRevolves { get; set; } = new();

        /// <summary>
        /// Host Sweeps extruding a profile along a 3D path (curved spouts, grab bars, moldings, trims).
        /// </summary>
        [JsonPropertyName("host_sweeps")]
        public List<SweepDef> HostSweeps { get; set; } = new();

        /// <summary>
        /// MEP System Connectors (Pipes, Ducts, Electrical) for MEP building engineering coordination.
        /// </summary>
        [JsonPropertyName("mep_connectors")]
        public List<MepConnectorDef> MepConnectors { get; set; } = new();

        /// <summary>
        /// 2D Symbolic Linework visible in Floor Plan / RCP / Elevation views (e.g. 90 deg door swings, schematic outlines).
        /// </summary>
        [JsonPropertyName("symbolic_curves")]
        public List<SymbolicCurveDef> SymbolicCurves { get; set; } = new();

        /// <summary>
        /// Wall / Ceiling Opening cuts for hosted families (e.g. rough door openings, window penetrations).
        /// </summary>
        [JsonPropertyName("host_openings")]
        public List<OpeningDef> HostOpenings { get; set; } = new();

        [JsonPropertyName("nested_families")]
        public List<NestedFamilyDef> NestedFamilies { get; set; } = new();

        /// <summary>
        /// Optional strongly-typed cut-sheet specification with datums and confidence scores.
        /// When present, drives deterministic archetype builders and Revit BRep verification.
        /// </summary>
        [JsonPropertyName("cut_sheet_spec")]
        public ExtractedCutSheetSpec? CutSheetSpec { get; set; }
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

        /// <summary>
        /// Optional Revit Subcategory name (e.g. "Bowl", "Pedestal", "Hardware", "Glazing").
        /// </summary>
        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }

        /// <summary>
        /// Controls whether the 3D extrusion is visible in Floor Plan and Ref. Level views.
        /// Set to false when 2D symbolic linework is used to represent the component in plan.
        /// </summary>
        [JsonPropertyName("visible_in_plan")]
        public bool VisibleInPlan { get; set; } = true;

        /// <summary>
        /// Controls whether the 3D extrusion is visible in coarse detail levels.
        /// </summary>
        [JsonPropertyName("visible_in_coarse")]
        public bool VisibleInCoarse { get; set; } = true;
    }

    /// <summary>
    /// Defines a 3D blend between a bottom and top profile (e.g., tapering pedestals, cones, truncated pyramids).
    /// </summary>
    public class BlendDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "SolidBlend";

        [JsonPropertyName("is_void")]
        public bool IsVoid { get; set; } = false;

        /// <summary>
        /// Profile shape: "Rectangle", "Cylinder", "Circle", "Oval", "DShape", "RoundedRectangle".
        /// </summary>
        [JsonPropertyName("bottom_profile_type")]
        public string BottomProfileType { get; set; } = "Rectangle";

        [JsonPropertyName("top_profile_type")]
        public string TopProfileType { get; set; } = "Rectangle";

        [JsonPropertyName("bottom_min_x_mm")]
        public double BottomMinX_mm { get; set; }

        [JsonPropertyName("bottom_min_y_mm")]
        public double BottomMinY_mm { get; set; }

        [JsonPropertyName("bottom_max_x_mm")]
        public double BottomMaxX_mm { get; set; }

        [JsonPropertyName("bottom_max_y_mm")]
        public double BottomMaxY_mm { get; set; }

        [JsonPropertyName("bottom_radius_mm")]
        public double BottomRadius_mm { get; set; } = 0.0;

        [JsonPropertyName("top_min_x_mm")]
        public double TopMinX_mm { get; set; }

        [JsonPropertyName("top_min_y_mm")]
        public double TopMinY_mm { get; set; }

        [JsonPropertyName("top_max_x_mm")]
        public double TopMaxX_mm { get; set; }

        [JsonPropertyName("top_max_y_mm")]
        public double TopMaxY_mm { get; set; }

        [JsonPropertyName("top_radius_mm")]
        public double TopRadius_mm { get; set; } = 0.0;

        [JsonPropertyName("bottom_offset_mm")]
        public double BottomOffset_mm { get; set; } = 0.0;

        [JsonPropertyName("top_offset_mm")]
        public double TopOffset_mm { get; set; } = 100.0;

        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }

        [JsonPropertyName("material_parameter")]
        public string? MaterialParameter { get; set; }

        [JsonPropertyName("visibility_parameter")]
        public string? VisibilityParameter { get; set; }
    }

    /// <summary>
    /// Defines a 3D revolution rotating a 2D profile around a central axis line.
    /// Ideal for organic bowls, dished basin cavity voids, circular pedestals, bottle traps, and round waste flanges.
    /// </summary>
    public class RevolveDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "SolidRevolve";

        [JsonPropertyName("is_void")]
        public bool IsVoid { get; set; } = false;

        /// <summary>
        /// "BowlDish", "Cylinder", "Cone", "HalfSphere", or "Custom"
        /// </summary>
        [JsonPropertyName("revolve_type")]
        public string RevolveType { get; set; } = "BowlDish";

        [JsonPropertyName("axis_x_mm")]
        public double AxisX_mm { get; set; } = 0.0;

        [JsonPropertyName("axis_y_mm")]
        public double AxisY_mm { get; set; } = 0.0;

        [JsonPropertyName("start_z_mm")]
        public double StartZ_mm { get; set; } = 700.0;

        [JsonPropertyName("end_z_mm")]
        public double EndZ_mm { get; set; } = 850.0;

        [JsonPropertyName("radius_mm")]
        public double Radius_mm { get; set; } = 200.0;

        [JsonPropertyName("inner_radius_mm")]
        public double InnerRadius_mm { get; set; } = 0.0;

        [JsonPropertyName("start_angle_deg")]
        public double StartAngleDeg { get; set; } = 0.0;

        [JsonPropertyName("end_angle_deg")]
        public double EndAngleDeg { get; set; } = 360.0;

        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }

        [JsonPropertyName("material_parameter")]
        public string? MaterialParameter { get; set; }

        [JsonPropertyName("visibility_parameter")]
        public string? VisibilityParameter { get; set; }
    }

    /// <summary>
    /// Defines a 3D sweep that extrudes a profile along a 3D path curve (curved spouts, towel rails, moldings, trims).
    /// </summary>
    public class SweepDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "SolidSweep";

        [JsonPropertyName("is_void")]
        public bool IsVoid { get; set; } = false;

        /// <summary>
        /// "Circle" or "Rectangle"
        /// </summary>
        [JsonPropertyName("profile_type")]
        public string ProfileType { get; set; } = "Circle";

        [JsonPropertyName("profile_radius_mm")]
        public double ProfileRadius_mm { get; set; } = 15.0;

        [JsonPropertyName("profile_width_mm")]
        public double ProfileWidth_mm { get; set; } = 30.0;

        [JsonPropertyName("profile_height_mm")]
        public double ProfileHeight_mm { get; set; } = 30.0;

        /// <summary>
        /// Ordered sequence of 3D points defining the path segments (in mm).
        /// </summary>
        [JsonPropertyName("path_points_mm")]
        public List<VectorOffsetDef> PathPoints_mm { get; set; } = new();

        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }

        [JsonPropertyName("material_parameter")]
        public string? MaterialParameter { get; set; }

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

    /// <summary>
    /// Defines an MEP system connector (Pipe, Duct, Electrical) for engineering coordination.
    /// </summary>
    public class MepConnectorDef
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "Connector";

        /// <summary>
        /// "Pipe", "Duct", "Electrical", "Conduit"
        /// </summary>
        [JsonPropertyName("domain")]
        public string Domain { get; set; } = "Pipe";

        /// <summary>
        /// For Pipe: "DomesticColdWater", "DomesticHotWater", "Sanitary", "HydronicSupply", "HydronicReturn", "FireProtectWet", "Other"
        /// For Duct: "SupplyAir", "ReturnAir", "ExhaustAir", "Other"
        /// For Electrical: "PowerUnbalanced", "PowerBalanced", "Lighting", "Data"
        /// </summary>
        [JsonPropertyName("system_type")]
        public string SystemType { get; set; } = "DomesticColdWater";

        /// <summary>
        /// Diameter in millimeters (e.g. 15mm cold water, 40mm sanitary drain).
        /// </summary>
        [JsonPropertyName("diameter_mm")]
        public double Diameter_mm { get; set; } = 15.0;

        /// <summary>
        /// Spatial 3D attachment point in mm.
        /// </summary>
        [JsonPropertyName("origin_offset_mm")]
        public VectorOffsetDef OriginOffsetMm { get; set; } = new();

        /// <summary>
        /// Flow direction or normal vector ("Z", "-Z", "Y", "-Y", "X", "-X").
        /// </summary>
        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "-Z";
    }

    /// <summary>
    /// Defines 2D symbolic linework visible in Floor Plan / RCP / Elevation views (e.g., door swings, sink outlines).
    /// </summary>
    public class SymbolicCurveDef
    {
        [JsonPropertyName("curve_type")]
        public string CurveType { get; set; } = "Line"; // "Line" or "Arc"

        [JsonPropertyName("start_x_mm")]
        public double StartX_mm { get; set; }

        [JsonPropertyName("start_y_mm")]
        public double StartY_mm { get; set; }

        [JsonPropertyName("end_x_mm")]
        public double EndX_mm { get; set; }

        [JsonPropertyName("end_y_mm")]
        public double EndY_mm { get; set; }

        [JsonPropertyName("center_x_mm")]
        public double CenterX_mm { get; set; }

        [JsonPropertyName("center_y_mm")]
        public double CenterY_mm { get; set; }

        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }
    }

    /// <summary>
    /// Defines a wall or ceiling opening cut for host-based families (Doors, Windows).
    /// </summary>
    public class OpeningDef
    {
        [JsonPropertyName("min_x_mm")]
        public double MinX_mm { get; set; }

        [JsonPropertyName("max_x_mm")]
        public double MaxX_mm { get; set; }

        [JsonPropertyName("min_z_mm")]
        public double MinZ_mm { get; set; } = 0.0;

        [JsonPropertyName("max_z_mm")]
        public double MaxZ_mm { get; set; } = 2100.0;
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
