using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Shared Cascade Control designer state, mirroring <see cref="PidSessionService"/>. The
/// gains, setpoint, disturbance and last run live here so the page survives navigation
/// (it is cached) and a run in flight is reflected when the user returns.
/// </summary>
public sealed class CascadeSessionService
{
    public static CascadeSessionService Instance { get; } = new();
    private CascadeSessionService() { }

    // SIMC-tuned defaults for the identified FOPDT plants (see CascadeSimulator): a
    // stable, mildly overshooting temperature response that settles at the setpoint,
    // and a gentle inner PI suited to the delay-dominated flow plant.
    public double OuterKp     { get; set; } = 1.53;
    public double OuterKi     { get; set; } = 0.015;
    public double OuterKd     { get; set; } = 8.0;
    public double InnerKp     { get; set; } = 0.036;
    public double InnerKi     { get; set; } = 0.10;
    public double Setpoint    { get; set; } = 60;
    public double Disturbance { get; set; } = -40;

    public CascadeDesignResult? LastResult { get; private set; }
    public CascadeRecommendation? PendingRecommendation { get; private set; }
    public bool IsRunning { get; private set; }

    // Attempts made this session, oldest first — sent to the advisor on each RUN so it reviews
    // against the trajectory instead of treating every RUN as the first. Capped to bound the prompt.
    private readonly List<CascadeAttempt> _attempts = new();
    private const int MaxHistory = 5;

    /// <summary>Forget the tuning trajectory (e.g. when starting a fresh design).</summary>
    public void ResetHistory() => _attempts.Clear();

    public event EventHandler<CascadeDesignResult>? ResultChanged;
    public event EventHandler<bool>? RunningChanged;
    public event EventHandler? RunFailed;
    public event EventHandler? RecommendationCleared;

    public async Task<CascadeDesignResult?> RunAsync(CancellationToken ct = default)
    {
        if (IsRunning) return null;

        NormalizeInputs();
        SetRunning(true);
        try
        {
            // Prior attempts only — a snapshot taken before this run, so the advisor sees the
            // trajectory that led here without this run in it yet.
            var result = await CascadeDesignService.RunAsync(BuildInput(), new List<CascadeAttempt>(_attempts), ct);

            // Record this run so the next advisor review sees it as prior context; keep only the
            // most recent MaxHistory to bound the advisor prompt.
            _attempts.Add(new CascadeAttempt
            {
                OuterKp = result.Input.OuterKp,
                OuterKi = result.Input.OuterKi,
                OuterKd = result.Input.OuterKd,
                InnerKp = result.Input.InnerKp,
                InnerKi = result.Input.InnerKi,
                Overshoot = result.Metrics.Overshoot,
                RiseTime = result.Metrics.RiseTime,
                SettlingTime = result.Metrics.SettlingTime,
                SteadyStateError = result.Metrics.SteadyStateError,
                Diagnosis = result.Diagnosis,
            });
            if (_attempts.Count > MaxHistory)
                _attempts.RemoveRange(0, _attempts.Count - MaxHistory);

            LastResult            = result;
            PendingRecommendation = result.AdvisorRecommendation;
            ResultChanged?.Invoke(this, result);
            return result;
        }
        catch
        {
            RunFailed?.Invoke(this, EventArgs.Empty);
            return null;
        }
        finally
        {
            SetRunning(false);
        }
    }

    /// <summary>Applies the pending advisor gains, then re-runs. Null if none pending.</summary>
    public Task<CascadeDesignResult?> AcceptRecommendationAsync(CancellationToken ct = default)
    {
        if (PendingRecommendation is not { } rec) return Task.FromResult<CascadeDesignResult?>(null);

        OuterKp = rec.OuterKp;
        OuterKi = rec.OuterKi;
        OuterKd = rec.OuterKd;
        InnerKp = rec.InnerKp;
        InnerKi = rec.InnerKi;
        ClearRecommendation();
        return RunAsync(ct);
    }

    public void ClearRecommendation()
    {
        if (PendingRecommendation is null) return;
        PendingRecommendation = null;
        RecommendationCleared?.Invoke(this, EventArgs.Empty);
    }

    private CascadeInput BuildInput() => new()
    {
        OuterKp = (float)OuterKp,
        OuterKi = (float)OuterKi,
        OuterKd = (float)OuterKd,
        InnerKp = (float)InnerKp,
        InnerKi = (float)InnerKi,
        Setpoint = (float)Setpoint,
        Disturbance = (float)Disturbance,
    };

    // NumberBox yields NaN when cleared — normalise so every view reads back what ran.
    private void NormalizeInputs()
    {
        if (double.IsNaN(Setpoint) || Setpoint <= 0) Setpoint = 60;
        if (double.IsNaN(OuterKp)) OuterKp = 0;
        if (double.IsNaN(OuterKi)) OuterKi = 0;
        if (double.IsNaN(OuterKd)) OuterKd = 0;
        if (double.IsNaN(InnerKp)) InnerKp = 0;
        if (double.IsNaN(InnerKi)) InnerKi = 0;
        if (double.IsNaN(Disturbance)) Disturbance = 0;
    }

    private void SetRunning(bool running)
    {
        IsRunning = running;
        RunningChanged?.Invoke(this, running);
    }
}
