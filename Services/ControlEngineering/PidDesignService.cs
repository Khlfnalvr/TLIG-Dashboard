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
    /// <summary>
    /// <see cref="PidDiagnosisAgent"/>'s label for the same response, computed but not
    /// shown — kept for the same reason as <see cref="MlEstimate"/>. The classifier only
    /// ever imitated the threshold rule its training labels encode, and imitated it
    /// imperfectly (see PidDiagnosisCalculator), so the rule itself is what students see.
    /// </summary>
    public string                MlDiagnosis          { get; set; } = "";
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
            MlDiagnosis = (string?)node?["mlDiagnosis"] ?? "",
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

            // Classifier still runs for comparison, but no longer drives what is shown.
            var mlDiagnosis = await Task.Run(() => _diagnosis.Predict(new PidDiagnosisInput
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

            var advisor = await new PidAdvisorService(input.Language).ReviewAsync(pred, metrics, input.Setpoint, input.History, ct);

            return new PidDesignResult
            {
                Prediction = pred,
                Setpoint = input.Setpoint,
                // Metrics above are read off the full settled run; the chart only needs
                // the transient, so the flat tail isn't shipped or plotted.
                Simulation = PidSimulator.BuildDisplayCurve(simResult, settling),
                Diagnosis = diagnosis,
                MlDiagnosis = mlDiagnosis,
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
