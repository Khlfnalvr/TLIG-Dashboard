using System.Collections.ObjectModel;
using System.Text;
using TLIGDashboard.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace TLIGDashboard.Services;

/// <summary>One pickable BLE device.</summary>
public record BluetoothDeviceInfo(string DeviceId, string DisplayName)
{
    public override string ToString() => DisplayName;
}

/// <summary>
/// Bluetooth Low-Energy link to an ESP32 master running the Nordic UART
/// Service (NUS). The firmware writes one JSON snapshot per line to its
/// TX characteristic; this service subscribes, accumulates bytes, splits
/// on newline, parses each line through <see cref="SerialService.TryParseLine"/>,
/// and raises <see cref="DataReceived"/>.
///
/// Wire format (one JSON object per BLE write, terminated by \n):
///
///   {"v":53.12,"i":-2.5,"soc":78,"st":"discharging",
///    "cells":[3.682, …20 values…],
///    "temps":[28,   …10 values…],
///    "bal":[0,5,12]}
///
/// Frames are merged into the last known snapshot — missing fields keep
/// their previous value. Bad JSON increments <see cref="ParseErrors"/>
/// and is dropped silently after a 5 s cooldown to avoid log spam.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    // ── Nordic UART Service UUIDs (de-facto ESP32 BLE UART) ────────────────
    private static readonly Guid NusServiceUuid = new("6E400001-B5A3-F393-E0A9-E50E24DCCA9E");
    private static readonly Guid NusTxCharUuid  = new("6E400003-B5A3-F393-E0A9-E50E24DCCA9E"); // notify (device -> host)

    // ── Public events ─────────────────────────────────────────────────────
    public event Action<BmsData>? DataReceived;
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;
    public event Action?          DevicesChanged;

    // ── Live state ────────────────────────────────────────────────────────
    public bool   IsConnected     => _device is not null && _notifyChar is not null;
    public string DeviceId        => _deviceId ?? "";
    public string DeviceName      { get; private set; } = "";
    public int    FramesReceived  { get; private set; }
    public int    ParseErrors     { get; private set; }
    public bool   IsScanning      { get; private set; }

    /// <summary>Devices discovered by the current/last scan. UI-bindable.</summary>
    public ObservableCollection<BluetoothDeviceInfo> Devices { get; } = new();

    // ── Internal state ────────────────────────────────────────────────────
    private DeviceWatcher?               _watcher;
    private BluetoothLEDevice?           _device;
    private GattCharacteristic?          _notifyChar;
    private string?                      _deviceId;
    private readonly StringBuilder       _lineBuf = new(512);
    private readonly object              _stateLock = new();
    private BmsData                      _last = new();
    private DateTime                     _lastParseErrorFired = DateTime.MinValue;
    private static readonly TimeSpan ParseErrorCooldown = TimeSpan.FromSeconds(5);

    // ── Discovery ─────────────────────────────────────────────────────────
    /// <summary>Start a BLE device watcher. Devices stream into <see cref="Devices"/>.</summary>
    public void StartScan()
    {
        StopScan();
        Devices.Clear();

        try
        {
            // Aqs string for BLE-capable devices.
            string aqs = BluetoothLEDevice.GetDeviceSelectorFromPairingState(false) + " OR " +
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);

            _watcher = DeviceInformation.CreateWatcher(
                aqs,
                new[] { "System.Devices.Aep.IsConnected", "System.Devices.Aep.Bluetooth.Le.IsConnectable" },
                DeviceInformationKind.AssociationEndpoint);

            _watcher.Added   += Watcher_Added;
            _watcher.Updated += Watcher_Updated;
            _watcher.Removed += Watcher_Removed;
            _watcher.EnumerationCompleted += (_, _) => IsScanning = false;
            _watcher.Stopped += (_, _) => IsScanning = false;

            IsScanning = true;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            IsScanning = false;
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_ScanError", ex.Message));
        }
    }

    public void StopScan()
    {
        if (_watcher is null) return;
        try
        {
            if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                _watcher.Stop();
        }
        catch { }
        _watcher = null;
        IsScanning = false;
    }

    private void Watcher_Added(DeviceWatcher sender, DeviceInformation info)
    {
        // Ignore unnamed devices — practically these are noise. The ESP32
        // firmware advertises a readable name (e.g. "BMS-ESP32").
        var name = (info.Name ?? "").Trim();
        if (name.Length == 0) return;

        var entry = new BluetoothDeviceInfo(info.Id, name);
        // Marshal to whoever subscribed; collection itself is thread-safe enough
        // for our usage (single watcher thread + UI reads via DevicesChanged).
        if (!ContainsId(info.Id))
        {
            Devices.Add(entry);
            DevicesChanged?.Invoke();
        }
    }

    private void Watcher_Updated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        // Names can arrive after the initial Added event for some advertisers.
        if (update.Properties.TryGetValue("System.ItemNameDisplay", out var raw) &&
            raw is string newName && newName.Length > 0)
        {
            for (int i = 0; i < Devices.Count; i++)
            {
                if (Devices[i].DeviceId == update.Id && Devices[i].DisplayName != newName)
                {
                    Devices[i] = new BluetoothDeviceInfo(update.Id, newName);
                    DevicesChanged?.Invoke();
                    return;
                }
            }
        }
    }

    private void Watcher_Removed(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        for (int i = 0; i < Devices.Count; i++)
        {
            if (Devices[i].DeviceId == update.Id)
            {
                Devices.RemoveAt(i);
                DevicesChanged?.Invoke();
                return;
            }
        }
    }

    private bool ContainsId(string id)
    {
        for (int i = 0; i < Devices.Count; i++)
            if (Devices[i].DeviceId == id) return true;
        return false;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public async Task<bool> ConnectAsync(BluetoothDeviceInfo target)
    {
        if (IsConnected) Disconnect();

        try
        {
            _device = await BluetoothLEDevice.FromIdAsync(target.DeviceId);
            if (_device is null)
            {
                ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_OpenFailed", target.DisplayName, "device not found"));
                return false;
            }
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_OpenFailed", target.DisplayName, ex.Message));
            CleanupDevice();
            return false;
        }

        // Locate the Nordic UART service. We use Uncached to force a fresh
        // GATT read in case the host has stale data from a previous session.
        GattDeviceServicesResult svcResult;
        try
        {
            svcResult = await _device.GetGattServicesForUuidAsync(NusServiceUuid, BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_OpenFailed", target.DisplayName, ex.Message));
            CleanupDevice();
            return false;
        }

        if (svcResult.Status != GattCommunicationStatus.Success || svcResult.Services.Count == 0)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format(
                "Bt_NoNusService", target.DisplayName));
            CleanupDevice();
            return false;
        }

        var service = svcResult.Services[0];
        GattCharacteristicsResult chResult;
        try
        {
            chResult = await service.GetCharacteristicsForUuidAsync(NusTxCharUuid, BluetoothCacheMode.Uncached);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_OpenFailed", target.DisplayName, ex.Message));
            CleanupDevice();
            return false;
        }

        if (chResult.Status != GattCommunicationStatus.Success || chResult.Characteristics.Count == 0)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format(
                "Bt_NoTxCharacteristic", target.DisplayName));
            CleanupDevice();
            return false;
        }

        _notifyChar = chResult.Characteristics[0];

        // Subscribe to indications/notifications. Most ESP32 NUS implementations
        // use Notify; fall back to Indicate if that's all the peer offers.
        var descriptor = _notifyChar.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify)
            ? GattClientCharacteristicConfigurationDescriptorValue.Notify
            : GattClientCharacteristicConfigurationDescriptorValue.Indicate;

        GattCommunicationStatus subscribeStatus;
        try
        {
            subscribeStatus = await _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(descriptor);
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_OpenFailed", target.DisplayName, ex.Message));
            CleanupDevice();
            return false;
        }

        if (subscribeStatus != GattCommunicationStatus.Success)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format(
                "Bt_SubscribeFailed", target.DisplayName));
            CleanupDevice();
            return false;
        }

        _notifyChar.ValueChanged += NotifyChar_ValueChanged;

        _deviceId       = target.DeviceId;
        DeviceName      = target.DisplayName;
        FramesReceived  = 0;
        ParseErrors     = 0;
        _lineBuf.Clear();
        lock (_stateLock) _last = new BmsData();

        StatusChanged?.Invoke(LocalizationManager.Instance.Format(
            "Bt_StatusConnected", target.DisplayName));
        return true;
    }

    public void Disconnect()
    {
        if (_notifyChar is not null)
        {
            try { _notifyChar.ValueChanged -= NotifyChar_ValueChanged; } catch { }
            try
            {
                // Best-effort unsubscribe; ignore failures (device may already be gone).
                _ = _notifyChar.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.None);
            }
            catch { }
        }
        CleanupDevice();
        StatusChanged?.Invoke(LocalizationManager.Instance.Get("Bt_StatusDisconnected"));
    }

    private void CleanupDevice()
    {
        _notifyChar = null;
        try { _device?.Dispose(); } catch { }
        _device     = null;
        _deviceId   = null;
        DeviceName  = "";
    }

    public void Dispose()
    {
        StopScan();
        Disconnect();
    }

    // ── Read pipeline ─────────────────────────────────────────────────────
    private void NotifyChar_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[args.CharacteristicValue.Length];
            reader.ReadBytes(bytes);

            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
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
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(LocalizationManager.Instance.Format("Bt_ReadError", ex.Message));
        }
    }

    private void HandleLine(string line)
    {
        if (!SerialService.TryParseLine(line, out var snap))
        {
            ParseErrors++;
            var now = DateTime.Now;
            if (now - _lastParseErrorFired > ParseErrorCooldown)
            {
                _lastParseErrorFired = now;
                string preview = line.Length > 60 ? line[..60] + "…" : line;
                ErrorOccurred?.Invoke(LocalizationManager.Instance.Format(
                    "Bt_ParseError", ParseErrors, preview));
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
}
