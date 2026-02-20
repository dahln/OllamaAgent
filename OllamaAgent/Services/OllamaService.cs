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
