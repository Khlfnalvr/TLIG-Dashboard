using System.ComponentModel;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace TLIGDashboard.Views;

/// <summary>
/// Riwayat pemakaian plant HE, dalam dua tabel yang menjawab dua pertanyaan
/// berbeda:
///
///   • <b>Riwayat percobaan</b> — kombinasi parameter apa saja yang sudah pernah
///     dijalankan, oleh siapa, berapa lama, hasilnya bagaimana, dan berapa kali
///     hasil itu dipakai ulang tanpa menjalankan plant lagi (dari cache parameter).
///   • <b>Riwayat antrian</b> — siapa memegang plant kapan, siapa mengantre, dan
///     siapa mengambil alih giliran siapa (dari log antrian).
///
/// Keduanya dibaca lewat <see cref="HeControlService"/>, jadi halaman ini sama
/// saja di Server (langsung ke database) dan di Client (lewat HTTP ke Server).
/// Halaman staf: log antrian dan daftar percobaan memperlihatkan siapa
/// mengerjakan apa, dan Server menolaknya untuk peran lain — pembatasan di menu
/// hanyalah cerminan aturan itu, bukan penjaganya.
/// </summary>
public sealed partial class HeQueueHistoryPage : Page
{
    /// <summary>
    /// Baris yang diambil per tabel. Praktikum satu semester tidak akan
    /// mendekatinya, dan tabel yang lebih panjang dari ini tidak lagi terbaca
    /// sebagai riwayat — Server sendiri membatasi di 500.
    /// </summary>
    private const int MaxRows = 200;

    private LocalizationManager Lang => App.Lang;

