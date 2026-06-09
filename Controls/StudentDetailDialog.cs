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
        string assignmentId)
    {
        var grading   = GradingService.Instance;
        var summaries = await grading.GetGradeSummaryByAssignmentAsync(assignmentId);
        var summary   = summaries.FirstOrDefault(s => s.StudentId == studentId);
        var sims      = await grading.GetSimulationResultsForStudentAsync(studentId);

        var content = new StackPanel { Width = 500, Spacing = 16 };

        content.Children.Add(BuildHeader(studentId, studentName, summary));

        content.Children.Add(BuildSectionTitle("Nilai Keseluruhan"));
        content.Children.Add(BuildScoreBreakdown(summary));

        content.Children.Add(BuildSectionTitle(
            sims.Count > 0 ? $"Hasil Simulasi  ({sims.Count} sesi)" : "Hasil Simulasi"));
        content.Children.Add(sims.Count > 0
            ? BuildSimulationList(sims)
            : new TextBlock { Text = "Belum ada sesi simulasi tercatat.", FontSize = 12, Opacity = 0.5 });

        var scroll = new ScrollViewer
        {
            Content = content,
            MaxHeight = 540,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var dialog = new ContentDialog
        {
            Title           = "Detail Mahasiswa",
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
