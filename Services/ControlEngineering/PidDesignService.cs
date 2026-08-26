using System.Text;
using System.Text.Json.Nodes;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class PidDesignResult
{
    public PidPrediction        Prediction            { get; set; } = new();
    /// Reference the simulation was run against — what the curve settles toward.
    public float                 Setpoint             { get; set; } = 1f;
    public SimulationResult     Simulation            { get; set; } = new();
    /// <summary>
    /// <see cref="PidDiagnosisCode"/> name from <see cref="PidDiagnosisCalculator"/> —
    /// a stable identifier, not display text. Render it with
    /// <see cref="PidDiagnosisCalculator.Describe(string, PidMetricsPrediction)"/> so a
    /// Client shows its own language rather than the Server's.
    /// </summary>
    public string                Diagnosis            { get; set; } = "";
    /// Exact metrics read off the RK4 curve above — the only numbers ever shown to
    /// the student or handed to the LLM advisor, so nothing on screen can disagree
    /// with the plotted chart.
    public PidMetricsPrediction Metrics               { get; set; } = new();
    public string                AdvisorExplanation   { get; set; } = "";
    public PidPrediction?       AdvisorRecommendation { get; set; }
}

/// <summary>
/// Client-side helper that talks to a <see cref="ShareService"/>'s PID simulation
/// endpoint over HTTP, presenting the session token as the Bearer credential.
/// Mirrors the style of <see cref="TaskClient"/>.
/// </summary>
public static class PidDesignClient
{
    public static async Task<PidDesignResult?> RunAsync(string host, string token, PidInput input)
    {
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            // History keys are PascalCase to match PidAttempt's property names, so the
            // server's case-sensitive JsonSerializer.Deserialize<PidInput> populates them.
            var historyArr = new JsonArray();
            foreach (var h in input.History)
                historyArr.Add(new JsonObject
                {
                    ["Kp"]               = h.Kp,
                    ["Ki"]               = h.Ki,
                    ["Kd"]               = h.Kd,
                    ["Overshoot"]        = h.Overshoot,
                    ["RiseTime"]         = h.RiseTime,
                    ["SettlingTime"]     = h.SettlingTime,
                    ["SteadyStateError"] = h.SteadyStateError,
                    ["Diagnosis"]        = h.Diagnosis,
                });

            var body = new JsonObject
            {
                ["Kp"] = input.Kp,
                ["Ki"] = input.Ki,
                ["Kd"] = input.Kd,
                ["Setpoint"] = input.Setpoint,
                ["Language"] = input.Language,
                ["History"] = historyArr,
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{ShareProtocol.PidSimPath}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            var result = ParseResult(JsonNode.Parse(await resp.Content.ReadAsStringAsync()));
            // The server doesn't echo the setpoint back (older servers ignore it
            // entirely) — carry the value we asked for so the chart can draw it.
            if (result is not null) result.Setpoint = input.Setpoint;
            return result;
        }
        catch { return null; }
    }

    private static PidDesignResult? ParseResult(JsonNode? node)
    {
        var pred = node?["prediction"];
        var sim  = node?["simulation"];
        if (pred is null || sim is null) return null;

        var metrics    = node?["metrics"];
        var rec         = node?["advisorRecommendation"];

        return new PidDesignResult
        {
            Prediction = new PidPrediction
            {
                Kp = (float?)pred["Kp"] ?? 0,
                Ki = (float?)pred["Ki"] ?? 0,
                Kd = (float?)pred["Kd"] ?? 0,
            },
            Simulation = new SimulationResult
            {
                Time      = sim["time"]?.AsArray().Select(n => (double?)n ?? 0).ToArray()      ?? [],
                Amplitude = sim["amplitude"]?.AsArray().Select(n => (double?)n ?? 0).ToArray()  ?? [],
            },
            Diagnosis = (string?)node?["diagnosis"] ?? "",
            // Older servers may also send mlDiagnosis/mlEstimate; those fields are dead and ignored.
            Metrics = new PidMetricsPrediction
            {
                Overshoot        = (float?)metrics?["overshoot"]        ?? 0,
                RiseTime         = (float?)metrics?["riseTime"]         ?? 0,
                SettlingTime     = (float?)metrics?["settlingTime"]     ?? 0,
                SteadyStateError = (float?)metrics?["steadyStateError"] ?? 0,
            },
            AdvisorExplanation = (string?)node?["advisorExplanation"] ?? "",
            AdvisorRecommendation = rec is null ? null : new PidPrediction
            {
                Kp = (float?)rec["Kp"] ?? 0,
                Ki = (float?)rec["Ki"] ?? 0,
                Kd = (float?)rec["Kd"] ?? 0,
            },
        };
    }
}

