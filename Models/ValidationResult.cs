using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    /// <summary>
    /// Captures post-execution verification of actual Revit BRep geometry, parameters, and constraints against requested design intent.
    /// </summary>
    public class ValidationResult
    {
        [JsonPropertyName("api_success")]
        public bool ApiSuccess { get; set; } = false;

        [JsonPropertyName("intent_satisfied")]
        public bool IntentSatisfied { get; set; } = false;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("expected_components_count")]
        public int ExpectedComponentsCount { get; set; }

        [JsonPropertyName("actual_components_count")]
        public int ActualComponentsCount { get; set; }

        [JsonPropertyName("discrepancies")]
        public List<string> Discrepancies { get; set; } = new();

        [JsonPropertyName("flex_test_results")]
        public List<string> FlexTestResults { get; set; } = new();

        [JsonPropertyName("failed_parameters")]
        public List<string> FailedParameters { get; set; } = new();

        [JsonPropertyName("suggested_repair_plan")]
        public ExecutionPlan? SuggestedRepairPlan { get; set; }
    }
}
