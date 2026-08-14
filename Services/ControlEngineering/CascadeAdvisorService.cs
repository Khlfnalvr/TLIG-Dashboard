using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>Cascade tuning recommendation (all five gains), echoed from <see cref="CascadeRecommender"/>.</summary>
public sealed class CascadeRecommendation
{
    public float OuterKp { get; set; }
    public float OuterKi { get; set; }
    public float OuterKd { get; set; }
    public float InnerKp { get; set; }
    public float InnerKi { get; set; }
}

public sealed class CascadeAdvisorResult
{
    /// The LLM's educational review — the advisor's only output. It no longer proposes gains.
    public string Explanation { get; set; } = "";
    /// The gains to offer the student, echoed straight from <see cref="CascadeRecommender"/>, or
    /// null when there is nothing to recommend. The LLM never chooses these.
    public CascadeRecommendation? Recommendation { get; set; }
}

/// <summary>
/// Writes the educational <i>review</i> shown next to the Cascade Control designer — and only the
/// review. The gains a student is offered come from <see cref="CascadeRecommender"/>, which finds
/// them by simulating candidates rather than guessing; the advisor used to emit its own JSON for
/// all five gains, but those numbers were never simulated and could contradict the diagnosis (and
/// a malformed block silently became all-zero gains). Now the search decides the numbers and this
/// service explains them. Cascade-aware sibling of <see cref="PidAdvisorService"/>: it is given
/// both loops' gains, the temperature response, the disturbance-rejection numbers, both identified
/// plant models, the session history and the recommended gains, and returns prose only.
/// </summary>
public class CascadeAdvisorService
{
    private readonly Services.AiService _aiService;

    /// <param name="language">
    /// "id" / "en"; empty falls back to this process's own setting. The cascade designer runs
    /// locally in both flavors, so the review should follow the app's language.
    /// </param>
    public CascadeAdvisorService(string language = "")
    {
        _aiService = new Services.AiService();
        Services.AiConfigService.ApplyActive(_aiService);

        // Follow the app's language rather than hard-coding English (the old prompt always came
        // back in English regardless of the setting): the review sits next to localized UI text.
        string lang = string.IsNullOrWhiteSpace(language)
            ? Services.LocalizationManager.Instance.CurrentLanguage
            : language;

        // The plants are fixed and known, but the old prompt never named them, so the model fell
        // back on generic PID intuition that misfires on strongly lag/dead-time-dominant plants.
        // Give it both identified transfer functions, built from the very constants the simulator
        // integrates so the description can never drift from what is simulated.
        string gp1 = $"Gp1(s) = {CascadeSimulator.K1} / ({CascadeSimulator.Tau1} s + 1) * e^(-{CascadeSimulator.Theta1:F2} s)";
        string gp2 = $"Gp2(s) = {CascadeSimulator.K2} / ({CascadeSimulator.Tau2} s + 1) * e^(-{CascadeSimulator.Theta2:F2} s)";
        string plantRule =
            "The cascade controls a heat exchanger with two identified First-Order-Plus-Dead-Time " +
            "plants (from a lab open-loop step test). OUTER loop — temperature, whose input is flow: " +
            gp1 + $" (time constant ~{CascadeSimulator.Tau1:F0} s, dead time ~{CascadeSimulator.Theta1:F0} s) — " +
            "slow and strongly lag/dead-time-dominant, so derivative action on temperature helps only " +
            "a little and overshoot is driven mainly by Kp/Ki. INNER loop — flow, whose input is the " +
            "valve: " + gp2 + $" (dead-time-dominated, theta/tau ~{CascadeSimulator.Theta2 / CascadeSimulator.Tau2:F1}), " +
            "so the inner PI must stay gentle. The outer PID's output is the inner PI's setpoint. " +
            "Cascade's golden rule: the inner loop must be several times faster than the outer and is " +
            "tuned first; the inner loop's speed is what lets cascade reject a flow disturbance long " +
            "before it reaches the temperature. Reason from these specific plants rather than from " +
            "generic tuning rules of thumb, which are unreliable here. ";

        bool indonesian = lang == "id";
        string languageRule = indonesian
            ? "Write your review in Indonesian (Bahasa Indonesia), using standard control " +
              "engineering terminology. Keep the established English terms (overshoot, rise time, " +
              "settling time, steady-state error, setpoint, gain, cascade, inner/outer loop) rather " +
              "than translating them, as that is how they appear in the course material and UI."
            : "Write your review in English.";

        _aiService.SystemPrompt =
            "You are a Senior Professor of Control Systems Engineering reviewing a student's CASCADE " +
            "tuning of a heat exchanger. The OUTER (primary) loop controls TEMPERATURE with a PID; its " +
            "output is the setpoint of the INNER (secondary) loop, which controls FLOW with a PI and " +
            "drives the valve. You will be given both loops' gains, the temperature step response, and " +
            "the peak temperature deviation after a flow disturbance for both cascade and an equivalent " +
            "single-loop controller. You may also be given the student's earlier attempts this session " +
            "and a recommended tuning that has already been found for them by simulation. " +
            plantRule +
            "Write a brief (3-5 sentence) educational review: comment on whether the inner loop is fast " +
            "enough relative to the outer, whether the outer PID's overshoot/settling are acceptable, " +
            "how much better the cascade rejects the flow disturbance, and what the error indices " +
            "(IAE/ISE/ITAE — lower is better; ISE weighs large early error, ITAE weighs late-lingering " +
            "error) say about the response, in terms of the physical meaning of each gain. When a " +
            "recommended tuning is given, explain why it behaves better; do " +
            "NOT propose gains of your own — the recommended gains are what the student will be offered, " +
            "and any numbers you invent would contradict them. When earlier attempts are shown, take " +
            "them into account and note what did or did not help. " +
            languageRule + " " +
            "Reply with the review prose only — no JSON, no code blocks, no gain values on their own line.";
    }

