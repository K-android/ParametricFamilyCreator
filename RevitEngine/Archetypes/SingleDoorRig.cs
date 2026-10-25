using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic rig for Architectural Single Doors (Wall-Hosted or Standalone).
    /// Constructs frame jambs/head, door leaf panel, glazed vision lite, nested lever handle,
    /// and 2D symbolic plan swing arc.
    /// </summary>
    public class SingleDoorRig : IArchetypeRig
    {
        public string ArchetypeName => "SingleDoor";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("door") || s.Contains("single door") || s.Contains("leaf door");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 915.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 2135.0);
            double t = spec.GetDim("Thickness", 45.0);
            double frameW = spec.GetDim("Frame_Width", 50.0);
            double frameD = spec.GetDim("Frame_Depth", 100.0);

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"Door_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : "SingleDoor_Parametric";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Doors",
                TemplateName = "Metric Generic Model.rft",
                Description = "High-precision architectural single door with frame, leaf, glazing, and 2D swing.",
                Manufacturer = spec.Manufacturer ?? "Standard Architectural",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Frame/Mullion", "Panel", "Glazing", "Plan Swing" }
            };

            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = h, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Thickness", DataType = "Length", DefaultValueMm = t, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Frame_Width", DataType = "Length", DefaultValueMm = frameW, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Frame_Depth", DataType = "Length", DefaultValueMm = frameD, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Door_Material", DataType = "Material", GroupType = "Materials" },
                new MasterParameterDef { Name = "Frame_Material", DataType = "Material", GroupType = "Materials" }
            };

            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Jamb", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right_Jamb", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Center_Wall", Orientation = "Y", OffsetFromOriginMm = 0.0 },
                new ReferencePlaneDef { Name = "Header_Plane", Orientation = "Z", OffsetFromOriginMm = h, DrivenByParameter = "Height" }
            };

            // Extrusions: Frame + Door Leaf + Vision Lite Void + Glazing Panel
            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // Frame Jambs & Head
                new ExtrusionDef
                {
                    Name = "Door_Frame",
                    ProfileType = "HollowBox",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 - frameW,
                    MaxX_mm = w / 2.0 + frameW,
                    MinY_mm = -frameD / 2.0,
                    MaxY_mm = frameD / 2.0,
                    WallThickness_mm = frameW,
                    StartZ_mm = 0.0,
                    EndZ_mm = h + frameW,
                    MaterialParameter = "Frame_Material",
                    Subcategory = "Frame/Mullion"
                },
                // Door Leaf
                new ExtrusionDef
                {
                    Name = "Door_Leaf",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -t / 2.0,
                    MaxY_mm = t / 2.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = h,
                    WidthParameter = "Width",
                    HeightParameter = "Height",
                    MaterialParameter = "Door_Material",
                    Subcategory = "Panel",
                    VisibleInPlan = false
                },
                // Vision Lite Cutout Void
                new ExtrusionDef
                {
                    Name = "Vision_Lite_Cutout",
                    ProfileType = "RoundedRectangle",
                    CornerRadius_mm = 25.0,
                    IsVoid = true,
                    MinX_mm = -150.0,
                    MaxX_mm = 150.0,
                    MinY_mm = -t / 2.0 - 5.0,
                    MaxY_mm = t / 2.0 + 5.0,
                    StartZ_mm = 1000.0,
                    EndZ_mm = 1700.0,
                    Subcategory = "Panel"
                },
                // Vision Lite Glass Panel
                new ExtrusionDef
                {
                    Name = "Vision_Lite_Glass",
                    ProfileType = "RoundedRectangle",
                    CornerRadius_mm = 20.0,
                    IsVoid = false,
                    MinX_mm = -145.0,
                    MaxX_mm = 145.0,
                    MinY_mm = -3.0,
                    MaxY_mm = 3.0,
                    StartZ_mm = 1005.0,
                    EndZ_mm = 1695.0,
                    Subcategory = "Glazing"
                }
            };

            // 2D Symbolic Plan Swing Linework
            schema.SymbolicCurves = new List<SymbolicCurveDef>
            {
                // Door leaf in open 90 degree plan position
                new SymbolicCurveDef
                {
                    CurveType = "Line",
                    StartX_mm = -w / 2.0,
                    StartY_mm = 0.0,
                    EndX_mm = -w / 2.0,
                    EndY_mm = -w,
                    Subcategory = "Plan Swing"
                },
                // 90 degree plan swing quarter arc
                new SymbolicCurveDef
                {
                    CurveType = "Arc",
                    StartX_mm = w / 2.0,
                    StartY_mm = 0.0,
                    EndX_mm = -w / 2.0,
                    EndY_mm = -w,
                    CenterX_mm = -w / 2.0,
                    CenterY_mm = 0.0,
                    Subcategory = "Plan Swing"
                }
            };

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Width={w}mm, Height={h}mm, LeafThk={t}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 915.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 2135.0);

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Clear Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Clear Height", h, "Height", 2.0, DimensionDatums.Floor)
            };
        }
    }
}
