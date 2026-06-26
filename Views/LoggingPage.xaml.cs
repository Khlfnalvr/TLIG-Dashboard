using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using Windows.UI;

namespace TLIGDashboard.Views;

// ── Display row bound to the ListView DataTemplate ────────────────────────────
public sealed class ActivityRow
{
    public string Timestamp     { get; init; } = "";
    public string DisplayName   { get; init; } = "";
    public string Role          { get; init; } = "";
    public string CategoryLabel { get; init; } = "";
    public SolidColorBrush CategoryColor { get; init; } = new(Colors.Gray);
    public string Action      { get; init; } = "";
    public string Description { get; init; } = "";
}

// ── Date group for grouped ListView ──────────────────────────────────────────
public sealed class ActivityGroup : List<ActivityRow>
{
    public string Key        { get; }
    public string CountLabel => $"{Count} aktivitas";
    public ActivityGroup(string key) { Key = key; }
}

// ── Page ──────────────────────────────────────────────────────────────────────
public sealed partial class LoggingPage : Page
{
    private record CategoryItem(string Label, ActivityCategory? Value);

    // Cached chart data so SizeChanged can redraw without re-querying
    private IReadOnlyList<ActivityLog> _chartData = Array.Empty<ActivityLog>();

    public LoggingPage()
    {
        InitializeComponent();

        // Populate category ComboBox
        CategoryFilter.ItemsSource = new List<CategoryItem>
        {
            new("Semua Kategori",  null),
            new("Autentikasi",     ActivityCategory.Authentication),
            new("Parameter",       ActivityCategory.ControlParameter),
            new("AI",              ActivityCategory.AIInteraction),
            new("Simulasi",        ActivityCategory.Simulation),
            new("Sistem Nyata",    ActivityCategory.RealSystem),
            new("Tugas",           ActivityCategory.TaskSubmission),
            new("Refleksi",        ActivityCategory.Reflection),
            new("Umum",            ActivityCategory.General),
        };
        CategoryFilter.DisplayMemberPath = "Label";
        CategoryFilter.SelectedIndex     = 0;

        ActivityStore.Instance.Changed += OnStoreChanged;
        Unloaded += (_, _) => ActivityStore.Instance.Changed -= OnStoreChanged;

        Loaded += (_, _) => Refresh();
    }

    // ── Store change callback ─────────────────────────────────────────────────

    private void OnStoreChanged() => DispatcherQueue.TryEnqueue(Refresh);

    // ── UI event handlers ─────────────────────────────────────────────────────

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Refresh();
    private void Filter_Changed(object sender, object e) => Refresh();

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => DrawChart(_chartData);

    private async void ExportBtn_Click(object sender, RoutedEventArgs e)
    {
        var all = ActivityStore.Instance.GetAll();
        if (all.Count == 0)
        {
            await ShowDialog("Export", "Tidak ada data untuk diekspor.");
            return;
        }

        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Downloads;
        picker.SuggestedFileName      = $"ActivityLog_{DateTime.Now:yyyyMMdd_HHmm}";
        picker.FileTypeChoices.Add("Excel Workbook", new List<string> { ".xlsx" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        try
        {
            ExportToExcel(all, file.Path);
            await ShowDialog("Export Berhasil", $"File disimpan:\n{file.Path}");
        }
        catch (Exception ex) { await ShowDialog("Export Gagal", ex.Message); }
    }

    // ── Core refresh ─────────────────────────────────────────────────────────

    private void Refresh()
    {
        var userQuery = SearchBox?.Text?.Trim();
        ActivityCategory? cat = null;
        if (CategoryFilter?.SelectedItem is CategoryItem ci && ci.Value.HasValue)
            cat = ci.Value;

        DateTime? fromUtc = null;
        if (FromDatePicker?.SelectedDate is { } fd)
            fromUtc = fd.UtcDateTime.Date;

        DateTime? toUtc = null;
        if (ToDatePicker?.SelectedDate is { } td)
            toUtc = td.UtcDateTime.Date.AddDays(1).AddSeconds(-1);

        var filtered = ActivityStore.Instance.GetFiltered(userQuery, cat, fromUtc, toUtc);
        var all      = ActivityStore.Instance.GetAll();

        // ── Summary cards ────────────────────────────────────────────────────
        TotalText.Text      = all.Count.ToString();
        LoginCountText.Text = all.Count(x => x.Action is ActivityActions.Login or ActivityActions.Logout).ToString();
        AiCountText.Text    = all.Count(x => x.Category == ActivityCategory.AIInteraction).ToString();
        TaskCountText.Text  = all.Count(x => x.Category == ActivityCategory.TaskSubmission).ToString();
        SimCountText.Text   = all.Count(x => x.Category == ActivityCategory.Simulation).ToString();

        // ── Bar chart (always shows all data, ignores filters) ───────────────
        _chartData = all;
        DrawChart(all);

        // ── Grouped list ─────────────────────────────────────────────────────
        var groups = filtered
            .GroupBy(a => a.TimestampUtc.ToLocalTime().Date)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var group = new ActivityGroup(DateLabel(g.Key));
                group.AddRange(g.OrderByDescending(a => a.TimestampUtc).Select(ToRow));
                return group;
            })
            .ToList();

        GroupedActivities.Source = groups;

        StatusText.Text = $"Menampilkan {filtered.Count} dari {all.Count} aktivitas  ·  "
                        + $"{groups.Count} hari  ·  Diperbarui {DateTime.Now:HH:mm:ss}";
    }

    // ── Bar chart ─────────────────────────────────────────────────────────────

