using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>
/// Wire-protocol constants shared by <see cref="ShareServer"/> and
/// <see cref="ShareClient"/>.
///
/// Authentication is credential-based: the client POSTs <c>{username,password}</c>
/// to <c>/auth/login</c> and receives a short-lived <b>session token</b> that the
/// server validates against its user database. That session token is then used as
/// the WebSocket credential and the AI-proxy Bearer credential — there is no longer
/// a static, manually-shared access token.
///
/// Frames are pushed server→client as binary WebSocket messages:
///   byte 0      = channel (<see cref="ChannelCamera"/> / <see cref="ChannelHmi"/>)
///   byte 1..end = JPEG image bytes
///
/// AI chat is proxied over HTTP: the client POSTs an OpenAI-compatible body to
/// <c>/ai/chat/completions</c> with the session token as the Bearer credential;
/// the server swaps in its real provider key and streams the reply back.
/// </summary>
public static class ShareProtocol
{
    public const byte ChannelCamera = 0;
    public const byte ChannelHmi    = 1;

    public const string WsPath            = "/ws";
    public const string AiPath            = "/ai/chat/completions";
    public const string AiConfigPath      = "/ai/config";       // GET = list providers, POST = save (staff)
    public const string AuthLoginPath     = "/auth/login";
    public const string AuthLogoutPath    = "/auth/logout";
    public const string AuthSignupPath    = "/auth/signup";
    public const string TasksPath         = "/tasks";          // GET = list, POST = create/update
    public const string TasksDeletePath   = "/tasks/delete";   // POST {id}
    public const string TasksCompletePath = "/tasks/complete"; // POST {id, completed}
    public const string GuidWs            = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Generates an opaque, high-entropy session token (URL-safe base64).</summary>
    public static string NewSessionToken()
    {
        Span<byte> rnd = stackalloc byte[24];
        RandomNumberGenerator.Fill(rnd);
        return Convert.ToBase64String(rnd)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  SERVER
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Lightweight share server built directly on <see cref="TcpListener"/> (no
/// http.sys / URL ACL, so it binds to the LAN without administrator rights).
/// Broadcasts the latest camera + HMI JPEG frame to every connected client and
/// proxies AI chat completions using the server's own provider credentials.
/// </summary>
public sealed class ShareServer
{
    public static ShareServer Instance { get; } = new();
    private ShareServer() { }

    // ── State ───────────────────────────────────────────────────────────────
    public bool   IsRunning   { get; private set; }
    public int    Port        { get; private set; }
    public bool   ShareCamera { get; private set; } = true;
    public bool   ShareHmi    { get; private set; } = true;
    public int    ClientCount => _clients.Count;

    public event Action? StateChanged;

    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<WsClient, byte> _clients = new();

    // Active login sessions: token → who it belongs to. Issued by /auth/login,
    // validated by the WebSocket + AI proxy, revoked by /auth/logout and on Stop.
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private sealed record Session(string Username, string Role, DateTime IssuedUtc);

    private string IssueSession(UserAccount user)
    {
        var token = ShareProtocol.NewSessionToken();
        _sessions[token] = new Session(user.Username, user.Role, DateTime.UtcNow);
        return token;
    }

    private bool ValidateSession(string? token) =>
        !string.IsNullOrEmpty(token) && _sessions.ContainsKey(token);

    private Session? GetSession(string? token) =>
        !string.IsNullOrEmpty(token) && _sessions.TryGetValue(token!, out var s) ? s : null;

    private void RevokeSession(string? token)
    {
        if (!string.IsNullOrEmpty(token)) _sessions.TryRemove(token, out _);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int port, bool shareCamera = true, bool shareHmi = true)
    {
        Stop();
        Port  = port;
        ShareCamera = shareCamera;
        ShareHmi    = shareHmi;
        _cts  = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        IsRunning = true;
        _ = AcceptLoopAsync(_cts.Token);
        RaiseStateChanged();
    }

    public void Stop()
    {
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        foreach (var c in _clients.Keys)
            c.Dispose();
        _clients.Clear();
        _sessions.Clear();
        _cts?.Dispose();
        _cts = null;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke();

    // ── Frame broadcast (called from the capture pipelines) ────────────────────

    public void PushCameraFrame(byte[] jpeg) => Broadcast(ShareProtocol.ChannelCamera, jpeg);
    public void PushHmiFrame(byte[] jpeg)    => Broadcast(ShareProtocol.ChannelHmi, jpeg);

    private void Broadcast(byte channel, byte[] jpeg)
    {
        if (!IsRunning || _clients.IsEmpty) return;
        foreach (var c in _clients.Keys)
            c.Offer(channel, jpeg);
    }

    // ── Accept loop ─────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener!;
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleConnectionAsync(tcp, ct);
        }
    }

    private async Task HandleConnectionAsync(TcpClient tcp, CancellationToken ct)
    {
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();

            var (requestLine, headers) = await ReadHeadersAsync(stream, ct);
            if (requestLine is null) { tcp.Close(); return; }

            var parts  = requestLine.Split(' ');
            var method = parts.Length > 0 ? parts[0] : "";
            var rawPath = parts.Length > 1 ? parts[1] : "/";
            var path    = rawPath.Split('?')[0];

            bool isWs = headers.TryGetValue("upgrade", out var up) &&
                        up.Contains("websocket", StringComparison.OrdinalIgnoreCase);

            if (isWs && path == ShareProtocol.WsPath)
            {
                await HandleWebSocketAsync(tcp, stream, rawPath, headers, ct);
                return; // socket owns the connection lifetime
            }

            if (method == "POST" && path == ShareProtocol.AuthLoginPath)
            {
                await HandleAuthLoginAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.AuthLogoutPath)
            {
                await HandleAuthLogoutAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.AuthSignupPath)
            {
                await HandleAuthSignupAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.AiPath)
            {
                await HandleAiProxyAsync(stream, headers, ct);
            }
            else if (method == "GET" && path == ShareProtocol.AiConfigPath)
            {
                await HandleAiConfigGetAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.AiConfigPath)
            {
                await HandleAiConfigSaveAsync(stream, headers, ct);
            }
            else if (method == "GET" && path == ShareProtocol.TasksPath)
            {
                await HandleTasksGetAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.TasksPath)
            {
                await HandleTasksSaveAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.TasksDeletePath)
            {
                await HandleTasksDeleteAsync(stream, headers, ct);
            }
            else if (method == "POST" && path == ShareProtocol.TasksCompletePath)
            {
                await HandleTasksCompleteAsync(stream, headers, ct);
            }
            else if (method == "GET" && path == "/info")
            {
                await WriteJsonAsync(stream, "{\"app\":\"TLIG Dashboard Server\"}", ct);
            }
            else
            {
                await WriteSimpleAsync(stream, "404 Not Found", "text/plain", "Not Found", ct);
            }

            tcp.Close();
        }
        catch
        {
            try { tcp.Close(); } catch { }
        }
    }

    // ── WebSocket upgrade + per-client send loop ──────────────────────────────

    private async Task HandleWebSocketAsync(
        TcpClient tcp, NetworkStream stream, string rawPath,
        Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!ValidateSession(GetToken(rawPath, headers)))
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            tcp.Close();
            return;
        }

