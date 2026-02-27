using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OllamaAgent.Models;

namespace OllamaAgent.Services;

/// <summary>
/// Orchestrates the full agentic flow: classification → plan generation → sandbox creation
/// → step execution → artifact collection → sandbox teardown.
///
/// Design principle: This service handles loops, sub-loops, and lifecycle management.
/// All intelligence — domain expertise, error recovery, scope control — lives in the
/// PromptLibrary and is composed by OllamaService. Code here is minimal orchestration.
/// </summary>
public class AgentService
{
    private readonly OllamaService _ollama;
    private readonly DockerService _docker;
    private readonly LoggingService _log;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // Maximum command iterations per step before forcibly moving on.
    private const int MaxIterationsPerStep = 20;

    // Number of recent commands to track for loop detection.
    private const int CommandHistoryWindow = 6;

    // How many times a command (or similar) can repeat before triggering loop-breaking.
    private const int LoopThreshold = 2;

    // Max times the same normalized failing command can be attempted before hard block.
    private const int MaxSameCommandFailures = 2;

    // Max placeholder violations allowed in a step before forcing progression.
    private const int MaxPlaceholderViolationsPerStep = 2;

    // Minimum file size (bytes) that counts as a non-trivial deliverable.
    private const int MinimumDeliverableFileSizeBytes = 1;

    // Maximum directory depth searched when looking for deliverable files.
    private const int MaxWorkspaceFindDepth = 5;

    // Working directory inside the sandbox.
    private const string SandboxWorkDir = "/workspace";

    private static readonly string[] PlaceholderTokens =
    {
        "ProjectName",
        "TODO_PROJECT",
        "__PROJECT__",
        "<project-name>",
        "<project_name>",
        "<name>",
    };

    public AgentService(OllamaService ollama, DockerService docker, LoggingService log)
    {
        _ollama = ollama;
        _docker = docker;
        _log = log;
    }

    /// <summary>
    /// Runs the complete agent lifecycle for a single user task.
    /// Returns the path on the host where deliverables were saved.
    /// </summary>
    public async Task<string> RunTaskAsync(
        string userPrompt, CancellationToken cancellationToken = default)
    {
        // ── Phase 1a: Classify the task ──────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 1a – Classifying task…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        var classifyTimer = System.Diagnostics.Stopwatch.StartNew();
        var classification = await _ollama.ClassifyTaskAsync(userPrompt, cancellationToken);
        classifyTimer.Stop();

        Console.WriteLine($"  Classification: {classification}");
        _log.LogClassification(classification, classifyTimer.ElapsedMilliseconds);

        // ── Phase 1b: Generate execution plan ────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 1b – Generating execution plan…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        var planMessages = _ollama.ComposePlanningMessages(userPrompt, classification);

        var planTimer = System.Diagnostics.Stopwatch.StartNew();
        var planJson = await _ollama.StreamChatAsync(
            planMessages, format: PromptLibrary.Schemas.TaskPlan, cancellationToken: cancellationToken);
        planTimer.Stop();

        TaskPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<TaskPlan>(planJson, _jsonOptions);
        }
        catch (JsonException ex)
        {
            _log.LogAgenticError("PlanParsing", $"Failed to parse execution plan JSON: {ex.Message}");
            throw new InvalidOperationException(
                $"Failed to parse execution plan from Ollama response.\nRaw: {planJson}", ex);
        }

        if (plan is null || string.IsNullOrWhiteSpace(plan.Title) || plan.Steps.Count == 0)
        {
            _log.LogAgenticError("PlanEmpty", "Ollama returned an empty execution plan.");
            throw new InvalidOperationException("Ollama returned an empty execution plan.");
        }

        _log.LogPlan(plan.Title, plan.Steps, planTimer.ElapsedMilliseconds);

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
        await RunInternalCommandAsync($"mkdir -p {SandboxWorkDir}", cancellationToken: cancellationToken);

        // ── Phase 3: Execute each step ────────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 3 – Executing steps…");
        Console.WriteLine("══════════════════════════════════════════");

        var stepOutputs = new List<string>();
        foreach (var step in plan.Steps.OrderBy(s => s.StepNumber))
        {
            var output = await ExecuteStepAsync(step, userPrompt, classification, cancellationToken);
            stepOutputs.Add(output);
        }

        // ── Phase 3.5: Finalize deliverables ──────────────────────────────────
        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Phase 3.5 – Verifying deliverables…");
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine();

