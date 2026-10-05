using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class CascadeDesignResult
{
    public CascadeInput            Input                 { get; set; } = new();
    public CascadeSimulationResult Simulation            { get; set; } = new();
    public CascadeMetrics          Metrics               { get; set; } = new();
    /// <summary>
    /// <see cref="PidDiagnosisCode"/> name for the OUTER (temperature) PID, from
    /// <see cref="PidDiagnosisCalculator"/> — a stable identifier, not display text.
    /// Render it with <see cref="PidDiagnosisCalculator.Describe(string, PidMetricsPrediction)"/>
    /// so a Client shows its own language rather than the Server's.
    /// </summary>
    public string                  Diagnosis             { get; set; } = "";
    public string                  AdvisorExplanation    { get; set; } = "";
    public CascadeRecommendation?  AdvisorRecommendation { get; set; }
}

/// <summary>
/// Single integration point for the Cascade Control designer, mirroring
/// <see cref="PidDesignService"/>: on the <b>Server</b> flavor the RK4 simulation, the
/// diagnosis, the recommender search and the LLM review run in-process; on the
/// <b>Client</b> flavor the whole run goes over HTTP (<see cref="CascadeDesignClient"/>,
/// <c>POST /sim/cascade</c>) to the signed-in server, and the Client only draws the
/// curve the Server sends back. The Server is the single relay for everything the
/// Client shows — nothing is computed or read from the plant on the Client itself.
/// </summary>
public static class CascadeDesignService
{
    private static readonly CascadeSimulator _sim = new();

    public static async Task<CascadeDesignResult> RunAsync(
        CascadeInput input, IReadOnlyList<CascadeAttempt>? history = null, CancellationToken ct = default)
    {
        if (BuildInfo.IsServer)
            return await RunLocalAsync(input, history, "", ct);

        var s = AppSettingsService.Load();
        return await CascadeDesignClient.RunAsync(
                   s.ServerHost, s.ServerToken, input, history,
                   LocalizationManager.Instance.CurrentLanguage, ct)
               // Null = Server unreachable / not signed in. Thrown so CascadeSessionService
               // raises RunFailed and the page shows its "unavailable" message.
               ?? throw new InvalidOperationException("Cascade simulation server unavailable");
    }

    /// <summary>
    /// Runs the full cascade design in this process. Called by the Server for its own UI
    /// and by the <c>/sim/cascade</c> handler on behalf of a Client.
    /// </summary>
    /// <param name="language">
    /// Language of the LLM review; empty = this process's own setting. A Client passes its
    /// own UI language so the Server does not answer in the Server's language.
    /// </param>
    public static async Task<CascadeDesignResult> RunLocalAsync(
        CascadeInput input, IReadOnlyList<CascadeAttempt>? history, string language, CancellationToken ct = default)
    {
        // Guard against a cleared/zero setpoint (NumberBox yields NaN) — a flat response
        // would divide the steady-state-error metric by zero.
        if (float.IsNaN(input.Setpoint) || input.Setpoint <= 0) input.Setpoint = 60f;

        var simResult = await Task.Run(() => _sim.Simulate(input), ct);
        var metrics   = CascadeSimulator.ComputeMetrics(simResult, input);

        // Diagnose the OUTER temperature loop by direct calculation on its step metrics
        // (PidDiagnosisCalculator), not the ML classifier the single-loop designer
        // deliberately dropped: the outer plant is the identical FOPDT Gp1 the calculator's
        // thresholds are anchored to, so the verdict agrees with the metric cards, quotes
        // the number behind it, and is a localizable code rather than a raw, out-of-
        // distribution classifier label.
        var diagnosis = PidDiagnosisCalculator.Evaluate(metrics.PrimaryStepMetrics(), metrics.Stable).ToString();

        // Gains to offer come from searching the simulator (verified against the same criteria as
        // the diagnosis), not from the LLM — so the card can't contradict the diagnosis. Null when
        // the current tuning is already ideal. The per-setpoint search is cached after the first run.
        var recommendation = await Task.Run(() => CascadeRecommender.Recommend(metrics, metrics.Stable, input.Setpoint), ct);

        // The LLM now only explains the recommended gains; it no longer picks numbers.
        var advisor = await new CascadeAdvisorService(language).ReviewAsync(
            input, metrics, history, recommendation?.gains, recommendation?.metrics, ct, diagnosis);

        return new CascadeDesignResult
        {
            Input = input,
            Simulation = simResult,
            Metrics = metrics,
            Diagnosis = diagnosis,
            AdvisorExplanation = advisor.Explanation,
            AdvisorRecommendation = advisor.Recommendation,
        };
    }
}

