// Controls/StudentDetailDialog.cs
// Dialog detail mahasiswa: overall score breakdown + hasil simulasi.
// Dipanggil dari LearningAnalyticView (dosen) dan UserPerformancePage.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using Windows.Foundation;

namespace TLIGDashboard.Controls;

internal static class StudentDetailDialog
{
    public static async Task ShowAsync(
        XamlRoot root,
        string studentId,
        string studentName,
        string assignmentId,
        DateTime? deadline = null)
    {
        var grading  = GradingService.Instance;
        var summaries = await grading.GetGradeSummaryByAssignmentAsync(assignmentId);
        var summary   = summaries.FirstOrDefault(s => s.StudentId == studentId);

        var peerReceived = (await grading.GetPeerEvaluationsForStudentAsync(studentId))
            .Where(e => e.AssignmentId == assignmentId).ToList();
        var peerGiven = (await grading.GetPeerEvaluationsByEvaluatorAsync(studentId))
            .Where(e => e.AssignmentId == assignmentId).ToList();
        var sysEval  = await grading.GetSystemEvaluationForStudentAsync(studentId, assignmentId);
        var lecGrade = await grading.GetLecturerGradeForStudentAsync(studentId, assignmentId);

        var allActs   = await grading.GetGroupActivitiesAsync(assignmentId, 500);
        var myActs    = allActs.Where(a => a.StudentId == studentId)
                               .OrderByDescending(a => a.ActivityTime).ToList();
        var mySubmits = myActs.Where(a => a.ActivityType == "Submit").ToList();

        // Data 3 parameter sistem
        var tunings   = await grading.GetTuningRecordsAsync(studentId, assignmentId);
        var aiUsage   = await grading.GetAiUsageAsync(studentId, assignmentId);
        var sims      = await grading.GetSimulationResultsAsync(studentId, assignmentId);
        var uploads   = await grading.GetUploadedFilesAsync(studentId, assignmentId);

        var content = new StackPanel { Width = 560, Spacing = 16 };

        // ── 1. Header ─────────────────────────────────────────────────────
        content.Children.Add(BuildHeader(studentId, studentName, summary));

        // ── 2. Nilai Keseluruhan ──────────────────────────────────────────
        content.Children.Add(BuildSectionTitle("Nilai Keseluruhan"));
        content.Children.Add(BuildScoreBreakdown(summary));

        // ── 3. Nilai Dosen (komponen) ─────────────────────────────────────
        content.Children.Add(BuildSectionTitle("Penilaian Dosen — Rincian Komponen"));
        content.Children.Add(BuildLecturerDetail(lecGrade));

        // ── 4. Penilaian Peer Diterima ────────────────────────────────────
        content.Children.Add(BuildSectionTitle(
            $"Penilaian Peer Diterima  ({peerReceived.Count} penilai)"));
        content.Children.Add(peerReceived.Count > 0
            ? BuildPeerReceivedList(peerReceived)
            : MutedNote("Belum ada penilaian peer yang diterima."));

        // ── 5. Penilaian Peer Diberikan ───────────────────────────────────
        content.Children.Add(BuildSectionTitle(
            $"Penilaian Peer Diberikan  ({peerGiven.Count} penilaian)"));
        content.Children.Add(peerGiven.Count > 0
            ? BuildPeerGivenList(peerGiven)
            : MutedNote("Belum memberikan penilaian peer."));

        // ── 6. Detail Skor Sistem (3 parameter) ──────────────────────────
        content.Children.Add(BuildSectionTitle(
            $"Penilaian Sistem — 3 Parameter  (Skor: {sysEval?.Score:F1}/100)"));
        content.Children.Add(BuildSystemDetail(sysEval, tunings, aiUsage, sims));

        // ── 7. File yang Diunggah Mahasiswa ──────────────────────────────
        content.Children.Add(BuildSectionTitle(
            $"File yang Diunggah  ({uploads.Count} file)"));
        content.Children.Add(uploads.Count > 0
            ? BuildUploadedFiles(uploads)
            : MutedNote("Belum ada file yang diunggah untuk tugas ini."));

        // ── 8. Rekap Submit vs Deadline ───────────────────────────────────
        content.Children.Add(BuildSectionTitle(
            $"Rekap Submit  ({mySubmits.Count} submit)"));
        content.Children.Add(mySubmits.Count > 0
            ? BuildSubmitHistory(mySubmits, deadline)
            : MutedNote("Belum ada aktivitas Submit tercatat."));

        // ── 9. Hasil Simulasi ─────────────────────────────────────────────
        content.Children.Add(BuildSectionTitle(
            sims.Count > 0 ? $"Hasil Simulasi  ({sims.Count} sesi)" : "Hasil Simulasi"));
        content.Children.Add(sims.Count > 0
            ? BuildSimulationList(sims)
            : MutedNote("Belum ada sesi simulasi tercatat."));

        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 600,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title           = $"Detail — {studentName}",
            Content         = scroll,
            CloseButtonText = "Tutup",
            DefaultButton   = ContentDialogButton.Close,
            XamlRoot        = root,
        };

