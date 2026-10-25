using System;
using System.Collections.Generic;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine.Archetypes
{
    /// <summary>
    /// Deterministic rig for Structural Column Baseplates & Moment Connections.
    /// Constructs structural steel plate, leveling grout layer, 4x anchor bolts, and column stub.
    /// </summary>
    public class BaseplateJointRig : IArchetypeRig
    {
        public string ArchetypeName => "BaseplateJoint";

        public bool CanHandle(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.ToLowerInvariant();
            return s.Contains("baseplate") || s.Contains("base plate") || s.Contains("column connection") || s.Contains("structural joint");
        }

        public FamilySchema BuildSchema(ExtractedCutSheetSpec spec, Action<string>? onLog = null)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 400.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 400.0);
            double plateThk = spec.GetDim("Plate_Thickness", 25.0);
            double groutThk = spec.GetDim("Grout_Thickness", 30.0);
            double boltDia = spec.GetDim("Bolt_Diameter", 24.0);
            double boltInset = spec.GetDim("Bolt_Inset", 50.0);

            string famName = !string.IsNullOrWhiteSpace(spec.ProductCode)
                ? $"Baseplate_{spec.Manufacturer}_{spec.ProductCode}".Replace(" ", "_")
                : "StructuralBaseplate_Parametric";

            var schema = new FamilySchema
            {
                MasterFamilyName = famName,
                FamilyCategory = "Structural Connections",
                TemplateName = "Metric Generic Model.rft",
                Description = "High-precision parametric structural steel baseplate with anchor bolts and grout bed.",
                Manufacturer = spec.Manufacturer ?? "Standard Steel",
                CutSheetSpec = spec,
                Subcategories = new List<string> { "Plates", "Anchors", "Grout", "Columns" }
            };

            schema.MasterParameters = new List<MasterParameterDef>
            {
                new MasterParameterDef { Name = "Plate_Width", DataType = "Length", DefaultValueMm = w, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Plate_Depth", DataType = "Length", DefaultValueMm = d, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Plate_Thickness", DataType = "Length", DefaultValueMm = plateThk, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Grout_Thickness", DataType = "Length", DefaultValueMm = groutThk, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Bolt_Diameter", DataType = "Length", DefaultValueMm = boltDia, GroupType = "Dimensions" },
                new MasterParameterDef { Name = "Steel_Material", DataType = "Material", GroupType = "Materials" }
            };

            schema.ReferencePlanes = new List<ReferencePlaneDef>
            {
                new ReferencePlaneDef { Name = "Left_Edge", Orientation = "X", OffsetFromOriginMm = -w / 2.0, DrivenByParameter = "Plate_Width" },
                new ReferencePlaneDef { Name = "Right_Edge", Orientation = "X", OffsetFromOriginMm = w / 2.0, DrivenByParameter = "Plate_Width" },
                new ReferencePlaneDef { Name = "Front_Edge", Orientation = "Y", OffsetFromOriginMm = -d / 2.0, DrivenByParameter = "Plate_Depth" },
                new ReferencePlaneDef { Name = "Back_Edge", Orientation = "Y", OffsetFromOriginMm = d / 2.0, DrivenByParameter = "Plate_Depth" }
            };

            schema.HostExtrusions = new List<ExtrusionDef>
            {
                // Non-shrink grout bedding layer
                new ExtrusionDef
                {
                    Name = "Grout_Bed",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0 - 25.0,
                    MaxX_mm = w / 2.0 + 25.0,
                    MinY_mm = -d / 2.0 - 25.0,
                    MaxY_mm = d / 2.0 + 25.0,
                    StartZ_mm = 0.0,
                    EndZ_mm = groutThk,
                    Subcategory = "Grout"
                },
                // Steel Baseplate
                new ExtrusionDef
                {
                    Name = "Steel_Baseplate",
                    ProfileType = "Rectangle",
                    IsVoid = false,
                    MinX_mm = -w / 2.0,
                    MaxX_mm = w / 2.0,
                    MinY_mm = -d / 2.0,
                    MaxY_mm = d / 2.0,
                    StartZ_mm = groutThk,
                    EndZ_mm = groutThk + plateThk,
                    MaterialParameter = "Steel_Material",
                    Subcategory = "Plates"
                }
            };

            // 4 Anchor Bolts
            double bx = w / 2.0 - boltInset;
            double by = d / 2.0 - boltInset;
            double boltR = boltDia / 2.0;

            var corners = new[] { (-bx, -by), (bx, -by), (-bx, by), (bx, by) };
            int bIdx = 1;
            foreach (var (cx, cy) in corners)
            {
                schema.HostExtrusions.Add(new ExtrusionDef
                {
                    Name = $"Anchor_Bolt_{bIdx++}",
                    ProfileType = "Cylinder",
                    IsVoid = false,
                    Radius_mm = boltR,
                    MinX_mm = cx - boltR,
                    MaxX_mm = cx + boltR,
                    MinY_mm = cy - boltR,
                    MaxY_mm = cy + boltR,
                    StartZ_mm = groutThk,
                    EndZ_mm = groutThk + plateThk + 60.0,
                    MaterialParameter = "Steel_Material",
                    Subcategory = "Anchors"
                });
            }

            onLog?.Invoke($"[ARCHETYPE RIG] Built '{ArchetypeName}' schema (Plate={w}x{d}x{plateThk}mm, Grout={groutThk}mm).");
            return schema;
        }

        public List<ArchetypeVerificationRule> GetVerificationRules(ExtractedCutSheetSpec spec)
        {
            double w = spec.GetDim("Width", spec.OverallWidthMm > 0 ? spec.OverallWidthMm : 400.0);
            double d = spec.GetDim("Depth", spec.OverallDepthMm > 0 ? spec.OverallDepthMm : 400.0);
            double plateThk = spec.GetDim("Plate_Thickness", 25.0);

            return new List<ArchetypeVerificationRule>
            {
                new ArchetypeVerificationRule("Plate Width", w, "Plate_Width", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Plate Depth", d, "Plate_Depth", 2.0, DimensionDatums.Centerline),
                new ArchetypeVerificationRule("Plate Thickness", plateThk, "Plate_Thickness", 1.0, DimensionDatums.Floor)
            };
        }
    }
}
