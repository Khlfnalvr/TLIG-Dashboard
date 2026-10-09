using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace TLIGDashboard.Controls;

public enum ChartSeriesKind
{
    /// <summary>A polyline through (X[i], Y[i]); NaN in Y breaks the line.</summary>
    Line,
    /// <summary>A full-height vertical line at X[0] (e.g. a disturbance marker). Not used for the Y range.</summary>
    VerticalMarker,
}

/// <summary>One curve on a <see cref="LineChartView"/>.</summary>
public sealed class ChartSeries
{
    public string Label { get; init; } = "";
    public Color Color { get; init; }
    public ChartSeriesKind Kind { get; init; } = ChartSeriesKind.Line;
    /// <summary>Ascending X values (time). For a marker only X[0] is used.</summary>
    public double[] X { get; init; } = [];
    public double[] Y { get; init; } = [];
    public double Thickness { get; init; } = 2;
    /// <summary>Dash pattern in pixels (e.g. {6, 4}); null = solid.</summary>
    public double[]? Dash { get; init; }
    /// <summary>Radius of a dot drawn on every point; 0 = no dots.</summary>
    public double PointRadius { get; init; }
}

/// <summary>
/// Lightweight native line chart for WinUI — replaces the former WebView2 + Chart.js pages,
/// so a chart costs a few XAML shapes instead of an Edge process, needs no internet (Chart.js
/// came from a CDN) and has no browser profile folder to write (which failed under Program
/// Files). Drawn with plain shapes on a <see cref="Canvas"/>, no third-party library.
///
/// <para>Interaction: Ctrl + mouse wheel zooms around the cursor (a plain wheel scrolls the page), drag pans,
/// double-click or the Reset button fits the data again, hovering shows every series'
/// value at the nearest time, and clicking a legend entry hides/shows that series. A new
/// <see cref="SetSeries"/> while zoomed keeps the zoom, so live data arriving every second
/// does not throw the user's view away.</para>
/// </summary>
public sealed partial class LineChartView : UserControl
{
    // ── Public surface ──────────────────────────────────────────────────────

    public string XTitle { get; set; } = "Waktu (s)";
    public string YTitle { get; set; } = "";
    /// <summary>Show the legend under the plot.</summary>
    public bool ShowLegend { get; set; } = true;

    /// <summary>Replaces every series and redraws. The current zoom/pan is kept.</summary>
    public void SetSeries(IReadOnlyList<ChartSeries> series)
    {
        _series = series;
        RebuildLegendIfChanged();
        Redraw();
    }

    // ── State ───────────────────────────────────────────────────────────────

    private readonly record struct Range(double Min, double Max)
    {
        public double Span => Max - Min;
    }

    private IReadOnlyList<ChartSeries> _series = [];
    private readonly HashSet<string> _hidden = new(StringComparer.Ordinal);
    private (Range X, Range Y)? _view;        // null = fit to data
    private (Range X, Range Y) _shown;         // what the last Redraw used
    private Rect _plot;                         // plot area inside _host, in pixels

    private readonly Grid _host;
    private readonly Canvas _canvas = new();
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly Button _reset;
    private readonly LegendPanel _legend = new();
    private string _legendKey = "";

    private bool _pressed, _panning;
    private Point _pressPos;
    private (Range X, Range Y) _pressView;

    private const double PadTop = 10, PadRight = 14, TickFont = 10, TitleFont = 10;