        if (!headers.TryGetValue("sec-websocket-key", out var key))
        {
            tcp.Close();
            return;
        }

        var accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key + ShareProtocol.GuidWs)));

        var response =
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        var bytes = Encoding.ASCII.GetBytes(response);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);

        var ws = WebSocket.CreateFromStream(
            stream, isServer: true, subProtocol: null,
            keepAliveInterval: TimeSpan.FromSeconds(20));

        var client = new WsClient(ws, tcp);
        _clients[client] = 0;
        RaiseStateChanged();

        try
        {
            await client.SendLoopAsync(ct);
        }
        catch { }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
            RaiseStateChanged();
        }
    }

    // ── Auth: credential login → session token, and logout → revoke ─────────────

    private async Task HandleAuthLoginAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var body = await ReadBodyAsync(stream, headers, ct);

        string username = "", password = "";
        try
        {
            var node = JsonNode.Parse(body);
            username = (string?)node?["username"] ?? "";
            password = (string?)node?["password"] ?? "";
        }
        catch { /* malformed body → treated as empty credentials below */ }

        var user = UserStore.Instance.Verify(username, password);
        if (user is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "application/json",
                "{\"error\":\"invalid_credentials\"}", ct);
            return;
        }

        var token = IssueSession(user);
        var json = new JsonObject
        {
            ["token"]       = token,
            ["username"]    = user.Username,
            ["displayName"] = user.DisplayName,
            ["role"]        = user.Role,
        }.ToJsonString();
        await WriteJsonAsync(stream, json, ct);
    }

    private async Task HandleAuthLogoutAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (headers.TryGetValue("authorization", out var auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            RevokeSession(auth["Bearer ".Length..].Trim());

        await WriteJsonAsync(stream, "{\"ok\":true}", ct);
    }

    // ── Auth: self-registration (client signup) → create a Viewer account ───────

    private async Task HandleAuthSignupAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var body = await ReadBodyAsync(stream, headers, ct);

        string email = "", password = "";
        try
        {
            var node = JsonNode.Parse(body);
            email    = (string?)node?["email"]    ?? "";
            password = (string?)node?["password"] ?? "";
        }
        catch { /* malformed body → empty credentials → validation fails below */ }

        var (ok, error) = UserStore.Instance.SignUp(email, password);
        if (!ok)
        {
            // Duplicate → 409 Conflict; everything else (bad domain/format/password) → 400.
            var status = error == "Signup_ErrExists" ? "409 Conflict" : "400 Bad Request";
            var json   = new JsonObject { ["error"] = error ?? "Signup_ErrUnknown" }.ToJsonString();
            await WriteSimpleAsync(stream, status, "application/json", json, ct);
            return;
        }

        await WriteJsonAsync(stream, "{\"ok\":true}", ct);
    }

    // ── AI proxy: resolve the chosen provider, inject its key, stream back ──────
    // The client always POSTs an OpenAI-shaped body. For OpenAI-protocol providers
    // we forward it (overriding the model); for Anthropic we translate the request
    // and translate the Anthropic SSE reply back into OpenAI SSE so the client's
    // parser is unchanged.

    private async Task HandleAiProxyAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }

        var body     = await ReadBodyAsync(stream, headers, ct);
        var settings = AppSettingsService.Load();

        // Which provider? Prefer the client's X-Ai-Provider hint, else the server default.
        string providerId = headers.TryGetValue("x-ai-provider", out var ph) && AiProviders.IsValid(ph)
            ? ph : settings.AiActiveProvider;
        var info   = AiProviders.Resolve(providerId);
        var config = settings.AiProviderConfigs.FirstOrDefault(c => c.Id == info.Id);

        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(config.ApiKey))
        {
            await WriteSimpleAsync(stream, "503 Service Unavailable", "text/plain",
                $"Provider '{info.Name}' is not enabled or has no API key on the server.", ct);
            return;
        }

        string model       = ResolveModel(body, config, settings, info);
        bool   isAnthropic = info.Protocol == AiProtocols.Anthropic;
        string providerUrl = isAnthropic
            ? $"{info.BaseUrl.TrimEnd('/')}/v1/messages"
            : $"{info.BaseUrl.TrimEnd('/')}/chat/completions";
        string forwardBody = isAnthropic
            ? OpenAiToAnthropicBody(body, model)
            : OverrideModel(body, model);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var req  = new HttpRequestMessage(HttpMethod.Post, providerUrl)
        {
            Content = new StringContent(forwardBody, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
        if (isAnthropic)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", config.ApiKey);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.ApiKey}");
        }

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            await WriteSimpleAsync(stream, "502 Bad Gateway", "text/plain",
                $"Upstream error: {ex.Message}", ct);
            return;
        }

        using (resp)
        {
            int status = (int)resp.StatusCode;

            // Relay an upstream error verbatim so the client surfaces the real reason.
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                await WriteSimpleAsync(stream, $"{status} {resp.ReasonPhrase}", "application/json", err, ct);
                return;
            }

            var header =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);

            await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
            if (isAnthropic)
                await RelayAnthropicAsOpenAiAsync(upstream, stream, ct);
            else
                await RelayRawChunkedAsync(upstream, stream, ct);

            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
    }

    /// <summary>Forwards OpenAI SSE bytes unchanged, framed as HTTP chunks.</summary>
    private static async Task RelayRawChunkedAsync(Stream upstream, NetworkStream stream, CancellationToken ct)
    {
        var buf = new byte[8192];
        int n;
        while ((n = await upstream.ReadAsync(buf, ct)) > 0)
        {
            var sizeLine = Encoding.ASCII.GetBytes($"{n:X}\r\n");
            await stream.WriteAsync(sizeLine, ct);
            await stream.WriteAsync(buf.AsMemory(0, n), ct);
            await stream.WriteAsync("\r\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
    }

    /// <summary>
    /// Translates an Anthropic Messages SSE stream into OpenAI chat-completion SSE
    /// (<c>choices[].delta.content</c> + a terminal <c>[DONE]</c>) so the client's
    /// OpenAI parser handles it without knowing the provider.
    /// </summary>
    private static async Task RelayAnthropicAsOpenAiAsync(Stream upstream, NetworkStream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(upstream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (data.Length == 0) continue;

            string? text = null;
            try
            {
                var node = JsonNode.Parse(data);
                if ((string?)node?["type"] == "content_block_delta")
                    text = (string?)node?["delta"]?["text"];
            }
            catch { }

            if (string.IsNullOrEmpty(text)) continue;

            var chunk = new JsonObject
            {
                ["choices"] = new JsonArray
                {
                    new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = text } }
                }
            }.ToJsonString();

            await WriteSseChunkAsync(stream, $"data: {chunk}\n\n", ct);
        }
        await WriteSseChunkAsync(stream, "data: [DONE]\n\n", ct);
    }

    private static async Task WriteSseChunkAsync(NetworkStream stream, string text, CancellationToken ct)
    {
        var payload  = Encoding.UTF8.GetBytes(text);
        var sizeLine = Encoding.ASCII.GetBytes($"{payload.Length:X}\r\n");
        await stream.WriteAsync(sizeLine, ct);
        await stream.WriteAsync(payload, ct);
        await stream.WriteAsync("\r\n"u8.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    private static string ResolveModel(string body, AiProviderSettings config, AppSettings settings, AiProviderInfo info)
    {
        string requested = "";
        try { requested = (string?)JsonNode.Parse(body)?["model"] ?? ""; } catch { }

        if (!string.IsNullOrWhiteSpace(requested) && config.Models.Contains(requested))
            return requested;
        if (config.Id == settings.AiActiveProvider &&
            !string.IsNullOrWhiteSpace(settings.AiActiveModel) &&
            config.Models.Contains(settings.AiActiveModel))
            return settings.AiActiveModel;
        if (config.Models.Count > 0) return config.Models[0];
        return info.Models.Length > 0 ? info.Models[0] : requested;
    }

    private static string OverrideModel(string body, string model)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not null) { node["model"] = model; return node.ToJsonString(); }
        }
        catch { }
        return body;
    }

    private static string OpenAiToAnthropicBody(string openAiBody, string model)
    {
        var system    = new StringBuilder();
        var outMsgs   = new JsonArray();
        int maxTokens = 4096;

        try
        {
            var node = JsonNode.Parse(openAiBody)?.AsObject();
            if ((int?)node?["max_tokens"] is int mi && mi > 0) maxTokens = mi;
            if (node?["messages"] is JsonArray msgs)
            {
                foreach (var m in msgs)
                {
                    var role    = (string?)m?["role"]    ?? "";
                    var content = (string?)m?["content"] ?? "";
                    if (role == "system")
                    {
                        if (system.Length > 0) system.Append("\n\n");
                        system.Append(content);
                    }
                    else if (role is "user" or "assistant")
                    {
                        outMsgs.Add(new JsonObject { ["role"] = role, ["content"] = content });
                    }
                }
            }
        }
        catch { }

        var body = new JsonObject
        {
            ["model"]      = model,
            ["max_tokens"] = maxTokens,
            ["stream"]     = true,
            ["messages"]   = outMsgs,
        };
        if (system.Length > 0) body["system"] = system.ToString();
        return body.ToJsonString();
    }

    // ── AI config: which providers/models are enabled (authored by staff) ───────

    private async Task HandleAiConfigGetAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }

        var settings = AppSettingsService.Load();
        var arr = new JsonArray();
        foreach (var info in AiProviders.All)
        {
            var cfg = settings.AiProviderConfigs.FirstOrDefault(c => c.Id == info.Id);

            var models = new JsonArray();
            foreach (var m in cfg?.Models ?? new List<string>()) models.Add(m);
            var allModels = new JsonArray();
            foreach (var m in info.Models) allModels.Add(m);

            arr.Add(new JsonObject
            {
                ["id"]        = info.Id,
                ["name"]      = info.Name,
                ["protocol"]  = info.Protocol,
                ["enabled"]   = cfg?.Enabled ?? false,
                ["hasKey"]    = !string.IsNullOrWhiteSpace(cfg?.ApiKey),
                ["models"]    = models,
                ["allModels"] = allModels,
            });
        }

        var json = new JsonObject
        {
            ["providers"]      = arr,
            ["canEdit"]        = UserRoles.IsStaff(session.Role),
            ["activeProvider"] = settings.AiActiveProvider,
            ["activeModel"]    = settings.AiActiveModel,
            ["systemPrompt"]   = settings.AiSystemPrompt,
        }.ToJsonString();
        await WriteJsonAsync(stream, json, ct);
    }

    private async Task HandleAiConfigSaveAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }
        if (!UserRoles.IsStaff(session.Role))
        {
            await WriteSimpleAsync(stream, "403 Forbidden", "text/plain", "Only staff can edit AI settings", ct);
            return;
        }

        var body     = await ReadBodyAsync(stream, headers, ct);
        var settings = AppSettingsService.Load();

        try
        {
            var node = JsonNode.Parse(body);

            if (node?["providers"] is JsonArray provs)
            {
                foreach (var p in provs)
                {
                    var id   = (string?)p?["id"] ?? "";
                    var info = AiProviders.Find(id);
                    if (info is null) continue;

                    var cfg = settings.AiProviderConfigs.FirstOrDefault(c => c.Id == id);
                    if (cfg is null)
                    {
                        cfg = new AiProviderSettings { Id = id };
                        settings.AiProviderConfigs.Add(cfg);
                    }

                    if (p?["enabled"] is JsonNode en) cfg.Enabled = (bool?)en ?? cfg.Enabled;

                    // Models: keep only ids that exist in the registry catalogue.
                    if (p?["models"] is JsonArray ms)
                    {
                        var picked = new List<string>();
                        foreach (var m in ms)
                        {
                            var mid = (string?)m ?? "";
                            if (info.Models.Contains(mid) && !picked.Contains(mid)) picked.Add(mid);
                        }
                        cfg.Models = picked;
                    }

                    // Key: a blank apiKey preserves the existing key; clearKey wipes it.
                    if ((bool?)p?["clearKey"] == true) cfg.ApiKey = "";
                    else
                    {
                        var key = (string?)p?["apiKey"] ?? "";
                        if (!string.IsNullOrWhiteSpace(key)) cfg.ApiKey = key.Trim();
                    }
                }
            }

            if ((string?)node?["systemPrompt"] is string sp && sp.Length > 0)
                settings.AiSystemPrompt = sp;

            var ap = (string?)node?["activeProvider"] ?? "";
            if (AiProviders.IsValid(ap)) settings.AiActiveProvider = ap;

            var am = (string?)node?["activeModel"] ?? "";
            if (!string.IsNullOrWhiteSpace(am)) settings.AiActiveModel = am;
        }
        catch
        {
            await WriteSimpleAsync(stream, "400 Bad Request", "text/plain", "Malformed config", ct);
            return;
        }

        AppSettingsService.Save(settings);
        await WriteJsonAsync(stream, "{\"ok\":true}", ct);
    }

    // ── Tasks: server-backed learning-task database (authored by staff) ─────────

    private static string BearerToken(Dictionary<string, string> headers) =>
        headers.TryGetValue("authorization", out var auth) &&
        auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? auth["Bearer ".Length..].Trim() : "";

    private static JsonObject TaskToJson(LearningTask t, bool completed) => new()
    {
        ["id"]        = t.Id,
        ["title"]     = t.Title,
        ["objective"] = t.Objective,
        ["metric"]    = t.Metric,
        ["op"]        = t.Op,
        ["target"]    = t.Target,
        ["tolerance"] = t.Tolerance,
        ["createdBy"] = t.CreatedBy,
        ["completed"] = completed,
    };

    private async Task HandleTasksGetAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }

        var tasks     = TaskStore.Instance.GetTasks();
        var completed = TaskStore.Instance.GetCompletedIds(session.Username);

        var arr = new JsonArray();
        foreach (var t in tasks) arr.Add(TaskToJson(t, completed.Contains(t.Id)));

        var json = new JsonObject
        {
            ["tasks"]    = arr,
            ["canEdit"]  = UserRoles.IsStaff(session.Role),
            ["username"] = session.Username,
        }.ToJsonString();
        await WriteJsonAsync(stream, json, ct);
    }

    private async Task HandleTasksSaveAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }
        if (!UserRoles.IsStaff(session.Role))
        {
            await WriteSimpleAsync(stream, "403 Forbidden", "text/plain", "Only staff can edit tasks", ct);
            return;
        }

        var body = await ReadBodyAsync(stream, headers, ct);
        LearningTask task;
        try
        {
            var node = JsonNode.Parse(body);
            task = new LearningTask
            {
                Id        = (string?)node?["id"]        ?? "",
                Title     = (string?)node?["title"]     ?? "",
                Objective = (string?)node?["objective"] ?? "",
                Metric    = (string?)node?["metric"]    ?? "",
                Op        = (string?)node?["op"]        ?? TaskOps.Lte,
                Target    = (double?)node?["target"]    ?? 0,
                Tolerance = (double?)node?["tolerance"] ?? 0,
                CreatedBy = session.Username,
            };
        }
        catch
        {
            await WriteSimpleAsync(stream, "400 Bad Request", "text/plain", "Malformed task", ct);
            return;
        }

        if (string.IsNullOrWhiteSpace(task.Title))
        {
            await WriteSimpleAsync(stream, "400 Bad Request", "application/json", "{\"error\":\"title_required\"}", ct);
            return;
        }
        if (!TaskMetrics.IsValid(task.Metric)) task.Metric = TaskMetrics.None;
        if (!TaskOps.IsValid(task.Op))         task.Op     = TaskOps.Lte;

        var saved = TaskStore.Instance.Upsert(task);
        await WriteJsonAsync(stream, new JsonObject { ["ok"] = true, ["id"] = saved.Id }.ToJsonString(), ct);
    }

    private async Task HandleTasksDeleteAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }
        if (!UserRoles.IsStaff(session.Role))
        {
            await WriteSimpleAsync(stream, "403 Forbidden", "text/plain", "Only staff can delete tasks", ct);
            return;
        }

        var body = await ReadBodyAsync(stream, headers, ct);
        string id = "";
        try { id = (string?)JsonNode.Parse(body)?["id"] ?? ""; } catch { }

        TaskStore.Instance.Delete(id);
        await WriteJsonAsync(stream, "{\"ok\":true}", ct);
    }

    private async Task HandleTasksCompleteAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        var session = GetSession(BearerToken(headers));
        if (session is null)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }

        var body = await ReadBodyAsync(stream, headers, ct);
        string id = ""; bool completed = false;
        try
        {
            var node  = JsonNode.Parse(body);
            id        = (string?)node?["id"]        ?? "";
            completed = (bool?)node?["completed"]   ?? false;
        }
        catch { }

        TaskStore.Instance.SetCompleted(session.Username, id, completed);
        await WriteJsonAsync(stream, "{\"ok\":true}", ct);
    }

    // ── HTTP parsing helpers ──────────────────────────────────────────────────

    private static string? GetTokenQuery(string rawPath)
    {
        int q = rawPath.IndexOf('?');
        if (q < 0) return null;
        foreach (var pair in rawPath[(q + 1)..].Split('&'))
        {
            var kv = pair.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "token")
                return Uri.UnescapeDataString(kv[1]);
        }
        return null;
    }

    private static string GetToken(string rawPath, Dictionary<string, string> headers)
        => GetTokenQuery(rawPath)
           ?? (headers.TryGetValue("x-share-token", out var t) ? t : "");

    private static async Task<(string? requestLine, Dictionary<string, string> headers)>
        ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
    {
        var raw = await ReadUntilAsync(stream, "\r\n\r\n"u8.ToArray(), 64 * 1024, ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw is null) return (null, headers);

        var text  = Encoding.ASCII.GetString(raw);
        var lines = text.Split("\r\n");
        if (lines.Length == 0) return (null, headers);

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        return (lines[0], headers);
    }

    private static async Task<string> ReadBodyAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        if (!headers.TryGetValue("content-length", out var lenStr) ||
            !int.TryParse(lenStr, out var len) || len <= 0)
            return "";

        var buf = new byte[len];
        int read = 0;
        while (read < len)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, len - read), ct);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private static async Task<byte[]?> ReadUntilAsync(
        NetworkStream stream, byte[] delimiter, int max, CancellationToken ct)
    {
        var ms  = new MemoryStream();
        var one = new byte[1];
        while (ms.Length < max)
        {
            int n = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (n == 0) return ms.Length > 0 ? ms.ToArray() : null;
            ms.WriteByte(one[0]);
            if (ms.Length >= delimiter.Length)
            {
                var arr = ms.GetBuffer();
                bool match = true;
                for (int i = 0; i < delimiter.Length; i++)
                    if (arr[ms.Length - delimiter.Length + i] != delimiter[i]) { match = false; break; }
                if (match) return ms.ToArray();
            }
        }
        return ms.ToArray();
    }

    private static async Task WriteSimpleAsync(
        NetworkStream stream, string status, string contentType, string body, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    private static Task WriteJsonAsync(NetworkStream stream, string json, CancellationToken ct)
        => WriteSimpleAsync(stream, "200 OK", "application/json", json, ct);

    // ── Per-connected-client send pump (keeps only the latest frame) ───────────

    private sealed class WsClient : IDisposable
    {
        private readonly WebSocket   _ws;
        private readonly TcpClient   _tcp;
        private readonly SemaphoreSlim _signal = new(0, 1);
        private readonly object      _lock = new();

        private byte[]? _camera; private bool _camDirty;
        private byte[]? _hmi;    private bool _hmiDirty;

        public WsClient(WebSocket ws, TcpClient tcp) { _ws = ws; _tcp = tcp; }

        public void Offer(byte channel, byte[] jpeg)
        {
            lock (_lock)
            {
                if (channel == ShareProtocol.ChannelCamera) { _camera = jpeg; _camDirty = true; }
                else                                        { _hmi = jpeg;    _hmiDirty = true; }
            }
            try { if (_signal.CurrentCount == 0) _signal.Release(); } catch { }
        }

        public async Task SendLoopAsync(CancellationToken ct)
        {
            while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                await _signal.WaitAsync(ct);

                byte[]? cam = null, hmi = null;
                lock (_lock)
                {
                    if (_camDirty) { cam = _camera; _camDirty = false; }
                    if (_hmiDirty) { hmi = _hmi;    _hmiDirty = false; }
                }

                if (cam is not null) await SendFrameAsync(ShareProtocol.ChannelCamera, cam, ct);
                if (hmi is not null) await SendFrameAsync(ShareProtocol.ChannelHmi, hmi, ct);
            }
        }

        private async Task SendFrameAsync(byte channel, byte[] jpeg, CancellationToken ct)
        {
            var msg = new byte[jpeg.Length + 1];
            msg[0] = channel;
            Buffer.BlockCopy(jpeg, 0, msg, 1, jpeg.Length);
            await _ws.SendAsync(msg, WebSocketMessageType.Binary, endOfMessage: true, ct);
        }

        public void Dispose()
        {
            try { _ws.Abort(); } catch { }
            try { _ws.Dispose(); } catch { }
            try { _tcp.Close(); } catch { }
            try { _signal.Dispose(); } catch { }
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
//  CLIENT
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Connects to a <see cref="ShareServer"/> and raises an event for each camera /
/// HMI JPEG frame received. AI chat is handled separately by the regular
/// <see cref="AiService"/> pointed at the server's <c>/ai</c> proxy.
/// </summary>
public sealed class ShareClient : IDisposable
{
    public static ShareClient Instance { get; } = new();
    private ShareClient() { }

    public bool IsConnected { get; private set; }

    /// <summary>Raised with (channel, jpegBytes) for every received frame.</summary>
    public event Action<byte, byte[]>? FrameReceived;
    public event Action<bool, string>? ConnectionChanged;

    private ClientWebSocket?         _ws;
    private CancellationTokenSource? _cts;

    public async Task<bool> ConnectAsync(string host, string token)
    {
        Disconnect();
        // Derive the WebSocket scheme from the same TLS-detection logic used by AuthClient:
        // wss:// for proxied domains (Cloudflare Tunnel etc.), ws:// for direct LAN:port.
        var wsScheme = AuthClient.NeedsTls(host) ? "wss" : "ws";
        var hostNorm = AuthClient.NormalizeHost(host);

        var uri = new Uri($"{wsScheme}://{hostNorm}{ShareProtocol.WsPath}?token={Uri.EscapeDataString(token.Trim())}");
        _cts = new CancellationTokenSource();
        _ws  = new ClientWebSocket();

        try
        {
            await _ws.ConnectAsync(uri, _cts.Token);
            IsConnected = true;
            ConnectionChanged?.Invoke(true, host);
            _ = ReceiveLoopAsync(_cts.Token);
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false, ex.Message);
            Disconnect();
            return false;
        }
    }

    public void Disconnect()
    {
        IsConnected = false;
        try { _cts?.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var ws  = _ws!;
        var buf = new byte[64 * 1024];
        var acc = new MemoryStream();

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                acc.SetLength(0);
                WebSocketReceiveResult result;
                var segment = new ArraySegment<byte>(buf);
                do
                {
                    result = await ws.ReceiveAsync(segment, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                        IsConnected = false;
                        ConnectionChanged?.Invoke(false, "closed");
                        return;
                    }
                    acc.Write(buf, 0, result.Count);
                } while (!result.EndOfMessage);

                if (acc.Length < 2) continue;
                var data    = acc.ToArray();
                byte channel = data[0];
                var jpeg     = data[1..];
                FrameReceived?.Invoke(channel, jpeg);
            }
        }
        catch
        {
            IsConnected = false;
            ConnectionChanged?.Invoke(false, "disconnected");
        }
    }

    public void Dispose() => Disconnect();
}

