using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic rig for Base & Wall Casework Cabinets.
    /// Constructs parametric carcass box, countertop slab, recessed toe kick, drawer/door fronts.
    /// </summary>
    public class CaseworkCabinetRig : IArchetypeRig
    {
        public string ArchetypeName => "CaseworkCabinet";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("cabinet") || s.Contains("casework") || s.Contains("vanity unit") || s.Contains("credenza");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 900.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 600.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 860.0);
            double kickH = spec.GetDim("Toe_Kick_Height", 100.0);
            double kickD = spec.GetDim("Toe_Kick_Depth", 75.0);
            double topThk = spec.GetDim("Countertop_Thickness", 30.0);

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"Cabinet_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : "Casework_BaseCabinet_Parametric";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Casework",
                TemplateName = "Metric Generic Model.rft",
                Description = "High-precision parametric casework base cabinet with toe kick, carcass, and countertop.",
                Manufacturer = spec.Manufacturer ?? "Standard Millwork",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Carcass", "Countertop", "Panels", "Hardware" }
            };

            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = d, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = h, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Countertop_Thickness", DataType = "Length", DefaultValueMm = topThk, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Carcass_Material", DataType = "Material", GroupType = "Materials" },
                new MasterParameterDef { Name = "Countertop_Material", DataType = "Material", GroupType = "Materials" }
            };

            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Back_Wall", Orientation = "Y", OffsetFromOriginMm = 0.0 },
                new ReferencePlaneDef { Name = "Front_Edge", Orientation = "Y", OffsetFromOriginMm = -d, DrivenByParameter = "Depth" },
                new ReferencePlaneDef { Name = "Countertop_Level", Orientation = "Z", OffsetFromOriginMm = h, DrivenByParameter = "Height" }
            };

            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // Toe kick base plinth
                new ExtrusionDef
                {
                    Name = "Cabinet_Toe_Kick",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 + 25.0,
                    MaxX_mm = w / 2.0 - 25.0,
                    MinY_mm = -d + kickD,
                    MaxY_mm = 0.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = kickH,
                    Subcategory = "Carcass"
                },
                // Main Carcass Body
                new ExtrusionDef
                {
                    Name = "Cabinet_Carcass",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -d + 20.0,
                    MaxY_mm = 0.0,
                    StartZ_mm = kickH,
                    EndZ_mm = h - topThk,
                    WidthParameter = "Width",
                    MaterialParameter = "Carcass_Material",
                    Subcategory = "Carcass"
                },
                // Door Front Panels
                new ExtrusionDef
                {
                    Name = "Cabinet_Door_Panels",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 + 3.0,
                    MaxX_mm = w / 2.0 - 3.0,
                    MinY_mm = -d,
                    MaxY_mm = -d + 19.0,
                    StartZ_mm = kickH + 5.0,
                    EndZ_mm = h - topThk - 5.0,
                    MaterialParameter = "Carcass_Material",
                    Subcategory = "Panels"
                },
                // Countertop Slab
                new ExtrusionDef
                {
                    Name = "Countertop_Slab",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 - 10.0,
                    MaxX_mm = w / 2.0 + 10.0,
                    MinY_mm = -d - 20.0,
                    MaxY_mm = 0.0,
                    StartZ_mm = h - topThk,
                    EndZ_mm = h,
                    MaterialParameter = "Countertop_Material",
                    Subcategory = "Countertop"
                }
            };

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Width={w}mm, Depth={d}mm, Height={h}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 900.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 600.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 860.0);

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Overall Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Overall Depth", d, "Depth", 2.0, DimensionDatums.WallFace),
                new ArchetypeVerificationRule("Overall Height", h, "Height", 2.0, DimensionDatums.Floor)
            };
        }
    }
}
