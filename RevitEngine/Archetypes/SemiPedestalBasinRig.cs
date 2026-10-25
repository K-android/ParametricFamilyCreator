using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic kinematic rig for Wall-Hung & Semi-Pedestal Washbasins.
    /// Eliminates LLM math hallucinations by procedurally constructing:
    /// - Monolithic 178mm ceramic body with concentric hollow loops (outer D-shape rim + inner bowl cavity + rear tap deck).
    /// - Watertight solid dish floor with 46mm waste drain cutout.
    /// - Overlapping semi-pedestal shroud blend anchored to wall datum (Y = 0).
    /// - Precision 35mm tap hole and 24mm overflow hole voids.
    /// - Grounded MEP connectors (Sanitary waste at Z=540mm, Cold/Hot water supply at Z=580mm).
    /// </summary>
    public class SemiPedestalBasinRig : IArchetypeRig
    {
        public string ArchetypeName => "SemiPedestalBasin";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("pedestal") || s.Contains("semi-pedestal") || s.Contains("semi pedestal") ||
                   s.Contains("wall-hung") || s.Contains("wall hung") || s.Contains("basin") || s.Contains("lavatory") ||
                   s.Contains("washbasin");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 550.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 420.0);
            double rimH = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 850.0);
            if (rimH < 500) rimH = 850.0;

            double bodyH = spec.GetDim("Basin_Body_Height", spec.GetDim("Body_Height", 178.0));
            double bowlCavityDepth = spec.GetDim("Bowl_Depth", 143.0);
            double pedestalH = spec.GetDim("Pedestal_Height", 320.0);
            double wasteH = spec.GetDim("Waste_Height", 540.0);

            double bodyBottomZ = rimH - bodyH;
            double dishFloorZ = bodyBottomZ + 35.0; // 35mm floor thickness
            double pedBottomZ = bodyBottomZ - pedestalH + 20.0; // 20mm overlap inside basin

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode) 
                ? $"Basin_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : (!string.IsNullOrWhiteSpace(spec.ModelSeries) 
                    ? $"Basin_{spec.ModelSeries}".Replace(" ", "_") 
                    : "SemiPedestalBasin_Parametric");

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Plumbing Fixtures",
                TemplateName = "Metric Generic Model.rft",
                Description = $"High-precision parametric semi-pedestal washbasin. Manufacturer: {spec.Manufacturer ?? "Standard"}, Series: {spec.ModelSeries ?? "Standard"}.",
                Manufacturer = spec.Manufacturer ?? "Generic Sanitaryware",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Basin", "Pedestal", "Fixtures", "Glazing" }
            };

            // 1. Master Parameters
            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = d, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Basin_Rim_Height", DataType = "Length", DefaultValueMm = rimH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Basin_Body_Height", DataType = "Length", DefaultValueMm = bodyH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Bowl_Depth", DataType = "Length", DefaultValueMm = bowlCavityDepth, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Pedestal_Height", DataType = "Length", DefaultValueMm = pedestalH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Waste_Centerline_Height", DataType = "Length", DefaultValueMm = wasteH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Tap_Hole_Diameter", DataType = "Length", DefaultValueMm = 35.0, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Drain_Hole_Diameter", DataType = "Length", DefaultValueMm = 46.0, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Ceramic_Material", DataType = "Material", GroupType = "Materials" },
                new MasterParameterDef { Name = "Pedestal_Visible", DataType = "YesNo", DefaultValue = 1.0, GroupType = "Visibility" }
            };

            // 2. Reference Planes
            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Wall_Face", Orientation = "Y", OffsetFromOriginMm = 0.0 },
                new ReferencePlaneDef { Name = "Front_Edge", Orientation = "Y", OffsetFromOriginMm = -d, DrivenByParameter = "Depth" },
                new ReferencePlaneDef { Name = "Rim_Level", Orientation = "Z", OffsetFromOriginMm = rimH, DrivenByParameter = "Basin_Rim_Height" },
                new ReferencePlaneDef { Name = "Basin_Body_Bottom", Orientation = "Z", OffsetFromOriginMm = bodyBottomZ },
                new ReferencePlaneDef { Name = "Waste_Centerline", Orientation = "Z", OffsetFromOriginMm = wasteH, DrivenByParameter = "Waste_Centerline_Height" },
                new ReferencePlaneDef { Name = "Pedestal_Bottom", Orientation = "Z", OffsetFromOriginMm = pedBottomZ }
            };

            // 3. Extrusions
            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // 3a. Monolithic Ceramic Body with concentric hollow loops
                new ExtrusionDef
                {
                    Name = "Basin_Main_Ceramic_Body",
                    ProfileType = "HollowDShape",
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -d,
                    MaxY_mm = 0.0,
                    WallThickness_mm = 30.0,
                    StartZ_mm = bodyBottomZ,
                    EndZ_mm = rimH,
                    WidthParameter = "Width",
                    DepthParameter = "Depth",
                    HeightParameter = "Basin_Body_Height",
                    MaterialParameter = "Ceramic_Material",
                    Subcategory = "Basin"
                },

                // 3b. Solid Dish Floor enclosing the bottom of the bowl
                new ExtrusionDef
                {
                    Name = "Basin_Bowl_Dish_Floor",
                    ProfileType = "DShape",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 + 30.0,
                    MaxX_mm = w / 2.0 - 30.0,
                    MinY_mm = -d + 30.0,
                    MaxY_mm = -85.0,
                    StartZ_mm = bodyBottomZ,
                    EndZ_mm = dishFloorZ,
                    MaterialParameter = "Ceramic_Material",
                    Subcategory = "Basin"
                },

                // 3c. Tap Hole Void Cutout (35mm diameter)
                new ExtrusionDef
                {
                    Name = "Basin_Tap_Hole_Void",
                    ProfileType = "Cylinder",
                    IsVoid = true,
                    Radius_mm = 17.5,
                    MinX_mm = -17.5,
                    MaxX_mm = 17.5,
                    MinY_mm = -55.0 - 17.5,
                    MaxY_mm = -55.0 + 17.5,
                    StartZ_mm = bodyBottomZ,
                    EndZ_mm = rimH + 10.0,
                    Subcategory = "Basin"
                },

                // 3d. Waste Drain Cutout (46mm diameter)
                new ExtrusionDef
                {
                    Name = "Basin_Waste_Drain_Void",
                    ProfileType = "Cylinder",
                    IsVoid = true,
                    Radius_mm = 23.0,
                    MinX_mm = -23.0,
                    MaxX_mm = 23.0,
                    MinY_mm = -d / 2.0 - 23.0,
                    MaxY_mm = -d / 2.0 + 23.0,
                    StartZ_mm = bodyBottomZ - 5.0,
                    EndZ_mm = dishFloorZ + 10.0,
                    Subcategory = "Basin"
                },

                // 3e. Overflow Hole Void (24mm diameter on rear bowl wall)
                new ExtrusionDef
                {
                    Name = "Basin_Overflow_Hole_Void",
                    ProfileType = "Cylinder",
                    IsVoid = true,
                    Radius_mm = 12.0,
                    MinX_mm = -12.0,
                    MaxX_mm = 12.0,
                    MinY_mm = -85.0 - 30.0,
                    MaxY_mm = -85.0 + 10.0,
                    StartZ_mm = rimH - 60.0,
                    EndZ_mm = rimH - 36.0,
                    Subcategory = "Basin"
                }
            };

            // 4. Overlapping Semi-Pedestal Shroud Blend
            double pedTopW = Math.Min(240.0, w * 0.42);
            double pedTopD = Math.Min(220.0, d * 0.52);
            double pedBotW = pedTopW * 0.72;
            double pedBotD = pedTopD * 0.70;

            schema.HostBlends = new List<BlendDef>
            {
                new BlendDef
                {
                    Name = "Semi_Pedestal_Shroud",
                    IsVoid = false,
                    BottomProfileType = "DShape",
                    TopProfileType = "DShape",
                    BottomMinX_mm = -pedBotW / 2.0,
                    BottomMaxX_mm = pedBotW / 2.0,
                    BottomMinY_mm = -pedBotD,
                    BottomMaxY_mm = 0.0,
                    TopMinX_mm = -pedTopW / 2.0,
                    TopMaxX_mm = pedTopW / 2.0,
                    TopMinY_mm = -pedTopD,
                    TopMaxY_mm = 0.0,
                    BottomOffset_mm = pedBottomZ,
                    TopOffset_mm = bodyBottomZ + 20.0, // 20mm overlap into basin underside
                    MaterialParameter = "Ceramic_Material",
                    VisibilityParameter = "Pedestal_Visible",
                    Subcategory = "Pedestal"
                }
            };

            // 5. MEP Connectors
            schema.MepConnectors = new List<MepConnectorDef>
            {
                // Sanitary Waste Outlet pointing into wall face
                new MepConnectorDef
                {
                    Name = "Sanitary_Waste_Outlet",
                    Domain = "Pipe",
                    SystemType = "Sanitary",
                    Diameter_mm = 32.0,
                    OriginOffsetMm = new VectorOffsetDef(0.0, -180.0, wasteH),
                    Direction = "-Y"
                },
                // Domestic Cold Water Supply
                new MepConnectorDef
                {
                    Name = "Cold_Water_Supply",
                    Domain = "Pipe",
                    SystemType = "DomesticColdWater",
                    Diameter_mm = 15.0,
                    OriginOffsetMm = new VectorOffsetDef(-100.0, 0.0, 580.0),
                    Direction = "Y"
                },
                // Domestic Hot Water Supply
                new MepConnectorDef
                {
                    Name = "Hot_Water_Supply",
                    Domain = "Pipe",
                    SystemType = "DomesticHotWater",
                    Diameter_mm = 15.0,
                    OriginOffsetMm = new VectorOffsetDef(100.0, 0.0, 580.0),
                    Direction = "Y"
                }
            };

            // 6. Family Type Catalog
            schema.FamilyTypes = new List<FamilyTypeDef>
            {
                new FamilyTypeDef
                {
                    Name = "Standard (550x420)",
                    ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Width", 550.0 }, { "Depth", 420.0 }, { "Basin_Rim_Height", 850.0 }, { "Basin_Body_Height", 178.0 }
                    }
                },
                new FamilyTypeDef
                {
                    Name = "Compact (500x380)",
                    ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Width", 500.0 }, { "Depth", 380.0 }, { "Basin_Rim_Height", 850.0 }, { "Basin_Body_Height", 170.0 }
                    }
                },
                new FamilyTypeDef
                {
                    Name = "Executive (600x460)",
                    ParameterValues = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        { "Width", 600.0 }, { "Depth", 460.0 }, { "Basin_Rim_Height", 850.0 }, { "Basin_Body_Height", 185.0 }
                    }
                }
            };

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Width={w}mm, Depth={d}mm, Rim={rimH}mm, Body={bodyH}mm, Pedestal={pedestalH}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 550.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 420.0);
            double rimH = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 850.0);
            if (rimH < 500) rimH = 850.0;
            double bodyH = spec.GetDim("Basin_Body_Height", 178.0);
            double pedH = spec.GetDim("Pedestal_Height", 320.0);

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Overall Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Overall Depth", d, "Depth", 2.0, DimensionDatums.WallFace),
                new ArchetypeVerificationRule("Basin Rim Elevation", rimH, "Basin_Rim_Height", 2.0, DimensionDatums.Floor),
                new ArchetypeVerificationRule("Basin Ceramic Body Height", bodyH, "Basin_Body_Height", 2.0, DimensionDatums.RimLevel),
                new ArchetypeVerificationRule("Semi-Pedestal Shroud Height", pedH, "Pedestal_Height", 2.0, DimensionDatums.Floor)
            };
        }
    }
}
