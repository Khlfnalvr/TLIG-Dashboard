using System;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>Parsed cascade tuning recommendation (all five gains).</summary>
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
    public string Explanation { get; set; } = "";
    public CascadeRecommendation? Recommendation { get; set; }
}

/// <summary>
/// Cascade-aware sibling of <see cref="PidAdvisorService"/>. Sends both loops' gains, the
/// temperature response and the disturbance-rejection numbers to the active LLM provider
/// and asks for a short educational review plus a machine-parseable recommendation for
/// all five gains. Uses the same fenced-```json convention (reliable to parse; a failure
/// is unambiguous) as the single-loop advisor.
/// </summary>
public class CascadeAdvisorService
{
    private static readonly Regex JsonFence = new(@"```json\s*(\{.*?\})\s*```", RegexOptions.Singleline);

    private readonly Services.AiService _aiService;

    public CascadeAdvisorService()
    {
        _aiService = new Services.AiService();
        Services.AiConfigService.ApplyActive(_aiService);
        _aiService.SystemPrompt =
            "You are a Senior Professor of Control Systems Engineering reviewing a student's " +
            "CASCADE tuning of a heat exchanger. The OUTER (primary) loop controls TEMPERATURE " +
            "with a PID; its output is the setpoint of the INNER (secondary) loop, which controls " +
            "FLOW with a PI and drives the valve. The golden rule of cascade: the inner loop must " +
            "be several times FASTER than the outer loop, and the inner loop is tuned first. " +
            "You are given both loops' gains, the temperature step response, and the peak " +
            "temperature deviation after a flow disturbance for both cascade and an equivalent " +
            "single-loop controller. Write a brief (3-5 sentence) educational review: comment on " +
            "whether the inner loop is fast enough, whether the outer PID's overshoot/settling are " +
            "acceptable, and how much better the cascade rejects the flow disturbance. " +
            "Then, on its own line, always end your reply with a fenced JSON block giving your " +
            "recommended next gains, in exactly this shape and nothing else after it:\n" +
            "```json\n{\"outerKp\": 0.0, \"outerKi\": 0.0, \"outerKd\": 0.0, \"innerKp\": 0.0, \"innerKi\": 0.0}\n```";
    }

    public async Task<CascadeAdvisorResult> ReviewAsync(
        CascadeInput gains, CascadeMetrics metrics, CancellationToken ct = default)
    {
        string prompt =
            $"Temperature setpoint: {gains.Setpoint:F1} °C.\n" +
            $"OUTER temperature PID: Kp = {gains.OuterKp:F3}, Ki = {gains.OuterKi:F3}, Kd = {gains.OuterKd:F3}.\n" +
            $"INNER flow PI: Kp = {gains.InnerKp:F3}, Ki = {gains.InnerKi:F3}.\n" +
            $"Temperature response: Overshoot = {metrics.Overshoot:F1}%, Rise Time = {metrics.RiseTime:F2}s, " +
            $"Settling Time = {metrics.SettlingTime:F1}s, Steady-State Error = {metrics.SteadyStateError:F3}.\n" +
            $"After a flow disturbance, peak temperature deviation was {metrics.CascadeMaxDeviation:F2} °C with " +
            $"cascade vs {metrics.SingleLoopMaxDeviation:F2} °C with a single loop " +
            $"({metrics.DisturbanceImprovement:F1}× better).\n\n" +
            "Review this cascade tuning and recommend new gains.";

        string raw;
        try
        {
            raw = await _aiService.StreamChatAsync(prompt, _ => { }, ct);
        }
        catch (Exception ex)
        {
            return new CascadeAdvisorResult { Explanation = $"Advisor unavailable: {ex.Message}" };
        }

        var match = JsonFence.Match(raw);
        CascadeRecommendation? recommendation = null;
        if (match.Success)
        {
            try
            {
                var node = JsonNode.Parse(match.Groups[1].Value);
                recommendation = new CascadeRecommendation
                {
                    OuterKp = (float?)node?["outerKp"] ?? 0,
                    OuterKi = (float?)node?["outerKi"] ?? 0,
                    OuterKd = (float?)node?["outerKd"] ?? 0,
                    InnerKp = (float?)node?["innerKp"] ?? 0,
                    InnerKi = (float?)node?["innerKi"] ?? 0,
                };
            }
            catch { /* malformed JSON in the fence — leave recommendation null */ }
        }

        string explanation = match.Success ? raw[..match.Index].TrimEnd() : raw;
        return new CascadeAdvisorResult { Explanation = explanation, Recommendation = recommendation };
    }
}
