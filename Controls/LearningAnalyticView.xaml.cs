using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using TLIGDashboard.Views;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace TLIGDashboard.Controls;

/// <summary>
/// Learning-analytics body shared by LearningAnalyticPage and the Dashboard panel.
/// Includes role-based grading section (Mahasiswa / Dosen) below the task list.
/// </summary>
public sealed partial class LearningAnalyticView : UserControl
{
    private LocalizationManager Lang => App.Lang;

    private const int GlyphCheck = 0xE930;
    private const int GlyphClock = 0xE823;
    private static string Glyph(int cp) => char.ConvertFromUtf32(cp);

    private readonly ObservableCollection<TaskRowVm> _rows = new();

    // ── Grading state ─────────────────────────────────────────────────────
    private readonly GradingService _grading = GradingService.Instance;
    private string _gradingAssignmentId = "ASGN-001";
    private string? _peerTargetStudentId;
    private string? _lecGradingStudentId;

    private readonly List<(string Id, string Title)> _assignments = new()
    {
        ("ASGN-001", "Tugas Kelompok - Sistem Kontrol PID"),
        ("ASGN-002", "Tugas Kelompok - Heat Exchanger"),
    };

    // Group members — populated from StudentService (excludes current user)
    private List<GradingGroupMemberVm> _groupMembers = new();

    // ─────────────────────────────────────────────────────────────────────

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
        InitGradingCombo();
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => Lang.PropertyChanged -= OnLangChanged;

    private void OnLangChanged(object? sender, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => _ = LoadAsync());

    public System.Threading.Tasks.Task ReloadAsync() => LoadAsync();

    // ═══════════════════════════════════════════════════════════════════
    //  TASKS (unchanged logic)
    // ═══════════════════════════════════════════════════════════════════

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

        // Load grading section berdasarkan role
        await LoadGradingSectionAsync();
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

    // ═══════════════════════════════════════════════════════════════════
    //  GRADING SECTION — init & dispatch by role
    // ═══════════════════════════════════════════════════════════════════

    private void InitGradingCombo()
    {
        GradingAssignmentCombo.Items.Clear();
        foreach (var (id, title) in _assignments)
            GradingAssignmentCombo.Items.Add(new ComboBoxItem { Content = title, Tag = id });
        GradingAssignmentCombo.SelectedIndex = 0;
    }

    private async System.Threading.Tasks.Task LoadGradingSectionAsync()
    {
        if (!App.Session.IsSignedIn)
        {
            GradingCard.Visibility = Visibility.Collapsed;
            return;
        }

        // Ensure student data is loaded (first call reads from disk)
        await StudentService.Instance.EnsureLoadedAsync();

        GradingCard.Visibility = Visibility.Visible;
        bool isStaff = UserRoles.IsStaff(App.Session.Role);

        if (isStaff)
        {
            GradingSubtitle.Text = "Kelola dan pantau seluruh penilaian mahasiswa";
            StudentGradingPivot.Visibility  = Visibility.Collapsed;
            LecturerGradingPivot.Visibility = Visibility.Visible;
            ManageStudentsBtn.Visibility    = Visibility.Visible;
            await LoadLecturerGradingAsync();
        }
        else
        {
            GradingSubtitle.Text = "Nilai rekan kelompokmu dan lihat nilai yang kamu terima";
            LecturerGradingPivot.Visibility = Visibility.Collapsed;
            StudentGradingPivot.Visibility  = Visibility.Visible;
            ManageStudentsBtn.Visibility    = Visibility.Collapsed;

            // Load group members excluding the current user
            _groupMembers = StudentService.Instance.GetAll()
                .Where(s => s.Id != CurrentStudentId)
                .Select(s => new GradingGroupMemberVm(s.Id, s.Name))
                .ToList();

            await LoadStudentGradingAsync();
        }
    }

