using System.Text.Json.Serialization;

namespace OllamaAgent.Models;

public class ExecutionStep
{
    [JsonPropertyName("stepNumber")]
    public int StepNumber { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
