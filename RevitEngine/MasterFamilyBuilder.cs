using System;
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
            string outputDirectoryPath)
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
                if (schema.HostExtrusions != null)
                {
                    foreach (var extDef in schema.HostExtrusions)
                    {
                        try
                        {
                            CreateHostExtrusion(masterDoc, famMgr, sketchPlane, extDef, paramDict, createdSolids);
                        }
                        catch { }
                    }
                }

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
                                PlaceAndBindNestedFamily(masterDoc, famMgr, nestedDef, nestedRfaPath, paramDict);
                            }
                        }
                        catch { }
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
            List<CombinableElement> createdSolids)
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

            CurveArrArray profile = BuildExtrusionProfile(extDef, minX, maxX, minY, maxY);

            bool isSolid = !extDef.IsVoid;
            Extrusion extrusion = doc.FamilyCreate.NewExtrusion(
                isSolid,
                profile,
                sketchPlane,
                extrusionHeight
            );

            if (!isSolid)
            {
                // Carve out hollow cavities in intersecting solids (e.g. inner wash basin bowl, drain hole, tap hole)
                foreach (var solid in createdSolids)
                {
                    try
                    {
                        if (SolidSolidCutUtils.CanElementCutElement(extrusion, solid, out _))
                        {
                            SolidSolidCutUtils.AddCutBetweenSolids(doc, solid, extrusion);
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
        }

        private static void PlaceAndBindNestedFamily(
            Document masterDoc,
            FamilyManager famMgr,
            NestedFamilyDef nestedDef,
            string nestedRfaPath,
            Dictionary<string, FamilyParameter> paramDict)
        {
            Family? loadedNestedFamily = null;
            bool loaded = masterDoc.LoadFamily(nestedRfaPath, out loadedNestedFamily);
            if (!loaded && loadedNestedFamily == null)
            {
                loadedNestedFamily = new FilteredElementCollector(masterDoc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(nestedDef.NestedFamilyName, StringComparison.OrdinalIgnoreCase));
            }

            if (loadedNestedFamily == null) return;

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

            if (symbol == null) return;

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

            FamilyInstance nestedInstance = masterDoc.FamilyCreate.NewFamilyInstance(
                origin,
                symbol,
                Autodesk.Revit.DB.Structure.StructuralType.NonStructural
            );

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
