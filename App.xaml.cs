using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;
using TLIGDashboard.ViewModels;

namespace TLIGDashboard;

public partial class App : Application
{
    public static MainWindow? CurrentWindow { get; private set; }
    public static MainViewModel ViewModel { get; private set; } = null!;
    public static LocalizationManager Lang { get; } = LocalizationManager.Instance;

    /// <summary>
    /// Shared AI service — single instance used by both AIPage and DashboardPage
    /// so conversation history persists across navigation.
    /// </summary>
    public static Services.AiService Ai { get; } = new();

    /// <summary>
    /// Shared live connection status for the "Status System" panel.
    /// Defaults to all-disconnected (red); updated as subsystems connect.
    /// </summary>
    public static Services.SystemStatusService Status { get; } = Services.SystemStatusService.Instance;

    /// <summary>
    /// Identity + role of the currently signed-in user. Drives the two client
    /// access levels (staff can edit learning analytics; students are read-only).
    /// </summary>
    public static Services.SessionService Session { get; } = Services.SessionService.Instance;

    /// <summary>
    /// Active simulation type (Flow / Level / Temperature). All HMI pages
    /// subscribe to <see cref="Services.SimulationTypeService.SimulationTypeChanged"/>
    /// so they update their labels and units whenever the user switches process.
    /// </summary>
    public static Services.SimulationTypeService SimType { get; } = Services.SimulationTypeService.Instance;

    /// <summary>Latest PID step-response metrics reported by the PLC over TCP.</summary>
    public static Services.PidMetricsService PidMetrics { get; } = Services.PidMetricsService.Instance;

    /// <summary>
    /// Bridges the HMI's PID controls to the external Python client (PIDtest.py):
    /// mirrors Kp/Ki/Kd/Setpoint into a file the script reads live, and launches /
    /// stops the script from the RUN / STOP buttons.
    /// </summary>
    public static Services.PythonBridgeService PythonBridge { get; } = Services.PythonBridgeService.Instance;

    /// <summary>
    /// Shared Smart PID Designer state (gains, setpoint, last run). The Dashboard's
    /// System Model panel and the Parameter page are two views of one designer, so a
    /// RUN on either is reflected on the other.
    /// </summary>
    public static Services.ControlEngineering.PidSessionService PidSession { get; }
        = Services.ControlEngineering.PidSessionService.Instance;

    /// <summary>
    /// Shared Cascade Control designer state (outer temperature PID + inner flow PI).
    /// Backs the dedicated Cascade page the same way <see cref="PidSession"/> backs the
    /// single-loop designer.
    /// </summary>
    public static Services.ControlEngineering.CascadeSessionService CascadeSession { get; }
        = Services.ControlEngineering.CascadeSessionService.Instance;

    /// <summary>
    /// Antrian giliran memakai plant HE — hanya satu orang boleh mengendalikan
    /// plant pada satu waktu, dan giliran dibagikan menurut prioritas
    /// Admin → Dosen/Asisten → Mahasiswa. Databasenya milik Server; Client
    /// meminta giliran ke Server, tidak punya file antrian sendiri.
    /// </summary>
    public static Services.HeQueueRepository HeQueue { get; } = Services.HeQueueRepository.Instance;

    /// <summary>
    /// Cache hasil percobaan parameter HE. Kombinasi SP/Kc/Ti/Td/Pump yang sudah
    /// pernah dijalankan hasilnya diambil dari sini, jadi plant tidak perlu
    /// dijalankan ulang hanya untuk mendapatkan angka yang sama.
    /// </summary>
    public static Services.HeParameterCacheRepository HeParamCache { get; } = Services.HeParameterCacheRepository.Instance;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            InitializeHeDatabases();

            CurrentWindow = new MainWindow();
            ViewModel = CurrentWindow.ViewModel;
            CurrentWindow.Activate();
            CurrentWindow.MaximizeOnLaunch();
        }
        catch (Exception ex)
        {
            ShowFatalError(ex);
        }
    }

    /// <summary>
    /// Membuat/memutakhirkan database HE (antrian giliran + cache parameter) di
    /// sisi Server. Berjalan di latar belakang supaya jendela tidak menunggu
    /// disk, dan kegagalannya tidak mematikan aplikasi: tanpa database, fitur
    /// antrian dan cache saja yang tidak aktif — dashboard tetap bisa dipakai.
    /// </summary>
    private static void InitializeHeDatabases()
    {
        if (!Services.BuildInfo.IsServer) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await HeQueue.InitializeAsync();
                await HeParamCache.InitializeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"HE database init failed: {ex}");
            }
        });
    }

    private async void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        ShowFatalError(e.Exception);
    }

    private static async void ShowFatalError(Exception ex)
    {
        var dialog = new ContentDialog
        {
            Title = Lang.Get("Ui_StartupError"),
            Content = $"{ex.GetType().Name}\n\n{ex.Message}\n\n{ex.StackTrace}",
            CloseButtonText = Lang.Get("Ui_Ok"),
            XamlRoot = CurrentWindow?.Content?.XamlRoot
        };
        try { await dialog.ShowAsync(); } catch { }
        System.Diagnostics.Debug.WriteLine($"FATAL: {ex}");
    }
}
