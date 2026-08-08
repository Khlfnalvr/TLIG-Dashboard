using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Makes the free-form AI chat give TRUSTWORTHY gains for this plant. An LLM cannot compute
/// overshoot for a lag/dead-time-dominant plant reliably, so:
/// <list type="bullet">
/// <item>a request that names a performance target (e.g. "overshoot &lt; 5%") is answered from the
///   verified simulator search (<see cref="CascadeRecommender.RecommendForTarget"/>), not the LLM;</item>
/// <item>any Kp/Ki/Kd the LLM <i>does</i> emit is simulated and the real result appended as a note.</item>
/// </list>
/// Both paths only ever report numbers measured on the RK4 model. The parsing is intentionally
/// conservative — anything it misses still falls through to the LLM, whose gains the safety net checks.
/// </summary>
public static class TuningChat
{
    // ── Target request → verified answer ──────────────────────────────────────

    /// <summary>
    /// If <paramref name="message"/> asks for gains meeting a performance target, returns a verified
    /// answer (real gains + measured metrics, or an honest "no tuning meets that"); otherwise null so
    /// the normal chat handles it.
    /// </summary>
    public static string? TryAnswerTargetRequest(string message, float setpoint)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        string m = message.ToLowerInvariant();
        if (!LooksLikeGainRequest(m)) return null;

        var target = ParseTarget(m);
        if (target is null || !target.Any) return null;

        var res = CascadeRecommender.RecommendForTarget(setpoint, target);
        return ComposeAnswer(target, res, setpoint);
    }

    private static bool LooksLikeGainRequest(string m) =>
        m.Contains("gain") || m.Contains("kp") || m.Contains("ki") || m.Contains("kd") ||
        m.Contains("tuning") || m.Contains("tune") || m.Contains("setel") || m.Contains("pid") ||
        m.Contains("rekomendasi") || m.Contains("saran") || m.Contains("cari") ||
        m.Contains("beri") || m.Contains("berapa") || m.Contains("supaya") ||
        m.Contains("agar") || m.Contains("target");

    private static TuningTarget? ParseTarget(string m)
    {
        var t = new TuningTarget();
        if (FindConstraint(m, @"overshoot") is float os)                    t.MaxOvershoot = os;
        if ((FindConstraint(m, @"settl\w*") ?? FindConstraint(m, @"tunak")) is float st) t.MaxSettlingTime = st;
        if ((FindConstraint(m, @"steady[\s-]*state") ?? FindConstraint(m, @"\bsse\b")) is float sse) t.MaxSteadyStateErrorPct = sse;
        return t.Any ? t : null;
    }

    // Finds "<keyword> … <number>" within a short window and returns the number (accepts "," or ".").
    private static float? FindConstraint(string m, string keyword)
    {
        var match = Regex.Match(m, keyword + @"[^0-9]{0,25}?([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase);
        return match.Success && TryNum(match.Groups[1].Value, out float v) ? v : null;
    }

    private static string ComposeAnswer(TuningTarget t, TargetTuningResult res, float setpoint)
    {
        string targetStr = DescribeTarget(t);
        if (!res.HasGains)
            return $"Tidak ada tuning stabil di rentang pencarian untuk target {targetStr} pada setpoint {setpoint:F0}°C.";

        var g = res.Gains!;
        var m = res.Metrics!;
        var sb = new StringBuilder();
        if (res.MetTarget)
        {
            sb.AppendLine($"**Gain terverifikasi untuk target {targetStr}** (setpoint {setpoint:F0}°C):");
            sb.AppendLine();
            sb.AppendLine($"- **OUTER** (PID suhu): Kp = {g.OuterKp:0.###}, Ki = {g.OuterKi:0.###}, Kd = {g.OuterKd:0.###}");
            sb.AppendLine($"- **INNER** (PI flow, SIMC): Kp = {g.InnerKp:0.###}, Ki = {g.InnerKi:0.###}");
            sb.AppendLine();
            sb.AppendLine($"Hasil simulasi RK4: overshoot **{m.Overshoot:0.0}%**, settling **{m.SettlingTime:0}s**, rise {m.RiseTime:0.0}s. ✅ memenuhi target.");
            sb.Append("Angka ini diukur langsung pada plant Gp1/Gp2, jadi saat diterapkan hasilnya akan sesuai — bukan tebakan.");
        }
        else
        {
            sb.AppendLine($"**Tidak ada tuning yang memenuhi {targetStr}** di rentang pencarian.");
            sb.AppendLine();
            sb.AppendLine($"Paling mendekati: OUTER Kp = {g.OuterKp:0.###}, Ki = {g.OuterKi:0.###}, Kd = {g.OuterKd:0.###} → overshoot {m.Overshoot:0.0}%, settling {m.SettlingTime:0}s.");
            sb.Append("Coba longgarkan targetnya — plant ini punya dead time ~52 s, jadi settling yang sangat kecil memang tak tercapai.");
        }
        return sb.ToString();
    }

    private static string DescribeTarget(TuningTarget t)
    {
        var parts = new List<string>();
        if (t.MaxOvershoot.HasValue)           parts.Add($"overshoot ≤ {t.MaxOvershoot.Value:0.#}%");
        if (t.MaxSettlingTime.HasValue)        parts.Add($"settling ≤ {t.MaxSettlingTime.Value:0}s");
        if (t.MaxSteadyStateErrorPct.HasValue) parts.Add($"steady-state error ≤ {t.MaxSteadyStateErrorPct.Value:0.#}%");
        return string.Join(" dan ", parts);
    }

    // ── Safety net: verify any gains the LLM emitted ──────────────────────────

    /// <summary>
    /// If <paramref name="reply"/> contains a full Kp/Ki/Kd set, simulates it on the plant and returns
    /// a note stating the real result. Returns null when no complete gain set is present. Meant to be
    /// appended to the LLM's reply so a guessed tuning can't pass unchecked.
    /// </summary>
    public static string? VerifyGainsNote(string reply, float setpoint)
    {
        if (ParseGains(reply) is not (float kp, float ki, float kd)) return null;
        var m = CascadeRecommender.SimulateOuter(setpoint, kp, ki, kd);

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("---");
        if (!m.Stable)
            sb.Append($"⚠️ **Verifikasi simulasi:** gain Kp={kp:0.###}, Ki={ki:0.###}, Kd={kd:0.###} membuat suhu **tidak settle** (osilasi / terlalu lambat) pada plant ini — jangan dipakai.");
        else
            sb.Append($"✅ **Verifikasi simulasi** (plant Gp1/Gp2, setpoint {setpoint:F0}°C, inner PI SIMC): Kp={kp:0.###}, Ki={ki:0.###}, Kd={kd:0.###} → overshoot **{m.Overshoot:0.0}%**, settling **{m.SettlingTime:0}s**, rise {m.RiseTime:0.0}s.");
        return sb.ToString();
    }

    private static (float kp, float ki, float kd)? ParseGains(string text)
    {
        if (FindGain(text, "kp") is float kp && FindGain(text, "ki") is float ki && FindGain(text, "kd") is float kd)
            return (kp, ki, kd);
        return null;
    }

    private static float? FindGain(string text, string name)
    {
        var match = Regex.Match(text, @"\b" + name + @"\s*[=:]?\s*([0-9]+(?:[.,][0-9]+)?)", RegexOptions.IgnoreCase);
        return match.Success && TryNum(match.Groups[1].Value, out float v) ? v : null;
    }

    private static bool TryNum(string s, out float v) =>
        float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
