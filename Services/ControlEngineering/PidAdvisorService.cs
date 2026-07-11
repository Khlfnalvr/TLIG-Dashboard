using System;
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
/// Sends the current Kp/Ki/Kd and PidMetricsRegressor's predicted response metrics to
/// the active LLM provider, asking for a short educational review plus a machine-
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

    public PidAdvisorService()
    {
        _aiService = new Services.AiService();
        Services.AiConfigService.ApplyActive(_aiService);
        _aiService.SystemPrompt =
            "You are a Senior Professor of Control Systems Engineering reviewing a student's " +
            "PID tuning attempt. You will be given the current Kp, Ki, Kd and the predicted " +
            "Overshoot (%), Rise Time (s), Settling Time (s), and Steady-State Error for those " +
            "gains. Write a brief (3-5 sentence) educational review explaining what is good or " +
            "bad about this response and why, in terms of the physical meaning of each gain. " +
            "Then, on its own line, always end your reply with a fenced JSON block giving your " +
            "recommended next gains to try, in exactly this shape and nothing else after it:\n" +
            "```json\n{\"kp\": 0.0, \"ki\": 0.0, \"kd\": 0.0}\n```";
    }

    public async Task<PidAdvisorResult> ReviewAsync(
        PidPrediction gains, PidMetricsPrediction metrics, CancellationToken ct = default)
    {
        string prompt =
            $"Current gains: Kp = {gains.Kp:F3}, Ki = {gains.Ki:F3}, Kd = {gains.Kd:F3}.\n" +
            $"Predicted response: Overshoot = {metrics.Overshoot:F2}%, Rise Time = {metrics.RiseTime:F3}s, " +
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
}
