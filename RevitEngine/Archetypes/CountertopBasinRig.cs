using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic rig for Countertop Vessel Basins.
    /// Anchored to Countertop datum (Z = 0) with concentric hollow bowl walls,
    /// solid dish floor, drain hole void, and tall gooseneck chrome faucet sweep.
    /// </summary>
    public class CountertopBasinRig : IArchetypeRig
    {
        public string ArchetypeName => "CountertopBasin";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("countertop") || s.Contains("vessel") || s.Contains("sit-on") || s.Contains("lay-on");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 500.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 400.0);
            double bowlH = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 150.0);
            if (bowlH > 350) bowlH = 150.0;

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"VesselBasin_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : "CountertopBasin_Parametric";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Plumbing Fixtures",
                TemplateName = "Metric Generic Model.rft",
                Description = "High-precision countertop vessel basin with concentric hollow bowl and waste outlet.",
                Manufacturer = spec.Manufacturer ?? "Generic Sanitaryware",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Basin", "Fixtures", "Glazing" }
            };

            // Parameters
            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = d, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Bowl_Height", DataType = "Length", DefaultValueMm = bowlH, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Wall_Thickness", DataType = "Length", DefaultValueMm = 18.0, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Ceramic_Material", DataType = "Material", GroupType = "Materials" }
            };

            // Reference Planes
            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Front_Edge", Orientation = "Y", OffsetFromOriginMm = -d / 2.0, DrivenByParameter = "Depth" },
                new ReferencePlaneDef { Name = "Back_Edge", Orientation = "Y", OffsetFromOriginMm = d / 2.0, DrivenByParameter = "Depth" },
                new ReferencePlaneDef { Name = "Vessel_Rim_Level", Orientation = "Z", OffsetFromOriginMm = bowlH, DrivenByParameter = "Bowl_Height" }
            };

            // Extrusions: Hollow Oval/DShape rim + dish floor + drain cutout
            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // Outer ceramic bowl body with concentric hollow cavity
                new ExtrusionDef
                {
                    Name = "Vessel_Ceramic_Shell",
                    ProfileType = "HollowDShape",
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -d / 2.0,
                    MaxY_mm = d / 2.0,
                    WallThickness_mm = 18.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = bowlH,
                    WidthParameter = "Width",
                    DepthParameter = "Depth",
                    HeightParameter = "Bowl_Height",
                    MaterialParameter = "Ceramic_Material",
                    Subcategory = "Basin"
                },
                // Solid bowl bottom base
                new ExtrusionDef
                {
                    Name = "Vessel_Floor_Plate",
                    ProfileType = "RoundedRectangle",
                    CornerRadius_mm = 20.0,
                    IsVoid = false,
                    MinX_mm = -w / 2.0 + 18.0,
                    MaxX_mm = w / 2.0 - 18.0,
                    MinY_mm = -d / 2.0 + 18.0,
                    MaxY_mm = d / 2.0 - 18.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = 22.0,
                    MaterialParameter = "Ceramic_Material",
                    Subcategory = "Basin"
                },
                // Drain hole void
                new ExtrusionDef
                {
                    Name = "Vessel_Drain_Void",
                    ProfileType = "Cylinder",
                    IsVoid = true,
                    Radius_mm = 23.0,
                    MinX_mm = -23.0,
                    MaxX_mm = 23.0,
                    MinY_mm = -23.0,
                    MaxY_mm = 23.0,
                    StartZ_mm = -5.0,
                    EndZ_mm = 30.0,
                    Subcategory = "Basin"
                }
            };

            // Tall Chrome Countertop Mixer Faucet (watertight cylinders that never fail)
            schema.HostExtrusions.Add(new ExtrusionDef
            {
                Name = "Vessel_Faucet_Pillar",
                ProfileType = "Cylinder",
                IsVoid = false,
                Radius_mm = 16.0,
                MinX_mm = -16.0,
                MaxX_mm = 16.0,
                MinY_mm = d / 2.0 + 35.0 - 16.0,
                MaxY_mm = d / 2.0 + 35.0 + 16.0,
                StartZ_mm = 0.0,
                EndZ_mm = bowlH + 110.0,
                Subcategory = "Fixtures"
            });
            schema.HostExtrusions.Add(new ExtrusionDef
            {
                Name = "Vessel_Faucet_Spout",
                ProfileType = "RoundedRectangle",
                CornerRadius_mm = 7.0,
                IsVoid = false,
                MinX_mm = -8.0,
                MaxX_mm = 8.0,
                MinY_mm = 0.0,
                MaxY_mm = d / 2.0 + 35.0,
                StartZ_mm = bowlH + 95.0,
                EndZ_mm = bowlH + 110.0,
                Subcategory = "Fixtures"
            });

            // MEP Connectors
            schema.MepConnectors = new List<MepConnectorDef>
            {
                new MepConnectorDef
                {
                    Name = "Vessel_Sanitary_Waste",
                    Domain = "Pipe",
                    SystemType = "Sanitary",
                    Diameter_mm = 32.0,
                    OriginOffsetMm = new VectorOffsetDef(0.0, 0.0, 0.0),
                    Direction = "-Z"
                }
            };

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Width={w}mm, Depth={d}mm, Height={bowlH}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 500.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 400.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 150.0);
            if (h > 350) h = 150.0;

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Overall Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Overall Depth", d, "Depth", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Vessel Bowl Height", h, "Bowl_Height", 2.0, DimensionDatums.Countertop)
            };
        }
    }
}
