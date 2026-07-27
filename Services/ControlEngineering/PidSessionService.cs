using System.Collections.Generic;
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

    // Gains both views start from, carried over from DashboardPage's XAML literals
    // (commit 5173fe0). They are placeholders — not derived from the plant or from any
    // tuning method — and the Parameter page disagreed with them (it opened at
    // Kp=Ki=Kd=10), which is the inconsistency unifying them here fixes.
    //
    // Both pairs are in fact stable for PidSimulator's plant (Routh-Hurwitz on
    // s^3 + (A1+B*Kd)s^2 + (A0+B*Kp)s + B*Ki): 10/10/10 is merely aggressive
    // (~32% overshoot, settles ~7.9s) where these settle ~13.8s with ~4.5% overshoot.
    // Caveat: 13.8s exceeds PidSimulator's hard-coded 10s window, so the metrics for
    // these very gains are read off a response that has not settled yet.
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

    // Attempts made this session, oldest first — sent to the LLM advisor on each RUN so it
    // reviews against the trajectory (and sees that a gain set it already suggested didn't
    // help) instead of treating every RUN as the first. Capped so the advisor prompt stays
    // small; the most recent attempts are the ones that matter.
    private readonly List<PidAttempt> _attempts = new();
    private const int MaxHistory = 5;

    /// <summary>Forget the tuning trajectory (e.g. when starting a fresh design).</summary>
    public void ResetHistory() => _attempts.Clear();

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
                // Carried so a Client-flavor student gets the advisor's review in their
                // own language rather than the server's.
                Language = LocalizationManager.Instance.CurrentLanguage,
                // Prior attempts only — a snapshot taken before this run, so the advisor
                // sees the trajectory that led here without this run in it yet.
                History  = new List<PidAttempt>(_attempts),
            }, ct);

            if (result is null)
            {
                RunFailed?.Invoke(this, EventArgs.Empty);
                return null;
            }

            // Record this run so the next advisor review sees it as prior context. Keep only
            // the most recent MaxHistory to bound the advisor prompt.
            _attempts.Add(new PidAttempt
            {
                Kp               = result.Prediction.Kp,
                Ki               = result.Prediction.Ki,
                Kd               = result.Prediction.Kd,
                Overshoot        = result.Metrics.Overshoot,
                RiseTime         = result.Metrics.RiseTime,
                SettlingTime     = result.Metrics.SettlingTime,
                SteadyStateError = result.Metrics.SteadyStateError,
                Diagnosis        = result.Diagnosis,
            });
            if (_attempts.Count > MaxHistory)
                _attempts.RemoveRange(0, _attempts.Count - MaxHistory);

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
