using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Represents the high-level design intent and ordered atomic operations synthesized by the AI planner.
    /// </summary>
    public class ExecutionPlan
    {
        [JsonPropertyName("intent")]
        public string Intent { get; set; } = "create_family"; // create_family, modify_family, repair_family, inspect_family, flex_test

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("target_family_name")]
        public string? TargetFamilyName { get; set; }

        [JsonPropertyName("family_category")]
        public string? FamilyCategory { get; set; } = "Windows";

        [JsonPropertyName("template_name")]
        public string? TemplateName { get; set; } = "Metric Window.rft";

        /// <summary>
        /// Expected architectural geometry components to verify post-execution (e.g. "Left Frame", "Glazing").
        /// </summary>
        [JsonPropertyName("expected_components")]
        public List<string> ExpectedComponents { get; set; } = new();

        /// <summary>
        /// Parameters that must be dynamically flex-tested in Revit to verify they actually drive geometry.
        /// </summary>
        [JsonPropertyName("parameters_to_flex")]
        public List<string> ParametersToFlex { get; set; } = new();

        /// <summary>
        /// The sequence of deterministic Revit operations.
        /// </summary>
        [JsonPropertyName("operations")]
        public List<FamilyOperation> Operations { get; set; } = new();
    }
}
