using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TLIGDashboard.Services;

/// <summary>One named value received from LabVIEW (e.g. "Temperature" = "24.5").</summary>
public readonly record struct HmiDatum(string Key, string Value);

/// <summary>
/// Single, app-wide bridge that receives live numeric/text values from a LabVIEW VI
/// over TCP and hands them to the HMI panel for display.
///
/// Topology: the dashboard is the TCP <b>listener</b> (server) on <see cref="Port"/>;
/// the LabVIEW VI is the <b>client</b> that connects and streams data. Start the
/// dashboard first, then run the VI.
///
/// Wire format (UTF-8 stream): a continuous sequence of <c>Key=Value</c> lines, each
/// terminated by a newline. TCP is a byte stream (not messages), so the boundary is
/// the newline — every complete line updates one value in place. Blank lines and
/// lines without '=' are ignored. Example the VI writes each cycle (~1 Hz):
///     MainValue=12.34\n
///     Current=1.20\n
///     Status=RUN\n
///     Sensor1=24.5\n
///
/// LabVIEW side: "TCP Open Connection" (address = dashboard IP, port = <see cref="Port"/>),
/// then in the loop build that string with "Format Into String" and send it with
/// "TCP Write"; "TCP Close Connection" when done. No NI toolkit required — TCP is part
/// of core LabVIEW.
///
/// The link is bidirectional: the dashboard also sends control commands back to the
/// VI on the same connection via <see cref="Send"/> (e.g. <c>Kp=12</c>, <c>Setpoint=10</c>,
/// <c>Pump=40</c>, <c>Run=1</c>). The VI reads those with "TCP Read" and applies them, so
/// changing a value in the dashboard controls LabVIEW in real time.
///
/// The service owns the listener in ONE place (singleton) so both HMI views (the
/// dashboard panel and the Live View page) share a single server and the same
/// snapshot. Keys are kept in first-seen order so the readout does not jump around
/// as values update. Multiple simultaneous client connections are tolerated and all
/// merge into the same snapshot; a dropped connection leaves the listener running so
/// the VI can reconnect.
/// </summary>
public sealed class HmiDataService
{
    public static HmiDataService Instance { get; } = new();
    private HmiDataService() { }

    private readonly object _gate = new();
    private readonly List<string> _order = [];                                    // keys, first-seen order
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    // Connected LabVIEW clients we can push commands back to (dashboard → LabVIEW).
    private readonly object _clientsGate = new();
    private readonly List<NetworkStream> _clients = [];

    /// <summary>TCP port currently bound (0 when not listening).</summary>
    public int Port { get; private set; }

    /// <summary>True while the TCP listener is bound and the accept loop is running.</summary>
    public bool IsListening { get; private set; }

    /// <summary>UTC time of the last line received (<see cref="DateTime.MinValue"/> = none yet).</summary>
    public DateTime LastReceivedUtc { get; private set; } = DateTime.MinValue;

    /// <summary>Raised on a background thread after every parsed line. Marshal to the UI thread.</summary>
    public event Action<IReadOnlyList<HmiDatum>>? DataReceived;

    /// <summary>
    /// Raised (background thread) when listening starts or stops. <c>error</c> is non-null
    /// only when starting failed (e.g. the port is already in use).
    /// </summary>
    public event Action<bool, string?>? ListeningChanged;

    /// <summary>Raised (background thread) when a LabVIEW client connects (true) or the last one disconnects (false).</summary>
    public event Action<bool>? ClientConnectedChanged;

    /// <summary>True when at least one LabVIEW client is connected (so <see cref="Send"/> has a target).</summary>
    public bool HasClient
    {
        get { lock (_clientsGate) return _clients.Count > 0; }
    }

    /// <summary>
    /// Pushes a single <c>key=value</c> command line (newline-terminated) to every
    /// connected LabVIEW client. No-op when nobody is connected.
    /// </summary>
    public void Send(string key, string value) => WriteToClients($"{key}={value}\n");