/// <summary>
/// Client side of <c>POST /sim/cascade</c>: sends the gains + session history to the
/// server and rebuilds a <see cref="CascadeDesignResult"/> from its answer. Same style as
/// <see cref="PidDesignClient"/> — JSON via <see cref="JsonNode"/>, reflection-free.
/// </summary>
public static class CascadeDesignClient
{
    // The Server also writes the LLM review before answering, so allow more than the
    // plain PID endpoint's 30 s — a tunnel adds its own round-trip on top.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public static async Task<CascadeDesignResult?> RunAsync(
        string host, string token, CascadeInput input, IReadOnlyList<CascadeAttempt>? history,
        string language, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return null;

        try
        {
            var body = CascadeJson.WriteRequest(input, history, language);

            using var http = new HttpClient { Timeout = Timeout };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{ShareProtocol.CascadeSimPath}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            return CascadeJson.ReadResult(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)));
        }
        catch { return null; }
    }
}

/// <summary>
/// Wire format of <c>/sim/cascade</c>, shared by the Server handler and
/// <see cref="CascadeDesignClient"/> so both sides can never drift apart. Property names
/// follow the model classes. Non-finite numbers travel as <c>null</c> (JSON has no NaN)
/// and come back as NaN.
/// </summary>
public static class CascadeJson
{
    /// <summary>
    /// Points per series sent over the wire. The pages thin the curve to ~1500 points
    /// before plotting anyway, and metrics are computed on the Server from the full run,
    /// so shipping the raw ~10^4–10^5 RK4 samples through a tunnel would only cost time.
    /// </summary>
    private const int MaxWirePoints = 1500;

    // ── Request ──────────────────────────────────────────────────────────────

    public static JsonObject WriteRequest(CascadeInput input, IReadOnlyList<CascadeAttempt>? history, string language)
    {
        var hist = new JsonArray();
        foreach (var h in history ?? Array.Empty<CascadeAttempt>())
            hist.Add((JsonNode)new JsonObject
            {
                ["OuterKp"]          = F(h.OuterKp),
                ["OuterKi"]          = F(h.OuterKi),
                ["OuterKd"]          = F(h.OuterKd),
                ["InnerKp"]          = F(h.InnerKp),
                ["InnerKi"]          = F(h.InnerKi),
                ["Overshoot"]        = F(h.Overshoot),
                ["RiseTime"]         = F(h.RiseTime),
                ["SettlingTime"]     = F(h.SettlingTime),
                ["SteadyStateError"] = F(h.SteadyStateError),
                ["Diagnosis"]        = h.Diagnosis,
            });

        return new JsonObject
        {
            ["input"]    = WriteInput(input),
            ["history"]  = hist,
            ["language"] = language,
        };
    }

