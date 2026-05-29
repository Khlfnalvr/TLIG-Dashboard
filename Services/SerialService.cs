using System.IO.Ports;
using System.Text;
using System.Text.Json;
using TLIGDashboard.Models;
using TLIGDashboard.Services.Transports;

namespace TLIGDashboard.Services;

/// <summary>One pickable COM port (USB-CDC from the ESP32).</summary>
public record SerialPortInfo(string PortName, string DisplayName);

/// <summary>One pickable UART baud rate.</summary>
public record SerialBaud(int Baud, string DisplayName);

/// <summary>
/// USB-serial link to the ESP32 master of the BMS. The firmware emits one
/// JSON snapshot per line over its USB-CDC port; this service opens the COM
/// port, runs a read loop, parses each line, and raises <see cref="DataReceived"/>.
///
/// Wire format (one JSON object per line, terminated by \n):
///
///   {"v":53.12,"i":-2.5,"soc":78,"st":"discharging",
///    "cells":[3.682, …20 values…],
///    "temps":[28,   …10 values…],
///    "bal":[0,5,12]}
///
/// Fields missing from a line keep their previous value (the service merges
/// into the last known snapshot). Bad JSON increments <see cref="ParseErrors"/>
/// and is dropped silently after a 5 s cooldown to avoid log spam.
/// </summary>
public class SerialService : IDisposable
{
    // ── Public events ─────────────────────────────────────────────────────
    public event Action<BmsData>? DataReceived;
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;

    // ── Live state ────────────────────────────────────────────────────────
    public bool   IsConnected     => _port is { IsOpen: true };
    public string Channel         => _portName ?? "";
    public int    Bitrate         { get; private set; }
    public string BitrateText     => $"{Bitrate} baud";
    public string ChannelName     { get; private set; } = "";
    public int    FramesReceived  { get; private set; }
    public int    ParseErrors     { get; private set; }

    public int  DefaultBitrate    => 115200;
    public bool IsDriverAvailable => TrySerialEnumerate();

    // ── Pickers ───────────────────────────────────────────────────────────
    public SerialPortInfo[] Channels => SerialPortHelper.EnumerateComChannels();

    public SerialBaud[] Bitrates =>
    [
        new(921600, "921 600 baud"),
        new(460800, "460 800 baud"),
        new(230400, "230 400 baud"),
        new(115200, "115 200 baud"),
        new( 57600,  "57 600 baud"),
        new( 38400,  "38 400 baud"),
        new( 19200,  "19 200 baud"),
        new(  9600,   "9 600 baud"),
    ];

    // ── Internal state ────────────────────────────────────────────────────
    private SerialPort? _port;
    private string?     _portName;
    private CancellationTokenSource? _cts;
    private Task?       _readTask;

    private readonly StringBuilder _lineBuf = new(512);
    private readonly object _stateLock = new();
    private BmsData _last = new();
    private DateTime _lastParseErrorFired = DateTime.MinValue;
    private static readonly TimeSpan ParseErrorCooldown = TimeSpan.FromSeconds(5);

