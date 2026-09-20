using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Menjawab pertanyaan AI chat tentang **respon real plant** dari arsip
/// <c>heParamCache.db</c> — bukan dari simulasi, bukan dari tebakan LLM.
///
/// <para>Tiga sumber angka di chat, jangan dicampur: (A) live detik ini via
/// <c>HmiDataService</c>, (B) simulasi RK4 terakhir via <c>CascadeSessionService</c>,
/// (C) riwayat real di sini. Kelas ini adalah sumber (C): tetangga terdekat milik
/// pengguna sendiri (Server: <c>HeParameterCacheRepository.FindNearestAsync</c>,
/// Client: <c>HeQueueClient.GetNearestRunsAsync</c>).</para>
///
/// <para>Cache menyimpan <c>Kc/Ti/Td</c> yang angkanya SAMA PERSIS dengan kotak
/// <c>Kp/Ki/Kd</c> dashboard (tanpa konversi — lihat <c>DashboardPage.CurrentHeInput</c>),
/// jadi query memakai angka kotak apa adanya. Hanya <c>Completed</c> yang dipakai sebagai
/// acuan; <c>Failed/Aborted/[terputus]</c> tetap riwayat tapi tidak valid untuk prediksi.</para>
/// </summary>
public static class RealHistoryService
{
    private const int DefaultLimit = 3;

    /// <summary>
    /// Titik query default: gain outer + setpoint sesi cascade saat ini. Pump tidak
    /// diketahui di konteks chat AI → <see cref="double.NaN"/> (diabaikan dalam jarak).
    /// </summary>
    public static HeParameterInput CurrentPoint()
    {
        var cs = CascadeSessionService.Instance;
        return new HeParameterInput
        {
            Sp   = cs.Setpoint,
            Kc   = cs.OuterKp,
            Ti   = cs.OuterKi,
            Td   = cs.OuterKd,
            Pump = double.NaN,
        };
    }

    /// <summary>Tetangga real terdekat milik pengguna sendiri, tanpa kurva (ringan untuk prompt).</summary>
    public static async Task<IReadOnlyList<HeParameterRun>> GetNearestAsync(
        HeParameterInput? target = null, int limit = DefaultLimit, CancellationToken ct = default)
    {
        target ??= CurrentPoint();
        limit = Math.Clamp(limit, 1, 5);
        try
        {
            if (BuildInfo.IsServer)
            {
                string user = SessionService.Instance.Username;
                if (string.IsNullOrWhiteSpace(user)) return [];
                var nearest = await App.HeParamCache.FindNearestAsync(user, target, limit, ct);
                return nearest.Select(n => n.Run).ToArray();
            }

            var s = AppSettingsService.Load();
            return await HeQueueClient.GetNearestRunsAsync(s.ServerHost, s.ServerToken, target, limit);
        }
        catch { return []; }
    }

