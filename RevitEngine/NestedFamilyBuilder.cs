using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Constructs nested/sub-component Revit Family (.rfa) documents in memory,
    /// sets up parametric constraints, writes them to temporary storage, and prepares them for host loading.
    /// </summary>
    public static class NestedFamilyBuilder
    {
        public static double MmToFeet(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        public static string BuildAndSave(UIApplication uiApp, NestedFamilyDef nestedDef, string templateDirectoryPath)
        {
            if (uiApp == null) throw new ArgumentNullException(nameof(uiApp));
            if (nestedDef == null) throw new ArgumentNullException(nameof(nestedDef));

            string templatePath = Path.Combine(templateDirectoryPath, nestedDef.TemplateName);
            if (!File.Exists(templatePath))
            {
                // Fallback search in standard Autodesk family templates directory if not found in custom folder
                string defaultTemplates = @"C:\ProgramData\Autodesk\RVT 2025\Family Templates\English";
                string altPath = Path.Combine(defaultTemplates, nestedDef.TemplateName);
                if (File.Exists(altPath))
                {
                    templatePath = altPath;
                }
                else
                {
                    throw new FileNotFoundException($"Family template not found: '{nestedDef.TemplateName}'. Looked in '{templateDirectoryPath}' and '{defaultTemplates}'.");
                }
            }

            Document familyDoc = uiApp.Application.NewFamilyDocument(templatePath);
            if (familyDoc == null)
            {
                throw new InvalidOperationException($"Failed to create new family document using template: {templatePath}");
            }

            string tempRfaPath = Path.Combine(Path.GetTempPath(), $"{nestedDef.NestedFamilyName}_{Guid.NewGuid():N}.rfa");

            using (Transaction trans = new Transaction(familyDoc, $"Build Sub-Component {nestedDef.NestedFamilyName}"))
            {
                trans.Start();

                FamilyManager famMgr = familyDoc.FamilyManager;

                // 1. Add Sub-Parameters (Modern Revit 2025/2026 ForgeTypeId & SpecTypeId)
                foreach (var paramDef in nestedDef.SubParameters)
                {
                    ForgeTypeId specType = SpecTypeId.Length;
                    if (string.Equals(paramDef.DataType, "YesNo", StringComparison.OrdinalIgnoreCase))
                    {
                        specType = SpecTypeId.Boolean.YesNo;
                    }
                    else if (string.Equals(paramDef.DataType, "Material", StringComparison.OrdinalIgnoreCase))
                    {
                        specType = SpecTypeId.Reference.Material;
                    }

                    FamilyParameter famParam = famMgr.AddParameter(
                        paramDef.Name,
                        GroupTypeId.Geometry,
                        specType,
                        paramDef.IsInstance
                    );

                    if (specType == SpecTypeId.Length && paramDef.DefaultValueMm != 0)
                    {
                        famMgr.Set(famParam, MmToFeet(paramDef.DefaultValueMm));
                    }
                }

                // 2. Locate or create standard SketchPlane (Reference Level)
                SketchPlane? sketchPlane = new FilteredElementCollector(familyDoc)
                    .OfClass(typeof(SketchPlane))
                    .Cast<SketchPlane>()
                    .FirstOrDefault(sp => sp.Name.Contains("Ref. Level") || sp.Name.Contains("Level"));

                if (sketchPlane == null)
                {
                    Plane defaultPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    sketchPlane = SketchPlane.Create(familyDoc, defaultPlane);
                }

                // 3. Build sub-extrusions
                foreach (var extrusionDef in nestedDef.SubExtrusions)
                {
                    CreateSubExtrusion(familyDoc, famMgr, sketchPlane, extrusionDef);
                }

                familyDoc.Regenerate();
                trans.Commit();
            }

            // Save nested family to temp file and close document
            SaveAsOptions saveOptions = new SaveAsOptions
            {
                OverwriteExistingFile = true,
                Compact = true
            };

            familyDoc.SaveAs(tempRfaPath, saveOptions);
            familyDoc.Close(false);

            return tempRfaPath;
        }

        private static void CreateSubExtrusion(
            Document familyDoc, 
            FamilyManager famMgr, 
            SketchPlane sketchPlane, 
            ExtrusionDef extDef)
        {
            double minX = MmToFeet(extDef.MinX_mm);
            double maxX = MmToFeet(extDef.MaxX_mm);
            double minY = MmToFeet(extDef.MinY_mm);
            double maxY = MmToFeet(extDef.MaxY_mm);
            double startZ = MmToFeet(extDef.StartZ_mm);
            double endZ = MmToFeet(extDef.EndZ_mm);

            CurveArrArray profile = new CurveArrArray();
            CurveArray loop = new CurveArray();

            XYZ p1 = new XYZ(minX, minY, 0);
            XYZ p2 = new XYZ(maxX, minY, 0);
            XYZ p3 = new XYZ(maxX, maxY, 0);
            XYZ p4 = new XYZ(minX, maxY, 0);

            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));

            profile.Append(loop);

            Extrusion extrusion = familyDoc.FamilyCreate.NewExtrusion(
                true,
                profile,
                sketchPlane,
                endZ - startZ
            );

            // Set extrusion height offsets
            Parameter startParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
            Parameter endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);

            if (startParam != null && !startParam.IsReadOnly)
            {
                startParam.Set(startZ);
            }
            if (endParam != null && !endParam.IsReadOnly)
            {
                endParam.Set(endZ);
            }

            // Parameterize height if specified
            if (!string.IsNullOrEmpty(extDef.HeightParameter))
            {
                FamilyParameter? heightFamParam = famMgr.get_Parameter(extDef.HeightParameter);
                if (heightFamParam != null && endParam != null && !endParam.IsReadOnly)
                {
                    famMgr.AssociateElementParameterToFamilyParameter(endParam, heightFamParam);
                }
            }
        }
    }
}