        await dialog.ShowAsync();
    }

    // ── Header: avatar · nama · ID · grade badge ─────────────────────────

    private static UIElement BuildHeader(
        string studentId, string studentName, StudentGradeSummary? summary)
    {
        string grade = summary?.LetterGrade ?? "—";

        var badgeBrush = new SolidColorBrush(GradeColor(grade));

        string initials = BuildInitials(studentName);

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new PersonPicture { Width = 52, Height = 52, Initials = initials };
        Grid.SetColumn(avatar, 0);
        grid.Children.Add(avatar);

        var namePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        namePanel.Children.Add(new TextBlock
        {
            Text = studentName, FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        namePanel.Children.Add(new TextBlock { Text = studentId, FontSize = 12, Opacity = 0.5 });
        Grid.SetColumn(namePanel, 1);
        grid.Children.Add(namePanel);

        var badge = new Border
        {
            Background        = badgeBrush,
            CornerRadius      = new CornerRadius(10),
            Padding           = new Thickness(16, 8, 16, 8),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.Child = new TextBlock
        {
            Text        = grade,
            FontSize    = 22,
            FontWeight  = FontWeights.Bold,
            Foreground  = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetColumn(badge, 2);
        grid.Children.Add(badge);

        return grid;
    }

    // ── Lecturer detail ──────────────────────────────────────────────────

    private static UIElement BuildLecturerDetail(LecturerGrade? lec)
    {
        if (lec == null)
            return MutedNote("Belum ada penilaian dari dosen.");

        var panel = new StackPanel { Spacing = 10 };

        AddScoreRow(panel, "Presentasi",   "25%",
            lec.ScorePresentation,
            Windows.UI.Color.FromArgb(255, 16, 185, 129));
        AddScoreRow(panel, "Laporan",      "25%",
            lec.ScoreReport,
            Windows.UI.Color.FromArgb(255, 59, 130, 246));
        AddScoreRow(panel, "Implementasi", "25%",
            lec.ScoreImplementation,
            Windows.UI.Color.FromArgb(255, 245, 158, 11));
        AddScoreRow(panel, "Defence/Ujian","25%",
            lec.ScoreDefense,
            Windows.UI.Color.FromArgb(255, 139, 92, 246));

        // Divider
        panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill   = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
        });

        // Rata-rata dosen + finalisasi
        var avgRow = new Grid { ColumnSpacing = 8 };
        avgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        avgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        avgRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        avgRow.Children.Add(new TextBlock
        {
            Text = "Nilai Dosen (Rata-rata)", FontSize = 12,
            FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        var avgVal = new TextBlock
        {
            Text = lec.Score.ToString("F1"), FontSize = 16, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(avgVal, 1);
        avgRow.Children.Add(avgVal);

        if (lec.IsFinalized)
        {
            var finBadge = new Border
            {
                Background   = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 16, 124, 16)),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            };
            finBadge.Child = new TextBlock
            {
                Text = "Final", FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 124, 16)),
            };
            Grid.SetColumn(finBadge, 2);
            avgRow.Children.Add(finBadge);
        }
        panel.Children.Add(avgRow);

        if (!string.IsNullOrWhiteSpace(lec.Feedback))
        {
            var fbCard = new Border
            {
                Background      = SafeThemeBrush("SubtleFillColorSecondaryBrush"),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(0, 2, 0, 0),
            };
            var fbSp = new StackPanel { Spacing = 4 };
            fbSp.Children.Add(new TextBlock
            {
                Text = "Feedback Dosen", FontSize = 11,
                FontWeight = FontWeights.SemiBold, Opacity = 0.7,
            });
            fbSp.Children.Add(new TextBlock
            {
                Text = lec.Feedback, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            fbCard.Child = fbSp;
            panel.Children.Add(fbCard);
        }

        return panel;
    }

    // ── Peer received list ────────────────────────────────────────────────

    private static UIElement BuildPeerReceivedList(List<PeerEvaluation> evals)
    {
        var panel = new StackPanel { Spacing = 8 };

        // Summary bar
        double avg = evals.Average(e => e.Score);
        var sumCard = new Border
        {
            Background      = SafeThemeBrush("AccentFillColorTertiaryBrush"),
            CornerRadius    = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
        };
        var sumGrid = new Grid { ColumnSpacing = 8 };
        sumGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sumGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        sumGrid.Children.Add(new TextBlock
        {
            Text = $"Rata-rata dari {evals.Count} penilai",
            FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        var avgTb = new TextBlock
        {
            Text = avg.ToString("F1"), FontSize = 18, FontWeight = FontWeights.Bold,
            Foreground = SafeThemeBrush("AccentTextFillColorPrimaryBrush"),
        };
        Grid.SetColumn(avgTb, 1); sumGrid.Children.Add(avgTb);
        sumCard.Child = sumGrid;
        panel.Children.Add(sumCard);

        foreach (var e in evals.OrderByDescending(x => x.EvaluatedAt))
        {
            var card = new Border
            {
                Background      = SafeThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush     = SafeThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding         = new Thickness(12, 10, 12, 10),
            };
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 3 };
            info.Children.Add(new TextBlock
            {
                Text = $"Dinilai oleh: {e.EvaluatorName}", FontSize = 12, FontWeight = FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"K:{e.CriteriaContribution:F0}  T:{e.CriteriaCooperation:F0}  " +
                       $"J:{e.CriteriaResponsibility:F0}  C:{e.CriteriaCreativity:F0}",
                FontSize = 11, Opacity = 0.6,
            });
            if (!string.IsNullOrWhiteSpace(e.Comment))
                info.Children.Add(new TextBlock
                {
                    Text = $"\"{e.Comment}\"", FontSize = 11, Opacity = 0.55,
                    TextWrapping = TextWrapping.Wrap, MaxLines = 2,
                });
            info.Children.Add(new TextBlock
            {
                Text = e.EvaluatedAt.ToString("dd MMM yyyy, HH:mm"),
                FontSize = 10, Opacity = 0.4,
            });
            g.Children.Add(info);

            var scoreBadge = new Border
            {
                Background        = SafeThemeBrush("AccentFillColorTertiaryBrush"),
                CornerRadius      = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };
            scoreBadge.Child = new TextBlock
            {
                Text = e.Score.ToString("F1"), FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = SafeThemeBrush("AccentTextFillColorPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(scoreBadge, 1); g.Children.Add(scoreBadge);

            card.Child = g; panel.Children.Add(card);
        }
        return panel;
    }

    // ── Peer given list ───────────────────────────────────────────────────

    private static UIElement BuildPeerGivenList(List<PeerEvaluation> evals)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var e in evals.OrderByDescending(x => x.EvaluatedAt))
        {
            var card = new Border
            {
                Background      = SafeThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush     = SafeThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding         = new Thickness(12, 10, 12, 10),
            };
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 3 };
            info.Children.Add(new TextBlock
            {
                Text = $"Menilai: {e.EvaluateeName}", FontSize = 12, FontWeight = FontWeights.SemiBold,
            });
            info.Children.Add(new TextBlock
            {
                Text = $"K:{e.CriteriaContribution:F0}  T:{e.CriteriaCooperation:F0}  " +
                       $"J:{e.CriteriaResponsibility:F0}  C:{e.CriteriaCreativity:F0}",
                FontSize = 11, Opacity = 0.6,
            });
            info.Children.Add(new TextBlock
            {
                Text = e.EvaluatedAt.ToString("dd MMM yyyy, HH:mm"),
                FontSize = 10, Opacity = 0.4,
            });
            g.Children.Add(info);

            var scoreBadge = new Border
            {
                Background        = SafeThemeBrush("AccentFillColorTertiaryBrush"),
                CornerRadius      = new CornerRadius(6), Padding = new Thickness(10, 6, 10, 6),
                VerticalAlignment = VerticalAlignment.Center,
            };
            scoreBadge.Child = new TextBlock
            {
                Text = e.Score.ToString("F1"), FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = SafeThemeBrush("AccentTextFillColorPrimaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(scoreBadge, 1); g.Children.Add(scoreBadge);

            card.Child = g; panel.Children.Add(card);
        }
        return panel;
    }

    // ── System detail — 3 parameter ──────────────────────────────────────

    private static UIElement BuildSystemDetail(
        SystemEvaluation? sys,
        List<TuningRecord> tunings,
        List<AiUsageRecord> aiUsage,
        List<SimulationResult> sims)
    {
        var panel = new StackPanel { Spacing = 10 };

        if (sys == null && tunings.Count == 0 && aiUsage.Count == 0 && sims.Count == 0)
            return MutedNote("Belum ada data penilaian sistem.");

        // ── Kartu ringkasan skor sistem ───────────────────────────────────
        if (sys != null)
        {
            var summCard = new Border
            {
                Background      = SafeThemeBrush("AccentFillColorTertiaryBrush"),
                CornerRadius    = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
            };
            var summGrid = new Grid { ColumnSpacing = 10 };
            summGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            summGrid.Children.Add(new TextBlock
            {
                Text = "Total Skor Sistem", FontSize = 13, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var scoreTb = new TextBlock
            {
                Text = $"{sys.Score:F1} / 100", FontSize = 22, FontWeight = FontWeights.Bold,
                Foreground = SafeThemeBrush("AccentTextFillColorPrimaryBrush"),
            };
            Grid.SetColumn(scoreTb, 1); summGrid.Children.Add(scoreTb);
            summCard.Child = summGrid;
            panel.Children.Add(summCard);
        }

        // ── 1. Rekam Tuning Parameter (maks 40 poin) ──────────────────────
        panel.Children.Add(BuildSystemParamCard(
            "1. Rekam Tuning Parameter",
            tunings.Any() ? $"{tunings.Count} percobaan · {tunings.Select(t => $"{t.Kp:F1}{t.Ki:F2}{t.Kd:F2}").Distinct().Count()} unik · Terbaik: {(tunings.Any() ? tunings.Max(t => t.QualityScore) : 0):F0}/100"
                          : "Belum ada rekam tuning",
            "maks 40 poin",
            Windows.UI.Color.FromArgb(255, 245, 158, 11)));

        if (tunings.Any())
        {
            foreach (var t in tunings.OrderByDescending(x => x.QualityScore).Take(3))
            {
                var tCard = new Border
                {
                    Background = SafeThemeBrush("SubtleFillColorSecondaryBrush"),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 0, 0, 0),
                };
                var tSp = new StackPanel { Spacing = 2 };
                tSp.Children.Add(new TextBlock
                {
                    Text = $"{t.PlantLabel}  —  {t.KpKiKdStr}",
                    FontSize = 12, FontWeight = FontWeights.SemiBold,
                });
                var metaRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                if (t.RiseTime.HasValue)    metaRow.Children.Add(MutedChip($"Rise: {t.RiseTime:F2}s"));
                if (t.Overshoot.HasValue)   metaRow.Children.Add(MutedChip($"OS: {t.Overshoot:F1}%"));
                if (t.SettlingTime.HasValue) metaRow.Children.Add(MutedChip($"Ts: {t.SettlingTime:F2}s"));
                if (t.SteadyStateError.HasValue) metaRow.Children.Add(MutedChip($"SSE: {t.SteadyStateError:F2}%"));
                tSp.Children.Add(metaRow);
                tSp.Children.Add(new TextBlock
                {
                    Text = $"Kualitas: {t.QualityScore:F0}/100 — {t.QualityStr}  ·  {t.RecordedAt:dd MMM HH:mm}",
                    FontSize = 11, Opacity = 0.55,
                });
                tCard.Child = tSp;
                panel.Children.Add(tCard);
            }
            if (tunings.Count > 3)
                panel.Children.Add(MutedNote($"... dan {tunings.Count - 3} percobaan lainnya"));
        }

        // ── 2. Penggunaan AI (maks 20 poin) ──────────────────────────────
        int prodAi = aiUsage.Count(a => a.IsProductive);
        panel.Children.Add(BuildSystemParamCard(
            "2. Penggunaan AI",
            aiUsage.Any() ? $"{aiUsage.Count} sesi · {prodAi} produktif"
                          : "Belum ada rekam penggunaan AI",
            "maks 20 poin",
            Windows.UI.Color.FromArgb(255, 139, 92, 246)));

        if (aiUsage.Any())
        {
            foreach (var a in aiUsage.OrderByDescending(x => x.SessionAt).Take(4))
            {
                var aCard = new Border
                {
                    Background = SafeThemeBrush("SubtleFillColorSecondaryBrush"),
                    CornerRadius = new CornerRadius(6), Padding = new Thickness(12, 8, 12, 8),
                };
                var aSp = new StackPanel { Spacing = 2 };
                aSp.Children.Add(new TextBlock
                {
                    Text = a.Topic, FontSize = 12, FontWeight = FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                });
                var row2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                row2.Children.Add(MutedChip($"{a.AiProvider}  ·  {a.MessageCount} pesan  ·  {a.SessionAtStr}"));
                var pill = new Border
                {
                    Background = new SolidColorBrush(a.IsProductive
                        ? Windows.UI.Color.FromArgb(30, 16, 124, 16)
                        : Windows.UI.Color.FromArgb(30, 150, 150, 150)),
                    CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
                };
                pill.Child = new TextBlock
                {
                    Text = a.IsProductive ? "Produktif" : "Eksplorasi",
                    FontSize = 10, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(a.IsProductive
                        ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
                        : Windows.UI.Color.FromArgb(255, 120, 120, 120)),
                };
                row2.Children.Add(pill);
                aSp.Children.Add(row2);
                aCard.Child = aSp;
                panel.Children.Add(aCard);
            }
        }

        // ── 3. Hasil Simulasi (maks 40 poin) ─────────────────────────────
        double bestSim = sims.Any() ? sims.Max(s => s.Score) : 0;
        panel.Children.Add(BuildSystemParamCard(
            "3. Hasil Simulasi",
            sims.Any() ? $"{sims.Count} sesi · Terbaik: {bestSim:F1}/100"
                       : "Belum ada sesi simulasi",
            "maks 40 poin",
            Windows.UI.Color.FromArgb(255, 16, 185, 129)));

        return panel;
    }

    private static Border BuildSystemParamCard(string title, string summary, string cap, Windows.UI.Color accent)
    {
        var card = new Border
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(0, 0, 0, 0),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(20, accent.R, accent.G, accent.B)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
        };
        var sp = new StackPanel { Spacing = 2 };
        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
        });
        var capTb = new TextBlock
        {
            Text = cap, FontSize = 10, Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(capTb, 1); header.Children.Add(capTb);
        sp.Children.Add(header);
        sp.Children.Add(new TextBlock { Text = summary, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        card.Child = sp;
        return card;
    }

    // ── File yang diunggah mahasiswa ──────────────────────────────────────

    private static UIElement BuildUploadedFiles(List<GroupActivity> uploads)
    {
        var panel = new StackPanel { Spacing = 6 };
        foreach (var u in uploads)
        {
            var card = new Border
            {
                Background      = SafeThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush     = SafeThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding         = new Thickness(12, 9, 12, 9),
            };
            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Ikon berdasarkan ekstensi
            string ext = System.IO.Path.GetExtension(u.FileName ?? "").ToLowerInvariant();
            string glyph = ext switch
            {
                ".pdf"  => "",  // dokumen
                ".docx" or ".doc" => "",
                ".xlsx" or ".xls" => "",
                ".pptx" or ".ppt" => "",
                ".zip"  or ".rar" => "",
                ".mp4"  or ".avi" or ".mkv" => "",
                ".png"  or ".jpg" or ".jpeg" => "",
                _       => "",
            };
            var icon = new FontIcon
            {
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                Glyph = glyph, FontSize = 20,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212)),
            };
            g.Children.Add(icon);

            var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = u.FileName ?? u.Description, FontSize = 12, FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            info.Children.Add(new TextBlock
            {
                Text = u.ActivityTime.ToString("dd MMM yyyy, HH:mm"),
                FontSize = 10, Opacity = 0.5,
            });
            Grid.SetColumn(info, 1); g.Children.Add(info);

            // Tombol buka file (jika path tersedia)
            if (!string.IsNullOrEmpty(u.FilePath))
            {
                var openBtn = new Button
                {
                    Content = "Buka",
                    FontSize = 11,
                    Padding = new Thickness(10, 4, 10, 4),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                string filePath = u.FilePath;
                openBtn.Click += async (_, _) =>
                {
                    try
                    {
                        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                        await Windows.System.Launcher.LaunchFileAsync(file);
                    }
                    catch
                    {
                        // File demo — path tidak nyata, tampilkan info saja
                        var infoDialog = new ContentDialog
                        {
                            Title = "Info File",
                            Content = $"File: {u.FileName}\nPath: {filePath}\n\n(Dalam implementasi nyata, file akan dibuka dari server.)",
                            CloseButtonText = "OK",
                            XamlRoot = openBtn.XamlRoot,
                        };
                        await infoDialog.ShowAsync();
                    }
                };
                Grid.SetColumn(openBtn, 2); g.Children.Add(openBtn);
            }

            card.Child = g;
            panel.Children.Add(card);
        }
        return panel;
    }

    // ── Submit history vs deadline ────────────────────────────────────────

    private static UIElement BuildSubmitHistory(
        List<GroupActivity> submits, DateTime? deadline)
    {
        var panel = new StackPanel { Spacing = 8 };
        foreach (var s in submits)
        {
            bool onTime = !deadline.HasValue || s.ActivityTime <= deadline.Value;
            TimeSpan diff = deadline.HasValue ? s.ActivityTime - deadline.Value : TimeSpan.Zero;

            string statusText = !deadline.HasValue ? "—"
                : onTime ? "Tepat Waktu" : "Terlambat";
            string diffText = deadline.HasValue ? FormatDiff(diff) : "";

            var accentColor = onTime
                ? Windows.UI.Color.FromArgb(255, 16, 124, 16)
                : Windows.UI.Color.FromArgb(255, 196, 43, 28);
            var bgColor = onTime
                ? Windows.UI.Color.FromArgb(25, 16, 124, 16)
                : Windows.UI.Color.FromArgb(25, 196, 43, 28);

            var card = new Border
            {
                BorderBrush     = SafeThemeBrush("CardStrokeColorDefaultBrush"),
                Background      = SafeThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(7),
                Padding         = new Thickness(12, 10, 12, 10),
            };

            var g = new Grid { ColumnSpacing = 10 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 3 };
            info.Children.Add(new TextBlock
            {
                Text = s.Description, FontSize = 12, FontWeight = FontWeights.SemiBold,
            });
            var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            times.Children.Add(MutedChip($"Submit: {s.ActivityTime:dd MMM yyyy, HH:mm}"));
            if (deadline.HasValue)
                times.Children.Add(MutedChip($"Deadline: {deadline.Value:dd MMM yyyy, HH:mm}"));
            info.Children.Add(times);
            if (!string.IsNullOrEmpty(diffText))
                info.Children.Add(new TextBlock
                {
                    Text = diffText, FontSize = 11,
                    Foreground = new SolidColorBrush(accentColor),
                });
            g.Children.Add(info);

            var pill = new Border
            {
                Background        = new SolidColorBrush(bgColor),
                CornerRadius      = new CornerRadius(6),
                Padding           = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            pill.Child = new TextBlock
            {
                Text = statusText, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(accentColor),
            };
            Grid.SetColumn(pill, 1); g.Children.Add(pill);

            card.Child = g; panel.Children.Add(card);
        }
        return panel;
    }

    private static string FormatDiff(TimeSpan diff)
    {
        if (diff.TotalSeconds <= 0)
        {
            var abs = diff.Negate();
            if (abs.TotalDays >= 1) return $"{(int)abs.TotalDays} hari sebelum deadline";
            if (abs.TotalHours >= 1) return $"{(int)abs.TotalHours} jam sebelum deadline";
            return $"{(int)abs.TotalMinutes} menit sebelum deadline";
        }
        if (diff.TotalDays >= 1) return $"{(int)diff.TotalDays} hari terlambat";
        if (diff.TotalHours >= 1) return $"{(int)diff.TotalHours} jam terlambat";
        return $"{(int)diff.TotalMinutes} menit terlambat";
    }

    private static UIElement MutedNote(string text) =>
        new TextBlock { Text = text, FontSize = 12, Opacity = 0.5, Margin = new Thickness(0, 0, 0, 4) };

    // ── Score breakdown: Peer / Sistem / Dosen + Final ──────────────────

    private static UIElement BuildScoreBreakdown(StudentGradeSummary? s)
    {
        var panel = new StackPanel { Spacing = 10 };

        if (s == null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Data nilai belum tersedia untuk tugas ini.",
                FontSize = 12, Opacity = 0.5,
            });
            return panel;
        }

        AddScoreRow(panel, "Nilai Peer",   "20%", s.PeerScore,
            Windows.UI.Color.FromArgb(255, 59,  130, 246));   // biru
        AddScoreRow(panel, "Nilai Sistem", "30%", s.SystemScore,
            Windows.UI.Color.FromArgb(255, 139, 92,  246));   // ungu
        AddScoreRow(panel, "Nilai Dosen",  "50%", s.LecturerScore,
            Windows.UI.Color.FromArgb(255, 16,  185, 129));   // hijau

        // Divider
        panel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill   = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 128, 128, 128)),
        });

        // Final score
        var finalGrid = new Grid { ColumnSpacing = 10 };
        finalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        finalGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        finalGrid.Children.Add(new TextBlock
        {
            Text              = "Nilai Final",
            FontSize          = 13,
            FontWeight        = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var finalVal = new TextBlock
        {
            Text       = s.FinalScore.HasValue ? s.FinalScore.Value.ToString("F1") : "—",
            FontSize   = 24,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(GradeColor(s.LetterGrade)),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(finalVal, 1);
        finalGrid.Children.Add(finalVal);

        panel.Children.Add(finalGrid);
        return panel;
    }

    private static void AddScoreRow(
        StackPanel parent, string label, string weight, double? score,
        Windows.UI.Color barColor)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        row.Children.Add(new TextBlock
        {
            Text              = label,
            FontSize          = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var wtTb = new TextBlock { Text = weight, FontSize = 11, Opacity = 0.5, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(wtTb, 1);
        row.Children.Add(wtTb);

        // Progress bar
        double fraction = score.HasValue ? Math.Clamp(score.Value / 100.0, 0, 1) : 0;
        var barBack = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 8,
            Fill   = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 128, 128, 128)),
            RadiusX = 4, RadiusY = 4,
        };
        var barFront = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height  = 8,
            Fill    = new SolidColorBrush(barColor),
            RadiusX = 4, RadiusY = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var barGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        barGrid.Children.Add(barBack);
        barGrid.Children.Add(barFront);
        double cap = fraction;
        barGrid.SizeChanged += (_, args) => barFront.Width = args.NewSize.Width * cap;
        Grid.SetColumn(barGrid, 2);
        row.Children.Add(barGrid);

        var scoreTb = new TextBlock
        {
            Text       = score.HasValue ? score.Value.ToString("F1") : "—",
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        Grid.SetColumn(scoreTb, 3);
        row.Children.Add(scoreTb);

        parent.Children.Add(row);
    }

    // ── Simulation list ──────────────────────────────────────────────────

    private static UIElement BuildSimulationList(List<SimulationResult> sims)
    {
        var panel = new StackPanel { Spacing = 8 };

        foreach (var sim in sims.OrderByDescending(s => s.StartedAt))
        {
            var scoreColor = new SolidColorBrush(sim.Score >= 85
                ? Windows.UI.Color.FromArgb(255, 16,  124, 16)
                : sim.Score >= 70
                    ? Windows.UI.Color.FromArgb(255, 0,  120, 212)
                    : Windows.UI.Color.FromArgb(255, 196, 43, 28));

            var card = new Border
            {
                Background      = SafeThemeBrush("CardBackgroundFillColorDefaultBrush"),
                BorderBrush     = SafeThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(8),
                Padding         = new Thickness(14, 12, 14, 12),
            };

            var rowGrid = new Grid { ColumnSpacing = 12 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });

            // Info
            var info = new StackPanel { Spacing = 4 };
            info.Children.Add(new TextBlock
            {
                Text       = sim.SessionName,
                FontSize   = 13,
                FontWeight = FontWeights.SemiBold,
            });
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            meta.Children.Add(MutedChip($"📅 {sim.StartedAtStr}"));
            meta.Children.Add(MutedChip($"⏱ {sim.DurationStr}"));
            meta.Children.Add(MutedChip($"Target {sim.ParameterStr}"));
            meta.Children.Add(MutedChip($"Stabilitas {sim.StabilityStr}"));
            info.Children.Add(meta);
            Grid.SetColumn(info, 0);
            rowGrid.Children.Add(info);

            // Score badge
            var scoreBadge = new Border
            {
                Background        = scoreColor,
                CornerRadius      = new CornerRadius(6),
                Padding           = new Thickness(8, 5, 8, 5),
                VerticalAlignment = VerticalAlignment.Center,
            };
            scoreBadge.Child = new TextBlock
            {
                Text        = sim.ScoreStr,
                FontSize    = 15,
                FontWeight  = FontWeights.Bold,
                Foreground  = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(scoreBadge, 1);
            rowGrid.Children.Add(scoreBadge);

            card.Child = rowGrid;
            panel.Children.Add(card);
        }

        return panel;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static UIElement BuildSectionTitle(string title)
    {
        var sp = new StackPanel { Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        sp.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold,
        });
        sp.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Height = 1,
            Fill   = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 128, 128, 128)),
        });
        return sp;
    }

    private static TextBlock MutedChip(string text) =>
        new() { Text = text, FontSize = 11, Opacity = 0.55 };

    private static string BuildInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? "?" :
               parts.Length == 1 ? name[..Math.Min(2, name.Length)].ToUpper() :
               $"{parts[0][0]}{parts[1][0]}".ToUpper();
    }

    private static Windows.UI.Color GradeColor(string grade) => grade switch
    {
        "A" or "A-"          => Windows.UI.Color.FromArgb(255, 16,  124, 16),
        "B+" or "B" or "B-"  => Windows.UI.Color.FromArgb(255, 0,   120, 212),
        "C+" or "C"          => Windows.UI.Color.FromArgb(255, 200, 130, 0),
        _                    => Windows.UI.Color.FromArgb(255, 196, 43,  28),
    };

    private static Brush SafeThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var val) && val is Brush b)
            return b;
        return new SolidColorBrush(Colors.Transparent);
    }
}