// ════════════════════════════════════════════════════════════════════════════
//  AUTH CLIENT (credential login → session token)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>Outcome of a credential login attempt against a server.</summary>
public sealed class AuthResult
{
    public bool    Success     { get; init; }
    public string  Token       { get; init; } = "";
    public string  DisplayName { get; init; } = "";
    public string  Role        { get; init; } = "";
    /// <summary>Localization key describing the failure (null on success).</summary>
    public string? ErrorKey    { get; init; }
    /// <summary>Optional technical detail (exception text, status code) for logs.</summary>
    public string? Detail      { get; init; }
}

/// <summary>
/// Stateless helper that performs credential login / logout against a
/// <see cref="ShareServer"/>. On success the server returns a session token that
/// the caller stores and feeds to <see cref="ShareClient"/> and the AI proxy.
/// </summary>
public static class AuthClient
{
    /// <summary>
    /// Strips any explicit scheme and trailing slash, returning just the
    /// <c>host[:port]</c> string used for connection.
    /// Examples: "http://192.168.1.10:8088/" → "192.168.1.10:8088"
    ///           "https://abc.trycloudflare.com" → "abc.trycloudflare.com"
    /// </summary>
    public static string NormalizeHost(string host) =>
        (host ?? "").Trim()
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://",  "", StringComparison.OrdinalIgnoreCase)
            .Replace("wss://",   "", StringComparison.OrdinalIgnoreCase)
            .Replace("ws://",    "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

    /// <summary>
    /// Returns true when the host string indicates a TLS connection is required.
    /// Rule: a host WITHOUT an explicit port number is assumed to be a proxied
    /// domain (Cloudflare Tunnel, ngrok HTTPS, etc.) that terminates TLS at the
    /// edge → use wss:// and https://.
    /// A host WITH an explicit port (e.g. "192.168.1.10:8088", "lab.internal:8088")
    /// is a direct / LAN connection → use ws:// and http://.
    /// The user can override by prefixing "https://" or "wss://" explicitly.
    /// </summary>
    public static bool NeedsTls(string rawHost)
    {
        rawHost = (rawHost ?? "").Trim();
        // Explicit scheme takes precedence.
        if (rawHost.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            rawHost.StartsWith("wss://",   StringComparison.OrdinalIgnoreCase))
            return true;
        if (rawHost.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) ||
            rawHost.StartsWith("ws://",    StringComparison.OrdinalIgnoreCase))
            return false;

        // No explicit scheme: presence of a port number → direct TCP, plain.
        var normalized = NormalizeHost(rawHost);
        int colonIdx   = normalized.LastIndexOf(':');
        if (colonIdx >= 0 && int.TryParse(normalized.AsSpan(colonIdx + 1), out _))
            return false;   // has ":PORT" → LAN / direct

        return true;        // domain with no port → assume proxied HTTPS
    }

