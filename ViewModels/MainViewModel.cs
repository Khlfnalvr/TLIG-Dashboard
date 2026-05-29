using CommunityToolkit.Mvvm.ComponentModel;
using TLIGDashboard.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace TLIGDashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherQueue _dispatcherQueue;

    // ── Services ──────────────────────────────────────────────────────────────
    public OpcUaService OpcUa { get; } = new();

    // ── Connection state ──────────────────────────────────────────────────────
    [ObservableProperty] private string _connectionStatus =
        LocalizationManager.Instance.Get("Ui_InitialConnectionHint");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusBarVisibility))]
    private bool _isConnected;

    [ObservableProperty] private string _dataSourceText =
        LocalizationManager.Instance.Get("Ui_SourceNotConnected");

    public Visibility StatusBarVisibility =>
        IsConnected ? Visibility.Collapsed : Visibility.Visible;

    // ── Display units ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _temperatureUnit = "C";
    [ObservableProperty] private string _voltageUnit     = "V";
    [ObservableProperty] private string _capacityUnit    = "mAh";

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainViewModel(DispatcherQueue dispatcherQueue)
    {
        _dispatcherQueue = dispatcherQueue;

        var saved = AppSettingsService.Load();
        TemperatureUnit = saved.TemperatureUnit;
        VoltageUnit     = saved.VoltageUnit;
        CapacityUnit    = saved.CapacityUnit;
        OpcUa.NodeConfig = saved.OpcUaNodeConfig;

        LocalizationManager.Instance.PropertyChanged += (_, _) => RefreshLocalizedText();

        OpcUa.StatusChanged += msg => _dispatcherQueue.TryEnqueue(() => OnOpcUaStatus(msg));
        OpcUa.ErrorOccurred += msg => _dispatcherQueue.TryEnqueue(() =>
            ConnectionStatus = LocalizationManager.Instance.Format("Ui_ErrorWithMessage", msg));
    }

    // ── OPC UA status handler ─────────────────────────────────────────────────
    private void OnOpcUaStatus(string msg)
    {
        ConnectionStatus = msg;
        IsConnected      = OpcUa.IsConnected;

        DataSourceText = OpcUa.IsConnected
            ? LocalizationManager.Instance.Format("Ui_SourceConnected", OpcUa.EndpointUrl, "OPC UA")
            : LocalizationManager.Instance.Get("Ui_SourceNotConnected");
    }

    // ── Localization refresh ──────────────────────────────────────────────────
    public void RefreshLocalizedText()
    {
        if (OpcUa.IsConnected)
        {
            DataSourceText   = LocalizationManager.Instance.Format("Ui_SourceConnected", OpcUa.EndpointUrl, "OPC UA");
            ConnectionStatus = LocalizationManager.Instance.Format("OpcUa_StatusConnected", OpcUa.EndpointUrl);
        }
        else
        {
            DataSourceText   = LocalizationManager.Instance.Get("Ui_SourceNotConnected");
            ConnectionStatus = LocalizationManager.Instance.Get("Ui_InitialConnectionHint");
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
