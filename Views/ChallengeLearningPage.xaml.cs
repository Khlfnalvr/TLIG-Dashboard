using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Pickers;
using Windows.UI;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using WinRT.Interop;

namespace TLIGDashboard.Views;

public sealed partial class ChallengeLearningPage : Page
{
    // ── State ────────────────────────────────────────────────────────────────
    private readonly ChallengeService _service = ChallengeService.Instance;
    private bool   _isAdmin;
    private string _studentId   = "";
    private string _studentName = "";

    private Challenge?      _selected;         // challenge in right panel
    private Challenge?      _editing;          // challenge being created/edited
    private List<ChallengeTask> _editTasks = new(); // task editor state
    private string? _attachPath, _attachName;
    private ChallengeSubmission? _selectedSubmission;

    public static event Action? GradeSaved;

    // Colour constants
    private static readonly SolidColorBrush Purple = Brush("#7c3aed");
    private static readonly SolidColorBrush Orange = Brush("#f59e0b");
    private static readonly SolidColorBrush Green  = Brush("#10b981");
    private static readonly SolidColorBrush Red    = Brush("#ef4444");
    private static readonly SolidColorBrush Muted  = Brush("#8080a0");
    private static readonly SolidColorBrush Surface = Brush("#1e1e2e");

    private static SolidColorBrush Brush(string hex)
    {
        hex = hex.TrimStart('#');
        return new SolidColorBrush(Color.FromArgb(255,
            Convert.ToByte(hex[0..2], 16),
            Convert.ToByte(hex[2..4], 16),
            Convert.ToByte(hex[4..6], 16)));
    }