    private void GradingAssignmentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GradingAssignmentCombo.SelectedItem is ComboBoxItem item)
        {
            _gradingAssignmentId = item.Tag?.ToString() ?? _gradingAssignmentId;
            _ = LoadGradingSectionAsync();
        }
    }

    private void GradingRefresh_Click(object sender, RoutedEventArgs e)
        => _ = LoadGradingSectionAsync();

    // ═══════════════════════════════════════════════════════════════════
    //  MAHASISWA — student grading
    // ═══════════════════════════════════════════════════════════════════

    private string CurrentStudentId =>
        App.Session.IsSignedIn ? App.Session.Username : "STU001";

    private async System.Threading.Tasks.Task LoadStudentGradingAsync()
    {
        await RefreshPeerMembersAsync();
        await RefreshReceivedScoresAsync();
        await RefreshStudentActivitiesAsync();
    }

    private async System.Threading.Tasks.Task RefreshPeerMembersAsync()
    {
        var myEvals = await _grading.GetPeerEvaluationsByEvaluatorAsync(CurrentStudentId);
        var rated   = myEvals.Select(e => e.EvaluateeId).ToHashSet();
        foreach (var m in _groupMembers)
        {
            m.AlreadyRated = rated.Contains(m.StudentId);
            m.AlreadyRatedVisibility = m.AlreadyRated ? Visibility.Visible : Visibility.Collapsed;
        }
        GroupMembersRepeater.ItemsSource = null;
        GroupMembersRepeater.ItemsSource = _groupMembers;
    }

    private async System.Threading.Tasks.Task RefreshReceivedScoresAsync()
    {
        var received = await _grading.GetPeerEvaluationsForStudentAsync(CurrentStudentId);
        var vms = received.Select(e => new GradingReceivedEvalVm
        {
            EvaluatorInitials = GetInitials(e.EvaluatorName),
            EvaluatorName     = e.EvaluatorName,
            Comment           = string.IsNullOrWhiteSpace(e.Comment) ? "(Tidak ada komentar)" : e.Comment,
            ScoreStr          = e.Score.ToString("F1"),
            EvaluatedAtStr    = e.EvaluatedAt.ToString("dd MMM yyyy, HH:mm"),
        }).ToList();
        GReceivedEvalRepeater.ItemsSource = vms;

        if (vms.Any())
        {
            GMyPeerScore.Text = received.Average(e => e.Score).ToString("F1");
            GMyPeerCount.Text = $"{vms.Count} penilai";
        }
        else
        {
            GMyPeerScore.Text = "—";
            GMyPeerCount.Text = "Belum ada penilai";
        }

        var sys = await _grading.GetSystemEvaluationForStudentAsync(CurrentStudentId, _gradingAssignmentId);
        if (sys != null)
        {
            GMySystemScore.Text = sys.Score.ToString("F1");
            GSysCommits.Text    = sys.CommitsCount.ToString();
            GSysFiles.Text      = sys.FilesModified.ToString();
            GSysOnTime.Text     = sys.SubmittedOnTime ? "Ya ✓" : "Tidak";
            GSysTasks.Text      = $"{sys.TasksCompleted}/{sys.TasksTotal}";
        }
        else
        {
            GMySystemScore.Text = "—";
        }

        var lec = await _grading.GetLecturerGradeForStudentAsync(CurrentStudentId, _gradingAssignmentId);
        GMyLecturerScore.Text = lec != null ? $"{lec.Score:F1} ({lec.LetterGrade})" : "Belum";
    }

    private async System.Threading.Tasks.Task RefreshStudentActivitiesAsync()
    {
        var acts = await _grading.GetGroupActivitiesAsync(_gradingAssignmentId);
        GStuActivitiesRepeater.ItemsSource = acts;
    }

    // Peer form handlers
    private void NilaiSekarang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _peerTargetStudentId = btn.Tag?.ToString();
        var target = _groupMembers.FirstOrDefault(m => m.StudentId == _peerTargetStudentId);
        if (target == null) return;

        FormTargetName.Text    = $"Menilai: {target.StudentName}";
        GSliderContrib.Value   = 75;
        GSliderCoop.Value      = 75;
        GSliderResp.Value      = 75;
        GSliderCreat.Value     = 75;
        GPeerCommentBox.Text   = string.Empty;
        UpdateGAveragePreview();
        PeerFormCard.Visibility = Visibility.Visible;
    }

    private void ClosePeerForm_Click(object sender, RoutedEventArgs e)
    {
        PeerFormCard.Visibility = Visibility.Collapsed;
        _peerTargetStudentId    = null;
    }

    private void GSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (sender is not Slider sl) return;
        string v = ((int)sl.Value).ToString();
        switch (sl.Tag?.ToString())
        {
            case "Contrib": if (GContribScore != null) GContribScore.Text = v; break;
            case "Coop":    if (GCoopScore    != null) GCoopScore.Text    = v; break;
            case "Resp":    if (GRespScore    != null) GRespScore.Text    = v; break;
            case "Creat":   if (GCreatScore   != null) GCreatScore.Text   = v; break;
        }
        UpdateGAveragePreview();
    }

    private void UpdateGAveragePreview()
    {
        if (GSliderContrib == null || GAveragePreview == null) return;
        double avg = (GSliderContrib.Value + GSliderCoop.Value + GSliderResp.Value + GSliderCreat.Value) / 4.0;
        GAveragePreview.Text = avg.ToString("F1");
    }

    private async void GSubmitPeer_Click(object sender, RoutedEventArgs e)
    {
        if (_peerTargetStudentId == null) return;
        var target = _groupMembers.FirstOrDefault(m => m.StudentId == _peerTargetStudentId);
        if (target == null) return;

        var eval = new PeerEvaluation
        {
            EvaluatorId   = CurrentStudentId,
            EvaluatorName = App.Session.IsSignedIn && !string.IsNullOrWhiteSpace(App.Session.DisplayName)
                                ? App.Session.DisplayName : CurrentStudentId,
            EvaluateeId   = _peerTargetStudentId,
            EvaluateeName = target.StudentName,
            AssignmentId  = _gradingAssignmentId,
            AssignmentTitle = _assignments.FirstOrDefault(a => a.Id == _gradingAssignmentId).Title ?? "",
            GroupId = "GRP-01",
            CriteriaContribution   = GSliderContrib.Value,
            CriteriaCooperation    = GSliderCoop.Value,
            CriteriaResponsibility = GSliderResp.Value,
            CriteriaCreativity     = GSliderCreat.Value,
            Comment = GPeerCommentBox.Text.Trim(),
        };

        bool ok = await _grading.SubmitPeerEvaluationAsync(eval);

        GradingInfoBar.Severity = ok ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        GradingInfoBar.Title   = ok ? "Penilaian Terkirim" : "Gagal";
        GradingInfoBar.Message = ok
            ? $"Kamu telah menilai {target.StudentName} dengan skor {eval.AverageScore:F1}."
            : "Terjadi kesalahan, coba lagi.";
        GradingInfoBar.IsOpen = true;

        if (ok)
        {
            PeerFormCard.Visibility = Visibility.Collapsed;
            _peerTargetStudentId = null;
            await RefreshPeerMembersAsync();
        }
    }

    private void StudentGradingPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (StudentGradingPivot.SelectedIndex)
        {
            case 1: _ = RefreshReceivedScoresAsync();    break;
            case 2: _ = RefreshStudentActivitiesAsync(); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DOSEN/ASISTEN — lecturer grading
    // ═══════════════════════════════════════════════════════════════════

    private async System.Threading.Tasks.Task LoadLecturerGradingAsync()
    {
        await RefreshLecSummaryAsync();
        await RefreshLecPeerAsync();
        await RefreshLecSystemAsync();
        await RefreshLecActivitiesAsync();
    }

    private async System.Threading.Tasks.Task RefreshLecSummaryAsync()
    {
        var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_gradingAssignmentId);
        GLecStatTotal.Text  = summaries.Count.ToString();
        GLecStatGraded.Text = summaries.Count(s => s.LecturerScore.HasValue).ToString();
        var withFinal = summaries.Where(s => s.FinalScore.HasValue).ToList();
        GLecStatAvg.Text  = withFinal.Any() ? withFinal.Average(s => s.FinalScore!.Value).ToString("F1") : "—";
        GLecStatPeer.Text = summaries.Count(s => s.PeerScore.HasValue).ToString();
        GLecSummaryRepeater.ItemsSource = summaries.Select(s => new GLecSummaryVm(s)).ToList();
    }

    private async System.Threading.Tasks.Task RefreshLecPeerAsync()
    {
        var evals = await _grading.GetPeerEvaluationsByAssignmentAsync(_gradingAssignmentId);
        GLecPeerRepeater.ItemsSource = evals
            .OrderByDescending(e => e.EvaluatedAt)
            .Select(e => new GLecPeerVm
            {
                EvaluatorName  = e.EvaluatorName,
                EvaluateeName  = e.EvaluateeName,
                ScoreStr       = e.Score.ToString("F1"),
                Comment        = string.IsNullOrWhiteSpace(e.Comment) ? "(Tidak ada komentar)" : e.Comment,
                EvaluatedAtStr = e.EvaluatedAt.ToString("dd MMM, HH:mm"),
                SubScoresStr   = $"K:{e.CriteriaContribution:F0}  T:{e.CriteriaCooperation:F0}  J:{e.CriteriaResponsibility:F0}  C:{e.CriteriaCreativity:F0}",
            }).ToList();
    }

    private async System.Threading.Tasks.Task RefreshLecSystemAsync()
    {
        var evals = await _grading.GetSystemEvaluationsByAssignmentAsync(_gradingAssignmentId);
        GLecSystemRepeater.ItemsSource = evals
            .OrderByDescending(e => e.Score)
            .Select(e => new GLecSystemVm
            {
                StudentId   = e.StudentId,
                StudentName = e.StudentName,
                ScoreStr    = e.Score.ToString("F1"),
                CommitStr   = $"Submit: {e.CommitsCount}",
                FileStr     = $"File: {e.FilesModified}",
                TaskStr     = $"Task: {e.TasksCompleted}/{e.TasksTotal}",
                OnTimeStr   = e.SubmittedOnTime ? "Tepat Waktu" : "Terlambat",
                OnTimeBg    = new SolidColorBrush(e.SubmittedOnTime
                    ? Windows.UI.Color.FromArgb(30, 16, 124, 16)
                    : Windows.UI.Color.FromArgb(30, 200, 40, 40)),
                OnTimeFg    = new SolidColorBrush(e.SubmittedOnTime
                    ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
                    : Windows.UI.Color.FromArgb(255, 200, 40, 40)),
            }).ToList();
    }

    private async System.Threading.Tasks.Task RefreshLecActivitiesAsync()
    {
        var acts = await _grading.GetGroupActivitiesAsync(_gradingAssignmentId, 50);
        GLecActivitiesRepeater.ItemsSource = acts.Select(a => new GLecActivityVm
        {
            StudentName   = a.StudentName,
            ActivityType  = a.ActivityType,
            Description   = a.Description,
            ActivityIcon  = a.ActivityIcon,
            TimeAgo       = a.TimeAgo,
            AutoScoreStr  = a.AutoScore.HasValue ? $"+{a.AutoScore:F0} poin" : "—",
        }).ToList();
    }

    private async void GLecShowDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? studentId = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(studentId)) return;

        var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_gradingAssignmentId);
        string studentName = summaries.FirstOrDefault(s => s.StudentId == studentId)?.StudentName
                             ?? studentId;

        await StudentDetailDialog.ShowAsync(XamlRoot, studentId, studentName, _gradingAssignmentId);
    }

    private async void GLecGradeStudent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _lecGradingStudentId = btn.Tag?.ToString();
        var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_gradingAssignmentId);
        var s = summaries.FirstOrDefault(x => x.StudentId == _lecGradingStudentId);
        if (s == null) return;

        var existing = await _grading.GetLecturerGradeForStudentAsync(_lecGradingStudentId!, _gradingAssignmentId);

        double pres = existing?.ScorePresentation  ?? 75;
        double rep  = existing?.ScoreReport        ?? 75;
        double impl = existing?.ScoreImplementation ?? 75;
        double def  = existing?.ScoreDefense       ?? 75;
        string fb   = existing?.Feedback           ?? "";
        bool fin    = existing?.IsFinalized        ?? false;

        // Build dialog programmatically so XamlRoot is available
        var nbPres = new NumberBox { Minimum = 0, Maximum = 100, Value = pres, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        var nbRep  = new NumberBox { Minimum = 0, Maximum = 100, Value = rep,  SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        var nbImpl = new NumberBox { Minimum = 0, Maximum = 100, Value = impl, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        var nbDef  = new NumberBox { Minimum = 0, Maximum = 100, Value = def,  SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1 };
        var fbBox  = new TextBox   { Text = fb, PlaceholderText = "Tuliskan catatan atau masukan...", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 70, MaxLength = 1000 };
        var finCb  = new CheckBox  { Content = "Finalisasi nilai (tidak dapat diubah mahasiswa)", IsChecked = fin };

        var formGrid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        formGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddCell(UIElement el, int row, int col, string label)
        {
            var sp = new StackPanel { Spacing = 4 };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            sp.Children.Add(el);
            Grid.SetRow(sp, row); Grid.SetColumn(sp, col);
            formGrid.Children.Add(sp);
        }
        AddCell(nbPres, 0, 0, "Nilai Presentasi");
        AddCell(nbRep,  0, 1, "Nilai Laporan");
        AddCell(nbImpl, 1, 0, "Nilai Implementasi");
        AddCell(nbDef,  1, 1, "Nilai Defence / Ujian");

        var fbHeader = new TextBlock { Text = "Feedback untuk Mahasiswa", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) };

        var content = new StackPanel { Width = 400, Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"{s.StudentName} — {s.AssignmentTitle}", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = Resources.TryGetValue("CardStrokeColorDefaultBrush", out var strokeBrush) ? (Brush)strokeBrush : new SolidColorBrush(Colors.Gray) });
        content.Children.Add(formGrid);
        content.Children.Add(fbHeader);
        content.Children.Add(fbBox);
        content.Children.Add(finCb);

        var dialog = new ContentDialog
        {
            Title = "Beri Nilai Mahasiswa",
            Content = content,
            PrimaryButtonText = "Simpan Nilai",
            CloseButtonText = "Batal",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        double avg = (nbPres.Value + nbRep.Value + nbImpl.Value + nbDef.Value) / 4.0;
        var grade = new LecturerGrade
        {
            LecturerId   = App.Session.IsSignedIn ? App.Session.Username : "LEC001",
            LecturerName = App.Session.IsSignedIn && !string.IsNullOrWhiteSpace(App.Session.DisplayName)
                               ? App.Session.DisplayName : "Dosen Pengampu",
            StudentId    = _lecGradingStudentId,
            StudentName  = s.StudentName,
            AssignmentId = _gradingAssignmentId,
            AssignmentTitle = _assignments.FirstOrDefault(a => a.Id == _gradingAssignmentId).Title ?? "",
            GroupId      = "GRP-01",
            ScorePresentation  = nbPres.Value,
            ScoreReport        = nbRep.Value,
            ScoreImplementation = nbImpl.Value,
            ScoreDefense       = nbDef.Value,
            Score      = avg,
            Feedback   = fbBox.Text.Trim(),
            IsFinalized = finCb.IsChecked == true,
        };

        await _grading.SaveLecturerGradeAsync(grade);
        await LoadLecturerGradingAsync();
    }

    private void LecturerGradingPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (LecturerGradingPivot.SelectedIndex)
        {
            case 0: _ = RefreshLecSummaryAsync();    break;
            case 1: _ = RefreshLecPeerAsync();       break;
            case 2: _ = RefreshLecSystemAsync();     break;
            case 3: _ = RefreshLecActivitiesAsync(); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MANAGE STUDENTS dialog
    // ═══════════════════════════════════════════════════════════════════

    private async void ManageStudents_Click(object sender, RoutedEventArgs e)
    {
        await StudentService.Instance.EnsureLoadedAsync();
        var students = StudentService.Instance.GetAll()
            .Select(s => new StudentEditVm { Id = s.Id, Name = s.Name, GroupId = s.GroupId ?? "" })
            .ToList();

        var listView = new ListView
        {
            SelectionMode = ListViewSelectionMode.None,
            ItemsSource   = students,
            ItemTemplate  = BuildStudentItemTemplate(),
            Height        = 280,
        };

        var addIdBox   = new TextBox   { PlaceholderText = "ID (mis. STU006)", MinWidth = 100 };
        var addNameBox = new TextBox   { PlaceholderText = "Nama Lengkap", MinWidth = 180, Margin = new Thickness(8, 0, 0, 0) };
        var addGroupBox = new TextBox  { PlaceholderText = "Grup (mis. GRP-01)", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var addBtn     = new Button    { Content = "Tambah", Margin = new Thickness(8, 0, 0, 0) };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(addIdBox);
        addRow.Children.Add(addNameBox);
        addRow.Children.Add(addGroupBox);
        addRow.Children.Add(addBtn);

        var content = new StackPanel { Width = 500, Spacing = 0 };
        content.Children.Add(new TextBlock { Text = "Daftar Mahasiswa (klik nama untuk edit)", FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(listView);
        content.Children.Add(addRow);

        var infoBar = new InfoBar { IsClosable = false, Margin = new Thickness(0, 6, 0, 0) };
        content.Children.Add(infoBar);

        var dialog = new ContentDialog
        {
            Title             = "Kelola Mahasiswa",
            Content           = content,
            PrimaryButtonText = "Simpan",
            CloseButtonText   = "Batal",
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };

        addBtn.Click += (_, _) =>
        {
            string newId   = addIdBox.Text.Trim();
            string newName = addNameBox.Text.Trim();
            string newGrp  = addGroupBox.Text.Trim();
            if (string.IsNullOrEmpty(newId) || string.IsNullOrEmpty(newName))
            {
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.Title   = "ID dan Nama wajib diisi.";
                infoBar.IsOpen  = true;
                return;
            }
            if (students.Any(s => s.Id == newId))
            {
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.Title   = $"ID '{newId}' sudah ada.";
                infoBar.IsOpen  = true;
                return;
            }
            students.Add(new StudentEditVm { Id = newId, Name = newName, GroupId = newGrp });
            listView.ItemsSource = null;
            listView.ItemsSource = students;
            addIdBox.Text = addNameBox.Text = addGroupBox.Text = "";
            infoBar.IsOpen = false;
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        // Save all students that have non-empty names (removes blanked-out entries)
        var toSave = students
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new StudentInfo { Id = s.Id, Name = s.Name.Trim(), GroupId = string.IsNullOrWhiteSpace(s.GroupId) ? null : s.GroupId.Trim() });
        await StudentService.Instance.ReplaceAllAsync(toSave);

        // Refresh grading section with updated names
        await LoadGradingSectionAsync();
    }

    private static DataTemplate BuildStudentItemTemplate() =>
        (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid ColumnSpacing="8" Margin="0,2">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="90"/>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="80"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column="0" Text="{Binding Id}" VerticalAlignment="Center"
                               Foreground="{ThemeResource TextFillColorSecondaryBrush}" FontSize="12"/>
                    <TextBox Grid.Column="1" Text="{Binding Name, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                             PlaceholderText="Nama mahasiswa"/>
                    <TextBox Grid.Column="2" Text="{Binding GroupId, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                             PlaceholderText="Grup"/>
                </Grid>
            </DataTemplate>
            """);

    // ═══════════════════════════════════════════════════════════════════
    //  Gauge & helpers
    // ═══════════════════════════════════════════════════════════════════

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

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return name[..Math.Min(2, name.Length)].ToUpper();
        return $"{parts[0][0]}{parts[1][0]}".ToUpper();
    }
}

// ══════════════════════════════════════════════════════════════════════
//  Task row VM (unchanged)
// ══════════════════════════════════════════════════════════════════════

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

// ══════════════════════════════════════════════════════════════════════
//  Grading view models
// ══════════════════════════════════════════════════════════════════════

public class GradingGroupMemberVm
{
    public string StudentId  { get; set; }
    public string StudentName { get; set; }
    public string Initials => GetInitials(StudentName);
    public bool   AlreadyRated { get; set; }
    public Visibility AlreadyRatedVisibility { get; set; } = Visibility.Collapsed;

    public GradingGroupMemberVm(string id, string name) { StudentId = id; StudentName = name; }

    private static string GetInitials(string name)
    {
        var p = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length == 0) return "?";
        if (p.Length == 1) return name[..Math.Min(2, name.Length)].ToUpper();
        return $"{p[0][0]}{p[1][0]}".ToUpper();
    }
}

public class GradingReceivedEvalVm
{
    public string EvaluatorInitials { get; set; } = "";
    public string EvaluatorName     { get; set; } = "";
    public string Comment           { get; set; } = "";
    public string ScoreStr          { get; set; } = "";
    public string EvaluatedAtStr    { get; set; } = "";
}

internal class GLecSummaryVm
{
    public string StudentId      { get; }
    public string StudentName    { get; }
    public string GroupName      { get; }
    public string PeerScoreStr   { get; }
    public string SystemScoreStr { get; }
    public string LecturerScoreStr { get; }
    public string FinalScoreStr  { get; }
    public string LetterGrade    { get; }
    public SolidColorBrush GradeBadgeColor { get; }

    public GLecSummaryVm(StudentGradeSummary s)
    {
        StudentId        = s.StudentId;
        StudentName      = s.StudentName;
        GroupName        = s.GroupName;
        PeerScoreStr     = s.PeerScore.HasValue     ? s.PeerScore.Value.ToString("F1")     : "—";
        SystemScoreStr   = s.SystemScore.HasValue   ? s.SystemScore.Value.ToString("F1")   : "—";
        LecturerScoreStr = s.LecturerScore.HasValue ? s.LecturerScore.Value.ToString("F1") : "—";
        FinalScoreStr    = s.FinalScore.HasValue    ? s.FinalScore.Value.ToString("F1")    : "—";
        LetterGrade      = s.LetterGrade;
        GradeBadgeColor  = new SolidColorBrush(s.LetterGrade switch
        {
            "A" or "A-"           => Windows.UI.Color.FromArgb(255, 16, 124, 16),
            "B+" or "B" or "B-"  => Windows.UI.Color.FromArgb(255, 0, 120, 212),
            "C+" or "C"           => Windows.UI.Color.FromArgb(255, 200, 130, 0),
            _                     => Windows.UI.Color.FromArgb(255, 196, 43, 28),
        });
    }
}

internal class GLecPeerVm
{
    public string EvaluatorName  { get; set; } = "";
    public string EvaluateeName  { get; set; } = "";
    public string ScoreStr       { get; set; } = "";
    public string Comment        { get; set; } = "";
    public string EvaluatedAtStr { get; set; } = "";
    public string SubScoresStr   { get; set; } = "";
}

internal class GLecSystemVm
{
    public string StudentId   { get; set; } = "";
    public string StudentName { get; set; } = "";
    public string ScoreStr    { get; set; } = "";
    public string CommitStr   { get; set; } = "";
    public string FileStr     { get; set; } = "";
    public string TaskStr     { get; set; } = "";
    public string OnTimeStr   { get; set; } = "";
    public SolidColorBrush? OnTimeBg { get; set; }
    public SolidColorBrush? OnTimeFg { get; set; }
}

internal class GLecActivityVm
{
    public string StudentName  { get; set; } = "";
    public string ActivityType { get; set; } = "";
    public string Description  { get; set; } = "";
    public string ActivityIcon { get; set; } = "";
    public string TimeAgo      { get; set; } = "";
    public string AutoScoreStr { get; set; } = "";
}

/// <summary>Editable row used inside the Kelola Mahasiswa dialog.</summary>
public class StudentEditVm : System.ComponentModel.INotifyPropertyChanged
{
    public string Id { get; set; } = "";

    private string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name))); }
    }

    private string _groupId = "";
    public string GroupId
    {
        get => _groupId;
        set { _groupId = value; PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(GroupId))); }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
