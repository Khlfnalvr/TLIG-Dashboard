// Views/GradingStudentPage.xaml.cs
// Tambahkan file ini ke folder Views/ di project TLIG-Dashboard

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views
{
    public sealed partial class GradingStudentPage : Page
    {
        // ── State ─────────────────────────────────────────────────────────────
        private readonly GradingService _grading = GradingService.Instance;
        // Gunakan username dari sesi login; fallback ke STU001 untuk demo
        private string _currentStudentId = App.Session.IsSignedIn ? App.Session.Username : "STU001";
        private string _currentAssignmentId = "ASGN-001";
        private string? _targetStudentId;               // Mahasiswa yang sedang dinilai

        // Group members — loaded from StudentService (excludes current user)
        private List<GroupMemberVM> _groupMembers = new();

        // Demo assignments
        private readonly List<(string Id, string Title)> _assignments = new()
        {
            ("ASGN-001", "Tugas Kelompok - Sistem Kontrol PID"),
            ("ASGN-002", "Tugas Kelompok - Heat Exchanger"),
        };

        public GradingStudentPage()
        {
            InitializeComponent();
            InitializeAssignmentCombo();
            LoadPageData();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Init
        // ─────────────────────────────────────────────────────────────────────

        private void InitializeAssignmentCombo()
        {
            foreach (var (id, title) in _assignments)
                AssignmentCombo.Items.Add(new ComboBoxItem { Content = title, Tag = id });
            AssignmentCombo.SelectedIndex = 0;
        }

        private async void LoadPageData()
        {
            await StudentService.Instance.EnsureLoadedAsync();
            _groupMembers = StudentService.Instance.GetAll()
                .Where(s => s.Id != _currentStudentId)
                .Select(s => new GroupMemberVM(s.Id, s.Name))
                .ToList();

            await RefreshPeerTab();
            await RefreshReceivedTab();
            await RefreshActivitiesTab();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 1: Peer Evaluation
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshPeerTab()
        {
            var myEvals = await _grading.GetPeerEvaluationsByEvaluatorAsync(_currentStudentId);
            var alreadyRatedIds = myEvals.Select(e => e.EvaluateeId).ToHashSet();

            foreach (var member in _groupMembers)
            {
                member.AlreadyRated = alreadyRatedIds.Contains(member.StudentId);
                member.AlreadyRatedVisibility = member.AlreadyRated ? Visibility.Visible : Visibility.Collapsed;
            }

            GroupMembersRepeater.ItemsSource = null;
            GroupMembersRepeater.ItemsSource = _groupMembers;
        }

        private void NilaiSekarang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _targetStudentId = btn.Tag?.ToString();
                var target = _groupMembers.First(m => m.StudentId == _targetStudentId);
                FormTargetName.Text = $"Menilai: {target.StudentName}";

                // Reset sliders
                SliderContrib.Value = 75;
                SliderCoop.Value = 75;
                SliderResp.Value = 75;
                SliderCreat.Value = 75;
                PeerCommentBox.Text = string.Empty;
                UpdateAveragePreview();

                PeerFormCard.Visibility = Visibility.Visible;

                // Scroll ke form
                // (ScrollViewer auto-handles dalam StackPanel)
            }
        }

        private void CloseForm_Click(object sender, RoutedEventArgs e)
        {
            PeerFormCard.Visibility = Visibility.Collapsed;
            _targetStudentId = null;
        }

        private void Slider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (sender is Slider sl)
            {
                string tag = sl.Tag?.ToString() ?? "";
                string valStr = ((int)sl.Value).ToString();
                switch (tag)
                {
                    case "Contrib": if (ContribScore != null) ContribScore.Text = valStr; break;
                    case "Coop":    if (CoopScore != null) CoopScore.Text = valStr; break;
                    case "Resp":    if (RespScore != null) RespScore.Text = valStr; break;
                    case "Creat":   if (CreatScore != null) CreatScore.Text = valStr; break;
                }
                UpdateAveragePreview();
            }
        }

        private void UpdateAveragePreview()
        {
            if (SliderContrib == null || AveragePreview == null) return;
            double avg = (SliderContrib.Value + SliderCoop.Value + SliderResp.Value + SliderCreat.Value) / 4.0;
            AveragePreview.Text = avg.ToString("F1");
        }

        private async void SubmitPeer_Click(object sender, RoutedEventArgs e)
        {
            if (_targetStudentId == null) return;

            var target = _groupMembers.FirstOrDefault(m => m.StudentId == _targetStudentId);
            if (target == null) return;

            var eval = new PeerEvaluation
            {
                EvaluatorId   = _currentStudentId,
                EvaluatorName = App.Session.IsSignedIn && !string.IsNullOrWhiteSpace(App.Session.DisplayName)
                                    ? App.Session.DisplayName : _currentStudentId,
                EvaluateeId   = _targetStudentId,
                EvaluateeName = target.StudentName,
                AssignmentId  = _currentAssignmentId,
                AssignmentTitle = _assignments.First(a => a.Id == _currentAssignmentId).Title,
                GroupId = "GRP-01",
                CriteriaContribution   = SliderContrib.Value,
                CriteriaCooperation    = SliderCoop.Value,
                CriteriaResponsibility = SliderResp.Value,
                CriteriaCreativity     = SliderCreat.Value,
                Comment = PeerCommentBox.Text.Trim(),
            };

            bool ok = await _grading.SubmitPeerEvaluationAsync(eval);

            if (ok)
            {
                PeerInfoBar.Severity = InfoBarSeverity.Success;
                PeerInfoBar.Title = "Penilaian Terkirim";
                PeerInfoBar.Message = $"Kamu telah menilai {target.StudentName} dengan skor {eval.AverageScore:F1}.";
                PeerInfoBar.IsOpen = true;
                PeerFormCard.Visibility = Visibility.Collapsed;
                _targetStudentId = null;
                await RefreshPeerTab();
            }
            else
            {
                PeerInfoBar.Severity = InfoBarSeverity.Error;
                PeerInfoBar.Title = "Gagal";
                PeerInfoBar.Message = "Terjadi kesalahan, coba lagi.";
                PeerInfoBar.IsOpen = true;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 2: Received Evaluations
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshReceivedTab()
        {
            // Peer scores received
            var received = await _grading.GetPeerEvaluationsForStudentAsync(_currentStudentId);
            var receivedVMs = received.Select(e => new ReceivedEvalVM
            {
                EvaluatorInitials = GetInitials(e.EvaluatorName),
                EvaluatorName = e.EvaluatorName,
                Comment = string.IsNullOrWhiteSpace(e.Comment) ? "(Tidak ada komentar)" : e.Comment,
                ScoreStr = e.Score.ToString("F1"),
                EvaluatedAtStr = e.EvaluatedAt.ToString("dd MMM yyyy, HH:mm"),
            }).ToList();

            ReceivedEvalRepeater.ItemsSource = receivedVMs;

            if (receivedVMs.Any())
            {
                double avg = received.Average(e => e.Score);
                MyPeerScore.Text = avg.ToString("F1");
                MyPeerCount.Text = $"{receivedVMs.Count} penilai";
            }
            else
            {
                MyPeerScore.Text = "—";
                MyPeerCount.Text = "Belum ada penilai";
            }

            // System score
            var sysEval = await _grading.GetSystemEvaluationForStudentAsync(_currentStudentId, _currentAssignmentId);
            if (sysEval != null)
            {
                MySystemScore.Text = sysEval.Score.ToString("F1");
                SysCommits.Text = sysEval.CommitsCount.ToString();
                SysFiles.Text   = sysEval.FilesModified.ToString();
                SysOnTime.Text  = sysEval.SubmittedOnTime ? "Ya ✓" : "Tidak";
                SysTasks.Text   = $"{sysEval.TasksCompleted}/{sysEval.TasksTotal}";
            }
            else
            {
                MySystemScore.Text = "—";
            }

            // Lecturer score
            var lecGrade = await _grading.GetLecturerGradeForStudentAsync(_currentStudentId, _currentAssignmentId);
            if (lecGrade != null)
            {
                MyLecturerScore.Text = $"{lecGrade.Score:F1} ({lecGrade.LetterGrade})";
            }
            else
            {
                MyLecturerScore.Text = "Belum";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 3: Group Activities
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshActivitiesTab()
        {
            var activities = await _grading.GetGroupActivitiesAsync(_currentAssignmentId);
            ActivitiesRepeater.ItemsSource = activities;
        }

        private async void RefreshActivities_Click(object sender, RoutedEventArgs e)
        {
            await RefreshActivitiesTab();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Event Handlers
        // ─────────────────────────────────────────────────────────────────────

        private void AssignmentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AssignmentCombo.SelectedItem is ComboBoxItem item)
            {
                _currentAssignmentId = item.Tag?.ToString() ?? _currentAssignmentId;
                LoadPageData();
            }
        }

        private void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Refresh tab sesuai indeks yang dipilih
            switch (MainPivot.SelectedIndex)
            {
                case 1: _ = RefreshReceivedTab(); break;
                case 2: _ = RefreshActivitiesTab(); break;
            }
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) { }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        private static string GetInitials(string name)
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return parts[0][..Math.Min(2, parts[0].Length)].ToUpper();
            return $"{parts[0][0]}{parts[1][0]}".ToUpper();
        }
    }

    // ── View Models lokal ─────────────────────────────────────────────────────

    public class GroupMemberVM
    {
        public string StudentId { get; set; }
        public string StudentName { get; set; }
        public string Initials => GetInitials(StudentName);
        public bool AlreadyRated { get; set; }
        public Visibility AlreadyRatedVisibility { get; set; } = Visibility.Collapsed;

        public GroupMemberVM(string id, string name)
        {
            StudentId = id;
            StudentName = name;
        }

        private static string GetInitials(string name)
        {
            var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            if (parts.Length == 1) return name[..Math.Min(2, name.Length)].ToUpper();
            return $"{parts[0][0]}{parts[1][0]}".ToUpper();
        }
    }

    public class ReceivedEvalVM
    {
        public string EvaluatorInitials { get; set; } = "";
        public string EvaluatorName { get; set; } = "";
        public string Comment { get; set; } = "";
        public string ScoreStr { get; set; } = "";
        public string EvaluatedAtStr { get; set; } = "";
    }
}
