using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace TLIGDashboard.Controls;

/// <summary>
/// Step-response plot for the Smart PID Designer: the RK4 curve plus a dashed setpoint line.
/// Drawn natively by <see cref="LineChartView"/> (no WebView2 / Chart.js) — scroll to zoom,
/// drag to pan, double-click (or the Reset button) to fit again.
/// </summary>
public sealed partial class PidResponseChart : UserControl
{
    private static readonly Color Response = Color.FromArgb(255, 75, 192, 192);
    private static readonly Color Setpoint = Color.FromArgb(204, 255, 99, 132);

    public PidResponseChart()
    {
        InitializeComponent();
    }

    /// <summary>Plots an RK4 step response with a dashed line at <paramref name="setpoint"/>.</summary>
    public void Update(IEnumerable<double> time, IEnumerable<double> amplitude, double setpoint)
    {
        var t = time.ToArray();
        var a = amplitude.ToArray();

        var series = new List<ChartSeries>
        {
            new() { Label = "Step Response", Color = Response, X = t, Y = a },
        };
        if (double.IsFinite(setpoint) && t.Length > 0)
            series.Add(new() { Label = "Setpoint", Color = Setpoint, X = [t[0], t[^1]], Y = [setpoint, setpoint], Thickness = 1.5, Dash = [6, 4] });

        Chart.SetSeries(series);
    }
}
