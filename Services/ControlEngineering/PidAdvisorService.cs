using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class PidAdvisorResult
{
    /// The LLM's educational review — the advisor's only output. It no longer proposes gains.
    public string Explanation { get; set; } = "";
    /// The gains to offer the student, echoed straight from the caller's search recommendation
    /// (<see cref="PidRecommender"/>), or null when there is nothing to recommend. Carried here
    /// so the existing plumbing (PidDesignResult.AdvisorRecommendation) is unchanged.
    public PidPrediction? Recommendation { get; set; }
}

/// <summary>
/// Writes the educational <i>review</i> shown next to the Smart PID Designer — and only the
/// review. The gains a student is offered come from <see cref="PidRecommender"/>, which finds
/// them by simulating candidates rather than guessing; the LLM used to emit its own JSON
/// recommendation, but those numbers were never simulated and routinely contradicted the
/// diagnosis (applying one drove overshoot from 72.7% to 76.6%). Now the search decides the
/// numbers and this service explains them: it is given the current response, the exact RK4
/// metrics (PidSimulator.ComputeStepMetrics — <b>not</b> PidMetricsRegressor's estimate; see
/// PidDesignResult.MlEstimate), the plant model, the session history, and the recommended
/// gains, and returns prose only.
/// </summary>
public class PidAdvisorService
{
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
            "attempts this session and a recommended tuning that has already been found for them " +
            "by simulation. " +
            plantRule +
            "Write a brief (3-5 sentence) educational review explaining what is good or bad " +
            "about the current response and why, in terms of the physical meaning of each gain. " +
            "When a recommended tuning is given, explain why it behaves better; do NOT propose " +
            "gains of your own — the recommended gains are what the student will be offered, and " +
            "any numbers you invent would contradict them. When earlier attempts are shown, take " +
            "them into account and note what did or did not help. " +
            languageRule + " " +
            "Reply with the review prose only — no JSON, no code blocks, no gain values on their " +
            "own line.";
    }

    /// <param name="history">
    /// Earlier attempts in this session, oldest first, so the advisor sees the trajectory
    /// instead of reviewing each RUN in isolation. Null/empty is fine (first run).
    /// </param>
    /// <param name="recommended">
    /// Gains found by <see cref="PidRecommender"/> for the LLM to explain, or null when the
    /// current tuning already meets every target (the review just affirms it). This is echoed
    /// unchanged into <see cref="PidAdvisorResult.Recommendation"/> — the LLM never chooses it.
    /// </param>
    /// <param name="recommendedMetrics">The recommended tuning's measured response, for context.</param>
    public async Task<PidAdvisorResult> ReviewAsync(
        PidPrediction gains, PidMetricsPrediction metrics, float setpoint = 1f,
        IReadOnlyList<PidAttempt>? history = null,
        PidPrediction? recommended = null, PidMetricsPrediction? recommendedMetrics = null,
        CancellationToken ct = default)
    {
        string task = recommended is not null
            ? "A better tuning has already been found for this plant by simulating candidate gains " +
              "and keeping one that meets every target: " +
              $"Kp = {recommended.Kp:F3}, Ki = {recommended.Ki:F3}, Kd = {recommended.Kd:F3}" +
              (recommendedMetrics is not null
                  ? $", which yields Overshoot = {recommendedMetrics.Overshoot:F2}%, " +
                    $"Rise Time = {recommendedMetrics.RiseTime:F3}s, Settling Time = {recommendedMetrics.SettlingTime:F2}s, " +
                    $"Steady-State Error = {recommendedMetrics.SteadyStateError:F3}.\n"
                  : ".\n") +
              "Explain why the current tuning underperforms and why these recommended gains behave " +
              "better. Do not propose any other numbers."
            : "This tuning already meets every target. Give a short affirming review of why it " +
              "behaves well. Do not recommend changes.";

        string prompt =
            $"Setpoint (reference): {setpoint:F2}.\n" +
            FormatHistory(history) +
            $"Current gains: Kp = {gains.Kp:F3}, Ki = {gains.Ki:F3}, Kd = {gains.Kd:F3}.\n" +
            $"Simulated response: Overshoot = {metrics.Overshoot:F2}%, Rise Time = {metrics.RiseTime:F3}s, " +
            $"Settling Time = {metrics.SettlingTime:F2}s, Steady-State Error = {metrics.SteadyStateError:F3}.\n\n" +
            task;

        string raw;
        try
        {
            raw = await _aiService.StreamChatAsync(prompt, _ => { }, ct);
        }
        catch (Exception ex)
        {
            // The recommendation comes from the search, not the LLM, so it still stands even
            // when the review can't be fetched.
            return new PidAdvisorResult { Explanation = $"Advisor unavailable: {ex.Message}", Recommendation = recommended };
        }

        return new PidAdvisorResult { Explanation = raw.TrimEnd(), Recommendation = recommended };
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
