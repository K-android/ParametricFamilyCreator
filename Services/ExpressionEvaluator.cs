using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    /// <summary>
    /// Evaluates parametric arithmetic expressions referencing Revit family parameters
    /// (e.g., "-Width / 2", "Width - 2 * Material_Thickness", "Height - Toe_Kick_Height").
    /// Inspired by ArchSmarterFamFab's deterministic expression evaluation pattern.
    /// </summary>
    public static class ExpressionEvaluator
    {
        private static readonly Regex TokenRegex = new Regex(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

        /// <summary>
        /// Builds a normalized parameter value dictionary (in millimeters) from the current live FamilyState
        /// overlaid with any CREATE_PARAMETER / SET_PARAMETER operations in the ExecutionPlan.
        /// Supports both exact names ("Frame Width") and underscore aliases ("Frame_Width").
        /// </summary>
        public static Dictionary<string, double> BuildParameterMap(FamilyState? state, ExecutionPlan? plan)
        {
            var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            void Register(string? name, double valMm)
            {
                if (string.IsNullOrWhiteSpace(name)) return;
                string trimmed = name!.Trim();
                map[trimmed] = valMm;
                string underscored = trimmed.Replace(' ', '_');
                map[underscored] = valMm;
                string spaced = trimmed.Replace('_', ' ');
                map[spaced] = valMm;
            }

            if (state?.Parameters != null)
            {
                foreach (var p in state.Parameters)
                {
                    if (p.ValueNumeric.HasValue)
                    {
                        Register(p.Name, p.ValueNumeric.Value);
                    }
                }
            }

            if (plan?.Operations != null)
            {
                // Pass 1: Explicit values
                foreach (var op in plan.Operations)
                {
                    if ((op.Operation == FamilyOperationType.CREATE_PARAMETER ||
                         op.Operation == FamilyOperationType.SET_PARAMETER) &&
                        !string.IsNullOrWhiteSpace(op.ParameterName) &&
                        op.Value.HasValue)
                    {
                        Register(op.ParameterName, op.Value.Value);
                    }
                }

                // Pass 2: Formula-driven values (up to 3 passes for chained formulas)
                for (int pass = 0; pass < 3; pass++)
                {
                    foreach (var op in plan.Operations)
                    {
                        if ((op.Operation == FamilyOperationType.CREATE_PARAMETER ||
                             op.Operation == FamilyOperationType.CREATE_FORMULA) &&
                            !string.IsNullOrWhiteSpace(op.ParameterName) &&
                            !string.IsNullOrWhiteSpace(op.Formula))
                        {
                            if (TryEvaluate(op.Formula, map, out double computed))
                            {
                                Register(op.ParameterName, computed);
                            }
                        }
                    }
                }
            }

            return map;
        }

        /// <summary>
        /// Evaluates an expression string (or returns the fallback literal if expression is null/empty).
        /// All values are in millimeters.
        /// </summary>
        public static double EvaluateOrDefault(
            string? expr,
            double? fallbackMm,
            Dictionary<string, double> paramMap,
            double defaultVal = 0.0)
        {
            if (!string.IsNullOrWhiteSpace(expr))
            {
                if (TryEvaluate(expr, paramMap, out double result))
                {
                    return result;
                }
            }

            return fallbackMm ?? defaultVal;
        }

        /// <summary>
        /// Attempts to evaluate a mathematical expression string against the parameter dictionary.
        /// Supports numbers, parameter names (with spaces or underscores), +, -, *, /, and parentheses.
        /// </summary>
        public static bool TryEvaluate(string? expr, Dictionary<string, double> paramMap, out double result)
        {
            result = 0.0;
            if (string.IsNullOrWhiteSpace(expr))
                return false;

            string trimmed = expr!.Trim();

            // Direct numeric literal fast-path
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
                return true;

            // Direct parameter lookup fast-path
            if (paramMap != null && paramMap.TryGetValue(trimmed, out result))
                return true;

            try
            {
                string resolved = trimmed;
                if (paramMap != null && paramMap.Count > 0)
                {
                    // Sort parameter keys longest-first so "Frame Width" or "Frame_Width" is substituted before "Width"
                    var orderedKeys = paramMap.Keys
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .OrderByDescending(k => k.Length)
                        .ToList();

                    foreach (string key in orderedKeys)
                    {
                        if (resolved.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            // Ensure word boundary matching so "Width" doesn't replace inside "DoorWidth"
                            string pattern = $@"(?<![A-Za-z0-9_]){Regex.Escape(key)}(?![A-Za-z0-9_])";
                            string replacement = paramMap[key].ToString("G17", CultureInfo.InvariantCulture);
                            resolved = Regex.Replace(resolved, pattern, $"({replacement})", RegexOptions.IgnoreCase);
                        }
                    }
                }

                // Strip common unit suffixes if Gemini included "mm" in the expression
                resolved = Regex.Replace(resolved, @"\bmm\b", "", RegexOptions.IgnoreCase);

                using var table = new DataTable();
                table.Locale = CultureInfo.InvariantCulture;
                object evalObj = table.Compute(resolved, string.Empty);
                if (evalObj != null && evalObj != DBNull.Value)
                {
                    result = Convert.ToDouble(evalObj, CultureInfo.InvariantCulture);
                    return !double.IsNaN(result) && !double.IsInfinity(result);
                }
            }
            catch
            {
                // Ignore and return false so caller can fall back
            }

            return false;
        }

        /// <summary>
        /// Extracts identifier tokens from an expression string (excluding unit literals like "mm").
        /// </summary>
        public static List<string> ExtractParameterTokens(string? expr)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(expr))
                return tokens;

            foreach (Match m in TokenRegex.Matches(expr!))
            {
                string val = m.Value;
                if (string.Equals(val, "mm", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(val, "deg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(val, "in", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(val, "ft", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!tokens.Contains(val, StringComparer.OrdinalIgnoreCase))
                {
                    tokens.Add(val);
                }
            }

            return tokens;
        }

        /// <summary>
        /// Computes the clear gap between N evenly-spaced internal divisions (e.g., shelves, mullions, Louver blades)
        /// bounded by top and bottom panels of the same thickness:
        /// spacing = (totalSpan - (count + 2) * thickness) / (count + 1)
        /// </summary>
        public static double CalculateEvenSpacing(double totalSpanMm, int divisionCount, double thicknessMm)
        {
            if (divisionCount <= 0) return Math.Max(0, totalSpanMm - 2 * thicknessMm);
            return (totalSpanMm - (divisionCount + 2) * thicknessMm) / (divisionCount + 1);
        }

        /// <summary>
        /// Computes the bottom/center offset for the K-th division (1-based: K = 1..N):
        /// offset_K = K * spacing + (K + 1) * thickness
        /// </summary>
        public static double CalculateDivisionOffset(int oneBasedIndex, double spacingMm, double thicknessMm)
        {
            return oneBasedIndex * spacingMm + (oneBasedIndex + 1) * thicknessMm;
        }
    }
}
