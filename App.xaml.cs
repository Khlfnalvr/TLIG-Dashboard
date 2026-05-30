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

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
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