    // ── Probe: short-lived open just to verify a port is the BMS ─────────
    public bool Probe(SerialPortInfo channel, SerialBaud bitrate, int timeoutMs)
    {
        SerialPort? sp = null;
        try
        {
            sp = new SerialPort(channel.PortName, bitrate.Baud)
            {
                ReadTimeout  = 250,
                WriteTimeout = 250,
                NewLine      = "\n",
                Encoding     = Encoding.UTF8,
            };
            sp.Open();
        }
        catch { try { sp?.Dispose(); } catch { } return false; }

        try
        {
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            var sb = new StringBuilder(256);
            while (DateTime.Now < deadline)
            {
                int ch;
                try { ch = sp.ReadByte(); }
                catch (TimeoutException) { continue; }
                catch { return false; }

                if (ch < 0) continue;
                if (ch == '\n')
                {
                    var line = sb.ToString().Trim();
                    sb.Clear();
                    if (TryParseLine(line, out _)) return true;
                }
                else if (ch != '\r')
                {
                    sb.Append((char)ch);
                    if (sb.Length > 4096) sb.Clear();
                }
            }
            return false;
        }
        finally
        {
            try { sp.Close(); } catch { }
            try { sp.Dispose(); } catch { }
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public bool Connect(SerialPortInfo channel, SerialBaud bitrate)
    {
        if (IsConnected) Disconnect();

        try
        {
            _port = new SerialPort(channel.PortName, bitrate.Baud)
            {
                ReadTimeout  = 500,
                WriteTimeout = 500,
                NewLine      = "\n",
                Encoding     = Encoding.UTF8,
                DtrEnable    = true,
                RtsEnable    = true,
            };
            _port.Open();
        }
        catch (UnauthorizedAccessException)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Serial_PortInUse", channel.PortName));
            _port?.Dispose(); _port = null;
            return false;
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Serial_OpenFailed", channel.PortName, ex.Message));
            _port?.Dispose(); _port = null;
            return false;
        }

        _portName       = channel.PortName;
        Bitrate         = bitrate.Baud;
        ChannelName     = channel.DisplayName;
        FramesReceived  = 0;
        ParseErrors     = 0;
        _lineBuf.Clear();
        lock (_stateLock) _last = new BmsData();

        _cts      = new CancellationTokenSource();
        _readTask = Task.Run(() => ReadLoop(_cts.Token));

        StatusChanged?.Invoke(LocalizationManager.Instance.Format(
            "Serial_StatusConnected",
            channel.DisplayName,
            bitrate.Baud));
        return true;
    }

    public void Disconnect()
    {
        try { _cts?.Cancel(); } catch { }
        try { _readTask?.Wait(1500); } catch { }
        if (_port is not null)
        {
            try { if (_port.IsOpen) _port.Close(); } catch { }
            try { _port.Dispose(); } catch { }
        }
        _port      = null;
        _portName  = null;
        _cts?.Dispose();
        _cts       = null;
        _readTask  = null;
        StatusChanged?.Invoke(LocalizationManager.Instance.Get("Serial_StatusDisconnected"));
    }

    public void Dispose() => Disconnect();

    // ── Read loop ─────────────────────────────────────────────────────────
    private void ReadLoop(CancellationToken ct)
    {
        var sp = _port;
        if (sp is null) return;
        var buf = new byte[1024];

        while (!ct.IsCancellationRequested && sp.IsOpen)
        {
            int n;
            try { n = sp.Read(buf, 0, buf.Length); }
            catch (TimeoutException) { continue; }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Serial_ReadError", ex.Message));
                StatusChanged?.Invoke(LocalizationManager.Instance.Get("Serial_StatusDisconnected"));
                break;
            }
            catch { break; }

            if (n <= 0) { Thread.Sleep(2); continue; }

            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b == (byte)'\n')
                {
                    var line = _lineBuf.ToString().Trim();
                    _lineBuf.Clear();
                    if (line.Length > 0) HandleLine(line);
                }
                else if (b != (byte)'\r')
                {
                    _lineBuf.Append((char)b);
                    if (_lineBuf.Length > 8192) _lineBuf.Clear();
                }
            }
        }
    }

    private void HandleLine(string line)
    {
        if (!TryParseLine(line, out var snap))
        {
            ParseErrors++;
            var now = DateTime.Now;
            if (now - _lastParseErrorFired > ParseErrorCooldown)
            {
                _lastParseErrorFired = now;
                string preview = line.Length > 60 ? line[..60] + "…" : line;
                ErrorOccurred?.Invoke(LocalizationManager.Instance.Format(
                    "Serial_ParseError",
                    ParseErrors,
                    preview));
            }
            return;
        }

        lock (_stateLock)
        {
            MergeInto(_last, snap);
            FramesReceived++;
            DataReceived?.Invoke(Clone(_last));
        }
    }

    public static bool TryParseLine(string line, out BmsData snap)
    {
        snap = new BmsData();
        if (string.IsNullOrWhiteSpace(line)) return false;
        var s = line.AsSpan().Trim();
        if (s.Length == 0 || s[0] != '{') return false;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (root.TryGetProperty("v",   out var v)   && v.TryGetDouble(out var vv))   snap.PackVoltage = vv;
            if (root.TryGetProperty("i",   out var i)   && i.TryGetDouble(out var iv))   snap.Current     = iv;
            if (root.TryGetProperty("soc", out var soc) && soc.TryGetDouble(out var sv)) snap.Soc         = sv;
            if (root.TryGetProperty("st",  out var st)  && st.ValueKind == JsonValueKind.String)
                snap.Status = st.GetString() ?? "idle";

            if (root.TryGetProperty("cells", out var cells) && cells.ValueKind == JsonValueKind.Array)
            {
                int len = Math.Min(cells.GetArrayLength(), 20);
                for (int k = 0; k < len; k++) if (cells[k].TryGetDouble(out var cv)) snap.Cells[k] = cv;
            }
            if (root.TryGetProperty("temps", out var temps) && temps.ValueKind == JsonValueKind.Array)
            {
                int len = Math.Min(temps.GetArrayLength(), 10);
                for (int k = 0; k < len; k++) if (temps[k].TryGetDouble(out var tv)) snap.Temps[k] = tv;
            }
            if (root.TryGetProperty("bal", out var bal))
            {
                if (bal.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in bal.EnumerateArray())
                        if (item.TryGetInt32(out var idx) && idx >= 0 && idx < 20)
                            snap.Balancing[idx] = true;
                }
                else if (bal.ValueKind == JsonValueKind.Number && bal.TryGetUInt32(out var bits))
                {
                    for (int k = 0; k < 20; k++) snap.Balancing[k] = ((bits >> k) & 1u) == 1u;
                }
            }
            return true;
        }
        catch { return false; }
    }

    private static void MergeInto(BmsData dst, BmsData src)
    {
        dst.PackVoltage = src.PackVoltage;
        dst.Current     = src.Current;
        dst.Soc         = src.Soc;
        dst.Status      = src.Status;
        for (int k = 0; k < 20; k++) if (src.Cells[k] != 0) dst.Cells[k] = src.Cells[k];
        for (int k = 0; k < 10; k++) if (src.Temps[k] != 0) dst.Temps[k] = src.Temps[k];
        for (int k = 0; k < 20; k++) dst.Balancing[k] = src.Balancing[k];
    }

    private static BmsData Clone(BmsData s)
    {
        var c = new BmsData
        {
            PackVoltage = s.PackVoltage,
            Current     = s.Current,
            Soc         = s.Soc,
            Status      = s.Status,
        };
        Array.Copy(s.Cells,     c.Cells,     20);
        Array.Copy(s.Temps,     c.Temps,     10);
        Array.Copy(s.Balancing, c.Balancing, 20);
        return c;
    }

    private static bool TrySerialEnumerate()
    {
        try { _ = SerialPort.GetPortNames(); return true; }
        catch { return false; }
    }
}
