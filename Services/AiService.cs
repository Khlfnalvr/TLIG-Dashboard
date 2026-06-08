using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>
/// Streaming AI chat client supporting two wire protocols (see <see cref="AiProtocols"/>):
///
/// <list type="bullet">
/// <item><b>openai</b> — DeepSeek, OpenAI, Ollama, and the server's own <c>/ai</c> proxy.
///   POSTs to <c>{ApiUrl}/chat/completions</c>, Bearer auth, <c>choices[].delta.content</c> SSE.</item>
/// <item><b>anthropic</b> — Anthropic Messages API. POSTs to <c>{ApiUrl}/v1/messages</c>,
///   <c>x-api-key</c> auth, system prompt as a top-level field, <c>content_block_delta</c> SSE.</item>
/// </list>
///
/// On the client flavor the service always speaks <b>openai</b> to the server proxy;
/// the server does any Anthropic translation. On the server flavor the protocol is
/// the active provider's protocol.
/// </summary>
public sealed class AiService : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────
    public string ApiUrl       { get; set; } = "https://api.deepseek.com";
    public string ApiKey       { get; set; } = "";
    public string Model        { get; set; } = "deepseek-chat";
    public string Protocol     { get; set; } = AiProtocols.OpenAi;
    public string SystemPrompt { get; set; } =
        "You are a helpful assistant integrated in TLIG Dashboard, " +
        "an industrial HMI dashboard and AI connector.";

    // Optional provider hint sent to the server proxy so it knows which configured
    // provider/key to use (the client always posts an OpenAI-shaped body). Ignored
    // when talking to a provider directly.
    public string ProviderId   { get; set; } = "";

    private const string AnthropicVersion = "2023-06-01";

    // ── History ───────────────────────────────────────────────────────────────
    public IReadOnlyList<ChatMessage> History => _history;
    private readonly List<ChatMessage> _history = new();
    public void ClearHistory() => _history.Clear();

    // ── HTTP ──────────────────────────────────────────────────────────────────
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private bool IsAnthropic => Protocol == AiProtocols.Anthropic;

    // ── Endpoint per protocol ──────────────────────────────────────────────────
    private string Endpoint() => IsAnthropic
        ? $"{ApiUrl.TrimEnd('/')}/v1/messages"
        : $"{ApiUrl.TrimEnd('/')}/chat/completions";

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
        request.Headers.Accept.ParseAdd("text/event-stream");

        if (IsAnthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            // Tell the proxy which configured provider to route to (no-op for direct providers).
            if (!string.IsNullOrWhiteSpace(ProviderId))
                request.Headers.TryAddWithoutValidation("X-Ai-Provider", ProviderId);
        }

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
            if (data == "[DONE]") break;           // OpenAI sentinel (Anthropic ends on stream close)

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

    private string BuildBody() => IsAnthropic ? BuildAnthropicBody() : BuildOpenAiBody();

    // NOTE: built with JsonObject/JsonArray (not JsonSerializer.Serialize(new {...})) —
    // the Release publish runs with reflection-based JSON serialization disabled
    // (trim-friendly default), which throws NotSupportedException for anonymous types.

    private string BuildOpenAiBody()
    {
        var msgs = new JsonArray();
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            msgs.Add(new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
        foreach (var m in _history)
            msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        return new JsonObject
        {
            ["model"]       = Model,
            ["messages"]    = msgs,
            ["stream"]      = true,
            ["max_tokens"]  = 4096,
            ["temperature"] = 0.7
        }.ToJsonString();
    }

    private string BuildAnthropicBody()
    {
        // Anthropic carries the system prompt as a top-level field, not a message.
        var msgs = new JsonArray();
        foreach (var m in _history)
            msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        return new JsonObject
        {
            ["model"]      = Model,
            ["system"]     = SystemPrompt ?? "",
            ["messages"]   = msgs,
            ["stream"]     = true,
            ["max_tokens"] = 4096
        }.ToJsonString();
    }

    private string? ExtractToken(string data) =>
        IsAnthropic ? ExtractAnthropicToken(data) : ExtractOpenAiToken(data);

    private static string? ExtractOpenAiToken(string data)
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

    private static string? ExtractAnthropicToken(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) ||
                t.GetString() != "content_block_delta") return null;
            if (root.TryGetProperty("delta", out var delta) &&
                delta.TryGetProperty("text", out var text))
                return text.GetString();
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
