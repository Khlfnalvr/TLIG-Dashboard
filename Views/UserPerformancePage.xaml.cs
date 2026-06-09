using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Controls;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

public sealed partial class UserPerformancePage : Page
{
    private LocalizationManager Lang => App.Lang;
    private readonly GradingService _grading = GradingService.Instance;
    private const string DefaultAssignmentId = "ASGN-001";

    private readonly Dictionary<string, string> _nameToStudentId = new()
    {
        ["Rizky Pratama"]  = "STU001",
        ["Siti Nurhaliza"] = "STU002",
        ["Ahmad Fauzi"]    = "STU003",
        ["Dewi Anggraini"] = "STU004",
        ["Budi Santoso"]   = "STU005",
    };

    public UserPerformancePage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Lang.PropertyChanged += OnLangChanged;
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => Lang.PropertyChanged -= OnLangChanged;

    private void OnLangChanged(object? s, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() => _ = LoadAsync());

    private async System.Threading.Tasks.Task LoadAsync()
    {
        var users = UserStore.Instance.GetUsers()
            .Where(u => u.Enabled)
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tasks    = TaskStore.Instance.GetTasks();
        int totalCount = tasks.Count;

        var rows = new List<UserPerformanceRow>();
        double totalTaskPct = 0;
        int scoredCount = 0;
        double totalFinal = 0;
        int gradedCount = 0;

        foreach (var u in users)
        {
            var completed  = TaskStore.Instance.GetCompletedIds(u.Username);
            int done       = completed.Count;
            double pct     = totalCount > 0 ? (double)done / totalCount * 100 : 0;

            var grading = await FindGradingDataAsync(u.DisplayName);

            double? peer  = grading?.PeerScore;
            double? sys   = grading?.SystemScore;
            double? lec   = grading?.LecturerScore;
            double? final = grading?.FinalScore;

            if (final.HasValue) { totalFinal += final.Value; scoredCount++; }
            if (lec.HasValue) gradedCount++;

            totalTaskPct += pct;

            rows.Add(new UserPerformanceRow
            {
                Username         = u.Username,
                DisplayName      = u.DisplayName,
                RoleLabel        = RoleLabelText(u.Role),
                RoleTintBrush    = RoleTintFor(u.Role),
                TasksDone        = done,
                TasksTotal       = totalCount,
                TaskProgressPct  = pct,
                PeerScore        = peer,
                SystemScore      = sys,
                LecturerScore    = lec,
                FinalScore       = final,
                PeerLabel        = Lang.Up_PeerLabel,
            });
        }

        int userCount = rows.Count;
        StatTotal.Text      = userCount.ToString();
        StatAvgTasks.Text   = userCount > 0 ? $"{(totalTaskPct / userCount):F0}%" : "—";
        StatAvgScore.Text   = scoredCount > 0 ? $"{(totalFinal / scoredCount):F1}" : "—";
        StatGraded.Text     = $"{gradedCount}/{userCount}";

        UserListRepeater.ItemsSource = rows;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async System.Threading.Tasks.Task<StudentGradeSummary?> FindGradingDataAsync(string displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName) &&
            _nameToStudentId.TryGetValue(displayName, out var sid))
        {
            var summaries = await _grading.GetGradeSummaryByAssignmentAsync(DefaultAssignmentId);
            return summaries.FirstOrDefault(s => s.StudentId == sid);
        }
        return null;
    }

    // Klik baris mahasiswa → tampilkan detail + hasil simulasi
    private async void UserRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not UserPerformanceRow row) return;

        // Cari student ID dari display name
        if (!_nameToStudentId.TryGetValue(row.DisplayName, out var studentId))
            studentId = row.Username;   // fallback ke username

        await StudentDetailDialog.ShowAsync(XamlRoot, studentId, row.DisplayName, DefaultAssignmentId);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
        => _ = LoadAsync();

    private string RoleLabelText(string role) => role switch
    {
        UserRoles.Dosen     => Lang.Um_RoleDosen,
        UserRoles.Asisten   => Lang.Um_RoleAsisten,
        _                   => Lang.Um_RoleMahasiswa,
    };

    private static SolidColorBrush RoleTintFor(string role) => role switch
    {
        UserRoles.Dosen   => new(Windows.UI.Color.FromArgb(30, 59, 130, 246)),
        UserRoles.Asisten => new(Windows.UI.Color.FromArgb(30, 139, 92, 246)),
        _                 => new(Windows.UI.Color.FromArgb(30, 16, 185, 129)),
    };
}