    // Warna latar lencana, mengikuti palet UserPerformancePage (alfa 0x26 supaya
    // tetap terbaca di tema terang maupun gelap).
    private static readonly SolidColorBrush TintGreen  = new(Color.FromArgb(0x26, 0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush TintBlue   = new(Color.FromArgb(0x26, 0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush TintOrange = new(Color.FromArgb(0x26, 0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush TintRed    = new(Color.FromArgb(0x26, 0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush TintGray   = new(Color.FromArgb(0x26, 0x80, 0x80, 0x80));

    private bool _loading;

    // Baris yang sedang tampil, disimpan apa adanya untuk diekspor: yang masuk
    // file harus persis yang dilihat di layar, bukan hasil pembacaan ulang yang
    // bisa saja sudah berbeda.
    private IReadOnlyList<HeParameterRun> _runs = [];
    private IReadOnlyList<HeQueueLogEntry> _log = [];

    public HeQueueHistoryPage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Lang.PropertyChanged += OnLangChanged;
        RefreshLocalizedText();
        _ = LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => Lang.PropertyChanged -= OnLangChanged;

    // Baris tabel menyimpan teks yang sudah dirakit (nama kejadian, status,
    // metrik), jadi ganti bahasa berarti merakit ulang isinya — bukan sekadar
    // mengganti judul kolom.
    private void OnLangChanged(object? sender, PropertyChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            RefreshLocalizedText();
            _ = LoadAsync();
        });

    private void RefreshLocalizedText()
    {
        TitleText.Text    = Lang.HeH_Title;
        SubtitleText.Text = Lang.HeH_Subtitle;
        RefreshText.Text  = Lang.HeH_Refresh;
        ExportRunsText.Text = Lang.HeH_ExportRuns;
        ExportLogText.Text  = Lang.HeH_ExportLog;

        StatTotalLabel.Text     = Lang.HeH_StatTotalRuns;
        StatCompletedLabel.Text = Lang.HeH_StatCompleted;
        StatReusesLabel.Text    = Lang.HeH_StatReuses;
        StatLastRunLabel.Text   = Lang.HeH_StatLastRun;

        RunsTitle.Text     = Lang.HeH_RunsTitle;
        RunColWhen.Text    = Lang.HeH_ColWhen;
        RunColUser.Text    = Lang.HeH_ColUser;
        RunColParams.Text  = Lang.HeH_ColParams;
        RunColDuration.Text = Lang.HeH_ColDuration;
        RunColStatus.Text  = Lang.HeH_ColStatus;
        RunColMetrics.Text = Lang.HeH_ColMetrics;
        RunColReuse.Text   = Lang.HeH_ColReuse;
        RunsEmptyText.Text = Lang.HeH_RunsEmpty;

        LogTitle.Text     = Lang.HeH_LogTitle;
        LogColWhen.Text   = Lang.HeH_ColWhen;
        LogColEvent.Text  = Lang.HeH_ColEvent;
        LogColUser.Text   = Lang.HeH_ColUser;
        LogColNote.Text   = Lang.HeH_ColNote;
        LogEmptyText.Text = Lang.HeH_LogEmpty;

        UnreachableBar.Title   = Lang.HeH_Title;
        UnreachableBar.Message = Lang.HeH_Unreachable;
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadAsync();

    // ── Ekspor CSV ──────────────────────────────────────────────────────────

    private async void ExportRuns_Click(object sender, RoutedEventArgs e)
        => await SaveCsvAsync(HeCsvExport.Runs(_runs), "riwayat-percobaan-he");

    private async void ExportLog_Click(object sender, RoutedEventArgs e)
        => await SaveCsvAsync(HeCsvExport.QueueLog(_log), "riwayat-antrian-he");

    /// <summary>
    /// Menanyakan lokasi simpan lalu menulis CSV-nya. Ditulis UTF-8 <b>dengan
    /// BOM</b>: tanpa itu Excel membaca file sebagai ANSI dan "°C" berikut huruf
    /// beraksen di nama mahasiswa jadi rusak.
    /// </summary>
    private async Task SaveCsvAsync(string csv, string baseName)
    {
        if (App.CurrentWindow is not { } window) return;

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName      = $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}",
            };
            picker.FileTypeChoices.Add(Lang.Get("Export_FileTypeCsv"), new[] { ".csv" });

            // WinUI 3: picker perlu tahu jendela pemiliknya, kalau tidak ia
            // melempar saat dibuka (pola yang sama dipakai ChartExportService).
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(window));

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;   // dibatalkan pengguna — bukan kegagalan

            await FileIO.WriteBytesAsync(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(csv));

            ExportBar.Severity = InfoBarSeverity.Success;
            ExportBar.Title    = Lang.HeH_ExportSaved;
            ExportBar.Message  = file.Path;
            ExportBar.IsOpen   = true;
        }
        catch (Exception ex)
        {
            ExportBar.Severity = InfoBarSeverity.Error;
            ExportBar.Title    = Lang.HeH_ExportFailed;
            ExportBar.Message  = ex.Message;
            ExportBar.IsOpen   = true;
        }
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        try
        {
            var history = await HeControlService.GetRecentRunsAsync(MaxRows);
            var log     = await HeControlService.GetRecentLogAsync(MaxRows);

            _runs = history.Runs;
            _log  = log;
            ExportRunsButton.IsEnabled = _runs.Count > 0;
            ExportLogButton.IsEnabled  = _log.Count > 0;

            // Bedakan "tidak terbaca" dari "memang masih kosong": praktikum yang
            // belum dimulai dan Server yang tidak terjangkau kelihatan sama di
            // layar kalau keduanya cuma menampilkan tabel kosong.
            UnreachableBar.IsOpen = !history.Ok;

            ApplyStats(history.Stats);

            var runRows = new List<HeRunHistoryRow>();
            foreach (var run in history.Runs) runRows.Add(BuildRunRow(run));
            RunsRepeater.ItemsSource = runRows;
            RunsEmptyText.Visibility = runRows.Count == 0 && history.Ok
                ? Visibility.Visible : Visibility.Collapsed;

            var logRows = new List<HeQueueLogRow>();
            foreach (var entry in log) logRows.Add(BuildLogRow(entry));
            LogRepeater.ItemsSource = logRows;
            LogEmptyText.Visibility = logRows.Count == 0 && history.Ok
                ? Visibility.Visible : Visibility.Collapsed;
        }
        catch
        {
            UnreachableBar.IsOpen = true;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            _loading = false;
        }
    }

    private void ApplyStats(HeParameterCacheStats? stats)
    {
        StatTotalValue.Text     = stats is null ? "—" : stats.TotalRuns.ToString();
        StatCompletedValue.Text = stats is null ? "—" : stats.CompletedRuns.ToString();
        StatReusesValue.Text    = stats is null ? "—" : stats.TotalReuses.ToString();
        StatLastRunValue.Text   = stats?.LastRunUtc is { } last
            ? last.ToLocalTime().ToString("dd MMM HH:mm") : "—";
    }

    // ── Perakitan baris ─────────────────────────────────────────────────────

    private HeRunHistoryRow BuildRunRow(HeParameterRun run)
    {
        var m = run.Metrics;
        string metrics = m is null ? "—" : string.Join(" · ", new[]
        {
            m.RiseTimeSeconds     is { } rise    ? $"tr {rise:0.#}s"       : null,
            m.SettlingTimeSeconds is { } settle  ? $"ts {settle:0.#}s"     : null,
            m.OvershootPercent    is { } over    ? $"OS {over:0.#}%"       : null,
            m.SteadyStateError    is { } err     ? $"e {err:0.##}°C"  : null,
        }.Where(part => part is not null));

        return new HeRunHistoryRow
        {
            When       = run.StartedAtUtc.ToLocalTime().ToString("dd MMM HH:mm"),
            User       = string.IsNullOrWhiteSpace(run.RequestedByName)
                ? (run.RequestedByUserId ?? "—") : run.RequestedByName!,
            Parameters = $"SP {run.Input.Sp:0.##}  Kc {run.Input.Kc:0.###}  " +
                         $"Ti {run.Input.Ti:0.###}  Td {run.Input.Td:0.###}  V {run.Input.Pump:0.#}%",
            Duration   = run.DurationSeconds is { } seconds ? FormatDuration(seconds) : "—",
            Status     = StatusLabel(run.Status),
            StatusTint = run.Status switch
            {
                HeParameterRunStatus.Completed => TintGreen,
                HeParameterRunStatus.Failed    => TintRed,
                _                              => TintOrange,
            },
            Metrics = metrics.Length == 0 ? "—" : metrics,
            Reuse   = run.ReuseCount > 0 ? Lang.Format(nameof(Lang.HeH_ReuseTimes), run.ReuseCount) : "—",
        };
    }

    private HeQueueLogRow BuildLogRow(HeQueueLogEntry entry)
    {
        // Lawan main hanya disimpan sebagai user_id (akunnya bisa saja sudah
        // dihapus), jadi ditampilkan apa adanya di belakang catatan.
        string note = entry.Note ?? "";
        if (!string.IsNullOrWhiteSpace(entry.RelatedUserId))
            note = note.Length == 0 ? $"↔ {entry.RelatedUserId}" : $"{note}  ↔ {entry.RelatedUserId}";

        return new HeQueueLogRow
        {
            When      = entry.OccurredAtUtc.ToLocalTime().ToString("dd MMM HH:mm:ss"),
            Event     = EventLabel(entry.EventType),
            EventTint = EventTint(entry.EventType),
            User      = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.UserId : entry.DisplayName!,
            Role      = entry.Priority is { } priority ? HeQueuePriorityMap.Label(priority) : "",
            Note      = note,
        };
    }

    private string StatusLabel(HeParameterRunStatus status) => status switch
    {
        HeParameterRunStatus.Completed => Lang.HeH_StatusCompleted,
        HeParameterRunStatus.Failed    => Lang.HeH_StatusFailed,
        _                              => Lang.HeH_StatusAborted,
    };

    /// <summary>
    /// Nama kejadian di log. Nilainya berasal dari kolom <c>event_type</c> yang
    /// dibatasi CHECK di skema, jadi daftar ini memang tertutup; yang tidak
    /// dikenal ditampilkan apa adanya supaya skema baru tidak membuat barisnya
    /// hilang dari layar.
    /// </summary>
    private string EventLabel(string eventType) => eventType switch
    {
        "Granted"           => Lang.HeH_EvGranted,
        "GrantedFromQueue"  => Lang.HeH_EvGrantedFromQueue,
        "GrantedByOverride" => Lang.HeH_EvGrantedByOverride,
        "Queued"            => Lang.HeH_EvQueued,
        "Released"          => Lang.HeH_EvReleased,
        "Cancelled"         => Lang.HeH_EvCancelled,
        "Overridden"        => Lang.HeH_EvOverridden,
        "Expired"           => Lang.HeH_EvExpired,
        "ForceReleased"     => Lang.HeH_EvForceReleased,
        _                   => eventType,
    };

    private static SolidColorBrush EventTint(string eventType) => eventType switch
    {
        "Granted" or "GrantedFromQueue" => TintGreen,
        "GrantedByOverride"             => TintBlue,
        "Queued"                        => TintGray,
        "Released"                      => TintGray,
        "Overridden" or "Expired" or "ForceReleased" => TintRed,
        _                               => TintOrange,
    };

    private static string FormatDuration(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}

/// <summary>Satu baris tabel riwayat percobaan (dibaca DataTemplate lewat {Binding}).</summary>
public sealed class HeRunHistoryRow
{
    public string When       { get; init; } = "";
    public string User       { get; init; } = "";
    public string Parameters { get; init; } = "";
    public string Duration   { get; init; } = "";
    public string Status     { get; init; } = "";
    public string Metrics    { get; init; } = "";
    public string Reuse      { get; init; } = "";
    public SolidColorBrush? StatusTint { get; init; }
}

/// <summary>Satu baris tabel riwayat antrian.</summary>
public sealed class HeQueueLogRow
{
    public string When  { get; init; } = "";
    public string Event { get; init; } = "";
    public string User  { get; init; } = "";
    public string Role  { get; init; } = "";
    public string Note  { get; init; } = "";
    public SolidColorBrush? EventTint { get; init; }
}
