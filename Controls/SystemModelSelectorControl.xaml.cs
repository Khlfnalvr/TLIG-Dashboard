using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;

namespace TLIGDashboard.Controls;

/// <summary>
/// Card-based selector for Flow / Level / Temperature process simulations.
/// Displays a compact collapsed button; clicking opens a styled flyout with
/// full cards showing K, tau, unit, and description for each system.
/// </summary>
public sealed partial class SystemModelSelectorControl : UserControl
{
    // Theme-aware WinUI brushes (follow light/dark theme + system accent)
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // Per-system collapsed-view glyphs (Segoe Fluent / MDL2)
    private static readonly Dictionary<SimulationType, (string Glyph, string Name, string Unit)> _meta = new()
    {
        [SimulationType.Flow]        = ("", "Flow",        "L/min"),
        [SimulationType.Level]       = ("", "Level",       "cm"),
        [SimulationType.Temperature] = ("", "Temperature", "°C"),
    };

    public SystemModelSelectorControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.SimType.SimulationTypeChanged += OnSimTypeChanged;
        UpdateCollapsed(App.SimType.CurrentType);
        UpdateCheckmarks(App.SimType.CurrentType);
    }

    private void OnSimTypeChanged(object? sender, SimulationType t)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCollapsed(t);
            UpdateCheckmarks(t);
        });
    }

    // ── Button click — flyout opens automatically, just keep state sync ──────
    private void AnchorBtn_Click(object sender, RoutedEventArgs e) { /* flyout auto-opens */ }

    // ── Card selection ────────────────────────────────────────────────────────
    private void CardFlow_Pressed(object sender, PointerRoutedEventArgs e)  => Select(SimulationType.Flow);
    private void CardLevel_Pressed(object sender, PointerRoutedEventArgs e) => Select(SimulationType.Level);
    private void CardTemp_Pressed(object sender, PointerRoutedEventArgs e)  => Select(SimulationType.Temperature);

    private void Select(SimulationType t)
    {
        App.SimType.CurrentType = t;
        SelectorFlyout.Hide();
    }

    // ── Hover highlight ───────────────────────────────────────────────────────
    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = Res("SubtleFillColorSecondaryBrush");
    }
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = Res("CardBackgroundFillColorDefaultBrush");
    }

    // ── UI update helpers ─────────────────────────────────────────────────────
    private void UpdateCollapsed(SimulationType t)
    {
        if (!_meta.TryGetValue(t, out var m)) return;
        CollapsedName.Text = m.Name;
        CollapsedUnit.Text = $"· {SimulationTypeService.Configs[t].Unit}";
    }

    private void UpdateCheckmarks(SimulationType t)
    {
        CheckFlow.Visibility  = t == SimulationType.Flow        ? Visibility.Visible : Visibility.Collapsed;
        CheckLevel.Visibility = t == SimulationType.Level       ? Visibility.Visible : Visibility.Collapsed;
        CheckTemp.Visibility  = t == SimulationType.Temperature ? Visibility.Visible : Visibility.Collapsed;

        // Highlight active card border
        var accent  = Res("AccentFillColorDefaultBrush");
        var neutral = Res("CardStrokeColorDefaultBrush");
        CardFlow.BorderBrush  = t == SimulationType.Flow        ? accent : neutral;
        CardLevel.BorderBrush = t == SimulationType.Level       ? accent : neutral;
        CardTemp.BorderBrush  = t == SimulationType.Temperature ? accent : neutral;
    }
}
