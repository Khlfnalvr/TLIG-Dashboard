using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class PidAdvisorResult
{
    /// LLM's educational review, with the trailing JSON recommendation fence stripped.
    public string Explanation { get; set; } = "";
    /// Parsed Kp/Ki/Kd recommendation, or null if the LLM didn't return a parseable one.
    public PidPrediction? Recommendation { get; set; }
}

/// <summary>
/// Sends the current Kp/Ki/Kd and the exact RK4 response metrics (PidSimulator.
/// ComputeStepMetrics — <b>not</b> PidMetricsRegressor's estimate, which diverges from the
/// plotted curve; see PidDesignResult.MlEstimate) to the active LLM provider, asking for
/// a short educational review plus a machine-
/// parseable recommendation for new gains. The recommendation is requested as a fenced
/// ```json block rather than an inline tag (e.g. "[RECOMMENDED_KP: 15.5]") — fenced
/// code blocks are a pattern LLMs are heavily trained on and follow far more reliably,
/// and a JSON parse failure is an unambiguous, catchable signal rather than a silent
/// regex miss.
/// </summary>
public class PidAdvisorService
{
    private static readonly Regex JsonFence = new(@"```json\s*(\{.*?\})\s*```", RegexOptions.Singleline);

    private readonly Services.AiService _aiService;

    /// <param name="language">
    /// "id" / "en"; empty falls back to this process's own setting. Passed explicitly
    /// because the server answers Client-flavor students and must reply in <i>their</i>
    /// language, not its own.
    /// </param>
    public PidAdvisorService(string language = "")
    {
        _aiService = new Services.AiService();
        Services.AiConfigService.ApplyActive(_aiService);

        // The prompt never named a language, so models defaulted to replying in English —
        // the language of the prompt itself — regardless of the app's setting. State it
        // explicitly, and follow the app rather than hard-coding: the review sits next to
        // localized UI text, so the two should not disagree.
        string lang = string.IsNullOrWhiteSpace(language)
            ? Services.LocalizationManager.Instance.CurrentLanguage
            : language;
        // The plant is fixed and known, but the LLM was never told it — so it fell back on
        // generic PID intuition that misfires on a strongly lag-dominant plant (e.g. it
        // would advise lowering Kp to cut overshoot when, from an integral-dominated start,
        // raising Kp is what adds damping). Give it the actual transfer function and the
        // closed-loop structure, built from the same constants the simulator integrates.
        var (dcGain, tau) = PidSimulator.PlantCharacteristics();
        string plantRule =
            "The plant under control is fixed and known: " + PidSimulator.PlantTransferFunction + ", " +
            $"with a steady-state (DC) gain of about {dcGain:F1} and a dominant time constant of about " +
            $"{tau:F1} s — it is strongly lag-dominant and slow in open loop. Writing the plant as " +
            "B/(s^2 + A1*s + A0) and using an ideal PID on the error under unity feedback, the closed-loop " +
            "transfer function is B(Kp*s + Ki) / (s^3 + (A1 + B*Kd)*s^2 + (A0 + B*Kp)*s + B*Ki). It carries a " +
            "zero at s = -Ki/Kp: when Kp is small the loop is integral-dominated, that slow zero sits among the " +
            "dominant poles and inflates overshoot, so raising Kp (not lowering it) adds damping to the dominant " +
            "mode. Reason from this specific plant and its closed-loop form rather than from generic tuning " +
            "rules of thumb, which are unreliable here. ";

        bool indonesian = lang == "id";
        string languageRule = indonesian
            ? "Write your review in Indonesian (Bahasa Indonesia), using standard control " +
              "engineering terminology. Keep the established English terms (overshoot, rise " +
              "time, settling time, steady-state error, setpoint, gain) rather than " +
              "translating them, as that is how they appear in the course material and in " +
              "the surrounding UI."
            : "Write your review in English.";

        _aiService.SystemPrompt =
            "You are a Senior Professor of Control Systems Engineering reviewing a student's " +
            "PID tuning attempt. You will be given the setpoint (reference value), the current " +
            "Kp, Ki, Kd, and the resulting Overshoot (%), Rise Time (s), Settling Time (s), and " +
            "Steady-State Error for those gains. You may also be given the student's earlier " +
            "attempts this session and the response each produced. " +
            plantRule +
            "Write a brief (3-5 sentence) educational review explaining what is good or " +
            "bad about this response and why, in terms of the physical meaning of each gain. " +
            "If earlier attempts are shown, take them into account: do not re-recommend a gain " +
            "set that already failed to fix the offending metric, and change direction when a " +
            "previous suggestion did not help. " +
            languageRule + " " +
            "Then, on its own line, always end your reply with a fenced JSON block giving your " +
            "recommended next gains to try, in exactly this shape and nothing else after it:\n" +
            "```json\n{\"kp\": 0.0, \"ki\": 0.0, \"kd\": 0.0}\n```";
    }

