using System.Text;
using System.Text.Json;
using OllamaAgent.Models;

namespace OllamaAgent.Services;

/// <summary>
/// Communicates with a local Ollama instance, streaming token output to the console
/// and returning the full accumulated response text.
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
