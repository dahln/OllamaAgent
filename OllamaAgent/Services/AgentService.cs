using System.Text;
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

        // Build a safe folder name: title + UTC timestamp (no spaces or invalid chars).
        var safeTitle = string.Concat(
            plan.Title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
            .Replace(' ', '_');
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
        await _docker.ExecuteCommandAsync($"mkdir -p {SandboxWorkDir}", cancellationToken: cancellationToken);

        // ── Phase 3: Execute each step ────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 3 – Executing steps…");
        Console.WriteLine("══════════════════════════════════════════");

        var stepOutputs = new List<string>();
        foreach (var step in plan.Steps.OrderBy(s => s.StepNumber))
        {
            var output = await ExecuteStepAsync(step, userPrompt, cancellationToken);
            stepOutputs.Add(output);
        }

        // ── Phase 4: Collect deliverables ─────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 4 – Collecting deliverables…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        var executionFolder   = Path.Combine(outputFolder, "Execution");
        var deliverableFolder = Path.Combine(outputFolder, "Deliverable");

        Directory.CreateDirectory(executionFolder);
        Directory.CreateDirectory(deliverableFolder);

        // Always persist the execution log so the Execution folder is never empty.
        var executionMarkdown = BuildMarkdownSummary(plan.Title, userPrompt, stepOutputs);
        var execMdPath  = Path.Combine(executionFolder, "execution.md");
        var execTxtPath = Path.Combine(executionFolder, "execution.txt");
        await File.WriteAllTextAsync(execMdPath,  executionMarkdown,                    cancellationToken);
        await File.WriteAllTextAsync(execTxtPath, StripMarkdown(executionMarkdown),     cancellationToken);
        Console.WriteLine($"  Execution log saved to: {executionFolder}");

        // Copy whatever the sandbox produced into the Deliverable folder.
        try
        {
            await _docker.CopyFromContainerAsync(SandboxWorkDir, deliverableFolder, cancellationToken);
            bool filesCopied = new DirectoryInfo(deliverableFolder)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Any(f => f.Length > 0);
            if (filesCopied)
                Console.WriteLine($"  Deliverable files saved to: {deliverableFolder}");
            else
                Console.WriteLine("  No deliverable files were produced in the sandbox workspace.");
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

    private async Task<string> ExecuteStepAsync(
        ExecutionStep step, string originalTask, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"┌─ Step {step.StepNumber}: {step.Description}");
        Console.WriteLine("│");

        // Accumulate text output from this step for use as a fallback if the workspace is empty.
        var stepOutput = new StringBuilder();
        stepOutput.AppendLine($"## Step {step.StepNumber}: {step.Description}");
        stepOutput.AppendLine();

        var stepMessages = new List<OllamaChatMessage>
        {
            new()
            {
                Role = "system",
                Content = $$"""
                    You are an AI agent executing a task inside a Docker sandbox (Ubuntu 24.04).
                    The working directory is "{{SandboxWorkDir}}". ALL output and deliverables MUST be saved as files in that directory.

                    IMPORTANT: If the task produces any textual output (reports, summaries, analysis, answers, code, etc.),
                    you MUST write it to a file in {{SandboxWorkDir}}. Use markdown (.md) files for all text content.
                    Format all text using proper markdown (headings, lists, code blocks, bold/italic, etc.).
                    Never rely solely on stdout — always save results to files.

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
            stepOutput.AppendLine($"**{cmd.Message}**");
            stepOutput.AppendLine();

            if (cmd.Done)
            {
                Console.WriteLine($"│  ✓ Step {step.StepNumber} complete.");
                stepDone = true;
                break;
            }

            if (!string.IsNullOrWhiteSpace(cmd.Command))
            {
                Console.WriteLine($"│  $ {cmd.Command}");
                stepOutput.AppendLine($"```shell");
                stepOutput.AppendLine($"$ {cmd.Command}");
                var (stdout, stderr, exitCode) = await _docker.ExecuteCommandAsync(
                    cmd.Command, SandboxWorkDir, cancellationToken);

                if (!string.IsNullOrEmpty(stdout))
                {
                    Console.WriteLine($"│  stdout: {stdout}");
                    stepOutput.AppendLine(stdout);
                }
                if (!string.IsNullOrEmpty(stderr))
                {
                    Console.WriteLine($"│  stderr: {stderr}");
                    stepOutput.AppendLine($"stderr: {stderr}");
                }
                Console.WriteLine($"│  exit: {exitCode}");
                stepOutput.AppendLine($"```");
                stepOutput.AppendLine();

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
        return stepOutput.ToString();
    }

    /// <summary>
    /// Builds a markdown document that summarizes the task and all step outputs.
    /// Used as a fallback when the sandbox workspace contains no files.
    /// </summary>
    private static string BuildMarkdownSummary(
        string taskTitle, string originalTask, IEnumerable<string> stepOutputs)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {taskTitle}");
        sb.AppendLine();
        sb.AppendLine($"**Task:** {originalTask}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        foreach (var output in stepOutputs)
        {
            sb.AppendLine(output);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Removes common markdown syntax tokens to produce a plain-text version of a markdown string.
    /// </summary>
    private static string StripMarkdown(string markdown)
    {
        var lines = markdown.Split('\n');
        var result = new StringBuilder();
        bool inCodeBlock = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');

            // Toggle code-block state but drop the fence lines themselves.
            if (line.StartsWith("```"))
            {
                inCodeBlock = !inCodeBlock;
                continue;
            }

            if (!inCodeBlock)
            {
                // Strip ATX headings (# Heading → Heading).
                if (line.StartsWith("#"))
                    line = line.TrimStart('#').TrimStart();

                // Strip horizontal rules.
                if (line == "---" || line == "***" || line == "___")
                {
                    result.AppendLine();
                    continue;
                }

                // Strip bold/italic markers (**text**, *text*, __text__, _text_).
                line = line.Replace("**", "").Replace("__", "").Replace("*", "").Replace("_", "");
            }

            result.AppendLine(line);
        }

        return result.ToString();
    }
}
