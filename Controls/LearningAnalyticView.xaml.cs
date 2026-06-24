using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace TLIGDashboard.Controls;

public sealed partial class LearningAnalyticView : UserControl
{
    private LocalizationManager Lang => App.Lang;

    public LearningAnalyticView()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Lang.PropertyChanged += OnLangChanged;
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => Lang.PropertyChanged -= OnLangChanged;

    private void OnLangChanged(object? sender, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => _ = LoadAsync());

    public System.Threading.Tasks.Task ReloadAsync() => LoadAsync();

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var result = await LearningTaskService.LoadAsync();

        int done  = result.Tasks.Count(t => result.Completed.Contains(t.Id));
        int total = result.Tasks.Count;
        double ratio = total > 0 ? (double)done / total : 0;

        DrawGauge(ratio);
        OverallPercentText.Text = $"{ratio * 100:0}%";
        OverallSubText.Text     = $"{done}/{total} {Lang.LearnDash_TasksCompletedLabel}";
        TotalTasksText.Text     = total.ToString();
        DoneTasksText.Text      = done.ToString();
        RemainingTasksText.Text = (total - done).ToString();
    }

    private void DrawGauge(double fraction)
    {
        const double cx = 82, cy = 82, r = 66;
        GaugeTrack.Data = ArcGeometry(cx, cy, r, 180, 0, SweepDirection.Clockwise, isLargeArc: false);
        double end = 180 - Clamp01(fraction) * 180;
        GaugeValue.Data = ArcGeometry(cx, cy, r, 180, end, SweepDirection.Clockwise, isLargeArc: false);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    private static Geometry ArcGeometry(double cx, double cy, double r,
                                        double startDeg, double endDeg,
                                        SweepDirection dir, bool isLargeArc)
    {
        var figure = new PathFigure
        {
            StartPoint = PointOnCircle(cx, cy, r, startDeg),
            IsClosed = false
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointOnCircle(cx, cy, r, endDeg),
            Size = new Size(r, r),
            SweepDirection = dir,
            IsLargeArc = isLargeArc,
            RotationAngle = 0
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointOnCircle(double cx, double cy, double r, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
    }

    private Brush Res(string key) => (Brush)Resources[key];
}
