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

    // SIMC defaults for the identified FOPDT plants, verified on the simulator itself:
    // overshoot 0.8%, rise 183 s, settling 315 s, no steady-state offset. Kept identical to
    // CascadeInput's defaults — see the derivation there. The disturbance is sized at ~42%
    // of the operating flow (~50 L/min at the default 60 °C setpoint), the same relative
    // upset the previous identification's -13 represented on its own smaller flow scale.
    public double OuterKp     { get; set; } = 1.8;
    public double OuterKi     { get; set; } = 0.007;
    public double OuterKd     { get; set; } = 0;
    public double InnerKp     { get; set; } = 0.0395;
    public double InnerKi     { get; set; } = 0.0295;
    public double Setpoint    { get; set; } = 60;
    public double Disturbance { get; set; } = -21;

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

    /// <summary>
    /// Masukan sesi ini baru saja diganti oleh run salinan dari Client (<see cref="RunMirroredAsync"/>).
    /// Layar yang menampilkan kotak gain/setpoint perlu menariknya ulang supaya angka di kotak
    /// sama dengan kurva yang digambar. Bisa dipanggil dari thread latar.
    /// </summary>
    public event EventHandler? RemoteInputsApplied;

    /// <summary>
    /// Menjalankan simulasi yang sama dengan yang baru dijalankan Client, supaya layar Server
    /// ikut menggambarnya. Dipanggil Server saat Client menekan RUN dan antriannya sudah
    /// memberi giliran (lihat <c>/sim/pid/run</c>). Penasihat AI dan riwayat percobaan
    /// sengaja dilewati: itu milik Client yang menekan RUN, bukan milik sesi Server.
    /// Null di tiap parameter = pakai nilai yang sedang ada di sesi ini.
    /// </summary>
    public async Task<CascadeDesignResult?> RunMirroredAsync(
        double outerKp, double outerKi, double outerKd, double setpoint,
        double? innerKp, double? innerKi, double? disturbance)
    {
        // Run Server sendiri yang sedang berjalan tidak boleh ditimpa kiriman Client.
        if (IsRunning) return null;

        OuterKp  = outerKp;
        OuterKi  = outerKi;
        OuterKd  = outerKd;
        Setpoint = setpoint;
        if (innerKp is { } ikp)     InnerKp     = ikp;
        if (innerKi is { } iki)     InnerKi     = iki;
        if (disturbance is { } dst) Disturbance = dst;

        try { RemoteInputsApplied?.Invoke(this, EventArgs.Empty); } catch { }
        return await RunAsync(default, mirrored: true);
    }

    public async Task<CascadeDesignResult?> RunAsync(CancellationToken ct = default, bool mirrored = false)
    {
        if (IsRunning) return null;

        NormalizeInputs();
        SetRunning(true);
        try
        {
            // Prior attempts only — a snapshot taken before this run, so the advisor sees the
            // trajectory that led here without this run in it yet.
            var result = await CascadeDesignService.RunAsync(
                BuildInput(), new List<CascadeAttempt>(_attempts), ct, mirrored);

            // Record this run so the next advisor review sees it as prior context; keep only the
            // most recent MaxHistory to bound the advisor prompt. A mirrored run is the Client's
            // attempt, not this session's, so it stays out of the trajectory.
            if (!mirrored)
            {
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
            }

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

        var (outerOk, _) = GainValidator.ValidateOuter(rec.OuterKp, rec.OuterKi, rec.OuterKd);
        var (innerOk, _) = GainValidator.ValidateInner(rec.InnerKp, rec.InnerKi);
        ClearRecommendation();
        if (!outerOk || !innerOk) return Task.FromResult<CascadeDesignResult?>(null);

        OuterKp = rec.OuterKp;
        OuterKi = rec.OuterKi;
        OuterKd = rec.OuterKd;
        InnerKp = rec.InnerKp;
        InnerKi = rec.InnerKi;
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

    private void NormalizeInputs()
    {
        if (!double.IsFinite(Setpoint) || Setpoint <= 0) Setpoint = 60;
        OuterKp = GainValidator.Sanitize(OuterKp);
        OuterKi = GainValidator.Sanitize(OuterKi);
        OuterKd = GainValidator.Sanitize(OuterKd);
        InnerKp = GainValidator.Sanitize(InnerKp);
        InnerKi = GainValidator.Sanitize(InnerKi);
        if (!double.IsFinite(Disturbance)) Disturbance = 0;
    }

    private void SetRunning(bool running)
    {
        IsRunning = running;
        RunningChanged?.Invoke(this, running);
    }
}
