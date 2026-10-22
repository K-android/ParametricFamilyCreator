using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Constructs the Master Host Family (.rfa), generates parametric host geometry,
    /// sets up reference planes, dimensional constraints, loads nested families,
    /// and associates nested parameters to master family parameters.
    /// </summary>
    public static class MasterFamilyBuilder
    {
        public static double MmToFeet(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        public static string BuildAndLoadIntoProject(
            UIApplication uiApp,
            FamilySchema schema,
            Dictionary<string, string> nestedRfaPaths,
            string templateDirectoryPath,
            string outputDirectoryPath)
        {
            if (uiApp == null) throw new ArgumentNullException(nameof(uiApp));
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            // Safely locate active project document (null if user is on home screen)
            Document? activeProjectDoc = uiApp.ActiveUIDocument?.Document;

            // Intelligently resolve template across versions, languages, and Metric/Imperial names
            string templatePath = TemplateResolver.Resolve(uiApp, schema.TemplateName, templateDirectoryPath);

            Document masterDoc = uiApp.Application.NewFamilyDocument(templatePath);
            if (masterDoc == null)
            {
                throw new InvalidOperationException($"Failed to create Master Family document from '{templatePath}'.");
            }

            if (!Directory.Exists(outputDirectoryPath))
            {
                Directory.CreateDirectory(outputDirectoryPath);
            }

            string masterRfaPath = Path.Combine(outputDirectoryPath, $"{schema.MasterFamilyName}.rfa");

            using (Transaction trans = new Transaction(masterDoc, $"Build Master Family {schema.MasterFamilyName}"))
            {
                trans.Start();

                FamilyManager famMgr = masterDoc.FamilyManager;

                // 1. Create Master Parameters
                var paramDict = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
                foreach (var paramDef in schema.MasterParameters)
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

                    if (!string.IsNullOrEmpty(paramDef.Formula))
                    {
                        try { famMgr.SetFormula(famParam, paramDef.Formula); } catch { }
                    }

                    paramDict[paramDef.Name] = famParam;
                }

                // 2. Identify Plan View and Elevation View
                View? planView = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => v.ViewType == ViewType.FloorPlan && (v.Name.Contains("Ref. Level") || v.Name.Contains("Level")));

                if (planView == null)
                {
                    planView = masterDoc.ActiveView;
                }

                View? elevView = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(v => v.ViewType == ViewType.Elevation && (v.Name.Contains("Front") || v.Name.Contains("Exterior") || v.Name.Contains("Elevation")));

                // 3. Create Reference Planes & Apply The Critical Regenerate() Rule
                var createdRefPlanes = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
                foreach (var rpDef in schema.ReferencePlanes)
                {
                    try
                    {
                        ReferencePlane? rp = CreateReferencePlane(masterDoc, planView, elevView, rpDef);
                        if (rp != null && !string.IsNullOrEmpty(rpDef.Name))
                        {
                            createdRefPlanes[rpDef.Name] = rp;
                        }
                    }
                    catch { }
                }

                // CRITICAL REQUIREMENT: Regenerate() before any geometry or dimension binding
                masterDoc.Regenerate();

                // 4. Create Host Extrusions
                SketchPlane? sketchPlane = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(SketchPlane))
                    .Cast<SketchPlane>()
                    .FirstOrDefault(sp => sp.Name.Contains("Ref. Level") || sp.Name.Contains("Level"));

                if (sketchPlane == null)
                {
                    Plane defaultPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
                    sketchPlane = SketchPlane.Create(masterDoc, defaultPlane);
                }

                foreach (var extDef in schema.HostExtrusions)
                {
                    try
                    {
                        CreateHostExtrusion(masterDoc, famMgr, sketchPlane, extDef, paramDict);
                    }
                    catch { }
                }

                masterDoc.Regenerate();

                // 5. Load and Place Nested Families + Associate Parameters
                foreach (var nestedDef in schema.NestedFamilies)
                {
                    try
                    {
                        if (nestedRfaPaths.TryGetValue(nestedDef.NestedFamilyName, out string? nestedRfaPath) && File.Exists(nestedRfaPath))
                        {
                            PlaceAndBindNestedFamily(masterDoc, famMgr, nestedDef, nestedRfaPath);
                        }
                    }
                    catch { }
                }

                masterDoc.Regenerate();
                trans.Commit();
            }

            // Save Master Family to output directory
            SaveAsOptions saveOptions = new SaveAsOptions
            {
                OverwriteExistingFile = true,
                Compact = true
            };
            masterDoc.SaveAs(masterRfaPath, saveOptions);

            // 6. Load Master Family into Active Project Document (if one is open)
            if (activeProjectDoc != null && !activeProjectDoc.IsFamilyDocument)
            {
                try
                {
                    using (Transaction projectTrans = new Transaction(activeProjectDoc, $"Load Family {schema.MasterFamilyName}"))
                    {
                        projectTrans.Start();
                        activeProjectDoc.LoadFamily(masterRfaPath, new FamilyLoadOptions(), out Family loadedProjectFamily);
                        projectTrans.Commit();
                    }
                }
                catch { }
            }

            masterDoc.Close(false);

            return masterRfaPath;
        }

        private static ReferencePlane? CreateReferencePlane(Document doc, View planView, View? elevView, ReferencePlaneDef rpDef)
        {
            try
            {
                double offsetFeet = MmToFeet(rpDef.OffsetFromOriginMm);
                XYZ bubbleEnd;
                XYZ freeEnd;
                XYZ cutVec;
                View targetView = planView;

                if (string.Equals(rpDef.Orientation, "X", StringComparison.OrdinalIgnoreCase))
                {
                    // Parallel to Y axis in Plan View
                    bubbleEnd = new XYZ(offsetFeet, -10.0, 0.0);
                    freeEnd = new XYZ(offsetFeet, 10.0, 0.0);
                    cutVec = XYZ.BasisX;
                    targetView = planView;
                }
                else if (string.Equals(rpDef.Orientation, "Y", StringComparison.OrdinalIgnoreCase))
                {
                    // Parallel to X axis in Plan View
                    bubbleEnd = new XYZ(-10.0, offsetFeet, 0.0);
                    freeEnd = new XYZ(10.0, offsetFeet, 0.0);
                    cutVec = XYZ.BasisY;
                    targetView = planView;
                }
                else
                {
                    // Elevation plane (Z)
                    if (elevView != null)
                    {
                        bubbleEnd = new XYZ(-10.0, 0.0, offsetFeet);
                        freeEnd = new XYZ(10.0, 0.0, offsetFeet);
                        cutVec = XYZ.BasisZ;
                        targetView = elevView;
                    }
                    else
                    {
                        return null;
                    }
                }

                ReferencePlane rp = doc.FamilyCreate.NewReferencePlane(bubbleEnd, freeEnd, cutVec, targetView);
                if (!string.IsNullOrEmpty(rpDef.Name))
                {
                    rp.Name = rpDef.Name;
                }
                rp.Maximize3DExtents();
                return rp;
            }
            catch
            {
                return null;
            }
        }

        private static void CreateHostExtrusion(
            Document doc,
            FamilyManager famMgr,
            SketchPlane sketchPlane,
            ExtrusionDef extDef,
            Dictionary<string, FamilyParameter> paramDict)
        {
            double minX = MmToFeet(extDef.MinX_mm);
            double maxX = MmToFeet(extDef.MaxX_mm);
            double minY = MmToFeet(extDef.MinY_mm);
            double maxY = MmToFeet(extDef.MaxY_mm);
            double startZ = MmToFeet(extDef.StartZ_mm);
            double endZ = MmToFeet(extDef.EndZ_mm);

            CurveArrArray profile = new CurveArrArray();

            if (string.Equals(extDef.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase))
            {
                double centerX = (minX + maxX) / 2.0;
                double centerY = (minY + maxY) / 2.0;
                double radius = extDef.Radius_mm > 0 ? MmToFeet(extDef.Radius_mm) : Math.Abs(maxX - minX) / 2.0;
                if (radius <= 0.001) radius = MmToFeet(25.0);

                XYZ pt1 = new XYZ(centerX - radius, centerY, 0);
                XYZ pt2 = new XYZ(centerX + radius, centerY, 0);
                XYZ mid1 = new XYZ(centerX, centerY - radius, 0);
                XYZ mid2 = new XYZ(centerX, centerY + radius, 0);

                CurveArray circleLoop = new CurveArray();
                circleLoop.Append(Arc.Create(pt1, pt2, mid1));
                circleLoop.Append(Arc.Create(pt2, pt1, mid2));
                profile.Append(circleLoop);
            }
            else
            {
                CurveArray outerLoop = new CurveArray();
                XYZ p1 = new XYZ(minX, minY, 0);
                XYZ p2 = new XYZ(maxX, minY, 0);
                XYZ p3 = new XYZ(maxX, maxY, 0);
                XYZ p4 = new XYZ(minX, maxY, 0);

                outerLoop.Append(Line.CreateBound(p1, p2));
                outerLoop.Append(Line.CreateBound(p2, p3));
                outerLoop.Append(Line.CreateBound(p3, p4));
                outerLoop.Append(Line.CreateBound(p4, p1));
                profile.Append(outerLoop);

                if (extDef.WallThickness_mm > 0 || string.Equals(extDef.ProfileType, "HollowBox", StringComparison.OrdinalIgnoreCase))
                {
                    double wt = extDef.WallThickness_mm > 0 ? MmToFeet(extDef.WallThickness_mm) : MmToFeet(5.0);
                    if ((maxX - minX) > 2 * wt && (maxY - minY) > 2 * wt)
                    {
                        CurveArray innerLoop = new CurveArray();
                        XYZ ip1 = new XYZ(minX + wt, minY + wt, 0);
                        XYZ ip2 = new XYZ(maxX - wt, minY + wt, 0);
                        XYZ ip3 = new XYZ(maxX - wt, maxY - wt, 0);
                        XYZ ip4 = new XYZ(minX + wt, maxY - wt, 0);

                        innerLoop.Append(Line.CreateBound(ip1, ip4));
                        innerLoop.Append(Line.CreateBound(ip4, ip3));
                        innerLoop.Append(Line.CreateBound(ip3, ip2));
                        innerLoop.Append(Line.CreateBound(ip2, ip1));
                        profile.Append(innerLoop);
                    }
                }
            }

            Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                true,
                profile,
                sketchPlane,
                endZ - startZ
            );

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
            if (!string.IsNullOrEmpty(extDef.HeightParameter) && paramDict.TryGetValue(extDef.HeightParameter, out FamilyParameter? heightFamParam))
            {
                if (endParam != null && !endParam.IsReadOnly)
                {
                    famMgr.AssociateElementParameterToFamilyParameter(endParam, heightFamParam);
                }
            }
        }

        private static void PlaceAndBindNestedFamily(
            Document masterDoc,
            FamilyManager famMgr,
            NestedFamilyDef nestedDef,
            string nestedRfaPath)
        {
            // Load nested RFA into Master Family
            bool loaded = masterDoc.LoadFamily(nestedRfaPath, out Family loadedNestedFamily);
            if (!loaded && loadedNestedFamily == null)
            {
                // In case it was already loaded earlier
                loadedNestedFamily = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(nestedDef.NestedFamilyName, StringComparison.OrdinalIgnoreCase));
            }

            if (loadedNestedFamily == null) return;

            var symbolIds = loadedNestedFamily.GetFamilySymbolIds();
            if (symbolIds.Count == 0) return;

            FamilySymbol symbol = (FamilySymbol)masterDoc.GetElement(symbolIds.First());

            // Activate symbol if not active
            if (!symbol.IsActive)
            {
                symbol.Activate();
                masterDoc.Regenerate();
            }

            // Placement point
            XYZ origin = new XYZ(
                MmToFeet(nestedDef.PlacementOffsetMm.X),
                MmToFeet(nestedDef.PlacementOffsetMm.Y),
                MmToFeet(nestedDef.PlacementOffsetMm.Z)
            );

            FamilyInstance nestedInstance = masterDoc.FamilyCreate.NewFamilyInstance(
                origin,
                symbol,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural
            );

            // Parameter Association (Critical Step)
            // Associate nested instance parameters to host family parameters
            foreach (var kvp in nestedDef.ParameterBindings)
            {
                string nestedParamName = kvp.Key;
                string masterParamName = kvp.Value;

                Parameter? nestedElemParam = nestedInstance.LookupParameter(nestedParamName);
                FamilyParameter? masterFamParam = famMgr.get_Parameter(masterParamName);

                if (nestedElemParam != null && masterFamParam != null && !nestedElemParam.IsReadOnly)
                {
                    famMgr.AssociateElementParameterToFamilyParameter(nestedElemParam, masterFamParam);
                }
            }

            // Visibility Association (for component toggling, e.g. Show_Lever_Handle / Show_Pull_Bar)
            if (!string.IsNullOrEmpty(nestedDef.VisibilityParameter))
            {
                Parameter? visParam = nestedInstance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                FamilyParameter? masterVisParam = famMgr.get_Parameter(nestedDef.VisibilityParameter);

                if (masterVisParam == null)
                {
                    // Create master Yes/No parameter if not already defined
                    masterVisParam = famMgr.AddParameter(
                        nestedDef.VisibilityParameter,
                        GroupTypeId.Visibility,
                        SpecTypeId.Boolean.YesNo,
                        false
                    );
                    famMgr.Set(masterVisParam, 1); // Default to visible
                }

                if (visParam != null && !visParam.IsReadOnly && masterVisParam != null)
                {
                    famMgr.AssociateElementParameterToFamilyParameter(visParam, masterVisParam);
                }
            }
        }
    }

    /// <summary>
    /// Custom IFamilyLoadOptions implementation to always overwrite existing family parameters and definitions.
    /// </summary>
    public class FamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}
