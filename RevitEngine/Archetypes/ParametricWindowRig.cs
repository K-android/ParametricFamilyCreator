using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic archetype rig for Architectural Windows.
    /// Constructs 4 distinct frame members (Left Jamb, Right Jamb, Sill, Head) + 1 Glazing panel,
    /// complete reference planes, host opening cuts, and labeled dimensions.
    /// </summary>
    public class ParametricWindowRig : IArchetypeRig
    {
        public string ArchetypeName => "ParametricWindow";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("window") || s.Contains("casement") || s.Contains("fenestration") || s.Contains("glazing unit");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 1200.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 1500.0);
            double frameW = spec.GetDim("Frame_Width", spec.GetDim("Frame Width", 75.0));
            double frameD = spec.GetDim("Frame_Depth", spec.GetDim("Frame Depth", 100.0));
            double glassThk = spec.GetDim("Glass_Thickness", spec.GetDim("Glass Thickness", 10.0));

            // Guard against negative or degenerate dimensions
            if (w < 200) w = 1200.0;
            if (h < 200) h = 1500.0;
            if (frameW < 15 || frameW * 2 >= w || frameW * 2 >= h) frameW = 75.0;
            if (frameD < 20) frameD = 100.0;
            if (glassThk < 3) glassThk = 10.0;

            double halfW = w / 2.0;

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"Window_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : "Parametric_Window";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Windows",
                TemplateName = "Metric Window.rft",
                Description = "Parametric rectangular window with 4 distinct frame members, central glazing panel, and wall opening.",
                Manufacturer = spec.Manufacturer ?? "Standard Architectural",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Frame/Mullion", "Glazing" }
            };

            // 1. Parameters
            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = h, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Frame_Width", DataType = "Length", DefaultValueMm = frameW, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Frame_Depth", DataType = "Length", DefaultValueMm = frameD, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Glass_Thickness", DataType = "Length", DefaultValueMm = glassThk, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Frame_Material", DataType = "Material", GroupType = "Materials" },
                new MasterParameterDef { Name = "Glass_Material", DataType = "Material", GroupType = "Materials" }
            };

            // 2. Reference Planes
            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left", Orientation = "X", OffsetFromOriginMm = -halfW, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right", Orientation = "X", OffsetFromOriginMm = halfW, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Frame_Left_Inner", Orientation = "X", OffsetFromOriginMm = -halfW + frameW },
                new ReferencePlaneDef { Name = "Frame_Right_Inner", Orientation = "X", OffsetFromOriginMm = halfW - frameW },
                new ReferencePlaneDef { Name = "Center_Front_Back", Orientation = "Y", OffsetFromOriginMm = 0.0 },
                new ReferencePlaneDef { Name = "Exterior", Orientation = "Y", OffsetFromOriginMm = frameD / 2.0, DrivenByParameter = "Frame_Depth" },
                new ReferencePlaneDef { Name = "Interior", Orientation = "Y", OffsetFromOriginMm = -frameD / 2.0, DrivenByParameter = "Frame_Depth" },
                new ReferencePlaneDef { Name = "Window_Head", Orientation = "Z", OffsetFromOriginMm = h, DrivenByParameter = "Height" },
                new ReferencePlaneDef { Name = "Frame_Head_Inner", Orientation = "Z", OffsetFromOriginMm = h - frameW },
                new ReferencePlaneDef { Name = "Frame_Sill_Inner", Orientation = "Z", OffsetFromOriginMm = frameW }
            };

            // 3. Wall Opening Cut
            schema.HostOpenings = new List<OpeningDef>
            {
                new OpeningDef { MinX_mm = -halfW, MaxX_mm = halfW, MinZ_mm = 0.0, MaxZ_mm = h }
            };

            // 4. Five Distinct 3D BRep Extrusions (4 Frame Members + 1 Glazing Panel)
            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // Member 1: Left Jamb
                new ExtrusionDef
                {
                    Name = "Window_Frame_Left",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -halfW,
                    MaxX_mm = -halfW + frameW,
                    MinY_mm = -frameD / 2.0,
                    MaxY_mm = frameD / 2.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = h,
                    Subcategory = "Frame/Mullion",
                    MaterialParameter = "Frame_Material"
                },

                // Member 2: Right Jamb
                new ExtrusionDef
                {
                    Name = "Window_Frame_Right",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = halfW - frameW,
                    MaxX_mm = halfW,
                    MinY_mm = -frameD / 2.0,
                    MaxY_mm = frameD / 2.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = h,
                    Subcategory = "Frame/Mullion",
                    MaterialParameter = "Frame_Material"
                },

                // Member 3: Bottom Sill Frame
                new ExtrusionDef
                {
                    Name = "Window_Frame_Bottom",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -halfW + frameW,
                    MaxX_mm = halfW - frameW,
                    MinY_mm = -frameD / 2.0,
                    MaxY_mm = frameD / 2.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = frameW,
                    Subcategory = "Frame/Mullion",
                    MaterialParameter = "Frame_Material"
                },

                // Member 4: Top Head Frame
                new ExtrusionDef
                {
                    Name = "Window_Frame_Top",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -halfW + frameW,
                    MaxX_mm = halfW - frameW,
                    MinY_mm = -frameD / 2.0,
                    MaxY_mm = frameD / 2.0,
                    StartZ_mm = h - frameW,
                    EndZ_mm = h,
                    Subcategory = "Frame/Mullion",
                    MaterialParameter = "Frame_Material"
                },

                // Member 5: Central Glazing Panel
                new ExtrusionDef
                {
                    Name = "Glazing_Panel",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -halfW + frameW,
                    MaxX_mm = halfW - frameW,
                    MinY_mm = -glassThk / 2.0,
                    MaxY_mm = glassThk / 2.0,
                    StartZ_mm = frameW,
                    EndZ_mm = h - frameW,
                    Subcategory = "Glazing",
                    MaterialParameter = "Glass_Material"
                }
            };

            // 5. 2D Symbolic Linework in Plan View
            schema.SymbolicCurves = new List<SymbolicCurveDef>
            {
                new SymbolicCurveDef
                {
                    CurveType = "Line",
                    StartX_mm = -halfW + frameW,
                    StartY_mm = 0.0,
                    EndX_mm = halfW - frameW,
                    EndY_mm = 0.0,
                    Subcategory = "Glazing"
                }
            };

            onLog?.Invoke($"[RIG: ParametricWindow] Built 5 geometric elements: 4 frame members (Left/Right/Top/Bottom) + Glazing panel (W={w}mm, H={h}mm, Frame={frameW}x{frameD}mm, Glass={glassThk}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 1200.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 1500.0);
            double frameW = spec.GetDim("Frame_Width", spec.GetDim("Frame Width", 75.0));

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Overall Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Overall Height", h, "Height", 2.0, DimensionDatums.Floor),
                new ArchetypeVerificationRule("Frame Width", frameW, "Frame_Width", 2.0, DimensionDatums.Centerline)
            };
        }
    }
}
