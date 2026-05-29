using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TLIGDashboard.Services;

/// <summary>
/// Always-on auto-connect for the ESP serial link. Enumerates available COM
/// ports via the live service (<see cref="SerialService.Channels"/>), probes
/// each at the user-chosen baud rate, and holds the first one that returns
/// a valid BMS JSON line.
///
///   • <b>Suspended</b> — set when the user clicks Disconnect; scanning halts
///     until <see cref="ResumeReconnect"/> is called.
///   • <b>Unexpected drop</b> — service reports disconnect; scanning resumes
///     automatically (no suspension).
/// </summary>
public sealed class AutoConnectService : IDisposable
{
    private readonly SerialService _serial;
    private readonly object        _lock          = new();
    private Timer?                 _timer;
    private HashSet<string>        _failedPorts   = new(StringComparer.OrdinalIgnoreCase);
    private DateTime               _lastFailClear = DateTime.MinValue;
    private bool                   _suspended     = false;
    private int                    _polling       = 0;   // concurrent-call guard

    /// <summary>The user-chosen baud rate. Used as the lookup key
    /// into <see cref="SerialService.Bitrates"/>.</summary>
    public int  Baud           { get; set; }
    public bool IsSuspended    { get { lock (_lock) return _suspended; } }
    public bool IsEnabled      => !IsSuspended;

    private int _reconnectIntervalSec = 2;
    public int ReconnectIntervalSec
    {
        get { lock (_lock) return _reconnectIntervalSec; }
        set
        {
            int v = Math.Clamp(value, 1, 60);
            lock (_lock)
            {
                if (_reconnectIntervalSec == v) return;
                _reconnectIntervalSec = v;
                _timer?.Change(TimeSpan.FromMilliseconds(500),
                               TimeSpan.FromSeconds(v));
            }
        }
    }
    public int ProbeTimeoutMs { get; set; } = 3000;

    public event Action<string>? Notification;

    public AutoConnectService(SerialService serial)
    {
        _serial = serial;
        Baud    = serial.DefaultBitrate;
    }

    public void Start(int baud)
    {
        lock (_lock)
        {
            Baud           = baud;
            _failedPorts.Clear();
            _suspended     = false;
            _lastFailClear = DateTime.Now;

            _timer?.Dispose();
            _timer = new Timer(Poll, null,
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromSeconds(_reconnectIntervalSec));
        }
    }

    public void SuspendReconnect()
    {
        lock (_lock) { _suspended = true; }
        Notification?.Invoke(LocalizationManager.Instance.Get("AutoConnect_Suspended"));
    }

    public void ResumeReconnect()
    {
        lock (_lock)
        {
            _suspended = false;
            _failedPorts.Clear();
        }
    }

    public void Dispose()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        t?.Dispose();
    }

    private void Poll(object? _)
    {
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        try   { DoPoll(); }
        finally { Interlocked.Exchange(ref _polling, 0); }
    }

    private void DoPoll()
    {
        SerialPortInfo? candidate = null;
        SerialBaud?     bitrate   = null;
        int baud;

        lock (_lock)
        {
            baud = Baud;

            if (_serial.IsConnected) return;
            if (_suspended)          return;

            // Periodically retry ports that failed earlier (USB re-enumeration,
            // device replugged, etc.).
            if ((DateTime.Now - _lastFailClear).TotalSeconds > 30)
            {
                _failedPorts.Clear();
                _lastFailClear = DateTime.Now;
            }

            bitrate = _serial.Bitrates.FirstOrDefault(b => b.Baud == baud)
                   ?? _serial.Bitrates.FirstOrDefault(b => b.Baud == _serial.DefaultBitrate)
                   ?? _serial.Bitrates.FirstOrDefault();
            if (bitrate is null) return;

            foreach (var ch in _serial.Channels)
            {
                if (_failedPorts.Contains(ch.PortName)) continue;
                candidate = ch;
                break;
            }
        }

        if (candidate is null || _serial.IsConnected) return;

        Notification?.Invoke(LocalizationManager.Instance.Format(
            "AutoConnect_Probing",
            candidate.DisplayName,
            bitrate.DisplayName));
        bool verified;
        try { verified = _serial.Probe(candidate, bitrate, timeoutMs: ProbeTimeoutMs); }
        catch { verified = false; }

        lock (_lock)
        {
            if (_suspended) return;
            if (!verified)
            {
                _failedPorts.Add(candidate.PortName);
                Notification?.Invoke(LocalizationManager.Instance.Format(
                    "AutoConnect_NoData",
                    candidate.DisplayName));
                return;
            }
        }

        Notification?.Invoke(LocalizationManager.Instance.Format(
            "AutoConnect_Verified",
            candidate.DisplayName));
        bool ok = _serial.Connect(candidate, bitrate);

        if (!ok)
        {
            lock (_lock) { _failedPorts.Add(candidate.PortName); }
            Notification?.Invoke(LocalizationManager.Instance.Format(
                "AutoConnect_ConnectFailed",
                candidate.DisplayName));
        }
    }
}
