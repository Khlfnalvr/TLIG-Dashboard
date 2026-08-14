using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Streaming AI chat client supporting three wire protocols (see <see cref="AiProtocols"/>):
///
/// <list type="bullet">
/// <item><b>openai</b> — DeepSeek, OpenAI, Gemini (OpenAI-compatible endpoint), Ollama,
///   and the server's own <c>/ai</c> proxy. POSTs to <c>{ApiUrl}/chat/completions</c>,
///   Bearer auth, <c>choices[].delta.content</c> SSE.</item>
/// <item><b>anthropic</b> — Anthropic Messages API. POSTs to <c>{ApiUrl}/v1/messages</c>,
///   <c>x-api-key</c> auth, system prompt as a top-level field, <c>content_block_delta</c> SSE.</item>
/// <item><b>gemini</b> — Google Generative Language API. POSTs to
///   <c>{ApiUrl}/v1beta/models/{Model}:streamGenerateContent</c> (model in the URL, not the
///   body), <c>x-goog-api-key</c> auth, <c>contents[].parts[].text</c> body with a top-level
///   <c>systemInstruction</c>, <c>candidates[].content.parts[].text</c> SSE.</item>
/// </list>
///
/// On the client flavor the service always speaks <b>openai</b> to the server proxy;
/// the server does any Anthropic/Gemini translation. On the server flavor the protocol
/// is the active provider's protocol.
/// </summary>
public sealed class AiService
{
    // ── Configuration ─────────────────────────────────────────────────────────
    public string ApiUrl       { get; set; } = "https://api.deepseek.com";
    public string ApiKey       { get; set; } = "";
    public string Model        { get; set; } = "deepseek-v4-flash";
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

    /// Injects a turn into history without calling the API — lets a caller (e.g. the
    /// PID Designer's advisor exchange) fold context into a shared AiService instance
    /// so a later StreamChatAsync call carries it forward as prior conversation.
    public void AddHistoryEntry(string role, string content) => _history.Add(new ChatMessage(role, content));

    // ── HTTP ──────────────────────────────────────────────────────────────────
    // One connection pool shared by every AiService. These are not all long-lived —
    // the PID advisor builds one per RUN — and an HttpClient per instance leaks its
    // pool and churns sockets into TIME_WAIT (the classic .NET socket-exhaustion
    // antipattern), especially since nothing ever disposed them. HttpClient is
    // thread-safe for concurrent requests, so sharing is safe for the server flavor
    // handling several students at once.
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private bool IsAnthropic => Protocol == AiProtocols.Anthropic;
    private bool IsGemini    => Protocol == AiProtocols.Gemini;

    // ── Endpoint per protocol ──────────────────────────────────────────────────
    // Gemini embeds the model in the URL path rather than the request body.
    private string Endpoint() => Protocol switch
    {
        AiProtocols.Anthropic => $"{ApiUrl.TrimEnd('/')}/v1/messages",
        AiProtocols.Gemini    => $"{ApiUrl.TrimEnd('/')}/v1beta/models/{Model}:streamGenerateContent?alt=sse",
        _                     => $"{ApiUrl.TrimEnd('/')}/chat/completions",
    };

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
        else if (IsGemini)
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);
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
            if (data == "[DONE]") break;           // OpenAI sentinel (Anthropic/Gemini end on stream close)

            var token = ExtractToken(data);
            if (string.IsNullOrEmpty(token)) continue;

            full.Append(token);
            onToken(token);           // caller is responsible for thread safety
        }

        var reply = full.ToString();
        if (reply.Length > 0)
        {
            _history.Add(new ChatMessage("assistant", reply));
            var preview = userMessage.Length > 120 ? userMessage[..120] + "…" : userMessage;
            ActivityStore.Instance.LogSession(
                Models.ActivityCategory.AIInteraction,
                Models.ActivityActions.AiQuery,
                $"Query: {preview}",
                metadata: new() { ["model"] = Model, ["replyChars"] = reply.Length.ToString() });
        }

        return reply;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string BuildBody() => Protocol switch
    {
        AiProtocols.Anthropic => BuildAnthropicBody(),
        AiProtocols.Gemini    => BuildGeminiBody(),
        _                     => BuildOpenAiBody(),
    };

    // NOTE: built with JsonObject/JsonArray (not JsonSerializer.Serialize(new {...})) —
    // the Release publish runs with reflection-based JSON serialization disabled
    // (trim-friendly default), which throws NotSupportedException for anonymous types.

    private string BuildOpenAiBody()
    {
        var msgs = new JsonArray();
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            msgs.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = SystemPrompt });
        foreach (var m in _history)
            msgs.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

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
            msgs.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        return new JsonObject
        {
            ["model"]      = Model,
            ["system"]     = SystemPrompt ?? "",
            ["messages"]   = msgs,
            ["stream"]     = true,
            ["max_tokens"] = 4096
        }.ToJsonString();
    }

    // Gemini: no system-role message and no "model" field in the body — the system
    // prompt is a top-level systemInstruction and the model is already in the URL.
    // Roles are "user"/"model" (not "assistant").
    private string BuildGeminiBody()
    {
        var contents = new JsonArray();
        foreach (var m in _history)
            contents.Add(new JsonObject
            {
                ["role"]  = m.Role == "assistant" ? "model" : "user",
                ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Content } }
            });

        var body = new JsonObject
        {
            ["contents"]         = contents,
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = 4096, ["temperature"] = 0.7 }
        };
        if (!string.IsNullOrWhiteSpace(SystemPrompt))
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = SystemPrompt } }
            };
        return body.ToJsonString();
    }

    private string? ExtractToken(string data) => Protocol switch
    {
        AiProtocols.Anthropic => ExtractAnthropicToken(data),
        AiProtocols.Gemini    => ExtractGeminiToken(data),
        _                     => ExtractOpenAiToken(data),
    };

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

    private static string? ExtractGeminiToken(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0) return null;
            var parts = candidates[0].GetProperty("content").GetProperty("parts");
            if (parts.GetArrayLength() > 0 && parts[0].TryGetProperty("text", out var t))
                return t.GetString();
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
}

public record ChatMessage(string Role, string Content);