    /// <summary>
    /// Blok konteks sekali-pakai untuk ditempel ke system prompt, sejajar dengan
    /// <c>ProcessErrorService.BuildChatContext</c>. Ringkas (tanpa kurva) agar hemat token.
    /// </summary>
    public static async Task<string> BuildRealContextAsync(
        int limit = DefaultLimit, CancellationToken ct = default)
    {
        var runs = await GetNearestAsync(null, limit, ct);
        var sb = new StringBuilder();
        sb.AppendLine("=== RIWAYAT REAL PLANT (heParamCache.db, milik pengguna sendiri, status Completed saja) ===");

        if (runs.Count == 0)
        {
            sb.AppendLine("Belum ada run real yang tersimpan untuk akun ini. Jangan mengarang angka real —");
            sb.AppendLine("jawab dari simulasi dan tandai jelas sebagai SIMULASI, bukan hasil LabVIEW.");
            sb.AppendLine("Ingatkan: data real baru ada setelah pengguna menekan RUN lalu STOP di LabVIEW/plant (run Completed).");
            return sb.ToString().TrimEnd();
        }

        var q = CurrentPoint();
        sb.AppendLine($"Titik saat ini: SP={N(q.Sp)}°C, Kc={N(q.Kc)}, Ti={N(q.Ti)}, Td={N(q.Td)}.");
        sb.AppendLine("Tetangga terdekat (parameter → hasil real yang terukur):");
        int n = 1;
        foreach (var r in runs)
            sb.AppendLine($"   {n++}. {DescribeRun(r)}");
        sb.AppendLine("Pakai angka di atas apa adanya kalau pengguna menanyakan hasil real / waktu mencapai setpoint. " +
                      "Simulasi (blok B) adalah model ideal; angka real di sini yang berlaku untuk plant.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Jawaban langsung (tanpa LLM) untuk pertanyaan tentang hasil real historis.
    /// <c>null</c> = bukan pertanyaan real → teruskan ke router berikutnya / LLM.
    /// </summary>
    public static async Task<string?> TryAnswerRealRequestAsync(
        string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (!LooksLikeRealRequest(message.ToLowerInvariant())) return null;

        // Gain eksplisit di pesan ("dengan Kp=.. Ki=..") mengalahkan titik sesi saat ini.
        var target = ParseGains(message) ?? CurrentPoint();
        var runs = await GetNearestAsync(target, DefaultLimit, ct);
        if (runs.Count == 0)
            return "Belum ada run real tersimpan untuk akun ini (hanya run **Completed** yang dipakai acuan). " +
                   "Jalankan plant sekali dulu (RUN, tunggu, lalu **STOP** — data real baru tersimpan setelah di-STOP), " +
                   "lalu tanyakan lagi — tanpa data real saya tidak menebak.";

        var sb = new StringBuilder();
        sb.AppendLine("**Hasil real plant terdekat** (dari arsip percobaanmu, bukan simulasi):");
        sb.AppendLine();
        int n = 1;
        foreach (var r in runs)
            sb.AppendLine($"- {DescribeRun(r)}");
        sb.AppendLine();
        sb.Append("Simulasi adalah model ideal (FOPDT satu titik operasi); " +
                  "kalau angka sim dan real di atas berbeda, yang berlaku untuk LabVIEW adalah angka real ini.");
        _ = n;
        return sb.ToString();
    }

    // ── Intent ──────────────────────────────────────────────────────────────

    private static bool LooksLikeRealRequest(string m)
    {
        bool real = m.Contains("real") || m.Contains("nyata") || m.Contains("labview") ||
                    m.Contains("plant") || m.Contains("percobaan") || m.Contains("riwayat") ||
                    m.Contains("arsip") || m.Contains("dulu") || m.Contains("pernah") ||
                    m.Contains("sebelumnya") || m.Contains("run");
        bool result = m.Contains("hasil") || m.Contains("berapa") || m.Contains("waktu") ||
                      m.Contains("settl") || m.Contains("tunak") || m.Contains("mencapai") ||
                      m.Contains("overshoot") || m.Contains("setpoint") || m.Contains("set point") ||
                      m.Contains("sp");
        return real && result;
    }

    // ── Parsing gain eksplisit ──────────────────────────────────────────────

    private static HeParameterInput? ParseGains(string text)
    {
        float? kp = FindGain(text, "kp") ?? FindGain(text, "kc");
        float? ki = FindGain(text, "ki") ?? FindGain(text, "ti");
        float? kd = FindGain(text, "kd") ?? FindGain(text, "td");
        float? sp = FindGain(text, "sp") ?? FindGain(text, "setpoint") ?? FindGain(text, "set point");
        float? pump = FindGain(text, "pump") ?? FindGain(text, "valve");
        if (kp is null && ki is null && kd is null && sp is null) return null;

        var cur = CurrentPoint();
        return new HeParameterInput
        {
            Sp   = sp   ?? cur.Sp,
            Kc   = kp   ?? cur.Kc,
            Ti   = ki   ?? cur.Ti,
            Td   = kd   ?? cur.Td,
            Pump = pump ?? double.NaN,
        };
    }

    private static float? FindGain(string text, string name)
    {
        var match = Regex.Match(text, @"\b" + Regex.Escape(name) + @"\s*[=:]?\s*([0-9]+(?:[.,][0-9]+)?)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        string raw = match.Groups[1].Value.Replace(',', '.');
        return float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : null;
    }

    // ── Format ──────────────────────────────────────────────────────────────

    private static string DescribeRun(HeParameterRun r)
    {
        var m = r.Metrics;
        string metrics = m is null
            ? "metrik belum tersedia"
            : $"settling {F(m.SettlingTimeSeconds)}s, overshoot {F(m.OvershootPercent)}%, " +
              $"rise {F(m.RiseTimeSeconds)}s, SSE {F(m.SteadyStateError)}°C, durasi {F(r.DurationSeconds)}s";
        string when = r.StartedAtUtc == default ? "-" : r.StartedAtUtc.ToLocalTime().ToString("dd/MM HH:mm");
        return $"run #{r.RunId} [{when}] SP={N(r.Input.Sp)} Kc={N(r.Input.Kc)} Ti={N(r.Input.Ti)} Td={N(r.Input.Td)} Pump={N(r.Input.Pump)} → {metrics}.";
    }

    private static string N(double v) =>
        double.IsNaN(v) ? "?" : v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string F(double? v) =>
        v is null || double.IsNaN(v.Value) ? "?" : v.Value.ToString("0.#", CultureInfo.InvariantCulture);
}