/// <summary>
/// Single integration point the Dashboard UI uses for the Smart PID Designer, hiding
/// the server-vs-client difference: on the <b>Server</b> flavor it runs the RK4
/// simulator + rule-based diagnosis + LLM advisor in-process; on
/// the <b>Client</b> flavor it goes over HTTP via <see cref="PidDesignClient"/> to the
/// server the user is signed in to. Mirrors <see cref="LearningTaskService"/>.
/// </summary>
public static class PidDesignService
{
    private static readonly PidSimulator _sim = new();

    public static async Task<PidDesignResult?> RunAsync(PidInput input, CancellationToken ct = default)
    {
        // An empty/zero setpoint box would flatline the response and divide the
        // steady-state-error metric by zero — fall back to the classic unit step.
        if (float.IsNaN(input.Setpoint) || input.Setpoint <= 0) input.Setpoint = 1f;

        if (BuildInfo.IsServer)
        {
            var pred = new PidPrediction { Kp = input.Kp, Ki = input.Ki, Kd = input.Kd };
            var simResult = await Task.Run(() => _sim.SimulateStepResponse(pred, reference: input.Setpoint), ct);

            // Exact metrics off the real RK4 curve — the single source of truth for
            // the diagnosis classifier, the metric cards, and the LLM advisor.
            var (rise, overshootPct, settling, steadyErrPct) = PidSimulator.ComputeStepMetrics(simResult.Time, simResult.Amplitude, input.Setpoint);
            bool stable = PidSimulator.IsResponseStable(simResult.Amplitude, input.Setpoint);
            var metrics = new PidMetricsPrediction
            {
                Overshoot        = (float)overshootPct,
                RiseTime         = (float)rise,
                SettlingTime     = (float)settling,
                SteadyStateError = (float)(steadyErrPct / 100.0),
            };

            // Diagnosis is arithmetic on the metrics above — exact, instant, and able to
            // quote the number behind the verdict (see PidDiagnosisCalculator).
            var diagnosis = PidDiagnosisCalculator.Evaluate(metrics, stable).ToString();

            // Gains to offer come from searching the simulator (verified against the same
            // criteria as the diagnosis), not from the LLM — so the card can't contradict the
            // diagnosis. Null when the current tuning is already ideal. The one-time grid
            // search is cached, so this is O(1) after the first run; wrap it anyway to keep
            // the first run off the caller's thread.
            var recommendation = await Task.Run(() => PidRecommender.Recommend(metrics, stable), ct);

            // The LLM now only explains the recommended gains; it no longer picks numbers.
            var advisor = await new PidAdvisorService(input.Language).ReviewAsync(
                pred, metrics, input.Setpoint, input.History,
                recommendation?.gains, recommendation?.metrics, ct);

            return new PidDesignResult
            {
                Prediction = pred,
                Setpoint = input.Setpoint,
                // Metrics above are read off the full settled run; the chart only needs
                // the transient, so the flat tail isn't shipped or plotted.
                Simulation = PidSimulator.BuildDisplayCurve(simResult, settling),
                Diagnosis = diagnosis,
                Metrics = metrics,
                AdvisorExplanation = advisor.Explanation,
                AdvisorRecommendation = advisor.Recommendation,
            };
        }

        var s = AppSettingsService.Load();
        return await PidDesignClient.RunAsync(s.ServerHost, s.ServerToken, input);
    }
}
