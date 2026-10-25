using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Universal deterministic rig providing guaranteed watertight parametric geometry
    /// for any architectural component when no specialized rig matches.
    /// </summary>
    public class UniversalArchetypeRig : IArchetypeRig
    {
        public string ArchetypeName => "UniversalComponent";

        public bool CanHandle(string text) => true; // Fallback rig

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 600.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 450.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 750.0);

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"Component_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : (!string.IsNullOrWhiteSpace(spec.ModelSeries) 
                    ? $"Component_{spec.ModelSeries}".Replace(" ", "_") 
                    : "Parametric_Component");

            string cat = !string.IsNullOrWhiteSpace(spec.RevitCategory) ? spec.RevitCategory : "Generic Models";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = cat,
                TemplateName = "Metric Generic Model.rft",
                Description = $"Parametric {cat} component modeled deterministically from cut-sheet specifications.",
                Manufacturer = spec.Manufacturer ?? "Generic",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Body", "Trim", "Hardware" }
            };

            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Depth", DataType = "Length", DefaultValueMm = d, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Height", DataType = "Length", DefaultValueMm = h, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Main_Material", DataType = "Material", GroupType = "Materials" }
            };

            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Plane", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Right_Plane", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Width" },
                new ReferencePlaneDef { Name = "Back_Plane", Orientation = "Y", OffsetFromOriginMm = 0.0 },
                new ReferencePlaneDef { Name = "Front_Plane", Orientation = "Y", OffsetFromOriginMm = -d, DrivenByParameter = "Depth" },
                new ReferencePlaneDef { Name = "Top_Plane", Orientation = "Z", OffsetFromOriginMm = h, DrivenByParameter = "Height" }
            };

            schema.HostExtrusions = new List<ExtrusionDef>
            {
                new ExtrusionDef
                {
                    Name = "Component_Body",
                    ProfileType = "RoundedRectangle",
                    CornerRadius_mm = 15.0,
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -d,
                    MaxY_mm = 0.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = h,
                    WidthParameter = "Width",
                    DepthParameter = "Depth",
                    HeightParameter = "Height",
                    MaterialParameter = "Main_Material",
                    Subcategory = "Body"
                }
            };

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Width={w}mm, Depth={d}mm, Height={h}mm, Category='{cat}').");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 600.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 450.0);
            double h = spec.GetDim("Height", spec.OverallHeightMm > 0 ? spec.OverallHeightMm : 750.0);

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Overall Width", w, "Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Overall Depth", d, "Depth", 2.0, DimensionDatums.WallFace),
                new ArchetypeVerificationRule("Overall Height", h, "Height", 2.0, DimensionDatums.Floor)
            };
        }
    }
}
