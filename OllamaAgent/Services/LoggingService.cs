using System.Diagnostics;
using System.Text;

namespace OllamaAgent.Services;

/// <summary>
/// Structured logging service that writes a date-stamped markdown log file
/// capturing prompt effectiveness, prompt performance, sandbox runtime errors,
/// missing dependencies, and agentic errors.
///
/// The log is organized into collapsible sections per task, with timing data,
/// prompt metadata, and error diagnostics designed for post-run review.
///
/// Thread-safe: all writes go through a lock to support concurrent logging
/// from callbacks (e.g. Docker progress events).
/// </summary>
public class LoggingService : IDisposable
{
    private readonly string _logDir;
    private readonly string _logFilePath;
    private readonly StringBuilder _buffer = new();
    private readonly object _lock = new();
    private readonly Stopwatch _sessionTimer = Stopwatch.StartNew();

    // Counters for the session summary
    private int _totalAiCalls;
    private int _totalSandboxCommands;
    private int _totalCommandErrors;
    private int _loopsDetected;
    private int _missingDependencies;
    private long _totalAiLatencyMs;
    private readonly List<string> _missingDeps = new();
    private readonly List<string> _agenticErrors = new();
    private readonly List<PromptRecord> _promptRecords = new();

    /// <summary>Path to the current session's log file.</summary>
    public string LogFilePath => _logFilePath;

