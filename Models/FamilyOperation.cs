using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Supported atomic BIM operations executed deterministically by the Revit Family Copilot engine.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum FamilyOperationType
    {
        CREATE_PARAMETER,
        SET_PARAMETER,
        CREATE_REFERENCE_PLANE,
        CREATE_DIMENSION,
        CREATE_EXTRUSION,
        MODIFY_EXTRUSION,
        CREATE_VOID,
        MODIFY_VOID,
        CREATE_SWEEP,
        CREATE_BLEND,
        CREATE_FORMULA,
        ALIGN,
        LOCK_CONSTRAINT,
        SET_MATERIAL,
        DELETE_ELEMENT,
        CREATE_NESTED_FAMILY,
        INSPECT_FAMILY,
        VALIDATE_FAMILY,
        FLEX_PARAMETER,
        DIAGNOSE_CONSTRAINTS,
        REPAIR_FAMILY
    }

    public class SweepPathPointDef
    {
        [JsonPropertyName("u_expr")]
        public string? U_Expr { get; set; }

        [JsonPropertyName("v_expr")]
        public string? V_Expr { get; set; }

        [JsonPropertyName("u_mm")]
        public double? U_mm { get; set; }

        [JsonPropertyName("v_mm")]
        public double? V_mm { get; set; }
    }

    /// <summary>
    /// Strongly-typed operation model representing a single deterministic modification or creation step in Revit.
    /// Supports both literal millimeter bounds and parametric arithmetic string expressions (e.g. "-Width / 2 + Frame_Width").
    /// </summary>
    public class FamilyOperation
    {
        [JsonPropertyName("operation")]
        public FamilyOperationType Operation { get; set; } = FamilyOperationType.CREATE_PARAMETER;

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        [JsonPropertyName("summary")]
        public string? Summary { get; set; }

        // Parameter arguments
        [JsonPropertyName("parameter_name")]
        public string? ParameterName { get; set; }

        [JsonPropertyName("parameter_type")]
        public string? ParameterType { get; set; } = "Length"; // Length, YesNo, Material, Angle, Integer, Number, Text

        [JsonPropertyName("group_type")]
        public string? GroupType { get; set; } = "Dimensions";

        [JsonPropertyName("is_instance")]
        public bool IsInstance { get; set; } = false;

        [JsonPropertyName("value")]
        public double? Value { get; set; }

        [JsonPropertyName("text_value")]
        public string? TextValue { get; set; }

        [JsonPropertyName("unit")]
        public string? Unit { get; set; } = "mm";

        [JsonPropertyName("formula")]
        public string? Formula { get; set; }

        // Reference plane arguments
        [JsonPropertyName("plane_name")]
        public string? PlaneName { get; set; }

        [JsonPropertyName("orientation")]
        public string? Orientation { get; set; } = "X"; // X, Y, Z

        [JsonPropertyName("offset_mm")]
        public double? OffsetMm { get; set; }

        [JsonPropertyName("offset_expr")]
        public string? OffsetExpr { get; set; }

        [JsonPropertyName("driven_by_parameter")]
        public string? DrivenByParameter { get; set; }

        // Dimension and Alignment arguments
        [JsonPropertyName("plane_a")]
        public string? PlaneA { get; set; }

        [JsonPropertyName("plane_b")]
        public string? PlaneB { get; set; }

        [JsonPropertyName("target_ref")]
        public string? TargetRef { get; set; }

        [JsonPropertyName("geometry_face")]
        public string? GeometryFace { get; set; } // start, end, min_u, max_u, min_v, max_v

        [JsonPropertyName("is_locked")]
        public bool IsLocked { get; set; } = false;

        [JsonPropertyName("label_parameter")]
        public string? LabelParameter { get; set; }

        [JsonPropertyName("view_type")]
        public string? ViewType { get; set; } = "Plan"; // Plan, FrontElevation, SideElevation

        // Geometry extrusion/void/sweep/blend arguments
        [JsonPropertyName("element_name")]
        public string? ElementName { get; set; }

        [JsonPropertyName("sketch_plane")]
        public string? SketchPlane { get; set; } // Optional named ReferencePlane (e.g. "Left", "Right", "Front", "Back", "Top", "Bottom")

        [JsonPropertyName("profile_type")]
        public string? ProfileType { get; set; } = "Rectangle"; // Rectangle, Cylinder, Circle, HollowBox, RoundedRectangle

        [JsonPropertyName("is_void")]
        public bool IsVoid { get; set; } = false;

        [JsonPropertyName("min_x_mm")]
        public double? MinX_mm { get; set; }

        [JsonPropertyName("max_x_mm")]
        public double? MaxX_mm { get; set; }

        [JsonPropertyName("min_y_mm")]
        public double? MinY_mm { get; set; }

        [JsonPropertyName("max_y_mm")]
        public double? MaxY_mm { get; set; }

        [JsonPropertyName("start_z_mm")]
        public double? StartZ_mm { get; set; }

        [JsonPropertyName("end_z_mm")]
        public double? EndZ_mm { get; set; }

        // Parametric expression strings for world (X,Y,Z) or sketch-plane (u,v) coordinates
        [JsonPropertyName("min_x_expr")]
        public string? MinX_Expr { get; set; }

        [JsonPropertyName("max_x_expr")]
        public string? MaxX_Expr { get; set; }

        [JsonPropertyName("min_y_expr")]
        public string? MinY_Expr { get; set; }

        [JsonPropertyName("max_y_expr")]
        public string? MaxY_Expr { get; set; }

        [JsonPropertyName("start_z_expr")]
        public string? StartZ_Expr { get; set; }

        [JsonPropertyName("end_z_expr")]
        public string? EndZ_Expr { get; set; }

        // Sketch-plane (u, v) origin and dimension expressions (ArchSmarterFamFab convention)
        [JsonPropertyName("origin_u_expr")]
        public string? OriginU_Expr { get; set; }

        [JsonPropertyName("origin_v_expr")]
        public string? OriginV_Expr { get; set; }

        [JsonPropertyName("width_expr")]
        public string? Width_Expr { get; set; }

        [JsonPropertyName("height_expr")]
        public string? Height_Expr { get; set; }

        [JsonPropertyName("radius_mm")]
        public double? Radius_mm { get; set; }

        [JsonPropertyName("radius_expr")]
        public string? Radius_Expr { get; set; }

        [JsonPropertyName("top_radius_mm")]
        public double? TopRadius_mm { get; set; }

        [JsonPropertyName("top_radius_expr")]
        public string? TopRadius_Expr { get; set; }

        [JsonPropertyName("top_origin_u_expr")]
        public string? TopOriginU_Expr { get; set; }

        [JsonPropertyName("top_origin_v_expr")]
        public string? TopOriginV_Expr { get; set; }

        [JsonPropertyName("top_width_expr")]
        public string? TopWidth_Expr { get; set; }

        [JsonPropertyName("top_height_expr")]
        public string? TopHeight_Expr { get; set; }

        [JsonPropertyName("corner_radius_mm")]
        public double? CornerRadius_mm { get; set; }

        [JsonPropertyName("wall_thickness_mm")]
        public double? WallThickness_mm { get; set; }

        [JsonPropertyName("width_parameter")]
        public string? WidthParameter { get; set; }

        [JsonPropertyName("height_parameter")]
        public string? HeightParameter { get; set; }

        [JsonPropertyName("depth_parameter")]
        public string? DepthParameter { get; set; }

        [JsonPropertyName("material_parameter")]
        public string? MaterialParameter { get; set; }

        [JsonPropertyName("subcategory")]
        public string? Subcategory { get; set; }

        // Sweep path arguments
        [JsonPropertyName("sweep_path_points")]
        public List<SweepPathPointDef>? SweepPathPoints { get; set; }

        // Flex & diagnostics arguments
        [JsonPropertyName("flex_test_values")]
        public List<double>? FlexTestValues { get; set; }

        // Identification of existing target element
        [JsonPropertyName("target_element_id")]
        public int? TargetElementId { get; set; }

        [JsonPropertyName("target_solids")]
        public List<string>? TargetSolids { get; set; }
    }
}
