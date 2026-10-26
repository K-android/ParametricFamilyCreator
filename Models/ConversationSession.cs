using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ParametricFamilyForge.Models
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ChatRole
    {
        User,
        Assistant,
        System
    }

    public class ChatMessage
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("role")]
        public ChatRole Role { get; set; } = ChatRole.User;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("timestamp")]
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        [JsonPropertyName("linked_plan")]
        public ExecutionPlan? LinkedPlan { get; set; }

        [JsonPropertyName("linked_execution_result")]
        public ExecutionResult? LinkedExecutionResult { get; set; }

        [JsonPropertyName("linked_validation_result")]
        public ValidationResult? LinkedValidationResult { get; set; }

        [JsonPropertyName("attached_image_or_pdf")]
        public string? AttachedImageOrPdf { get; set; }
    }

    public class ConversationSession
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = Guid.NewGuid().ToString("N");

        [JsonPropertyName("active_family_name")]
        public string? ActiveFamilyName { get; set; }

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("last_inspected_state")]
        public FamilyState? LastInspectedState { get; set; }

        public void AddUserMessage(string content, string? attachmentPath = null)
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.User,
                Content = content,
                AttachedImageOrPdf = attachmentPath
            });
        }

        public void AddAssistantMessage(string content, ExecutionPlan? plan = null, ExecutionResult? execResult = null, ValidationResult? valResult = null)
        {
            Messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                Content = content,
                LinkedPlan = plan,
                LinkedExecutionResult = execResult,
                LinkedValidationResult = valResult
            });
        }
    }
}