    public static (CascadeInput Input, List<CascadeAttempt> History, string Language) ReadRequest(JsonNode node)
    {
        var input = ReadInput(node["input"]);

        var history = new List<CascadeAttempt>();
        if (node["history"] is JsonArray arr)
            foreach (var h in arr)
            {
                if (h is null) continue;
                history.Add(new CascadeAttempt
                {
                    OuterKp          = RF(h["OuterKp"]),
                    OuterKi          = RF(h["OuterKi"]),
                    OuterKd          = RF(h["OuterKd"]),
                    InnerKp          = RF(h["InnerKp"]),
                    InnerKi          = RF(h["InnerKi"]),
                    Overshoot        = RF(h["Overshoot"]),
                    RiseTime         = RF(h["RiseTime"]),
                    SettlingTime     = RF(h["SettlingTime"]),
                    SteadyStateError = RF(h["SteadyStateError"]),
                    Diagnosis        = (string?)h["Diagnosis"] ?? "",
                });
            }

        return (input, history, ((string?)node["language"] ?? "").Trim());
    }

    // ── Result ───────────────────────────────────────────────────────────────

    public static JsonObject WriteResult(CascadeDesignResult r)
    {
        var sim = r.Simulation;
        int[] idx = WireIndices(sim.Time.Length);
        var m = r.Metrics;
        var rec = r.AdvisorRecommendation;

        return new JsonObject
        {
            ["input"] = WriteInput(r.Input),
            ["simulation"] = new JsonObject
            {
                ["Time"]                  = Series(sim.Time, idx),
                ["Temperature"]           = Series(sim.Temperature, idx),
                ["Flow"]                  = Series(sim.Flow, idx),
                ["FlowSetpoint"]          = Series(sim.FlowSetpoint, idx),
                ["Valve"]                 = Series(sim.Valve, idx),
                ["SingleLoopTemperature"] = Series(sim.SingleLoopTemperature, idx),
                ["DisturbanceTime"]       = F(sim.DisturbanceTime),
                ["Settled"]               = sim.Settled,
            },
            ["metrics"] = new JsonObject
            {
                ["RiseTime"]               = F(m.RiseTime),
                ["Overshoot"]              = F(m.Overshoot),
                ["SettlingTime"]           = F(m.SettlingTime),
                ["SteadyStateError"]       = F(m.SteadyStateError),
                ["IAE"]                    = F(m.IAE),
                ["ISE"]                    = F(m.ISE),
                ["ITAE"]                   = F(m.ITAE),
                ["FlowPeak"]               = F(m.FlowPeak),
                ["FlowSettled"]            = F(m.FlowSettled),
                ["CascadeMaxDeviation"]    = F(m.CascadeMaxDeviation),
                ["SingleLoopMaxDeviation"] = F(m.SingleLoopMaxDeviation),
                ["Stable"]                 = m.Stable,
            },
            ["diagnosis"]          = r.Diagnosis,
            ["advisorExplanation"] = r.AdvisorExplanation,
            ["advisorRecommendation"] = rec is null ? null : new JsonObject
            {
                ["OuterKp"] = F(rec.OuterKp),
                ["OuterKi"] = F(rec.OuterKi),
                ["OuterKd"] = F(rec.OuterKd),
                ["InnerKp"] = F(rec.InnerKp),
                ["InnerKi"] = F(rec.InnerKi),
            },
        };
    }

