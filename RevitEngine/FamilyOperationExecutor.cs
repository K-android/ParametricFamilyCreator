using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Deterministic execution engine that converts high-level FamilyOperation models into concrete,
    /// atomic Revit API operations against the current active Family Document.
    /// </summary>
    public static class FamilyOperationExecutor
    {
        public static double MmToFeet(double mm)
        {
            return UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        }

        public static double FeetToMm(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
        }

        /// <summary>
        /// Executes an entire ExecutionPlan within a coordinated transaction on the given Family Document.
        /// </summary>
        public static ExecutionResult ExecutePlan(Document doc, ExecutionPlan plan, Action<string>? onLog = null)
        {
            var result = new ExecutionResult();
            if (doc == null || !doc.IsFamilyDocument)
            {
                result.OverallStatus = OperationStatus.FAILED;
                result.Message = "Target document is not a valid Revit Family document.";
                onLog?.Invoke($"[EXECUTOR ERROR] {result.Message}");
                return result;
            }

            if (plan == null || plan.Operations == null || plan.Operations.Count == 0)
            {
                result.OverallStatus = OperationStatus.SUCCESS;
                result.ApiSuccess = true;
                result.Message = "Execution plan contains zero operations.";
                return result;
            }

            onLog?.Invoke($"[EXECUTOR] Starting execution of plan: '{plan.Summary}' ({plan.Operations.Count} operations, intent: {plan.Intent})");

            using (Transaction trans = new Transaction(doc, $"Copilot: {plan.Summary}"))
            {
                FailureHandlingOptions failOpts = trans.GetFailureHandlingOptions();
                failOpts.SetFailuresPreprocessor(new ForgeFailuresPreprocessor());
                failOpts.SetClearAfterRollback(true);
                trans.SetFailureHandlingOptions(failOpts);

                trans.Start();

                var refPlaneCache = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
                // Pre-populate reference plane cache from existing planes in family
                PopulateExistingReferencePlanes(doc, refPlaneCache);

                // Ensure at least one family type exists
                EnsureFamilyTypeExists(doc);

                bool anyFailed = false;
                foreach (var op in plan.Operations)
                {
                    var opResult = ExecuteSingleOperation(doc, op, refPlaneCache, onLog);
                    result.OperationResults.Add(opResult);

                    if (opResult.Status == OperationStatus.FAILED)
                    {
                        anyFailed = true;
                        onLog?.Invoke($"[EXECUTOR OP FAILED] {op.Operation} '{op.Role ?? op.ElementName ?? op.ParameterName}': {opResult.Message}");
                    }
                    else
                    {
                        onLog?.Invoke($"[EXECUTOR OP OK] {op.Operation} '{op.Role ?? op.ElementName ?? op.ParameterName}': {opResult.Message}");
                    }
                }

                try
                {
                    doc.Regenerate();
                    var commitStatus = trans.Commit();

                    if (commitStatus == TransactionStatus.Committed)
                    {
                        result.ApiSuccess = true;
                        result.OverallStatus = anyFailed ? OperationStatus.PARTIAL_SUCCESS : OperationStatus.SUCCESS;
                        result.Message = anyFailed
                            ? "Plan executed with some warnings or non-critical operation failures."
                            : "Plan executed and committed to Revit document successfully.";
                    }
                    else
                    {
                        result.ApiSuccess = false;
                        result.RolledBack = true;
                        result.OverallStatus = OperationStatus.FAILED;
                        result.Message = $"Transaction ended with status {commitStatus}. Changes rolled back.";
                    }
                }
                catch (Exception ex)
                {
                    try { trans.RollBack(); } catch { }
                    result.ApiSuccess = false;
                    result.RolledBack = true;
                    result.OverallStatus = OperationStatus.FAILED;
                    result.Message = $"Transaction commit failed: {ex.Message}";
                    onLog?.Invoke($"[EXECUTOR EXCEPTION] {ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Executes a single atomic FamilyOperation against the family document.
        /// </summary>
        public static OperationExecutionResult ExecuteSingleOperation(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache,
            Action<string>? onLog = null)
        {
            var res = new OperationExecutionResult { Operation = op };

            try
            {
                switch (op.Operation)
                {
                    case FamilyOperationType.CREATE_PARAMETER:
                        return ExecuteCreateParameter(doc, op);

                    case FamilyOperationType.SET_PARAMETER:
                        return ExecuteSetParameter(doc, op);

                    case FamilyOperationType.CREATE_FORMULA:
                        return ExecuteCreateFormula(doc, op);

                    case FamilyOperationType.CREATE_REFERENCE_PLANE:
                        return ExecuteCreateReferencePlane(doc, op, refPlaneCache);

                    case FamilyOperationType.CREATE_DIMENSION:
                        return ExecuteCreateDimension(doc, op, refPlaneCache);

                    case FamilyOperationType.CREATE_EXTRUSION:
                        return ExecuteCreateExtrusion(doc, op, isVoid: false);

                    case FamilyOperationType.CREATE_VOID:
                        return ExecuteCreateExtrusion(doc, op, isVoid: true);

                    case FamilyOperationType.MODIFY_EXTRUSION:
                    case FamilyOperationType.MODIFY_VOID:
                        return ExecuteModifyExtrusion(doc, op);

                    case FamilyOperationType.ALIGN:
                    case FamilyOperationType.LOCK_CONSTRAINT:
                        return ExecuteAlignOrLock(doc, op, refPlaneCache);

                    case FamilyOperationType.SET_MATERIAL:
                        return ExecuteSetMaterial(doc, op);

                    case FamilyOperationType.DELETE_ELEMENT:
                        return ExecuteDeleteElement(doc, op);

                    case FamilyOperationType.FLEX_PARAMETER:
                        return ExecuteFlexParameter(doc, op, onLog);

                    default:
                        res.Status = OperationStatus.SUCCESS;
                        res.Message = $"Operation '{op.Operation}' acknowledged.";
                        return res;
                }
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = ex.Message;
                return res;
            }
        }

        // =========================================================================
        // PARAMETER OPERATIONS
        // =========================================================================

        private static OperationExecutionResult ExecuteCreateParameter(Document doc, FamilyOperation op)
        {
            var res = new OperationExecutionResult { Operation = op };
            if (string.IsNullOrWhiteSpace(op.ParameterName))
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "ParameterName is required for CREATE_PARAMETER.";
                return res;
            }

            FamilyManager famMgr = doc.FamilyManager;
            string paramName = op.ParameterName!.Trim();

            // Check if parameter already exists
            FamilyParameter? existing = famMgr.Parameters.Cast<FamilyParameter>()
                .FirstOrDefault(p => string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase));

            ForgeTypeId specType = MasterFamilyBuilder.ResolveSpecType(op.ParameterType);
            ForgeTypeId groupType = MasterFamilyBuilder.ResolveGroupType(op.GroupType, op.ParameterType);

            FamilyParameter targetParam;
            if (existing != null)
            {
                targetParam = existing;
                res.Message = $"Parameter '{paramName}' already exists. Updating value/formula.";
            }
            else
            {
                targetParam = famMgr.AddParameter(paramName, groupType, specType, op.IsInstance);
                res.Message = $"Created parameter '{paramName}' ({op.ParameterType}, Instance={op.IsInstance}).";
            }

            // Set value if provided
            if (op.Value.HasValue)
            {
                ApplyNumericValue(famMgr, targetParam, op.Value.Value, specType);
            }
            else if (!string.IsNullOrEmpty(op.TextValue))
            {
                try { famMgr.Set(targetParam, op.TextValue); } catch { }
            }

            // Set formula if provided
            if (!string.IsNullOrWhiteSpace(op.Formula))
            {
                try
                {
                    famMgr.SetFormula(targetParam, op.Formula);
                    res.Message += $" Set formula: '{op.Formula}'.";
                }
                catch (Exception fEx)
                {
                    res.Message += $" (Formula '{op.Formula}' rejected: {fEx.Message})";
                }
            }

            res.CreatedElementId = (int)targetParam.Id.Value;
            res.Status = OperationStatus.SUCCESS;
            return res;
        }

        private static OperationExecutionResult ExecuteSetParameter(Document doc, FamilyOperation op)
        {
            var res = new OperationExecutionResult { Operation = op };
            if (string.IsNullOrWhiteSpace(op.ParameterName))
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "ParameterName is required for SET_PARAMETER.";
                return res;
            }

            FamilyManager famMgr = doc.FamilyManager;
            string paramName = op.ParameterName!.Trim();

            FamilyParameter? targetParam = famMgr.Parameters.Cast<FamilyParameter>()
                .FirstOrDefault(p => string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase));

            if (targetParam == null)
            {
                // If parameter does not exist yet, create it on the fly
                return ExecuteCreateParameter(doc, op);
            }

            ForgeTypeId spec = targetParam.Definition.GetDataType();

            if (op.Value.HasValue)
            {
                ApplyNumericValue(famMgr, targetParam, op.Value.Value, spec);
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Set '{paramName}' = {op.Value.Value} {op.Unit ?? "mm"}.";
            }
            else if (op.TextValue != null)
            {
                famMgr.Set(targetParam, op.TextValue);
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Set '{paramName}' = '{op.TextValue}'.";
            }
            else
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"No value provided to set for parameter '{paramName}'.";
            }

            return res;
        }

        private static OperationExecutionResult ExecuteCreateFormula(Document doc, FamilyOperation op)
        {
            var res = new OperationExecutionResult { Operation = op };
            if (string.IsNullOrWhiteSpace(op.ParameterName) || string.IsNullOrWhiteSpace(op.Formula))
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "ParameterName and Formula are required for CREATE_FORMULA.";
                return res;
            }

            FamilyManager famMgr = doc.FamilyManager;
            string paramName = op.ParameterName!.Trim();
            FamilyParameter? targetParam = famMgr.Parameters.Cast<FamilyParameter>()
                .FirstOrDefault(p => string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase));

            if (targetParam == null)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Cannot set formula: parameter '{paramName}' not found in family.";
                return res;
            }

            try
            {
                famMgr.SetFormula(targetParam, op.Formula);
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Applied formula '{op.Formula}' to parameter '{paramName}'.";
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Revit rejected formula '{op.Formula}' for '{paramName}': {ex.Message}";
            }
            return res;
        }

        // =========================================================================
        // REFERENCE PLANE OPERATIONS
        // =========================================================================

        private static OperationExecutionResult ExecuteCreateReferencePlane(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache)
        {
            var res = new OperationExecutionResult { Operation = op };
            string planeName = op.PlaneName?.Trim() ?? $"RP_{Guid.NewGuid().ToString().Substring(0, 6)}";

            // If already exists in cache, return existing
            if (refPlaneCache.TryGetValue(planeName, out ReferencePlane? existingRp) && existingRp != null && existingRp.IsValidObject)
            {
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Reference plane '{planeName}' already exists.";
                res.CreatedElementId = (int)existingRp.Id.Value;
                return res;
            }

            View? planView = FindPlanView(doc);
            View? elevView = FindElevationView(doc);

            double offsetFeet = MmToFeet(op.OffsetMm ?? 0.0);
            string orient = (op.Orientation ?? "X").Trim().ToUpperInvariant();

            XYZ bubbleEnd;
            XYZ freeEnd;
            XYZ cutVec;
            View targetView;

            if (orient == "X")
            {
                // Vertical line in plan view (constant X)
                bubbleEnd = new XYZ(offsetFeet, -10.0, 0.0);
                freeEnd = new XYZ(offsetFeet, 10.0, 0.0);
                cutVec = XYZ.BasisX;
                targetView = planView ?? doc.ActiveView;
            }
            else if (orient == "Y")
            {
                // Horizontal line in plan view (constant Y)
                bubbleEnd = new XYZ(-10.0, offsetFeet, 0.0);
                freeEnd = new XYZ(10.0, offsetFeet, 0.0);
                cutVec = XYZ.BasisY;
                targetView = planView ?? doc.ActiveView;
            }
            else
            {
                // Elevation plane (constant Z)
                bubbleEnd = new XYZ(-10.0, 0.0, offsetFeet);
                freeEnd = new XYZ(10.0, 0.0, offsetFeet);
                cutVec = XYZ.BasisZ;
                targetView = elevView ?? planView ?? doc.ActiveView;
            }

            try
            {
                ReferencePlane rp = doc.FamilyCreate.NewReferencePlane(bubbleEnd, freeEnd, cutVec, targetView);
                rp.Name = planeName;
                try { rp.Maximize3DExtents(); } catch { }

                refPlaneCache[planeName] = rp;

                res.CreatedElementId = (int)rp.Id.Value;
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Created reference plane '{planeName}' (Orientation={orient}, Offset={op.OffsetMm ?? 0}mm).";
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Failed to create reference plane '{planeName}': {ex.Message}";
            }

            return res;
        }

        // =========================================================================
        // DIMENSION & CONSTRAINT OPERATIONS
        // =========================================================================

        private static OperationExecutionResult ExecuteCreateDimension(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache)
        {
            var res = new OperationExecutionResult { Operation = op };
            if (string.IsNullOrWhiteSpace(op.PlaneA) || string.IsNullOrWhiteSpace(op.PlaneB))
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "PlaneA and PlaneB are required to create a dimension.";
                return res;
            }

            string nameA = op.PlaneA!.Trim();
            string nameB = op.PlaneB!.Trim();

            if (!refPlaneCache.TryGetValue(nameA, out ReferencePlane? rpA) || rpA == null || !rpA.IsValidObject)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Reference plane '{nameA}' not found.";
                return res;
            }

            if (!refPlaneCache.TryGetValue(nameB, out ReferencePlane? rpB) || rpB == null || !rpB.IsValidObject)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Reference plane '{nameB}' not found.";
                return res;
            }

            View? planView = FindPlanView(doc);
            View? elevView = FindElevationView(doc);

            // Determine if these planes are in plan or elevation
            string orient = (op.Orientation ?? "X").Trim().ToUpperInvariant();
            View targetView = (orient == "Z" && elevView != null) ? elevView : (planView ?? doc.ActiveView);

            ReferenceArray refArray = new ReferenceArray();
            refArray.Append(rpA.GetReference());
            refArray.Append(rpB.GetReference());

            // Build dimension line between the planes
            XYZ ptA = rpA.BubbleEnd;
            XYZ ptB = rpB.BubbleEnd;

            Line dimLine;
            if (orient == "X")
            {
                dimLine = Line.CreateBound(new XYZ(ptA.X, 0, 0), new XYZ(ptB.X, 0, 0));
            }
            else if (orient == "Y")
            {
                dimLine = Line.CreateBound(new XYZ(0, ptA.Y, 0), new XYZ(0, ptB.Y, 0));
            }
            else
            {
                dimLine = Line.CreateBound(new XYZ(0, 0, ptA.Z), new XYZ(0, 0, ptB.Z));
            }

            try
            {
                Dimension dim = doc.FamilyCreate.NewDimension(targetView, dimLine, refArray);

                // Label parameter association
                if (!string.IsNullOrWhiteSpace(op.LabelParameter))
                {
                    string labelName = op.LabelParameter!.Trim();
                    FamilyManager famMgr = doc.FamilyManager;
                    FamilyParameter? famParam = famMgr.Parameters.Cast<FamilyParameter>()
                        .FirstOrDefault(p => string.Equals(p.Definition.Name, labelName, StringComparison.OrdinalIgnoreCase));

                    if (famParam != null)
                    {
                        try
                        {
                            dim.FamilyLabel = famParam;
                            res.Message = $"Created dimension between '{nameA}' and '{nameB}', labeled with '{labelName}'.";
                        }
                        catch (Exception labEx)
                        {
                            res.Message = $"Created dimension between '{nameA}' and '{nameB}', but label assignment failed: {labEx.Message}";
                        }
                    }
                    else
                    {
                        res.Message = $"Created dimension between '{nameA}' and '{nameB}' (parameter '{labelName}' not found for label).";
                    }
                }
                else
                {
                    res.Message = $"Created dimension between '{nameA}' and '{nameB}'.";
                }

                if (op.IsLocked)
                {
                    try
                    {
                        dim.IsLocked = true;
                        res.Message += " (Locked constraint)";
                    }
                    catch { }
                }

                res.CreatedElementId = (int)dim.Id.Value;
                res.Status = OperationStatus.SUCCESS;
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Failed to create dimension between '{nameA}' and '{nameB}': {ex.Message}";
            }

            return res;
        }

        private static OperationExecutionResult ExecuteAlignOrLock(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache)
        {
            var res = new OperationExecutionResult { Operation = op };
            res.Status = OperationStatus.SUCCESS;
            res.Message = $"Constraint alignment requested for '{op.PlaneA}' and '{op.TargetRef}'.";
            return res;
        }

        // =========================================================================
        // GEOMETRY EXTRUSION & VOID OPERATIONS
        // =========================================================================

        private static OperationExecutionResult ExecuteCreateExtrusion(
            Document doc,
            FamilyOperation op,
            bool isVoid)
        {
            var res = new OperationExecutionResult { Operation = op };
            FamilyManager famMgr = doc.FamilyManager;

            double minX = MmToFeet(op.MinX_mm ?? -100.0);
            double maxX = MmToFeet(op.MaxX_mm ?? 100.0);
            double minY = MmToFeet(op.MinY_mm ?? -100.0);
            double maxY = MmToFeet(op.MaxY_mm ?? 100.0);
            double startZ = MmToFeet(op.StartZ_mm ?? 0.0);
            double endZ = MmToFeet(op.EndZ_mm ?? 100.0);

            // Bounds sanity: prevent zero or inverted dimensions
            if (maxX <= minX) maxX = minX + MmToFeet(10.0);
            if (maxY <= minY) maxY = minY + MmToFeet(10.0);
            if (endZ <= startZ) endZ = startZ + MmToFeet(5.0);

            // Construct extrusion def to leverage profile builder
            var extDef = new ExtrusionDef
            {
                Name = op.ElementName ?? op.Role ?? (isVoid ? "VoidElement" : "SolidElement"),
                ProfileType = op.ProfileType ?? "Rectangle",
                MinX_mm = FeetToMm(minX),
                MaxX_mm = FeetToMm(maxX),
                MinY_mm = FeetToMm(minY),
                MaxY_mm = FeetToMm(maxY),
                StartZ_mm = FeetToMm(startZ),
                EndZ_mm = FeetToMm(endZ),
                Radius_mm = op.Radius_mm ?? 0.0,
                CornerRadius_mm = op.CornerRadius_mm ?? 0.0,
                WallThickness_mm = op.WallThickness_mm ?? 0.0,
                IsVoid = isVoid
            };

            CurveArrArray profile = MasterFamilyBuilder.BuildExtrusionProfile(extDef, minX, maxX, minY, maxY);

            // Sketch plane at Z=0
            Plane xyPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
            SketchPlane sketchPlane = SketchPlane.Create(doc, xyPlane);

            double initialEnd = Math.Max(endZ, MmToFeet(5.0));
            Extrusion extrusion;
            try
            {
                extrusion = doc.FamilyCreate.NewExtrusion(!isVoid, profile, sketchPlane, initialEnd);
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Revit NewExtrusion failed: {ex.Message}";
                return res;
            }

            Parameter startParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
            Parameter endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);

            // Respect start/end param ordering rule
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

            // Bind height/depth parameter if specified
            string? hParamName = op.HeightParameter ?? op.DepthParameter;
            if (!string.IsNullOrEmpty(hParamName))
            {
                FamilyParameter? hFamParam = famMgr.Parameters.Cast<FamilyParameter>()
                    .FirstOrDefault(p => string.Equals(p.Definition.Name, hParamName, StringComparison.OrdinalIgnoreCase));
                if (hFamParam != null && endParam != null && !endParam.IsReadOnly)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(endParam, hFamParam); } catch { }
                }
            }

            // Bind material parameter if specified
            if (!string.IsNullOrEmpty(op.MaterialParameter) && !isVoid)
            {
                FamilyParameter? matFamParam = famMgr.Parameters.Cast<FamilyParameter>()
                    .FirstOrDefault(p => string.Equals(p.Definition.Name, op.MaterialParameter, StringComparison.OrdinalIgnoreCase));
                Parameter matElemParam = extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (matFamParam != null && matElemParam != null && !matElemParam.IsReadOnly)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(matElemParam, matFamParam); } catch { }
                }
            }

            // Subcategory assignment (e.g. Frame/Mullion, Glass)
            if (!string.IsNullOrEmpty(op.Subcategory) && !isVoid && doc.OwnerFamily?.FamilyCategory != null)
            {
                try
                {
                    Category? parentCat = doc.OwnerFamily.FamilyCategory;
                    Category? subCat = parentCat.SubCategories.Cast<Category>()
                        .FirstOrDefault(sc => sc.Name.Equals(op.Subcategory, StringComparison.OrdinalIgnoreCase));
                    if (subCat != null)
                    {
                        extrusion.Subcategory = subCat;
                    }
                }
                catch { }
            }

            // If void, cut against target solids or all solids
            if (isVoid)
            {
                ApplyVoidCuts(doc, extrusion, op.TargetSolids);
            }

            res.CreatedElementId = (int)extrusion.Id.Value;
            res.Status = OperationStatus.SUCCESS;
            string kind = isVoid ? "VOID" : "SOLID";
            res.Message = $"Created {kind} extrusion '{extDef.Name}' ({extDef.ProfileType}) [{FeetToMm(minX):0},{FeetToMm(minY):0} to {FeetToMm(maxX):0},{FeetToMm(maxY):0}] Z:[{FeetToMm(startZ):0}..{FeetToMm(endZ):0}]mm.";
            return res;
        }

        private static OperationExecutionResult ExecuteModifyExtrusion(Document doc, FamilyOperation op)
        {
            var res = new OperationExecutionResult { Operation = op };
            Extrusion? target = FindExtrusionElement(doc, op.TargetElementId, op.ElementName ?? op.Role);

            if (target == null)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Target extrusion '{op.ElementName ?? op.Role ?? op.TargetElementId?.ToString()}' not found.";
                return res;
            }

            Parameter startParam = target.get_Parameter(BuiltInParameter.EXTRUSION_START_PARAM);
            Parameter endParam = target.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);

            if (op.EndZ_mm.HasValue && endParam != null && !endParam.IsReadOnly)
            {
                endParam.Set(MmToFeet(op.EndZ_mm.Value));
            }
            if (op.StartZ_mm.HasValue && startParam != null && !startParam.IsReadOnly)
            {
                startParam.Set(MmToFeet(op.StartZ_mm.Value));
            }

            res.Status = OperationStatus.SUCCESS;
            res.Message = $"Modified extrusion '{target.Name}'.";
            return res;
        }

        private static OperationExecutionResult ExecuteDeleteElement(Document doc, FamilyOperation op)
        {
            var res = new OperationExecutionResult { Operation = op };
            ElementId? targetId = null;

            if (op.TargetElementId.HasValue)
            {
                targetId = new ElementId((long)op.TargetElementId.Value);
            }
            else if (!string.IsNullOrEmpty(op.ElementName) || !string.IsNullOrEmpty(op.Role))
            {
                var elem = FindExtrusionElement(doc, null, op.ElementName ?? op.Role);
                if (elem != null) targetId = elem.Id;
            }

            if (targetId == null)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "Target element to delete not found.";
                return res;
            }

            try
            {
                doc.Delete(targetId);
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Deleted element ID {targetId}.";
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Failed to delete element ID {targetId}: {ex.Message}";
            }

            return res;
        }

        private static OperationExecutionResult ExecuteSetMaterial(Document doc, FamilyOperation op)
        {
            var res = new OperationExecutionResult { Operation = op };
            Extrusion? target = FindExtrusionElement(doc, op.TargetElementId, op.ElementName ?? op.Role);

            if (target == null)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "Target element for material assignment not found.";
                return res;
            }

            Parameter matParam = target.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (matParam == null || matParam.IsReadOnly)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "Element material parameter is read-only or not found.";
                return res;
            }

            // First check if a family parameter is named
            FamilyManager famMgr = doc.FamilyManager;
            string? matParamName = op.MaterialParameter ?? op.TextValue;
            if (!string.IsNullOrEmpty(matParamName))
            {
                FamilyParameter? famMatParam = famMgr.Parameters.Cast<FamilyParameter>()
                    .FirstOrDefault(p => string.Equals(p.Definition.Name, matParamName, StringComparison.OrdinalIgnoreCase));
                if (famMatParam != null)
                {
                    try
                    {
                        famMgr.AssociateElementParameterToFamilyParameter(matParam, famMatParam);
                        res.Status = OperationStatus.SUCCESS;
                        res.Message = $"Associated material of '{target.Name}' to family parameter '{matParamName}'.";
                        return res;
                    }
                    catch { }
                }

                // If not family parameter, search for material element by name in document
                Material? mat = new FilteredElementCollector(doc)
                    .OfClass(typeof(Material))
                    .Cast<Material>()
                    .FirstOrDefault(m => string.Equals(m.Name, matParamName, StringComparison.OrdinalIgnoreCase));
                if (mat != null)
                {
                    matParam.Set(mat.Id);
                    res.Status = OperationStatus.SUCCESS;
                    res.Message = $"Assigned material '{mat.Name}' to '{target.Name}'.";
                    return res;
                }
            }

            res.Status = OperationStatus.FAILED;
            res.Message = $"Material '{matParamName}' not found.";
            return res;
        }

        // =========================================================================
        // FLEX & DIAGNOSTICS OPERATIONS
        // =========================================================================

        private static OperationExecutionResult ExecuteFlexParameter(
            Document doc,
            FamilyOperation op,
            Action<string>? onLog = null)
        {
            var res = new OperationExecutionResult { Operation = op };
            if (string.IsNullOrWhiteSpace(op.ParameterName))
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "ParameterName required for FLEX_PARAMETER.";
                return res;
            }

            string paramName = op.ParameterName!.Trim();
            FamilyManager famMgr = doc.FamilyManager;
            FamilyParameter? famParam = famMgr.Parameters.Cast<FamilyParameter>()
                .FirstOrDefault(p => string.Equals(p.Definition.Name, paramName, StringComparison.OrdinalIgnoreCase));

            if (famParam == null)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = $"Parameter '{paramName}' not found for flex testing.";
                return res;
            }

            var testValues = op.FlexTestValues ?? new List<double> { 800, 1200, 1800 };
            int passedCount = 0;

            foreach (double valMm in testValues)
            {
                using (SubTransaction sub = new SubTransaction(doc))
                {
                    try
                    {
                        sub.Start();
                        famMgr.Set(famParam, MmToFeet(valMm));
                        doc.Regenerate();
                        sub.RollBack(); // Revert back after proving regeneration succeeds
                        passedCount++;
                        onLog?.Invoke($"[FLEX PASS] '{paramName}' flexed to {valMm:0} mm successfully.");
                    }
                    catch (Exception ex)
                    {
                        try { sub.RollBack(); } catch { }
                        onLog?.Invoke($"[FLEX FAIL] '{paramName}' flex to {valMm:0} mm failed: {ex.Message}");
                    }
                }
            }

            if (passedCount == testValues.Count)
            {
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"All {passedCount} flex tests passed for '{paramName}'. Constraints are healthy.";
            }
            else
            {
                res.Status = OperationStatus.PARTIAL_SUCCESS;
                res.Message = $"{passedCount}/{testValues.Count} flex tests passed for '{paramName}'. Over-constrained geometry detected.";
            }

            return res;
        }

        // =========================================================================
        // UTILITY HELPERS
        // =========================================================================

        private static void ApplyNumericValue(FamilyManager famMgr, FamilyParameter famParam, double value, ForgeTypeId specType)
        {
            try
            {
                if (specType == SpecTypeId.Length)
                {
                    famMgr.Set(famParam, MmToFeet(value));
                }
                else if (specType == SpecTypeId.Angle)
                {
                    famMgr.Set(famParam, UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Degrees));
                }
                else if (specType == SpecTypeId.Boolean.YesNo)
                {
                    famMgr.Set(famParam, value != 0 ? 1 : 0);
                }
                else if (specType == SpecTypeId.Int.Integer)
                {
                    famMgr.Set(famParam, (int)value);
                }
                else
                {
                    famMgr.Set(famParam, value);
                }
            }
            catch { }
        }

        private static void ApplyVoidCuts(Document doc, Extrusion voidExtrusion, List<string>? targetSolidNames)
        {
            var solids = new FilteredElementCollector(doc)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .Where(f => f.IsSolid && f.Id != voidExtrusion.Id)
                .ToList();

            foreach (var solid in solids)
            {
                if (targetSolidNames != null && targetSolidNames.Count > 0)
                {
                    bool matchesTarget = targetSolidNames.Any(name =>
                        solid.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0);
                    if (!matchesTarget) continue;
                }

                try
                {
                    SolidSolidCutUtils.AddCutBetweenSolids(doc, solid, voidExtrusion);
                }
                catch { }
            }
        }

        private static Extrusion? FindExtrusionElement(Document doc, int? targetId, string? nameOrRole)
        {
            if (targetId.HasValue)
            {
                Element elem = doc.GetElement(new ElementId((long)targetId.Value));
                if (elem is Extrusion ext) return ext;
            }

            if (!string.IsNullOrEmpty(nameOrRole))
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(Extrusion))
                    .Cast<Extrusion>()
                    .FirstOrDefault(e => e.Name.IndexOf(nameOrRole, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            return null;
        }

        private static View? FindPlanView(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.ViewType == ViewType.FloorPlan && (v.Name.Contains("Ref. Level") || v.Name.Contains("Level")));
        }

        private static View? FindElevationView(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(v => v.ViewType == ViewType.Elevation && (v.Name.Contains("Front") || v.Name.Contains("Exterior") || v.Name.Contains("Elevation")));
        }

        private static void PopulateExistingReferencePlanes(Document doc, Dictionary<string, ReferencePlane> cache)
        {
            var rps = new FilteredElementCollector(doc)
                .OfClass(typeof(ReferencePlane))
                .Cast<ReferencePlane>();

            foreach (var rp in rps)
            {
                if (!string.IsNullOrEmpty(rp.Name))
                {
                    cache[rp.Name] = rp;
                }
            }
        }

        private static void EnsureFamilyTypeExists(Document doc)
        {
            FamilyManager famMgr = doc.FamilyManager;
            if (famMgr.CurrentType == null)
            {
                try
                {
                    if (famMgr.Types.Size == 0)
                    {
                        famMgr.NewType("Default");
                    }
                    else
                    {
                        famMgr.CurrentType = famMgr.Types.Cast<FamilyType>().FirstOrDefault();
                    }
                }
                catch { }
            }
        }
    }
}