    public LoggingService(string logDir = "logs")
    {
        _logDir = Path.IsPathRooted(logDir)
            ? logDir
            : Path.Combine(Environment.CurrentDirectory, logDir);

        Directory.CreateDirectory(_logDir);

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HHmmss");
        _logFilePath = Path.Combine(_logDir, $"agent_log_{timestamp}.md");

        // Write the session header immediately.
        AppendLine("# OllamaAgent Session Log");
        AppendLine();
        AppendLine($"**Session started:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        AppendLine($"**Machine:** {Environment.MachineName}");
        AppendLine($"**OS:** {Environment.OSVersion}");
        AppendLine();
        AppendLine("---");
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SESSION & TASK LIFECYCLE
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs the start of a new task with its user prompt.</summary>
    public void LogTaskStart(string userPrompt, string model, string ollamaUrl)
    {
        AppendLine($"## Task: {Truncate(userPrompt, 100)}");
        AppendLine();
        AppendLine($"- **Full prompt:** {userPrompt}");
        AppendLine($"- **Model:** {model}");
        AppendLine($"- **Ollama URL:** {ollamaUrl}");
        AppendLine($"- **Started:** {DateTime.UtcNow:HH:mm:ss} UTC");
        AppendLine();
        Flush();
    }

    /// <summary>Logs the end of a task with its output path and overall result.</summary>
    public void LogTaskEnd(string? outputPath, bool success, string? errorMessage = null)
    {
        AppendLine();
        AppendLine("### Task Result");
        AppendLine();
        AppendLine($"- **Status:** {(success ? "✅ Success" : "❌ Failed")}");
        if (outputPath is not null)
            AppendLine($"- **Output:** `{outputPath}`");
        if (errorMessage is not null)
            AppendLine($"- **Error:** {errorMessage}");
        AppendLine($"- **Ended:** {DateTime.UtcNow:HH:mm:ss} UTC");
        AppendLine();
        AppendLine("---");
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  CLASSIFICATION & PLANNING
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs the AI-generated task classification.</summary>
    public void LogClassification(Models.TaskClassification classification, long elapsedMs)
    {
        AppendLine("### Classification");
        AppendLine();
        AppendLine($"| Field | Value |");
        AppendLine($"|---|---|");
        AppendLine($"| Category | {classification.PrimaryCategory} |");
        AppendLine($"| Language | {classification.Language} |");
        AppendLine($"| Framework | {classification.Framework} |");
        AppendLine($"| Complexity | {classification.Complexity} |");
        AppendLine($"| Capabilities | {string.Join(", ", classification.RequiredCapabilities)} |");
        AppendLine($"| Latency | {elapsedMs}ms |");
        AppendLine();

        RecordAiCall("classification", elapsedMs);
        Flush();
    }

    /// <summary>Logs the AI-generated execution plan.</summary>
    public void LogPlan(string title, IReadOnlyList<Models.ExecutionStep> steps, long elapsedMs)
    {
        AppendLine("### Execution Plan");
        AppendLine();
        AppendLine($"**Title:** {title}  ");
        AppendLine($"**Steps:** {steps.Count} | **Plan generation:** {elapsedMs}ms");
        AppendLine();
        foreach (var step in steps)
            AppendLine($"{step.StepNumber}. {step.Description}");
        AppendLine();

        RecordAiCall("planning", elapsedMs);
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PROMPT TRACKING — effectiveness & performance
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Logs an AI prompt call with its context, the prompts that were composed,
    /// the raw AI response, and the elapsed time.
    /// </summary>
    public void LogPromptCall(
        string phase,
        string purpose,
        IReadOnlyList<string> promptsUsed,
        string? aiResponse,
        long elapsedMs,
        bool success)
    {
        lock (_lock)
        {
            _promptRecords.Add(new PromptRecord
            {
                Phase = phase,
                Purpose = purpose,
                PromptsUsed = promptsUsed.ToList(),
                ElapsedMs = elapsedMs,
                Success = success,
                ResponseLength = aiResponse?.Length ?? 0,
            });
        }

        AppendLine($"<details><summary>🤖 AI Call: {purpose} ({elapsedMs}ms) {(success ? "✅" : "❌")}</summary>");
        AppendLine();
        AppendLine($"**Phase:** {phase}  ");
        AppendLine($"**Latency:** {elapsedMs}ms  ");
        AppendLine($"**Response length:** {aiResponse?.Length ?? 0} chars  ");
        AppendLine($"**Success:** {success}");
        AppendLine();
        if (promptsUsed.Count > 0)
        {
            AppendLine("**Prompts composed:**");
            foreach (var p in promptsUsed)
                AppendLine($"- `{p}`");
            AppendLine();
        }
        if (aiResponse is not null)
        {
            AppendLine("**AI response (truncated):**");
            AppendLine("```json");
            AppendLine(Truncate(aiResponse, 500));
            AppendLine("```");
        }
        AppendLine();
        AppendLine("</details>");
        AppendLine();

        RecordAiCall(phase, elapsedMs);
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  STEP EXECUTION — sandbox commands & results
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs the start of a step.</summary>
    public void LogStepStart(int stepNumber, string description)
    {
        AppendLine($"### Step {stepNumber}: {description}");
        AppendLine();
        Flush();
    }

    /// <summary>Logs a sandbox command execution and its result.</summary>
    public void LogSandboxCommand(
        int stepNumber, int iteration, string command,
        string stdout, string stderr, long exitCode)
    {
        _totalSandboxCommands++;

        var status = exitCode == 0 ? "✅" : "❌";
        AppendLine($"<details><summary>Step {stepNumber} / Iter {iteration}: `{Truncate(command, 80)}` → exit {exitCode} {status}</summary>");
        AppendLine();
        AppendLine("```shell");
        AppendLine($"$ {command}");
        AppendLine($"# exit: {exitCode}");
        AppendLine("```");

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            AppendLine();
            AppendLine("**stdout:**");
            AppendLine("```");
            AppendLine(Truncate(stdout, 1000));
            AppendLine("```");
        }
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            AppendLine();
            AppendLine("**stderr:**");
            AppendLine("```");
            AppendLine(Truncate(stderr, 1000));
            AppendLine("```");
        }
        AppendLine();
        AppendLine("</details>");
        AppendLine();

        if (exitCode != 0)
            _totalCommandErrors++;

        Flush();
    }

    /// <summary>Logs a step completion or failure.</summary>
    public void LogStepEnd(int stepNumber, bool completed, int iterationsUsed)
    {
        AppendLine(completed
            ? $"**Step {stepNumber} ✅ completed** in {iterationsUsed} iteration(s)."
            : $"**Step {stepNumber} ⚠️ max iterations reached** ({iterationsUsed}).");
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  AGENTIC ERRORS — loop detection, false completions, wrong tools
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs a loop detection event.</summary>
    public void LogLoopDetected(int stepNumber, int iteration, IReadOnlyList<string> repeatingCommands)
    {
        _loopsDetected++;
        var msg = $"Loop detected at step {stepNumber}, iteration {iteration}: " +
                  string.Join("; ", repeatingCommands);
        lock (_lock)
            _agenticErrors.Add(msg);

        AppendLine($"> ⚠️ **LOOP DETECTED** at step {stepNumber}, iteration {iteration}");
        AppendLine($"> Repeating commands: {string.Join(", ", repeatingCommands.Select(c => $"`{Truncate(c, 60)}`"))}");
        AppendLine();
        Flush();
    }

    /// <summary>Logs when AI claims done but deliverables are empty.</summary>
    public void LogFalseCompletion(string phase, string detail)
    {
        var msg = $"False completion in {phase}: {detail}";
        lock (_lock)
            _agenticErrors.Add(msg);

        AppendLine($"> ⚠️ **FALSE COMPLETION** ({phase}): {detail}");
        AppendLine();
        Flush();
    }

    /// <summary>Logs a general agentic error (wrong language, unnecessary installs, etc.).</summary>
    public void LogAgenticError(string category, string detail)
    {
        var msg = $"[{category}] {detail}";
        lock (_lock)
            _agenticErrors.Add(msg);

        AppendLine($"> ❌ **AGENTIC ERROR** [{category}]: {detail}");
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  MISSING DEPENDENCIES
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs a missing dependency detected in the sandbox.</summary>
    public void LogMissingDependency(string packageName, bool installSucceeded)
    {
        _missingDependencies++;
        lock (_lock)
            _missingDeps.Add($"{packageName} (installed: {installSucceeded})");

        AppendLine(installSucceeded
            ? $"> 📦 **Missing dependency installed:** `{packageName}`"
            : $"> 📦 **Missing dependency FAILED to install:** `{packageName}`");
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  DOCKER EVENTS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs a Docker lifecycle event (pull, start, stop).</summary>
    public void LogDockerEvent(string eventType, string detail)
    {
        AppendLine($"- **Docker [{eventType}]:** {detail}");
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  RUNTIME ERRORS
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs an unhandled runtime error.</summary>
    public void LogRuntimeError(string context, Exception ex)
    {
        AppendLine($"> ❌ **RUNTIME ERROR** in {context}");
        AppendLine($"> `{ex.GetType().Name}`: {ex.Message}");
        if (ex.StackTrace is not null)
        {
            AppendLine("```");
            AppendLine(Truncate(ex.StackTrace, 500));
            AppendLine("```");
        }
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  SESSION SUMMARY — written at the end of the log
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes a final summary section to the log with aggregate statistics
    /// for prompt performance, error counts, and missing dependencies.
    /// Call this before disposing.
    /// </summary>
    public void WriteSessionSummary()
    {
        _sessionTimer.Stop();

        AppendLine("---");
        AppendLine();
        AppendLine("## Session Summary");
        AppendLine();
        AppendLine($"| Metric | Value |");
        AppendLine($"|---|---|");
        AppendLine($"| Session duration | {_sessionTimer.Elapsed:hh\\:mm\\:ss} |");
        AppendLine($"| Total AI calls | {_totalAiCalls} |");
        AppendLine($"| Total AI latency | {_totalAiLatencyMs}ms ({(_totalAiCalls > 0 ? _totalAiLatencyMs / _totalAiCalls : 0)}ms avg) |");
        AppendLine($"| Sandbox commands executed | {_totalSandboxCommands} |");
        AppendLine($"| Command errors (non-zero exit) | {_totalCommandErrors} |");
        AppendLine($"| Loops detected | {_loopsDetected} |");
        AppendLine($"| Missing dependencies | {_missingDependencies} |");
        AppendLine($"| Agentic errors | {_agenticErrors.Count} |");
        AppendLine();

        // Prompt performance breakdown
        List<PromptRecord> records;
        lock (_lock)
            records = _promptRecords.ToList();

        if (records.Count > 0)
        {
            AppendLine("### Prompt Performance");
            AppendLine();
            AppendLine("| Phase | Count | Avg Latency | Success Rate |");
            AppendLine("|---|---|---|---|");

            var grouped = records.GroupBy(r => r.Phase);
            foreach (var g in grouped)
            {
                var count = g.Count();
                var avgMs = (long)g.Average(r => r.ElapsedMs);
                var successRate = g.Count(r => r.Success) * 100 / count;
                AppendLine($"| {g.Key} | {count} | {avgMs}ms | {successRate}% |");
            }
            AppendLine();
        }

        // Missing dependencies list
        List<string> deps;
        lock (_lock)
            deps = _missingDeps.ToList();

        if (deps.Count > 0)
        {
            AppendLine("### Missing Dependencies (add to Dockerfile)");
            AppendLine();
            foreach (var d in deps)
                AppendLine($"- `{d}`");
            AppendLine();
        }

        // Agentic error log
        List<string> errors;
        lock (_lock)
            errors = _agenticErrors.ToList();

        if (errors.Count > 0)
        {
            AppendLine("### Agentic Errors");
            AppendLine();
            foreach (var e in errors)
                AppendLine($"- {e}");
            AppendLine();
        }

        AppendLine($"**Session ended:** {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        AppendLine();
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  GENERAL
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>Logs an informational message.</summary>
    public void LogInfo(string message)
    {
        AppendLine($"- {message}");
        Flush();
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  INTERNALS
    // ═════════════════════════════════════════════════════════════════════════

    private void RecordAiCall(string phase, long elapsedMs)
    {
        lock (_lock)
        {
            _totalAiCalls++;
            _totalAiLatencyMs += elapsedMs;
        }
    }

    private void AppendLine(string line = "")
    {
        lock (_lock)
            _buffer.AppendLine(line);
    }

    /// <summary>Flushes the in-memory buffer to disk.</summary>
    private void Flush()
    {
        string content;
        lock (_lock)
        {
            if (_buffer.Length == 0)
                return;
            content = _buffer.ToString();
            _buffer.Clear();
        }

        // Append to the file (create if it doesn't exist).
        File.AppendAllText(_logFilePath, content);
    }

    public void Dispose()
    {
        Flush();
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";

    /// <summary>Internal record for tracking prompt call metrics.</summary>
    private class PromptRecord
    {
        public string Phase { get; set; } = "";
        public string Purpose { get; set; } = "";
        public List<string> PromptsUsed { get; set; } = new();
        public long ElapsedMs { get; set; }
        public bool Success { get; set; }
        public int ResponseLength { get; set; }
    }
}