    private void DrawChart(IReadOnlyList<ActivityLog> data)
    {
        ChartCanvas.Children.Clear();

        double w = ChartCanvas.ActualWidth;
        double h = ChartCanvas.ActualHeight;
        if (w < 10 || h < 10) return;

        const int days = 14;
        var today = DateTime.Today;

        // Count per day
        var counts = new int[days];
        var dayDates = new DateTime[days];
        for (int i = 0; i < days; i++)
            dayDates[i] = today.AddDays(-(days - 1 - i));

        var byDay = data
            .GroupBy(a => a.TimestampUtc.ToLocalTime().Date)
            .ToDictionary(g => g.Key, g => g.Count());

        for (int i = 0; i < days; i++)
            counts[i] = byDay.TryGetValue(dayDates[i], out int c) ? c : 0;

        int maxCount = counts.Max();
        if (maxCount == 0) maxCount = 1;

        const double labelH  = 22;
        double chartH = h - labelH;
        double slotW  = w / days;
        double barW   = Math.Max(4, slotW * 0.55);

        // Baseline
        var baseline = new Line
        {
            X1 = 0, Y1 = chartH, X2 = w, Y2 = chartH,
            Stroke = new SolidColorBrush(Color.FromArgb(40, 200, 200, 200)),
            StrokeThickness = 1,
        };
        ChartCanvas.Children.Add(baseline);

        for (int i = 0; i < days; i++)
        {
            double x     = i * slotW + (slotW - barW) / 2;
            bool isToday = dayDates[i] == today;

            // Bar
            double barH = counts[i] == 0
                ? 2
                : Math.Max(4, (double)counts[i] / maxCount * (chartH - 16));

            var rect = new Rectangle
            {
                Width    = barW,
                Height   = barH,
                RadiusX  = 3,
                RadiusY  = 3,
                Fill     = new SolidColorBrush(isToday
                    ? Color.FromArgb(230, 96, 165, 250)   // brighter blue for today
                    : Color.FromArgb(180, 30, 100, 180)), // dimmer for past days
            };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, chartH - barH);
            ChartCanvas.Children.Add(rect);

            // Count label above bar
            if (counts[i] > 0)
            {
                var countLbl = new TextBlock
                {
                    Text     = counts[i].ToString(),
                    FontSize = 9,
                    Opacity  = 0.75,
                    Foreground = new SolidColorBrush(Color.FromArgb(220, 220, 220, 220)),
                };
                Canvas.SetLeft(countLbl, x + barW / 2 - 5);
                Canvas.SetTop(countLbl, chartH - barH - 14);
                ChartCanvas.Children.Add(countLbl);
            }

            // Date label below baseline
            string lblText = isToday
                ? "Hari ini"
                : dayDates[i].Day == 1 || i == 0
                    ? dayDates[i].ToString("dd/MM")
                    : dayDates[i].ToString("dd");

            var dateLbl = new TextBlock
            {
                Text      = lblText,
                FontSize  = 9,
                Opacity   = isToday ? 0.9 : 0.5,
                Foreground = new SolidColorBrush(isToday
                    ? Color.FromArgb(255, 150, 200, 255)
                    : Color.FromArgb(200, 180, 180, 180)),
            };
            Canvas.SetLeft(dateLbl, x + barW / 2 - (isToday ? 18 : 8));
            Canvas.SetTop(dateLbl, chartH + 5);
            ChartCanvas.Children.Add(dateLbl);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DateLabel(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)          return $"Hari ini  —  {date:dddd, d MMMM yyyy}";
        if (date == today.AddDays(-1)) return $"Kemarin  —  {date:dddd, d MMMM yyyy}";
        return date.ToString("dddd, d MMMM yyyy");
    }

    private static ActivityRow ToRow(ActivityLog a) => new()
    {
        Timestamp     = a.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
        DisplayName   = a.DisplayName,
        Role          = a.Role,
        CategoryLabel = a.CategoryLabel,
        CategoryColor = CategoryBrush(a.Category),
        Action        = a.Action,
        Description   = a.Description,
    };

    private static SolidColorBrush CategoryBrush(ActivityCategory cat) => cat switch
    {
        ActivityCategory.Authentication   => new SolidColorBrush(Color.FromArgb(255, 16,  110, 190)),
        ActivityCategory.ControlParameter => new SolidColorBrush(Color.FromArgb(255, 130, 80,  180)),
        ActivityCategory.AIInteraction    => new SolidColorBrush(Color.FromArgb(255, 0,   140, 120)),
        ActivityCategory.Simulation       => new SolidColorBrush(Color.FromArgb(255, 200, 100, 0  )),
        ActivityCategory.RealSystem       => new SolidColorBrush(Color.FromArgb(255, 180, 40,  40 )),
        ActivityCategory.TaskSubmission   => new SolidColorBrush(Color.FromArgb(255, 20,  140, 60 )),
        ActivityCategory.Reflection       => new SolidColorBrush(Color.FromArgb(255, 100, 100, 160)),
        _                                 => new SolidColorBrush(Color.FromArgb(255, 100, 100, 100)),
    };

    private static void ExportToExcel(IReadOnlyList<ActivityLog> logs, string path)
    {
        var rows = logs.Select(a => new
        {
            Timestamp   = a.TimestampLocal,
            Username    = a.Username,
            DisplayName = a.DisplayName,
            Role        = a.Role,
            Category    = a.CategoryLabel,
            Action      = a.Action,
            Description = a.Description,
            RelatedId   = a.RelatedId,
        });
        MiniExcelLibs.MiniExcel.SaveAs(path, rows, overwriteFile: true);
    }

    private async System.Threading.Tasks.Task ShowDialog(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title, Content = message,
            CloseButtonText = "OK", XamlRoot = XamlRoot,
        };
        await dlg.ShowAsync();
    }
}