    /// <param name="history">
    /// Earlier attempts in this session, oldest first, so the advisor sees the trajectory
    /// instead of reviewing each RUN in isolation. Null/empty is fine (first run).
    /// </param>
    public async Task<PidAdvisorResult> ReviewAsync(
        PidPrediction gains, PidMetricsPrediction metrics, float setpoint = 1f,
        IReadOnlyList<PidAttempt>? history = null, CancellationToken ct = default)
    {
        string prompt =
            $"Setpoint (reference): {setpoint:F2}.\n" +
            FormatHistory(history) +
            $"Current gains: Kp = {gains.Kp:F3}, Ki = {gains.Ki:F3}, Kd = {gains.Kd:F3}.\n" +
            $"Simulated response: Overshoot = {metrics.Overshoot:F2}%, Rise Time = {metrics.RiseTime:F3}s, " +
            $"Settling Time = {metrics.SettlingTime:F2}s, Steady-State Error = {metrics.SteadyStateError:F3}.\n\n" +
            "Review this tuning and recommend new gains.";

        string raw;
        try
        {
            raw = await _aiService.StreamChatAsync(prompt, _ => { }, ct);
        }
        catch (Exception ex)
        {
            return new PidAdvisorResult { Explanation = $"Advisor unavailable: {ex.Message}" };
        }

        var match = JsonFence.Match(raw);
        PidPrediction? recommendation = null;
        if (match.Success)
        {
            try
            {
                var node = JsonNode.Parse(match.Groups[1].Value);
                recommendation = new PidPrediction
                {
                    Kp = (float?)node?["kp"] ?? 0,
                    Ki = (float?)node?["ki"] ?? 0,
                    Kd = (float?)node?["kd"] ?? 0,
                };
            }
            catch { /* malformed JSON in the fence — leave recommendation null */ }
        }

        string explanation = match.Success ? raw[..match.Index].TrimEnd() : raw;
        return new PidAdvisorResult { Explanation = explanation, Recommendation = recommendation };
    }

    /// <summary>
    /// Renders prior attempts as a numbered list the LLM can read: the gains tried and the
    /// response each produced. Returns "" when there is no history, so the prompt is
    /// unchanged for the first run. Steady-State Error is carried as a fraction (see
    /// PidDesignService), matching how the current-attempt line prints it.
    /// </summary>
    private static string FormatHistory(IReadOnlyList<PidAttempt>? history)
    {
        if (history is not { Count: > 0 }) return "";

        var sb = new StringBuilder();
        sb.Append("Earlier attempts this session (oldest first), each with the response it produced:\n");
        int n = 1;
        foreach (var h in history)
            sb.Append($"  {n++}. Kp = {h.Kp:F3}, Ki = {h.Ki:F3}, Kd = {h.Kd:F3}  ->  " +
                      $"Overshoot = {h.Overshoot:F2}%, Rise Time = {h.RiseTime:F3}s, " +
                      $"Settling Time = {h.SettlingTime:F2}s, Steady-State Error = {h.SteadyStateError:F3}.\n");
        sb.Append('\n');
        return sb.ToString();
    }
}
