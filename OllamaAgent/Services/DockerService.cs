using Docker.DotNet;
using Docker.DotNet.Models;

namespace OllamaAgent.Services;

/// <summary>
/// Manages a disposable Ubuntu sandbox Docker container for safe task execution.
/// </summary>
public class DockerService : IAsyncDisposable
{
    private readonly DockerClient _client;
    private string? _containerId;

    private const string SandboxImage = "ghcr.io/dahln/lunasandbox:latest";

    public DockerService()
    {
        _client = new DockerClientConfiguration().CreateClient();
    }

    /// <summary>
    /// Verifies that the sandbox image is available locally. Throws if it is not found.
    /// Call this once at application startup so the process can exit before accepting tasks.
    /// </summary>
    public static async Task EnsureSandboxImageExistsAsync(CancellationToken cancellationToken = default)
    {
        using var client = new DockerClientConfiguration().CreateClient();
        try
        {
            await client.Images.InspectImageAsync(SandboxImage, cancellationToken);
            Console.WriteLine($"[Docker] Sandbox image found: {SandboxImage}");
        }
        catch (Docker.DotNet.DockerImageNotFoundException)
        {
            throw new InvalidOperationException(
                $"Sandbox image '{SandboxImage}' was not found on this machine.\n" +
                $"Pull it first with:\n" +
                $"  docker pull {SandboxImage}");
        }
    }

    /// <summary>
    /// Starts a long-running container from the pre-pulled sandbox image.
    /// </summary>
    public async Task StartSandboxAsync(CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"[Docker] Starting sandbox ({SandboxImage})…");

        Console.WriteLine("[Docker] Creating sandbox container…");
        var response = await _client.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = SandboxImage,
                // Keep the container alive indefinitely
                Cmd = ["/bin/bash", "-c", "while true; do sleep 1; done"],
                Tty = false,
            },
            cancellationToken);

        _containerId = response.ID;
        await _client.Containers.StartContainerAsync(
            _containerId, new ContainerStartParameters(), cancellationToken);

        Console.WriteLine($"[Docker] Sandbox ready (container: {_containerId[..12]})");
    }

    /// <summary>
    /// Executes a shell command inside the sandbox container and returns stdout, stderr,
    /// and the exit code.
    /// </summary>
    public async Task<(string Output, string Error, long ExitCode)> ExecuteCommandAsync(
        string command, string? workingDir = null, CancellationToken cancellationToken = default)
    {
        if (_containerId is null)
            throw new InvalidOperationException("Sandbox has not been started.");

        var exec = await _client.Exec.ExecCreateContainerAsync(
            _containerId,
            new ContainerExecCreateParameters
            {
                Cmd = ["/bin/bash", "-c", command],
                AttachStdout = true,
                AttachStderr = true,
                Tty = false,
                WorkingDir = workingDir,
            },
            cancellationToken);

        using var stream = await _client.Exec.StartAndAttachContainerExecAsync(
            exec.ID, false, cancellationToken);

        var (stdout, stderr) = await stream.ReadOutputToEndAsync(cancellationToken);

        var inspect = await _client.Exec.InspectContainerExecAsync(exec.ID, cancellationToken);

        return (stdout.TrimEnd(), stderr.TrimEnd(), inspect.ExitCode);
    }

    /// <summary>
    /// Copies all files under <paramref name="containerPath"/> into <paramref name="hostPath"/>
    /// by streaming a tar archive from the container and extracting it on the host.
    /// </summary>
    public async Task CopyFromContainerAsync(
        string containerPath, string hostPath, CancellationToken cancellationToken = default)
    {
        if (_containerId is null)
            throw new InvalidOperationException("Sandbox has not been started.");

        Directory.CreateDirectory(hostPath);

        var archiveResponse = await _client.Containers.GetArchiveFromContainerAsync(
            _containerId,
            new GetArchiveFromContainerParameters { Path = containerPath },
            false,
            cancellationToken);

        var tarPath = Path.Combine(Path.GetTempPath(), $"sandbox_{Guid.NewGuid():N}.tar");
        try
        {
            await using (var fs = File.Create(tarPath))
                await archiveResponse.Stream.CopyToAsync(fs, cancellationToken);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                ArgumentList = { "-xf", tarPath, "--strip-components=1", "-C", hostPath },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = System.Diagnostics.Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start tar process.");
            await proc.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (File.Exists(tarPath))
                File.Delete(tarPath);
        }
    }

    /// <summary>
    /// Stops and removes the sandbox container.
    /// </summary>
    public async Task StopAndRemoveSandboxAsync(CancellationToken cancellationToken = default)
    {
        if (_containerId is null)
            return;

        Console.WriteLine("[Docker] Stopping sandbox container…");
        await _client.Containers.StopContainerAsync(
            _containerId,
            new ContainerStopParameters { WaitBeforeKillSeconds = 5 },
            cancellationToken);

        await _client.Containers.RemoveContainerAsync(
            _containerId,
            new ContainerRemoveParameters { Force = true },
            cancellationToken);

        Console.WriteLine("[Docker] Sandbox removed.");
        _containerId = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_containerId is not null)
        {
            try { await StopAndRemoveSandboxAsync(); }
            catch { /* best-effort cleanup */ }
        }
        _client.Dispose();
    }
}
