using System;
using System.Collections.Generic;
using System.Linq;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    /// <summary>
    /// Pre-flight semantic graph validator and auto-healer for ExecutionPlans before starting a Revit Transaction.
    /// Ported and adapted from ArchSmarterFamFab's validate_family.py semantic reference checker.
    /// Verifies reference plane dependencies, parameter tokens in expressions, non-zero extrusion depths,
    /// and auto-synthesizes missing standard reference planes (Left, Right, Front, Back, Top, Bottom).
    /// </summary>
    public static class PlanSemanticValidator
    {
        private static readonly HashSet<string> BuiltInPlaneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Center (Left/Right)",
            "Center (Front/Back)",
            "Ref. Level",
            "Reference Level",
            "Lower Ref. Level",
            "Origin"
        };

        public static List<string> ValidateAndHeal(ExecutionPlan plan, FamilyState? currentState)
        {
            var diagnostics = new List<string>();
            if (plan?.Operations == null || plan.Operations.Count == 0)
                return diagnostics;

            var paramMap = ExpressionEvaluator.BuildParameterMap(currentState, plan);

            // Track known reference planes from live document + built-ins
            var knownPlanes = new HashSet<string>(BuiltInPlaneNames, StringComparer.OrdinalIgnoreCase);
            if (currentState?.ReferencePlanes != null)
            {
                foreach (var rp in currentState.ReferencePlanes)
                {
                    if (!string.IsNullOrWhiteSpace(rp.Name))
                        knownPlanes.Add(rp.Name);
                }
            }

            // Track planes declared in the plan itself
            foreach (var op in plan.Operations)
            {
                if (op.Operation == FamilyOperationType.CREATE_REFERENCE_PLANE &&
                    !string.IsNullOrWhiteSpace(op.PlaneName))
                {
                    knownPlanes.Add(op.PlaneName!);
                }
            }

            // Auto-heal missing standard reference planes referenced by SketchPlane, PlaneA, or PlaneB
            var planesToPrepend = new List<FamilyOperation>();
            var referencedPlanes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var op in plan.Operations)
            {
                if (!string.IsNullOrWhiteSpace(op.SketchPlane)) referencedPlanes.Add(op.SketchPlane!);
                if (!string.IsNullOrWhiteSpace(op.PlaneA)) referencedPlanes.Add(op.PlaneA!);
                if (!string.IsNullOrWhiteSpace(op.PlaneB)) referencedPlanes.Add(op.PlaneB!);
            }

            foreach (string reqPlane in referencedPlanes)
            {
                if (knownPlanes.Contains(reqPlane))
                    continue;

                var healedPlaneOp = TrySynthesizeStandardPlane(reqPlane, paramMap);
                if (healedPlaneOp != null)
                {
                    planesToPrepend.Add(healedPlaneOp);
                    knownPlanes.Add(reqPlane);
                    diagnostics.Add($"[Auto-Heal] Synthesized missing standard reference plane '{reqPlane}' ({healedPlaneOp.Orientation}, offset {healedPlaneOp.OffsetMm:F1} mm).");
                }
                else
                {
                    diagnostics.Add($"[Semantic Warning] Referenced plane '{reqPlane}' is not in the live document or plan; executor will fallback gracefully.");
                }
            }

            if (planesToPrepend.Count > 0)
            {
                // Insert right after any CREATE_PARAMETER operations so parameters exist first
                int insertIdx = 0;
                while (insertIdx < plan.Operations.Count &&
                       (plan.Operations[insertIdx].Operation == FamilyOperationType.CREATE_PARAMETER ||
                        plan.Operations[insertIdx].Operation == FamilyOperationType.SET_PARAMETER))
                {
                    insertIdx++;
                }
                plan.Operations.InsertRange(insertIdx, planesToPrepend);
            }

            // Pre-evaluate expressions and validate geometry thickness / bounds
            foreach (var op in plan.Operations)
            {
                if (op.Operation == FamilyOperationType.CREATE_REFERENCE_PLANE)
                {
                    if (!string.IsNullOrWhiteSpace(op.OffsetExpr))
                    {
                        if (ExpressionEvaluator.TryEvaluate(op.OffsetExpr, paramMap, out double evalOffset))
                        {
                            op.OffsetMm = evalOffset;
                        }
                        else
                        {
                            diagnostics.Add($"[Semantic Warning] Could not evaluate offset_expr '{op.OffsetExpr}' on plane '{op.PlaneName}'.");
                        }
                    }
                }
                else if (op.Operation == FamilyOperationType.CREATE_EXTRUSION ||
                         op.Operation == FamilyOperationType.MODIFY_EXTRUSION ||
                         op.Operation == FamilyOperationType.CREATE_VOID ||
                         op.Operation == FamilyOperationType.MODIFY_VOID ||
                         op.Operation == FamilyOperationType.CREATE_SWEEP ||
                         op.Operation == FamilyOperationType.CREATE_BLEND)
                {
                    if (!string.IsNullOrWhiteSpace(op.MinX_Expr) && ExpressionEvaluator.TryEvaluate(op.MinX_Expr, paramMap, out double minX))
                        op.MinX_mm = minX;
                    if (!string.IsNullOrWhiteSpace(op.MaxX_Expr) && ExpressionEvaluator.TryEvaluate(op.MaxX_Expr, paramMap, out double maxX))
                        op.MaxX_mm = maxX;
                    if (!string.IsNullOrWhiteSpace(op.MinY_Expr) && ExpressionEvaluator.TryEvaluate(op.MinY_Expr, paramMap, out double minY))
                        op.MinY_mm = minY;
                    if (!string.IsNullOrWhiteSpace(op.MaxY_Expr) && ExpressionEvaluator.TryEvaluate(op.MaxY_Expr, paramMap, out double maxY))
                        op.MaxY_mm = maxY;
                    if (!string.IsNullOrWhiteSpace(op.StartZ_Expr) && ExpressionEvaluator.TryEvaluate(op.StartZ_Expr, paramMap, out double startZ))
                        op.StartZ_mm = startZ;
                    if (!string.IsNullOrWhiteSpace(op.EndZ_Expr) && ExpressionEvaluator.TryEvaluate(op.EndZ_Expr, paramMap, out double endZ))
                        op.EndZ_mm = endZ;
                    if (!string.IsNullOrWhiteSpace(op.Radius_Expr) && ExpressionEvaluator.TryEvaluate(op.Radius_Expr, paramMap, out double rad))
                        op.Radius_mm = rad;
                    if (!string.IsNullOrWhiteSpace(op.TopRadius_Expr) && ExpressionEvaluator.TryEvaluate(op.TopRadius_Expr, paramMap, out double topRad))
                        op.TopRadius_mm = topRad;

                    // Ensure non-zero extrusion depth
                    if (op.StartZ_mm.HasValue && op.EndZ_mm.HasValue &&
                        Math.Abs(op.EndZ_mm.Value - op.StartZ_mm.Value) < 0.5)
                    {
                        op.EndZ_mm = op.StartZ_mm.Value + 25.0;
                        diagnostics.Add($"[Auto-Heal] Extrusion '{op.ElementName ?? op.Role}' had zero depth; adjusted end_z_mm to {op.EndZ_mm:F1} mm.");
                    }
                }
            }

            return diagnostics;
        }

        private static FamilyOperation? TrySynthesizeStandardPlane(string planeName, Dictionary<string, double> paramMap)
        {
            double width = paramMap.TryGetValue("Width", out double w) ? w : 1000.0;
            double depth = paramMap.TryGetValue("Depth", out double d) ? d : 600.0;
            double height = paramMap.TryGetValue("Height", out double h) ? h : 1000.0;

            if (string.Equals(planeName, "Left", StringComparison.OrdinalIgnoreCase))
            {
                return new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_REFERENCE_PLANE,
                    PlaneName = "Left",
                    Orientation = "X",
                    OffsetMm = -width / 2.0,
                    OffsetExpr = "-Width / 2",
                    Summary = "Auto-healed Left reference plane"
                };
            }
            if (string.Equals(planeName, "Right", StringComparison.OrdinalIgnoreCase))
            {
                return new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_REFERENCE_PLANE,
                    PlaneName = "Right",
                    Orientation = "X",
                    OffsetMm = width / 2.0,
                    OffsetExpr = "Width / 2",
                    Summary = "Auto-healed Right reference plane"
                };
            }
            if (string.Equals(planeName, "Front", StringComparison.OrdinalIgnoreCase))
            {
                return new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_REFERENCE_PLANE,
                    PlaneName = "Front",
                    Orientation = "Y",
                    OffsetMm = -depth / 2.0,
                    OffsetExpr = "-Depth / 2",
                    Summary = "Auto-healed Front reference plane"
                };
            }
            if (string.Equals(planeName, "Back", StringComparison.OrdinalIgnoreCase))
            {
                return new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_REFERENCE_PLANE,
                    PlaneName = "Back",
                    Orientation = "Y",
                    OffsetMm = depth / 2.0,
                    OffsetExpr = "Depth / 2",
                    Summary = "Auto-healed Back reference plane"
                };
            }
            if (string.Equals(planeName, "Top", StringComparison.OrdinalIgnoreCase))
            {
                return new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_REFERENCE_PLANE,
                    PlaneName = "Top",
                    Orientation = "Z",
                    OffsetMm = height,
                    OffsetExpr = "Height",
                    Summary = "Auto-healed Top reference plane"
                };
            }
            if (string.Equals(planeName, "Bottom", StringComparison.OrdinalIgnoreCase))
            {
                return new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_REFERENCE_PLANE,
                    PlaneName = "Bottom",
                    Orientation = "Z",
                    OffsetMm = 0.0,
                    OffsetExpr = "0",
                    Summary = "Auto-healed Bottom reference plane"
                };
            }

            return null;
        }
    }
}
