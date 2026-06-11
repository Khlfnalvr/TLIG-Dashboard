using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;
using Windows.UI;

namespace TLIGDashboard.Controls;

/// <summary>
/// Card-based selector for Flow / Level / Temperature process simulations.
/// Displays a compact collapsed button; clicking opens a styled flyout with
/// full cards showing K, tau, unit, and description for each system.
/// </summary>
public sealed partial class SystemModelSelectorControl : UserControl
{
    // Accent purple
    private static readonly SolidColorBrush AccentBrush =
        new(Color.FromArgb(255, 124, 58, 237));    // #7c3aed
    private static readonly SolidColorBrush HoverBrush =
        new(Color.FromArgb(30, 124, 58, 237));     // purple tint hover
    private static readonly SolidColorBrush DefaultCardBorder =
        new(Color.FromArgb(255, 45, 45, 62));      // #2D2D3E

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
        if (sender is Border b) b.Background = HoverBrush;
    }
    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border b) b.Background = new SolidColorBrush(Color.FromArgb(255, 30, 30, 46)); // #1E1E2E
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
        CardFlow.BorderBrush  = t == SimulationType.Flow        ? AccentBrush : DefaultCardBorder;
        CardLevel.BorderBrush = t == SimulationType.Level       ? AccentBrush : DefaultCardBorder;
        CardTemp.BorderBrush  = t == SimulationType.Temperature ? AccentBrush : DefaultCardBorder;
    }
}
