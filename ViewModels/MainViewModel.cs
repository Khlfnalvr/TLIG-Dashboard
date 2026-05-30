using CommunityToolkit.Mvvm.ComponentModel;
using TLIGDashboard.Services;
using Microsoft.UI.Dispatching;

namespace TLIGDashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue;

    // ── Services ──────────────────────────────────────────────────────────────
    public OpcUaService OpcUa { get; } = new();

    // ── OPC UA connection state (server uses this for the InfoBar) ─────────────
    [ObservableProperty] private string _connectionStatus =
        LocalizationManager.Instance.Get(
            BuildInfo.IsServer ? "Ui_ServerOpcHint" : "Ui_ClientNotConnectedHint");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarIsOpen))]
    private bool _isConnected;

    [ObservableProperty] private string _dataSourceText =
        LocalizationManager.Instance.Get(
            BuildInfo.IsServer ? "Ui_ServerOpcNotConnected" : "Ui_ClientNotConnected");

    // ── Share-server connection state (client uses this for the InfoBar) ────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusInfoBarIsOpen))]
    private bool _isServerConnected;

    // Server → warn when OPC UA is disconnected.
    // Client → warn when not connected to the TLIG Dashboard Server.
    public bool StatusInfoBarIsOpen =>
        BuildInfo.IsServer ? !IsConnected : !IsServerConnected;

    // ── Display units ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _temperatureUnit = "C";
    [ObservableProperty] private string _voltageUnit     = "V";
    [ObservableProperty] private string _capacityUnit    = "mAh";

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;

        var saved = AppSettingsService.Load();
        TemperatureUnit  = saved.TemperatureUnit;
        VoltageUnit      = saved.VoltageUnit;
        CapacityUnit     = saved.CapacityUnit;
        OpcUa.NodeConfig = saved.OpcUaNodeConfig;

        LocalizationManager.Instance.PropertyChanged += (_, _) => RefreshLocalizedText();

        // OPC UA events (used on both flavors, but only drives the InfoBar on server).
        OpcUa.StatusChanged += msg => _dispatcherQueue.TryEnqueue(() => OnOpcUaStatus(msg));
        OpcUa.ErrorOccurred += msg => _dispatcherQueue.TryEnqueue(() =>
            ConnectionStatus = LocalizationManager.Instance.Format("Ui_ErrorWithMessage", msg));

        // ShareClient events (drives the InfoBar on the client).
        if (BuildInfo.IsClient)
        {
            ShareClient.Instance.ConnectionChanged += (ok, info) =>
                _dispatcherQueue.TryEnqueue(() => OnShareClientStatus(ok, info));
        }
    }

    // ── OPC UA status handler ─────────────────────────────────────────────────
    private void OnOpcUaStatus(string msg)
    {
        ConnectionStatus = msg;
        IsConnected      = OpcUa.IsConnected;

        if (BuildInfo.IsServer)
        {
            DataSourceText = OpcUa.IsConnected
                ? LocalizationManager.Instance.Format("Ui_SourceConnected", OpcUa.EndpointUrl, "OPC UA")
                : LocalizationManager.Instance.Get("Ui_ServerOpcNotConnected");
        }
    }

    // ── Share-server status handler (client only) ─────────────────────────────
    private void OnShareClientStatus(bool connected, string info)
    {
        IsServerConnected = connected;
        DataSourceText = connected
            ? LocalizationManager.Instance.Format("Ui_ClientConnected", info)
            : LocalizationManager.Instance.Get("Ui_ClientNotConnected");
        ConnectionStatus = connected
            ? LocalizationManager.Instance.Format("Ui_ClientConnected", info)
            : LocalizationManager.Instance.Get("Ui_ClientNotConnectedHint");
    }

    // ── Localization refresh ──────────────────────────────────────────────────
    public void RefreshLocalizedText()
    {
        if (BuildInfo.IsServer)
        {
            if (OpcUa.IsConnected)
            {
                DataSourceText   = LocalizationManager.Instance.Format("Ui_SourceConnected", OpcUa.EndpointUrl, "OPC UA");
                ConnectionStatus = LocalizationManager.Instance.Format("OpcUa_StatusConnected", OpcUa.EndpointUrl);
            }
            else
            {
                DataSourceText   = LocalizationManager.Instance.Get("Ui_ServerOpcNotConnected");
                ConnectionStatus = LocalizationManager.Instance.Get("Ui_ServerOpcHint");
            }
        }
        else
        {
            if (IsServerConnected)
            {
                var host = AppSettingsService.Load().ServerHost;
                DataSourceText   = LocalizationManager.Instance.Format("Ui_ClientConnected", host);
                ConnectionStatus = LocalizationManager.Instance.Format("Ui_ClientConnected", host);
            }
            else
            {
                DataSourceText   = LocalizationManager.Instance.Get("Ui_ClientNotConnected");
                ConnectionStatus = LocalizationManager.Instance.Get("Ui_ClientNotConnectedHint");
            }
        }
    }

    // ── Unit setters (persist on change) ─────────────────────────────────────
    public void SetTemperatureUnit(string unit)
    {
        if (TemperatureUnit == unit) return;
        TemperatureUnit = unit;
        SaveSettings();
    }

    public void SetVoltageUnit(string unit)
    {
        if (VoltageUnit == unit) return;
        VoltageUnit = unit;
        SaveSettings();
    }

    public void SetCapacityUnit(string unit)
    {
        if (CapacityUnit == unit) return;
        CapacityUnit = unit;
        SaveSettings();
    }

    // ── Settings persistence ──────────────────────────────────────────────────
    public void SaveSettings()
    {
        var s = AppSettingsService.Load();
        s.OpcUaEndpointUrl = OpcUa.EndpointUrl;
        s.OpcUaNodeConfig  = OpcUa.NodeConfig;
        s.TemperatureUnit  = TemperatureUnit;
        s.VoltageUnit      = VoltageUnit;
        s.CapacityUnit     = CapacityUnit;
        s.Language         = LocalizationManager.Instance.CurrentLanguage;
        AppSettingsService.Save(s);
    }
}
