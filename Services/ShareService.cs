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

    public const string WsPath         = "/ws";
    public const string AiPath         = "/ai/chat/completions";
    public const string AuthLoginPath  = "/auth/login";
    public const string AuthLogoutPath = "/auth/logout";
    public const string GuidWs         = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

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
            else if (method == "POST" && path == ShareProtocol.AiPath)
            {
                await HandleAiProxyAsync(stream, headers, ct);
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

    // ── AI proxy: inject the real provider key, override the model, stream back ─

    private async Task HandleAiProxyAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        // Validate the session token the client presented as the Bearer credential.
        string presented = "";
        if (headers.TryGetValue("authorization", out var auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = auth["Bearer ".Length..].Trim();

        if (!ValidateSession(presented))
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid or expired session", ct);
            return;
        }

        // Read the request body (Content-Length).
        var body = await ReadBodyAsync(stream, headers, ct);

        var settings = AppSettingsService.Load();
        if (string.IsNullOrWhiteSpace(settings.AiApiKey))
        {
            await WriteSimpleAsync(stream, "503 Service Unavailable", "text/plain",
                "Server has no AI API key configured.", ct);
            return;
        }

        // Override the model with the server's configured model.
        string forwardBody = body;
        try
        {
            var node = JsonNode.Parse(body);
            if (node is not null)
            {
                node["model"] = settings.AiModel;
                forwardBody = node.ToJsonString();
            }
        }
        catch { /* forward as-is if it isn't valid JSON */ }

        var providerUrl = $"{settings.AiApiUrl.TrimEnd('/')}/chat/completions";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
        using var req  = new HttpRequestMessage(HttpMethod.Post, providerUrl)
        {
            Content = new StringContent(forwardBody, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {settings.AiApiKey}");
        req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

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
            // Stream the upstream response back to the client using chunked transfer.
            int status = (int)resp.StatusCode;
            var header =
                $"HTTP/1.1 {status} {resp.ReasonPhrase}\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "Transfer-Encoding: chunked\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);

            await using var upstream = await resp.Content.ReadAsStreamAsync(ct);
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
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
            await stream.FlushAsync(ct);
        }
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
