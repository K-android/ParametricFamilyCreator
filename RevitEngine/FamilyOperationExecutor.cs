using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParametricFamilyForge.Models;
using ParametricFamilyForge.Services;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Deterministic execution engine that converts high-level FamilyOperation models into concrete,
    /// atomic Revit API operations against the current active Family Document.
    /// Enhanced with ArchSmarterFamFab's parametric ExpressionEvaluator, (u,v)-to-3D SketchPlane mapping,
    /// tangent-aligned Sweep frames, adaptive polygon Blends, and real PlanarFace NewAlignment execution.
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
        /// Maps 2D sketch-plane (u, v) coordinates (in feet) to 3D Revit world XYZ coordinates (in feet)
        /// based on the reference plane orientation ("X", "Y", or "Z") and offset along its normal.
        /// - X plane (Left/Right, normal = BasisX): u = Y (front-to-back), v = Z (bottom-to-top)
        /// - Y plane (Front/Back, normal = BasisY): u = X (left-to-right), v = Z (bottom-to-top)
        /// - Z plane (Top/Bottom/Level, normal = BasisZ): u = X (left-to-right), v = Y (front-to-back)
        /// </summary>
        public static XYZ UVToXYZ(double uFeet, double vFeet, string axis, double planeOffsetFeet = 0.0)
        {
            string normAxis = (axis ?? "Z").Trim().ToUpperInvariant();
            return normAxis switch
            {
                "X" => new XYZ(planeOffsetFeet, uFeet, vFeet),
                "Y" => new XYZ(uFeet, planeOffsetFeet, vFeet),
                _ => new XYZ(uFeet, vFeet, planeOffsetFeet)
            };
        }

        /// <summary>
        /// Executes an entire ExecutionPlan within a coordinated transaction on the given Family Document.
        /// Runs pre-flight semantic validation & auto-healing before opening the Revit transaction.
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

            // Pre-flight semantic graph check & auto-healing
            FamilyState liveState = FamilyInspectionService.Inspect(doc);
            var semanticNotes = PlanSemanticValidator.ValidateAndHeal(plan, liveState);
            foreach (string note in semanticNotes)
            {
                onLog?.Invoke($"[SEMANTIC VALIDATOR] {note}");
            }

            var paramMap = ExpressionEvaluator.BuildParameterMap(liveState, plan);

            onLog?.Invoke($"[EXECUTOR] Starting execution of plan: '{plan.Summary}' ({plan.Operations.Count} operations, intent: {plan.Intent})");

            using (Transaction trans = new Transaction(doc, $"Copilot: {plan.Summary}"))
            {
                FailureHandlingOptions failOpts = trans.GetFailureHandlingOptions();
                failOpts.SetFailuresPreprocessor(new ForgeFailuresPreprocessor());
                failOpts.SetClearAfterRollback(true);
                trans.SetFailureHandlingOptions(failOpts);

                trans.Start();

                var refPlaneCache = new Dictionary<string, ReferencePlane>(StringComparer.OrdinalIgnoreCase);
                PopulateExistingReferencePlanes(doc, refPlaneCache);

                EnsureFamilyTypeExists(doc);

                bool anyFailed = false;
                foreach (var op in plan.Operations)
                {
                    var opResult = ExecuteSingleOperation(doc, op, refPlaneCache, paramMap, onLog);
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
            Dictionary<string, double>? paramMap = null,
            Action<string>? onLog = null)
        {
            var res = new OperationExecutionResult { Operation = op };
            paramMap ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            try
            {
                switch (op.Operation)
                {
                    case FamilyOperationType.CREATE_PARAMETER:
                        return ExecuteCreateParameter(doc, op, paramMap);

                    case FamilyOperationType.SET_PARAMETER:
                        return ExecuteSetParameter(doc, op, paramMap);

                    case FamilyOperationType.CREATE_FORMULA:
                        return ExecuteCreateFormula(doc, op, paramMap);

                    case FamilyOperationType.CREATE_REFERENCE_PLANE:
                        return ExecuteCreateReferencePlane(doc, op, refPlaneCache, paramMap);

                    case FamilyOperationType.CREATE_DIMENSION:
                        return ExecuteCreateDimension(doc, op, refPlaneCache);

                    case FamilyOperationType.CREATE_EXTRUSION:
                        return ExecuteCreateExtrusion(doc, op, refPlaneCache, paramMap, isVoid: false);

                    case FamilyOperationType.CREATE_VOID:
                        return ExecuteCreateExtrusion(doc, op, refPlaneCache, paramMap, isVoid: true);

                    case FamilyOperationType.CREATE_SWEEP:
                        return ExecuteCreateSweep(doc, op, refPlaneCache, paramMap);

                    case FamilyOperationType.CREATE_BLEND:
                        return ExecuteCreateBlend(doc, op, refPlaneCache, paramMap);

                    case FamilyOperationType.MODIFY_EXTRUSION:
                    case FamilyOperationType.MODIFY_VOID:
                        return ExecuteModifyExtrusion(doc, op, paramMap);

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

        private static OperationExecutionResult ExecuteCreateParameter(
            Document doc,
            FamilyOperation op,
            Dictionary<string, double> paramMap)
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

            if (op.Value.HasValue)
            {
                ApplyNumericValue(famMgr, targetParam, op.Value.Value, specType);
                RegisterParamInMap(paramMap, paramName, op.Value.Value);
            }
            else if (!string.IsNullOrEmpty(op.TextValue))
            {
                try { famMgr.Set(targetParam, op.TextValue); } catch { }
            }

            if (!string.IsNullOrWhiteSpace(op.Formula))
            {
                try
                {
                    famMgr.SetFormula(targetParam, op.Formula);
                    res.Message += $" Set formula: '{op.Formula}'.";
                    if (ExpressionEvaluator.TryEvaluate(op.Formula, paramMap, out double evalVal))
                    {
                        RegisterParamInMap(paramMap, paramName, evalVal);
                    }
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

        private static OperationExecutionResult ExecuteSetParameter(
            Document doc,
            FamilyOperation op,
            Dictionary<string, double> paramMap)
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
                return ExecuteCreateParameter(doc, op, paramMap);
            }

            ForgeTypeId spec = targetParam.Definition.GetDataType();

            if (op.Value.HasValue)
            {
                ApplyNumericValue(famMgr, targetParam, op.Value.Value, spec);
                RegisterParamInMap(paramMap, paramName, op.Value.Value);
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

        private static OperationExecutionResult ExecuteCreateFormula(
            Document doc,
            FamilyOperation op,
            Dictionary<string, double> paramMap)
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
                if (ExpressionEvaluator.TryEvaluate(op.Formula, paramMap, out double evalVal))
                {
                    RegisterParamInMap(paramMap, paramName, evalVal);
                }
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
            Dictionary<string, ReferencePlane> refPlaneCache,
            Dictionary<string, double> paramMap)
        {
            var res = new OperationExecutionResult { Operation = op };
            string planeName = op.PlaneName?.Trim() ?? $"RP_{Guid.NewGuid().ToString().Substring(0, 6)}";

            if (refPlaneCache.TryGetValue(planeName, out ReferencePlane? existingRp) && existingRp != null && existingRp.IsValidObject)
            {
                res.Status = OperationStatus.SUCCESS;
                res.Message = $"Reference plane '{planeName}' already exists.";
                res.CreatedElementId = (int)existingRp.Id.Value;
                return res;
            }

            View? planView = FindPlanView(doc);
            View? elevView = FindElevationView(doc);

            double resolvedOffsetMm = ExpressionEvaluator.EvaluateOrDefault(op.OffsetExpr, op.OffsetMm, paramMap, 0.0);
            op.OffsetMm = resolvedOffsetMm;
            double offsetFeet = MmToFeet(resolvedOffsetMm);
            string orient = (op.Orientation ?? "X").Trim().ToUpperInvariant();

            XYZ bubbleEnd;
            XYZ freeEnd;
            XYZ cutVec;
            View targetView;

            if (orient == "X")
            {
                bubbleEnd = new XYZ(offsetFeet, -10.0, 0.0);
                freeEnd = new XYZ(offsetFeet, 10.0, 0.0);
                cutVec = XYZ.BasisZ;
                targetView = planView ?? doc.ActiveView;
            }
            else if (orient == "Y")
            {
                bubbleEnd = new XYZ(-10.0, offsetFeet, 0.0);
                freeEnd = new XYZ(10.0, offsetFeet, 0.0);
                cutVec = XYZ.BasisZ;
                targetView = planView ?? doc.ActiveView;
            }
            else
            {
                bubbleEnd = new XYZ(-10.0, 0.0, offsetFeet);
                freeEnd = new XYZ(10.0, 0.0, offsetFeet);
                cutVec = XYZ.BasisY;
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
                res.Message = $"Created reference plane '{planeName}' (Orientation={orient}, Offset={resolvedOffsetMm:0.#}mm).";
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

            string orient = (op.Orientation ?? "X").Trim().ToUpperInvariant();
            View targetView = (orient == "Z" && elevView != null) ? elevView : (planView ?? doc.ActiveView);

            ReferenceArray refArray = new ReferenceArray();
            refArray.Append(rpA.GetReference());
            refArray.Append(rpB.GetReference());

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

        /// <summary>
        /// Aligns and optionally locks a geometry element's planar face to a named ReferencePlane
        /// using doc.FamilyCreate.NewAlignment (ported from ArchSmarterFamFab's ApplyConstraints).
        /// </summary>
        private static OperationExecutionResult ExecuteAlignOrLock(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache)
        {
            var res = new OperationExecutionResult { Operation = op };
            string? planeName = op.PlaneA ?? op.PlaneName;
            string? targetName = op.TargetRef ?? op.ElementName ?? op.Role;

            if (string.IsNullOrWhiteSpace(planeName))
            {
                res.Status = OperationStatus.SUCCESS;
                res.Message = "Alignment skipped (no reference plane specified).";
                return res;
            }

            if (!refPlaneCache.TryGetValue(planeName!.Trim(), out ReferencePlane? rp) || rp == null || !rp.IsValidObject)
            {
                res.Status = OperationStatus.PARTIAL_SUCCESS;
                res.Message = $"Reference plane '{planeName}' not found for alignment.";
                return res;
            }

            try
            {
                doc.Regenerate();

                GenericForm? targetGeom = FindGenericFormElement(doc, op.TargetElementId, targetName);
                if (targetGeom == null)
                {
                    res.Status = OperationStatus.PARTIAL_SUCCESS;
                    res.Message = $"Target geometry '{targetName}' not found for alignment to '{planeName}'.";
                    return res;
                }

                Options geomOpts = new Options
                {
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = true
                };

                GeometryElement? geomElem = targetGeom.get_Geometry(geomOpts);
                if (geomElem == null)
                {
                    res.Status = OperationStatus.PARTIAL_SUCCESS;
                    res.Message = $"No geometry faces available on '{targetGeom.Name}' for alignment.";
                    return res;
                }

                XYZ planeNormal = rp.Normal;
                XYZ planeOrigin = rp.BubbleEnd;
                PlanarFace? bestFace = null;
                double bestDist = double.MaxValue;

                foreach (GeometryObject geomObj in geomElem)
                {
                    if (geomObj is Solid solid && solid.Faces.Size > 0)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace pf && pf.Reference != null)
                            {
                                // Face normal must be parallel (or anti-parallel) to the reference plane normal
                                double dot = Math.Abs(pf.FaceNormal.DotProduct(planeNormal));
                                if (dot < 0.99) continue;

                                // Distance from face origin to the reference plane along planeNormal
                                double dist = Math.Abs((pf.Origin - planeOrigin).DotProduct(planeNormal));
                                if (dist < bestDist)
                                {
                                    bestDist = dist;
                                    bestFace = pf;
                                }
                            }
                        }
                    }
                }

                if (bestFace?.Reference == null)
                {
                    res.Status = OperationStatus.PARTIAL_SUCCESS;
                    res.Message = $"No parallel planar face found on '{targetGeom.Name}' to align with '{planeName}'.";
                    return res;
                }

                // Choose an appropriate view perpendicular to the reference plane
                View? alignView = Math.Abs(planeNormal.Z) > 0.9
                    ? (FindElevationView(doc) ?? FindPlanView(doc) ?? doc.ActiveView)
                    : (FindPlanView(doc) ?? FindElevationView(doc) ?? doc.ActiveView);

                if (alignView != null)
                {
                    Dimension alignment = doc.FamilyCreate.NewAlignment(alignView, rp.GetReference(), bestFace.Reference);
                    if (op.IsLocked || op.Operation == FamilyOperationType.LOCK_CONSTRAINT)
                    {
                        try { alignment.IsLocked = true; } catch { }
                    }
                    res.CreatedElementId = (int)alignment.Id.Value;
                    res.Status = OperationStatus.SUCCESS;
                    res.Message = $"Aligned and locked face of '{targetGeom.Name}' to reference plane '{planeName}'.";
                    return res;
                }
            }
            catch (Exception ex)
            {
                res.Status = OperationStatus.PARTIAL_SUCCESS;
                res.Message = $"Alignment between '{planeName}' and '{targetName}' skipped: {ex.Message}";
                return res;
            }

            res.Status = OperationStatus.SUCCESS;
            res.Message = $"Alignment processed for '{planeName}' and '{targetName}'.";
            return res;
        }

        // =========================================================================
        // GEOMETRY EXTRUSION, SWEEP, BLEND & VOID OPERATIONS
        // =========================================================================

        private static OperationExecutionResult ExecuteCreateExtrusion(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache,
            Dictionary<string, double> paramMap,
            bool isVoid)
        {
            var res = new OperationExecutionResult { Operation = op };
            FamilyManager famMgr = doc.FamilyManager;

            // Resolve parametric expressions if present
            double minX_mm = ExpressionEvaluator.EvaluateOrDefault(op.MinX_Expr, op.MinX_mm, paramMap, -100.0);
            double maxX_mm = ExpressionEvaluator.EvaluateOrDefault(op.MaxX_Expr, op.MaxX_mm, paramMap, 100.0);
            double minY_mm = ExpressionEvaluator.EvaluateOrDefault(op.MinY_Expr, op.MinY_mm, paramMap, -100.0);
            double maxY_mm = ExpressionEvaluator.EvaluateOrDefault(op.MaxY_Expr, op.MaxY_mm, paramMap, 100.0);
            double startZ_mm = ExpressionEvaluator.EvaluateOrDefault(op.StartZ_Expr, op.StartZ_mm, paramMap, 0.0);
            double endZ_mm = ExpressionEvaluator.EvaluateOrDefault(op.EndZ_Expr, op.EndZ_mm, paramMap, 100.0);
            double radius_mm = ExpressionEvaluator.EvaluateOrDefault(op.Radius_Expr, op.Radius_mm, paramMap, 0.0);

            // Check if sketch-plane (u, v) coordinates are specified (ArchSmarterFamFab convention)
            bool hasUV = !string.IsNullOrWhiteSpace(op.OriginU_Expr) ||
                         !string.IsNullOrWhiteSpace(op.OriginV_Expr) ||
                         !string.IsNullOrWhiteSpace(op.Width_Expr) ||
                         !string.IsNullOrWhiteSpace(op.Height_Expr);

            string planeAxis = "Z";
            double planeOffsetFeet = 0.0;
            Plane sketchGeomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);

            if (!string.IsNullOrWhiteSpace(op.SketchPlane))
            {
                ResolveSketchPlaneGeometry(op.SketchPlane!, refPlaneCache, paramMap, out planeAxis, out planeOffsetFeet, out sketchGeomPlane);
            }

            SketchPlane sketchPlane = SketchPlane.Create(doc, sketchGeomPlane);

            CurveArrArray profile;
            double startOffsetFeet = MmToFeet(startZ_mm);
            double endOffsetFeet = MmToFeet(endZ_mm);
            if (Math.Abs(endOffsetFeet - startOffsetFeet) < MmToFeet(1.0))
            {
                endOffsetFeet = startOffsetFeet + MmToFeet(10.0);
            }

            string elemName = op.ElementName ?? op.Role ?? (isVoid ? "VoidElement" : "SolidElement");
            string profileType = op.ProfileType ?? "Rectangle";

            if (hasUV || !string.Equals(planeAxis, "Z", StringComparison.OrdinalIgnoreCase))
            {
                // Build profile in (u, v) coordinates mapped onto the 3D SketchPlane via UVToXYZ
                double uMinFeet, uMaxFeet, vMinFeet, vMaxFeet;
                if (hasUV)
                {
                    double originU_mm = ExpressionEvaluator.EvaluateOrDefault(op.OriginU_Expr, 0.0, paramMap, 0.0);
                    double originV_mm = ExpressionEvaluator.EvaluateOrDefault(op.OriginV_Expr, 0.0, paramMap, 0.0);
                    double width_mm = ExpressionEvaluator.EvaluateOrDefault(op.Width_Expr, maxX_mm - minX_mm, paramMap, 200.0);
                    double height_mm = ExpressionEvaluator.EvaluateOrDefault(op.Height_Expr, maxY_mm - minY_mm, paramMap, 200.0);

                    uMinFeet = MmToFeet(originU_mm - width_mm / 2.0);
                    uMaxFeet = MmToFeet(originU_mm + width_mm / 2.0);
                    vMinFeet = MmToFeet(originV_mm - height_mm / 2.0);
                    vMaxFeet = MmToFeet(originV_mm + height_mm / 2.0);
                }
                else
                {
                    uMinFeet = MmToFeet(minX_mm);
                    uMaxFeet = MmToFeet(maxX_mm);
                    vMinFeet = MmToFeet(minY_mm);
                    vMaxFeet = MmToFeet(maxY_mm);
                }

                if (uMaxFeet <= uMinFeet) uMaxFeet = uMinFeet + MmToFeet(10.0);
                if (vMaxFeet <= vMinFeet) vMaxFeet = vMinFeet + MmToFeet(10.0);

                profile = BuildUVProfileOnPlane(profileType, uMinFeet, uMaxFeet, vMinFeet, vMaxFeet, MmToFeet(radius_mm), planeAxis, planeOffsetFeet);
            }
            else
            {
                double minX = MmToFeet(minX_mm);
                double maxX = MmToFeet(maxX_mm);
                double minY = MmToFeet(minY_mm);
                double maxY = MmToFeet(maxY_mm);

                if (maxX <= minX) maxX = minX + MmToFeet(10.0);
                if (maxY <= minY) maxY = minY + MmToFeet(10.0);

                var extDef = new ExtrusionDef
                {
                    Name = elemName,
                    ProfileType = profileType,
                    MinX_mm = FeetToMm(minX),
                    MaxX_mm = FeetToMm(maxX),
                    MinY_mm = FeetToMm(minY),
                    MaxY_mm = FeetToMm(maxY),
                    StartZ_mm = FeetToMm(startOffsetFeet),
                    EndZ_mm = FeetToMm(endOffsetFeet),
                    Radius_mm = radius_mm,
                    CornerRadius_mm = op.CornerRadius_mm ?? 0.0,
                    WallThickness_mm = op.WallThickness_mm ?? 0.0,
                    IsVoid = isVoid
                };

                profile = MasterFamilyBuilder.BuildExtrusionProfile(extDef, minX, maxX, minY, maxY);
            }

            double initialEnd = Math.Max(Math.Abs(endOffsetFeet - startOffsetFeet), MmToFeet(5.0));
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

            if (endOffsetFeet >= startOffsetFeet)
            {
                if (endParam != null && !endParam.IsReadOnly) endParam.Set(endOffsetFeet);
                if (startParam != null && !startParam.IsReadOnly) startParam.Set(startOffsetFeet);
            }
            else
            {
                if (startParam != null && !startParam.IsReadOnly) startParam.Set(startOffsetFeet);
                if (endParam != null && !endParam.IsReadOnly) endParam.Set(endOffsetFeet);
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

            BindMaterialAndSubcategory(doc, famMgr, extrusion, op.MaterialParameter, op.Subcategory, isVoid);

            if (isVoid)
            {
                ApplyVoidCuts(doc, extrusion, op.TargetSolids);
            }

            res.CreatedElementId = (int)extrusion.Id.Value;
            res.Status = OperationStatus.SUCCESS;
            string kind = isVoid ? "VOID" : "SOLID";
            res.Message = $"Created {kind} extrusion '{elemName}' ({profileType}) on plane '{op.SketchPlane ?? "Ref. Level"}' Z/Offset:[{FeetToMm(startOffsetFeet):0}..{FeetToMm(endOffsetFeet):0}]mm.";
            return res;
        }

        /// <summary>
        /// Creates a resilient Revit Sweep using ArchSmarterFamFab's tangent-aligned orthogonal coordinate frame
        /// (profileU = sketchNormal, profileV = pathTangent x profileU) so the sweep profile is guaranteed
        /// perpendicular to the first path curve segment at its start point.
        /// </summary>
        private static OperationExecutionResult ExecuteCreateSweep(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache,
            Dictionary<string, double> paramMap)
        {
            var res = new OperationExecutionResult { Operation = op };
            FamilyManager famMgr = doc.FamilyManager;

            string planeAxis = "Z";
            double planeOffsetFeet = 0.0;
            Plane sketchGeomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);

            if (!string.IsNullOrWhiteSpace(op.SketchPlane))
            {
                ResolveSketchPlaneGeometry(op.SketchPlane!, refPlaneCache, paramMap, out planeAxis, out planeOffsetFeet, out sketchGeomPlane);
            }

            SketchPlane pathSketchPlane = SketchPlane.Create(doc, sketchGeomPlane);
            CurveArray pathCurves = new CurveArray();

            if (op.SweepPathPoints != null && op.SweepPathPoints.Count >= 2)
            {
                for (int i = 0; i < op.SweepPathPoints.Count - 1; i++)
                {
                    double u1 = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.SweepPathPoints[i].U_Expr, op.SweepPathPoints[i].U_mm, paramMap, 0.0));
                    double v1 = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.SweepPathPoints[i].V_Expr, op.SweepPathPoints[i].V_mm, paramMap, 0.0));
                    double u2 = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.SweepPathPoints[i + 1].U_Expr, op.SweepPathPoints[i + 1].U_mm, paramMap, 100.0));
                    double v2 = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.SweepPathPoints[i + 1].V_Expr, op.SweepPathPoints[i + 1].V_mm, paramMap, 0.0));

                    XYZ p1 = UVToXYZ(u1, v1, planeAxis, planeOffsetFeet);
                    XYZ p2 = UVToXYZ(u2, v2, planeAxis, planeOffsetFeet);
                    if (p1.DistanceTo(p2) > MmToFeet(1.0))
                    {
                        pathCurves.Append(Line.CreateBound(p1, p2));
                    }
                }
            }
            else
            {
                // Fallback rectangular loop path from MinX/MaxX/MinY/MaxY
                double uMin = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.MinX_Expr, op.MinX_mm, paramMap, -200.0));
                double uMax = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.MaxX_Expr, op.MaxX_mm, paramMap, 200.0));
                double vMin = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.MinY_Expr, op.MinY_mm, paramMap, -200.0));
                double vMax = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.MaxY_Expr, op.MaxY_mm, paramMap, 200.0));

                XYZ p0 = UVToXYZ(uMin, vMin, planeAxis, planeOffsetFeet);
                XYZ p1 = UVToXYZ(uMax, vMin, planeAxis, planeOffsetFeet);
                XYZ p2 = UVToXYZ(uMax, vMax, planeAxis, planeOffsetFeet);
                XYZ p3 = UVToXYZ(uMin, vMax, planeAxis, planeOffsetFeet);

                pathCurves.Append(Line.CreateBound(p0, p1));
                pathCurves.Append(Line.CreateBound(p1, p2));
                pathCurves.Append(Line.CreateBound(p2, p3));
                pathCurves.Append(Line.CreateBound(p3, p0));
            }

            if (pathCurves.Size == 0)
            {
                res.Status = OperationStatus.FAILED;
                res.Message = "Sweep path contained no valid segments.";
                return res;
            }

            Curve firstCurve = pathCurves.get_Item(0);
            XYZ pathStart = firstCurve.GetEndPoint(0);
            XYZ pathTangent = (firstCurve.GetEndPoint(1) - pathStart).Normalize();

            // Compute orthogonal profile coordinate system at pathStart perpendicular to pathTangent
            XYZ sketchNormal = sketchGeomPlane.Normal;
            XYZ profileU = sketchNormal;
            XYZ profileV = pathTangent.CrossProduct(profileU).Normalize();

            if (profileV.IsZeroLength())
            {
                profileU = Math.Abs(pathTangent.X) < 0.9 ? XYZ.BasisX : XYZ.BasisY;
                profileV = pathTangent.CrossProduct(profileU).Normalize();
                profileU = profileV.CrossProduct(pathTangent).Normalize();
            }

            double profWidthFeet = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.Width_Expr, op.WallThickness_mm ?? 40.0, paramMap, 40.0));
            double profHeightFeet = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.Height_Expr, op.WallThickness_mm ?? 40.0, paramMap, 40.0));
            double profRadiusFeet = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.Radius_Expr, op.Radius_mm, paramMap, 0.0));

            CurveArray profileLoop = new CurveArray();
            if (profRadiusFeet > MmToFeet(1.0) || string.Equals(op.ProfileType, "Circle", StringComparison.OrdinalIgnoreCase) || string.Equals(op.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase))
            {
                double r = profRadiusFeet > MmToFeet(1.0) ? profRadiusFeet : profWidthFeet / 2.0;
                const int segments = 12;
                var pts = new XYZ[segments];
                for (int i = 0; i < segments; i++)
                {
                    double angle = 2.0 * Math.PI * i / segments;
                    pts[i] = pathStart + r * Math.Cos(angle) * profileU + r * Math.Sin(angle) * profileV;
                }
                for (int i = 0; i < segments; i++)
                {
                    profileLoop.Append(Line.CreateBound(pts[i], pts[(i + 1) % segments]));
                }
            }
            else
            {
                double hw = Math.Max(profWidthFeet / 2.0, MmToFeet(2.0));
                double hh = Math.Max(profHeightFeet / 2.0, MmToFeet(2.0));

                XYZ c0 = pathStart - hw * profileU - hh * profileV;
                XYZ c1 = pathStart + hw * profileU - hh * profileV;
                XYZ c2 = pathStart + hw * profileU + hh * profileV;
                XYZ c3 = pathStart - hw * profileU + hh * profileV;

                profileLoop.Append(Line.CreateBound(c0, c1));
                profileLoop.Append(Line.CreateBound(c1, c2));
                profileLoop.Append(Line.CreateBound(c2, c3));
                profileLoop.Append(Line.CreateBound(c3, c0));
            }

            CurveArrArray profileLoops = new CurveArrArray();
            profileLoops.Append(profileLoop);

            Plane profilePlane = Plane.CreateByNormalAndOrigin(pathTangent, pathStart);
            SketchPlane profileSketchPlane = SketchPlane.Create(doc, profilePlane);

            SweepProfile sweepProfile = doc.Application.Create.NewCurveLoopsProfile(profileLoops);
            Sweep sweep = doc.FamilyCreate.NewSweep(
                !op.IsVoid,
                pathCurves,
                pathSketchPlane,
                sweepProfile,
                0,
                ProfilePlaneLocation.Start);

            BindMaterialAndSubcategory(doc, famMgr, sweep, op.MaterialParameter, op.Subcategory, op.IsVoid);

            res.CreatedElementId = (int)sweep.Id.Value;
            res.Status = OperationStatus.SUCCESS;
            res.Message = $"Created sweep '{op.ElementName ?? op.Role ?? "Sweep"}' with tangent-aligned profile frame.";
            return res;
        }

        /// <summary>
        /// Creates a resilient Revit Blend between bottom and top profiles.
        /// Uses ArchSmarterFamFab's 8-segment polygon approximation for circular blends so Revit's vertex
        /// matching never fails on two-arc circles, with automatic fallback if top/bottom profiles are offset.
        /// </summary>
        private static OperationExecutionResult ExecuteCreateBlend(
            Document doc,
            FamilyOperation op,
            Dictionary<string, ReferencePlane> refPlaneCache,
            Dictionary<string, double> paramMap)
        {
            var res = new OperationExecutionResult { Operation = op };
            FamilyManager famMgr = doc.FamilyManager;

            string planeAxis = "Z";
            double planeOffsetFeet = 0.0;
            Plane sketchGeomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);

            if (!string.IsNullOrWhiteSpace(op.SketchPlane))
            {
                ResolveSketchPlaneGeometry(op.SketchPlane!, refPlaneCache, paramMap, out planeAxis, out planeOffsetFeet, out sketchGeomPlane);
            }

            SketchPlane sketchPlane = SketchPlane.Create(doc, sketchGeomPlane);

            double botOriginU = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.OriginU_Expr, 0.0, paramMap, 0.0));
            double botOriginV = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.OriginV_Expr, 0.0, paramMap, 0.0));
            double botWidth = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.Width_Expr, (op.MaxX_mm ?? 100.0) - (op.MinX_mm ?? -100.0), paramMap, 200.0));
            double botHeight = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.Height_Expr, (op.MaxY_mm ?? 100.0) - (op.MinY_mm ?? -100.0), paramMap, 200.0));
            double botRadius = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.Radius_Expr, op.Radius_mm, paramMap, 0.0));

            double topOriginU = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.TopOriginU_Expr, FeetToMm(botOriginU), paramMap, FeetToMm(botOriginU)));
            double topOriginV = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.TopOriginV_Expr, FeetToMm(botOriginV), paramMap, FeetToMm(botOriginV)));
            double topWidth = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.TopWidth_Expr, FeetToMm(botWidth) * 0.75, paramMap, FeetToMm(botWidth) * 0.75));
            double topHeight = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.TopHeight_Expr, FeetToMm(botHeight) * 0.75, paramMap, FeetToMm(botHeight) * 0.75));
            double topRadius = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.TopRadius_Expr, op.TopRadius_mm ?? (FeetToMm(botRadius) * 0.75), paramMap, FeetToMm(botRadius) * 0.75));

            double startOffsetFeet = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.StartZ_Expr, op.StartZ_mm, paramMap, 0.0));
            double endOffsetFeet = MmToFeet(ExpressionEvaluator.EvaluateOrDefault(op.EndZ_Expr, op.EndZ_mm, paramMap, 300.0));
            if (Math.Abs(endOffsetFeet - startOffsetFeet) < MmToFeet(2.0))
            {
                endOffsetFeet = startOffsetFeet + MmToFeet(50.0);
            }

            bool isCircle = botRadius > MmToFeet(1.0) ||
                            string.Equals(op.ProfileType, "Circle", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(op.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase);

            CurveArray bottomLoop = isCircle
                ? BuildCircleAsPolygonLoop(botOriginU, botOriginV, Math.Max(botRadius, MmToFeet(5.0)), planeAxis, planeOffsetFeet)
                : BuildRectLoop(botOriginU, botOriginV, botWidth, botHeight, planeAxis, planeOffsetFeet);

            CurveArray topLoop = isCircle
                ? BuildCircleAsPolygonLoop(topOriginU, topOriginV, Math.Max(topRadius, MmToFeet(3.0)), planeAxis, planeOffsetFeet)
                : BuildRectLoop(topOriginU, topOriginV, topWidth, topHeight, planeAxis, planeOffsetFeet);

            Blend blend = doc.FamilyCreate.NewBlend(!op.IsVoid, topLoop, bottomLoop, sketchPlane);

            Parameter botParam = blend.get_Parameter(BuiltInParameter.BLEND_START_PARAM);
            Parameter topParam = blend.get_Parameter(BuiltInParameter.BLEND_END_PARAM);

            if (endOffsetFeet >= startOffsetFeet)
            {
                if (topParam != null && !topParam.IsReadOnly) topParam.Set(endOffsetFeet);
                if (botParam != null && !botParam.IsReadOnly) botParam.Set(startOffsetFeet);
            }
            else
            {
                if (botParam != null && !botParam.IsReadOnly) botParam.Set(startOffsetFeet);
                if (topParam != null && !topParam.IsReadOnly) topParam.Set(endOffsetFeet);
            }

            BindMaterialAndSubcategory(doc, famMgr, blend, op.MaterialParameter, op.Subcategory, op.IsVoid);

            res.CreatedElementId = (int)blend.Id.Value;
            res.Status = OperationStatus.SUCCESS;
            res.Message = $"Created blend '{op.ElementName ?? op.Role ?? "Blend"}' Z/Offset:[{FeetToMm(startOffsetFeet):0}..{FeetToMm(endOffsetFeet):0}]mm.";
            return res;
        }

        private static OperationExecutionResult ExecuteModifyExtrusion(
            Document doc,
            FamilyOperation op,
            Dictionary<string, double> paramMap)
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

            double? resolvedEndZ = !string.IsNullOrWhiteSpace(op.EndZ_Expr)
                ? ExpressionEvaluator.EvaluateOrDefault(op.EndZ_Expr, op.EndZ_mm, paramMap)
                : op.EndZ_mm;
            double? resolvedStartZ = !string.IsNullOrWhiteSpace(op.StartZ_Expr)
                ? ExpressionEvaluator.EvaluateOrDefault(op.StartZ_Expr, op.StartZ_mm, paramMap)
                : op.StartZ_mm;

            if (resolvedEndZ.HasValue && endParam != null && !endParam.IsReadOnly)
            {
                endParam.Set(MmToFeet(resolvedEndZ.Value));
            }
            if (resolvedStartZ.HasValue && startParam != null && !startParam.IsReadOnly)
            {
                startParam.Set(MmToFeet(resolvedStartZ.Value));
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
                var elem = FindGenericFormElement(doc, null, op.ElementName ?? op.Role);
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
            GenericForm? target = FindGenericFormElement(doc, op.TargetElementId, op.ElementName ?? op.Role);

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
                        sub.RollBack();
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
        // SKETCH PLANE & PROFILE GEOMETRY HELPERS
        // =========================================================================

        private static void ResolveSketchPlaneGeometry(
            string sketchPlaneName,
            Dictionary<string, ReferencePlane> refPlaneCache,
            Dictionary<string, double> paramMap,
            out string axis,
            out double planeOffsetFeet,
            out Plane geomPlane)
        {
            axis = "Z";
            planeOffsetFeet = 0.0;
            geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);

            string trimmed = sketchPlaneName.Trim();

            if (refPlaneCache.TryGetValue(trimmed, out ReferencePlane? rp) && rp != null && rp.IsValidObject)
            {
                XYZ n = rp.Normal;
                XYZ orig = rp.BubbleEnd;
                if (Math.Abs(n.X) > 0.9)
                {
                    axis = "X";
                    planeOffsetFeet = orig.X;
                    geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(planeOffsetFeet, 0, 0));
                    return;
                }
                if (Math.Abs(n.Y) > 0.9)
                {
                    axis = "Y";
                    planeOffsetFeet = orig.Y;
                    geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, planeOffsetFeet, 0));
                    return;
                }
                axis = "Z";
                planeOffsetFeet = orig.Z;
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, planeOffsetFeet));
                return;
            }

            // Standard architectural reference plane names fallback
            double widthMm = paramMap.TryGetValue("Width", out double w) ? w : 1000.0;
            double depthMm = paramMap.TryGetValue("Depth", out double d) ? d : 600.0;
            double heightMm = paramMap.TryGetValue("Height", out double h) ? h : 1000.0;

            if (string.Equals(trimmed, "Left", StringComparison.OrdinalIgnoreCase))
            {
                axis = "X";
                planeOffsetFeet = MmToFeet(-widthMm / 2.0);
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(planeOffsetFeet, 0, 0));
            }
            else if (string.Equals(trimmed, "Right", StringComparison.OrdinalIgnoreCase))
            {
                axis = "X";
                planeOffsetFeet = MmToFeet(widthMm / 2.0);
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, new XYZ(planeOffsetFeet, 0, 0));
            }
            else if (string.Equals(trimmed, "Center (Left/Right)", StringComparison.OrdinalIgnoreCase))
            {
                axis = "X";
                planeOffsetFeet = 0.0;
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisX, XYZ.Zero);
            }
            else if (string.Equals(trimmed, "Front", StringComparison.OrdinalIgnoreCase))
            {
                axis = "Y";
                planeOffsetFeet = MmToFeet(-depthMm / 2.0);
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, planeOffsetFeet, 0));
            }
            else if (string.Equals(trimmed, "Back", StringComparison.OrdinalIgnoreCase))
            {
                axis = "Y";
                planeOffsetFeet = MmToFeet(depthMm / 2.0);
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, new XYZ(0, planeOffsetFeet, 0));
            }
            else if (string.Equals(trimmed, "Center (Front/Back)", StringComparison.OrdinalIgnoreCase))
            {
                axis = "Y";
                planeOffsetFeet = 0.0;
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisY, XYZ.Zero);
            }
            else if (string.Equals(trimmed, "Top", StringComparison.OrdinalIgnoreCase))
            {
                axis = "Z";
                planeOffsetFeet = MmToFeet(heightMm);
                geomPlane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, planeOffsetFeet));
            }
        }

        private static CurveArrArray BuildUVProfileOnPlane(
            string profileType,
            double uMinFeet,
            double uMaxFeet,
            double vMinFeet,
            double vMaxFeet,
            double radiusFeet,
            string axis,
            double planeOffsetFeet)
        {
            var curveArrArray = new CurveArrArray();
            double uCenter = (uMinFeet + uMaxFeet) / 2.0;
            double vCenter = (vMinFeet + vMaxFeet) / 2.0;
            double uSpan = Math.Max(uMaxFeet - uMinFeet, MmToFeet(5.0));
            double vSpan = Math.Max(vMaxFeet - vMinFeet, MmToFeet(5.0));

            if (string.Equals(profileType, "Cylinder", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(profileType, "Circle", StringComparison.OrdinalIgnoreCase))
            {
                double r = radiusFeet > MmToFeet(1.0) ? radiusFeet : Math.Min(uSpan, vSpan) / 2.0;
                var loop = new CurveArray();
                XYZ p0 = UVToXYZ(uCenter + r, vCenter, axis, planeOffsetFeet);
                XYZ pMid1 = UVToXYZ(uCenter, vCenter + r, axis, planeOffsetFeet);
                XYZ p1 = UVToXYZ(uCenter - r, vCenter, axis, planeOffsetFeet);
                XYZ pMid2 = UVToXYZ(uCenter, vCenter - r, axis, planeOffsetFeet);

                loop.Append(Arc.Create(p0, p1, pMid1));
                loop.Append(Arc.Create(p1, p0, pMid2));
                curveArrArray.Append(loop);
                return curveArrArray;
            }

            curveArrArray.Append(BuildRectLoop(uCenter, vCenter, uSpan, vSpan, axis, planeOffsetFeet));
            return curveArrArray;
        }

        private static CurveArray BuildRectLoop(
            double uCenterFeet,
            double vCenterFeet,
            double widthFeet,
            double heightFeet,
            string axis,
            double planeOffsetFeet)
        {
            double hw = Math.Max(widthFeet / 2.0, MmToFeet(2.5));
            double hh = Math.Max(heightFeet / 2.0, MmToFeet(2.5));

            XYZ p0 = UVToXYZ(uCenterFeet - hw, vCenterFeet - hh, axis, planeOffsetFeet);
            XYZ p1 = UVToXYZ(uCenterFeet + hw, vCenterFeet - hh, axis, planeOffsetFeet);
            XYZ p2 = UVToXYZ(uCenterFeet + hw, vCenterFeet + hh, axis, planeOffsetFeet);
            XYZ p3 = UVToXYZ(uCenterFeet - hw, vCenterFeet + hh, axis, planeOffsetFeet);

            var loop = new CurveArray();
            loop.Append(Line.CreateBound(p0, p1));
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p0));
            return loop;
        }

        private static CurveArray BuildCircleAsPolygonLoop(
            double uCenterFeet,
            double vCenterFeet,
            double radiusFeet,
            string axis,
            double planeOffsetFeet,
            int segments = 8)
        {
            var loop = new CurveArray();
            var pts = new XYZ[segments];
            for (int i = 0; i < segments; i++)
            {
                double angle = 2.0 * Math.PI * i / segments;
                double u = uCenterFeet + radiusFeet * Math.Cos(angle);
                double v = vCenterFeet + radiusFeet * Math.Sin(angle);
                pts[i] = UVToXYZ(u, v, axis, planeOffsetFeet);
            }
            for (int i = 0; i < segments; i++)
            {
                loop.Append(Line.CreateBound(pts[i], pts[(i + 1) % segments]));
            }
            return loop;
        }

        private static void BindMaterialAndSubcategory(
            Document doc,
            FamilyManager famMgr,
            GenericForm geom,
            string? materialParameter,
            string? subcategoryName,
            bool isVoid)
        {
            if (isVoid) return;

            if (!string.IsNullOrEmpty(materialParameter))
            {
                FamilyParameter? matFamParam = famMgr.Parameters.Cast<FamilyParameter>()
                    .FirstOrDefault(p => string.Equals(p.Definition.Name, materialParameter, StringComparison.OrdinalIgnoreCase));
                Parameter matElemParam = geom.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
                if (matFamParam != null && matElemParam != null && !matElemParam.IsReadOnly)
                {
                    try { famMgr.AssociateElementParameterToFamilyParameter(matElemParam, matFamParam); } catch { }
                }
            }

            if (!string.IsNullOrEmpty(subcategoryName) && doc.OwnerFamily?.FamilyCategory != null)
            {
                try
                {
                    Category parentCat = doc.OwnerFamily.FamilyCategory;
                    Category? subCat = parentCat.SubCategories.Cast<Category>()
                        .FirstOrDefault(sc => sc.Name.Equals(subcategoryName, StringComparison.OrdinalIgnoreCase));

                    if (subCat == null)
                    {
                        subCat = doc.Settings.Categories.NewSubcategory(parentCat, subcategoryName!.Trim());
                    }

                    if (subCat != null)
                    {
                        geom.Subcategory = subCat;
                    }
                }
                catch { }
            }
        }

        private static void RegisterParamInMap(Dictionary<string, double> paramMap, string name, double valMm)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string trimmed = name.Trim();
            paramMap[trimmed] = valMm;
            paramMap[trimmed.Replace(' ', '_')] = valMm;
            paramMap[trimmed.Replace('_', ' ')] = valMm;
        }

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

        private static GenericForm? FindGenericFormElement(Document doc, int? targetId, string? nameOrRole)
        {
            if (targetId.HasValue)
            {
                Element elem = doc.GetElement(new ElementId((long)targetId.Value));
                if (elem is GenericForm gf) return gf;
            }

            if (!string.IsNullOrEmpty(nameOrRole))
            {
                return new FilteredElementCollector(doc)
                    .OfClass(typeof(GenericForm))
                    .Cast<GenericForm>()
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
