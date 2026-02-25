using System.Text.Json.Serialization;

namespace OllamaAgent.Models;

/// <summary>
/// AI-generated classification of a user task, used to select the appropriate
/// prompts from the PromptLibrary for each execution phase.
/// </summary>
public class TaskClassification
{
    [JsonPropertyName("primaryCategory")]
    public string PrimaryCategory { get; set; } = "coding";

    [JsonPropertyName("language")]
    public string Language { get; set; } = "none";

    [JsonPropertyName("framework")]
    public string Framework { get; set; } = "none";

    [JsonPropertyName("requiredCapabilities")]
    public List<string> RequiredCapabilities { get; set; } = new();

    [JsonPropertyName("complexity")]
    public string Complexity { get; set; } = "moderate";

    public override string ToString() =>
        $"Category={PrimaryCategory}, Language={Language}, Framework={Framework}, Complexity={Complexity}";
}
