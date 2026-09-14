using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;
using Windows.Foundation;

namespace TLIGDashboard.Controls;

public sealed partial class ContextRing : UserControl
{
    public ContextRing()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Ai.UsageChanged += OnUsage;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Ai.UsageChanged -= OnUsage;
    }

    private void OnUsage(AiTokenUsage u) => DispatcherQueue.TryEnqueue(Refresh);

    public void Refresh()
    {
        var usage = App.Ai.LastUsage;
        int limit = AiContextLimits.ForModel(App.Ai.Model, out bool known);
        if (usage is null)
        {
            Arc.Visibility = Visibility.Collapsed;
            Pct.Text = "–";
            ToolTipService.SetToolTip(Root, "Konteks AI: belum ada percakapan.");
            return;
        }

        double frac = Math.Clamp((double)usage.Total / Math.Max(1, limit), 0, 1);
        DrawArc(frac);
        Arc.Stroke = (Brush)Application.Current.Resources[
            frac >= 0.9 ? "SystemFillColorCriticalBrush" :
            frac >= 0.7 ? "SystemFillColorCautionBrush" :
                          "SystemFillColorSuccessBrush"];
        Pct.Text = known ? frac.ToString("P0") : Compact(usage.Total);

        string detail = usage.IsEstimated
            ? $"≈{usage.Total:N0} token (estimasi)"
            : $"{usage.Total:N0} token (prompt {usage.PromptTokens:N0} + balasan {usage.CompletionTokens:N0})";
        ToolTipService.SetToolTip(Root, known
            ? $"Konteks AI: {detail} dari {limit:N0} ({frac:P0})."
            : $"Konteks AI: {detail}. Limit model tak dikenal.");
    }

    private void DrawArc(double frac)
    {
        const double cx = 10, cy = 10, r = 7.5;
        if (frac <= 0.001)
        {
            Arc.Visibility = Visibility.Collapsed;
            return;
        }
        Arc.Visibility = Visibility.Visible;
        if (frac >= 0.999)
        {
            Arc.Data = new EllipseGeometry { Center = new Point(cx, cy), RadiusX = r, RadiusY = r };
            return;
        }
        double a = frac * 2 * Math.PI;
        var figure = new PathFigure
        {
            StartPoint = new Point(cx, cy - r),
            IsClosed = false,
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a)),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = a > Math.PI,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        Arc.Data = geometry;
    }

    private static string Compact(int total) =>
        total >= 1000 ? (total / 1000.0).ToString("0.#") + "k" : total.ToString();
}
