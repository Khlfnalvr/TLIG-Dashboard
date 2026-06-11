using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

public sealed partial class ParameterPage : Page
{
    private LocalizationManager Lang => App.Lang;
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
}
