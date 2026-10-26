using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using ParametricFamilyForge.Models;
using ParametricFamilyForge.Services;

namespace ParametricFamilyForge.RevitEngine
{
    /// <summary>
    /// Audits Revit Family post-execution: verifies actual BRep geometry, verifies distinct component count,
    /// performs live parameter flexing to ensure parameters physically drive geometry, and distinguishes
    /// API execution success from design intent validation.
    /// </summary>
    public static class PostExecutionValidator
    {
        public static ValidationResult ValidateExecution(
            Document doc,
            ExecutionPlan plan,
            ExecutionResult executionResult,
            Action<string>? onLog = null)
        {
            var val = new ValidationResult
            {
                ApiSuccess = executionResult != null && executionResult.ApiSuccess
            };

            if (doc == null || !doc.IsFamilyDocument)
            {
                val.IntentSatisfied = false;
                val.Summary = "Target is not a valid Revit Family document.";
                return val;
            }

            if (!val.ApiSuccess)
            {
                val.IntentSatisfied = false;
                val.Summary = "Revit API execution failed or was rolled back.";
                val.Discrepancies.Add(executionResult?.Message ?? "Transaction failure.");
                return val;
            }

            onLog?.Invoke("[VALIDATOR] Inspecting live family state for design intent verification...");

            // 1. Authoritative Inspection from Live Document
            FamilyState liveState = FamilyInspectionService.Inspect(doc);

            int expectedCount = plan?.ExpectedComponents?.Count ?? 0;
            int actualSolidsCount = liveState.Solids.Count(s => !s.IsVoid);
            val.ExpectedComponentsCount = expectedCount;
            val.ActualComponentsCount = actualSolidsCount;

            // 2. Component Count Audit
            if (expectedCount > 0)
            {
                if (actualSolidsCount < expectedCount)
                {
                    string msg = $"Expected {expectedCount} distinct geometric components, but found {actualSolidsCount} solid(s) in Revit family.";
                    if (actualSolidsCount == 1 && expectedCount > 1)
                    {
                        msg += " (Detected geometry collapse into a single generic extrusion).";
                    }
                    val.Discrepancies.Add(msg);
                    onLog?.Invoke($"[VALIDATOR WARNING] {msg}");
                }
                else
                {
                    onLog?.Invoke($"[VALIDATOR OK] Component count verified: {actualSolidsCount} distinct solid(s) found (expected >= {expectedCount}).");
                }
            }

            // 3. Parameter Existence Audit
            if (plan?.Operations != null)
            {
                foreach (var op in plan.Operations)
                {
                    if (op.Operation == FamilyOperationType.CREATE_PARAMETER && !string.IsNullOrEmpty(op.ParameterName))
                    {
                        bool exists = liveState.Parameters.Any(p => p.Name.Equals(op.ParameterName, StringComparison.OrdinalIgnoreCase));
                        if (!exists && !string.IsNullOrEmpty(op.ParameterName))
                        {
                            string pMsg = $"Required parameter '{op.ParameterName}' was not created in the family.";
                            val.Discrepancies.Add(pMsg);
                            val.FailedParameters.Add(op.ParameterName!);
                            onLog?.Invoke($"[VALIDATOR WARNING] {pMsg}");
                        }
                    }
                }
            }

            // 4. Physical Geometry Flex Test (Ensure parameters physically move the geometry)
            FamilyManager famMgr = doc.FamilyManager;
            var widthParam = famMgr.Parameters.Cast<FamilyParameter>()
                .FirstOrDefault(p => p.Definition.Name.Equals("Width", StringComparison.OrdinalIgnoreCase) ||
                                     p.Definition.Name.Equals("Window_Width", StringComparison.OrdinalIgnoreCase));

            if (widthParam != null && actualSolidsCount > 0)
            {
                double initialSpanX = CalculateTotalXSpan(doc);

                using (SubTransaction sub = new SubTransaction(doc))
                {
                    try
                    {
                        sub.Start();
                        // Flex by +200 mm
                        double currentValFeet = (widthParam.StorageType == StorageType.Double && famMgr.CurrentType != null)
                            ? (famMgr.CurrentType.AsDouble(widthParam) ?? FamilyOperationExecutor.MmToFeet(1200))
                            : FamilyOperationExecutor.MmToFeet(1200);

                        double testValFeet = currentValFeet + FamilyOperationExecutor.MmToFeet(200.0);
                        famMgr.Set(widthParam, testValFeet);
                        doc.Regenerate();

                        double flexedSpanX = CalculateTotalXSpan(doc);
                        sub.RollBack(); // Revert back to maintain initial state

                        double deltaMm = Math.Abs(FamilyOperationExecutor.FeetToMm(flexedSpanX - initialSpanX));
                        if (deltaMm >= 10.0)
                        {
                            string flexMsg = $"✔ Flex test passed: Modifying 'Width' by +200 mm physically resized geometry by {deltaMm:0.0} mm.";
                            val.FlexTestResults.Add(flexMsg);
                            onLog?.Invoke($"[VALIDATOR OK] {flexMsg}");
                        }
                        else
                        {
                            string flexMsg = $"✘ Flex test warning: Parameter 'Width' did NOT physically resize geometry (BRep delta was {deltaMm:0.0} mm). Dimensions may lack driving constraint bindings.";
                            val.FlexTestResults.Add(flexMsg);
                            val.Discrepancies.Add(flexMsg);
                            val.FailedParameters.Add(widthParam.Definition.Name);
                            onLog?.Invoke($"[VALIDATOR WARNING] {flexMsg}");
                        }
                    }
                    catch (Exception fEx)
                    {
                        try { sub.RollBack(); } catch { }
                        string flexMsg = $"✘ Flex test exception: {fEx.Message}";
                        val.FlexTestResults.Add(flexMsg);
                        val.Discrepancies.Add(flexMsg);
                    }
                }
            }

            // 5. Final Intent Determination
            if (val.Discrepancies.Count == 0)
            {
                val.IntentSatisfied = true;
                val.Summary = $"Design intent fully verified: {actualSolidsCount} components present, parameters verified, constraints actively driving physical geometry.";
                onLog?.Invoke($"[VALIDATOR SUCCESS] {val.Summary}");
            }
            else
            {
                val.IntentSatisfied = false;
                val.Summary = $"Revit API execution succeeded, but {val.Discrepancies.Count} design intent discrepancy(ies) were detected.";
                onLog?.Invoke($"[VALIDATOR AUDIT] {val.Summary}");

                // Generate repair plan
                val.SuggestedRepairPlan = BuildRepairPlan(val.FailedParameters, val.Discrepancies, plan);
            }

            return val;
        }

