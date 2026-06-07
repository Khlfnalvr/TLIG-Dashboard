using System.Linq;
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
    private static string Glyph(int cp) => char.ConvertFromUtf32(cp);

    private string        _taskId = "";
    private LearningTask? _task;
    private bool          _completed;

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
        => App.CurrentWindow?.NavigateToPage("LearningAnalytic");

    private Brush Res(string key) => (Brush)Resources[key];
}
