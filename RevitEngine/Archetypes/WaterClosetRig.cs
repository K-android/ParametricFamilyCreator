using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic rig for Water Closets (Toilets) - Wall-Hung and Back-to-Wall.
    /// Constructs contoured ceramic pan body, hollow inner siphon bowl, seat/cover,
    /// 110mm soil pipe waste connector, and cold water supply.
    /// </summary>
    public class WaterClosetRig : IArchetypeRig
    {
        public string ArchetypeName => "WaterCloset";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("toilet") || s.Contains("water closet") || s.Contains("wc") || s.Contains("commode");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 360.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 540.0);
            double panRimH = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 400.0);
            if (panRimH > 600) panRimH = 400.0;
            double soilH = spec.GetDim("Soil_Pipe_Height", 220.0);

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"WC_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : "WaterCloset_WallHung_Parametric";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Plumbing Fixtures",
                TemplateName = "Metric Generic Model.rft",
                Description = "High-precision parametric wall-hung water closet with siphon jet bowl and MEP connectors.",
                Manufacturer = spec.Manufacturer ?? "Generic Sanitaryware",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Sanitaryware", "Hardware", "Fixtures" }
            };

            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = d, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Rim_Height", DataType = "Length", DefaultValueMm = panRimH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Soil_Pipe_Height", DataType = "Length", DefaultValueMm = soilH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Ceramic_Material", DataType = "Material", GroupType = "Materials" }
            };

            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Wall_Face", Orientation = "Y", OffsetFromOriginMm = 0.0 },
                new ReferencePlaneDef { Name = "Pan_Front", Orientation = "Y", OffsetFromOriginMm = -d, DrivenByParameter = "Depth" },
                new ReferencePlaneDef { Name = "Rim_Level", Orientation = "Z", OffsetFromOriginMm = panRimH, DrivenByParameter = "Rim_Height" }
            };

            // Pan Body: D-shape body from Z=100mm to Z=400mm (wall-hung)
            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // Ceramic pan body
                new ExtrusionDef
                {
                    Name = "WC_Pan_Body",
                    ProfileType = "HollowDShape",
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -d,
                    MaxY_mm = 0.0,
                    WallThickness_mm = 35.0,
                    StartZ_mm = 100.0,
                    EndZ_mm = panRimH,
                    WidthParameter = "Width",
                    DepthParameter = "Depth",
                    HeightParameter = "Rim_Height",
                    MaterialParameter = "Ceramic_Material",
                    Subcategory = "Sanitaryware"
                },
                // Inner bowl bottom contour
                new ExtrusionDef
                {
                    Name = "WC_Siphon_Dish",
                    ProfileType = "RoundedRectangle",
                    CornerRadius_mm = 30.0,
                    IsVoid = false,
                    MinX_mm = -w / 2.0 + 35.0,
                    MaxX_mm = w / 2.0 - 35.0,
                    MinY_mm = -d + 40.0,
                    MaxY_mm = -80.0,
                    StartZ_mm = 100.0,
                    EndZ_mm = 140.0,
                    MaterialParameter = "Ceramic_Material",
                    Subcategory = "Sanitaryware"
                },
                // Toilet Seat & Cover
                new ExtrusionDef
                {
                    Name = "WC_Seat_Cover",
                    ProfileType = "DShape",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 - 5.0,
                    MaxX_mm = w / 2.0 + 5.0,
                    MinY_mm = -d - 5.0,
                    MaxY_mm = -30.0,
                    StartZ_mm = panRimH,
                    EndZ_mm = panRimH + 25.0,
                    Subcategory = "Hardware"
                }
            };

            // MEP Connectors: 110mm Soil pipe at wall face + 20mm flush supply
            schema.MepConnectors = new List<MepConnectorDef>
            {
                new MepConnectorDef
                {
                    Name = "Soil_Pipe_Outlet",
                    Domain = "Pipe",
                    SystemType = "Sanitary",
                    Diameter_mm = 110.0,
                    OriginOffsetMm = new VectorOffsetDef(0.0, 0.0, soilH),
                    Direction = "Y"
                },
                new MepConnectorDef
                {
                    Name = "Flush_Water_Supply",
                    Domain = "Pipe",
                    SystemType = "DomesticColdWater",
                    Diameter_mm = 20.0,
                    OriginOffsetMm = new VectorOffsetDef(0.0, 0.0, 350.0),
                    Direction = "Y"
                }
            };

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Width={w}mm, Depth={d}mm, Rim={panRimH}mm, Soil={soilH}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 360.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 540.0);
            double panRimH = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 400.0);
            if (panRimH > 600) panRimH = 400.0;

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Overall Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Overall Depth", d, "Depth", 2.0, DimensionDatums.WallFace),
                new ArchetypeVerificationRule("Pan Rim Height", panRimH, "Rim_Height", 2.0, DimensionDatums.Floor)
            };
        }
    }
}