    public LineChartView()
    {
        _host = new Grid { Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)) };
        _reset = new Button
        {
            Content = "↻ Reset",
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            MinHeight = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        _reset.Click += (_, _) => ResetView();

        _host.Children.Add(_canvas);
        _host.Children.Add(_overlay);
        _host.Children.Add(_reset);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(_host, 0);
        Grid.SetRow(_legend, 1);
        _legend.Margin = new Thickness(4, 4, 4, 2);
        root.Children.Add(_host);
        root.Children.Add(_legend);
        Content = root;

        _host.SizeChanged          += (_, _) => Redraw();
        _host.PointerWheelChanged  += OnWheel;
        _host.PointerPressed       += OnPressed;
        _host.PointerMoved         += OnMoved;
        _host.PointerReleased      += OnReleased;
        _host.PointerCaptureLost   += (_, _) => { _pressed = _panning = false; };
        _host.PointerCanceled      += (_, _) => { _pressed = _panning = false; _overlay.Children.Clear(); };
        _host.PointerExited        += (_, _) => { if (!_panning) _overlay.Children.Clear(); };
        _host.DoubleTapped         += (_, _) => ResetView();
        ActualThemeChanged         += (_, _) => { RebuildLegend(); Redraw(); };
    }

    // ── View range ──────────────────────────────────────────────────────────

    private IEnumerable<ChartSeries> Visible =>
        _series.Where(s => !_hidden.Contains(s.Label));

    /// <summary>Bounds of every visible series; markers widen X only.</summary>
    private (Range X, Range Y)? FitToData()
    {
        double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
        foreach (var s in Visible)
        {
            if (s.Kind == ChartSeriesKind.VerticalMarker)
            {
                if (s.X.Length > 0 && double.IsFinite(s.X[0])) { x0 = Math.Min(x0, s.X[0]); x1 = Math.Max(x1, s.X[0]); }
                continue;
            }
            int n = Math.Min(s.X.Length, s.Y.Length);
            for (int i = 0; i < n; i++)
            {
                double x = s.X[i], y = s.Y[i];
                if (!double.IsFinite(x) || !double.IsFinite(y)) continue;
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        }
        if (x0 > x1 || y0 > y1) return null;

        if (x1 - x0 < 1e-9) { x0 -= 1; x1 += 1; }
        double pad = (y1 - y0) * 0.05;
        if (pad < 1e-9) pad = Math.Max(1, Math.Abs(y0) * 0.05);
        return (new Range(x0, x1), new Range(y0 - pad, y1 + pad));
    }

    private void ResetView()
    {
        _view = null;
        Redraw();
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    private void Redraw()
    {
        _canvas.Children.Clear();
        _overlay.Children.Clear();

        double w = _host.ActualWidth, h = _host.ActualHeight;
        var fit = FitToData();
        _reset.Visibility = _view is not null && fit is not null ? Visibility.Visible : Visibility.Collapsed;
        if (w < 60 || h < 50 || fit is null) return;

        var (xr, yr) = _view ?? fit.Value;
        _shown = (xr, yr);

        var gridBrush = new SolidColorBrush(Color.FromArgb(0x38, 0x80, 0x80, 0x80));
        var axisBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x80, 0x80, 0x80));

        // Y tick labels first: their width decides the left margin.
        var (yStep, yFirst) = NiceTicks(yr.Min, yr.Max, Math.Clamp((int)(h / 40), 2, 8));
        var yLabels = new List<(double Value, TextBlock Text)>();
        double maxLabelW = 0;
        for (double v = yFirst; v <= yr.Max + yStep * 1e-6; v += yStep)
        {
            var tb = Label(FormatTick(v, yStep), TickFont, 0.7);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            maxLabelW = Math.Max(maxLabelW, tb.DesiredSize.Width);
            yLabels.Add((v, tb));
        }

        double left   = (string.IsNullOrEmpty(YTitle) ? 4 : 18) + maxLabelW + 6;
        double bottom = TickFont + 6 + (string.IsNullOrEmpty(XTitle) ? 2 : TitleFont + 8);
        _plot = new Rect(left, PadTop, Math.Max(10, w - left - PadRight), Math.Max(10, h - PadTop - bottom));

        // Grid + Y labels
        foreach (var (v, tb) in yLabels)
        {
            double py = MapY(v);
            _canvas.Children.Add(new Line { X1 = _plot.Left, X2 = _plot.Right, Y1 = py, Y2 = py, Stroke = gridBrush, StrokeThickness = 1 });
            Canvas.SetLeft(tb, left - 6 - tb.DesiredSize.Width);
            Canvas.SetTop(tb, py - tb.DesiredSize.Height / 2);
            _canvas.Children.Add(tb);
        }

        // Grid + X labels
        var (xStep, xFirst) = NiceTicks(xr.Min, xr.Max, Math.Clamp((int)(_plot.Width / 80), 2, 10));
        for (double v = xFirst; v <= xr.Max + xStep * 1e-6; v += xStep)
        {
            double px = MapX(v);
            _canvas.Children.Add(new Line { X1 = px, X2 = px, Y1 = _plot.Top, Y2 = _plot.Bottom, Stroke = gridBrush, StrokeThickness = 1 });
            var tb = Label(FormatTick(v, xStep), TickFont, 0.7);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, px - tb.DesiredSize.Width / 2);
            Canvas.SetTop(tb, _plot.Bottom + 3);
            _canvas.Children.Add(tb);
        }

        // Axes frame (left + bottom)
        _canvas.Children.Add(new Line { X1 = _plot.Left, X2 = _plot.Left, Y1 = _plot.Top, Y2 = _plot.Bottom, Stroke = axisBrush, StrokeThickness = 1 });
        _canvas.Children.Add(new Line { X1 = _plot.Left, X2 = _plot.Right, Y1 = _plot.Bottom, Y2 = _plot.Bottom, Stroke = axisBrush, StrokeThickness = 1 });

        // Axis titles
        if (!string.IsNullOrEmpty(XTitle))
        {
            var tb = Label(XTitle, TitleFont, 0.6);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, _plot.Left + (_plot.Width - tb.DesiredSize.Width) / 2);
            Canvas.SetTop(tb, h - tb.DesiredSize.Height - 1);
            _canvas.Children.Add(tb);
        }
        if (!string.IsNullOrEmpty(YTitle))
        {
            var tb = Label(YTitle, TitleFont, 0.6);
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tb.RenderTransform = new RotateTransform { Angle = -90 };
            // Rotated about its top-left corner: after -90° it extends upward from (left, top).
            Canvas.SetLeft(tb, 1);
            Canvas.SetTop(tb, _plot.Top + (_plot.Height + tb.DesiredSize.Width) / 2);
            _canvas.Children.Add(tb);
        }

        // Series, clipped to the plot area
        var layer = new Canvas
        {
            Width = w, Height = h,
            Clip = new RectangleGeometry { Rect = _plot },
        };
        foreach (var s in Visible) DrawSeries(layer, s);
        _canvas.Children.Add(layer);
    }

    private void DrawSeries(Canvas layer, ChartSeries s)
    {
        var brush = new SolidColorBrush(s.Color);

        if (s.Kind == ChartSeriesKind.VerticalMarker)
        {
            if (s.X.Length == 0 || !double.IsFinite(s.X[0])) return;
            double px = MapX(s.X[0]);
            layer.Children.Add(new Line
            {
                X1 = px, X2 = px, Y1 = _plot.Top, Y2 = _plot.Bottom,
                Stroke = brush, StrokeThickness = s.Thickness, StrokeDashArray = Dash(s),
            });
            return;
        }

        int n = Math.Min(s.X.Length, s.Y.Length);
        PointCollection? run = null;
        GeometryGroup? dots = s.PointRadius > 0 ? new GeometryGroup() : null;

        void Flush()
        {
            if (run is { Count: > 1 })
                layer.Children.Add(new Polyline
                {
                    Points = run, Stroke = brush, StrokeThickness = s.Thickness,
                    StrokeDashArray = Dash(s), StrokeLineJoin = PenLineJoin.Round,
                });
            run = null;
        }

        for (int i = 0; i < n; i++)
        {
            double x = s.X[i], y = s.Y[i];
            if (!double.IsFinite(x) || !double.IsFinite(y)) { Flush(); continue; }

            var p = new Point(MapX(x), MapY(y));
            (run ??= new PointCollection()).Add(p);

            if (dots is not null && p.X >= _plot.Left - 4 && p.X <= _plot.Right + 4)
                dots.Children.Add(new EllipseGeometry { Center = p, RadiusX = s.PointRadius, RadiusY = s.PointRadius });
        }
        Flush();

        if (dots is { Children.Count: > 0 })
            layer.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = dots, Fill = brush });
    }

    // Shapes measure dashes in multiples of the stroke thickness; ChartSeries.Dash is in pixels.
    private static DoubleCollection? Dash(ChartSeries s)
    {
        if (s.Dash is not { Length: > 0 } d) return null;
        var c = new DoubleCollection();
        foreach (var v in d) c.Add(v / Math.Max(0.5, s.Thickness));
        return c;
    }

    private double MapX(double x) => _plot.Left + (x - _shown.X.Min) / _shown.X.Span * _plot.Width;
    private double MapY(double y) => _plot.Bottom - (y - _shown.Y.Min) / _shown.Y.Span * _plot.Height;
    private double UnmapX(double px) => _shown.X.Min + (px - _plot.Left) / _plot.Width * _shown.X.Span;
    private double UnmapY(double py) => _shown.Y.Min + (_plot.Bottom - py) / _plot.Height * _shown.Y.Span;

    // No Foreground: the text inherits the theme colour from the page, so it follows
    // light/dark switches without a redraw.
    private static TextBlock Label(string text, double size, double opacity) => new()
    {
        Text = text, FontSize = size, Opacity = opacity,
    };

    /// <summary>"Nice" tick spacing (1/2/5 × 10^n) with roughly <paramref name="count"/> ticks.</summary>
    private static (double Step, double First) NiceTicks(double min, double max, int count)
    {
        double span = Math.Max(1e-12, max - min);
        double raw  = span / Math.Max(1, count);
        double mag  = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
        return (step, Math.Ceiling(min / step - 1e-9) * step);
    }

    private static string FormatTick(double v, double step)
    {
        if (Math.Abs(v) < step * 1e-6) v = 0;   // no "-0" or 1e-17 at the origin
        int dec = step >= 1 ? 0 : Math.Min(6, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        return v.ToString("F" + dec, CultureInfo.InvariantCulture);
    }

    private static string FormatValue(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ── Interaction ─────────────────────────────────────────────────────────

    private bool HasPlot => _plot.Width > 0 && _shown.X.Span > 0 && _shown.Y.Span > 0 && _series.Count > 0;

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl + wheel zooms; a plain wheel is left to the page's ScrollViewer. The charts sit
        // inside scrolling pages, and zooming on a plain wheel hijacked the page scroll every
        // time the cursor happened to pass over a chart.
        if (!HasPlot || !e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        var pt = e.GetCurrentPoint(_host);
        int delta = pt.Properties.MouseWheelDelta;
        if (delta == 0) return;

        double f  = delta > 0 ? 0.8 : 1.25;
        double cx = UnmapX(Math.Clamp(pt.Position.X, _plot.Left, _plot.Right));
        double cy = UnmapY(Math.Clamp(pt.Position.Y, _plot.Top, _plot.Bottom));
        var (xr, yr) = _shown;
        _view = (new Range(cx - (cx - xr.Min) * f, cx + (xr.Max - cx) * f),
                 new Range(cy - (cy - yr.Min) * f, cy + (yr.Max - cy) * f));
        Redraw();
        e.Handled = true;   // zoom the chart, don't scroll the page underneath
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(_host);
        if (!pt.Properties.IsLeftButtonPressed || !HasPlot) return;
        _pressed  = true;
        _panning  = false;
        _pressPos = pt.Position;
        _pressView = _shown;
        _host.CapturePointer(e.Pointer);
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        var pt  = e.GetCurrentPoint(_host);
        var pos = pt.Position;

        // A release that happened outside the window can be missed; never keep panning
        // (and hiding the tooltip) with no button actually held.
        if (_pressed && !pt.Properties.IsLeftButtonPressed) _pressed = _panning = false;

        if (_pressed)
        {
            double dx = pos.X - _pressPos.X, dy = pos.Y - _pressPos.Y;
            if (!_panning && Math.Abs(dx) + Math.Abs(dy) < 4) return;   // keep clicks/double-clicks clean
            _panning = true;

            var (xr, yr) = _pressView;
            double sx = dx / _plot.Width * xr.Span, sy = dy / _plot.Height * yr.Span;
            _view = (new Range(xr.Min - sx, xr.Max - sx), new Range(yr.Min + sy, yr.Max + sy));
            Redraw();
            return;
        }

        ShowTooltip(pos);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressed = _panning = false;
        _host.ReleasePointerCapture(e.Pointer);
    }

    /// <summary>Crosshair + every visible series' value at the time nearest the cursor.</summary>
    private void ShowTooltip(Point pos)
    {
        _overlay.Children.Clear();
        if (!HasPlot || !_plot.Contains(pos)) return;

        double x = UnmapX(pos.X);
        var rows = new List<(ChartSeries S, double Y)>();
        double? nearestX = null;
        foreach (var s in Visible)
        {
            if (s.Kind != ChartSeriesKind.Line) continue;
            int i = NearestIndex(s.X, Math.Min(s.X.Length, s.Y.Length), x);
            if (i < 0 || !double.IsFinite(s.Y[i])) continue;
            rows.Add((s, s.Y[i]));
            if (nearestX is null || Math.Abs(s.X[i] - x) < Math.Abs(nearestX.Value - x)) nearestX = s.X[i];
        }
        if (rows.Count == 0) return;

        bool dark = ActualTheme == ElementTheme.Dark;
        var fg = new SolidColorBrush(dark ? Color.FromArgb(255, 240, 240, 240) : Color.FromArgb(255, 30, 30, 30));

        _overlay.Children.Add(new Line
        {
            X1 = pos.X, X2 = pos.X, Y1 = _plot.Top, Y2 = _plot.Bottom,
            Stroke = new SolidColorBrush(Color.FromArgb(0x70, 0x80, 0x80, 0x80)), StrokeThickness = 1,
        });

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{FormatValue(nearestX ?? x)} s", FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = fg,
        });
        foreach (var (s, y) in rows)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new Rectangle { Width = 9, Height = 9, Fill = new SolidColorBrush(s.Color), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = $"{s.Label}: {FormatValue(y)}", FontSize = 11, Foreground = fg });
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Ctrl + scroll: zoom · seret: geser · klik ganda: reset",
            FontSize = 10, Opacity = 0.6, Foreground = fg, Margin = new Thickness(0, 2, 0, 0),
        });

        var tip = new Border
        {
            Child = panel,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(dark ? Color.FromArgb(0xE8, 0x20, 0x20, 0x20) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)),
            BorderThickness = new Thickness(1),
        };
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tx = pos.X + 14, ty = pos.Y + 10;
        if (tx + tip.DesiredSize.Width > _host.ActualWidth)  tx = pos.X - 14 - tip.DesiredSize.Width;
        if (ty + tip.DesiredSize.Height > _host.ActualHeight) ty = _host.ActualHeight - tip.DesiredSize.Height;
        Canvas.SetLeft(tip, Math.Max(0, tx));
        Canvas.SetTop(tip, Math.Max(0, ty));
        _overlay.Children.Add(tip);
    }

    /// <summary>Index of the X closest to <paramref name="x"/> (X ascending); -1 when empty.</summary>
    private static int NearestIndex(double[] xs, int n, double x)
    {
        if (n == 0) return -1;
        int lo = 0, hi = n - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (xs[mid] < x) lo = mid + 1; else hi = mid;
        }
        if (lo > 0 && Math.Abs(xs[lo - 1] - x) <= Math.Abs(xs[lo] - x)) lo--;
        return lo;
    }

    // ── Legend ──────────────────────────────────────────────────────────────

    private void RebuildLegendIfChanged()
    {
        string key = string.Join('\u001f', _series.Select(s => s.Label + "|" + s.Color));
        if (key == _legendKey) return;
        _legendKey = key;
        _hidden.RemoveWhere(l => _series.All(s => s.Label != l));
        RebuildLegend();
    }

    private void RebuildLegend()
    {
        _legend.Children.Clear();
        _legend.Visibility = ShowLegend && _series.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!ShowLegend) return;

        foreach (var s in _series)
        {
            var sample = new Line
            {
                X1 = 0, X2 = 18, Y1 = 6, Y2 = 6,
                Stroke = new SolidColorBrush(s.Color),
                StrokeThickness = Math.Max(1.5, s.Thickness),
                StrokeDashArray = Dash(s),
                VerticalAlignment = VerticalAlignment.Center,
            };
            var item = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 5,
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),   // hit-testable gaps
                Opacity = _hidden.Contains(s.Label) ? 0.35 : 1,
            };
            item.Children.Add(new Grid { Width = 18, Height = 12, Children = { sample } });
            item.Children.Add(new TextBlock { Text = s.Label, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            ToolTipService.SetToolTip(item, "Klik untuk menyembunyikan / menampilkan");

            string label = s.Label;
            item.Tapped += (_, e) =>
            {
                if (!_hidden.Remove(label)) _hidden.Add(label);
                item.Opacity = _hidden.Contains(label) ? 0.35 : 1;
                Redraw();
                e.Handled = true;
            };
            _legend.Children.Add(item);
        }
    }

    /// <summary>Wraps its children onto centered rows (WinUI has no built-in WrapPanel).</summary>
    private sealed partial class LegendPanel : Panel
    {
        private const double HGap = 14, VGap = 3;

        protected override Size MeasureOverride(Size available)
        {
            double maxW = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
            double x = 0, y = 0, rowH = 0, widest = 0;
            foreach (var c in Children)
            {
                c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var s = c.DesiredSize;
                if (x > 0 && x + s.Width > maxW) { y += rowH + VGap; x = 0; rowH = 0; }
                x += s.Width + HGap;
                rowH = Math.Max(rowH, s.Height);
                widest = Math.Max(widest, x - HGap);
            }
            double width = double.IsInfinity(available.Width) ? widest : available.Width;
            return new Size(width, Children.Count == 0 ? 0 : y + rowH);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var row = new List<UIElement>();
            double y = 0, rowW = 0, rowH = 0;

            void PlaceRow()
            {
                double x = Math.Max(0, (final.Width - rowW) / 2);
                foreach (var c in row)
                {
                    c.Arrange(new Rect(x, y + (rowH - c.DesiredSize.Height) / 2, c.DesiredSize.Width, c.DesiredSize.Height));
                    x += c.DesiredSize.Width + HGap;
                }
                y += rowH + VGap;
                row.Clear(); rowW = 0; rowH = 0;
            }

            foreach (var c in Children)
            {
                var s = c.DesiredSize;
                double needed = row.Count == 0 ? s.Width : rowW + HGap + s.Width;
                if (row.Count > 0 && needed > final.Width) { PlaceRow(); needed = s.Width; }
                row.Add(c);
                rowW = needed;
                rowH = Math.Max(rowH, s.Height);
            }
            if (row.Count > 0) PlaceRow();
            return final;
        }
    }
}
