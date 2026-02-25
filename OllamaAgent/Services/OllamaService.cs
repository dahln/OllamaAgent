using System.Text;
using System.Text.Json;
using OllamaAgent.Models;

namespace OllamaAgent.Services;

/// <summary>
/// The AI brain of the agent. Communicates with a local Ollama instance and manages
/// a comprehensive prompt library to drive all agent behavior.
///
/// Responsibilities:
/// 1. HTTP communication with Ollama (streaming chat, model management).
/// 2. Task classification — analyzes user prompts to determine domain, language, and complexity.
/// 3. Prompt composition — selects and combines prompts from PromptLibrary based on classification.
///
/// Design principle: Coding logic is minimal. AI prompts do the work.
/// This service composes the right prompts and lets the LLM make all domain decisions.
/// </summary>
public class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly string _model;
    private readonly string _baseUrl;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public OllamaService(string model, string baseUrl = "http://localhost:11434")
    {
        _model = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  TASK CLASSIFICATION — Uses AI to determine the right prompts to use
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Analyzes the user's prompt using AI to classify the task into a category, language,
    /// framework, required capabilities, and complexity level. The classification drives
    /// which prompts from the PromptLibrary are selected for each execution phase.
    /// Falls back to sensible defaults if the AI response cannot be parsed.
    /// </summary>
    public async Task<TaskClassification> ClassifyTaskAsync(
        string userPrompt, CancellationToken cancellationToken = default)
    {
        var messages = new List<OllamaChatMessage>
        {
            new() { Role = "system", Content = PromptLibrary.Core.TaskClassification() },
            new() { Role = "user", Content = userPrompt },
        };

        try
        {
            var rawResponse = await StreamChatAsync(
                messages, format: PromptLibrary.Schemas.TaskClassification, cancellationToken: cancellationToken);

            var classification = JsonSerializer.Deserialize<TaskClassification>(rawResponse, _jsonOptions);
            if (classification is not null)
                return classification;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Task classification failed ({ex.Message}), using defaults.");
        }

        // Sensible fallback: coding/none
        return new TaskClassification
        {
            PrimaryCategory = "coding",
            Language = "none",
            Framework = "none",
            RequiredCapabilities = new List<string> { "file_writing" },
            Complexity = "moderate",
        };
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PROMPT COMPOSITION — Selects and combines prompts for each phase
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Composes the initial messages for the planning phase, selecting appropriate prompts
    /// based on the task classification.
    /// </summary>
    public List<OllamaChatMessage> ComposePlanningMessages(
        string userPrompt, TaskClassification classification)
    {
        return new List<OllamaChatMessage>
        {
            new()
            {
                Role = "system",
                Content = PromptLibrary.ComposePlanningSystemPrompt(classification),
            },
            new() { Role = "user", Content = userPrompt },
        };
    }

    /// <summary>
    /// Composes the initial messages for a step execution iteration, combining domain-specific
    /// coding/writing guidance, guard prompts, and iteration awareness based on the classification.
    /// </summary>
    public List<OllamaChatMessage> ComposeStepExecutionMessages(
        ExecutionStep step,
        string originalTask,
        TaskClassification classification,
        string workDir,
        int iteration,
        int maxIterations,
        IReadOnlyList<string>? recentCommands = null)
    {
        string systemPrompt = PromptLibrary.ComposeExecutionSystemPrompt(
            workDir, originalTask, classification, iteration, maxIterations, recentCommands);

        return new List<OllamaChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new() { Role = "user", Content = $"Execute step {step.StepNumber}: {step.Description}" },
        };
    }

    /// <summary>
    /// Composes the initial messages for the finalization phase when deliverables are missing.
    /// </summary>
    public List<OllamaChatMessage> ComposeFinalizationMessages(
        string originalTask,
        string taskTitle,
        TaskClassification classification,
        string workDir,
        string workspaceState,
        string researchContext)
    {
        string systemPrompt = PromptLibrary.ComposeFinalizationSystemPrompt(
            workDir, originalTask, taskTitle, classification);

        return new List<OllamaChatMessage>
        {
            new() { Role = "system", Content = systemPrompt },
            new()
            {
                Role = "user",
                Content = $"The workspace deliverable files are empty or missing.\n"
                         + $"Current workspace state:\n{workspaceState}\n\n"
                         + $"Use the following research and work to write the complete deliverables:\n\n"
                         + researchContext,
            },
        };
    }

    /// <summary>
    /// Composes a user-role message to inject into conversation when a command loop is detected.
    /// This allows the orchestrator to break infinite loops by giving the AI explicit guidance.
    /// </summary>
    public OllamaChatMessage ComposeLoopBreaker(
        IReadOnlyList<string> recentCommands, string originalTask)
    {
        return new OllamaChatMessage
        {
            Role = "user",
            Content = PromptLibrary.Guards.LoopDetection(recentCommands)
                    + $"\n\nRemember, the original task is: {originalTask}\n"
                    + "Focus on producing the deliverable. Skip anything that isn't working.",
        };
    }

    /// <summary>
    /// Composes a user-role message providing structured error recovery guidance
    /// after a command failure.
    /// </summary>
    public OllamaChatMessage ComposeErrorGuidance(string command, string error, long exitCode)
    {
        return new OllamaChatMessage
        {
            Role = "user",
            Content = PromptLibrary.ErrorRecovery.CommandFailed(command, error, exitCode),
        };
    }

    /// <summary>
    /// Ensures the configured model is available locally, pulling it from the Ollama
    /// registry if it has not been downloaded yet.
    /// </summary>
    public async Task EnsureModelPulledAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Ollama] Checking whether model '{_model}' is available locally…");

        // Query the list of locally available models.
        try
        {
            var tagsResponse = await _httpClient.GetAsync($"{_baseUrl}/api/tags", cancellationToken);
            if (tagsResponse.IsSuccessStatusCode)
            {
                var tagsJson = await tagsResponse.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(tagsJson);
                if (doc.RootElement.TryGetProperty("models", out var models))
                {
                    foreach (var model in models.EnumerateArray())
                    {
                        if (model.TryGetProperty("name", out var nameEl))
                        {
                            var name = nameEl.GetString() ?? string.Empty;
                            // Model names may include a tag (e.g. "llama3.2:latest"), so match on prefix.
                            if (name.Equals(_model, StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith(_model + ":", StringComparison.OrdinalIgnoreCase))
                            {
                                Console.WriteLine($"[Ollama] Model '{_model}' is already available.");
                                return;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Ollama] Warning: could not query local models – {ex.Message}");
        }

        // Model not found locally – pull it.
        Console.WriteLine($"[Ollama] Pulling model '{_model}'… (this may take a while)");

        var pullJson = JsonSerializer.Serialize(new { name = _model, stream = true });
        var content = new StringContent(pullJson, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync($"{_baseUrl}/api/pull", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("status", out var statusEl))
                {
                    var status = statusEl.GetString();
                    if (!string.IsNullOrEmpty(status))
                        Console.WriteLine($"[Ollama] {status}");

                    if (status == "success")
                        break;
                }
            }
            catch (JsonException) { }
        }

        Console.WriteLine($"[Ollama] Model '{_model}' is ready.");
    }

    /// <summary>
    /// Sends a chat request to Ollama, streams each token to the console, and returns
    /// the full response text. When <paramref name="format"/> is provided, Ollama will
    /// return structured JSON (the tokens still stream live to the terminal).
    /// </summary>
    public async Task<string> StreamChatAsync(
        List<OllamaChatMessage> messages,
        object? format = null,
        CancellationToken cancellationToken = default)
    {
        var request = new OllamaChatRequest
        {
            Model = _model,
            Messages = messages,
            Stream = true,
            Format = format,
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var response = await _httpClient.PostAsync(
            $"{_baseUrl}/api/chat", content, cancellationToken);
        response.EnsureSuccessStatusCode();

        var fullResponse = new StringBuilder();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            OllamaChatResponse? chatResponse;
            try
            {
                chatResponse = JsonSerializer.Deserialize<OllamaChatResponse>(line, _jsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chatResponse?.Message?.Content is { } token)
            {
                Console.Write(token);
                fullResponse.Append(token);
            }

            if (chatResponse?.Done == true)
                break;
        }

        Console.WriteLine();
        return fullResponse.ToString();
    }
}
