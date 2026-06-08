// Views/GradingLecturerPage.xaml.cs
// Tambahkan file ini ke folder Views/ di project TLIG-Dashboard

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views
{
    public sealed partial class GradingLecturerPage : Page
    {
        private readonly GradingService _grading = GradingService.Instance;
        private string _currentAssignmentId = "ASGN-001";
        private string? _gradingStudentId;

        private readonly List<(string Id, string Title)> _assignments = new()
        {
            ("ASGN-001", "Tugas Kelompok - Sistem Kontrol PID"),
            ("ASGN-002", "Tugas Kelompok - Heat Exchanger"),
        };

        public GradingLecturerPage()
        {
            InitializeComponent();
            InitAssignmentCombo();
            LoadAllData();
        }

        private void InitAssignmentCombo()
        {
            foreach (var (id, title) in _assignments)
                AssignmentCombo.Items.Add(new ComboBoxItem { Content = title, Tag = id });
            AssignmentCombo.SelectedIndex = 0;
        }

        private void LoadAllData()
        {
            _ = RefreshSummaryTab();
            _ = RefreshPeerTab();
            _ = RefreshSystemTab();
            _ = RefreshActivitiesTab();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 1: Summary
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshSummaryTab()
        {
            var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_currentAssignmentId);

            // Stats
            StatTotalStudents.Text = summaries.Count.ToString();
            StatGraded.Text = summaries.Count(s => s.LecturerScore.HasValue).ToString();
            var withFinal = summaries.Where(s => s.FinalScore.HasValue).ToList();
            StatClassAvg.Text = withFinal.Any() ? withFinal.Average(s => s.FinalScore!.Value).ToString("F1") : "—";
            StatPeerDone.Text = summaries.Count(s => s.PeerScore.HasValue).ToString();

            var vms = summaries.Select(s => new SummaryRowVM(s)).ToList();
            SummaryRepeater.ItemsSource = vms;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 2: Peer
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshPeerTab()
        {
            var evals = await _grading.GetPeerEvaluationsByAssignmentAsync(_currentAssignmentId);
            var vms = evals.OrderByDescending(e => e.EvaluatedAt).Select(e => new PeerRowVM
            {
                EvaluatorName = e.EvaluatorName,
                EvaluateeName = e.EvaluateeName,
                ScoreStr = e.Score.ToString("F1"),
                Comment = string.IsNullOrWhiteSpace(e.Comment) ? "(Tidak ada komentar)" : e.Comment,
                EvaluatedAtStr = e.EvaluatedAt.ToString("dd MMM, HH:mm"),
                SubScoresStr = $"K:{e.CriteriaContribution:F0}  T:{e.CriteriaCooperation:F0}  J:{e.CriteriaResponsibility:F0}  C:{e.CriteriaCreativity:F0}",
            }).ToList();

            AllPeerRepeater.ItemsSource = vms;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 3: System
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshSystemTab()
        {
            var evals = await _grading.GetSystemEvaluationsByAssignmentAsync(_currentAssignmentId);
            var vms = evals.OrderByDescending(e => e.Score).Select(e => new SystemRowVM
            {
                StudentId = e.StudentId,
                StudentName = e.StudentName,
                ScoreStr = e.Score.ToString("F1"),
                CommitStr = $"Submit: {e.CommitsCount}",
                FileStr = $"File: {e.FilesModified}",
                TaskStr = $"Task: {e.TasksCompleted}/{e.TasksTotal}",
                OnTimeStr = e.SubmittedOnTime ? "Tepat Waktu" : "Terlambat",
                OnTimeBg = new SolidColorBrush(e.SubmittedOnTime
                    ? Windows.UI.Color.FromArgb(30, 16, 124, 16)
                    : Windows.UI.Color.FromArgb(30, 200, 40, 40)),
                OnTimeFg = new SolidColorBrush(e.SubmittedOnTime
                    ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
                    : Windows.UI.Color.FromArgb(255, 200, 40, 40)),
            }).ToList();

            SystemEvalRepeater.ItemsSource = vms;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tab 4: Activities
        // ─────────────────────────────────────────────────────────────────────

        private async System.Threading.Tasks.Task RefreshActivitiesTab()
        {
            var acts = await _grading.GetGroupActivitiesAsync(_currentAssignmentId, 50);
            var vms = acts.Select(a => new ActivityRowVM
            {
                StudentName = a.StudentName,
                ActivityType = a.ActivityType,
                Description = a.Description,
                ActivityIcon = a.ActivityIcon,
                TimeAgo = a.TimeAgo,
                AutoScoreStr = a.AutoScore.HasValue ? $"+{a.AutoScore:F0} poin" : "—",
            }).ToList();

            LecActivitiesRepeater.ItemsSource = vms;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Grade Dialog
        // ─────────────────────────────────────────────────────────────────────

        private async void GradeStudent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn)
            {
                _gradingStudentId = btn.Tag?.ToString();
                var summary = (await _grading.GetGradeSummaryByAssignmentAsync(_currentAssignmentId))
                    .FirstOrDefault(s => s.StudentId == _gradingStudentId);

                if (summary == null) return;

                GradeDialogStudentName.Text = $"{summary.StudentName} — {summary.AssignmentTitle}";

                // Load nilai yang sudah ada jika ada
                var existing = await _grading.GetLecturerGradeForStudentAsync(_gradingStudentId!, _currentAssignmentId);
                if (existing != null)
                {
                    NbPresentation.Value = existing.ScorePresentation;
                    NbReport.Value = existing.ScoreReport;
                    NbImplementation.Value = existing.ScoreImplementation;
                    NbDefense.Value = existing.ScoreDefense;
                    FeedbackBox.Text = existing.Feedback;
                    FinalizeCheck.IsChecked = existing.IsFinalized;
                }
                else
                {
                    NbPresentation.Value = 75;
                    NbReport.Value = 75;
                    NbImplementation.Value = 75;
                    NbDefense.Value = 75;
                    FeedbackBox.Text = string.Empty;
                    FinalizeCheck.IsChecked = false;
                }

                await GradeDialog.ShowAsync();
            }
        }

        private async void GradeDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            if (_gradingStudentId == null) return;

            var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_currentAssignmentId);
            var s = summaries.FirstOrDefault(x => x.StudentId == _gradingStudentId);

            double pres = NbPresentation.Value;
            double rep  = NbReport.Value;
            double impl = NbImplementation.Value;
            double def  = NbDefense.Value;
            double avg  = (pres + rep + impl + def) / 4.0;

            var grade = new LecturerGrade
            {
                LecturerId = "LEC001",
                LecturerName = "Dosen Pengampu",
                StudentId = _gradingStudentId,
                StudentName = s?.StudentName ?? _gradingStudentId,
                AssignmentId = _currentAssignmentId,
                AssignmentTitle = _assignments.First(a => a.Id == _currentAssignmentId).Title,
                GroupId = "GRP-01",
                ScorePresentation = pres,
                ScoreReport = rep,
                ScoreImplementation = impl,
                ScoreDefense = def,
                Score = avg,
                Feedback = FeedbackBox.Text.Trim(),
                IsFinalized = FinalizeCheck.IsChecked == true,
            };

            await _grading.SaveLecturerGradeAsync(grade);

            // Refresh semua tab
            LoadAllData();
        }

        private async void GradeAll_Click(object sender, RoutedEventArgs e)
        {
            // Buka dialog nilai pertama yang belum dinilai
            var summaries = await _grading.GetGradeSummaryByAssignmentAsync(_currentAssignmentId);
            var first = summaries.FirstOrDefault(s => !s.LecturerScore.HasValue);
            if (first != null)
            {
                var btn = new Button { Tag = first.StudentId };
                GradeStudent_Click(btn, e);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Refresh handlers
        // ─────────────────────────────────────────────────────────────────────

        private void RefreshSummary_Click(object sender, RoutedEventArgs e) => _ = RefreshSummaryTab();
        private void RefreshActivities_Click(object sender, RoutedEventArgs e) => _ = RefreshActivitiesTab();

        private void AssignmentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AssignmentCombo.SelectedItem is ComboBoxItem item)
            {
                _currentAssignmentId = item.Tag?.ToString() ?? _currentAssignmentId;
                LoadAllData();
            }
        }

        private void MainPivot_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch (MainPivot.SelectedIndex)
            {
                case 0: _ = RefreshSummaryTab(); break;
                case 1: _ = RefreshPeerTab(); break;
                case 2: _ = RefreshSystemTab(); break;
                case 3: _ = RefreshActivitiesTab(); break;
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            // TODO: implementasi export ke Excel/CSV
            // Bisa menggunakan ClosedXML atau EPPlus
        }
    }

    // ── View Models ───────────────────────────────────────────────────────────

    internal class SummaryRowVM
    {
        public string StudentId { get; }
        public string StudentName { get; }
        public string GroupName { get; }
        public string PeerScoreStr { get; }
        public string SystemScoreStr { get; }
        public string LecturerScoreStr { get; }
        public string FinalScoreStr { get; }
        public string LetterGrade { get; }
        public SolidColorBrush GradeBadgeColor { get; }

        public SummaryRowVM(StudentGradeSummary s)
        {
            StudentId = s.StudentId;
            StudentName = s.StudentName;
            GroupName = s.GroupName;
            PeerScoreStr = s.PeerScore.HasValue ? s.PeerScore.Value.ToString("F1") : "—";
            SystemScoreStr = s.SystemScore.HasValue ? s.SystemScore.Value.ToString("F1") : "—";
            LecturerScoreStr = s.LecturerScore.HasValue ? s.LecturerScore.Value.ToString("F1") : "—";
            FinalScoreStr = s.FinalScore.HasValue ? s.FinalScore.Value.ToString("F1") : "—";
            LetterGrade = s.LetterGrade;
            GradeBadgeColor = new SolidColorBrush(s.LetterGrade switch
            {
                "A" or "A-" => Windows.UI.Color.FromArgb(255, 16, 124, 16),
                "B+" or "B" or "B-" => Windows.UI.Color.FromArgb(255, 0, 120, 212),
                "C+" or "C" => Windows.UI.Color.FromArgb(255, 200, 130, 0),
                _ => Windows.UI.Color.FromArgb(255, 196, 43, 28),
            });
        }
    }

    internal class PeerRowVM
    {
        public string EvaluatorName { get; set; } = "";
        public string EvaluateeName { get; set; } = "";
        public string ScoreStr { get; set; } = "";
        public string Comment { get; set; } = "";
        public string EvaluatedAtStr { get; set; } = "";
        public string SubScoresStr { get; set; } = "";
    }

    internal class SystemRowVM
    {
        public string StudentId { get; set; } = "";
        public string StudentName { get; set; } = "";
        public string ScoreStr { get; set; } = "";
        public string CommitStr { get; set; } = "";
        public string FileStr { get; set; } = "";
        public string TaskStr { get; set; } = "";
        public string OnTimeStr { get; set; } = "";
        public SolidColorBrush? OnTimeBg { get; set; }
        public SolidColorBrush? OnTimeFg { get; set; }
    }

    internal class ActivityRowVM
    {
        public string StudentName { get; set; } = "";
        public string ActivityType { get; set; } = "";
        public string Description { get; set; } = "";
        public string ActivityIcon { get; set; } = "";
        public string TimeAgo { get; set; } = "";
        public string AutoScoreStr { get; set; } = "";
    }
}
