using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TLIGDashboard.Services;

/// <summary>
/// Direct TCP link to the PLC through the LabVIEW HMI (replaces the former
/// OPC UA / OPC DA path, which third-party apps could not use).
///
/// Topology — the dashboard can sit on either end of the socket:
///
///   • Server / listen mode (default):
///         LabVIEW HMI ──"TCP Open Connection"──▶ TLIG Dashboard (TcpListener)
///     The dashboard waits on the configured port; LabVIEW dials in with
///     "TCP Open Connection" (address = localhost, remote port = 6000).
///
///   • Client / connect mode:
///         TLIG Dashboard ──TCP──▶ LabVIEW HMI ("TCP Listen.vi")
///     The dashboard connects out to a LabVIEW listener.
///
/// Either way the wire protocol is the same line-based text, so it maps 1:1
/// onto LabVIEW's TCP Read/Write string primitives. Lines are CRLF-terminated
/// so LabVIEW's TCP Read in "CRLF" mode returns exactly one message:
///
///   • Write (dashboard → LabVIEW): the three PID gains packed on one line
///         kp=2.5;ki=0.1;kd=0.05\r\n
///     Parse in LabVIEW with "Scan From String" (format  kp=%f;ki=%f;kd=%f )
///     and wire the results to KC / KI / KD. Single tags use the same shape:
///         cmd=run\r\n
///   • Read (LabVIEW → dashboard): the same "name=value" format whenever a
///     value changes; multiple tags may be packed on one line separated by ';'.
///
/// Numbers use '.' as the decimal separator (invariant culture). The four PID
/// step-response metrics received from LabVIEW are recognised and forwarded to
/// <see cref="PidMetricsService"/> automatically (risetime / overshoot /
/// settling / sse, case-insensitive).
///
/// Reconnect: in client mode a dropped socket is retried every 5 s; in server
/// mode the listener simply keeps waiting for LabVIEW to dial in again.
/// </summary>
public sealed class PlcTcpService : IDisposable
{
    public enum LinkMode { Client, Server }

    // ── Public events ─────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    /// <summary>Raised for every tag=value pair received from LabVIEW.</summary>
    public event Action<string, string>? DataReceived;
    /// <summary>Raised whenever a live peer connection is established (either mode).</summary>
    public event Action? Connected;

    // ── Live state ────────────────────────────────────────────────────────────
    /// <summary>True while a peer socket is actually connected (data can flow).</summary>
    public bool   IsConnected { get; private set; }
    /// <summary>True while the link is running: connected, reconnecting, or listening.</summary>
    public bool   IsActive    { get; private set; }
    public LinkMode Mode      { get; private set; } = LinkMode.Client;
    public string Host        { get; private set; } = "";
    public int    Port        { get; private set; }
    /// <summary>"host:port" label for status texts.</summary>
    public string EndpointLabel => Port > 0 ? $"{Host}:{Port}" : Host;
    public int    FramesReceived { get; private set; }
    public int    ErrorCount     { get; private set; }

    /// <summary>Last received value per tag (case-insensitive names).</summary>
    public IReadOnlyDictionary<string, string> Tags
    {
        get { lock (_tagLock) return new Dictionary<string, string>(_tags, StringComparer.OrdinalIgnoreCase); }
    }

    // ── Internals ─────────────────────────────────────────────────────────────
    private TcpClient?               _client;
    private TcpListener?             _listener;
    private NetworkStream?           _stream;
    private CancellationTokenSource? _cts;
    private readonly object          _writeLock = new();
    private readonly object          _tagLock   = new();
    private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastErrorTime = DateTime.MinValue;
    private bool     _userDisconnected;

    private const int ConnectTimeoutMs   = 5_000;
    private const int ReconnectDelayMs   = 5_000;

    // ── Start (mode dispatch) / Disconnect ─────────────────────────────────────

    /// <summary>
    /// Starts the link in the requested mode. In server mode a successful return
    /// means the listener is up (LabVIEW may not have dialled in yet); watch
    /// <see cref="IsConnected"/> / <see cref="Connected"/> for the actual peer.
    /// </summary>
    public Task<bool> StartAsync(bool serverMode, string host, int port)
        => serverMode ? ListenAsync(host, port) : ConnectAsync(host, port);