    /// <summary>
    /// Pushes one raw line terminated with CRLF (<c>\r\n</c>) to every connected client —
    /// used for the comma-separated control line the VI reads with "TCP Read (CRLF)" +
    /// "Scan From String". No-op when nobody is connected. Safe to call from the UI thread.
    /// </summary>
    public void SendLine(string line) => WriteToClients(line + "\r\n");

    private void WriteToClients(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);

        List<NetworkStream> targets;
        lock (_clientsGate)
        {
            if (_clients.Count == 0) return;
            targets = new List<NetworkStream>(_clients);
        }

        foreach (var stream in targets)
        {
            try
            {
                lock (stream)
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
            catch
            {
                // Write failed → the connection is gone; drop it.
                lock (_clientsGate) _clients.Remove(stream);
            }
        }
    }

    /// <summary>Thread-safe copy of the current values in first-seen key order.</summary>
    public IReadOnlyList<HmiDatum> Snapshot()
    {
        lock (_gate)
            return _order.Select(k => new HmiDatum(k, _values[k])).ToArray();
    }

    /// <summary>
    /// Binds the TCP listener on <paramref name="port"/> and starts accepting clients.
    /// Idempotent: a repeated call with the same port that is already listening is a
    /// no-op, so both HMI views can call it freely on load.
    /// </summary>
    public void Start(int port)
    {
        lock (_gate)
        {
            if (IsListening && Port == port)
                return;

            StopLocked();
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                Port        = port;
                IsListening = true;
                _cts        = new CancellationTokenSource();
                _ = AcceptLoopAsync(_listener, _cts.Token);
            }
            catch (Exception ex)
            {
                _listener   = null;
                Port        = 0;
                IsListening = false;
                ListeningChanged?.Invoke(false, ex.Message);
                return;
            }
        }
        ListeningChanged?.Invoke(true, null);
    }

    public void Stop()
    {
        lock (_gate)
            StopLocked();
        ListeningChanged?.Invoke(false, null);
    }

    private void StopLocked()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;

        try { _listener?.Stop(); } catch { }
        _listener   = null;
        Port        = 0;
        IsListening = false;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException)    { break; }
            catch                              { break; }  // listener stopped / fatal

            // Handle each connection independently; the accept loop keeps running so a
            // client can reconnect without tearing the listener down.
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        NetworkStream? stream = null;
        try
        {
            using (client)
            using (stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                RegisterClient(stream);   // enable dashboard → LabVIEW commands on this link
                while (!ct.IsCancellationRequested)
                {
                    string? line;
                    try { line = await reader.ReadLineAsync(ct); }
                    catch { break; }

                    if (line is null)
                        break;  // client disconnected (end of stream)

                    var parsed = Parse(line);
                    if (parsed.Count == 0)
                        continue;

                    Merge(parsed);
                    LastReceivedUtc = DateTime.UtcNow;
                    DataReceived?.Invoke(Snapshot());
                }
            }
        }
        catch
        {
            // Drop this connection and keep the listener alive for a reconnect.
        }
        finally
        {
            if (stream is not null)
                UnregisterClient(stream);
        }
    }

    private void RegisterClient(NetworkStream stream)
    {
        bool first;
        lock (_clientsGate)
        {
            _clients.Add(stream);
            first = _clients.Count == 1;
        }
        if (first)
            ClientConnectedChanged?.Invoke(true);
    }

    private void UnregisterClient(NetworkStream stream)
    {
        bool last;
        lock (_clientsGate)
        {
            _clients.Remove(stream);
            last = _clients.Count == 0;
        }
        if (last)
            ClientConnectedChanged?.Invoke(false);
    }

    private static List<HmiDatum> Parse(string text)
    {
        var list = new List<HmiDatum>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;

            int eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            if (key.Length == 0)
                continue;

            list.Add(new HmiDatum(key, val));
        }
        return list;
    }

    private void Merge(IReadOnlyList<HmiDatum> parsed)
    {
        lock (_gate)
        {
            foreach (var (key, val) in parsed)
            {
                if (!_values.ContainsKey(key))
                    _order.Add(key);
                _values[key] = val;
            }
        }
    }
}
