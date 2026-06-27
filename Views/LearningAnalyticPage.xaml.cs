using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using TLIGDashboard.Controls;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

public sealed partial class LearningAnalyticPage : Page
{
    private readonly GradingService _grading = GradingService.Instance;
    private string _gradingAssignmentId = "ASGN-001";
    private string? _peerTargetStudentId;
    private string? _lecGradingStudentId;

    private readonly List<(string Id, string Title)> _assignments = new()
    {
        ("ASGN-001", "Tugas Kelompok - Sistem Kontrol PID"),
        ("ASGN-002", "Tugas Kelompok - Heat Exchanger"),
    };

    private List<GradingGroupMemberVm> _groupMembers = new();

    private string CurrentStudentId =>
        App.Session.IsSignedIn ? App.Session.Username : "STU001";

    public LearningAnalyticPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!ChallengeFrame.CanGoBack && ChallengeFrame.Content is null)
            ChallengeFrame.Navigate(typeof(ChallengeLearningPage));

#if CLIENT
        PenilaianDivider.Visibility = Visibility.Collapsed;
        GradingCard.Visibility      = Visibility.Collapsed;
#else
        InitGradingCombo();
        _ = LoadGradingSectionAsync();
        ChallengeLearningPage.GradeSaved += OnChallengeGradeSaved;
        Unloaded += (_, _) => { ChallengeLearningPage.GradeSaved -= OnChallengeGradeSaved; };
