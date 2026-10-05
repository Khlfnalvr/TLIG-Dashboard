using System.Diagnostics;
using System.Net;
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

    private const int MaxSendAttempts = 3;
    private const int MaxSentHistory = 40;

    // ── Long-term memory (Level 1: rolling summary) ─────────────────────
    // History yang dikirim ke API dibatasi 40 turn terakhir; selebihnya DULU
    // hilang diam-diam. Sekarang pesan lama dipadatkan jadi ringkasan
    // (ConversationSummary) yang ikut dikirim sebagai konteks + disimpan
    // ChatSessionService ke chat-sessions.json agar bertahan antar restart.
    private const int CompactThreshold = 60;  // mulai padatkan di atas ini
    private const int KeepRecent = 40;        // pesan mentah yang dipertahankan
    private const int MaxSummaryChars = 2000; // ringkasan tidak boleh bengkak
    private readonly object _historyLock = new();

    /// <summary>
    /// Ringkasan padat pesan-pesan lama sesi aktif. Disuntik ke system prompt
    /// tiap request; disinkron dua arah dengan ChatSession.Summary.
    /// </summary>
    public string ConversationSummary { get; set; } = "";

    // ── History ───────────────────────────────────────────────────────────────
    public IReadOnlyList<ChatMessage> History => _history;
    private readonly List<ChatMessage> _history = new();
    public void ClearHistory() => _history.Clear();

    /// Injects a turn into history without calling the API — lets a caller (e.g. the
    /// PID Designer's advisor exchange) fold context into a shared AiService instance
    /// so a later StreamChatAsync call carries it forward as prior conversation.
    public void AddHistoryEntry(string role, string content) => _history.Add(new ChatMessage(role, content));

    public void AmendLastAssistantEntry(string suffix)
    {
        if (string.IsNullOrEmpty(suffix)) return;
        if (_history.Count > 0 && _history[^1].Role == "assistant")
            _history[^1] = _history[^1] with { Content = _history[^1].Content + suffix };
    }

    /// <summary>
    /// Padatkan pesan lama jadi ringkasan bila history melebihi
    /// <see cref="CompactThreshold"/>. Dipanggil fire-and-forget dari
    /// ChatSessionService.SaveActive (semua jalur chat bermuara ke sana).
    /// Mengembalikan (summaryBaru, jumlahPesanDibuang); (null, 0) = tidak ada
    /// yang dilakukan. Aman gagal: riwayat tidak disentuh bila summarizer gagal.
    /// </summary>
    public async Task<(string? Summary, int Dropped)> MaybeCompactAsync(
        CancellationToken ct = default)
    {
        List<ChatMessage> chunk;
        string prior;
        lock (_historyLock)
        {
            if (_history.Count <= CompactThreshold)
                return (null, 0);
            int drop = _history.Count - KeepRecent;
            chunk = _history.GetRange(0, drop);
            prior = ConversationSummary ?? "";
        }

        string? updated = await SummarizeChunkAsync(prior, chunk, ct).ConfigureAwait(false);
        if (updated is null)
            return (null, 0);

        lock (_historyLock)
        {
            // Sesi bisa berganti / history berubah saat summarizer bekerja —
            // hanya pangkas bila chunk yang sama masih di depan.
            if (_history.Count < chunk.Count)
                return (null, 0);
            for (int i = 0; i < chunk.Count; i++)
                if (_history[i].Role != chunk[i].Role || _history[i].Content != chunk[i].Content)
                    return (updated, 0); // ringkasan tetap dipakai, tapi jangan pangkas
            _history.RemoveRange(0, chunk.Count);
            ConversationSummary = updated;
            return (updated, chunk.Count);
        }
    }

    private async Task<string?> SummarizeChunkAsync(
        string prior, List<ChatMessage> chunk, CancellationToken ct)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prior))
            sb.AppendLine(prior.Trim());
        foreach (var m in chunk)
        {
            string line = m.Content.Replace("\n", " ").Trim();
            if (line.Length > 300) line = line[..300] + "…";
            sb.Append(m.Role == "user" ? "U: " : "A: ").AppendLine(line);
        }
        string condensed = sb.ToString().Trim();
        if (condensed.Length == 0)
            return null;

        // Tanpa kunci API tidak bisa memanggil LLM — pakai ringkasan ekstraktif
        // (topik dari pesan user) agar history tetap terpangkas.
        if (string.IsNullOrWhiteSpace(ApiKey))
            return ExtractiveSummary(prior, chunk);

        try
        {
            string prompt =
                "Summarize this conversation excerpt for long-term memory. " +
                "Write 3-8 dense bullet lines, same language as the conversation, " +
                "keeping: user goals, key facts/decisions/numbers, open tasks. " +
                "No preamble, bullets only.\n\n" +
                (string.IsNullOrWhiteSpace(prior) ? "" : "Existing summary:\n" + prior.Trim() + "\n\n") +
                "New excerpt:\n" + condensed;

            string? reply = await CallSummarizerAsync(prompt, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(reply))
                return ExtractiveSummary(prior, chunk);
            string merged = string.IsNullOrWhiteSpace(prior)
                ? reply.Trim()
                : prior.Trim() + "\n" + reply.Trim();
            if (merged.Length > MaxSummaryChars)
                merged = merged[^MaxSummaryChars..];
            return merged;
        }
        catch
        {
            return ExtractiveSummary(prior, chunk);
        }
    }

    private static string ExtractiveSummary(string prior, List<ChatMessage> chunk)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(prior))
            lines.AddRange(prior.Trim().Split('\n'));
        foreach (var m in chunk)
        {
            if (m.Role != "user") continue;
            string line = m.Content.Replace("\n", " ").Trim();
            if (line.Length == 0) continue;
            if (line.Length > 120) line = line[..120] + "…";
            string bullet = "- " + line;
            if (!lines.Contains(bullet))
                lines.Add(bullet);
        }
        // Cap: baris terbaru yang bertahan.
        while (string.Join("\n", lines).Length > MaxSummaryChars && lines.Count > 1)
            lines.RemoveAt(string.IsNullOrWhiteSpace(prior) ? 0 : 1);
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// Satu request non-streaming ke provider aktif tanpa menyentuh _history.
    /// Trim-friendly: body dibangun via JsonObject seperti Build*Body.
    /// </summary>
    private async Task<string?> CallSummarizerAsync(string prompt, CancellationToken ct)
    {
        string body = Protocol switch
        {
            AiProtocols.Anthropic => new JsonObject
            {
                ["model"] = Model,
                ["system"] = "You compress chat history into memory bullets.",
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "user", ["content"] = prompt }
                },
                ["stream"] = false,
                ["max_tokens"] = 512,
            }.ToJsonString(),
            AiProtocols.Gemini => new JsonObject
            {
                ["contents"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["parts"] = new JsonArray
                        {
                            new JsonObject { ["text"] = prompt }
                        }
                    }
                },
                ["generationConfig"] = new JsonObject
                {
                    ["maxOutputTokens"] = 512,
                    ["temperature"] = 0.2,
                },
                ["systemInstruction"] = new JsonObject
                {
                    ["parts"] = new JsonArray
                    {
                        new JsonObject { ["text"] = "You compress chat history into memory bullets." }
                    }
                },
            }.ToJsonString(),
            _ => new JsonObject
            {
                ["model"] = Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = "You compress chat history into memory bullets." },
                    new JsonObject { ["role"] = "user", ["content"] = prompt },
                },
                ["stream"] = false,
                ["max_tokens"] = 512,
                ["temperature"] = 0.2,
            }.ToJsonString(),
        };

        using var request = CreateRequest(body);
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ExtractNonStreamingText(json);
    }

    private string? ExtractNonStreamingText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (IsAnthropic)
            {
                if (root.TryGetProperty("content", out var content) && content.GetArrayLength() > 0)
                    return content[0].TryGetProperty("text", out var t) ? t.GetString() : null;
                return null;
            }
            if (IsGemini)
            {
                var parts = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
                if (parts.GetArrayLength() > 0 && parts[0].TryGetProperty("text", out var t))
                    return t.GetString();
                return null;
            }
            var choices = root.GetProperty("choices");
            if (choices.GetArrayLength() == 0) return null;
            var msg = choices[0].GetProperty("message");
            return msg.TryGetProperty("content", out var c) ? c.GetString() : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] summary parse failed: {ex.Message}");
            return null;
        }
    }

    public AiTokenUsage? LastUsage { get; private set; }

    public event Action<AiTokenUsage>? UsageChanged;

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

        int historyMark = _history.Count;
        int requestChars = (SystemPrompt?.Length ?? 0) + (ConversationSummary?.Length ?? 0) + userMessage.Length;
        foreach (var m in _history) requestChars += m.Content?.Length ?? 0;
        _history.Add(new ChatMessage("user", userMessage));

        try
        {
            var body = BuildBody();

            // ── Send with retry, read headers first ──────────────────────────
            using var response = await SendWithRetryAsync(body, ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n{ParseError(errJson)}");
            }

            var (reply, usedPrompt, usedCompletion) =
                await ReadStreamAsync(response, userMessage, onToken, ct).ConfigureAwait(false);

            AiTokenUsage usage = usedPrompt.HasValue || usedCompletion.HasValue
                ? new AiTokenUsage(usedPrompt ?? 0, usedCompletion ?? 0, IsEstimated: false)
                : new AiTokenUsage(requestChars / 4, reply.Length / 4, IsEstimated: true);
            LastUsage = usage;
            UsageChanged?.Invoke(usage);

            return reply;
        }
        catch
        {
            if (_history.Count > historyMark)
                _history.RemoveRange(historyMark, _history.Count - historyMark);
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(string body)
    {
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
        return request;
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(string body, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = CreateRequest(body);
            HttpResponseMessage? response = null;
            bool sent = false;
            try
            {
                response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                sent = true;
            }
            catch (HttpRequestException ex) when (attempt + 1 < MaxSendAttempts)
            {
                Debug.WriteLine($"[AiService] send attempt {attempt + 1} failed ({ex.Message}), retrying");
            }

            if (!sent)
            {
                await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (!IsTransientFailure(response!.StatusCode) || attempt + 1 >= MaxSendAttempts)
                return response;

            Debug.WriteLine($"[AiService] HTTP {(int)response.StatusCode}, retrying (attempt {attempt + 2})");
            response.Dispose();
            await Task.Delay(RetryDelay(attempt), ct).ConfigureAwait(false);
        }
    }

    private static bool IsTransientFailure(HttpStatusCode status) =>
        (int)status == 429 || ((int)status >= 500 && (int)status <= 599);

    private static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromSeconds(1 << Math.Min(attempt, 3));

    private async Task<(string Reply, int? UsedPrompt, int? UsedCompletion)> ReadStreamAsync(
        HttpResponseMessage response, string userMessage, Action<string> onToken, CancellationToken ct)
    {
        // ── Read SSE stream line-by-line ─────────────────────────────────────
        await using var stream = await response.Content
            .ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        var full = new StringBuilder();
        int? usedPrompt = null, usedCompletion = null;

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null
               && !ct.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: "))       continue;

            var data = line[6..].Trim();
            if (data == "[DONE]") break;           // OpenAI sentinel (Anthropic/Gemini end on stream close)

            CaptureUsage(data, ref usedPrompt, ref usedCompletion);

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

        return (reply, usedPrompt, usedCompletion);
    }

    private void CaptureUsage(string data, ref int? prompt, ref int? completion)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (IsAnthropic)
            {
                string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "message_start" &&
                    root.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("usage", out var u) &&
                    u.TryGetProperty("input_tokens", out var it))
                    prompt = it.GetInt32();
                else if (type == "message_delta" &&
                    root.TryGetProperty("usage", out var du) &&
                    du.TryGetProperty("output_tokens", out var ot))
                    completion = ot.GetInt32();
            }
            else if (IsGemini)
            {
                if (root.TryGetProperty("usageMetadata", out var um))
                {
                    if (um.TryGetProperty("promptTokenCount", out var pt)) prompt = pt.GetInt32();
                    if (um.TryGetProperty("candidatesTokenCount", out var cc)) completion = cc.GetInt32();
                }
            }
            else if (root.TryGetProperty("usage", out var ou))
            {
                if (ou.TryGetProperty("prompt_tokens", out var pt)) prompt = pt.GetInt32();
                if (ou.TryGetProperty("completion_tokens", out var cc)) completion = cc.GetInt32();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] usage parse failed: {ex.Message}");
        }
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

    private string EffectiveSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(ConversationSummary))
            return SystemPrompt ?? "";
        return (SystemPrompt ?? "").TrimEnd()
            + "\n\n[Ringkasan percakapan sebelumnya — ingat fakta di dalamnya, jangan minta pengguna mengulang]\n"
            + ConversationSummary.Trim();
    }

    /// <summary>
    /// Jendela history yang muat dalam limit konteks model: berjalan dari pesan
    /// terbaru ke terlama sampai budget token habis, dengan hard cap
    /// <see cref="MaxSentHistory"/>. Estimasi kasar 4 char ≈ 1 token.
    /// </summary>
    private List<ChatMessage> WindowedHistory(int systemChars)
    {
        int limit = AiContextLimits.ForModel(Model, out _);
        int budget = limit - (4096 + 1000); // completion + margin
        if (budget < 8000) budget = 8000;
        int avail = budget - systemChars / 4;
        if (avail < 1000) avail = 1000;

        var window = new List<ChatMessage>(Math.Min(_history.Count, MaxSentHistory));
        int used = 0;
        for (int i = _history.Count - 1; i >= 0 && window.Count < MaxSentHistory; i--)
        {
            int t = ((_history[i].Content?.Length ?? 0) / 4) + 4;
            if (used + t > avail) break;
            used += t;
            window.Add(_history[i]);
        }
        window.Reverse();
        return window;
    }

    private (string System, List<ChatMessage> Window) SnapshotWindow()
    {
        lock (_historyLock)
        {
            string system = EffectiveSystemPrompt();
            return (system, WindowedHistory(system.Length));
        }
    }

    private string BuildOpenAiBody()
    {
        var (system, window) = SnapshotWindow();
        var msgs = new JsonArray();
        if (!string.IsNullOrWhiteSpace(system))
            msgs.Add((JsonNode)new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in window)
            msgs.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        return new JsonObject
        {
            ["model"]       = Model,
            ["messages"]    = msgs,
            ["stream"]      = true,
            ["max_tokens"]  = 4096,
            ["temperature"] = 0.7,
            ["stream_options"] = new JsonObject { ["include_usage"] = true }
        }.ToJsonString();
    }

    private string BuildAnthropicBody()
    {
        // Anthropic carries the system prompt as a top-level field, not a message.
        var (system, window) = SnapshotWindow();
        var msgs = new JsonArray();
        foreach (var m in window)
            msgs.Add((JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content });

        return new JsonObject
        {
            ["model"]      = Model,
            ["system"]     = system,
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
        var (system, window) = SnapshotWindow();
        var contents = new JsonArray();
        foreach (var m in window)
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
        if (!string.IsNullOrWhiteSpace(system))
            body["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = system } }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] OpenAI token parse failed: {ex.Message}");
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] Anthropic token parse failed: {ex.Message}");
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] Gemini token parse failed: {ex.Message}");
        }
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[AiService] error-body parse failed: {ex.Message}");
        }
        return json.Length > 400 ? json[..400] + "…" : json;
    }
}

public record ChatMessage(string Role, string Content);

public sealed record AiTokenUsage(int PromptTokens, int CompletionTokens, bool IsEstimated)
{
    public int Total => PromptTokens + CompletionTokens;
}