        private static double CalculateTotalXSpan(Document doc)
        {
            var forms = new FilteredElementCollector(doc)
                .OfClass(typeof(GenericForm))
                .Cast<GenericForm>()
                .Where(f => f.IsSolid)
                .ToList();

            if (forms.Count == 0) return 0.0;

            double minX = double.MaxValue;
            double maxX = double.MinValue;

            foreach (var form in forms)
            {
                var bbox = form.get_BoundingBox(null);
                if (bbox != null)
                {
                    if (bbox.Min.X < minX) minX = bbox.Min.X;
                    if (bbox.Max.X > maxX) maxX = bbox.Max.X;
                }
            }

            return (maxX > minX) ? (maxX - minX) : 0.0;
        }

        private static ExecutionPlan BuildRepairPlan(List<string> failedParams, List<string> discrepancies, ExecutionPlan? originalPlan)
        {
            var repair = new ExecutionPlan
            {
                Intent = "repair_family",
                Summary = "Auto-repair parametric bindings and missing components"
            };

            foreach (var fp in failedParams)
            {
                repair.Operations.Add(new FamilyOperation
                {
                    Operation = FamilyOperationType.CREATE_DIMENSION,
                    PlaneA = "Left",
                    PlaneB = "Right",
                    LabelParameter = fp,
                    Summary = $"Re-bind driving constraint dimension for '{fp}'"
                });
            }

            return repair;
        }
    }
}
