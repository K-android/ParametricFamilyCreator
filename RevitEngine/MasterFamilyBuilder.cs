using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Electrical;
using System.Data;
using System.Globalization;
﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Constructs the Master Host Family (.rfa), generates parametric host geometry,
    /// sets up reference planes, dimensional constraints, multi-type catalog, loads nested families,
    /// and associates nested parameters to master family parameters.
    /// </summary>
    public static class MasterFamilyBuilder
    {
        public static double MmToFeet(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        public static BuiltInCategory ResolveCategory(string? categoryName)
        {
            if (string.IsNullOrWhiteSpace(categoryName)) return BuiltInCategory.OST_GenericModel;
            string cat = categoryName!.Trim().ToLowerInvariant();
            if (cat.Contains("door")) return BuiltInCategory.OST_Doors;
            if (cat.Contains("window")) return BuiltInCategory.OST_Windows;
            if (cat.Contains("furn")) return BuiltInCategory.OST_Furniture;
            if (cat.Contains("case")) return BuiltInCategory.OST_Casework;
            if (cat.Contains("special")) return BuiltInCategory.OST_SpecialityEquipment;
            if (cat.Contains("mass")) return BuiltInCategory.OST_Mass;
            if (cat.Contains("light")) return BuiltInCategory.OST_LightingFixtures;
            if (cat.Contains("plumb")) return BuiltInCategory.OST_PlumbingFixtures;
            if (cat.Contains("column")) return BuiltInCategory.OST_Columns;
            return BuiltInCategory.OST_GenericModel;
        }

        public static ForgeTypeId ResolveSpecType(string? dataType)
        {
            if (string.IsNullOrWhiteSpace(dataType)) return SpecTypeId.Length;
            string dt = dataType!.Trim().ToLowerInvariant();
            if (dt.Contains("yes") || dt.Contains("bool")) return SpecTypeId.Boolean.YesNo;
            if (dt.Contains("mat")) return SpecTypeId.Reference.Material;
            if (dt.Contains("angle")) return SpecTypeId.Angle;
            if (dt.Contains("area")) return SpecTypeId.Area;
            if (dt.Contains("volume")) return SpecTypeId.Volume;
            if (dt.Contains("int")) return SpecTypeId.Int.Integer;
            if (dt.Contains("num") || dt.Contains("double")) return SpecTypeId.Number;
            if (dt.Contains("text") || dt.Contains("string")) return SpecTypeId.String.Text;
            if (dt.Contains("url")) return SpecTypeId.String.Url;
            return SpecTypeId.Length;
        }

        public static ForgeTypeId ResolveGroupType(string? groupType, string? dataType)
        {
            if (!string.IsNullOrWhiteSpace(groupType))
            {
                string gt = groupType!.Trim().ToLowerInvariant();
                if (gt.Contains("geom") || gt.Contains("dim")) return GroupTypeId.Geometry;
                if (gt.Contains("const")) return GroupTypeId.Constraints;
                if (gt.Contains("mat")) return GroupTypeId.Materials;
                if (gt.Contains("vis")) return GroupTypeId.Visibility;
                if (gt.Contains("ident")) return GroupTypeId.IdentityData;
                if (gt.Contains("data")) return GroupTypeId.Data;
                if (gt.Contains("gen")) return GroupTypeId.General;
                if (gt.Contains("text")) return GroupTypeId.Text;
            }

            if (string.IsNullOrWhiteSpace(dataType)) return GroupTypeId.Geometry;
            string dt = dataType!.Trim().ToLowerInvariant();
            if (dt.Contains("mat")) return GroupTypeId.Materials;
            if (dt.Contains("yes") || dt.Contains("bool")) return GroupTypeId.Visibility;
            if (dt.Contains("text") || dt.Contains("string") || dt.Contains("url")) return GroupTypeId.IdentityData;
            return GroupTypeId.Geometry;
        }

        public static string BuildAndLoadIntoProject(
            UIApplication uiApp,
            FamilySchema schema,
            Dictionary<string, string> nestedRfaPaths,
            string templateDirectoryPath,
            string outputDirectoryPath,
            Action<string>? onLog = null)
        {
            if (uiApp == null) throw new ArgumentNullException(nameof(uiApp));
            if (schema == null) throw new ArgumentNullException(nameof(schema));

            // Safely locate active project document (null if user is on home screen)
            Document? activeProjectDoc = uiApp.ActiveUIDocument?.Document;

            // Intelligently resolve template across versions, languages, and Metric/Imperial names
            string templatePath = TemplateResolver.Resolve(uiApp, schema.TemplateName, templateDirectoryPath);

            Document? masterDoc = null;
            try
            {
                if (templatePath.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
                {
                    masterDoc = uiApp.Application.OpenDocumentFile(templatePath);
                }
                else
                {
                    masterDoc = uiApp.Application.NewFamilyDocument(templatePath);
                }
            }
            catch
            {
                try
                {
                    masterDoc = uiApp.Application.OpenDocumentFile(templatePath);
                }
                catch { }
            }

            if (masterDoc == null)
            {
                throw new InvalidOperationException($"Failed to create or open Master Family document from '{templatePath}'.");
            }

            if (!Directory.Exists(outputDirectoryPath))
            {
                Directory.CreateDirectory(outputDirectoryPath);
            }

            string masterRfaPath = Path.Combine(outputDirectoryPath, $"{schema.MasterFamilyName}.rfa");

            using (Transaction trans = new Transaction(masterDoc, $"Build Master Family {schema.MasterFamilyName}"))
            {
                trans.Start();

                // If opening a seed family (.rfa), delete pre-existing model geometry so family starts fresh
                try
                {
                    var existingGeomIds = new FilteredElementCollector(masterDoc)
                        .OfClass(typeof(GenericForm))
                        .ToElementIds();
                    if (existingGeomIds.Count > 0)
                    {
                        masterDoc.Delete(existingGeomIds);
                    }
                }
                catch { }

                // Dynamically re-categorize family to intended BIM category
                try
                {
                    BuiltInCategory targetBic = ResolveCategory(schema.FamilyCategory);
                    Category? targetCat = masterDoc.Settings?.Categories?.get_Item(targetBic);
                    if (targetCat != null && masterDoc.OwnerFamily != null && masterDoc.OwnerFamily.FamilyCategory?.Id != targetCat.Id)
                    {
                        masterDoc.OwnerFamily.FamilyCategory = targetCat;
                    }
                }
                catch { }

                FamilyManager famMgr = masterDoc.FamilyManager;

                // Ensure at least one initial FamilyType exists
                FamilyType? defaultType = famMgr.CurrentType;
                if (defaultType == null)
                {
                    try
                    {
                        if (famMgr.Types.Size == 0)
                        {
                            string typeName = !string.IsNullOrWhiteSpace(schema.MasterFamilyName) ? schema.MasterFamilyName : "Standard";
                            defaultType = famMgr.NewType(typeName);
                        }
                        else
                        {
                            defaultType = famMgr.Types.Cast<FamilyType>().FirstOrDefault();
                            if (defaultType != null) famMgr.CurrentType = defaultType;
                        }
                    }
                    catch { }
                }
                if (famMgr.CurrentType == null)
                {
                    try { famMgr.NewType("Standard"); } catch { }
                }

                // 1. Create Master Parameters
                var paramDict = new Dictionary<string, FamilyParameter>(StringComparer.OrdinalIgnoreCase);
                if (schema.MasterParameters != null)
                {
                    foreach (var paramDef in schema.MasterParameters)
                    {
                        try
                        {
                            ForgeTypeId specType = ResolveSpecType(paramDef.DataType);
                            ForgeTypeId groupType = ResolveGroupType(paramDef.GroupType, paramDef.DataType);

                            FamilyParameter famParam = famMgr.AddParameter(
                                paramDef.Name,
                                groupType,
                                specType,
                                paramDef.IsInstance
                            );

                            SetParameterDefaultValue(famMgr, famParam, paramDef, specType);

                            if (!string.IsNullOrEmpty(paramDef.Formula))
                            {
                                try { famMgr.SetFormula(famParam, paramDef.Formula); } catch { }
                            }

                            paramDict[paramDef.Name] = famParam;
                        }
                        catch { }
                    }
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

                // 3. Create Reference Planes & Apply Critical Regenerate() Rule
                var createdRefPlanes = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
                if (schema.ReferencePlanes != null)
                {
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
                }

                // CRITICAL REQUIREMENT: Regenerate() before any geometry or dimension binding
                masterDoc.Regenerate();

                // 3b. Initialize Revit BIM Subcategories
                if (schema.Subcategories != null)
                {
                    foreach (var subName in schema.Subcategories)
                    {
                        EnsureSubcategory(masterDoc, subName);
                    }
                }

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

                var createdSolids = new List<CombinableElement>();
                var createdVoids = new List<CombinableElement>();

                if (schema.HostExtrusions != null)
                {
                    foreach (var extDef in schema.HostExtrusions)
                    {
                        try
                        {
                            CreateHostExtrusion(masterDoc, famMgr, sketchPlane, extDef, paramDict, createdSolids, createdVoids, onLog);
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"[Geometry Error] Extrusion '{extDef.Name}': {ex.Message}");
                        }
                    }
                }

                // 4b. Create Host Blends (tapered pedestals, transitions)
                if (schema.HostBlends != null)
                {
                    foreach (var blendDef in schema.HostBlends)
                    {
                        try
                        {
                            CreateHostBlend(masterDoc, uiApp.Application, famMgr, sketchPlane, blendDef, paramDict, createdSolids, createdVoids, onLog);
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"[Geometry Error] Blend '{blendDef.Name}': {ex.Message}");
                        }
                    }
                }

                // 4c. Create Host Revolutions (curved dished bowls, organic cavities, bottle traps, circular columns)
                if (schema.HostRevolves != null)
                {
                    foreach (var revDef in schema.HostRevolves)
                    {
                        try
                        {
                            CreateHostRevolve(masterDoc, famMgr, revDef, paramDict, createdSolids, createdVoids, onLog);
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"[Geometry Error] Revolution '{revDef.Name}': {ex.Message}");
                        }
                    }
                }

                // 4d. Create Host Sweeps (curved spouts, grab bars, rails, moldings)
                if (schema.HostSweeps != null)
                {
                    foreach (var sweepDef in schema.HostSweeps)
                    {
                        try
                        {
                            CreateHostSweep(masterDoc, uiApp.Application, famMgr, sketchPlane, sweepDef, paramDict, createdSolids, createdVoids, onLog);
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"[Geometry Error] Sweep '{sweepDef.Name}': {ex.Message}");
                        }
                    }
                }

                // Update geometry BRep in Revit's engine BEFORE applying boolean cuts
                masterDoc.Regenerate();

                // 4c-ii. Dedicated Boolean Void Cut Pass: carve out inner bowl cavities, tap holes, drain holes
                if (createdVoids.Count > 0 && createdSolids.Count > 0)
                {
                    onLog?.Invoke($"[REVIT ENGINE] Applying boolean void cuts ({createdVoids.Count} void(s) against {createdSolids.Count} solid(s))...");
                    int successfulCuts = 0;
                    foreach (var voidElem in createdVoids)
                    {
                        ICollection<ElementId>? existingCutSolids = null;
                        try { existingCutSolids = SolidSolidCutUtils.GetSolidsBeingCut(voidElem); } catch { }

                        foreach (var solidElem in createdSolids)
                        {
                            // 1. Check if Revit's engine already cut this solid upon creation
                            if (existingCutSolids != null && existingCutSolids.Contains(solidElem.Id))
                            {
                                successfulCuts++;
                                onLog?.Invoke($"[Void Cut VERIFIED] '{voidElem.Name}' is actively cutting '{solidElem.Name}'.");
                                continue;
                            }

                            bool cutSucceeded = false;
                            // 2. Try AddCutBetweenSolids
                            try
                            {
                                SolidSolidCutUtils.AddCutBetweenSolids(masterDoc, solidElem, voidElem);
                                successfulCuts++;
                                cutSucceeded = true;
                                onLog?.Invoke($"[Void Cut SUCCESS] '{voidElem.Name}' cleanly cut solid '{solidElem.Name}'.");
                            }
                            catch { }

                            // 3. Fallback: try CombineElements
                            if (!cutSucceeded)
                            {
                                try
                                {
                                    var combo = new CombinableElementArray();
                                    combo.Append(solidElem);
                                    combo.Append(voidElem);
                                    masterDoc.CombineElements(combo);
                                    successfulCuts++;
                                    cutSucceeded = true;
                                    onLog?.Invoke($"[CombineElements Cut] '{voidElem.Name}' combined with '{solidElem.Name}'.");
                                }
                                catch { }
                            }
                        }
                    }
                    onLog?.Invoke($"[REVIT ENGINE] Void cutting complete. {successfulCuts} boolean intersection cut(s) verified.");
                    masterDoc.Regenerate();
                }

                // 4d. Create Wall / Ceiling Opening Cuts (for Doors, Windows, hosted penetrations)
                CreateHostOpenings(masterDoc, schema.HostOpenings);

                // 4e. Create 2D Symbolic Linework (Plan Swing, Schematic Linework)
                CreateSymbolicCurves(masterDoc, sketchPlane, schema.SymbolicCurves);

                // 4f. Attach MEP System Connectors (Pipes, Ducts, Electrical)
                CreateMepConnectors(masterDoc, schema.MepConnectors, createdSolids);

                masterDoc.Regenerate();

                // 5. Load and Place Nested Families + Associate Parameters
                if (schema.NestedFamilies != null)
                {
                    foreach (var nestedDef in schema.NestedFamilies)
                    {
                        try
                        {
                            if (nestedRfaPaths.TryGetValue(nestedDef.NestedFamilyName, out string? nestedRfaPath) && File.Exists(nestedRfaPath))
                            {
                                PlaceAndBindNestedFamily(masterDoc, famMgr, nestedDef, nestedRfaPath, paramDict, onLog);
                            }
                        }
                        catch (Exception ex)
                        {
                            onLog?.Invoke($"[Nested Component Error] '{nestedDef.NestedFamilyName}': {ex.Message}");
                        }
                    }
                }

                masterDoc.Regenerate();

                // 6. Multi-Type Catalog Generation (Create all defined Family Types)
                if (schema.FamilyTypes != null && schema.FamilyTypes.Count > 0)
                {
                    FamilyType? primaryType = null;
                    foreach (var typeDef in schema.FamilyTypes)
                    {
                        if (string.IsNullOrWhiteSpace(typeDef.Name)) continue;
                        try
                        {
                            FamilyType fType = famMgr.NewType(typeDef.Name);
                            primaryType ??= fType;
                            famMgr.CurrentType = fType;

                            if (typeDef.ParameterValues != null)
                            {
                                foreach (var kvp in typeDef.ParameterValues)
                                {
                                    if (paramDict.TryGetValue(kvp.Key, out FamilyParameter? targetParam) && targetParam != null && !targetParam.IsReadOnly)
                                    {
                                        ApplyDynamicValue(famMgr, targetParam, kvp.Value);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (primaryType != null)
                    {
                        try { famMgr.CurrentType = primaryType; } catch { }
                    }
                }

                // 6b. Automated Parametric Flex Verification across catalog types
                VerifyFamilyFlexing(masterDoc, schema, onLog);

                // 6c. Closed-Loop In-Revit BRep Geometry Verification & Auto-Healing (Delta <= 2mm check)
                GeometryVerifier.VerifyAndSelfHeal(masterDoc, schema, onLog);

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

            // 7. Load Master Family into Active Project Document (if one is open)
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

        private static void SetParameterDefaultValue(
            FamilyManager famMgr, 
            FamilyParameter famParam, 
            MasterParameterDef paramDef, 
            ForgeTypeId specType)
        {
            try
            {
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
            }
            catch { }
        }

        private static void ApplyDynamicValue(FamilyManager famMgr, FamilyParameter targetParam, object rawValue)
        {
            try
            {
                ForgeTypeId spec = targetParam.Definition.GetDataType();
                double numVal = 0.0;
                bool hasNum = false;

                if (rawValue is JsonElement je)
                {
                    if (je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out double d))
                    {
                        numVal = d;
                        hasNum = true;
                    }
                    else if (je.ValueKind == JsonValueKind.True)
                    {
                        numVal = 1;
                        hasNum = true;
                    }
                    else if (je.ValueKind == JsonValueKind.False)
                    {
                        numVal = 0;
                        hasNum = true;
                    }
                    else if (je.ValueKind == JsonValueKind.String)
                    {
                        string s = je.GetString() ?? "";
                        if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.Url)
                        {
                            famMgr.Set(targetParam, s);
                            return;
                        }
                        if (double.TryParse(s, out double parsed))
                        {
                            numVal = parsed;
                            hasNum = true;
                        }
                    }
                }
                else if (rawValue is double d)
                {
                    numVal = d;
                    hasNum = true;
                }
                else if (rawValue is int i)
                {
                    numVal = i;
                    hasNum = true;
                }
                else if (rawValue is bool b)
                {
                    numVal = b ? 1 : 0;
                    hasNum = true;
                }
                else if (rawValue is string s)
                {
                    if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.Url)
                    {
                        famMgr.Set(targetParam, s);
                        return;
                    }
                    if (double.TryParse(s, out double parsed))
                    {
                        numVal = parsed;
                        hasNum = true;
                    }
                }

                if (hasNum)
                {
                    if (spec == SpecTypeId.Length)
                    {
                        famMgr.Set(targetParam, MmToFeet(numVal));
                    }
                    else if (spec == SpecTypeId.Angle)
                    {
                        famMgr.Set(targetParam, UnitUtils.ConvertToInternalUnits(numVal, UnitTypeId.Degrees));
                    }
                    else if (spec == SpecTypeId.Boolean.YesNo)
                    {
                        famMgr.Set(targetParam, numVal != 0 ? 1 : 0);
                    }
                    else if (spec == SpecTypeId.Int.Integer)
                    {
                        famMgr.Set(targetParam, (int)numVal);
                    }
                    else
                    {
                        famMgr.Set(targetParam, numVal);
                    }
                }
            }
            catch { }
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
                    bubbleEnd = new XYZ(offsetFeet, -10.0, 0.0);
                    freeEnd = new XYZ(offsetFeet, 10.0, 0.0);
                    cutVec = XYZ.BasisX;
                    targetView = planView;
                }
                else if (string.Equals(rpDef.Orientation, "Y", StringComparison.OrdinalIgnoreCase))
                {
                    bubbleEnd = new XYZ(-10.0, offsetFeet, 0.0);
                    freeEnd = new XYZ(10.0, offsetFeet, 0.0);
                    cutVec = XYZ.BasisY;
                    targetView = planView;
                }
                else
                {
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

        public static CurveArrArray BuildExtrusionProfile(ExtrusionDef extDef, double minX, double maxX, double minY, double maxY)
        {
            CurveArrArray profile = new CurveArrArray();
            string pType = extDef.ProfileType?.Trim().ToLowerInvariant() ?? "rectangle";

            if (pType == "cylinder" || pType == "circle")
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

                if (extDef.WallThickness_mm > 0)
                {
                    double wt = MmToFeet(extDef.WallThickness_mm);
                    double innerRad = radius - wt;
                    if (innerRad > 0.005)
                    {
                        XYZ ipt1 = new XYZ(centerX - innerRad, centerY, 0);
                        XYZ ipt2 = new XYZ(centerX + innerRad, centerY, 0);
                        XYZ imid1 = new XYZ(centerX, centerY + innerRad, 0);
                        XYZ imid2 = new XYZ(centerX, centerY - innerRad, 0);
                        CurveArray innerCircle = new CurveArray();
                        innerCircle.Append(Arc.Create(ipt1, ipt2, imid1));
                        innerCircle.Append(Arc.Create(ipt2, ipt1, imid2));
                        profile.Append(innerCircle);
                    }
                }
            }
            else if (pType == "oval" || pType == "ellipse")
            {
                double centerX = (minX + maxX) / 2.0;
                double centerY = (minY + maxY) / 2.0;
                double radX = Math.Max(Math.Abs(maxX - minX) / 2.0, MmToFeet(5.0));
                double radY = Math.Max(Math.Abs(maxY - minY) / 2.0, MmToFeet(5.0));

                XYZ center = new XYZ(centerX, centerY, 0);
                double majorRad = Math.Max(radX, radY);
                double minorRad = Math.Min(radX, radY);
                XYZ xVec = radX >= radY ? XYZ.BasisX : XYZ.BasisY;
                XYZ yVec = radX >= radY ? XYZ.BasisY : XYZ.BasisX;

                CurveArray ovalLoop = new CurveArray();
                ovalLoop.Append(Ellipse.CreateCurve(center, majorRad, minorRad, xVec, yVec, 0, Math.PI));
                ovalLoop.Append(Ellipse.CreateCurve(center, majorRad, minorRad, xVec, yVec, Math.PI, 2 * Math.PI));
                profile.Append(ovalLoop);
            }
            else if (pType == "dshape" || pType == "basin" || pType == "bowfront" || pType == "pedestal")
            {
                // Flat rear wall line at maxY, straight shoulders, and smooth curved front bow sweeping to minY
                double ySplit = maxY - (maxY - minY) * 0.40;
                if (ySplit <= minY + MmToFeet(2.0)) ySplit = (minY + maxY) / 2.0;

                XYZ p1 = new XYZ(minX, ySplit, 0);
                XYZ p2 = new XYZ(minX, maxY, 0);
                XYZ p3 = new XYZ(maxX, maxY, 0);
                XYZ p4 = new XYZ(maxX, ySplit, 0);
                XYZ pMid = new XYZ((minX + maxX) / 2.0, minY, 0);

                CurveArray dLoop = new CurveArray();
                dLoop.Append(Line.CreateBound(p1, p2));
                dLoop.Append(Line.CreateBound(p2, p3));
                dLoop.Append(Line.CreateBound(p3, p4));
                dLoop.Append(Arc.Create(p4, p1, pMid));
                profile.Append(dLoop);

                // If hollow or wall thickness specified, bake the inner bowl cavity loop directly into the sketch!
                if (extDef.WallThickness_mm > 0 || pType.Contains("hollow"))
                {
                    double wt = extDef.WallThickness_mm > 0 ? MmToFeet(extDef.WallThickness_mm) : MmToFeet(35.0);
                    double rearLedge = MmToFeet(85.0); // leaves 85mm solid rear deck for faucet mounting

                    double iMinX = minX + wt;
                    double iMaxX = maxX - wt;
                    double iMinY = minY + wt;
                    double iMaxY = maxY - rearLedge;

                    if (iMaxX > iMinX + MmToFeet(20.0) && iMaxY > iMinY + MmToFeet(20.0))
                    {
                        double iYSplit = iMaxY - (iMaxY - iMinY) * 0.40;
                        if (iYSplit <= iMinY + MmToFeet(2.0)) iYSplit = (iMinY + iMaxY) / 2.0;

                        XYZ ip1 = new XYZ(iMinX, iYSplit, 0);
                        XYZ ip2 = new XYZ(iMinX, iMaxY, 0);
                        XYZ ip3 = new XYZ(iMaxX, iMaxY, 0);
                        XYZ ip4 = new XYZ(iMaxX, iYSplit, 0);
                        XYZ ipMid = new XYZ((iMinX + iMaxX) / 2.0, iMinY, 0);

                        CurveArray innerLoop = new CurveArray();
                        innerLoop.Append(Line.CreateBound(ip1, ip2));
                        innerLoop.Append(Line.CreateBound(ip2, ip3));
                        innerLoop.Append(Line.CreateBound(ip3, ip4));
                        innerLoop.Append(Arc.Create(ip4, ip1, ipMid));
                        profile.Append(innerLoop);
                    }
                }
            }
            else if (pType == "roundedrectangle" || pType == "fillet" || pType == "filletbox" || extDef.CornerRadius_mm > 0)
            {
                double rMm = extDef.CornerRadius_mm > 0 ? extDef.CornerRadius_mm : (extDef.Radius_mm > 0 ? extDef.Radius_mm : 25.0);
                double r = MmToFeet(rMm);
                r = Math.Min(r, Math.Min(Math.Abs(maxX - minX) * 0.45, Math.Abs(maxY - minY) * 0.45));

                if (r <= MmToFeet(2.0))
                {
                    CurveArray rectLoop = new CurveArray();
                    rectLoop.Append(Line.CreateBound(new XYZ(minX, minY, 0), new XYZ(maxX, minY, 0)));
                    rectLoop.Append(Line.CreateBound(new XYZ(maxX, minY, 0), new XYZ(maxX, maxY, 0)));
                    rectLoop.Append(Line.CreateBound(new XYZ(maxX, maxY, 0), new XYZ(minX, maxY, 0)));
                    rectLoop.Append(Line.CreateBound(new XYZ(minX, maxY, 0), new XYZ(minX, minY, 0)));
                    profile.Append(rectLoop);
                }
                else
                {
                    CurveArray filletLoop = new CurveArray();
                    filletLoop.Append(Line.CreateBound(new XYZ(minX + r, minY, 0), new XYZ(maxX - r, minY, 0)));
                    filletLoop.Append(Arc.Create(new XYZ(maxX - r, minY + r, 0), r, 1.5 * Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    filletLoop.Append(Line.CreateBound(new XYZ(maxX, minY + r, 0), new XYZ(maxX, maxY - r, 0)));
                    filletLoop.Append(Arc.Create(new XYZ(maxX - r, maxY - r, 0), r, 0.0, 0.5 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    filletLoop.Append(Line.CreateBound(new XYZ(maxX - r, maxY, 0), new XYZ(minX + r, maxY, 0)));
                    filletLoop.Append(Arc.Create(new XYZ(minX + r, maxY - r, 0), r, 0.5 * Math.PI, 1.0 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    filletLoop.Append(Line.CreateBound(new XYZ(minX, maxY - r, 0), new XYZ(minX, minY + r, 0)));
                    filletLoop.Append(Arc.Create(new XYZ(minX + r, minY + r, 0), r, 1.0 * Math.PI, 1.5 * Math.PI, XYZ.BasisX, XYZ.BasisY));
                    profile.Append(filletLoop);
                }
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

            return profile;
        }

        private static void CreateHostExtrusion(
            Document doc,
            FamilyManager famMgr,
            SketchPlane sketchPlane,
            ExtrusionDef extDef,
            Dictionary<string, FamilyParameter> paramDict,
            List<CombinableElement> createdSolids,
            List<CombinableElement> createdVoids,
            Action<string>? onLog = null)
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

            CurveArrArray profile = BuildExtrusionProfile(extDef, minX, maxX, minY, maxY);

            bool isSolid = !extDef.IsVoid;
            // In Revit Family Editor, sketchPlane is at Z=0.
            // By passing initialEnd = Math.Max(endZ, MmToFeet(5.0)), Revit creates the extrusion with End already at endZ.
            // This prevents the critical Revit bug where startParam > initialEnd fails silently!
            double initialEnd = Math.Max(endZ, MmToFeet(5.0));
            Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                isSolid,
                profile,
                sketchPlane,
                initialEnd
            );

            Parameter startParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
            Parameter endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);

            // CRITICAL REVIT API RULE: EXTRUSION_START_PARAM CANNOT BE GREATER THAN EXTRUSION_END_PARAM.
            // If endZ >= startZ, set endParam FIRST (expanding upper limit), then set startParam.
            if (endZ >= startZ)
            {
                if (endParam != null && !endParam.IsReadOnly) endParam.Set(endZ);
                if (startParam != null && !startParam.IsReadOnly) startParam.Set(startZ);
            }
            else
            {
                if (startParam != null && !startParam.IsReadOnly) startParam.Set(startZ);
                if (endParam != null && !endParam.IsReadOnly) endParam.Set(endZ);
            }

            if (!isSolid)
            {
                createdVoids.Add(extrusion);
                onLog?.Invoke($"[Host Extrusion] Created VOID '{extDef.Name}' ({extDef.ProfileType}) Z:[{extDef.StartZ_mm:0}..{extDef.EndZ_mm:0}]mm");
            }
            else
            {
                createdSolids.Add(extrusion);
                onLog?.Invoke($"[Host Extrusion] Created SOLID '{extDef.Name}' ({extDef.ProfileType}) Z:[{extDef.StartZ_mm:0}..{extDef.EndZ_mm:0}]mm");
            }

            // Link height parameter if specified
            if (!string.IsNullOrEmpty(extDef.HeightParameter) && paramDict.TryGetValue(extDef.HeightParameter!, out FamilyParameter? heightFamParam))
            {
                if (endParam != null && !endParam.IsReadOnly && heightFamParam != null)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(endParam, heightFamParam); } catch { }
                }
            }

            // Link material parameter if specified
            if (!string.IsNullOrEmpty(extDef.MaterialParameter))
            {
                string matName = extDef.MaterialParameter!;
                FamilyParameter? matFamParam = famMgr.get_Parameter(matName);
                if (matFamParam == null)
                {
                    try
                    {
                        matFamParam = famMgr.AddParameter(
                            matName,
                            GroupTypeId.Materials,
                            SpecTypeId.Reference.Material,
                            false
                        );
                        paramDict[matName] = matFamParam;
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

            // Link visibility Yes/No parameter if specified
            if (!string.IsNullOrEmpty(extDef.VisibilityParameter))
            {
                string visName = extDef.VisibilityParameter!;
                FamilyParameter? visFamParam = famMgr.get_Parameter(visName);
                if (visFamParam == null)
                {
                    try
                    {
                        visFamParam = famMgr.AddParameter(
                            visName,
                            GroupTypeId.Visibility,
                            SpecTypeId.Boolean.YesNo,
                            false
                        );
                        famMgr.Set(visFamParam, 1);
                        paramDict[visName] = visFamParam;
                    }
                    catch { }
                }

                if (visFamParam != null)
                {
                    Parameter visParam = extrusion.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (visParam != null && !visParam.IsReadOnly)
                    {
                        try { famMgr.AssociateElementParameterToFamilyParameter(visParam, visFamParam); } catch { }
                    }
                }
            }

            if (!string.IsNullOrEmpty(extDef.Subcategory))
            {
                Category? subcat = EnsureSubcategory(doc, extDef.Subcategory);
                if (subcat != null)
                {
                    try { extrusion.Subcategory = subcat; } catch { }
                }
            }

            try
            {
                FamilyElementVisibility vis = new FamilyElementVisibility(FamilyElementVisibilityType.Model);
                vis.IsShownInCoarse = extDef.VisibleInCoarse;
                vis.IsShownInMedium = true;
                vis.IsShownInFine = true;
                vis.IsShownInPlanRCPCut = extDef.VisibleInPlan;
                extrusion.SetVisibility(vis);
            }
            catch { }
        }

        public static Category? EnsureSubcategory(Document doc, string? subcatName)
        {
            if (string.IsNullOrWhiteSpace(subcatName)) return null;
            try
            {
                Category? parentCat = doc.OwnerFamily?.FamilyCategory;
                if (parentCat == null) return null;

                Category? existing = parentCat.SubCategories
                    .Cast<Category>()
                    .FirstOrDefault(c => string.Equals(c.Name, subcatName, StringComparison.OrdinalIgnoreCase));

                if (existing != null) return existing;

                return doc.Settings.Categories.NewSubcategory(parentCat, subcatName);
            }
            catch
            {
                return null;
            }
        }

        public static double EvaluateExpression(string? expr, Dictionary<string, double>? paramValues = null)
        {
            if (string.IsNullOrWhiteSpace(expr)) return 0.0;
            if (double.TryParse(expr, NumberStyles.Any, CultureInfo.InvariantCulture, out double directVal))
                return directVal;

            string evaluated = expr!;
            if (paramValues != null)
            {
                foreach (var kvp in paramValues.OrderByDescending(k => k.Key.Length))
                {
                    evaluated = evaluated.Replace(kvp.Key, kvp.Value.ToString(CultureInfo.InvariantCulture));
                }
            }

            try
            {
                using var dt = new DataTable();
                object result = dt.Compute(evaluated, "");
                return Convert.ToDouble(result, CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0.0;
            }
        }

        public static CurveArray BuildBlendProfileLoop(string? profileType, double minX, double maxX, double minY, double maxY, double radiusFeet)
        {
            CurveArray loop = new CurveArray();
            string pType = profileType?.Trim().ToLowerInvariant() ?? "rectangle";

            double midX = (minX + maxX) / 2.0;
            double midY = (minY + maxY) / 2.0;

            if (pType == "cylinder" || pType == "circle")
            {
                double r = radiusFeet > 0 ? radiusFeet : Math.Max(Math.Abs(maxX - minX) / 2.0, MmToFeet(5.0));
                List<XYZ> pts = new List<XYZ>();
                for (int i = 0; i < 8; i++)
                {
                    double angle = 2.0 * Math.PI * i / 8.0;
                    pts.Add(new XYZ(midX + r * Math.Cos(angle), midY + r * Math.Sin(angle), 0));
                }
                for (int i = 0; i < 8; i++)
                {
                    int next = (i + 1) % 8;
                    loop.Append(Line.CreateBound(pts[i], pts[next]));
                }
            }
            else if (pType == "oval" || pType == "ellipse")
            {
                double rx = Math.Max(Math.Abs(maxX - minX) / 2.0, MmToFeet(5.0));
                double ry = Math.Max(Math.Abs(maxY - minY) / 2.0, MmToFeet(5.0));
                List<XYZ> pts = new List<XYZ>();
                for (int i = 0; i < 8; i++)
                {
                    double angle = 2.0 * Math.PI * i / 8.0;
                    pts.Add(new XYZ(midX + rx * Math.Cos(angle), midY + ry * Math.Sin(angle), 0));
                }
                for (int i = 0; i < 8; i++)
                {
                    int next = (i + 1) % 8;
                    loop.Append(Line.CreateBound(pts[i], pts[next]));
                }
            }
            else if (pType == "dshape" || pType == "basin" || pType == "bowfront" || pType == "pedestal")
            {
                double ySplit = maxY - (maxY - minY) * 0.40;
                if (ySplit <= minY + MmToFeet(2.0)) ySplit = (minY + maxY) / 2.0;

                XYZ p0 = new XYZ(minX, ySplit, 0);
                XYZ p1 = new XYZ(minX, maxY, 0);
                XYZ p2 = new XYZ(midX, maxY, 0);
                XYZ p3 = new XYZ(maxX, maxY, 0);
                XYZ p4 = new XYZ(maxX, ySplit, 0);
                
                double bowW = (maxX - minX) / 2.0;
                XYZ p5 = new XYZ(maxX - bowW * 0.3, minY + (ySplit - minY) * 0.3, 0);
                XYZ p6 = new XYZ(midX, minY, 0);
                XYZ p7 = new XYZ(minX + bowW * 0.3, minY + (ySplit - minY) * 0.3, 0);

                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p4));
                loop.Append(Line.CreateBound(p4, p5));
                loop.Append(Line.CreateBound(p5, p6));
                loop.Append(Line.CreateBound(p6, p7));
                loop.Append(Line.CreateBound(p7, p0));
            }
            else
            {
                XYZ p0 = new XYZ(minX, minY, 0);
                XYZ p1 = new XYZ(midX, minY, 0);
                XYZ p2 = new XYZ(maxX, minY, 0);
                XYZ p3 = new XYZ(maxX, midY, 0);
                XYZ p4 = new XYZ(maxX, maxY, 0);
                XYZ p5 = new XYZ(midX, maxY, 0);
                XYZ p6 = new XYZ(minX, maxY, 0);
                XYZ p7 = new XYZ(minX, midY, 0);

                loop.Append(Line.CreateBound(p0, p1));
                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p4));
                loop.Append(Line.CreateBound(p4, p5));
                loop.Append(Line.CreateBound(p5, p6));
                loop.Append(Line.CreateBound(p6, p7));
                loop.Append(Line.CreateBound(p7, p0));
            }

            return loop;
        }

        private static void CreateHostBlend(
            Document doc,
            Autodesk.Revit.ApplicationServices.Application app,
            FamilyManager famMgr,
            SketchPlane sketchPlane,
            BlendDef blendDef,
            Dictionary<string, FamilyParameter> paramDict,
            List<CombinableElement> createdSolids,
            List<CombinableElement> createdVoids,
            Action<string>? onLog = null)
        {
            double bMinX = MmToFeet(blendDef.BottomMinX_mm);
            double bMaxX = MmToFeet(blendDef.BottomMaxX_mm);
            double bMinY = MmToFeet(blendDef.BottomMinY_mm);
            double bMaxY = MmToFeet(blendDef.BottomMaxY_mm);
            double bRad = blendDef.BottomRadius_mm > 0 ? MmToFeet(blendDef.BottomRadius_mm) : 0;

            double tMinX = MmToFeet(blendDef.TopMinX_mm);
            double tMaxX = MmToFeet(blendDef.TopMaxX_mm);
            double tMinY = MmToFeet(blendDef.TopMinY_mm);
            double tMaxY = MmToFeet(blendDef.TopMaxY_mm);
            double tRad = blendDef.TopRadius_mm > 0 ? MmToFeet(blendDef.TopRadius_mm) : 0;

            if (bMaxX <= bMinX) bMaxX = bMinX + MmToFeet(10.0);
            if (bMaxY <= bMinY) bMaxY = bMinY + MmToFeet(10.0);
            if (tMaxX <= tMinX) tMaxX = tMinX + MmToFeet(10.0);
            if (tMaxY <= tMinY) tMaxY = tMinY + MmToFeet(10.0);

            CurveArray bottomProfile = BuildBlendProfileLoop(blendDef.BottomProfileType, bMinX, bMaxX, bMinY, bMaxY, bRad);
            CurveArray topProfile = BuildBlendProfileLoop(blendDef.TopProfileType, tMinX, tMaxX, tMinY, tMaxY, tRad);

            double bOffsetFeet = MmToFeet(blendDef.BottomOffset_mm);
            double tOffsetFeet = MmToFeet(blendDef.TopOffset_mm);

            bool isSolid = !blendDef.IsVoid;
            Blend blend = doc.FamilyCreate.NewBlend(
                isSolid,
                topProfile,
                bottomProfile,
                sketchPlane
            );

            double maxOff = Math.Max(tOffsetFeet, bOffsetFeet);
            double minOff = Math.Min(tOffsetFeet, bOffsetFeet);
            if (maxOff <= minOff) maxOff = minOff + MmToFeet(5.0);

            // In Revit API, always expand TopOffset first so BottomOffset never exceeds TopOffset
            blend.TopOffset = maxOff;
            blend.BottomOffset = minOff;

            if (!isSolid)
            {
                createdVoids.Add(blend);
                onLog?.Invoke($"[Host Blend] Created VOID '{blendDef.Name}' Z:[{blendDef.BottomOffset_mm:0}..{blendDef.TopOffset_mm:0}]mm");
            }
            else
            {
                createdSolids.Add(blend);
                onLog?.Invoke($"[Host Blend] Created SOLID '{blendDef.Name}' Z:[{blendDef.BottomOffset_mm:0}..{blendDef.TopOffset_mm:0}]mm");
            }

            if (!string.IsNullOrEmpty(blendDef.Subcategory))
            {
                Category? subcat = EnsureSubcategory(doc, blendDef.Subcategory);
                if (subcat != null)
                {
                    try { blend.Subcategory = subcat; } catch { }
                }
            }

            if (!string.IsNullOrEmpty(blendDef.MaterialParameter))
            {
                string matName = blendDef.MaterialParameter!;
                FamilyParameter? matFamParam = famMgr.get_Parameter(matName);
                if (matFamParam == null)
                {
                    try
                    {
                        matFamParam = famMgr.AddParameter(matName, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                        paramDict[matName] = matFamParam;
                    }
                    catch { }
                }
                if (matFamParam != null)
                {
                    Parameter matParam = blend.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (matParam != null && !matParam.IsReadOnly)
                    {
                        try { famMgr.AssociateElementParameterToFamilyParameter(matParam, matFamParam); } catch { }
                    }
                }
            }

            if (!string.IsNullOrEmpty(blendDef.VisibilityParameter))
            {
                string visName = blendDef.VisibilityParameter!;
                FamilyParameter? visFamParam = famMgr.get_Parameter(visName);
                if (visFamParam == null)
                {
                    try
                    {
                        visFamParam = famMgr.AddParameter(visName, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, false);
                        famMgr.Set(visFamParam, 1);
                        paramDict[visName] = visFamParam;
                    }
                    catch { }
                }
                if (visFamParam != null)
                {
                    Parameter visParam = blend.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (visParam != null && !visParam.IsReadOnly)
                    {
                        try { famMgr.AssociateElementParameterToFamilyParameter(visParam, visFamParam); } catch { }
                    }
                }
            }
        }

        private static void CreateHostSweep(
            Document doc,
            Autodesk.Revit.ApplicationServices.Application app,
            FamilyManager famMgr,
            SketchPlane sketchPlane,
            SweepDef sweepDef,
            Dictionary<string, FamilyParameter> paramDict,
            List<CombinableElement> createdSolids,
            List<CombinableElement> createdVoids,
            Action<string>? onLog = null)
        {
            var pathPoints = sweepDef.PathPoints_mm;
            if (pathPoints == null || pathPoints.Count < 2) return;

            var pts = pathPoints.Select(p => new XYZ(MmToFeet(p.X), MmToFeet(p.Y), MmToFeet(p.Z))).ToList();
            var modelCurves = new List<ModelCurve>();

            for (int i = 0; i < pts.Count - 1; i++)
            {
                XYZ pA = pts[i];
                XYZ pB = pts[i + 1];
                if (pA.DistanceTo(pB) < 0.005) continue;

                XYZ dir = (pB - pA).Normalize();
                XYZ norm = dir.CrossProduct(XYZ.BasisZ);
                if (norm.GetLength() < 0.001)
                {
                    norm = dir.CrossProduct(XYZ.BasisY);
                }
                norm = norm.CrossProduct(dir).Normalize();

                Plane linePlane = Plane.CreateByNormalAndOrigin(norm, pA);
                SketchPlane lineSketch = SketchPlane.Create(doc, linePlane);
                Line line = Line.CreateBound(pA, pB);
                ModelCurve mc = doc.FamilyCreate.NewModelCurve(line, lineSketch);
                modelCurves.Add(mc);
            }

            if (modelCurves.Count == 0) return;
            doc.Regenerate();

            ReferenceArray pathRefArray = new ReferenceArray();
            foreach (var mc in modelCurves)
            {
                Reference? curveRef = mc.GeometryCurve?.Reference;
                if (curveRef != null)
                {
                    pathRefArray.Append(curveRef);
                }
                else
                {
                    Options opts = new Options { ComputeReferences = true };
                    GeometryElement geomElem = mc.get_Geometry(opts);
                    if (geomElem != null)
                    {
                        foreach (GeometryObject geomObj in geomElem)
                        {
                            if (geomObj is Curve c && c.Reference != null)
                            {
                                pathRefArray.Append(c.Reference);
                                break;
                            }
                        }
                    }
                }
            }

            if (pathRefArray.Size == 0)
            {
                onLog?.Invoke($"[Host Sweep Warning] Could not obtain geometry references for sweep path '{sweepDef.Name}'.");
                return;
            }

            XYZ startPt = pts[0];
            XYZ tangent = (pts[1] - pts[0]).Normalize();
            XYZ basisU = tangent.CrossProduct(XYZ.BasisZ);
            if (basisU.GetLength() < 0.001)
            {
                basisU = tangent.CrossProduct(XYZ.BasisY);
            }
            basisU = basisU.Normalize();
            XYZ basisV = tangent.CrossProduct(basisU).Normalize();

            CurveArrArray profileArr = new CurveArrArray();
            CurveArray profileCurves = new CurveArray();

            string pType = sweepDef.ProfileType?.Trim().ToLowerInvariant() ?? "circle";
            if (pType == "rectangle")
            {
                double w = MmToFeet(sweepDef.ProfileWidth_mm > 0 ? sweepDef.ProfileWidth_mm : 30.0);
                double h = MmToFeet(sweepDef.ProfileHeight_mm > 0 ? sweepDef.ProfileHeight_mm : 30.0);
                XYZ c0 = startPt - basisU * (w / 2.0) - basisV * (h / 2.0);
                XYZ c1 = c0 + basisU * w;
                XYZ c2 = c1 + basisV * h;
                XYZ c3 = c0 + basisV * h;
                profileCurves.Append(Line.CreateBound(c0, c1));
                profileCurves.Append(Line.CreateBound(c1, c2));
                profileCurves.Append(Line.CreateBound(c2, c3));
                profileCurves.Append(Line.CreateBound(c3, c0));
            }
            else
            {
                double r = MmToFeet(sweepDef.ProfileRadius_mm > 0 ? sweepDef.ProfileRadius_mm : 15.0);
                XYZ ptTop = startPt + basisV * r;
                XYZ ptBottom = startPt - basisV * r;
                XYZ ptRight = startPt + basisU * r;
                XYZ ptLeft = startPt - basisU * r;
                profileCurves.Append(Arc.Create(ptTop, ptBottom, ptRight));
                profileCurves.Append(Arc.Create(ptBottom, ptTop, ptLeft));
            }

            profileArr.Append(profileCurves);
            SweepProfile sweepProfile = app.Create.NewCurveLoopsProfile(profileArr);

            bool isSolid = !sweepDef.IsVoid;
            Sweep sweep = doc.FamilyCreate.NewSweep(
                isSolid,
                pathRefArray,
                sweepProfile,
                0,
                ProfilePlaneLocation.Start
            );

            if (!isSolid)
            {
                createdVoids.Add(sweep);
                onLog?.Invoke($"[Host Sweep] Created VOID '{sweepDef.Name}'");
            }
            else
            {
                createdSolids.Add(sweep);
                onLog?.Invoke($"[Host Sweep] Created SOLID '{sweepDef.Name}'");
            }

            if (!string.IsNullOrEmpty(sweepDef.Subcategory))
            {
                Category? subcat = EnsureSubcategory(doc, sweepDef.Subcategory);
                if (subcat != null)
                {
                    try { sweep.Subcategory = subcat; } catch { }
                }
            }

            if (!string.IsNullOrEmpty(sweepDef.MaterialParameter))
            {
                string matName = sweepDef.MaterialParameter!;
                FamilyParameter? matFamParam = famMgr.get_Parameter(matName);
                if (matFamParam == null)
                {
                    try
                    {
                        matFamParam = famMgr.AddParameter(matName, GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
                        paramDict[matName] = matFamParam;
                    }
                    catch { }
                }
                if (matFamParam != null)
                {
                    Parameter matParam = sweep.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                    if (matParam != null && !matParam.IsReadOnly)
                    {
                        try { famMgr.AssociateElementParameterToFamilyParameter(matParam, matFamParam); } catch { }
                    }
                }
            }

            if (!string.IsNullOrEmpty(sweepDef.VisibilityParameter))
            {
                string visName = sweepDef.VisibilityParameter!;
                FamilyParameter? visFamParam = famMgr.get_Parameter(visName);
                if (visFamParam == null)
                {
                    try
                    {
                        visFamParam = famMgr.AddParameter(visName, GroupTypeId.Visibility, SpecTypeId.Boolean.YesNo, false);
                        famMgr.Set(visFamParam, 1);
                        paramDict[visName] = visFamParam;
                    }
                    catch { }
                }
                if (visFamParam != null)
                {
                    Parameter visParam = sweep.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (visParam != null && !visParam.IsReadOnly)
                    {
                        try { famMgr.AssociateElementParameterToFamilyParameter(visParam, visFamParam); } catch { }
                    }
                }
            }
        }

        private static void CreateHostOpenings(Document doc, List<OpeningDef>? openings)
        {
            if (openings == null || openings.Count == 0) return;

            Wall? hostWall = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .FirstOrDefault();

            if (hostWall == null) return;

            foreach (var op in openings)
            {
                try
                {
                    double minX = MmToFeet(op.MinX_mm);
                    double maxX = MmToFeet(op.MaxX_mm);
                    double minZ = MmToFeet(op.MinZ_mm);
                    double maxZ = MmToFeet(op.MaxZ_mm);

                    if (maxX <= minX) maxX = minX + MmToFeet(50.0);
                    if (maxZ <= minZ) maxZ = minZ + MmToFeet(50.0);

                    CurveArray rect = new CurveArray();
                    XYZ p0 = new XYZ(minX, 0, minZ);
                    XYZ p1 = new XYZ(maxX, 0, minZ);
                    XYZ p2 = new XYZ(maxX, 0, maxZ);
                    XYZ p3 = new XYZ(minX, 0, maxZ);

                    rect.Append(Line.CreateBound(p0, p1));
                    rect.Append(Line.CreateBound(p1, p2));
                    rect.Append(Line.CreateBound(p2, p3));
                    rect.Append(Line.CreateBound(p3, p0));

                    doc.FamilyCreate.NewOpening(hostWall, rect);
                }
                catch { }
            }
        }

        private static void CreateSymbolicCurves(Document doc, SketchPlane sketchPlane, List<SymbolicCurveDef>? curves)
        {
            if (curves == null || curves.Count == 0) return;

            foreach (var sc in curves)
            {
                try
                {
                    Curve? c = null;
                    if (string.Equals(sc.CurveType, "Arc", StringComparison.OrdinalIgnoreCase))
                    {
                        XYZ p0 = new XYZ(MmToFeet(sc.StartX_mm), MmToFeet(sc.StartY_mm), 0);
                        XYZ p1 = new XYZ(MmToFeet(sc.EndX_mm), MmToFeet(sc.EndY_mm), 0);
                        XYZ pMid = new XYZ(MmToFeet(sc.CenterX_mm), MmToFeet(sc.CenterY_mm), 0);
                        if (p0.DistanceTo(p1) > 0.005)
                        {
                            c = Arc.Create(p0, p1, pMid);
                        }
                    }
                    else
                    {
                        XYZ p0 = new XYZ(MmToFeet(sc.StartX_mm), MmToFeet(sc.StartY_mm), 0);
                        XYZ p1 = new XYZ(MmToFeet(sc.EndX_mm), MmToFeet(sc.EndY_mm), 0);
                        if (p0.DistanceTo(p1) > 0.005)
                        {
                            c = Line.CreateBound(p0, p1);
                        }
                    }

                    if (c != null)
                    {
                        SymbolicCurve sym = doc.FamilyCreate.NewSymbolicCurve(c, sketchPlane);
                        if (!string.IsNullOrEmpty(sc.Subcategory))
                        {
                            Category? subcat = EnsureSubcategory(doc, sc.Subcategory);
                            if (subcat != null) { GraphicsStyle? gs = subcat.GetGraphicsStyle(GraphicsStyleType.Projection); if (gs != null) sym.Subcategory = gs; }
                        }
                    }
                }
                catch { }
            }
        }

        private static void CreateMepConnectors(Document doc, List<MepConnectorDef>? connectors, List<CombinableElement> createdSolids)
        {
            if (connectors == null || connectors.Count == 0 || createdSolids == null || createdSolids.Count == 0) return;

            Options opt = new Options { ComputeReferences = true };

            foreach (var mep in connectors)
            {
                try
                {
                    XYZ targetPoint = new XYZ(MmToFeet(mep.OriginOffsetMm.X), MmToFeet(mep.OriginOffsetMm.Y), MmToFeet(mep.OriginOffsetMm.Z));
                    XYZ targetNormal = ParseNormal(mep.Direction);

                    Reference? faceRef = null;
                    PlanarFace? bestFace = null;
                    double bestDist = double.MaxValue;

                    // 1. Find a solid planar face close to the connector origin and normal (avoiding top rim)
                    foreach (var elem in createdSolids)
                    {
                        GeometryElement geom = elem.get_Geometry(opt);
                        if (geom == null) continue;
                        foreach (GeometryObject obj in geom)
                        {
                            if (obj is Solid s && s.Faces.Size > 0)
                            {
                                foreach (Face f in s.Faces)
                                {
                                    if (f is PlanarFace pf && pf.Reference != null)
                                    {
                                        // Ignore faces near the top rim elevation
                                        if (pf.Origin.Z > MmToFeet(800.0) && targetPoint.Z < MmToFeet(750.0)) continue;

                                        double dist = pf.Origin.DistanceTo(targetPoint);
                                        double alignment = pf.FaceNormal.DotProduct(targetNormal);
                                        if (alignment > 0.3)
                                        {
                                            if (dist < bestDist)
                                            {
                                                bestDist = dist;
                                                bestFace = pf;
                                            }
                                        }
                                        else if (bestFace == null && dist < MmToFeet(200.0))
                                        {
                                            bestFace = pf;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (bestFace != null && (bestDist < MmToFeet(400.0) || bestFace.Origin.Z < MmToFeet(750.0)))
                    {
                        faceRef = bestFace.Reference;
                    }

                    if (faceRef == null) continue;

                    string domain = mep.Domain?.ToLowerInvariant() ?? "pipe";
                    if (domain.Contains("pipe"))
                    {
                        PipeSystemType sysType = ResolvePipeSystemType(mep.SystemType);
                        ConnectorElement conn = ConnectorElement.CreatePipeConnector(doc, sysType, faceRef);
                        Parameter radParam = conn.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
                        if (radParam != null && !radParam.IsReadOnly)
                        {
                            radParam.Set(MmToFeet(mep.Diameter_mm / 2.0));
                        }
                    }
                    else if (domain.Contains("elect"))
                    {
                        ElectricalSystemType elecType = ResolveElectricalSystemType(mep.SystemType);
                        ConnectorElement conn = ConnectorElement.CreateElectricalConnector(doc, elecType, faceRef);
                    }
                    else if (domain.Contains("duct"))
                    {
                        DuctSystemType ductType = ResolveDuctSystemType(mep.SystemType);
                        ConnectorElement conn = ConnectorElement.CreateDuctConnector(doc, ductType, ConnectorProfileType.Round, faceRef);
                        Parameter radParam = conn.get_Parameter(BuiltInParameter.CONNECTOR_RADIUS);
                        if (radParam != null && !radParam.IsReadOnly)
                        {
                            radParam.Set(MmToFeet(mep.Diameter_mm / 2.0));
                        }
                    }
                }
                catch { }
            }
        }

        private static XYZ ParseNormal(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return -XYZ.BasisZ;
            string d = dir!.Trim().ToUpperInvariant();
            if (d == "Z" || d == "+Z") return XYZ.BasisZ;
            if (d == "-Z") return -XYZ.BasisZ;
            if (d == "Y" || d == "+Y") return XYZ.BasisY;
            if (d == "-Y") return -XYZ.BasisY;
            if (d == "X" || d == "+X") return XYZ.BasisX;
            if (d == "-X") return -XYZ.BasisX;
            return -XYZ.BasisZ;
        }

        private static PipeSystemType ResolvePipeSystemType(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return PipeSystemType.DomesticColdWater;
            string t = typeName!.ToLowerInvariant();
            if (t.Contains("sanit") || t.Contains("waste") || t.Contains("drain")) return PipeSystemType.Sanitary;
            if (t.Contains("hot")) return PipeSystemType.DomesticHotWater;
            if (t.Contains("cold")) return PipeSystemType.DomesticColdWater;
            if (t.Contains("supply")) return PipeSystemType.SupplyHydronic;
            if (t.Contains("return")) return PipeSystemType.ReturnHydronic;
            if (t.Contains("fire")) return PipeSystemType.FireProtectWet;
            return PipeSystemType.DomesticColdWater;
        }

        private static ElectricalSystemType ResolveElectricalSystemType(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return ElectricalSystemType.PowerCircuit;
            string t = typeName!.ToLowerInvariant();
            if (t.Contains("bal")) return ElectricalSystemType.PowerCircuit;
            if (t.Contains("light")) return ElectricalSystemType.PowerCircuit;
            if (t.Contains("data")) return ElectricalSystemType.Data;
            return ElectricalSystemType.PowerCircuit;
        }

        private static DuctSystemType ResolveDuctSystemType(string? typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName)) return DuctSystemType.SupplyAir;
            string t = typeName!.ToLowerInvariant();
            if (t.Contains("ret")) return DuctSystemType.ReturnAir;
            if (t.Contains("exh")) return DuctSystemType.ExhaustAir;
            return DuctSystemType.SupplyAir;
        }


        private class ForgeFamilyLoadOptions : IFamilyLoadOptions
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

        private static void PlaceAndBindNestedFamily(
            Document masterDoc,
            FamilyManager famMgr,
            NestedFamilyDef nestedDef,
            string nestedRfaPath,
            Dictionary<string, FamilyParameter> paramDict,
            Action<string>? onLog = null)
        {
            Family? loadedNestedFamily = null;
            try
            {
                bool loaded = masterDoc.LoadFamily(nestedRfaPath, new ForgeFamilyLoadOptions(), out loadedNestedFamily);
                onLog?.Invoke($"[Nested Component] LoadFamily '{nestedDef.NestedFamilyName}' -> {(loaded ? "Loaded" : "Resolved")}");
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[Nested Component Info] LoadFamily '{nestedDef.NestedFamilyName}': {ex.Message}");
            }

            if (!File.Exists(nestedRfaPath))
            {
                onLog?.Invoke($"[Nested Component Warning] Nested file not found on disk: {nestedRfaPath}");
            }

            if (loadedNestedFamily == null)
            {
                loadedNestedFamily = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(nestedDef.NestedFamilyName, StringComparison.OrdinalIgnoreCase));
            }

            if (loadedNestedFamily == null)
            {
                onLog?.Invoke($"[Nested Component Error] Family '{nestedDef.NestedFamilyName}' not found in master document.");
                return;
            }

            var symbolIds = loadedNestedFamily.GetFamilySymbolIds();
            FamilySymbol? symbol = null;

            if (symbolIds != null && symbolIds.Count > 0)
            {
                symbol = masterDoc.GetElement(symbolIds.First()) as FamilySymbol;
            }

            if (symbol == null)
            {
                symbol = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>()
                    .FirstOrDefault(s => s.Family != null && s.Family.Id == loadedNestedFamily.Id);
            }

            if (symbol == null)
            {
                onLog?.Invoke($"[Nested Component Error] No FamilySymbol found in family '{nestedDef.NestedFamilyName}'.");
                return;
            }

            if (!symbol.IsActive)
            {
                symbol.Activate();
                masterDoc.Regenerate();
            }

            XYZ origin = new XYZ(
                MmToFeet(nestedDef.PlacementOffsetMm.X),
                MmToFeet(nestedDef.PlacementOffsetMm.Y),
                MmToFeet(nestedDef.PlacementOffsetMm.Z)
            );

            FamilyInstance nestedInstance;
            try
            {
                nestedInstance = masterDoc.FamilyCreate.NewFamilyInstance(
                    origin,
                    symbol,
                    Autodesk.Revit.DB.Structure.StructuralType.NonStructural
                );
                onLog?.Invoke($"[Nested Component SUCCESS] Placed '{nestedDef.NestedFamilyName}' at ({nestedDef.PlacementOffsetMm.X:0}, {nestedDef.PlacementOffsetMm.Y:0}, {nestedDef.PlacementOffsetMm.Z:0})mm.");
            }
            catch (Exception ex)
            {
                onLog?.Invoke($"[Nested Component Error] NewFamilyInstance failed for '{nestedDef.NestedFamilyName}': {ex.Message}");
                return;
            }

            // Parameter Association
            foreach (var kvp in nestedDef.ParameterBindings)
            {
                string nestedParamName = kvp.Key;
                string masterParamName = kvp.Value;

                Parameter? nestedElemParam = nestedInstance.LookupParameter(nestedParamName);
                FamilyParameter? masterFamParam = famMgr.get_Parameter(masterParamName);

                if (masterFamParam == null)
                {
                    try
                    {
                        masterFamParam = famMgr.AddParameter(
                            masterParamName,
                            GroupTypeId.Geometry,
                            SpecTypeId.Length,
                            false
                        );
                        paramDict[masterParamName] = masterFamParam;
                    }
                    catch { }
                }

                if (nestedElemParam != null && masterFamParam != null && !nestedElemParam.IsReadOnly)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(nestedElemParam, masterFamParam); } catch { }
                }
            }

            // Visibility Association
            if (!string.IsNullOrEmpty(nestedDef.VisibilityParameter))
            {
                string visName = nestedDef.VisibilityParameter!;
                Parameter? visParam = nestedInstance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                FamilyParameter? masterVisParam = famMgr.get_Parameter(visName);

                if (masterVisParam == null)
                {
                    try
                    {
                        masterVisParam = famMgr.AddParameter(
                            visName,
                            GroupTypeId.Visibility,
                            SpecTypeId.Boolean.YesNo,
                            false
                        );
                        famMgr.Set(masterVisParam, 1);
                        paramDict[visName] = masterVisParam;
                    }
                    catch { }
                }

                if (visParam != null && !visParam.IsReadOnly && masterVisParam != null)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(visParam, masterVisParam); } catch { }
                }
            }
        }

        private static void CreateHostRevolve(
            Document doc,
            FamilyManager famMgr,
            RevolveDef revDef,
            Dictionary<string, FamilyParameter> paramDict,
            List<CombinableElement> createdSolids,
            List<CombinableElement> createdVoids,
            Action<string>? onLog = null)
        {
            double axisX = MmToFeet(revDef.AxisX_mm);
            double axisY = MmToFeet(revDef.AxisY_mm);
            double startZ = MmToFeet(revDef.StartZ_mm);
            double endZ = MmToFeet(revDef.EndZ_mm);
            double radius = MmToFeet(revDef.Radius_mm > 0 ? revDef.Radius_mm : 150.0);
            double innerRadius = MmToFeet(Math.Max(0.0, revDef.InnerRadius_mm));

            if (endZ <= startZ) endZ = startZ + MmToFeet(50.0);
            if (radius <= MmToFeet(5.0)) radius = MmToFeet(150.0);
            if (innerRadius >= radius) innerRadius = radius * 0.5;

            // Revolution axis is along Z at (axisX, axisY).
            // Plane normal is BasisY, passing through (axisX, axisY, 0).
            Plane revPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(axisX, axisY, 0));
            SketchPlane sketchPlane = SketchPlane.Create(doc, revPlane);

            // Axis line lies strictly in the sketchPlane along the Z direction
            XYZ axisPt1 = new XYZ(axisX, axisY, startZ - MmToFeet(100.0));
            XYZ axisPt2 = new XYZ(axisX, axisY, endZ + MmToFeet(100.0));
            Line axisLine = Line.CreateBound(axisPt1, axisPt2);

            CurveArray loop = new CurveArray();
            string rType = revDef.RevolveType?.Trim().ToLowerInvariant() ?? "bowldish";

            if (rType.Contains("bowl") || rType.Contains("dish") || rType.Contains("cavity"))
            {
                // Organic dished bowl profile:
                // Top outer rim
                XYZ ptRim = new XYZ(axisX + radius, axisY, endZ);
                // Bottom center dish
                XYZ ptBottom = new XYZ(axisX + innerRadius, axisY, startZ);
                // Intermediate control point for smooth spherical / parabolic curvature
                double midX = axisX + (radius + innerRadius) * 0.65;
                double midZ = startZ + (endZ - startZ) * 0.30;
                XYZ ptMid = new XYZ(midX, axisY, midZ);

                loop.Append(Arc.Create(ptRim, ptBottom, ptMid));
                if (innerRadius > 0.001)
                {
                    loop.Append(Line.CreateBound(ptBottom, new XYZ(axisX + innerRadius, axisY, endZ)));
                    loop.Append(Line.CreateBound(new XYZ(axisX + innerRadius, axisY, endZ), ptRim));
                }
                else
                {
                    loop.Append(Line.CreateBound(ptBottom, new XYZ(axisX, axisY, endZ)));
                    loop.Append(Line.CreateBound(new XYZ(axisX, axisY, endZ), ptRim));
                }
            }
            else if (rType.Contains("cone") || rType.Contains("taper"))
            {
                double topR = radius;
                double botR = innerRadius > 0 ? innerRadius : radius * 0.6;
                XYZ p1 = new XYZ(axisX + botR, axisY, startZ);
                XYZ p2 = new XYZ(axisX, axisY, startZ);
                XYZ p3 = new XYZ(axisX, axisY, endZ);
                XYZ p4 = new XYZ(axisX + topR, axisY, endZ);

                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p4));
                loop.Append(Line.CreateBound(p4, p1));
            }
            else
            {
                // Cylinder, bottle trap, column, waste flange
                XYZ p1 = new XYZ(axisX + radius, axisY, startZ);
                XYZ p2 = new XYZ(axisX + innerRadius, axisY, startZ);
                XYZ p3 = new XYZ(axisX + innerRadius, axisY, endZ);
                XYZ p4 = new XYZ(axisX + radius, axisY, endZ);

                loop.Append(Line.CreateBound(p1, p2));
                loop.Append(Line.CreateBound(p2, p3));
                loop.Append(Line.CreateBound(p3, p4));
                loop.Append(Line.CreateBound(p4, p1));
            }

            CurveArrArray profileArr = new CurveArrArray();
            profileArr.Append(loop);

            double startAngle = revDef.StartAngleDeg * Math.PI / 180.0;
            double endAngle = revDef.EndAngleDeg * Math.PI / 180.0;
            if (endAngle <= startAngle) endAngle = startAngle + 2.0 * Math.PI;

            bool isSolid = !revDef.IsVoid;
            Revolution revolution = doc.FamilyCreate.NewRevolution(
                isSolid,
                profileArr,
                sketchPlane,
                axisLine,
                startAngle,
                endAngle
            );

            if (!isSolid)
            {
                createdVoids.Add(revolution);
                onLog?.Invoke($"[Host Revolve] Created VOID '{revDef.Name}' (Cavity Dish Cut)");
            }
            else
            {
                createdSolids.Add(revolution);
                onLog?.Invoke($"[Host Revolve] Created SOLID '{revDef.Name}'");
            }

            if (!string.IsNullOrEmpty(revDef.Subcategory))
            {
                EnsureSubcategory(doc, revDef.Subcategory);
                Category? subCat = doc.Settings?.Categories?.get_Item(doc.OwnerFamily.FamilyCategory?.BuiltInCategory ?? BuiltInCategory.OST_GenericModel)
                    ?.SubCategories?.get_Item(revDef.Subcategory);
                if (subCat != null) revolution.Subcategory = subCat;
            }

            if (!string.IsNullOrEmpty(revDef.MaterialParameter) && revDef.MaterialParameter != null && paramDict.TryGetValue(revDef.MaterialParameter, out FamilyParameter? matParam))
            {
                Parameter? p = revolution.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (p != null && matParam != null)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(p, matParam); } catch { }
                }
            }

            if (!string.IsNullOrEmpty(revDef.VisibilityParameter) && revDef.VisibilityParameter != null && paramDict.TryGetValue(revDef.VisibilityParameter, out FamilyParameter? visParam))
            {
                Parameter? vp = revolution.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                if (vp != null && visParam != null)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(vp, visParam); } catch { }
                }
            }
        }

        public static List<string> VerifyFamilyFlexing(Document doc, FamilySchema schema, Action<string>? onLog = null)
        {
            var results = new List<string>();
            if (schema.FamilyTypes == null || schema.FamilyTypes.Count == 0)
            {
                results.Add("No family types configured for flex testing.");
                return results;
            }

            FamilyManager famMgr = doc.FamilyManager;
            onLog?.Invoke($"[FLEX TEST RIG] Initiating automated parametric flex test across {schema.FamilyTypes.Count} types...");

            foreach (var typeDef in schema.FamilyTypes)
            {
                if (string.IsNullOrWhiteSpace(typeDef.Name)) continue;
                try
                {
                    using (SubTransaction sub = new SubTransaction(doc))
                    {
                        sub.Start();
                        FamilyType? target = famMgr.Types.Cast<FamilyType>()
                            .FirstOrDefault(t => t.Name.Equals(typeDef.Name, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                        {
                            famMgr.CurrentType = target;
                        }

                        if (typeDef.ParameterValues != null)
                        {
                            foreach (var kvp in typeDef.ParameterValues)
                            {
                                FamilyParameter? p = famMgr.get_Parameter(kvp.Key);
                                if (p != null && !p.IsReadOnly)
                                {
                                    ApplyDynamicValue(famMgr, p, kvp.Value);
                                }
                            }
                        }

                        doc.Regenerate();
                        sub.Commit();

                        string ok = $"✔ Type '{typeDef.Name}': Constraints satisfied & geometry regenerated successfully.";
                        results.Add(ok);
                        onLog?.Invoke($"[FLEX TEST] {ok}");
                    }
                }
                catch (Exception ex)
                {
                    string err = $"✘ Type '{typeDef.Name}': Flex constraint violation: {ex.Message}";
                    results.Add(err);
                    onLog?.Invoke($"[FLEX TEST ERROR] {err}");
                }
            }

            return results;
        }
    }

    /// <summary>
    /// Custom IFamilyLoadOptions implementation to overwrite existing family parameters and definitions.
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
