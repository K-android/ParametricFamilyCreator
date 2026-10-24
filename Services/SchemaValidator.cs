using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ParametricFamilyForge.Models;

namespace ParametricFamilyForge.Services
{
    public class ValidationReport
    {
        public bool IsValid => Errors.Count == 0;
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
        public int ParameterCount { get; set; }
        public int ExtrusionCount { get; set; }
        public int BlendCount { get; set; }
        public int SweepCount { get; set; }
        public int NestedFamilyCount { get; set; }
        public int TypeCount { get; set; }
        public int ReferencePlaneCount { get; set; }

        public string SummaryText
        {
            get
            {
                string status = IsValid ? "✅ VALID" : "❌ INVALID";
                string geomDetails = $"{ExtrusionCount} Extrusions";
                if (BlendCount > 0) geomDetails += $", {BlendCount} Blends";
                if (SweepCount > 0) geomDetails += $", {SweepCount} Sweeps";
                return $"{status} | {ParameterCount} Parameters, {geomDetails}, {NestedFamilyCount} Nested, {TypeCount} Types";
            }
        }
    }

    /// <summary>
    /// Static analysis and diagnostic validation engine for FamilySchema instances.
    /// Catches geometry anomalies, missing bindings, duplicate parameters, and Revit API constraints before execution.
    /// </summary>
    public static class SchemaValidator
    {
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

