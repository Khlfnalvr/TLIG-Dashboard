using System.Text;
using System.Text.Json.Nodes;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class PidDesignResult
{
    public PidPrediction        Prediction            { get; set; } = new();
    public SimulationResult     Simulation            { get; set; } = new();
    public string                Diagnosis            { get; set; } = "";
    /// Exact metrics read off the RK4 curve above — the only numbers ever shown to
    /// the student or handed to the LLM advisor, so nothing on screen can disagree
    /// with the plotted chart.
    public PidMetricsPrediction Metrics               { get; set; } = new();
    /// PidMetricsRegressor's raw multi-output-regression estimate, computed on every
    /// run but NOT surfaced in the UI: it was found to diverge sharply from the RK4
    /// ground truth for gain combinations sparse in the training data (e.g. low Kp),
    /// which produced Advisor text that contradicted the plotted chart. Kept here so
    /// the model stays exercised/available without misleading anyone.
    public PidMetricsPrediction MlEstimate            { get; set; } = new();
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
            var body = new JsonObject
            {
                ["Kp"] = input.Kp,
                ["Ki"] = input.Ki,
                ["Kd"] = input.Kd,
            };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{ShareProtocol.PidSimPath}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            return ParseResult(JsonNode.Parse(await resp.Content.ReadAsStringAsync()));
        }
        catch { return null; }
    }

    private static PidDesignResult? ParseResult(JsonNode? node)
    {
        var pred = node?["prediction"];
        var sim  = node?["simulation"];
        if (pred is null || sim is null) return null;

        var metrics    = node?["metrics"];
        var mlEstimate = node?["mlEstimate"];
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
            Metrics = new PidMetricsPrediction
            {
                Overshoot        = (float?)metrics?["overshoot"]        ?? 0,
                RiseTime         = (float?)metrics?["riseTime"]         ?? 0,
                SettlingTime     = (float?)metrics?["settlingTime"]     ?? 0,
                SteadyStateError = (float?)metrics?["steadyStateError"] ?? 0,
            },
            MlEstimate = new PidMetricsPrediction
            {
                Overshoot        = (float?)mlEstimate?["overshoot"]        ?? 0,
                RiseTime         = (float?)mlEstimate?["riseTime"]         ?? 0,
                SettlingTime     = (float?)mlEstimate?["settlingTime"]     ?? 0,
                SteadyStateError = (float?)mlEstimate?["steadyStateError"] ?? 0,
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
/// simulator + diagnosis classifier + metrics regressor + LLM advisor in-process; on
/// the <b>Client</b> flavor it goes over HTTP via <see cref="PidDesignClient"/> to the
/// server the user is signed in to. Mirrors <see cref="LearningTaskService"/>.
/// </summary>
public static class PidDesignService
{
    private static readonly PidSimulator        _sim       = new();
    private static readonly PidDiagnosisAgent   _diagnosis = new();
    private static readonly PidMetricsRegressor _mlMetrics = new();

    public static async Task<PidDesignResult?> RunAsync(PidInput input, CancellationToken ct = default)
    {
        if (BuildInfo.IsServer)
        {
            var pred = new PidPrediction { Kp = input.Kp, Ki = input.Ki, Kd = input.Kd };
            var simResult = await Task.Run(() => _sim.SimulateStepResponse(pred), ct);

            // Exact metrics off the real RK4 curve — the single source of truth for
            // the diagnosis classifier, the metric cards, and the LLM advisor.
            var (rise, overshootPct, settling, steadyErrPct) = PidSimulator.ComputeStepMetrics(simResult.Time, simResult.Amplitude);
            bool stable = PidSimulator.IsResponseStable(simResult.Amplitude);
            var metrics = new PidMetricsPrediction
            {
                Overshoot        = (float)overshootPct,
                RiseTime         = (float)rise,
                SettlingTime     = (float)settling,
                SteadyStateError = (float)(steadyErrPct / 100.0),
            };

            var diagnosis = await Task.Run(() => _diagnosis.Predict(new PidDiagnosisInput
            {
                Kp_Saat_Ini        = pred.Kp,
                Ki_Saat_Ini        = pred.Ki,
                Kd_Saat_Ini        = pred.Kd,
                Overshoot_Riil     = metrics.Overshoot,
                RiseTime_Riil      = metrics.RiseTime,
                SettlingTime_Riil  = metrics.SettlingTime,
                SteadyStateError   = metrics.SteadyStateError,
                Is_Stable          = stable ? 1f : 0f,
            }).Rekomendasi_Aksi, ct);

            // Multi-output ML.NET estimate straight from Kp/Ki/Kd — kept computed and
            // available (see PidDesignResult.MlEstimate) but no longer what drives the
            // Advisor prompt or the metric cards; see the comment above PidDesignResult.
            var mlEstimate = await Task.Run(() => _mlMetrics.Predict(pred.Kp, pred.Ki, pred.Kd), ct);

            var advisor = await new PidAdvisorService().ReviewAsync(pred, metrics, ct);

            return new PidDesignResult
            {
                Prediction = pred,
                Simulation = simResult,
                Diagnosis = diagnosis,
                Metrics = metrics,
                MlEstimate = mlEstimate,
                AdvisorExplanation = advisor.Explanation,
                AdvisorRecommendation = advisor.Recommendation,
            };
        }

        var s = AppSettingsService.Load();
        return await PidDesignClient.RunAsync(s.ServerHost, s.ServerToken, input);
    }
}