    /// <summary>
    /// Returns the base HTTP/HTTPS URL for the server (no trailing slash).
    /// Used by <see cref="LoginAsync"/>, <see cref="LogoutAsync"/>, and
    /// <see cref="Views.AIPage"/> for the AI proxy URL.
    /// </summary>
    public static string BaseUrl(string rawHost)
    {
        var host   = NormalizeHost(rawHost);
        var scheme = NeedsTls(rawHost) ? "https" : "http";
        return $"{scheme}://{host}";
    }

    public static async Task<AuthResult> LoginAsync(
        string host, string username, string password, CancellationToken ct = default)
    {
        var normalizedHost = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return new AuthResult { ErrorKey = "Login_ErrorNoServer" };

        var url     = $"{BaseUrl(host)}{ShareProtocol.AuthLoginPath}";
        var payload = new JsonObject { ["username"] = username, ["password"] = password }.ToJsonString();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var resp = await http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
                return new AuthResult { ErrorKey = "Login_ErrorInvalid" };
            if (!resp.IsSuccessStatusCode)
                return new AuthResult { ErrorKey = "Login_ErrorUnreachable", Detail = $"HTTP {(int)resp.StatusCode}" };

            var json = await resp.Content.ReadAsStringAsync(ct);
            var node = JsonNode.Parse(json);
            return new AuthResult
            {
                Success     = true,
                Token       = (string?)node?["token"]       ?? "",
                DisplayName = (string?)node?["displayName"] ?? username,
                Role        = (string?)node?["role"]        ?? "",
            };
        }
        catch (Exception ex)
        {
            return new AuthResult { ErrorKey = "Login_ErrorUnreachable", Detail = ex.Message };
        }
    }

    /// <summary>
    /// Registers a new account on the server (the <c>/auth/signup</c> endpoint).
    /// On success the account exists and can immediately be used with
    /// <see cref="LoginAsync"/>. On failure <see cref="AuthResult.ErrorKey"/> carries
    /// a localization key (e.g. <c>Signup_ErrEmailDomain</c>, <c>Signup_ErrExists</c>).
    /// </summary>
    public static async Task<AuthResult> SignUpAsync(
        string host, string email, string password, CancellationToken ct = default)
    {
        var normalizedHost = NormalizeHost(host);
        if (string.IsNullOrWhiteSpace(normalizedHost))
            return new AuthResult { ErrorKey = "Login_ErrorNoServer" };

        var url     = $"{BaseUrl(host)}{ShareProtocol.AuthSignupPath}";
        var payload = new JsonObject { ["email"] = email, ["password"] = password }.ToJsonString();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            using var resp = await http.PostAsync(
                url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);

            if (resp.IsSuccessStatusCode)
                return new AuthResult { Success = true };

            // The server returns the failure reason as a localization key in {"error":...}.
            var key = "Signup_ErrUnknown";
            try
            {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                key = (string?)node?["error"] ?? key;
            }
            catch { /* non-JSON body → keep the generic key */ }
            return new AuthResult { ErrorKey = key, Detail = $"HTTP {(int)resp.StatusCode}" };
        }
        catch (Exception ex)
        {
            return new AuthResult { ErrorKey = "Login_ErrorUnreachable", Detail = ex.Message };
        }
    }

    /// <summary>Best-effort revoke of a session token on the server.</summary>
    public static async Task LogoutAsync(string host, string token)
    {
        if (string.IsNullOrWhiteSpace(NormalizeHost(host)) ||
            string.IsNullOrWhiteSpace(token)) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req  = new HttpRequestMessage(
                HttpMethod.Post, $"{BaseUrl(host)}{ShareProtocol.AuthLogoutPath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            await http.SendAsync(req);
        }
        catch { /* logout is best-effort; the session also dies when the server stops */ }
    }
}
