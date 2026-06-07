using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;
using TLIGDashboard.Views;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace TLIGDashboard.Controls;

/// <summary>
/// The learning-analytics body (overall-performance gauge + task summary + task
/// list), shared by the full <see cref="LearningAnalyticPage"/> and the Dashboard's
/// embedded "Learning Analytic" panel so both show identical content. Tasks come
/// from the server via <see cref="LearningTaskService"/>; rows open
/// <see cref="TaskDetailPage"/> in the main content frame.
/// </summary>
public sealed partial class LearningAnalyticView : UserControl
{
    private LocalizationManager Lang => App.Lang;

    private const int GlyphCheck = 0xE930; // completed
    private const int GlyphClock = 0xE823; // to do
    private static string Glyph(int cp) => char.ConvertFromUtf32(cp);

    private readonly ObservableCollection<TaskRowVm> _rows = new();

    public LearningAnalyticView()
    {
        InitializeComponent();
        TasksList.ItemsSource = _rows;
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

    /// <summary>Reloads tasks from the server. Safe to call when the host page reappears.</summary>
    public System.Threading.Tasks.Task ReloadAsync() => LoadAsync();

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var result = await LearningTaskService.LoadAsync();

        _rows.Clear();
        int done = 0;
        foreach (var t in result.Tasks)
        {
            bool isDone = result.Completed.Contains(t.Id);
            if (isDone) done++;
            _rows.Add(BuildRow(t, isDone));
        }

        int total = result.Tasks.Count;
        double ratio = total > 0 ? (double)done / total : 0;

        DrawGauge(ratio);
        OverallPercentText.Text = $"{ratio * 100:0}%";
        OverallSubText.Text     = $"{done}/{total} {Lang.LearnDash_TasksCompletedLabel}";

        TotalTasksText.Text     = total.ToString();
        DoneTasksText.Text      = done.ToString();
        RemainingTasksText.Text = (total - done).ToString();
        TaskCountText.Text      = $"({total})";

        EmptyText.Visibility    = total == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddTaskBtn.Visibility   = result.CanEdit ? Visibility.Visible : Visibility.Collapsed;
    }

    private TaskRowVm BuildRow(LearningTask t, bool done)
    {
        var accent = done ? Res("AccentGreen") : Res("AccentOrange");
        var tint   = done ? Res("TintGreen")   : Res("TintOrange");
        return new TaskRowVm
        {
            Id               = t.Id,
            Title            = t.Title,
            ObjectiveSummary = string.IsNullOrWhiteSpace(t.Objective) ? "—" : t.Objective,
            TargetSummary    = TaskUi.FormatTarget(t),
            TargetVisibility = t.HasStructuredTarget ? Visibility.Visible : Visibility.Collapsed,
            StatusText       = done ? Lang.LearnDash_Completed : Lang.LearnDash_ToDo,
            StatusGlyph      = done ? Glyph(GlyphCheck) : Glyph(GlyphClock),
            StatusBrush      = accent,
            StatusTintBrush  = tint,
        };
    }

    private void TaskRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TaskRowVm vm)
            App.CurrentWindow?.NavigateToTaskDetail(vm.Id);
    }

    private async void AddTask_Click(object sender, RoutedEventArgs e)
    {
        var task = await TaskUi.ShowEditDialogAsync(XamlRoot, existing: null);
        if (task is null) return;
        await LearningTaskService.SaveAsync(task);
        await LoadAsync();
    }

    private Brush Res(string key) => (Brush)Resources[key];

    // ── Gauge: top half-circle, fills from the left end ───────────────────
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

    // Angle in degrees: 0° = 3 o'clock, 90° = 12 o'clock (screen y is flipped).
    private static Point PointOnCircle(double cx, double cy, double r, double angleDeg)
    {
        double a = angleDeg * Math.PI / 180.0;
        return new Point(cx + r * Math.Cos(a), cy - r * Math.Sin(a));
    }
}

/// <summary>One clickable task row in the list (rebuilt on each load — no change notification needed).</summary>
public sealed class TaskRowVm
{
    public string     Id               { get; init; } = "";
    public string     Title            { get; init; } = "";
    public string     ObjectiveSummary { get; init; } = "";
    public string     TargetSummary    { get; init; } = "";
    public Visibility  TargetVisibility { get; init; } = Visibility.Collapsed;
    public string     StatusText       { get; init; } = "";
    public string     StatusGlyph      { get; init; } = "";
    public Brush?     StatusBrush      { get; init; }
    public Brush?     StatusTintBrush  { get; init; }
}