        await FinalizeDeliverablesAsync(userPrompt, plan.Title, stepOutputs, classification, cancellationToken);

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
        ExecutionStep step, string originalTask, TaskClassification classification,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine($"┌─ Step {step.StepNumber}: {step.Description}");
        Console.WriteLine("│");

        _log.LogStepStart(step.StepNumber, step.Description);

        var stepOutput = new StringBuilder();
        stepOutput.AppendLine($"## Step {step.StepNumber}: {step.Description}");
        stepOutput.AppendLine();

        // Track recent commands for loop detection.
        var recentCommands = new List<string>();
        var failedCommandCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var blockedCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int placeholderViolations = 0;

        // Compose initial messages using classification-driven prompt selection.
        var stepMessages = _ollama.ComposeStepExecutionMessages(
            step, originalTask, classification, SandboxWorkDir,
            iteration: 1, maxIterations: MaxIterationsPerStep);

        bool stepDone = false;
        int iteration = 0;

        for (; iteration < MaxIterationsPerStep; iteration++)
        {
            Console.WriteLine($"│  [iteration {iteration + 1}]");

            // ── Loop detection: check for repeating command patterns ──
            if (DetectLoop(recentCommands))
            {
                Console.WriteLine("│  ⚠ Loop detected – injecting course correction…");
                _log.LogLoopDetected(step.StepNumber, iteration + 1, recentCommands);
                stepMessages.Add(_ollama.ComposeLoopBreaker(recentCommands, originalTask));
            }

            var stepCallTimer = System.Diagnostics.Stopwatch.StartNew();
            var rawResponse = await _ollama.StreamChatAsync(
                stepMessages, format: PromptLibrary.Schemas.StepCommand, cancellationToken: cancellationToken);
            stepCallTimer.Stop();

            StepCommand? cmd;
            try
            {
                cmd = JsonSerializer.Deserialize<StepCommand>(rawResponse, _jsonOptions);
            }
            catch (JsonException)
            {
                Console.WriteLine($"│  ⚠ Could not parse AI response as JSON – skipping iteration.");
                _log.LogAgenticError("JsonParse", $"Step {step.StepNumber} iter {iteration + 1}: could not parse StepCommand JSON");
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

            if (string.IsNullOrWhiteSpace(cmd.Command))
            {
                stepMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                stepMessages.Add(new OllamaChatMessage
                {
                    Role = "user",
                    Content = "You responded with done=false but did not provide a command. "
                              + "Please run a shell command to make progress, or set done=true if the step is already complete.",
                });
                continue;
            }
            else
            {
                if (ContainsPlaceholderToken(cmd.Command))
                {
                    placeholderViolations++;
                    Console.WriteLine("│  ⚠ Blocked command with unresolved placeholder token.");
                    _log.LogAgenticError(
                        "PlaceholderCommand",
                        $"Step {step.StepNumber} iter {iteration + 1}: blocked placeholder command '{cmd.Command}'");

                    stepMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                    stepMessages.Add(new OllamaChatMessage
                    {
                        Role = "user",
                        Content = "Your command includes unresolved placeholder tokens (e.g. ProjectName). "
                                  + "Use concrete names from the current workspace. Run `ls -la /workspace` "
                                  + "to discover actual directories and then use those exact names.",
                    });

                    if (placeholderViolations >= MaxPlaceholderViolationsPerStep)
                    {
                        Console.WriteLine("│  ⚠ Too many placeholder violations – moving to next step.");
                        _log.LogAgenticError(
                            "PlaceholderLoop",
                            $"Step {step.StepNumber} hit placeholder violation limit ({MaxPlaceholderViolationsPerStep})");
                        break;
                    }

                    continue;
                }

                var normalizedCandidate = NormalizeCommand(cmd.Command);
                if (blockedCommands.Contains(normalizedCandidate))
                {
                    Console.WriteLine("│  ⚠ Blocked previously failed command pattern.");
                    stepMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                    stepMessages.Add(new OllamaChatMessage
                    {
                        Role = "user",
                        Content = "This command pattern already failed multiple times and is blocked. "
                                  + "Choose a different approach that directly advances the deliverable.",
                    });
                    continue;
                }

                Console.WriteLine($"│  $ {cmd.Command}");
                stepOutput.AppendLine("```shell");
                stepOutput.AppendLine($"$ {cmd.Command}");

                // Track command for loop detection.
                recentCommands.Add(cmd.Command);
                if (recentCommands.Count > CommandHistoryWindow)
                    recentCommands.RemoveAt(0);

                var (stdout, stderr, exitCode) = await _docker.ExecuteCommandAsync(
                    cmd.Command, SandboxWorkDir, cancellationToken);

                var normalizedCommand = normalizedCandidate;
                if (exitCode != 0)
                {
                    failedCommandCounts.TryGetValue(normalizedCommand, out int failures);
                    failures++;
                    failedCommandCounts[normalizedCommand] = failures;

                    if (failures >= MaxSameCommandFailures)
                    {
                        blockedCommands.Add(normalizedCommand);
                        Console.WriteLine("│  ⚠ Repeated failure for same command – enforcing strategy reset.");
                        stepMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                        stepMessages.Add(new OllamaChatMessage
                        {
                            Role = "user",
                            Content = PromptLibrary.ErrorRecovery.StrategyReset(new[] { cmd.Command })
                                      + "\nDo NOT run this same command pattern again in this step.",
                        });
                    }
                }

                _log.LogSandboxCommand(step.StepNumber, iteration + 1, cmd.Command, stdout, stderr, exitCode);

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
                stepOutput.AppendLine("```");
                stepOutput.AppendLine();

                // Auto-install missing commands (lightweight code guard).
                if (exitCode != 0 && stderr.Contains("command not found", StringComparison.OrdinalIgnoreCase))
                {
                    var missingPackage = TryExtractMissingCommand(stderr);
                    if (!string.IsNullOrWhiteSpace(missingPackage))
                    {
                        Console.WriteLine($"│  ⚠ Missing command '{missingPackage}' – attempting apt-get install…");
                        await _docker.ExecuteCommandAsync(
                            $"echo '{missingPackage}' >> {SandboxWorkDir}/missing_deps.log",
                            cancellationToken: cancellationToken);
                        var (_, aptErr, aptExit) = await _docker.ExecuteCommandAsync(
                            $"apt-get install -y {missingPackage} 2>&1", cancellationToken: cancellationToken);
                        var installed = aptExit == 0;
                        _log.LogMissingDependency(missingPackage, installed);
                        Console.WriteLine(installed
                            ? $"│  ✓ Installed '{missingPackage}'."
                            : $"│  ✗ Could not install '{missingPackage}': {aptErr}");
                    }
                }

                // Feed the result back — with structured error guidance on failure.
                stepMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });

                if (exitCode != 0)
                {
                    // Provide AI-driven error recovery guidance via prompt.
                    stepMessages.Add(_ollama.ComposeErrorGuidance(cmd.Command, stderr, exitCode));
                }
                else
                {
                    stepMessages.Add(new OllamaChatMessage
                    {
                        Role = "user",
                        Content = $"Command: {cmd.Command}\nExit code: {exitCode}\nstdout:\n{stdout}\nstderr:\n{stderr}",
                    });
                }
            }
        }

        if (!stepDone)
        {
            Console.WriteLine("│  ⚠ Max iterations reached – moving to next step.");
            _log.LogAgenticError("MaxIterations", $"Step {step.StepNumber} hit iteration limit ({MaxIterationsPerStep})");
        }

        _log.LogStepEnd(step.StepNumber, stepDone, Math.Min(iteration + 1, MaxIterationsPerStep));

        Console.WriteLine("└─────────────────────────────────────────");
        return stepOutput.ToString();
    }

    /// <summary>
    /// Phase 3.5: After all steps complete, verifies that the sandbox workspace contains
    /// non-trivial deliverable files. If no file larger than <see cref="MinimumDeliverableFileSizeBytes"/>
    /// bytes is found, runs an iterative AI-driven finalization loop that uses the accumulated
    /// step outputs as research context to generate and write the complete deliverables.
    /// </summary>
    private async Task FinalizeDeliverablesAsync(
        string originalTask,
        string taskTitle,
        IReadOnlyList<string> stepOutputs,
        TaskClassification classification,
        CancellationToken cancellationToken)
    {
        // List workspace, noting any files larger than minimum size.
        var (lsOutput, _, _) = await RunInternalCommandAsync(
            $"ls -lhR {SandboxWorkDir} 2>/dev/null || echo 'workspace is empty'",
            cancellationToken: cancellationToken);

        var (substantialFiles, _, _) = await RunInternalCommandAsync(
            $"find {SandboxWorkDir} -maxdepth {MaxWorkspaceFindDepth} -type f -size +{MinimumDeliverableFileSizeBytes}c 2>/dev/null",
            cancellationToken: cancellationToken);

        var (placeholderMatches, _, _) = await FindPlaceholderTokensAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(substantialFiles) && string.IsNullOrWhiteSpace(placeholderMatches))
        {
            Console.WriteLine("  Workspace already contains non-empty deliverable files.");
            Console.WriteLine($"  Files: {substantialFiles.Replace('\n', ' ')}");
            return;
        }

        if (!string.IsNullOrWhiteSpace(placeholderMatches))
        {
            Console.WriteLine("  Placeholder tokens detected in workspace files; remediation required.");
            Console.WriteLine($"  Matches:\n{placeholderMatches}");
        }

        Console.WriteLine($"  Workspace state:\n{lsOutput}");
        Console.WriteLine("  No substantial deliverable files found – running finalization.");
        _log.LogFalseCompletion("post-execution", "Workspace empty after all steps completed");

        // Build research context from step outputs.
        var researchContext = new StringBuilder();
        researchContext.AppendLine("## Research and Work Completed So Far");
        researchContext.AppendLine();
        foreach (var output in stepOutputs)
            researchContext.AppendLine(output);

        // Compose finalization messages using classification-driven prompts.
        var finalMessages = _ollama.ComposeFinalizationMessages(
            originalTask, taskTitle, classification, SandboxWorkDir,
            lsOutput, researchContext.ToString());

        Console.WriteLine();
        Console.WriteLine("┌─ Finalization step");
        Console.WriteLine("│");

        bool finalized = false;
        int finalizationPlaceholderViolations = 0;
        for (int iteration = 0; iteration < MaxIterationsPerStep; iteration++)
        {
            Console.WriteLine($"│  [finalization iteration {iteration + 1}]");

            var rawResponse = await _ollama.StreamChatAsync(
                finalMessages, format: PromptLibrary.Schemas.StepCommand, cancellationToken: cancellationToken);

            StepCommand? cmd;
            try
            {
                cmd = JsonSerializer.Deserialize<StepCommand>(rawResponse, _jsonOptions);
            }
            catch (JsonException)
            {
                Console.WriteLine("│  ⚠ Could not parse AI response as JSON – stopping finalization.");
                break;
            }

            if (cmd is null)
            {
                Console.WriteLine("│  ⚠ Null AI response – stopping finalization.");
                break;
            }

            Console.WriteLine($"│  AI: {cmd.Message}");

            if (cmd.Done)
            {
                // Verify that the workspace actually contains substantial files before accepting.
                var (verifyFiles, _, _) = await RunInternalCommandAsync(
                    $"find {SandboxWorkDir} -maxdepth {MaxWorkspaceFindDepth} -type f -size +{MinimumDeliverableFileSizeBytes}c 2>/dev/null",
                    cancellationToken: cancellationToken);

                var (placeholderMatchesOnDone, _, _) = await FindPlaceholderTokensAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(verifyFiles) && string.IsNullOrWhiteSpace(placeholderMatchesOnDone))
                {
                    Console.WriteLine("│  ✓ Deliverables finalized.");
                    finalized = true;
                    break;
                }

                // AI claimed done but workspace is still empty — push feedback and keep iterating.
                Console.WriteLine("│  ⚠ AI claimed done but workspace is still empty – prompting for content…");
                _log.LogFalseCompletion("finalization", $"AI claimed done at iteration {iteration + 1} but workspace is still empty");
                finalMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                finalMessages.Add(new OllamaChatMessage
                {
                    Role = "user",
                    Content = $"You set done=true but completion criteria failed. "
                              + $"Workspace files must exist AND contain no unresolved placeholders. "
                              + $"You MUST run a shell command (e.g. a heredoc) to write/fix content in {SandboxWorkDir}. "
                              + $"Do NOT set done=true until files are written and validated with `ls -lh` and placeholder scan.",
                });
                continue;
            }

            if (string.IsNullOrWhiteSpace(cmd.Command))
            {
                // AI said not done but gave no command — push feedback so it makes progress.
                finalMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                finalMessages.Add(new OllamaChatMessage
                {
                    Role = "user",
                    Content = $"You responded with done=false but did not provide a command. "
                              + $"Please run a shell command to write the complete deliverable to {SandboxWorkDir}, "
                              + $"or set done=true only after the file is written and verified.",
                });
                continue;
            }
            else
            {
                if (ContainsPlaceholderToken(cmd.Command))
                {
                    finalizationPlaceholderViolations++;
                    Console.WriteLine("│  ⚠ Blocked finalization command with unresolved placeholder token.");
                    finalMessages.Add(new OllamaChatMessage { Role = "assistant", Content = rawResponse });
                    finalMessages.Add(new OllamaChatMessage
                    {
                        Role = "user",
                        Content = "Your command contains unresolved placeholders like ProjectName. "
                                  + "Use concrete existing file/folder names from /workspace.",
                    });

                    if (finalizationPlaceholderViolations >= MaxPlaceholderViolationsPerStep)
                    {
                        Console.WriteLine("│  ⚠ Finalization placeholder limit reached.");
                        break;
                    }

                    continue;
                }

                Console.WriteLine($"│  $ {cmd.Command}");
                var (stdout, stderr, exitCode) = await _docker.ExecuteCommandAsync(
                    cmd.Command, SandboxWorkDir, cancellationToken);

                if (!string.IsNullOrEmpty(stdout))
                    Console.WriteLine($"│  stdout: {stdout}");
                if (!string.IsNullOrEmpty(stderr))
                    Console.WriteLine($"│  stderr: {stderr}");
                Console.WriteLine($"│  exit: {exitCode}");

                finalMessages.Add(new OllamaChatMessage
                {
                    Role = "assistant",
                    Content = rawResponse,
                });
                finalMessages.Add(new OllamaChatMessage
                {
                    Role = "user",
                    Content = $"Command: {cmd.Command}\nExit code: {exitCode}\n"
                             + $"stdout:\n{stdout}\n"
                             + $"stderr:\n{stderr}",
                });

                if (exitCode == 0)
                {
                    var (matches, _, _) = await FindPlaceholderTokensAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(matches))
                    {
                        finalMessages.Add(new OllamaChatMessage
                        {
                            Role = "user",
                            Content = "Placeholder tokens still exist in workspace files. Replace all unresolved "
                                      + "tokens (ProjectName, TODO_PROJECT, __PROJECT__, <project-name>, <name>) "
                                      + "before setting done=true. Current matches:\n"
                                      + matches,
                        });
                    }
                }
            }
        }

        if (!finalized)
        {
            Console.WriteLine("│  ⚠ Finalization max iterations reached.");
            _log.LogAgenticError("FinalizationFailed", "Finalization loop exhausted without producing deliverables");
        }

        Console.WriteLine("└─────────────────────────────────────────");
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

    /// <summary>
    /// Detects whether the recent command history contains a repeating pattern,
    /// indicating the AI is stuck in a loop retrying the same failing approach.
    /// </summary>
    private static bool DetectLoop(IReadOnlyList<string> recentCommands)
    {
        if (recentCommands.Count < 3)
            return false;

        // Check if any command appears LoopThreshold+ times in the history window.
        return recentCommands
            .GroupBy(c => NormalizeCommand(c), StringComparer.OrdinalIgnoreCase)
            .Any(g => g.Count() >= LoopThreshold);
    }

    /// <summary>
    /// Normalizes a command for comparison by stripping version numbers and whitespace
    /// so that variants like "pip3 install dotnet" and "pip3  install dotnet" match.
    /// </summary>
    private static string NormalizeCommand(string command) =>
        Regex.Replace(command.Trim(), @"\s+", " ");

    private static bool ContainsPlaceholderToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return PlaceholderTokens.Any(token =>
            value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<(string Output, string Error, long ExitCode)> FindPlaceholderTokensAsync(
        CancellationToken cancellationToken)
    {
        const string grepPattern = "ProjectName|TODO_PROJECT|__PROJECT__|<project[-_ ]?name>|<name>";
        return await RunInternalCommandAsync(
            $"grep -RInE '{grepPattern}' {SandboxWorkDir} --exclude-dir=.git 2>/dev/null || true",
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Executes a shell command in the sandbox, echoing the command to the console before
    /// running it so that every sandbox invocation is visible in the output logs.
    /// </summary>
    private async Task<(string Output, string Error, long ExitCode)> RunInternalCommandAsync(
        string command, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"  $ {command}");
        return await _docker.ExecuteCommandAsync(command, workingDir, cancellationToken);
    }

    /// <summary>
    /// Parses a "command not found" error message and returns the missing command name,
    /// or null if the pattern is not recognized.
    /// Example input: "/bin/bash: line 1: python: command not found"
    /// Returns: "python"
    /// </summary>
    private static string? TryExtractMissingCommand(string stderr)
    {
        // Pattern: "...: <command>: command not found"
        const string marker = ": command not found";
        var idx = stderr.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return null;

        // Walk back to the preceding colon to extract the command token.
        var before = stderr[..idx];
        var lastColon = before.LastIndexOf(':');
        if (lastColon < 0)
            return null;

        var command = before[(lastColon + 1)..].Trim();
        // Reject empty strings or tokens with spaces/slashes (not a simple command name).
        if (string.IsNullOrWhiteSpace(command) || command.Contains(' ') || command.Contains('/'))
            return null;

        return command;
    }
}