    public async Task<bool> ConnectAsync(string host, int port)
    {
        Disconnect();
        Mode = LinkMode.Client;
        Host = host.Trim();
        Port = port;
        _userDisconnected = false;
        _cts = new CancellationTokenSource();

        bool ok = await TryOpenAsync(_cts.Token);
        if (ok)
        {
            IsActive = true;
            StatusChanged?.Invoke(
                LocalizationManager.Instance.Format("OpcUa_StatusConnected", EndpointLabel));
        }
        return ok;
    }

    /// <summary>Server mode: bind a listener and accept LabVIEW's incoming connection.</summary>
    public async Task<bool> ListenAsync(string host, int port)
    {
        Disconnect();
        Mode = LinkMode.Server;
        Host = host.Trim();
        Port = port;
        _userDisconnected = false;
        _cts = new CancellationTokenSource();

        try
        {
            var listener = CreateListener(Host, port);
            listener.Start();
            _listener   = listener;
            IsActive    = true;
            IsConnected = false;
            StatusChanged?.Invoke(
                LocalizationManager.Instance.Format("OpcUa_StatusListening", EndpointLabel));
            _ = AcceptLoopAsync(listener, _cts.Token);
            return true;   // listener is up; LabVIEW connects on its own
        }
        catch (Exception ex)
        {
            IsActive = false;
            FireError($"{EndpointLabel} — {ex.Message}");
            return false;
        }
    }

    // Loopback/any hosts get a dual-stack (IPv4 + IPv6) listener so LabVIEW
    // connects whether its "TCP Open Connection" targets "localhost" (which
    // Windows may resolve to ::1), "127.0.0.1", or the machine's LAN IP.
    // A specific address binds to exactly that interface.
    private static TcpListener CreateListener(string host, int port) => host switch
    {
        "" or "0.0.0.0" or "any" or "Any" or "localhost" or "127.0.0.1"
            => TcpListener.Create(port),   // IPv6Any with DualMode = accepts both families
        _   => IPAddress.TryParse(host, out var ip)
                   ? new TcpListener(ip, port)
                   : TcpListener.Create(port),
    };

    public void Disconnect()
    {
        _userDisconnected = true;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;
        CloseSocket();

        bool wasActive = IsActive || IsConnected;
        IsConnected = false;
        IsActive    = false;
        if (wasActive)
            StatusChanged?.Invoke(LocalizationManager.Instance.Get("OpcUa_StatusDisconnected"));
    }

    // ── Client connect + auto-reconnect ────────────────────────────────────────

    private async Task<bool> TryOpenAsync(CancellationToken ct)
    {
        try
        {
            var client = new TcpClient { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(ConnectTimeoutMs);
                await client.ConnectAsync(Host, Port, timeout.Token);
            }

            _client = client;
            _stream = client.GetStream();
            IsConnected = true;
            Connected?.Invoke();
            _ = ClientReadLoopAsync(_stream, ct);
            return true;
        }
        catch (Exception ex)
        {
            IsConnected = false;
            if (!ct.IsCancellationRequested)
                FireError($"{EndpointLabel} — {ex.Message}");
            return false;
        }
    }

    private async Task ClientReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        await ReadStreamAsync(stream, ct);

        if (ct.IsCancellationRequested || _userDisconnected) return;

        // Connection dropped unexpectedly → report and retry every 5 s.
        IsConnected = false;
        CloseSocket();
        StatusChanged?.Invoke(LocalizationManager.Instance.Get("OpcUa_StatusReconnecting"));