    // ── Init ─────────────────────────────────────────────────────────────────
    public ChallengeLearningPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        // Set NumberBox defaults (cannot be set in XAML in WinUI 3 1.8)
        WeightDosenBox.Value = 50;
        WeightAiBox.Value    = 30;
        WeightPeerBox.Value  = 20;
        ApplySession();
        RefreshList();
        ActivityStore.Instance.Changed += OnActivityChanged;
        Unloaded += (_, _) => ActivityStore.Instance.Changed -= OnActivityChanged;
    }

    private void OnActivityChanged()
        => DispatcherQueue.TryEnqueue(() => { if (_selected != null && !_isAdmin) RefreshMyActivityLog(); });

    private void ApplySession()
    {
#if CLIENT
        _isAdmin = false;
#else
        _isAdmin = App.Session.IsStaff || !App.Session.IsSignedIn;
#endif
        _studentId   = App.Session.Username.Length > 0 ? App.Session.Username : "DEMO_S";
        _studentName = App.Session.DisplayName.Length > 0 ? App.Session.DisplayName : "Demo Student";

        ModeLabel.Text = _isAdmin ? "Mode: Admin / Dosen" : $"Mode: Mahasiswa — {_studentName}";

        NewChallengeBtn.Visibility  = _isAdmin ? Visibility.Visible  : Visibility.Collapsed;
        StudentListHint.Visibility  = _isAdmin ? Visibility.Collapsed : Visibility.Visible;

        if (!_isAdmin) ShowStudentOverview();
        else           ShowEmpty();
    }

    // ════════════════════════════════════════════════════════════
    //  LIST
    // ════════════════════════════════════════════════════════════

    private void RefreshList()
    {
        var all = _service.GetAllChallenges();
        var items = _isAdmin
            ? all.ToList()
            : all.Where(c => c.Status == ChallengeStatus.Active).ToList();

        ChallengeList.ItemsSource = null;
        ChallengeList.ItemsSource = items;

        // Apply badges after layout pass
        ChallengeList.UpdateLayout();
        foreach (var container in items.Select((c, i) =>
            ChallengeList.ContainerFromIndex(i) as ListViewItem))
        {
            if (container == null) continue;
            var ch = items[ChallengeList.Items.IndexOf(
                ChallengeList.ItemFromContainer(container))];

            var titleBlock = FindChild<TextBlock>(container, "ItemTitle");
            var metaBlock  = FindChild<TextBlock>(container, "ItemMeta");
            var badge      = FindChild<Border>(container, "StatusBadge");
            var badgeText  = FindChild<TextBlock>(container, "StatusText");

            if (titleBlock != null) titleBlock.Text = ch.Title;
            if (metaBlock  != null) metaBlock.Text  =
                $"{ch.SystemLabel} · {ch.Tasks.Count} task{(ch.Tasks.Count != 1 ? "s" : "")}";

            if (badge != null && badgeText != null)
                ApplyStatusBadge(badge, badgeText, ch.Status);
        }
    }

    private static void ApplyStatusBadge(Border b, TextBlock t, ChallengeStatus s)
    {
        (b.Background, t.Foreground, t.Text) = s switch
        {
            ChallengeStatus.Active => (Brush("#0d2e1e"), Green,  "Aktif"),
            ChallengeStatus.Draft  => (Brush("#2e2a0d"), Orange, "Draft"),
            ChallengeStatus.Closed => (Brush("#2e0d0d"), Red,    "Ditutup"),
            _                      => (Brush("#1e1e2e"), Muted,  "—"),
        };
    }

    private void ChallengeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ChallengeList.SelectedItem is not Challenge ch) return;
        _selected = ch;
        if (_isAdmin) ShowAdminDetail(ch);
        else          ShowStudentDetail(ch);
    }

    // ════════════════════════════════════════════════════════════
    //  PANEL VISIBILITY
    // ════════════════════════════════════════════════════════════

    private void ShowEmpty()
    {
        EmptyState.Visibility         = Visibility.Visible;
        AdminFormPanel.Visibility     = Visibility.Collapsed;
        AdminDetailPanel.Visibility   = Visibility.Collapsed;
        StudentOverviewPanel.Visibility = Visibility.Collapsed;
        StudentDetailPanel.Visibility = Visibility.Collapsed;

        EmptyHint.Text = _isAdmin
            ? "Pilih challenge atau klik \"+ Buat Challenge\"."
            : "Pilih challenge aktif dari daftar.";
    }

    private void ShowAdminDetail(Challenge ch)
    {
        EmptyState.Visibility         = Visibility.Collapsed;
        AdminFormPanel.Visibility     = Visibility.Collapsed;
        AdminDetailPanel.Visibility   = Visibility.Visible;
        StudentOverviewPanel.Visibility = Visibility.Collapsed;
        StudentDetailPanel.Visibility = Visibility.Collapsed;

        DetailTitle.Text = ch.Title;
        DetailMeta.Text  =
            $"{ch.SystemLabel} · Deadline: {(ch.Deadline.HasValue ? ch.Deadline.Value.ToString("dd MMM yyyy") : "—")} " +
            $"· Bobot: Dosen {ch.WeightDosen}% / AI {ch.WeightAI}% / Peer {ch.WeightPeer}%";

        bool isDraft  = ch.Status == ChallengeStatus.Draft;
        bool isActive = ch.Status == ChallengeStatus.Active;
        PublishBtn.Visibility = isDraft  ? Visibility.Visible : Visibility.Collapsed;
        CloseBtn.Visibility   = isActive ? Visibility.Visible : Visibility.Collapsed;

        // Task summary
        AdminTaskSummary.Children.Clear();
        foreach (var t in ch.Tasks)
        {
            var row = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 6, 10, 6)
            };
            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 2 };
            left.Children.Add(new TextBlock { Text = t.Name, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Brush("#e0e0f0") });
            if (!string.IsNullOrEmpty(t.Description))
                left.Children.Add(new TextBlock { Text = t.Description, FontSize = 11, Foreground = Muted });
            Grid.SetColumn(left, 0);

            if (t.HasMetricTarget)
            {
                var badge = new Border
                {
                    Background = Brush("#2a1a0e"), CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 3, 8, 3), VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock { Text = t.FormatTarget(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Orange };
                Grid.SetColumn(badge, 1);
                g.Children.Add(badge);
            }
            g.Children.Add(left);

            // Wrap card in a StackPanel so we can append activity log below task header
            var taskStack = new StackPanel { Spacing = 6 };
            taskStack.Children.Add(g);


            row.Child = taskStack;
            AdminTaskSummary.Children.Add(row);
        }
        if (ch.Tasks.Count == 0)
            AdminTaskSummary.Children.Add(new TextBlock { Text = "Tidak ada task.", FontSize = 11, Foreground = Muted });

        // Student list
        StudentDetailCard.Visibility = Visibility.Collapsed;
        _selectedSubmission = null;
        _ = RebuildStudentListAsync(ch);
    }

    private async System.Threading.Tasks.Task RebuildStudentListAsync(Challenge ch)
    {
        StudentListRows.Children.Clear();

        var subs = ch.Submissions
            .Where(s => s.Status != SubmissionStatus.NotSubmitted)
            .ToList();

        NoStudentText.Visibility = subs.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var sub in subs)
        {
            bool graded = sub.DosenGrade != null;

            var card = new Border
            {
                Background      = Brush("#1a1a2a"),
                BorderBrush     = graded ? Brush("#1a3a2a") : Brush("#2d2d3e"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(12, 10, 12, 10)
            };

            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameStack = new StackPanel { Spacing = 2 };
            nameStack.Children.Add(new TextBlock
            {
                Text = sub.StudentName.Length > 0 ? sub.StudentName : sub.StudentId,
                FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("#e0e0f0")
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = $"Submit: {sub.SubmittedAt:dd MMM yyyy, HH:mm}",
                FontSize = 10, Foreground = Muted
            });
            Grid.SetColumn(nameStack, 0);

            if (graded)
            {
                var gradedBadge = MakeBadge($"Dinilai: {sub.DosenGrade!.Score:F0}", "#0d2e1e", Green);
                gradedBadge.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(gradedBadge, 1);
                g.Children.Add(gradedBadge);
            }

            var viewBtn = new Button
            {
                Content = "Lihat →", FontSize = 10,
                CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 4, 8, 4),
                Background = Purple, Foreground = Brush("#ffffff"),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(viewBtn, 2);

            var capturedSub = sub;
            viewBtn.Click += (_, _) => ShowStudentGradingDetail(capturedSub);

            g.Children.Add(nameStack);
            g.Children.Add(viewBtn);
            card.Child = g;
            StudentListRows.Children.Add(card);
        }

        await System.Threading.Tasks.Task.CompletedTask;
    }

    private void ShowStudentGradingDetail(ChallengeSubmission sub)
    {
        _selectedSubmission = sub;
        StudentDetailCard.Visibility = Visibility.Visible;

        SelectedStudentName.Text = sub.StudentName.Length > 0 ? sub.StudentName : sub.StudentId;
        SelectedStudentMeta.Text = !string.IsNullOrEmpty(sub.TextAnswer)
            ? $"Submit: {sub.SubmittedAt:dd MMM yyyy, HH:mm} · Status: {sub.Status}"
            : "Belum ada jawaban tertulis.";

        // Aktivitas mahasiswa — hanya sebelum/sampai waktu submit
        SelectedStudentActivity.Children.Clear();
        DateTime cutoffUtc = sub.Status == SubmissionStatus.NotSubmitted
            ? DateTime.UtcNow
            : sub.SubmittedAt.Kind == DateTimeKind.Utc
                ? sub.SubmittedAt
                : sub.SubmittedAt.ToUniversalTime();

        var logs = ActivityStore.Instance.GetAll()
            .Where(a =>
                (a.Category == ActivityCategory.ControlParameter
                 || a.Category == ActivityCategory.AIInteraction
                 || a.Category == ActivityCategory.TaskSubmission)
                && (!string.IsNullOrEmpty(a.Username) && a.Username == sub.StudentId
                    || !string.IsNullOrEmpty(a.DisplayName) && a.DisplayName == sub.StudentName)
                && a.TimestampUtc <= cutoffUtc)
            .OrderByDescending(a => a.TimestampUtc)
            .Take(50)
            .ToList();

        NoSelectedStudentActivity.Visibility = logs.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        // Summary badges
        if (logs.Count > 0)
        {
            int paramCount = logs.Count(a => a.Category == ActivityCategory.ControlParameter);
            int aiCount    = logs.Count(a => a.Category == ActivityCategory.AIInteraction);
            int subCount   = logs.Count(a => a.Category == ActivityCategory.TaskSubmission);
            var badgeRow   = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 4) };
            if (paramCount > 0) badgeRow.Children.Add(MakeBadge($"Parameter ×{paramCount}", "#1a2e1a", Green));
            if (aiCount    > 0) badgeRow.Children.Add(MakeBadge($"AI Prompt ×{aiCount}",   "#1a1a2e", Purple));
            if (subCount   > 0) badgeRow.Children.Add(MakeBadge($"Submit ×{subCount}",      "#2e2a0d", Orange));
            SelectedStudentActivity.Children.Add(badgeRow);
        }

        foreach (var log in logs)
            SelectedStudentActivity.Children.Add(BuildCompactLogRow(log));

        // Pre-fill form penilaian
        GradeScoreBox.Value    = sub.DosenGrade?.Score ?? 0;
        GradeFeedbackBox.Text  = sub.DosenGrade?.Feedback ?? "";
        GradeSaveStatus.Text   = sub.DosenGrade != null
            ? $"✓ Sudah dinilai pada {sub.DosenGrade.GradedAt:dd MMM yyyy}"
            : "";
        GradeSaveStatus.Foreground = Green;
    }

    private void BackToStudentListBtn_Click(object sender, RoutedEventArgs e)
    {
        StudentDetailCard.Visibility = Visibility.Collapsed;
        _selectedSubmission = null;
    }

    private void SaveGradeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedSubmission == null || _selected == null) return;

        double score = double.IsNaN(GradeScoreBox.Value) ? 0 : GradeScoreBox.Value;
        score = Math.Max(0, Math.Min(100, score));
        string lecName = App.Session.DisplayName.Length > 0 ? App.Session.DisplayName : "Dosen";

        // Jika submission belum ada di challenge, tambahkan dulu
        if (!_selected.Submissions.Contains(_selectedSubmission))
        {
            _selectedSubmission.ChallengeId = _selected.Id;
            _selectedSubmission.Status      = SubmissionStatus.Graded;
            _selected.Submissions.Add(_selectedSubmission);
        }

        _selectedSubmission.DosenGrade = new GradeEntry
        {
            GraderName = lecName,
            Score      = score,
            Feedback   = GradeFeedbackBox.Text.Trim(),
            GradedAt   = DateTime.Now,
            IsAI       = false
        };
        _selectedSubmission.Status = SubmissionStatus.Graded;
        _service.UpdateChallenge(_selected);

        GradeSaveStatus.Text       = $"✓ Nilai {score:F0} berhasil disimpan!";
        GradeSaveStatus.Foreground = Green;

        // Refresh student list untuk update badge
        _ = RebuildStudentListAsync(_selected);

        // Notify Penilaian section
        GradeSaved?.Invoke();
    }

    private Border MakeBadge(string text, string bg, Brush fg)
    {
        var b = new Border
        {
            Background    = Brush(bg),
            CornerRadius  = new CornerRadius(4),
            Padding       = new Thickness(6, 2, 6, 2)
        };
        b.Child = new TextBlock { Text = text, FontSize = 9, Foreground = fg,
                                  FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        return b;
    }

    private void ShowStudentOverview()
    {
        EmptyState.Visibility           = Visibility.Collapsed;
        AdminFormPanel.Visibility       = Visibility.Collapsed;
        AdminDetailPanel.Visibility     = Visibility.Collapsed;
        StudentDetailPanel.Visibility   = Visibility.Collapsed;
        StudentOverviewPanel.Visibility = Visibility.Visible;
    }

    private void ShowStudentDetail(Challenge ch)
    {
        EmptyState.Visibility         = Visibility.Collapsed;
        AdminFormPanel.Visibility     = Visibility.Collapsed;
        AdminDetailPanel.Visibility   = Visibility.Collapsed;
        StudentOverviewPanel.Visibility = Visibility.Collapsed;
        StudentDetailPanel.Visibility = Visibility.Visible;

        StDetailTitle.Text = ch.Title;
        StSystemText.Text  = ch.SystemLabel;
        StDetailMeta.Text  = $"Deadline: {(ch.Deadline.HasValue ? ch.Deadline.Value.ToString("dd MMM yyyy") : "—")} " +
                             $"· {ch.Tasks.Count} task{(ch.Tasks.Count != 1 ? "s" : "")}";
        StDetailDesc.Text  = ch.Description;
        StDetailInstr.Text = ch.Instructions;

        BuildStudentTaskList(ch);
        RefreshMyActivityLog();

        // Pre-fill if already submitted
        var mySub = ch.Submissions.FirstOrDefault(s => s.StudentId == _studentId);
        SubmitTextBox.Text = mySub?.TextAnswer ?? "";
        AttachLabel.Text   = mySub?.AttachmentFileName ?? "";
        SubmitBtn.Content  = mySub != null ? "Update Jawaban" : "Kirim Jawaban";
        SubmitStatusText.Text = "";

    }

    private void BuildStudentTaskList(Challenge ch)
    {
        StudentTaskList.Children.Clear();
        foreach (var t in ch.Tasks)
        {
            double? live = App.PidMetrics.Get(t.Metric);
            bool?   ok   = t.IsAchieved(live);

            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = ok == true ? Green : ok == false ? Red :
                    (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
            };

            var vstack = new StackPanel { Spacing = 6 };

            // Task name row
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(new TextBlock
            {
                Text = t.Name, FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Brush("#e0e0f0"), VerticalAlignment = VerticalAlignment.Center
            });
            if (ok == true)
                titleRow.Children.Add(new FontIcon
                {
                    Glyph = "", FontSize = 12, Foreground = Green,
                    FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                    VerticalAlignment = VerticalAlignment.Center
                });
            vstack.Children.Add(titleRow);

            if (!string.IsNullOrEmpty(t.Description))
                vstack.Children.Add(new TextBlock { Text = t.Description, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });

            // Metric target + live value
            if (t.HasMetricTarget)
            {
                var metricRow = new Grid { ColumnSpacing = 10 };
                metricRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                metricRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Target
                var targetBorder = new Border
                {
                    Background = Brush("#2a1a0e"), CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10, 6, 10, 6)
                };
                var ts = new StackPanel { Spacing = 2 };
                ts.Children.Add(new TextBlock { Text = "TARGET", FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Orange });
                ts.Children.Add(new TextBlock { Text = t.FormatTarget(), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = Orange });
                targetBorder.Child = ts;
                Grid.SetColumn(targetBorder, 0);

                // Live value
                string liveStr = live.HasValue
                    ? $"{live.Value:0.##} {PidMetricsService.UnitOf(t.Metric)}"
                    : "Jalankan simulasi";
                var liveBorder = new Border
                {
                    Background = ok == true ? Brush("#0d2e1e") : ok == false ? Brush("#2e0d0d") : Brush("#1e1e2e"),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                    BorderThickness = new Thickness(1),
                    BorderBrush = ok == true ? Green : ok == false ? Red : Brush("#2d2d3e")
                };
                var ls = new StackPanel { Spacing = 2 };
                ls.Children.Add(new TextBlock { Text = "NILAI SAAT INI", FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ok == true ? Green : ok == false ? Red : Muted });
                ls.Children.Add(new TextBlock { Text = liveStr, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ok == true ? Green : ok == false ? Red : Muted });
                liveBorder.Child = ls;
                Grid.SetColumn(liveBorder, 1);

                metricRow.Children.Add(targetBorder);
                metricRow.Children.Add(liveBorder);
                vstack.Children.Add(metricRow);
            }

#if CLIENT
            // ── Activity log per task (CLIENT) ───────────────────────────────
            var allLogs      = ActivityStore.Instance.GetAll();
            var paramLogs    = allLogs.Where(a => a.Category == ActivityCategory.ControlParameter).ToList();
            var aiLogs       = allLogs.Where(a => a.Category == ActivityCategory.AIInteraction).ToList();
            var combinedLogs = allLogs
                .Where(a => a.Category is ActivityCategory.ControlParameter or ActivityCategory.AIInteraction)
                .OrderByDescending(a => a.TimestampUtc)
                .Take(10)
                .ToList();
            int paramCount   = paramLogs.Count;
            bool limitReached = paramCount >= 3;

            vstack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
            {
                Height = 1, Fill = Muted, Margin = new Thickness(0, 8, 0, 4), Opacity = 0.25
            });

            var actHeader = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 0, 0, 4) };
            actHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            actHeader.Children.Add(new TextBlock
            {
                Text = "LOG AKTIVITAS", FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = Muted, VerticalAlignment = VerticalAlignment.Center
            });

            var counterBg = limitReached ? Brush("#2e0d0d") : Brush("#1a1a2e");
            var counterFg = limitReached ? Red : Muted;
            var counterBorder = new Border
            {
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
                Background = counterBg, VerticalAlignment = VerticalAlignment.Center
            };
            counterBorder.Child = new TextBlock
            {
                Text = $"{Math.Min(paramCount, 3)}/3 perubahan parameter",
                FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = counterFg
            };
            Grid.SetColumn(counterBorder, 1);
            actHeader.Children.Add(counterBorder);
            vstack.Children.Add(actHeader);

            if (combinedLogs.Count == 0)
                vstack.Children.Add(new TextBlock
                {
                    Text = "Belum ada aktivitas.", FontSize = 10,
                    Foreground = Muted, Margin = new Thickness(0, 2, 0, 2)
                });
            else
                foreach (var log in combinedLogs)
                    vstack.Children.Add(BuildCompactLogRow(log));
