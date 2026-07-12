using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace TLIGDashboard.Services;

/// <summary>
/// Direct TCP link to the PLC through the LabVIEW HMI (replaces the former
/// OPC UA / OPC DA path, which third-party apps could not use).
///
/// Topology:
///   TLIG Dashboard Server ──TCP──▶ LabVIEW HMI ──▶ PLC
///
/// LabVIEW runs a plain TCP listener ("TCP Listen.vi") on the configured port;
/// this service is the TCP client. The wire protocol is line-based text so it
/// maps 1:1 onto LabVIEW's TCP Read/Write string primitives:
///
///   • Write (app → LabVIEW → PLC):   one tag per line, e.g.
///         kp=2.50\n        ki=0.10\n        kd=0.05\n        sp=50\n
///   • Read (PLC → LabVIEW → app):    LabVIEW sends the same format whenever a
///     value changes (or on a fixed poll). Multiple tags may be packed into one
///     line separated by ';', e.g.   pv=52.3;sp=50;out=71.2\n
///
/// Numbers use '.' as the decimal separator (invariant culture). Tag names are
/// free-form; the four PID step-response metrics are recognised and forwarded
/// to <see cref="PidMetricsService"/> automatically:
///   risetime / overshoot / settling / sse   (case-insensitive).
///
/// Reconnect: while the user has not explicitly disconnected, a dropped socket
/// is retried every 5 s (same behaviour the OPC UA session had).
/// </summary>
public sealed class PlcTcpService : IDisposable
{
    // ── Public events ─────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;
    /// <summary>Raised for every tag=value pair received from LabVIEW.</summary>
    public event Action<string, string>? DataReceived;

    // ── Live state ────────────────────────────────────────────────────────────
    public bool   IsConnected { get; private set; }
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
    private NetworkStream?           _stream;
    private CancellationTokenSource? _cts;
    private readonly object          _writeLock = new();
    private readonly object          _tagLock   = new();
    private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastErrorTime = DateTime.MinValue;
    private bool     _userDisconnected;

    private const int ConnectTimeoutMs   = 5_000;
    private const int ReconnectDelayMs   = 5_000;

    // ── Connect / Disconnect ──────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(string host, int port)
    {
        Disconnect();
        Host = host.Trim();
        Port = port;
        _userDisconnected = false;
        _cts = new CancellationTokenSource();

        bool ok = await TryOpenAsync(_cts.Token);
        if (ok)
        {
            StatusChanged?.Invoke(
                LocalizationManager.Instance.Format("OpcUa_StatusConnected", EndpointLabel));
        }
        return ok;
    }

    public void Disconnect()
    {
        _userDisconnected = true;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        CloseSocket();

        if (IsConnected)
        {
            IsConnected = false;
            StatusChanged?.Invoke(LocalizationManager.Instance.Get("OpcUa_StatusDisconnected"));
        }
    }

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
            _ = ReadLoopAsync(_stream, ct);
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

    private void CloseSocket()
    {
        try { _stream?.Dispose(); } catch { }
        try { _client?.Close();  } catch { }
        _stream = null;
        _client = null;
    }

    // ── Read loop + auto-reconnect ────────────────────────────────────────────

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
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
        catch { /* socket error → fall through to reconnect */ }

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
                return;   // new ReadLoopAsync took over
            }
        }
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

    /// <summary>Sends one "name=value\n" line to LabVIEW. Returns false when not connected.</summary>
    public bool WriteTag(string name, double value) =>
        WriteTag(name, value.ToString("0.####", CultureInfo.InvariantCulture));

    public bool WriteTag(string name, string value)
    {
        var stream = _stream;
        if (!IsConnected || stream is null) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes($"{name}={value}\n");
            lock (_writeLock) stream.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            FireError(ex.Message);
            return false;
        }
    }

    /// <summary>Convenience: writes several tags in one call (kp, ki, kd, …).</summary>
    public bool WriteTags(params (string Name, double Value)[] tags)
    {
        bool ok = true;
        foreach (var (name, value) in tags)
            ok &= WriteTag(name, value);
        return ok;
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
        CloseSocket();
    }
}
