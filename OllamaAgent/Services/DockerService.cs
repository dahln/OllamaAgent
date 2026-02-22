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
    /// Attempts to pull the sandbox image from the registry. If the pull fails, checks
    /// whether the image is already available locally. Throws if neither succeeds so the
    /// caller can exit the process before accepting any tasks.
    /// </summary>
    public static async Task EnsureSandboxImageExistsAsync(CancellationToken cancellationToken = default)
    {
        using var client = new DockerClientConfiguration().CreateClient();

        Console.WriteLine($"[Docker] Pulling sandbox image '{SandboxImage}'…");

        try
        {
            // Split "registry/image:tag" into FromImage + Tag for the API.
            var lastColon = SandboxImage.LastIndexOf(':');
            var fromImage = lastColon >= 0 ? SandboxImage[..lastColon] : SandboxImage;
            var tag = lastColon >= 0 ? SandboxImage[(lastColon + 1)..] : "latest";

            var progress = new Progress<JSONMessage>(msg =>
            {
                if (!string.IsNullOrEmpty(msg.Status))
                    Console.WriteLine($"[Docker] {msg.Status}");
            });

            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = fromImage, Tag = tag },
                null,
                progress,
                cancellationToken);

            Console.WriteLine($"[Docker] Sandbox image ready: {SandboxImage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Docker] Pull failed: {ex.Message}");
            Console.WriteLine("[Docker] Checking for a locally cached image…");

            // Fall back to a locally cached copy of the image.
            try
            {
                await client.Images.InspectImageAsync(SandboxImage, cancellationToken);
                Console.WriteLine($"[Docker] Sandbox image found locally: {SandboxImage}");
            }
            catch (Docker.DotNet.DockerImageNotFoundException)
            {
                throw new InvalidOperationException(
                    $"Sandbox image '{SandboxImage}' could not be pulled and is not available locally.\n" +
                    $"Pull error: {ex.Message}\n" +
                    $"Please ensure Docker has access to the registry, or pull it manually with:\n" +
                    $"  docker pull {SandboxImage}");
            }
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