#endif

            card.Child = vstack;
            StudentTaskList.Children.Add(card);
        }

        if (ch.Tasks.Count == 0)
            StudentTaskList.Children.Add(new TextBlock { Text = "Challenge ini tidak memiliki task spesifik.", FontSize = 11, Foreground = Muted });
    }

    private void RefreshMyActivityLog()
    {
        MyActivityList.Children.Clear();

        var myLogs = ActivityStore.Instance.GetAll()
            .Where(a => (a.Category == ActivityCategory.ControlParameter
                      || a.Category == ActivityCategory.AIInteraction
                      || a.Category == ActivityCategory.TaskSubmission)
                     && (string.IsNullOrEmpty(a.Username)
                         || a.Username == App.Session.Username
                         || a.Username == _studentId))
            .OrderByDescending(a => a.TimestampUtc)
            .Take(20)
            .ToList();

        NoMyActivityText.Visibility = myLogs.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;

        foreach (var log in myLogs)
            MyActivityList.Children.Add(BuildCompactLogRow(log));
    }

    private Border BuildCompactLogRow(ActivityLog log)
    {
        var g = new Grid { ColumnSpacing = 8 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var timeBlk = new TextBlock
        {
            Text = log.TimestampUtc.ToLocalTime().ToString("HH:mm:ss"),
            FontSize = 9, Foreground = Muted, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(timeBlk, 0);

        var descBlk = new TextBlock
        {
            Text = log.Description, FontSize = 10, Foreground = Brush("#c0c0d0"),
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(descBlk, 1);

        var catBadge = new Border
        {
            CornerRadius = new CornerRadius(3), Padding = new Thickness(5, 1, 5, 1),
            Background = log.Category == ActivityCategory.ControlParameter ? Brush("#1a0e2e") : Brush("#0e1a2e"),
            VerticalAlignment = VerticalAlignment.Center
        };
        catBadge.Child = new TextBlock
        {
            Text = log.Category == ActivityCategory.ControlParameter ? "Param" : "AI",
            FontSize = 9, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = log.Category == ActivityCategory.ControlParameter ? Purple : Brush("#60A5FA")
        };
        Grid.SetColumn(catBadge, 2);

        g.Children.Add(timeBlk);
        g.Children.Add(descBlk);
        g.Children.Add(catBadge);
        return new Border { Child = g, Margin = new Thickness(0, 1, 0, 1) };
    }

    // ════════════════════════════════════════════════════════════
    //  ADMIN: New / Edit Form
    // ════════════════════════════════════════════════════════════

    private void NewChallengeBtn_Click(object sender, RoutedEventArgs e) => OpenForm(null);
    private void EditChallengeBtn_Click(object sender, RoutedEventArgs e) => OpenForm(_selected);
    private void CancelFormBtn_Click(object sender, RoutedEventArgs e)
    {
        _editing = null;
        if (_selected != null) ShowAdminDetail(_selected);
        else ShowEmpty();
    }

    private void OpenForm(Challenge? existing)
    {
        _editing = existing;
        _editTasks = existing?.Tasks.Select(t => new ChallengeTask
        {
            Id = t.Id, Name = t.Name, Description = t.Description,
            Metric = t.Metric, Op = t.Op, TargetValue = t.TargetValue, Tolerance = t.Tolerance
        }).ToList() ?? new();

        FormTitleLabel.Text = existing == null ? "Buat Challenge Baru" : "Edit Challenge";
        FormTitleBox.Text   = existing?.Title       ?? "";
        FormDescBox.Text    = existing?.Description  ?? "";
        FormInstrBox.Text   = existing?.Instructions ?? "";
        WeightDosenBox.Value = existing?.WeightDosen ?? 50;
        WeightAiBox.Value    = existing?.WeightAI    ?? 30;
        WeightPeerBox.Value  = existing?.WeightPeer  ?? 20;
        FormStatusCombo.SelectedIndex = existing?.Status == ChallengeStatus.Active ? 1 : 0;
        FormSystemCombo.SelectedIndex = existing == null ? 0 : (int)existing.TargetSystem;
        FormDeadlinePicker.Date = existing?.Deadline.HasValue == true
            ? new DateTimeOffset(existing.Deadline.Value) : (DateTimeOffset?)null;

        FormStatusText.Text = "";
        RebuildTaskEditor();
        UpdateWeightTotal();

        EmptyState.Visibility         = Visibility.Collapsed;
        AdminFormPanel.Visibility     = Visibility.Visible;
        AdminDetailPanel.Visibility   = Visibility.Collapsed;
        StudentOverviewPanel.Visibility = Visibility.Collapsed;
        StudentDetailPanel.Visibility = Visibility.Collapsed;
    }

    // ── Task editor ──────────────────────────────────────────────────────────

    private void AddTaskBtn_Click(object sender, RoutedEventArgs e)
    {
        _editTasks.Add(new ChallengeTask());
        RebuildTaskEditor();
    }

    private void RebuildTaskEditor()
    {
        TaskListPanel.Children.Clear();
        NoTasksHint.Visibility = _editTasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < _editTasks.Count; i++)
        {
            int idx = i;
            var task = _editTasks[i];

            var card = new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 10, 12, 10),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
            };
            var vs = new StackPanel { Spacing = 8 };

            // Row 1: Name + remove button
            var r1 = new Grid();
            r1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var nameBox = new TextBox { PlaceholderText = "Nama task...", Text = task.Name, FontSize = 11, CornerRadius = new CornerRadius(5) };
            nameBox.TextChanged += (s, _) => task.Name = ((TextBox)s).Text;
            Grid.SetColumn(nameBox, 0);
            var removeBtn = new Button { Content = "✕", FontSize = 10, Padding = new Thickness(6, 4, 6, 4), CornerRadius = new CornerRadius(5), Margin = new Thickness(6, 0, 0, 0) };
            removeBtn.Click += (_, _) => { _editTasks.RemoveAt(idx); RebuildTaskEditor(); };
            Grid.SetColumn(removeBtn, 1);
            r1.Children.Add(nameBox); r1.Children.Add(removeBtn);
            vs.Children.Add(r1);

            // Row 2: Description
            var descBox = new TextBox { PlaceholderText = "Deskripsi task (opsional)...", Text = task.Description, FontSize = 11, CornerRadius = new CornerRadius(5) };
            descBox.TextChanged += (s, _) => task.Description = ((TextBox)s).Text;
            vs.Children.Add(descBox);

            // Row 3: Metric + Op + Target + Tolerance
            var r3 = new Grid { ColumnSpacing = 8 };
            r3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            r3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.6, GridUnitType.Star) });
            r3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            r3.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var metricCombo = new ComboBox { FontSize = 11, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch };
            metricCombo.Items.Add(new ComboBoxItem { Content = "— Tidak ada —", Tag = "" });
            metricCombo.Items.Add(new ComboBoxItem { Content = "Rise Time",         Tag = TaskMetrics.RiseTime });
            metricCombo.Items.Add(new ComboBoxItem { Content = "Overshoot",         Tag = TaskMetrics.Overshoot });
            metricCombo.Items.Add(new ComboBoxItem { Content = "Settling Time",     Tag = TaskMetrics.Settling });
            metricCombo.Items.Add(new ComboBoxItem { Content = "Steady-State Error",Tag = TaskMetrics.SteadyStateError });
            int mIdx = Array.IndexOf(new[]{"",TaskMetrics.RiseTime,TaskMetrics.Overshoot,TaskMetrics.Settling,TaskMetrics.SteadyStateError}, task.Metric);
            metricCombo.SelectedIndex = mIdx >= 0 ? mIdx : 0;
            metricCombo.SelectionChanged += (s, _) => { if (((ComboBox)s).SelectedItem is ComboBoxItem ci && ci.Tag is string t2) task.Metric = t2; };
            Grid.SetColumn(metricCombo, 0);

            var opCombo = new ComboBox { FontSize = 11, CornerRadius = new CornerRadius(5), HorizontalAlignment = HorizontalAlignment.Stretch };
            opCombo.Items.Add(new ComboBoxItem { Content = "≤", Tag = TaskOps.Lte });
            opCombo.Items.Add(new ComboBoxItem { Content = "≥", Tag = TaskOps.Gte });
            opCombo.Items.Add(new ComboBoxItem { Content = "~", Tag = TaskOps.Approx });
            opCombo.SelectedIndex = Array.IndexOf(new[]{TaskOps.Lte,TaskOps.Gte,TaskOps.Approx}, task.Op);
            opCombo.SelectionChanged += (s, _) => { if (((ComboBox)s).SelectedItem is ComboBoxItem ci && ci.Tag is string t2) task.Op = t2; };
            Grid.SetColumn(opCombo, 1);

            var targetBox = new NumberBox { Value = task.TargetValue, Minimum = 0, FontSize = 11, PlaceholderText = "Target", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, CornerRadius = new CornerRadius(5) };
            targetBox.ValueChanged += (s, _) => { if (!double.IsNaN(s.Value)) task.TargetValue = s.Value; };
            Grid.SetColumn(targetBox, 2);

            var tolBox = new NumberBox { Value = task.Tolerance, Minimum = 0, FontSize = 11, PlaceholderText = "± Toleransi", SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, CornerRadius = new CornerRadius(5) };
            tolBox.ValueChanged += (s, _) => { if (!double.IsNaN(s.Value)) task.Tolerance = s.Value; };
            Grid.SetColumn(tolBox, 3);

            r3.Children.Add(metricCombo); r3.Children.Add(opCombo);
            r3.Children.Add(targetBox);   r3.Children.Add(tolBox);
            vs.Children.Add(r3);

            card.Child = vs;
            TaskListPanel.Children.Add(card);
        }
    }

    // ── Weight validator ─────────────────────────────────────────────────────

    private void WeightBox_Changed(NumberBox sender, NumberBoxValueChangedEventArgs e)
        => UpdateWeightTotal();

    private void UpdateWeightTotal()
    {
        int total = (int)(WeightDosenBox.Value + WeightAiBox.Value + WeightPeerBox.Value);
        bool ok = total == 100;
        WeightTotalText.Text = $"Total: {total}%";
        WeightTotalText.Foreground = ok ? Green : Red;
    }

    // ── Save ─────────────────────────────────────────────────────────────────

    private void SaveChallengeBtn_Click(object sender, RoutedEventArgs e)
    {
        string title = FormTitleBox.Text.Trim();
        if (string.IsNullOrEmpty(title))
        {
            FormStatusText.Text = "⚠ Judul tidak boleh kosong.";
            FormStatusText.Foreground = Orange;
            return;
        }
        int wd = (int)WeightDosenBox.Value, wa = (int)WeightAiBox.Value, wp = (int)WeightPeerBox.Value;
        if (wd + wa + wp != 100)
        {
            FormStatusText.Text = "⚠ Total bobot harus = 100%.";
            FormStatusText.Foreground = Red;
            return;
        }

        SimulationType sys = FormSystemCombo.SelectedIndex switch
        {
            1 => SimulationType.Level,
            2 => SimulationType.Temperature,
            _ => SimulationType.Flow
        };

        if (_editing == null)
        {
            var newCh = new Challenge
            {
                Title = title, Description = FormDescBox.Text.Trim(),
                Instructions = FormInstrBox.Text.Trim(),
                TargetSystem = sys,
                Deadline = FormDeadlinePicker.Date?.DateTime,
                WeightDosen = wd, WeightAI = wa, WeightPeer = wp,
                Status = FormStatusCombo.SelectedIndex == 1 ? ChallengeStatus.Active : ChallengeStatus.Draft,
                CreatedByName = App.Session.DisplayName.Length > 0 ? App.Session.DisplayName : "Admin",
                Tasks = _editTasks
            };
            _service.AddChallenge(newCh);
            _selected = newCh;
        }
        else
        {
            _editing.Title = title; _editing.Description = FormDescBox.Text.Trim();
            _editing.Instructions = FormInstrBox.Text.Trim();
            _editing.TargetSystem = sys;
            _editing.Deadline = FormDeadlinePicker.Date?.DateTime;
            _editing.WeightDosen = wd; _editing.WeightAI = wa; _editing.WeightPeer = wp;
            _editing.Status = FormStatusCombo.SelectedIndex == 1 ? ChallengeStatus.Active : ChallengeStatus.Draft;
            _editing.Tasks = _editTasks;
            _service.UpdateChallenge(_editing);
            _selected = _editing;
        }

        _editing = null;
        RefreshList();
        ShowAdminDetail(_selected!);
    }

    // ── Publish / Close / Delete ─────────────────────────────────────────────

    private void PublishBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Status = ChallengeStatus.Active;
        _service.UpdateChallenge(_selected);
        RefreshList();
        ShowAdminDetail(_selected);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        _selected.Status = ChallengeStatus.Closed;
        _service.UpdateChallenge(_selected);
        RefreshList();
        ShowAdminDetail(_selected);
    }

    private async void DeleteChallengeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        bool confirmed = await ShowConfirmAsync("Hapus Challenge",
            $"Yakin ingin menghapus \"{_selected.Title}\"? Data tidak bisa dipulihkan.");
        if (!confirmed) return;
        _service.DeleteChallenge(_selected.Id);
        _selected = null;
        RefreshList();
        ShowEmpty();
    }

    // ════════════════════════════════════════════════════════════
    //  STUDENT: Submit
    // ════════════════════════════════════════════════════════════

    private void RefreshMetricsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected != null) BuildStudentTaskList(_selected);
    }

    private void SubmitBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        string text = SubmitTextBox.Text.Trim();
        if (string.IsNullOrEmpty(text) && _attachPath == null)
        {
            SubmitStatusText.Text = "⚠ Isi jawaban atau lampirkan file terlebih dahulu.";
            SubmitStatusText.Foreground = Orange;
            return;
        }

        // Snapshot live PID metrics
        var snapshot = new System.Collections.Generic.Dictionary<string, double>();
        foreach (var m in new[] { TaskMetrics.RiseTime, TaskMetrics.Overshoot, TaskMetrics.Settling, TaskMetrics.SteadyStateError })
        {
            var v = App.PidMetrics.Get(m);
            if (v.HasValue) snapshot[m] = v.Value;
        }

        var existing = _selected.Submissions.FirstOrDefault(s => s.StudentId == _studentId);
        if (existing != null)
        {
            existing.TextAnswer = text;
            existing.AttachmentPath = _attachPath;
            existing.AttachmentFileName = _attachName;
            existing.MetricSnapshot = snapshot;
            existing.SubmittedAt = DateTime.Now;
            existing.Status = SubmissionStatus.Submitted;
        }
        else
        {
            _service.AddSubmission(_selected.Id, new ChallengeSubmission
            {
                StudentId = _studentId, StudentName = _studentName,
                TextAnswer = text,
                AttachmentPath = _attachPath, AttachmentFileName = _attachName,
                MetricSnapshot = snapshot,
                Status = SubmissionStatus.Submitted
            });
        }

        SubmitStatusText.Text = "✓ Jawaban berhasil dikirim!";
        SubmitStatusText.Foreground = Green;
        SubmitBtn.Content = "Update Jawaban";

        Services.ActivityStore.Instance.LogSession(
            Models.ActivityCategory.TaskSubmission,
            Models.ActivityActions.ChallengeSubmitted,
            $"Submit jawaban challenge: {_selected.Title}",
            relatedId: _selected.Id.ToString(),
            metadata: snapshot.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("F3")));
    }

    private async void AttachFileBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.CurrentWindow!));
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                _attachPath  = file.Path;
                _attachName  = file.Name;
                AttachLabel.Text = file.Name;
            }
        }
        catch { }
    }

    // ════════════════════════════════════════════════════════════
    //  HELPERS
    // ════════════════════════════════════════════════════════════

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Ya",
            CloseButtonText = "Batal",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private static string MetricShortLabel(string m) => m switch
    {
        TaskMetrics.RiseTime         => "RT",
        TaskMetrics.Overshoot        => "OS",
        TaskMetrics.Settling         => "Ts",
        TaskMetrics.SteadyStateError => "SSE",
        _                            => m
    };

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var result = FindChild<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }
}
