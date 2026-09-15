using System.Globalization;
using System.Text;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Merakit isi halaman riwayat HE menjadi CSV untuk lampiran laporan praktikum.
/// Murni teks — tidak menyentuh file maupun UI, jadi bisa diuji apa adanya.
///
/// <para><b>Dibuat supaya langsung benar saat dibuka Excel.</b> Pemisah kolom
/// mengikuti pemisah daftar milik Windows yang sedang dipakai (di Indonesia
/// titik-koma), angkanya diformat dengan budaya yang sama, dan barisnya diawali
/// petunjuk <c>sep=</c> supaya Excel tetap memecah kolom dengan benar walau
/// filenya dibuka di komputer dengan setelan wilayah lain. File ditulis UTF-8
/// <i>dengan</i> BOM oleh pemanggilnya — tanpa BOM, Excel merusak "°C" dan
/// huruf beraksen.</para>
///
/// <para><b>Nama kolomnya sengaja tidak diterjemahkan.</b> Isinya data yang
/// diolah lagi (Excel, Python, MATLAB), dan nama kolom yang ikut berubah saat
/// bahasa aplikasi diganti akan mematahkan setiap rumus dan skrip yang sudah
/// dibuat mahasiswa. Yang dilokalkan hanya layarnya, bukan filenya.</para>
///
/// <para>Waktu ditulis dalam <b>waktu lokal</b> — itu yang dipakai di laporan —
/// dan nama kolomnya diakhiri <c>_local</c> supaya tidak ada yang mengira UTC.</para>
/// </summary>
public static class HeCsvExport
{
    private const string NewLine = "\r\n";

    /// <summary>Riwayat percobaan: parameter masukan, hasil, dan metriknya.</summary>
    public static string Runs(IEnumerable<HeParameterRun> runs, CultureInfo? culture = null)
    {
        var c   = culture ?? CultureInfo.CurrentCulture;
        var sep = Separator(c);
        var sb  = new StringBuilder();

        sb.Append("sep=").Append(sep).Append(NewLine);
        Row(sb, sep,
            "run_id", "started_at_local", "finished_at_local", "duration_s",
            "requested_by", "requested_by_user_id", "source", "status",
            "sp", "kc", "ti", "td", "pump",
            "rise_time_s", "settling_time_s", "overshoot_percent", "peak_value",
            "steady_state_error_c", "ise", "iae", "itae",
            "final_pv_shell_out", "final_pv_shell_in", "final_flow_tube",
            "final_flow_shell", "final_signal_percent",
            "sample_count", "reuse_count", "last_reused_at_local", "note");

        foreach (var run in runs)
        {
            var m = run.Metrics;
            Row(sb, sep,
                run.RunId.ToString(CultureInfo.InvariantCulture),
                Time(run.StartedAtUtc),
                Time(run.FinishedAtUtc),
                Num(run.DurationSeconds, c),
                run.RequestedByName ?? "",
                run.RequestedByUserId ?? "",
                run.Source.ToString(),
                run.Status.ToString(),
                Num(run.Input.Sp, c), Num(run.Input.Kc, c), Num(run.Input.Ti, c),
                Num(run.Input.Td, c), Num(run.Input.Pump, c),
                Num(m?.RiseTimeSeconds, c), Num(m?.SettlingTimeSeconds, c),
                Num(m?.OvershootPercent, c), Num(m?.PeakValue, c),
                Num(m?.SteadyStateError, c), Num(m?.Ise, c), Num(m?.Iae, c), Num(m?.Itae, c),
                Num(m?.FinalPvShellOut, c), Num(m?.FinalPvShellIn, c), Num(m?.FinalFlowTube, c),
                Num(m?.FinalFlowShell, c), Num(m?.FinalSignalPercent, c),
                run.Samples.Count.ToString(CultureInfo.InvariantCulture),
                run.ReuseCount.ToString(CultureInfo.InvariantCulture),
                Time(run.LastReusedAtUtc),
                run.Note ?? "");
        }

        return sb.ToString();
    }

    /// <summary>Riwayat antrian: siapa memegang plant kapan, dan siapa mengambil alih siapa.</summary>
    public static string QueueLog(IEnumerable<HeQueueLogEntry> entries, CultureInfo? culture = null)
    {
        var c   = culture ?? CultureInfo.CurrentCulture;
        var sep = Separator(c);
        var sb  = new StringBuilder();

        sb.Append("sep=").Append(sep).Append(NewLine);
        Row(sb, sep,
            "log_id", "occurred_at_local", "event_type", "user_id", "display_name",
            "priority", "priority_label", "request_type", "queue_id",
            "related_user_id", "note");

        foreach (var e in entries)
        {
            Row(sb, sep,
                e.LogId.ToString(CultureInfo.InvariantCulture),
                Time(e.OccurredAtUtc),
                e.EventType,
                e.UserId,
                e.DisplayName ?? "",
                e.Priority is { } p ? ((int)p).ToString(CultureInfo.InvariantCulture) : "",
                e.Priority is { } pl ? HeQueuePriorityMap.Label(pl) : "",
                e.RequestType ?? "",
                e.QueueId?.ToString(CultureInfo.InvariantCulture) ?? "",
                e.RelatedUserId ?? "",
                e.Note ?? "");
        }

        return sb.ToString();
    }

    // ── Internals ───────────────────────────────────────────────────────────

    /// <summary>
    /// Pemisah kolom: pemisah daftar milik budaya yang dipakai. Budaya yang
    /// melaporkan pemisah kosong atau berupa spasi (jarang, tapi ada) jatuh ke
    /// koma — file tanpa pemisah yang jelas tidak bisa dibaca siapa pun.
    /// </summary>
    private static string Separator(CultureInfo c)
    {
        var sep = c.TextInfo.ListSeparator;
        return string.IsNullOrWhiteSpace(sep) ? "," : sep;
    }

    private static void Row(StringBuilder sb, string sep, params string[] fields)
    {
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append(sep);
            sb.Append(Escape(fields[i], sep));
        }
        sb.Append(NewLine);
    }

    /// <summary>
    /// Aturan CSV yang lazim (RFC 4180): kolom yang mengandung pemisah, tanda
    /// kutip, atau ganti baris dibungkus tanda kutip, dan tanda kutip di dalamnya
    /// digandakan. Catatan antrian bisa berisi koma ("Diambil alih oleh Budi,
    /// Dosen"), jadi ini bukan kehati-hatian teoretis.
    /// </summary>
    private static string Escape(string? value, string sep)
    {
        var text = value ?? "";
        bool needsQuotes = text.Contains(sep, StringComparison.Ordinal) ||
                           text.Contains('"') || text.Contains('\n') || text.Contains('\r');

        return needsQuotes ? '"' + text.Replace("\"", "\"\"") + '"' : text;
    }

    private static string Num(double? value, CultureInfo c) =>
        value is { } v && !double.IsNaN(v) && !double.IsInfinity(v)
            ? v.ToString("0.####", c) : "";

    /// <summary>
    /// Kolom waktu di database berisi UTC; yang dibaca di laporan waktu lokal.
    /// Kind ditetapkan dulu supaya nilai yang datang tanpa Kind (mis. dari
    /// pembacaan lama) tidak dihitung seolah-olah sudah waktu lokal.
    /// </summary>
    private static string Time(DateTime? utc) =>
        utc is { } t
            ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : "";
}
