using System.Text.Json.Serialization;

namespace OllamaAgent.Models;

public class TaskPlan
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("steps")]
    public List<ExecutionStep> Steps { get; set; } = new();
}
