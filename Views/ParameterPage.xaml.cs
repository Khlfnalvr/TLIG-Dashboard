using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

public sealed partial class ParameterPage : Page
{
    private LocalizationManager Lang   => App.Lang;
    private SystemStatusService Status => App.Status;

    public ParameterPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        App.SimType.SimulationTypeChanged += OnSimulationTypeChanged;
        ApplySimulationType(App.SimType.CurrentType);
    }

    protected override void OnNavigatedFrom(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.SimType.SimulationTypeChanged -= OnSimulationTypeChanged;
    }

    private void OnSimulationTypeChanged(object? sender, SimulationType type)
        => DispatcherQueue.TryEnqueue(() => ApplySimulationType(type));

    private void ApplySimulationType(SimulationType type)
    {
        var svc = App.SimType;
        if (BlkSetpointLabel != null) BlkSetpointLabel.Text = svc.SetpointLabel;
        if (BlkPlantLabel    != null) BlkPlantLabel.Text    = svc.PlantLabel;
        if (CtlSetpointLabel != null) CtlSetpointLabel.Text = svc.SetpointLabel;
    }

    // ── PID Apply ────────────────────────────────────────────────────────────

    private void ApplyPid_Click(object sender, RoutedEventArgs e)
    {
#if CLIENT
        int paramUsed = ActivityStore.Instance.GetAll()
            .Count(a => a.Category == ActivityCategory.ControlParameter);
        if (paramUsed >= 3)
        {
            ParamLimitInfoBar.IsOpen = true;
            return;
        }
        ParamLimitInfoBar.IsOpen = false;
#endif

        double kp = double.IsNaN(KpBox.Value) ? 0 : KpBox.Value;
        double ki = double.IsNaN(KiBox.Value) ? 0 : KiBox.Value;
        double kd = double.IsNaN(KdBox.Value) ? 0 : KdBox.Value;

        ActivityStore.Instance.LogSession(
            ActivityCategory.ControlParameter,
            ActivityActions.ParameterChanged,
            $"Parameter PID diterapkan: Kp={kp:F2}, Ki={ki:F2}, Kd={kd:F2}",
            metadata: new()
            {
                ["Kp"] = kp.ToString("F2"),
                ["Ki"] = ki.ToString("F2"),
                ["Kd"] = kd.ToString("F2"),
                ["system"] = App.SimType.CurrentType.ToString(),
            });

        ShowApplyFeedback();
    }

    // ── Simulation Run / Stop ────────────────────────────────────────────────

    private void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        double kp = double.IsNaN(KpBox.Value) ? 0 : KpBox.Value;
        double ki = double.IsNaN(KiBox.Value) ? 0 : KiBox.Value;
        double kd = double.IsNaN(KdBox.Value) ? 0 : KdBox.Value;

        ActivityStore.Instance.LogSession(
            ActivityCategory.Simulation,
            ActivityActions.SimulationStarted,
            $"Simulasi {App.SimType.CurrentType} dimulai: Kp={kp:F2}, Ki={ki:F2}, Kd={kd:F2}",
            metadata: new()
            {
                ["Kp"] = kp.ToString("F2"),
                ["Ki"] = ki.ToString("F2"),
                ["Kd"] = kd.ToString("F2"),
                ["system"] = App.SimType.CurrentType.ToString(),
            });
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        ActivityStore.Instance.LogSession(
            ActivityCategory.Simulation,
            ActivityActions.SimulationCompleted,
            $"Simulasi {App.SimType.CurrentType} dihentikan");
    }

    // ── Feedback visual saat Terapkan diklik ─────────────────────────────────

    private async void ShowApplyFeedback()
    {
        var original = ApplyPidBtn.Content;
        ApplyPidBtn.IsEnabled = false;
        ApplyPidBtn.Content = new Microsoft.UI.Xaml.Controls.StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new FontIcon
                {
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                    Glyph = "", FontSize = 11
                },
                new TextBlock { Text = "Diterapkan!" }
            }
        };
        await System.Threading.Tasks.Task.Delay(1500);
        ApplyPidBtn.Content   = original;
        ApplyPidBtn.IsEnabled = true;
    }
}
