using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TLIGDashboard.Services;

/// <summary>
/// Singleton that tracks live connection status of the four subsystems shown in
/// the "Status System" panel (PLC, Camera, Sensor, AI Assistant).
///
/// Every flag defaults to <c>false</c> (disconnected → red) and only turns green
/// when the corresponding subsystem reports a real connection:
///   • PLC / Sensor — driven by the OPC UA session (<see cref="OpcUaService.IsConnected"/>).
///   • AI Assistant — driven by whether an API key is configured.
///   • Camera       — no integration yet, so it stays offline until one is wired in.
///
/// Bindings use {x:Bind Status.PlcBrush, Mode=OneWay} etc.; changing a flag raises
/// PropertyChanged for its brush + text so the dot colour and label refresh live.
/// </summary>
public sealed class SystemStatusService : INotifyPropertyChanged
{
    public static SystemStatusService Instance { get; } = new();

    private SystemStatusService()
    {
        // Re-emit the Online/Offline · Active/Inactive labels when the UI language changes.
        LocalizationManager.Instance.PropertyChanged += (_, _) => RefreshLabels();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    // ── Backing flags (all start disconnected) ──────────────────────────────
    private bool _plc;
    private bool _camera;
    private bool _sensor;
    private bool _ai;

    public bool PlcConnected
    {
        get => _plc;
        set { if (_plc != value) { _plc = value; Raise(nameof(PlcConnected), nameof(PlcText)); } }
    }

    public bool CameraConnected
    {
        get => _camera;
        set { if (_camera != value) { _camera = value; Raise(nameof(CameraConnected), nameof(CameraText)); } }
    }

    public bool SensorConnected
    {
        get => _sensor;
        set { if (_sensor != value) { _sensor = value; Raise(nameof(SensorConnected), nameof(SensorText)); } }
    }

    public bool AiConnected
    {
        get => _ai;
        set { if (_ai != value) { _ai = value; Raise(nameof(AiConnected), nameof(AiText)); } }
    }

    // ── Indicator brushes ───────────────────────────────────────────────────
    // ── Status labels ───────────────────────────────────────────────────────
    // PLC & Camera report "Online / Offline"; Sensor & AI report "Active / Inactive".
    public string PlcText    => OnlineText(_plc);
    public string CameraText => OnlineText(_camera);
    public string SensorText => ActiveText(_sensor);
    public string AiText     => ActiveText(_ai);

    // ── Helpers ─────────────────────────────────────────────────────────────
    private static Brush BrushFor(bool connected)
    {
        var key = connected ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush";
        if (Application.Current?.Resources.TryGetValue(key, out var res) == true && res is Brush b)
            return b;
        // Fallback — should never hit since both keys are defined in App.xaml.
        return new SolidColorBrush(connected
            ? Windows.UI.Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F)
            : Windows.UI.Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));
    }

    private static string OnlineText(bool connected) =>
        LocalizationManager.Instance.Get(connected ? "Sys_Online" : "Sys_Offline");

    private static string ActiveText(bool connected) =>
        LocalizationManager.Instance.Get(connected ? "Sys_Active" : "Sys_Inactive");

    /// <summary>Re-emit all text properties (e.g. after a language change).</summary>
    public void RefreshLabels() =>
        Raise(nameof(PlcText), nameof(CameraText), nameof(SensorText), nameof(AiText));

    private void Raise(params string[] names)
    {
        foreach (var n in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