    public static CascadeDesignResult? ReadResult(JsonNode? node)
    {
        var sim = node?["simulation"];
        var m   = node?["metrics"];
        if (node is null || sim is null || m is null) return null;

        var rec = node["advisorRecommendation"];
        return new CascadeDesignResult
        {
            Input = ReadInput(node["input"]),
            Simulation = new CascadeSimulationResult
            {
                Time                  = ReadSeries(sim["Time"]),
                Temperature           = ReadSeries(sim["Temperature"]),
                Flow                  = ReadSeries(sim["Flow"]),
                FlowSetpoint          = ReadSeries(sim["FlowSetpoint"]),
                Valve                 = ReadSeries(sim["Valve"]),
                SingleLoopTemperature = ReadSeries(sim["SingleLoopTemperature"]),
                DisturbanceTime       = (double?)sim["DisturbanceTime"] ?? -1,
                Settled               = (bool?)sim["Settled"] ?? false,
            },
            Metrics = new CascadeMetrics
            {
                RiseTime               = RF(m["RiseTime"]),
                Overshoot              = RF(m["Overshoot"]),
                SettlingTime           = RF(m["SettlingTime"]),
                SteadyStateError       = RF(m["SteadyStateError"]),
                IAE                    = RF(m["IAE"]),
                ISE                    = RF(m["ISE"]),
                ITAE                   = RF(m["ITAE"]),
                FlowPeak               = RF(m["FlowPeak"]),
                FlowSettled            = RF(m["FlowSettled"]),
                CascadeMaxDeviation    = RF(m["CascadeMaxDeviation"]),
                SingleLoopMaxDeviation = RF(m["SingleLoopMaxDeviation"]),
                Stable                 = (bool?)m["Stable"] ?? false,
            },
            Diagnosis          = (string?)node["diagnosis"] ?? "",
            AdvisorExplanation = (string?)node["advisorExplanation"] ?? "",
            AdvisorRecommendation = rec is null ? null : new CascadeRecommendation
            {
                OuterKp = RF(rec["OuterKp"]),
                OuterKi = RF(rec["OuterKi"]),
                OuterKd = RF(rec["OuterKd"]),
                InnerKp = RF(rec["InnerKp"]),
                InnerKi = RF(rec["InnerKi"]),
            },
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static JsonObject WriteInput(CascadeInput i) => new()
    {
        ["OuterKp"]     = F(i.OuterKp),
        ["OuterKi"]     = F(i.OuterKi),
        ["OuterKd"]     = F(i.OuterKd),
        ["InnerKp"]     = F(i.InnerKp),
        ["InnerKi"]     = F(i.InnerKi),
        ["Setpoint"]    = F(i.Setpoint),
        ["Disturbance"] = F(i.Disturbance),
    };

    // Missing fields keep CascadeInput's own defaults rather than becoming 0 / NaN.
    private static CascadeInput ReadInput(JsonNode? n)
    {
        var i = new CascadeInput();
        if (n is null) return i;
        i.OuterKp     = (float?)n["OuterKp"]     ?? i.OuterKp;
        i.OuterKi     = (float?)n["OuterKi"]     ?? i.OuterKi;
        i.OuterKd     = (float?)n["OuterKd"]     ?? i.OuterKd;
        i.InnerKp     = (float?)n["InnerKp"]     ?? i.InnerKp;
        i.InnerKi     = (float?)n["InnerKi"]     ?? i.InnerKi;
        i.Setpoint    = (float?)n["Setpoint"]    ?? i.Setpoint;
        i.Disturbance = (float?)n["Disturbance"] ?? i.Disturbance;
        return i;
    }

    /// <summary>
    /// Evenly spaced sample indices (same stride the pages use to thin the chart), always
    /// ending on the last sample — the end value is what ProcessErrorService compares
    /// against the live plant.
    /// </summary>
    private static int[] WireIndices(int n)
    {
        if (n <= 0) return [];
        int stride = Math.Max(1, n / MaxWirePoints);
        var list = new List<int>(n / stride + 2);
        for (int i = 0; i < n; i += stride) list.Add(i);
        if (list[^1] != n - 1) list.Add(n - 1);
        return list.ToArray();
    }

    private static JsonArray Series(double[] values, int[] idx)
    {
        var arr = new JsonArray();
        // A series may be empty (e.g. no single-loop comparison) — send it empty, not padded.
        if (values.Length == 0) return arr;
        foreach (int i in idx)
            arr.Add(i < values.Length ? F(Math.Round(values[i], 4)) : null);
        return arr;
    }

    private static double[] ReadSeries(JsonNode? n) =>
        n is JsonArray arr ? arr.Select(v => (double?)v ?? double.NaN).ToArray() : [];

    private static JsonNode? F(double v) => double.IsFinite(v) ? JsonValue.Create(v) : null;

    private static float RF(JsonNode? n) => (float?)n ?? float.NaN;
}
