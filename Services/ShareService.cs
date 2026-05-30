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
/// Frames are pushed server→client as binary WebSocket messages:
///   byte 0      = channel (<see cref="ChannelCamera"/> / <see cref="ChannelHmi"/>)
///   byte 1..end = JPEG image bytes
///
/// AI chat is proxied over HTTP: the client POSTs an OpenAI-compatible body to
/// <c>/ai/chat/completions</c> with the access token as the Bearer credential;
/// the server swaps in its real provider key and streams the reply back.
/// </summary>
public static class ShareProtocol
{
    public const byte ChannelCamera = 0;
    public const byte ChannelHmi    = 1;

    public const string WsPath  = "/ws";
    public const string AiPath  = "/ai/chat/completions";
    public const string GuidWs  = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    /// <summary>Generates a short, human-typeable access token.</summary>
    public static string NewToken()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
        Span<byte> rnd = stackalloc byte[8];
        RandomNumberGenerator.Fill(rnd);
        var sb = new StringBuilder(8);
        foreach (var b in rnd) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
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
    public string Token       { get; private set; } = "";
    public bool   ShareCamera { get; private set; } = true;
    public bool   ShareHmi    { get; private set; } = true;
    public int    ClientCount => _clients.Count;

    public event Action? StateChanged;

    private TcpListener?             _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<WsClient, byte> _clients = new();

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start(int port, string token, bool shareCamera = true, bool shareHmi = true)
    {
        Stop();
        Port  = port;
        ShareCamera = shareCamera;
        ShareHmi    = shareHmi;
        Token = string.IsNullOrWhiteSpace(token) ? ShareProtocol.NewToken() : token.Trim();
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

            if (method == "POST" && path == ShareProtocol.AiPath)
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
        if (GetToken(rawPath, headers) != Token)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid token", ct);
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

    // ── AI proxy: inject the real provider key, override the model, stream back ─

    private async Task HandleAiProxyAsync(
        NetworkStream stream, Dictionary<string, string> headers, CancellationToken ct)
    {
        // Validate the bearer token the client presented.
        string presented = "";
        if (headers.TryGetValue("authorization", out var auth) &&
            auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            presented = auth["Bearer ".Length..].Trim();

        if (presented != Token)
        {
            await WriteSimpleAsync(stream, "401 Unauthorized", "text/plain", "Invalid token", ct);
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
        host = host.Trim();
        // Accept "host:port", "ws://host:port", or "http://host:port".
        host = host.Replace("http://", "").Replace("ws://", "").TrimEnd('/');

        var uri = new Uri($"ws://{host}{ShareProtocol.WsPath}?token={Uri.EscapeDataString(token.Trim())}");
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