#endif
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = AnalyticView.ReloadAsync();
        _ = LoadGradingSectionAsync();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GRADING — init & dispatch by role
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

            _groupMembers = StudentService.Instance.GetAll()
                .Where(s => s.Id != CurrentStudentId)
                .Select(s => new GradingGroupMemberVm(s.Id, s.Name))
                .ToList();

            await LoadStudentGradingAsync();
        }
    }

    internal void GradingAssignmentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GradingAssignmentCombo.SelectedItem is ComboBoxItem item)
        {
            _gradingAssignmentId = item.Tag?.ToString() ?? _gradingAssignmentId;
            _ = LoadGradingSectionAsync();
        }
    }

    internal void GradingRefresh_Click(object sender, RoutedEventArgs e)
        => _ = LoadGradingSectionAsync();

    // ═══════════════════════════════════════════════════════════════════
    //  MAHASISWA — student grading
    // ═══════════════════════════════════════════════════════════════════

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

    internal void NilaiSekarang_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _peerTargetStudentId = btn.Tag?.ToString();
        var target = _groupMembers.FirstOrDefault(m => m.StudentId == _peerTargetStudentId);
        if (target == null) return;

        FormTargetName.Text  = $"Menilai: {target.StudentName}";
        GSliderContrib.Value = 75;
        GSliderCoop.Value    = 75;
        GSliderResp.Value    = 75;
        GSliderCreat.Value   = 75;
        GPeerCommentBox.Text = string.Empty;
        UpdateGAveragePreview();
        PeerFormCard.Visibility = Visibility.Visible;
    }

    internal void ClosePeerForm_Click(object sender, RoutedEventArgs e)
    {
        PeerFormCard.Visibility = Visibility.Collapsed;
        _peerTargetStudentId    = null;
    }

    internal void GSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
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

    internal async void GSubmitPeer_Click(object sender, RoutedEventArgs e)
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

    internal void StudentGradingPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
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
        InitChallengeRecapCombo();
        BuildChallengeRecap();
        await RefreshLecActivitiesAsync();
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

    internal async void GLecShowDetail_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        string? studentId = btn.Tag?.ToString();
        if (string.IsNullOrEmpty(studentId)) return;

        var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_gradingAssignmentId);
        string studentName = summaries.FirstOrDefault(s => s.StudentId == studentId)?.StudentName
                             ?? studentId;

        await StudentDetailDialog.ShowAsync(XamlRoot, studentId, studentName, _gradingAssignmentId);
    }

    internal async void GLecGradeStudent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        _lecGradingStudentId = btn.Tag?.ToString();
        var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_gradingAssignmentId);
        var s = summaries.FirstOrDefault(x => x.StudentId == _lecGradingStudentId);
        if (s == null) return;

        var existing = await _grading.GetLecturerGradeForStudentAsync(_lecGradingStudentId!, _gradingAssignmentId);

        double pres = existing?.ScorePresentation   ?? 75;
        double rep  = existing?.ScoreReport         ?? 75;
        double impl = existing?.ScoreImplementation ?? 75;
        double def  = existing?.ScoreDefense        ?? 75;
        string fb   = existing?.Feedback            ?? "";
        bool fin    = existing?.IsFinalized         ?? false;

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

        var content = new StackPanel { Width = 400, Spacing = 12 };
        content.Children.Add(new TextBlock { Text = $"{s.StudentName} — {s.AssignmentTitle}", FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        content.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = new SolidColorBrush(Colors.Gray) });
        content.Children.Add(formGrid);
        content.Children.Add(new TextBlock { Text = "Feedback untuk Mahasiswa", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
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
            ScorePresentation   = nbPres.Value,
            ScoreReport         = nbRep.Value,
            ScoreImplementation = nbImpl.Value,
            ScoreDefense        = nbDef.Value,
            Score       = avg,
            Feedback    = fbBox.Text.Trim(),
            IsFinalized = finCb.IsChecked == true,
        };

        await _grading.SaveLecturerGradeAsync(grade);
        await LoadLecturerGradingAsync();
    }

    internal void LecturerGradingPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (LecturerGradingPivot.SelectedIndex)
        {
            case 0: InitChallengeRecapCombo(); BuildChallengeRecap(); break;
            case 1: _ = RefreshLecActivitiesAsync(); break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MANAGE STUDENTS dialog
    // ═══════════════════════════════════════════════════════════════════

    internal async void ManageStudents_Click(object sender, RoutedEventArgs e)
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

        var addIdBox    = new TextBox { PlaceholderText = "ID (mis. STU006)", MinWidth = 100 };
        var addNameBox  = new TextBox { PlaceholderText = "Nama Lengkap", MinWidth = 180, Margin = new Thickness(8, 0, 0, 0) };
        var addGroupBox = new TextBox { PlaceholderText = "Grup (mis. GRP-01)", MinWidth = 100, Margin = new Thickness(8, 0, 0, 0) };
        var addBtn      = new Button  { Content = "Tambah", Margin = new Thickness(8, 0, 0, 0) };

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0, Margin = new Thickness(0, 8, 0, 0) };
        addRow.Children.Add(addIdBox);
        addRow.Children.Add(addNameBox);
        addRow.Children.Add(addGroupBox);
        addRow.Children.Add(addBtn);

        var infoBar = new InfoBar { IsClosable = false, Margin = new Thickness(0, 6, 0, 0) };

        var content = new StackPanel { Width = 500, Spacing = 0 };
        content.Children.Add(new TextBlock { Text = "Daftar Mahasiswa (klik nama untuk edit)", FontSize = 13, Margin = new Thickness(0, 0, 0, 6) });
        content.Children.Add(listView);
        content.Children.Add(addRow);
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
                infoBar.Title    = "ID dan Nama wajib diisi.";
                infoBar.IsOpen   = true;
                return;
            }
            if (students.Any(s => s.Id == newId))
            {
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.Title    = $"ID '{newId}' sudah ada.";
                infoBar.IsOpen   = true;
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

        var toSave = students
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new StudentInfo { Id = s.Id, Name = s.Name.Trim(), GroupId = string.IsNullOrWhiteSpace(s.GroupId) ? null : s.GroupId.Trim() });
        await StudentService.Instance.ReplaceAllAsync(toSave);

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

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        if (parts.Length == 1) return name[..Math.Min(2, name.Length)].ToUpper();
        return $"{parts[0][0]}{parts[1][0]}".ToUpper();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  REKAP PENILAIAN CHALLENGE (SERVER)
    // ═══════════════════════════════════════════════════════════════════

    private void OnChallengeGradeSaved()
        => DispatcherQueue.TryEnqueue(BuildChallengeRecap);

    private void InitChallengeRecapCombo()
    {
        var challenges = ChallengeService.Instance.GetAllChallenges();
        ChallengeRecapCombo.Items.Clear();
        foreach (var ch in challenges)
            ChallengeRecapCombo.Items.Add(new ComboBoxItem { Content = ch.Title, Tag = ch.Id.ToString() });
        if (ChallengeRecapCombo.Items.Count > 0)
            ChallengeRecapCombo.SelectedIndex = 0;
    }

    private void BuildChallengeRecap()
    {
        ChallengeRecapRows.Children.Clear();

        string? selectedId = (ChallengeRecapCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (selectedId == null || !Guid.TryParse(selectedId, out var challengeGuid))
        {
            ChallengeRecapEmpty.Visibility = Visibility.Visible;
            return;
        }

        var challenge = ChallengeService.Instance.GetById(challengeGuid);
        if (challenge == null)
        {
            ChallengeRecapEmpty.Visibility = Visibility.Visible;
            return;
        }

        var subs = challenge.Submissions;

        bool hasRows = false;
        foreach (var sub in subs)
        {
            double? finalScore = sub.ComputeFinalScore(challenge);
            string scoreStr = finalScore.HasValue ? $"{finalScore.Value:F1}" : "—";
            string grade    = finalScore.HasValue ? ChallengeScoreToGrade(finalScore.Value) : "—";

            var row = new Grid
            {
                Padding = new Thickness(14, 10, 14, 10),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
            };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Nama mahasiswa
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock
            {
                Text = sub.StudentName.Length > 0 ? sub.StudentName : sub.StudentId,
                FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = sub.StudentId, FontSize = 11,
                Opacity = 0.5
            });
            Grid.SetColumn(nameStack, 0);

            // Kelompok
            var grpText = new TextBlock
            {
                Text = "—", FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(grpText, 1);

            // Nilai Final
            var finalText = new TextBlock
            {
                Text = scoreStr, FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(finalText, 2);

            // Grade badge
            var gradeBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = ChallengeGradeToBrush(grade),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            gradeBadge.Child = new TextBlock
            {
                Text = grade, FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White)
            };
            Grid.SetColumn(gradeBadge, 3);

            // Tombol Detail
            var capturedStudentId   = sub.StudentId;
            var capturedStudentName = sub.StudentName.Length > 0 ? sub.StudentName : sub.StudentId;
            var detailBtn = new Button
            {
                Content = "Detail", FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            detailBtn.Click += (_, _) => ShowStudentAllChallengesDetail(capturedStudentId, capturedStudentName);
            Grid.SetColumn(detailBtn, 4);

            row.Children.Add(nameStack);
            row.Children.Add(grpText);
            row.Children.Add(finalText);
            row.Children.Add(gradeBadge);
            row.Children.Add(detailBtn);

            ChallengeRecapRows.Children.Add(row);
            hasRows = true;
        }

        ChallengeRecapEmpty.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowStudentAllChallengesDetail(string studentId, string studentName)
    {
        var allChallenges = ChallengeService.Instance.GetAllChallenges();

        Border MakeScoreCard(string label, string value)
        {
            var sp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            return new Border
            {
                Background      = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush     = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(10, 8, 10, 8),
                Child           = sp
            };
        }

        Border WrapSection(UIElement child)
            => new Border
            {
                Background      = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush     = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(7),
                Padding         = new Thickness(12, 10, 12, 10),
                Child           = child
            };

        var challengesPanel = new StackPanel { Spacing = 6 };

        foreach (var ch in allChallenges)
        {
            var sub = ch.Submissions.FirstOrDefault(s => s.StudentId == studentId);

            double? finalScore = sub?.ComputeFinalScore(ch);
            string  grade      = finalScore.HasValue ? ChallengeScoreToGrade(finalScore.Value) : "—";
            double  peerAvg    = sub?.PeerGrades.Count > 0 ? sub.PeerGrades.Average(g => g.Score) : 0;
            double? sysScore   = sub?.AIGrade?.Score;
            double? dosScore   = sub?.DosenGrade?.Score;

            // ── Expander header ──────────────────────────────────────────
            var headerGrid = new Grid { ColumnSpacing = 10 };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleSp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            titleSp.Children.Add(new TextBlock { Text = ch.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            titleSp.Children.Add(new TextBlock { Text = ch.Status.ToString(), FontSize = 10, Opacity = 0.55 });

            var statusBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Background = sub != null ? ChallengeGradeToBrush(grade) : new SolidColorBrush(Windows.UI.Color.FromArgb(60, 128, 128, 128)),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusBadge.Child = new TextBlock
            {
                Text = sub != null ? grade : "Belum submit",
                FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White)
            };

            Grid.SetColumn(titleSp, 0); Grid.SetColumn(statusBadge, 1);
            headerGrid.Children.Add(titleSp); headerGrid.Children.Add(statusBadge);

            // ── Expander content ─────────────────────────────────────────
            var content = new StackPanel { Spacing = 10, Margin = new Thickness(4, 8, 4, 4) };

            if (sub == null)
            {
                content.Children.Add(new TextBlock { Text = "Mahasiswa belum mengumpulkan submission untuk challenge ini.", FontSize = 12, Opacity = 0.5 });
            }
            else
            {
                // Score summary pills
                var scoreGrid = new Grid { ColumnSpacing = 8 };
                scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var c0 = MakeScoreCard($"Peer ({sub.PeerGrades.Count})", sub.PeerGrades.Count > 0 ? peerAvg.ToString("F1") : "—");
                var c1 = MakeScoreCard("Sistem",  sysScore.HasValue ? sysScore.Value.ToString("F1") : "—");
                var c2 = MakeScoreCard("Dosen",   dosScore.HasValue ? dosScore.Value.ToString("F1") : "—");
                var c3 = MakeScoreCard("Final",   finalScore.HasValue ? $"{finalScore.Value:F1}" : "—");
                Grid.SetColumn(c0, 0); Grid.SetColumn(c1, 1); Grid.SetColumn(c2, 2); Grid.SetColumn(c3, 3);
                scoreGrid.Children.Add(c0); scoreGrid.Children.Add(c1);
                scoreGrid.Children.Add(c2); scoreGrid.Children.Add(c3);
                content.Children.Add(scoreGrid);

                // Peer grades detail
                var peerSp = new StackPanel { Spacing = 4 };
                peerSp.Children.Add(new TextBlock { Text = $"Nilai Peer  ({sub.PeerGrades.Count} penilai)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (sub.PeerGrades.Count > 0)
                {
                    foreach (var pg in sub.PeerGrades)
                    {
                        var pgRow = new Grid { ColumnSpacing = 8 };
                        pgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        pgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var pgName  = new TextBlock { Text = pg.GraderName, FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
                        var pgScore = new TextBlock { Text = pg.Score.ToString("F1"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                        Grid.SetColumn(pgName, 0); Grid.SetColumn(pgScore, 1);
                        pgRow.Children.Add(pgName); pgRow.Children.Add(pgScore);
                        peerSp.Children.Add(pgRow);
                    }
                    peerSp.Children.Add(new TextBlock { Text = $"Rata-rata: {peerAvg:F1}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) });
                }
                else
                    peerSp.Children.Add(new TextBlock { Text = "Belum ada penilaian peer", FontSize = 11, Opacity = 0.45 });

                // System grade
                var sysSp = new StackPanel { Spacing = 2 };
                sysSp.Children.Add(new TextBlock { Text = "Nilai Sistem  (log aktivitas)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                sysSp.Children.Add(new TextBlock { Text = sysScore.HasValue ? sysScore.Value.ToString("F1") : "Belum dinilai sistem", FontSize = 11, Opacity = sysScore.HasValue ? 1 : 0.45 });
                if (sub.AIGrade?.Feedback is { Length: > 0 } sysNote)
                    sysSp.Children.Add(new TextBlock { Text = sysNote, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

                // Lecturer grade
                var lecSp = new StackPanel { Spacing = 2 };
                lecSp.Children.Add(new TextBlock { Text = "Nilai Dosen  (input dosen)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                lecSp.Children.Add(new TextBlock { Text = dosScore.HasValue ? dosScore.Value.ToString("F1") : "Belum dinilai dosen", FontSize = 11, Opacity = dosScore.HasValue ? 1 : 0.45 });
                if (sub.DosenGrade?.Feedback is { Length: > 0 } dosFb)
                    lecSp.Children.Add(new TextBlock { Text = dosFb, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

                content.Children.Add(WrapSection(peerSp));
                content.Children.Add(WrapSection(sysSp));
                content.Children.Add(WrapSection(lecSp));

                content.Children.Add(new TextBlock { Text = $"Disubmit: {sub.SubmittedAt:dd MMM yyyy, HH:mm}", FontSize = 11, Opacity = 0.4 });
            }

            var expander = new Expander
            {
                Header = headerGrid,
                Content = content,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            challengesPanel.Children.Add(expander);
        }

        if (!allChallenges.Any())
            challengesPanel.Children.Add(new TextBlock { Text = "Belum ada challenge yang dibuat.", FontSize = 12, Opacity = 0.5 });

        var scrollViewer = new ScrollViewer
        {
            Content = challengesPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 480
        };

        var dialogContent = new StackPanel { Width = 500, Spacing = 12 };
        dialogContent.Children.Add(new TextBlock { Text = $"Rincian nilai per challenge untuk {studentName}", FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
        dialogContent.Children.Add(scrollViewer);

        var dialog = new ContentDialog
        {
            Title           = studentName,
            Content         = dialogContent,
            CloseButtonText = "Tutup",
            XamlRoot        = XamlRoot
        };
        _ = dialog.ShowAsync();
    }

    private void ShowChallengeSubmissionDetail(ChallengeSubmission sub, Challenge challenge)
    {
        double? finalScore = sub.ComputeFinalScore(challenge);
        string  grade      = finalScore.HasValue ? ChallengeScoreToGrade(finalScore.Value) : "—";

        double peerAvg  = sub.PeerGrades.Count > 0 ? sub.PeerGrades.Average(g => g.Score) : 0;
        double? sysScore = sub.AIGrade?.Score;
        double? dosScore = sub.DosenGrade?.Score;

        // ── Helper: small score pill card ────────────────────────────────
        Border MakeScoreCard(string label, string value)
        {
            var sp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 10, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = value, FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
            return new Border
            {
                Background      = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush     = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(12, 8, 12, 8),
                Child           = sp
            };
        }

        // ── Summary header ────────────────────────────────────────────────
        var headerStack = new StackPanel { Spacing = 10 };
        headerStack.Children.Add(new TextBlock
        {
            Text = sub.StudentName.Length > 0 ? sub.StudentName : sub.StudentId,
            FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });

        var scoreGrid = new Grid { ColumnSpacing = 8 };
        scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scoreGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var c0 = MakeScoreCard($"Peer ({sub.PeerGrades.Count})", sub.PeerGrades.Count > 0 ? peerAvg.ToString("F1") : "—");
        var c1 = MakeScoreCard("Sistem",   sysScore.HasValue ? sysScore.Value.ToString("F1") : "—");
        var c2 = MakeScoreCard("Dosen",    dosScore.HasValue ? dosScore.Value.ToString("F1") : "—");
        var c3 = MakeScoreCard("Final",    finalScore.HasValue ? $"{finalScore.Value:F1} ({grade})" : "—");
        Grid.SetColumn(c0, 0); Grid.SetColumn(c1, 1); Grid.SetColumn(c2, 2); Grid.SetColumn(c3, 3);
        scoreGrid.Children.Add(c0); scoreGrid.Children.Add(c1);
        scoreGrid.Children.Add(c2); scoreGrid.Children.Add(c3);
        headerStack.Children.Add(scoreGrid);
        headerStack.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle { Height = 1, Fill = new SolidColorBrush(Colors.Gray), Opacity = 0.25 });
        headerStack.Children.Add(new TextBlock { Text = "Daftar Tugas", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

        // ── Per-task expanders ────────────────────────────────────────────
        var tasksPanel = new StackPanel { Spacing = 6 };

        foreach (var task in challenge.Tasks)
        {
            double actualVal = 0;
            bool hasActual = task.HasMetricTarget && sub.MetricSnapshot.TryGetValue(task.Metric, out actualVal);
            bool? achieved = task.IsAchieved(hasActual ? actualVal : (double?)null);
            string targetStr = task.FormatTarget();

            // Expander header
            var taskHeader = new StackPanel { Spacing = 2 };
            var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            nameRow.Children.Add(new TextBlock { Text = task.Name, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (achieved.HasValue)
            {
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2, 6, 2),
                    Background = achieved.Value
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(40, 16, 185, 129))
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(40, 239, 68, 68)),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                {
                    Text = achieved.Value ? "✓ Tercapai" : "✗ Belum",
                    FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = achieved.Value
                        ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 185, 129))
                        : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68))
                };
                nameRow.Children.Add(badge);
            }
            taskHeader.Children.Add(nameRow);
            if (!string.IsNullOrEmpty(task.Description))
                taskHeader.Children.Add(new TextBlock { Text = task.Description, FontSize = 11, Opacity = 0.55, TextWrapping = TextWrapping.Wrap });

            // Expander content
            var taskContent = new StackPanel { Spacing = 12, Margin = new Thickness(4, 8, 4, 4) };

            // Metric target vs actual
            if (task.HasMetricTarget)
            {
                var metricGrid = new Grid { ColumnSpacing = 12 };
                metricGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                metricGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var tgtSp = new StackPanel { Spacing = 2 };
                tgtSp.Children.Add(new TextBlock { Text = "Target", FontSize = 10, Opacity = 0.6 });
                tgtSp.Children.Add(new TextBlock { Text = targetStr, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

                var actSp = new StackPanel { Spacing = 2 };
                actSp.Children.Add(new TextBlock { Text = "Aktual", FontSize = 10, Opacity = 0.6 });
                actSp.Children.Add(new TextBlock { Text = hasActual ? actualVal.ToString("F3") : "—", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

                Grid.SetColumn(tgtSp, 0); Grid.SetColumn(actSp, 1);
                metricGrid.Children.Add(tgtSp); metricGrid.Children.Add(actSp);

                var metricCard = new Border
                {
                    Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8, 10, 8),
                    Child = metricGrid
                };
                taskContent.Children.Add(metricCard);
            }

            taskContent.Children.Add(new TextBlock { Text = "Rincian Nilai", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Opacity = 0.7 });

            // Peer grades
            var peerSp = new StackPanel { Spacing = 4 };
            peerSp.Children.Add(new TextBlock { Text = $"Nilai Peer  ({sub.PeerGrades.Count} penilai)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (sub.PeerGrades.Count > 0)
            {
                foreach (var pg in sub.PeerGrades)
                {
                    var pgRow = new Grid { ColumnSpacing = 8 };
                    pgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    pgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    var pgName  = new TextBlock { Text = pg.GraderName, FontSize = 11, Opacity = 0.65, VerticalAlignment = VerticalAlignment.Center };
                    var pgScore = new TextBlock { Text = pg.Score.ToString("F1"), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    Grid.SetColumn(pgName, 0); Grid.SetColumn(pgScore, 1);
                    pgRow.Children.Add(pgName); pgRow.Children.Add(pgScore);
                    peerSp.Children.Add(pgRow);
                }
                peerSp.Children.Add(new TextBlock { Text = $"Rata-rata: {peerAvg:F1}", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) });
            }
            else
                peerSp.Children.Add(new TextBlock { Text = "Belum ada penilaian peer", FontSize = 11, Opacity = 0.45 });

            // System (AI) grade
            var sysSp = new StackPanel { Spacing = 2 };
            sysSp.Children.Add(new TextBlock { Text = "Nilai Sistem  (log aktivitas)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            sysSp.Children.Add(new TextBlock
            {
                Text = sysScore.HasValue ? sysScore.Value.ToString("F1") : "Belum dinilai sistem",
                FontSize = 11, Opacity = sysScore.HasValue ? 1 : 0.45
            });
            if (sub.AIGrade?.Feedback is { Length: > 0 } sysNote)
                sysSp.Children.Add(new TextBlock { Text = sysNote, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // Lecturer grade
            var lecSp = new StackPanel { Spacing = 2 };
            lecSp.Children.Add(new TextBlock { Text = "Nilai Dosen  (input dosen)", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            lecSp.Children.Add(new TextBlock
            {
                Text = dosScore.HasValue ? dosScore.Value.ToString("F1") : "Belum dinilai dosen",
                FontSize = 11, Opacity = dosScore.HasValue ? 1 : 0.45
            });
            if (sub.DosenGrade?.Feedback is { Length: > 0 } dosFb)
                lecSp.Children.Add(new TextBlock { Text = dosFb, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });

            // Wrap 3 grade sections in card borders
            Border WrapSection(StackPanel sp)
                => new Border
                {
                    Background      = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                    BorderBrush     = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                    BorderThickness = new Thickness(1),
                    CornerRadius    = new CornerRadius(7),
                    Padding         = new Thickness(12, 10, 12, 10),
                    Child           = sp
                };

            taskContent.Children.Add(WrapSection(peerSp));
            taskContent.Children.Add(WrapSection(sysSp));
            taskContent.Children.Add(WrapSection(lecSp));

            var expander = new Expander
            {
                Header = taskHeader,
                Content = taskContent,
                HorizontalAlignment        = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            tasksPanel.Children.Add(expander);
        }

        if (challenge.Tasks.Count == 0)
            tasksPanel.Children.Add(new TextBlock { Text = "Tidak ada tugas dalam challenge ini.", FontSize = 12, Opacity = 0.5 });

        var scrollViewer = new ScrollViewer
        {
            Content = tasksPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 420
        };

        var content = new StackPanel { Width = 480, Spacing = 10 };
        content.Children.Add(headerStack);
        content.Children.Add(scrollViewer);
        content.Children.Add(new TextBlock
        {
            Text = $"Disubmit: {sub.SubmittedAt:dd MMM yyyy, HH:mm}",
            FontSize = 11, Opacity = 0.45
        });

        var dialog = new ContentDialog
        {
            Title           = $"Detail — {(sub.StudentName.Length > 0 ? sub.StudentName : sub.StudentId)}",
            Content         = content,
            CloseButtonText = "Tutup",
            XamlRoot        = XamlRoot
        };
        _ = dialog.ShowAsync();
    }

    private static string ChallengeScoreToGrade(double score) => score switch
    {
        >= 80 => "A",
        >= 70 => "B",
        >= 60 => "C",
        >= 50 => "D",
        _     => "E"
    };

    private static SolidColorBrush ChallengeGradeToBrush(string grade) => grade switch
    {
        "A" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 185, 129)),
        "B" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 59, 130, 246)),
        "C" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 245, 158, 11)),
        "D" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 239, 68, 68)),
        "E" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 10, 10)),
        _   => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 128, 128, 128))
    };

    internal void ChallengeRecapCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => BuildChallengeRecap();

    internal void ChallengeRecapRefresh_Click(object sender, RoutedEventArgs e)
    {
        InitChallengeRecapCombo();
        BuildChallengeRecap();
    }
}

// ══════════════════════════════════════════════════════════════════════
//  Grading view models
// ══════════════════════════════════════════════════════════════════════

public class GradingGroupMemberVm
{
    public string StudentId   { get; set; }
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

internal class GLecActivityVm
{
    public string StudentName  { get; set; } = "";
    public string ActivityType { get; set; } = "";
    public string Description  { get; set; } = "";
    public string ActivityIcon { get; set; } = "";
    public string TimeAgo      { get; set; } = "";
    public string AutoScoreStr { get; set; } = "";
}

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