public sealed class UserPerformanceRow
{
    public string Username       { get; init; } = "";
    public string DisplayName    { get; init; } = "";
    public string RoleLabel      { get; init; } = "";
    public SolidColorBrush? RoleTintBrush { get; init; }
    public int    TasksDone      { get; init; }
    public int    TasksTotal     { get; init; }
    public double TaskProgressPct { get; init; }

    public double? PeerScore     { get; init; }
    public double? SystemScore   { get; init; }
    public double? LecturerScore { get; init; }
    public double? FinalScore    { get; init; }
    public string PeerLabel      { get; init; } = "";

    public string TaskProgressText =>
        TasksTotal > 0 ? $"{TasksDone}/{TasksTotal} ({TaskProgressPct:F0}%)" : "—";

    public double ProgressBarWidth =>
        TasksTotal > 0 ? Math.Clamp(TaskProgressPct, 0, 100) : 0;

    public SolidColorBrush ProgressFillBrush =>
        new(TaskProgressPct >= 80 ? Windows.UI.Color.FromArgb(255, 16, 185, 129)
          : TaskProgressPct >= 50 ? Windows.UI.Color.FromArgb(255, 245, 158, 11)
          : Windows.UI.Color.FromArgb(255, 196, 43, 28));

    public string PeerScoreStr =>
        PeerScore.HasValue ? PeerScore.Value.ToString("F1") : "—";

    public string SystemScoreStr =>
        SystemScore.HasValue ? SystemScore.Value.ToString("F1") : "—";

    public string LecturerScoreStr =>
        LecturerScore.HasValue ? LecturerScore.Value.ToString("F1") : "—";

    public string FinalScoreStr =>
        FinalScore.HasValue ? FinalScore.Value.ToString("F1") : "—";

    public SolidColorBrush FinalScoreBgBrush =>
        new(FinalScore.HasValue
            ? Windows.UI.Color.FromArgb(30, 16, 185, 129)
            : Windows.UI.Color.FromArgb(20, 128, 128, 128));

    public SolidColorBrush FinalScoreFgBrush =>
        new(FinalScore.HasValue
            ? Windows.UI.Color.FromArgb(255, 16, 185, 129)
            : Windows.UI.Color.FromArgb(255, 128, 128, 128));

    public string LetterGrade
    {
        get
        {
            var s = FinalScore;
            if (s == null) return "—";
            return s >= 85 ? "A" : s >= 80 ? "A-" : s >= 75 ? "B+" :
                   s >= 70 ? "B" : s >= 65 ? "B-" : s >= 60 ? "C+" :
                   s >= 55 ? "C" : s >= 50 ? "D" : "E";
        }
    }

    public SolidColorBrush GradeBadgeColor =>
        new(LetterGrade switch
        {
            "A" or "A-" => Windows.UI.Color.FromArgb(255, 16, 124, 16),
            "B+" or "B" or "B-" => Windows.UI.Color.FromArgb(255, 0, 120, 212),
            "C+" or "C" => Windows.UI.Color.FromArgb(255, 200, 130, 0),
            "D" or "E" => Windows.UI.Color.FromArgb(255, 196, 43, 28),
            _ => Windows.UI.Color.FromArgb(255, 128, 128, 128),
        });
}
