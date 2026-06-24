using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

/// <summary>
/// Detail view for a single learning task, opened from the task list. Shows the
/// objective and (if any) the structured target, lets the current user mark it
/// complete / not done, and — for staff (Dosen/Asisten) — edit or delete it.
/// Reached via <c>MainWindow.NavigateToTaskDetail(taskId)</c>; the task id is the
/// navigation parameter.
/// </summary>
public sealed partial class TaskDetailPage : Page
{
    private LocalizationManager Lang => App.Lang;

    private const int GlyphCheck = 0xE930;
    private const int GlyphClock = 0xE823;
    private const int GlyphPlay  = 0xE768;
    private const int GlyphStop  = 0xE71A;
    private static string Glyph(int cp) => char.ConvertFromUtf32(cp);

    private string        _taskId = "";
    private LearningTask? _task;
    private bool          _completed;

    // ── Simulation timer ─────────────────────────────────────────────────
    private DispatcherTimer? _simTimer;
    private DateTime         _simEnd;
    private bool             _simRunning;

    public TaskDetailPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _taskId = e.Parameter as string ?? "";
        _ = LoadAsync();
    }

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var result = await LearningTaskService.LoadAsync();
        _task      = result.Tasks.FirstOrDefault(t => t.Id == _taskId);
        _completed = result.Completed.Contains(_taskId);

        if (_task is null)
        {
            ContentRoot.Visibility = Visibility.Collapsed;
            NotFoundText.Visibility = Visibility.Visible;
            return;
        }

        ContentRoot.Visibility  = Visibility.Visible;
        NotFoundText.Visibility = Visibility.Collapsed;

        TitleText.Text     = _task.Title;
        CreatedByText.Text = string.IsNullOrWhiteSpace(_task.CreatedBy)
            ? "" : $"{Lang.LearnDash_CreatedBy} {_task.CreatedBy}";
        ObjectiveText.Text = string.IsNullOrWhiteSpace(_task.Objective) ? "—" : _task.Objective;
        TargetText.Text    = _task.HasStructuredTarget ? TaskUi.FormatTarget(_task) : Lang.LearnDash_NoTarget;

        // Status pill.
        var accent = _completed ? Res("AccentGreen") : Res("AccentOrange");
        var tint   = _completed ? Res("TintGreen")   : Res("TintOrange");
        StatusBorder.Background = tint;
        StatusIcon.Foreground   = accent;
        StatusText.Foreground   = accent;
        StatusIcon.Glyph        = Glyph(_completed ? GlyphCheck : GlyphClock);
        StatusText.Text         = _completed ? Lang.LearnDash_Completed : Lang.LearnDash_ToDo;

        // Actions.
        MarkBtn.Content      = _completed ? Lang.LearnDash_MarkNotDone : Lang.LearnDash_MarkDone;
        bool canEdit         = result.CanEdit;
        EditBtn.Visibility   = canEdit ? Visibility.Visible : Visibility.Collapsed;
        DeleteBtn.Visibility = canEdit ? Visibility.Visible : Visibility.Collapsed;

        // Simulation timer card — show only for students (mahasiswa).
        if (App.Session.IsStudent && _task is not null)
        {
            SimTimerCard.Visibility = Visibility.Visible;
            if (_task.SimulationTimeMinutes > 0)
            {
                SimCountdownText.Text = FormatTime(TimeSpan.FromMinutes(_task.SimulationTimeMinutes));
                SimTimerStatus.Text   = $"{_task.SimulationTimeMinutes} {Lang.Sim_Minutes}";
            }
            else
            {
                SimCountdownText.Text = "∞";
                SimTimerStatus.Text   = Lang.Sim_NoLimit;
                SimStartBtn.IsEnabled = false;
            }
            SimBtnText.Text = Lang.Sim_Start;
            SimBtnIcon.Glyph = Glyph(GlyphPlay);
        }
        else
        {
            SimTimerCard.Visibility = Visibility.Collapsed;
        }
    }

    private async void Mark_Click(object sender, RoutedEventArgs e)
    {
        if (_task is null) return;
        await LearningTaskService.SetCompletedAsync(_task.Id, !_completed);
        await LoadAsync();
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_task is null) return;
        var edited = await TaskUi.ShowEditDialogAsync(XamlRoot, _task);
        if (edited is null) return;
        await LearningTaskService.SaveAsync(edited);
        await LoadAsync();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_task is null) return;

        var confirm = new ContentDialog
        {
            Title             = _task.Title,
            Content           = Lang.LearnDash_DeleteConfirm,
            PrimaryButtonText = Lang.LearnDash_Delete,
            CloseButtonText   = Lang.LearnDash_Cancel,
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        ContentDialogResult res;
        try { res = await confirm.ShowAsync(); }
        catch { return; }
        if (res != ContentDialogResult.Primary) return;

        await LearningTaskService.DeleteAsync(_task.Id);
        App.CurrentWindow?.NavigateToPage("LearningAnalytic");
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        StopTimer();
        App.CurrentWindow?.NavigateToPage("LearningAnalytic");
    }

    // ── Simulation timer ─────────────────────────────────────────────────

    private void SimStart_Click(object sender, RoutedEventArgs e)
    {
        if (_simRunning)
        {
            StopTimer();
        }
        else
        {
            StartTimer();
        }
    }

    private void StartTimer()
    {
        if (_task is null || _task.SimulationTimeMinutes <= 0) return;
        _simEnd     = DateTime.UtcNow.AddMinutes(_task.SimulationTimeMinutes);
        _simRunning = true;
        SimBtnText.Text   = "Stop";
        SimBtnIcon.Glyph  = Glyph(GlyphStop);
        SimTimerIcon.Foreground = (Brush)Resources["AccentGreen"];

        _simTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _simTimer.Tick += SimTimer_Tick;
        _simTimer.Start();
        UpdateCountdown();
    }

    private void StopTimer()
    {
        _simTimer?.Stop();
        _simTimer = null;
        _simRunning = false;
        if (_task is not null)
        {
            SimBtnText.Text  = Lang.Sim_Start;
            SimBtnIcon.Glyph = Glyph(GlyphPlay);
            SimCountdownText.Text = FormatTime(TimeSpan.FromMinutes(_task.SimulationTimeMinutes));
            SimTimerStatus.Text   = $"{_task.SimulationTimeMinutes} {Lang.Sim_Minutes}";
            SimTimerIcon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        }
    }

    private void SimTimer_Tick(object? sender, object e) => UpdateCountdown();

    private void UpdateCountdown()
    {
        var remaining = _simEnd - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _simTimer?.Stop();
            _simTimer = null;
            _simRunning = false;
            SimCountdownText.Text = "00:00";
            SimTimerStatus.Text   = Lang.Sim_Expired;
            SimBtnText.Text       = Lang.Sim_Start;
            SimBtnIcon.Glyph      = Glyph(GlyphPlay);
            SimStartBtn.IsEnabled = false;
            SimTimerIcon.Foreground = (Brush)Resources["AccentOrange"];
            return;
        }
        SimCountdownText.Text = FormatTime(remaining);
        SimTimerStatus.Text   = Lang.Sim_TimeLeft;
    }

    private static string FormatTime(TimeSpan t)
    {
        int h = (int)t.TotalHours;
        return h > 0
            ? $"{h:D2}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";
    }

    private Brush Res(string key) => (Brush)Resources[key];
}
