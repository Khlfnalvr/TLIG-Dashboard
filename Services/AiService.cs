using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace TLIGDashboard.Services;

/// <summary>
/// OpenAI-compatible AI chat client.
///
/// DeepSeek endpoint : https://api.deepseek.com/chat/completions
/// OpenAI endpoint   : https://api.openai.com/v1/chat/completions
/// Ollama endpoint   : http://localhost:11434/v1/chat/completions
///
/// The service appends "/chat/completions" to ApiUrl as-is.
/// </summary>
public sealed class AiService : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────
    public string ApiUrl       { get; set; } = "https://api.deepseek.com";
    public string ApiKey       { get; set; } = "";
    public string Model        { get; set; } = "deepseek-v4-flash";
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant integrated in TLIG Dashboard, " +
        "an industrial HMI dashboard and AI connector.";

    // ── History ───────────────────────────────────────────────────────────────
    public IReadOnlyList<ChatMessage> History => _history;
    private readonly List<ChatMessage> _history = new();
    public void ClearHistory() => _history.Clear();

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    // ── Chat completions endpoint ─────────────────────────────────────────────
    private string Endpoint() => $"{ApiUrl.TrimEnd('/')}/chat/completions";

    // ── Streaming chat (callback-based, no IAsyncEnumerable) ─────────────────
    /// <summary>
    /// Sends userMessage to the API.
    /// <paramref name="onToken"/> is called once per streamed token.
    /// Returns the complete reply string.
    /// Throws on HTTP error or network failure — caller must catch.
    /// </summary>
    public async Task<string> StreamChatAsync(
        string         userMessage,
        Action<string> onToken,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
            throw new InvalidOperationException(
                "API key belum diset. Buka flyout → tab Pengaturan AI.");

        if (string.IsNullOrWhiteSpace(Model))
            throw new InvalidOperationException("Nama model belum diset.");

        _history.Add(new ChatMessage("user", userMessage));

        var body    = BuildBody();
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint())
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        request.Headers.Accept.ParseAdd("text/event-stream");

        // ── Send, read headers first ─────────────────────────────────────────
        var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{ParseError(errJson)}");
        }

        // ── Read SSE stream line-by-line ─────────────────────────────────────
        await using var stream = await response.Content
            .ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var full = new StringBuilder();

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null
               && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: "))       continue;

            var data = line[6..].Trim();
            if (data == "[DONE]") break;

            var token = ExtractToken(data);
            if (string.IsNullOrEmpty(token)) continue;

            full.Append(token);
            onToken(token);           // caller is responsible for thread safety
        }

        var reply = full.ToString();
        if (reply.Length > 0)
            _history.Add(new ChatMessage("assistant", reply));

        return reply;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildBody()
    {
        var msgs = new List<object>();
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            msgs.Add(new { role = "system", content = SystemPrompt });
        foreach (var m in _history)
            msgs.Add(new { role = m.Role, content = m.Content });

        return JsonSerializer.Serialize(new
        {
            model       = Model,
            messages    = msgs,
            stream      = true,
            max_tokens  = 4096,
            temperature = 0.7
        });
    }

    private static string? ExtractToken(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var choices   = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;
            var delta = choices[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var c))
                return c.GetString();
        }
        catch { }
        return null;
    }

    private static string ParseError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var e) &&
                e.TryGetProperty("message", out var m))
                return m.GetString() ?? json;
        }
        catch { }
        return json.Length > 400 ? json[..400] + "…" : json;
    }

    public void Dispose() => _http.Dispose();
}

public record ChatMessage(string Role, string Content);
