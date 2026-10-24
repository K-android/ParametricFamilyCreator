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

            string templatePath = TemplateResolver.Resolve(uiApp, nestedDef.TemplateName, templateDirectoryPath);

            Document? familyDoc = null;
            try
            {
                if (templatePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                {
                    familyDoc = uiApp.Application.OpenDocumentFile(templatePath);
                }
                else
                {
                    familyDoc = uiApp.Application.NewFamilyDocument(templatePath);
                }
            }
            catch
            {
                try
                {
                    familyDoc = uiApp.Application.OpenDocumentFile(templatePath);
                }
                catch { }
            }

            if (familyDoc == null)
            {
                throw new InvalidOperationException($"Failed to create or open family document using template: {templatePath}");
            }

            string tempRfaPath = Path.Combine(Path.GetTempPath(), $"{nestedDef.NestedFamilyName}_{Guid.NewGuid():N}.rfa");

            using (Transaction trans = new Transaction(familyDoc, $"Build Sub-Component {nestedDef.NestedFamilyName}"))
            {
                trans.Start();

                // If opening a seed family (.rfa), delete pre-existing model geometry
                try
                {
                    var existingGeomIds = new FilteredElementCollector(familyDoc)
                        .OfClass(typeof(GenericForm))
                        .ToElementIds();
                    if (existingGeomIds.Count > 0)
                    {
                        familyDoc.Delete(existingGeomIds);
                    }
                }
                catch { }

                FamilyManager famMgr = familyDoc.FamilyManager;

                // Ensure nested family has a valid FamilyType
                FamilyType? nestedType = famMgr.CurrentType;
                if (nestedType == null)
                {
                    try
                    {
                        if (famMgr.Types.Size == 0)
                        {
                            string typeName = !string.IsNullOrWhiteSpace(nestedDef.NestedFamilyName) ? nestedDef.NestedFamilyName : "Standard";
                            nestedType = famMgr.NewType(typeName);
                        }
                        else
                        {
                            nestedType = famMgr.Types.Cast<FamilyType>().FirstOrDefault();
                            if (nestedType != null) famMgr.CurrentType = nestedType;
                        }
                    }
                    catch { }
                }
                if (famMgr.CurrentType == null)
                {
                    try { famMgr.NewType("Standard"); } catch { }
                }

                // Configure Shared Family status if requested (for independent BIM scheduling)
                if (nestedDef.IsShared && familyDoc.OwnerFamily != null)
                {
                    Parameter? sharedParam = familyDoc.OwnerFamily.get_Parameter(BuiltInParameter.FAMILY_SHARED);
                    if (sharedParam != null && !sharedParam.IsReadOnly)
                    {
                        sharedParam.Set(1);
                    }
                }

                // 1. Add Sub-Parameters (Modern Revit 2024 / 2025 / 2026 ForgeTypeId & SpecTypeId)
                if (nestedDef.SubParameters != null)
                {
                    foreach (var paramDef in nestedDef.SubParameters)
                    {
                        try
                        {
                            ForgeTypeId specType = MasterFamilyBuilder.ResolveSpecType(paramDef.DataType);
                            ForgeTypeId groupType = MasterFamilyBuilder.ResolveGroupType(paramDef.GroupType, paramDef.DataType);

                            FamilyParameter famParam = famMgr.AddParameter(
                                paramDef.Name,
                                groupType,
                                specType,
                                paramDef.IsInstance
                            );

                            if (specType == SpecTypeId.Length)
                            {
                                double mm = paramDef.GetEffectiveNumericValue();
                                if (mm != 0) famMgr.Set(famParam, MmToFeet(mm));
                            }
                            else if (specType == SpecTypeId.Angle)
                            {
                                double deg = paramDef.GetEffectiveNumericValue();
                                if (deg != 0) famMgr.Set(famParam, UnitUtils.ConvertToInternalUnits(deg, UnitTypeId.Degrees));
                            }
                            else if (specType == SpecTypeId.Boolean.YesNo)
                            {
                                famMgr.Set(famParam, paramDef.GetEffectiveNumericValue() != 0 ? 1 : 0);
                            }
                            else if (specType == SpecTypeId.Int.Integer)
                            {
                                famMgr.Set(famParam, (int)paramDef.GetEffectiveNumericValue());
                            }
                            else if (specType == SpecTypeId.Number)
                            {
                                famMgr.Set(famParam, paramDef.GetEffectiveNumericValue());
                            }
                            else if (specType == SpecTypeId.String.Text || specType == SpecTypeId.String.Url)
                            {
                                if (!string.IsNullOrEmpty(paramDef.DefaultText))
                                {
                                    famMgr.Set(famParam, paramDef.DefaultText);
                                }
                            }

                            if (!string.IsNullOrEmpty(paramDef.Formula))
                            {
                                try { famMgr.SetFormula(famParam, paramDef.Formula); } catch { }
                            }
                        }
                        catch { }
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
                var createdSolids = new System.Collections.Generic.List<CombinableElement>();
                if (nestedDef.SubExtrusions != null)
                {
                    foreach (var extrusionDef in nestedDef.SubExtrusions)
                    {
                        try
                        {
                            CreateSubExtrusion(familyDoc, famMgr, sketchPlane, extrusionDef, createdSolids);
                        }
                        catch { }
                    }
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

        private static void CreateSubExtrusion(Document familyDoc, FamilyManager famMgr, SketchPlane sketchPlane, ExtrusionDef extDef, System.Collections.Generic.List<CombinableElement> createdSolids)
        {
            double minX = MmToFeet(extDef.MinX_mm);
            double maxX = MmToFeet(extDef.MaxX_mm);
            double minY = MmToFeet(extDef.MinY_mm);
            double maxY = MmToFeet(extDef.MaxY_mm);
            double startZ = MmToFeet(extDef.StartZ_mm);
            double endZ = MmToFeet(extDef.EndZ_mm);

            // Scale and boundary sanity guards: prevent degenerate zero-area profiles or negative heights
            if (maxX <= minX) maxX = minX + MmToFeet(10.0);
            if (maxY <= minY) maxY = minY + MmToFeet(10.0);
            if (endZ <= startZ) endZ = startZ + MmToFeet(5.0);
            double extrusionHeight = Math.Max(endZ - startZ, MmToFeet(2.0));

            CurveArrArray profile = MasterFamilyBuilder.BuildExtrusionProfile(extDef, minX, maxX, minY, maxY);

            bool isSolid = !extDef.IsVoid;
            Extrusion extrusion = familyDoc.FamilyCreate.NewExtrusion(
                isSolid,
                profile,
                sketchPlane,
                extrusionHeight
            );

            if (!isSolid)
            {
                foreach (var solid in createdSolids)
                {
                    try
                    {
                        if (SolidSolidCutUtils.CanElementCutElement(extrusion, solid, out _))
                        {
                            SolidSolidCutUtils.AddCutBetweenSolids(familyDoc, solid, extrusion);
                        }
                    }
                    catch { }
                }
            }
            else
            {
                createdSolids.Add(extrusion);
            }

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

            // Link height parameter if specified
            if (!string.IsNullOrEmpty(extDef.HeightParameter))
            {
                FamilyParameter? heightFamParam = famMgr.get_Parameter(extDef.HeightParameter);
                if (endParam != null && !endParam.IsReadOnly && heightFamParam != null)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(endParam, heightFamParam); } catch { }
                }
            }

            // Link material parameter if specified
            if (!string.IsNullOrEmpty(extDef.MaterialParameter))
            {
                FamilyParameter? matFamParam = famMgr.get_Parameter(extDef.MaterialParameter);
                if (matFamParam == null)
                {
                    try
                    {
                        matFamParam = famMgr.AddParameter(
                            extDef.MaterialParameter,
                            GroupTypeId.Materials,
                            SpecTypeId.Reference.Material,
                            false
                        );
                    }
                    catch { }
                }

                if (matFamParam != null)
                {
                    Parameter matParam = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (matParam != null && !matParam.IsReadOnly)
                    {
                        try { famMgr.AssociateElementParameterToFamilyParameter(matParam, matFamParam); } catch { }
                    }
                }
            }
        }
    }
}
