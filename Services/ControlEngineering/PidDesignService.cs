using System.Text;
using System.Text.Json.Nodes;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

public sealed class PidDesignResult
{
    public PidPrediction    Prediction { get; set; } = new();
    public SimulationResult Simulation { get; set; } = new();
    public string           Diagnosis  { get; set; } = "";
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
        };
    }
}

/// <summary>
/// Single integration point the Dashboard UI uses for the Smart PID Designer, hiding
/// the server-vs-client difference: on the <b>Server</b> flavor it runs the RK4
/// simulator + diagnosis classifier in-process; on the <b>Client</b> flavor it goes
/// over HTTP via <see cref="PidDesignClient"/> to the server the user is signed in
/// to. Mirrors <see cref="LearningTaskService"/>.
/// </summary>
public static class PidDesignService
{
    private static readonly PidSimulator      _sim       = new();
    private static readonly PidDiagnosisAgent _diagnosis = new();

    public static async Task<PidDesignResult?> RunAsync(PidInput input, CancellationToken ct = default)
    {
        if (BuildInfo.IsServer)
        {
            var pred = new PidPrediction { Kp = input.Kp, Ki = input.Ki, Kd = input.Kd };
            var simResult = await Task.Run(() => _sim.SimulateStepResponse(pred), ct);

            // Diagnose the resulting response (local classifier, async, off the calling thread).
            var diagnosis = await Task.Run(() =>
            {
                var (rise, overshootPct, settling, steadyErrPct) = PidSimulator.ComputeStepMetrics(simResult.Time, simResult.Amplitude);
                bool stable = PidSimulator.IsResponseStable(simResult.Amplitude);
                return _diagnosis.Predict(new PidDiagnosisInput
                {
                    Kp_Saat_Ini        = pred.Kp,
                    Ki_Saat_Ini        = pred.Ki,
                    Kd_Saat_Ini        = pred.Kd,
                    Overshoot_Riil     = (float)overshootPct,
                    RiseTime_Riil      = (float)rise,
                    SettlingTime_Riil  = (float)settling,
                    SteadyStateError   = (float)(steadyErrPct / 100.0),
                    Is_Stable          = stable ? 1f : 0f,
                }).Rekomendasi_Aksi;
            }, ct);

            return new PidDesignResult { Prediction = pred, Simulation = simResult, Diagnosis = diagnosis };
        }

        var s = AppSettingsService.Load();
        return await PidDesignClient.RunAsync(s.ServerHost, s.ServerToken, input);
    }
}
