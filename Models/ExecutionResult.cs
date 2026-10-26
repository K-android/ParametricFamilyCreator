using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OperationStatus
    {
        PLANNED,
        EXECUTING,
        API_SUCCESS,
        SUCCESS,
        PARTIAL_SUCCESS,
        FAILED,
        VALIDATION_FAILED
    }

    public class OperationExecutionResult
    {
        [JsonPropertyName("operation")]
        public FamilyOperation? Operation { get; set; }

        [JsonPropertyName("status")]
        public OperationStatus Status { get; set; } = OperationStatus.PLANNED;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("created_element_id")]
        public int? CreatedElementId { get; set; }
    }

    public class ExecutionResult
    {
        [JsonPropertyName("overall_status")]
        public OperationStatus OverallStatus { get; set; } = OperationStatus.PLANNED;

        [JsonPropertyName("api_success")]
        public bool ApiSuccess { get; set; } = false;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("operation_results")]
        public List<OperationExecutionResult> OperationResults { get; set; } = new();

        [JsonPropertyName("rolled_back")]
        public bool RolledBack { get; set; } = false;
    }
}
