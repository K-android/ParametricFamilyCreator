using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    /// <summary>
    /// Thread-safe inspection service that extracts an authoritative snapshot (FamilyState)
    /// from the active Revit Family document.
    /// </summary>
    public static class FamilyInspectionService
    {
        private const double FeetToMm = 304.8;

        public static FamilyState Inspect(Document? doc)
        {
            var state = new FamilyState();
            if (doc == null)
            {
                return state;
            }

            state.IsFamilyDocument = doc.IsFamilyDocument;
            state.FamilyName = doc.Title;

            if (doc.OwnerFamily != null && doc.OwnerFamily.FamilyCategory != null)
            {
                state.FamilyCategory = doc.OwnerFamily.FamilyCategory.Name;
            }

            if (!doc.IsFamilyDocument)
            {
                return state;
            }

            FamilyManager famMgr = doc.FamilyManager;
            if (famMgr.CurrentType != null)
            {
                state.CurrentTypeName = famMgr.CurrentType.Name;
            }

            // 1. Inspect Parameters
            try
            {
                foreach (FamilyParameter p in famMgr.Parameters)
                {
                    var paramSnap = new ParameterSnapshot
                    {
                        Name = p.Definition.Name,
                        IsInstance = p.IsInstance,
                        IsReadOnly = p.IsReadOnly,
                        Formula = p.Formula
                    };

                    try
                    {
                        paramSnap.DataType = p.Definition.GetDataType().TypeId;
                    }
                    catch
                    {
                        paramSnap.DataType = "Length";
                    }

                    if (famMgr.CurrentType != null)
                    {
                        try
                        {
                            if (p.StorageType == StorageType.Double)
                            {
                                double? val = famMgr.CurrentType.AsDouble(p);
                                if (val.HasValue)
                                {
                                    paramSnap.ValueNumeric = Math.Round(val.Value * FeetToMm, 2);
                                    paramSnap.ValueString = $"{paramSnap.ValueNumeric:0.##} mm";
                                }
                            }
                            else if (p.StorageType == StorageType.Integer)
                            {
                                int? val = famMgr.CurrentType.AsInteger(p);
                                if (val.HasValue)
                                {
                                    paramSnap.ValueNumeric = val.Value;
                                    paramSnap.ValueString = val.Value.ToString();
                                }
                            }
                            else if (p.StorageType == StorageType.String)
                            {
                                paramSnap.ValueString = famMgr.CurrentType.AsString(p) ?? string.Empty;
                            }
                        }
                        catch { }
                    }

                    state.Parameters.Add(paramSnap);
                }
            }
            catch { }

            // 2. Inspect Reference Planes
            try
            {
                var refPlanes = new FilteredElementCollector(doc)
                    .OfClass(typeof(ReferencePlane))
                    .Cast<ReferencePlane>();

                foreach (var rp in refPlanes)
                {
                    try
                    {
                        var bubbleEnd = rp.BubbleEnd;
                        state.ReferencePlanes.Add(new ReferencePlaneSnapshot
                        {
                            Name = rp.Name,
                            Id = (int)rp.Id.Value,
                            BubbleEndX = Math.Round(bubbleEnd.X * FeetToMm, 2),
                            BubbleEndY = Math.Round(bubbleEnd.Y * FeetToMm, 2),
                            BubbleEndZ = Math.Round(bubbleEnd.Z * FeetToMm, 2)
                        });
                    }
                    catch { }
                }
            }
            catch { }

            // 3. Inspect Solids and Voids (Generic Forms)
            try
            {
                var forms = new FilteredElementCollector(doc)
                    .WherePasses(new ElementClassFilter(typeof(GenericForm)))
                    .Cast<GenericForm>();

                var geomOpts = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = true };

                foreach (var form in forms)
                {
                    try
                    {
                        var solidSnap = new SolidSnapshot
                        {
                            Name = form.Name,
                            Id = (int)form.Id.Value,
                            IsVoid = !form.IsSolid,
                            Subcategory = form.Subcategory?.Name
                        };

                        // Extract BoundingBox and Volume
                        var bbox = form.get_BoundingBox(null);
                        if (bbox != null)
                        {
                            solidSnap.MinX_mm = Math.Round(bbox.Min.X * FeetToMm, 2);
                            solidSnap.MaxX_mm = Math.Round(bbox.Max.X * FeetToMm, 2);
                            solidSnap.MinY_mm = Math.Round(bbox.Min.Y * FeetToMm, 2);
                            solidSnap.MaxY_mm = Math.Round(bbox.Max.Y * FeetToMm, 2);
                            solidSnap.MinZ_mm = Math.Round(bbox.Min.Z * FeetToMm, 2);
                            solidSnap.MaxZ_mm = Math.Round(bbox.Max.Z * FeetToMm, 2);
                        }

                        // Check Volume
                        var geomElem = form.get_Geometry(geomOpts);
                        if (geomElem != null)
                        {
                            foreach (var geomObj in geomElem)
                            {
                                if (geomObj is Solid s && s.Volume > 1e-6)
                                {
                                    solidSnap.Volume += s.Volume;
                                }
                            }
                        }

                        state.Solids.Add(solidSnap);
                    }
                    catch { }
                }
            }
            catch { }

            // 4. Inspect Dimensions
            try
            {
                var dims = new FilteredElementCollector(doc)
                    .OfClass(typeof(Dimension))
                    .Cast<Dimension>();

                foreach (var dim in dims)
                {
                    try
                    {
                        var dimSnap = new DimensionSnapshot
                        {
                            Id = (int)dim.Id.Value,
                            IsLocked = dim.IsLocked,
                            LabelParameter = dim.FamilyLabel?.Definition?.Name
                        };

                        if (dim.Value.HasValue)
                        {
                            dimSnap.ValueMm = Math.Round(dim.Value.Value * FeetToMm, 2);
                        }

                        state.Dimensions.Add(dimSnap);
                    }
                    catch { }
                }
            }
            catch { }

            // 5. Inspect Nested Families
            try
            {
                var nestedInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>();

                foreach (var inst in nestedInstances)
                {
                    try
                    {
                        string fn = inst.Symbol?.Family?.Name ?? inst.Name;
                        if (!string.IsNullOrEmpty(fn) && !state.NestedFamilies.Contains(fn))
                        {
                            state.NestedFamilies.Add(fn);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return state;
        }
    }
}
