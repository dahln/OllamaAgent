using OllamaAgent.Services;

// ── Configuration ─────────────────────────────────────────────────────────────
// Override with environment variables OLLAMA_MODEL and OLLAMA_URL if needed.
var ollamaModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "gemma3:4b";
var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";

// ── Banner ────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║          O l l a m a  A g e n t          ║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine($"  Model : {ollamaModel}");
Console.WriteLine($"  Ollama: {ollamaUrl}");
Console.WriteLine();

using var log = new LoggingService();
Console.WriteLine($"  Log   : {log.LogFilePath}");
Console.WriteLine();

var ollama = new OllamaService(ollamaModel, ollamaUrl, log);

// Ensure the model is downloaded before accepting tasks.
await ollama.EnsureModelPulledAsync();

// Ensure the sandbox image exists locally before accepting tasks.
// If the image is missing the application exits immediately with an error.
try
{
    await DockerService.EnsureSandboxImageExistsAsync(log);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[Error] {ex.Message}");
    Console.ResetColor();
    return;
}

// ── Main prompt loop ──────────────────────────────────────────────────────────
while (true)
{
    Console.WriteLine("──────────────────────────────────────────");
    Console.Write("Enter task (or 'exit' to quit):\n> ");
    var userInput = Console.ReadLine()?.Trim();

    if (string.IsNullOrWhiteSpace(userInput))
        continue;

    if (userInput.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
        userInput.Equals("quit", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine("Goodbye!");
        break;
    }

    using var cts = new CancellationTokenSource();

    // Allow Ctrl+C to cancel the current task without killing the process.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine("\n[Cancelled – shutting down task gracefully…]");
        cts.Cancel();
    };

    await using var docker = new DockerService(log);
    var agent = new AgentService(ollama, docker, log);

    try
    {
        log.LogTaskStart(userInput, ollamaModel, ollamaUrl);
        var outputPath = await agent.RunTaskAsync(userInput, cts.Token);

        log.LogTaskEnd(outputPath, success: true);

        Console.WriteLine();
        Console.WriteLine("══════════════════════════════════════════");
        Console.WriteLine("  Task complete!");
        Console.WriteLine($"  Deliverables saved to: {outputPath}");
        Console.WriteLine("══════════════════════════════════════════");
    }
    catch (OperationCanceledException)
    {
        log.LogTaskEnd(null, success: false, "Task cancelled by user");
        Console.WriteLine("[Task cancelled.]");
    }
    catch (Exception ex)
    {
        log.LogTaskEnd(null, success: false, ex.Message);
        log.LogRuntimeError("RunTaskAsync", ex);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[Error] {ex.Message}");
        Console.ResetColor();
    }

    Console.WriteLine();
}

log.WriteSessionSummary();
Console.WriteLine($"Session log saved to: {log.LogFilePath}");

