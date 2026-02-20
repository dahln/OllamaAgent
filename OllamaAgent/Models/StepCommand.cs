using System.Text.Json.Serialization;

namespace OllamaAgent.Models;

/// <summary>
/// The structured JSON response the AI returns for each step-execution iteration.
/// </summary>
public class StepCommand
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("done")]
    public bool Done { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
