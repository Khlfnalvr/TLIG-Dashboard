using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Shared Smart PID Designer state. The Dashboard's System Model panel and the
/// Parameter page (its extended screen, reached from that panel's fullscreen button)
/// are two views of one designer, so the gains, the setpoint and the last run live
/// here rather than in either page's own fields — a RUN started on one page is what
/// the other shows when you switch to it.
/// </summary>
public sealed class PidSessionService
{
    public static PidSessionService Instance { get; } = new();
    private PidSessionService() { }

    // Gains both views start from. A stable, well-behaved response for the fixed
    // plant in PidSimulator — the Parameter page used to open at Kp=Ki=Kd=10, which
    // is wildly unstable, and disagreed with the Dashboard's boxes besides.
    public double Kp       { get; set; } = 1;
    public double Ki       { get; set; } = 0.1;
    public double Kd       { get; set; } = 0.01;
    public double Setpoint { get; set; } = 10;

    /// <summary>Last successful run, or null if the designer hasn't been run yet.</summary>
    public PidDesignResult? LastResult { get; private set; }

    /// <summary>
    /// Advisor gains awaiting an explicit accept/decline. Held separately from
    /// <see cref="LastResult"/>.AdvisorRecommendation so a decline on one page stays
    /// declined on the other instead of reappearing on navigation.
    /// </summary>
    public PidPrediction? PendingRecommendation { get; private set; }

    public bool IsRunning { get; private set; }

    public event EventHandler<PidDesignResult>? ResultChanged;
    public event EventHandler<bool>? RunningChanged;
    /// <summary>A run could not reach the design service (server down / signed out).</summary>
    public event EventHandler? RunFailed;
    /// <summary>The pending advisor recommendation was accepted or declined.</summary>
    public event EventHandler? RecommendationCleared;

    /// <summary>
    /// Runs the RK4 preview for the current gains. Returns the result, or null if the
    /// service was unreachable (<see cref="RunFailed"/> is raised) or a run was already
    /// in flight.
    /// </summary>
    public async Task<PidDesignResult?> RunAsync(CancellationToken ct = default)
    {
        if (IsRunning) return null;

        // NumberBox yields NaN when cleared; a zero setpoint would flatline the response
        // and divide the steady-state-error metric by zero — normalize here, once, so
        // every view can read back the value actually simulated.
        if (double.IsNaN(Setpoint) || Setpoint <= 0) Setpoint = 1.0;
        if (double.IsNaN(Kp)) Kp = 0;
        if (double.IsNaN(Ki)) Ki = 0;
        if (double.IsNaN(Kd)) Kd = 0;

        SetRunning(true);
        try
        {
            var result = await PidDesignService.RunAsync(new PidInput
            {
                Kp       = (float)Kp,
                Ki       = (float)Ki,
                Kd       = (float)Kd,
                Setpoint = (float)Setpoint,
            }, ct);

            if (result is null)
            {
                RunFailed?.Invoke(this, EventArgs.Empty);
                return null;
            }

            LastResult            = result;
            PendingRecommendation = result.AdvisorRecommendation;
            ResultChanged?.Invoke(this, result);
            return result;
        }
        finally
        {
            SetRunning(false);
        }
    }

    /// <summary>
    /// Human-in-the-loop "accept": applies the pending advisor gains, then re-runs.
    /// Null if there was nothing pending.
    /// </summary>
    public Task<PidDesignResult?> AcceptRecommendationAsync(CancellationToken ct = default)
    {
        if (PendingRecommendation is not { } rec) return Task.FromResult<PidDesignResult?>(null);

        Kp = rec.Kp;
        Ki = rec.Ki;
        Kd = rec.Kd;
        ClearRecommendation();
        return RunAsync(ct);
    }

    /// <summary>Human-in-the-loop "decline": leaves the gains untouched.</summary>
    public void ClearRecommendation()
    {
        if (PendingRecommendation is null) return;
        PendingRecommendation = null;
        RecommendationCleared?.Invoke(this, EventArgs.Empty);
    }

    private void SetRunning(bool running)
    {
        IsRunning = running;
        RunningChanged?.Invoke(this, running);
    }
}