        while (!ct.IsCancellationRequested && !_userDisconnected)
        {
            await Task.Delay(ReconnectDelayMs, ct).ContinueWith(_ => { });
            if (ct.IsCancellationRequested || _userDisconnected) return;
            if (await TryOpenAsync(ct))
            {
                StatusChanged?.Invoke(
                    LocalizationManager.Instance.Format("OpcUa_StatusConnected", EndpointLabel));
                return;   // new ClientReadLoopAsync took over
            }
        }
    }

    // ── Server accept loop ─────────────────────────────────────────────────────

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && !_userDisconnected)
            {
                TcpClient accepted;
                try { accepted = await listener.AcceptTcpClientAsync(ct); }
                catch { break; }   // listener stopped / cancelled

                CloseSocket();               // single-peer model: drop any previous client
                accepted.NoDelay = true;
                _client = accepted;
                _stream = accepted.GetStream();
                IsConnected = true;
                StatusChanged?.Invoke(
                    LocalizationManager.Instance.Format("OpcUa_StatusConnected", EndpointLabel));
                Connected?.Invoke();

                await ReadStreamAsync(_stream, ct);   // pumps until LabVIEW disconnects

                IsConnected = false;
                CloseSocket();
                if (ct.IsCancellationRequested || _userDisconnected) break;

                StatusChanged?.Invoke(
                    LocalizationManager.Instance.Format("OpcUa_StatusListening", EndpointLabel));
            }
        }
        catch { /* fall through to teardown */ }

        try { listener.Stop(); } catch { }
        // Only clear shared state if a newer Start()/Disconnect() hasn't already
        // replaced this listener — otherwise we'd stomp the new link's IsActive.
        if (_listener == listener)
        {
            _listener = null;
            IsActive  = false;
        }
    }

    // ── Shared read path ───────────────────────────────────────────────────────

    /// <summary>Reads '\n'-terminated (CR optional) lines until the socket closes.</summary>
    private async Task ReadStreamAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer  = new byte[4096];
        var pending = new StringBuilder();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;   // remote closed
                pending.Append(Encoding.UTF8.GetString(buffer, 0, n));
                FlushCompleteLines(pending);
            }
        }
        catch { /* socket error → caller decides whether to reconnect / re-accept */ }
    }

    private void CloseSocket()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close();  } catch { }
        _stream = null;
        _client = null;
    }

    /// <summary>Extracts complete '\n'-terminated lines, leaves the partial tail.</summary>
    private void FlushCompleteLines(StringBuilder pending)
    {
        while (true)
        {
            string all = pending.ToString();
            int nl = all.IndexOf('\n');
            if (nl < 0) return;

            string line = all[..nl].TrimEnd('\r');
            pending.Clear();
            pending.Append(all[(nl + 1)..]);

            if (line.Length > 0) ParseLine(line);
        }
    }

    /// <summary>Parses "name=value" pairs, optionally ';'-packed in one line.</summary>
    private void ParseLine(string line)
    {
        FramesReceived++;
        foreach (var pair in line.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            string name  = pair[..eq].Trim();
            string value = pair[(eq + 1)..].Trim();
            if (name.Length == 0) continue;

            lock (_tagLock) _tags[name] = value;
            DataReceived?.Invoke(name, value);
            MaybeUpdatePidMetric(name, value);
        }
    }

    /// <summary>Forwards recognised PID step-response metrics to PidMetricsService.</summary>
    private static void MaybeUpdatePidMetric(string name, string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            return;

        var m = PidMetricsService.Instance;
        switch (name.ToLowerInvariant())
        {
            case "risetime":  case "rise_time":          m.RiseTime         = v; break;
            case "overshoot":                            m.Overshoot        = v; break;
            case "settling":  case "settlingtime":
            case "settling_time":                        m.Settling         = v; break;
            case "sse":       case "steadystateerror":
            case "steady_state_error":                   m.SteadyStateError = v; break;
        }
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the three PID gains as one CRLF-terminated line ("kp=..;ki=..;kd=..")
    /// — the shape LabVIEW's "Scan From String" (kp=%f;ki=%f;kd=%f) parses in one
    /// TCP Read. Returns false when no peer is connected.
    /// </summary>
    public bool WritePid(double kp, double ki, double kd)
    {
        string line = string.Format(CultureInfo.InvariantCulture,
            "kp={0:0.####};ki={1:0.####};kd={2:0.####}\r\n", kp, ki, kd);
        return WriteRaw(line);
    }

    /// <summary>Sends one "name=value" line to LabVIEW. Returns false when not connected.</summary>
    public bool WriteTag(string name, double value) =>
        WriteTag(name, value.ToString("0.####", CultureInfo.InvariantCulture));

    public bool WriteTag(string name, string value) => WriteRaw($"{name}={value}\r\n");

    /// <summary>Convenience: writes several tags, one line each.</summary>
    public bool WriteTags(params (string Name, double Value)[] tags)
    {
        bool ok = true;
        foreach (var (name, value) in tags)
            ok &= WriteTag(name, value);
        return ok;
    }

    private bool WriteRaw(string line)
    {
        var stream = _stream;
        if (!IsConnected || stream is null) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line);
            lock (_writeLock) stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            FireError(ex.Message);
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FireError(string msg)
    {
        ErrorCount++;
        if ((DateTime.UtcNow - _lastErrorTime).TotalSeconds < 5) return;
        _lastErrorTime = DateTime.UtcNow;
        ErrorOccurred?.Invoke(msg);
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        try { _listener?.Stop(); } catch { }
        CloseSocket();
    }
}
