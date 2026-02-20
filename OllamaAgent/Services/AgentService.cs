using System.Text.Json;
using OllamaAgent.Models;

namespace OllamaAgent.Services;

/// <summary>
/// Orchestrates the full agentic flow: plan generation → sandbox creation → step execution
/// → artifact collection → sandbox teardown.
/// </summary>
public class AgentService
{
    private readonly OllamaService _ollama;
    private readonly DockerService _docker;

    // JSON schema objects used as the Ollama `format` parameter for structured output.
    private static readonly object TaskPlanSchema = new
    {
        type = "object",
        properties = new
        {
            title = new { type = "string", description = "A concise 2-5 word title for the task." },
            steps = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        stepNumber = new { type = "integer" },
                        description = new { type = "string" },
                    },
                    required = new[] { "stepNumber", "description" },
                },
            },
        },
        required = new[] { "title", "steps" },
    };

    private static readonly object StepCommandSchema = new
    {
        type = "object",
        properties = new
        {
            command = new { type = "string", description = "Shell command to execute in the sandbox. Empty string when done." },
            done = new { type = "boolean", description = "True when this step is fully complete." },
            message = new { type = "string", description = "Brief explanation of the action or result." },
        },
        required = new[] { "command", "done", "message" },
    };

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Maximum command iterations per step before forcibly moving on.
    private const int MaxIterationsPerStep = 20;

    // Working directory inside the sandbox that will be copied to the host on completion.
    private const string SandboxWorkDir = "/workspace";

    public AgentService(OllamaService ollama, DockerService docker)
    {
        _ollama = ollama;
        _docker = docker;
    }

    /// <summary>
    /// Runs the complete agent lifecycle for a single user task.
    /// Returns the path on the host where deliverables were saved.
    /// </summary>
    public async Task<string> RunTaskAsync(
        string userPrompt, CancellationToken cancellationToken = default)
    {
        // ── Phase 1: Generate title + execution plan ─────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 1 – Generating execution plan…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        var planMessages = new List<OllamaChatMessage>
        {
            new()
            {
                Role = "system",
                Content = """
                    You are a task-planning AI. Given a user's task description, produce a structured
                    execution plan with:
                    - A concise 2-5 word title (field: "title").
                    - An ordered list of steps (field: "steps"), each with a "stepNumber" (integer)
                      and a "description" (string). Keep the plan to 3-8 steps.
                    Respond with valid JSON only, matching the provided schema.
                    """,
            },
            new() { Role = "user", Content = userPrompt },
        };

        var planJson = await _ollama.StreamChatAsync(
            planMessages, format: TaskPlanSchema, cancellationToken: cancellationToken);

        TaskPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<TaskPlan>(planJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse execution plan from Ollama response.\nRaw: {planJson}", ex);
        }

        if (plan is null || string.IsNullOrWhiteSpace(plan.Title) || plan.Steps.Count == 0)
            throw new InvalidOperationException("Ollama returned an empty execution plan.");

        // Build a safe folder name: title + UTC timestamp.
        var safeTitle = string.Concat(
            plan.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var outputFolder = Path.Combine(
            Environment.CurrentDirectory, "tasks", $"{safeTitle}_{timestamp}");

        Console.WriteLine();
        Console.WriteLine($"  Task title : {plan.Title}");
        Console.WriteLine($"  Steps      : {plan.Steps.Count}");
        Console.WriteLine($"  Output dir : {outputFolder}");
        Console.WriteLine();

        // ── Phase 2: Start sandbox ────────────────────────────────────────────
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 2 – Starting Docker sandbox…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        await _docker.StartSandboxAsync(cancellationToken);

        // Create the workspace directory inside the sandbox.
        await _docker.ExecuteCommandAsync($"mkdir -p {SandboxWorkDir}", cancellationToken);

        // ── Phase 3: Execute each step ────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 3 – Executing steps…");
        Console.WriteLine("══════════════════════════════════════════");

        foreach (var step in plan.Steps.OrderBy(s => s.StepNumber))
        {
            await ExecuteStepAsync(step, userPrompt, cancellationToken);
        }

        // ── Phase 4: Collect deliverables ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 4 – Collecting deliverables…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        try
        {
            await _docker.CopyFromContainerAsync(SandboxWorkDir, outputFolder, cancellationToken);
            Console.WriteLine($"  Files saved to: {outputFolder}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not copy files from sandbox – {ex.Message}");
        }

        // ── Phase 5: Teardown ─────────────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 5 – Shutting down sandbox…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        await _docker.StopAndRemoveSandboxAsync(cancellationToken);

        return outputFolder;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ExecuteStepAsync(
        ExecutionStep step, string originalTask, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"┌─ Step {step.StepNumber}: {step.Description}");
        Console.WriteLine("│");

        var stepMessages = new List<OllamaChatMessage>
        {
            new()
            {
                Role = "system",
                Content = $$"""
                    You are an AI agent executing a task inside a Docker sandbox (Ubuntu 24.04).
                    The working directory is "{{SandboxWorkDir}}". All deliverables must be placed there.
                    
                    For each response, output ONLY valid JSON matching this structure:
                    {
                      "command": "<shell command to run, or empty string if this step is done>",
                      "done": <true when step is complete, false otherwise>,
                      "message": "<brief explanation of what you are doing or what was accomplished>"
                    }
                    
                    Original task: {{originalTask}}
                    """,
            },
            new()
            {
                Role = "user",
                Content = $"Execute step {step.StepNumber}: {step.Description}",
            },
        };

        bool stepDone = false;

        for (int iteration = 0; iteration < MaxIterationsPerStep; iteration++)
        {
            Console.WriteLine($"│  [iteration {iteration + 1}]");

            var rawResponse = await _ollama.StreamChatAsync(
                stepMessages, format: StepCommandSchema, cancellationToken: cancellationToken);

            StepCommand? cmd;
            try
            {
                cmd = JsonSerializer.Deserialize<StepCommand>(rawResponse, _jsonOptions);
            }
            catch (JsonException)
            {
                Console.WriteLine($"│  ⚠ Could not parse AI response as JSON – skipping iteration.");
                break;
            }

            if (cmd is null)
            {
                Console.WriteLine("│  ⚠ Null AI response – ending step.");
                break;
            }

            Console.WriteLine($"│  AI: {cmd.Message}");

            if (cmd.Done)
            {
                Console.WriteLine($"│  ✓ Step {step.StepNumber} complete.");
                stepDone = true;
                break;
            }

            if (!string.IsNullOrWhiteSpace(cmd.Command))
            {
                Console.WriteLine($"│  $ {cmd.Command}");
                var (stdout, stderr, exitCode) = await _docker.ExecuteCommandAsync(
                    cmd.Command, cancellationToken);

                if (!string.IsNullOrEmpty(stdout))
                    Console.WriteLine($"│  stdout: {stdout}");
                if (!string.IsNullOrEmpty(stderr))
                    Console.WriteLine($"│  stderr: {stderr}");
                Console.WriteLine($"│  exit: {exitCode}");

                // Feed the result back to the AI.
                stepMessages.Add(new OllamaChatMessage
                {
                    Role = "assistant",
                    Content = rawResponse,
                });
                stepMessages.Add(new OllamaChatMessage
                {
                    Role = "user",
                    Content = $"Command: {cmd.Command}\nExit code: {exitCode}\n"
                              + $"stdout:\n{stdout}\n"
                              + $"stderr:\n{stderr}",
                });
            }
        }

        if (!stepDone)
            Console.WriteLine("│  ⚠ Max iterations reached – moving to next step.");

        Console.WriteLine("└─────────────────────────────────────────");
    }
}