    /// <param name="history">Earlier attempts this session, oldest first. Null/empty is fine (first run).</param>
    /// <param name="recommended">
    /// Gains found by <see cref="CascadeRecommender"/> for the LLM to explain, or null when the
    /// current tuning already meets every target. Echoed unchanged into
    /// <see cref="CascadeAdvisorResult.Recommendation"/> — the LLM never chooses it.
    /// </param>
    /// <param name="recommendedMetrics">The recommended tuning's measured temperature response, for context.</param>
    public async Task<CascadeAdvisorResult> ReviewAsync(
        CascadeInput gains, CascadeMetrics metrics,
        IReadOnlyList<CascadeAttempt>? history = null,
        CascadeRecommendation? recommended = null, CascadeMetrics? recommendedMetrics = null,
        CancellationToken ct = default)
    {
        string task = recommended is not null
            ? "A better tuning has already been found for these plants by simulating candidate gains " +
              "and keeping one that meets every target: " +
              $"OUTER Kp = {recommended.OuterKp:F3}, Ki = {recommended.OuterKi:F3}, Kd = {recommended.OuterKd:F3}; " +
              $"INNER Kp = {recommended.InnerKp:F3}, Ki = {recommended.InnerKi:F3}" +
              (recommendedMetrics is not null
                  ? $", which yields temperature Overshoot = {recommendedMetrics.Overshoot:F2}%, " +
                    $"Rise Time = {recommendedMetrics.RiseTime:F3}s, Settling Time = {recommendedMetrics.SettlingTime:F2}s, " +
                    $"Steady-State Error = {recommendedMetrics.SteadyStateError:F3}.\n"
                  : ".\n") +
              "Explain why the current tuning underperforms and why these recommended gains behave " +
              "better. Do not propose any other numbers."
            : "This tuning already meets every target. Give a short affirming review of why it behaves " +
              "well. Do not recommend changes.";

        string prompt =
            $"Temperature setpoint: {gains.Setpoint:F1} °C.\n" +
            FormatHistory(history) +
            $"Current gains — OUTER temperature PID: Kp = {gains.OuterKp:F3}, Ki = {gains.OuterKi:F3}, Kd = {gains.OuterKd:F3}; " +
            $"INNER flow PI: Kp = {gains.InnerKp:F3}, Ki = {gains.InnerKi:F3}.\n" +
            $"Temperature response: Overshoot = {metrics.Overshoot:F1}%, Rise Time = {metrics.RiseTime:F2}s, " +
            $"Settling Time = {metrics.SettlingTime:F1}s, Steady-State Error = {metrics.SteadyStateError:F3}" +
            (metrics.Stable ? "" : " (the temperature never settled at the setpoint — the loop is oscillating or far too slow)") + ".\n" +
            $"Error performance indices over the step (integrals of the tracking error, lower is better): " +
            $"IAE = {metrics.IAE:F0}, ISE = {metrics.ISE:F0}, ITAE = {metrics.ITAE:F0}.\n" +
            (metrics.DisturbanceImprovement >= 1f
                ? $"After a flow disturbance, peak temperature deviation was {metrics.CascadeMaxDeviation:F2} °C with " +
                  $"cascade vs {metrics.SingleLoopMaxDeviation:F2} °C with a single loop " +
                  $"({metrics.DisturbanceImprovement:F1}x better).\n\n"
                : "\n") +
            task;

        string raw;
        try
        {
            raw = await _aiService.StreamChatAsync(prompt, _ => { }, ct);
        }
        catch (Exception ex)
        {
            // The recommendation comes from the search, not the LLM, so it still stands even when
            // the review can't be fetched.
            return new CascadeAdvisorResult { Explanation = $"Advisor unavailable: {ex.Message}", Recommendation = recommended };
        }

        return new CascadeAdvisorResult { Explanation = raw.TrimEnd(), Recommendation = recommended };
    }

    /// <summary>
    /// Renders prior attempts as a numbered list the LLM can read: the five gains tried and the
    /// temperature response each produced. Returns "" when there is no history so the prompt is
    /// unchanged for the first run.
    /// </summary>
    private static string FormatHistory(IReadOnlyList<CascadeAttempt>? history)
    {
        if (history is not { Count: > 0 }) return "";

        var sb = new StringBuilder();
        sb.Append("Earlier attempts this session (oldest first), each with the temperature response it produced:\n");
        int n = 1;
        foreach (var h in history)
            sb.Append($"  {n++}. OUTER Kp = {h.OuterKp:F3}, Ki = {h.OuterKi:F3}, Kd = {h.OuterKd:F3}; " +
                      $"INNER Kp = {h.InnerKp:F3}, Ki = {h.InnerKi:F3}  ->  " +
                      $"Overshoot = {h.Overshoot:F2}%, Rise Time = {h.RiseTime:F3}s, " +
                      $"Settling Time = {h.SettlingTime:F2}s, Steady-State Error = {h.SteadyStateError:F3}.\n");
        sb.Append('\n');
        return sb.ToString();
    }
}