        public static ValidationReport Validate(FamilySchema? schema)
        {
            var report = new ValidationReport();

            if (schema == null)
            {
                report.Errors.Add("FamilySchema is null or failed to deserialize.");
                return report;
            }

            // 1. Validate Master Family Name
            if (string.IsNullOrWhiteSpace(schema.MasterFamilyName))
            {
                report.Errors.Add("Master family name cannot be empty.");
            }
            else
            {
                if (schema.MasterFamilyName.IndexOfAny(InvalidFileNameChars) >= 0)
                {
                    report.Errors.Add($"Master family name '{schema.MasterFamilyName}' contains invalid filesystem characters.");
                }
            }

            // 2. Validate Parameters
            var paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            report.ParameterCount = schema.MasterParameters?.Count ?? 0;

            if (schema.MasterParameters != null)
            {
                foreach (var param in schema.MasterParameters)
                {
                    if (string.IsNullOrWhiteSpace(param.Name))
                    {
                        report.Errors.Add("Master parameter found with an empty name.");
                        continue;
                    }

                    if (paramNames.Contains(param.Name))
                    {
                        report.Errors.Add($"Duplicate master parameter name detected: '{param.Name}'. Revit requires unique parameter names.");
                    }
                    else
                    {
                        paramNames.Add(param.Name);
                    }

                    // Validate formula parenthesis balance
                    if (!string.IsNullOrWhiteSpace(param.Formula))
                    {
                        int openCount = param.Formula!.Count(c => c == '(');
                        int closeCount = param.Formula!.Count(c => c == ')');
                        if (openCount != closeCount)
                        {
                            report.Warnings.Add($"Formula for parameter '{param.Name}' has unbalanced parentheses: '{param.Formula}'.");
                        }
                    }
                }
            }

            // 3. Validate Geometry Extrusions, Blends, and Sweeps
            int totalExtrusions = schema.HostExtrusions?.Count ?? 0;
            int totalBlends = schema.HostBlends?.Count ?? 0;
            int totalSweeps = schema.HostSweeps?.Count ?? 0;
            report.ExtrusionCount = totalExtrusions;
            report.BlendCount = totalBlends;
            report.SweepCount = totalSweeps;
            report.NestedFamilyCount = schema.NestedFamilies?.Count ?? 0;

            if (totalExtrusions == 0 && totalBlends == 0 && totalSweeps == 0 && report.NestedFamilyCount == 0)
            {
                report.Warnings.Add("The family has 0 host extrusions, blends, sweeps, and nested families. A hollow family will be produced.");
            }

            if (schema.HostExtrusions != null)
            {
                for (int i = 0; i < schema.HostExtrusions.Count; i++)
                {
                    var ext = schema.HostExtrusions[i];
                    string extName = string.IsNullOrWhiteSpace(ext.Name) ? $"Extrusion #{i + 1}" : ext.Name;

                    if (ext.StartZ_mm >= ext.EndZ_mm)
                    {
                        report.Warnings.Add($"{extName}: start_z_mm ({ext.StartZ_mm}) >= end_z_mm ({ext.EndZ_mm}). Height will be automatically adjusted.");
                    }

                    if (string.Equals(ext.ProfileType, "Cylinder", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ext.Radius_mm <= 0 && Math.Abs(ext.MaxX_mm - ext.MinX_mm) <= 0.001)
                        {
                            report.Warnings.Add($"{extName}: Cylinder radius is zero or unconfigured.");
                        }
                    }
                    else
                    {
                        if (ext.MaxX_mm <= ext.MinX_mm)
                        {
                            report.Warnings.Add($"{extName}: min_x_mm ({ext.MinX_mm}) >= max_x_mm ({ext.MaxX_mm}).");
                        }
                        if (ext.MaxY_mm <= ext.MinY_mm)
                        {
                            report.Warnings.Add($"{extName}: min_y_mm ({ext.MinY_mm}) >= max_y_mm ({ext.MaxY_mm}).");
                        }
                    }

                    // Check associated parameter existence
                    CheckParamExists(ext.HeightParameter, "HeightParameter", extName, paramNames, report);
                    CheckParamExists(ext.WidthParameter, "WidthParameter", extName, paramNames, report);
                    CheckParamExists(ext.DepthParameter, "DepthParameter", extName, paramNames, report);
                    CheckParamExists(ext.MaterialParameter, "MaterialParameter", extName, paramNames, report);
                    CheckParamExists(ext.VisibilityParameter, "VisibilityParameter", extName, paramNames, report);
                }
            }

            // 4. Validate Reference Planes
            report.ReferencePlaneCount = schema.ReferencePlanes?.Count ?? 0;
            if (schema.ReferencePlanes != null)
            {
                foreach (var rp in schema.ReferencePlanes)
                {
                    if (string.IsNullOrWhiteSpace(rp.Name))
                    {
                        report.Warnings.Add("Reference plane found with an empty name.");
                    }

                    if (!string.IsNullOrWhiteSpace(rp.DrivenByParameter) && !paramNames.Contains(rp.DrivenByParameter!))
                    {
                        report.Warnings.Add($"Reference plane '{rp.Name}' references driven parameter '{rp.DrivenByParameter}', which is not defined in master_parameters.");
                    }
                }
            }

            // 5. Validate Nested Families
            if (schema.NestedFamilies != null)
            {
                var nestedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var nested in schema.NestedFamilies)
                {
                    if (string.IsNullOrWhiteSpace(nested.NestedFamilyName))
                    {
                        report.Errors.Add("Nested sub-component found with an empty family name.");
                        continue;
                    }

                    if (nested.NestedFamilyName.Equals(schema.MasterFamilyName, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Errors.Add($"Nested family '{nested.NestedFamilyName}' cannot share the exact same name as the host family.");
                    }

                    nestedNames.Add(nested.NestedFamilyName);

                    // Check parameter bindings
                    foreach (var binding in nested.ParameterBindings)
                    {
                        if (!paramNames.Contains(binding.Value))
                        {
                            report.Warnings.Add($"Nested component '{nested.NestedFamilyName}' binds '{binding.Key}' -> '{binding.Value}', but '{binding.Value}' is not in master_parameters (it will be created automatically).");
                        }
                    }
                }
            }

            // 6. Validate Family Types (Catalog)
            report.TypeCount = schema.FamilyTypes?.Count ?? 0;
            if (schema.FamilyTypes != null)
            {
                var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var fType in schema.FamilyTypes)
                {
                    if (string.IsNullOrWhiteSpace(fType.Name))
                    {
                        report.Warnings.Add("FamilyType defined with empty name.");
                        continue;
                    }

                    if (typeNames.Contains(fType.Name))
                    {
                        report.Warnings.Add($"Duplicate family type name in catalog: '{fType.Name}'.");
                    }
                    else
                    {
                        typeNames.Add(fType.Name);
                    }
                }
            }

            return report;
        }

        private static void CheckParamExists(
            string? paramName,
            string propertyName,
            string extrusionName,
            HashSet<string> existingParams,
            ValidationReport report)
        {
            if (!string.IsNullOrWhiteSpace(paramName) && !existingParams.Contains(paramName!))
            {
                report.Warnings.Add($"{extrusionName}: {propertyName} references '{paramName}', which will be created automatically if missing.");
            }
        }
    }
}
