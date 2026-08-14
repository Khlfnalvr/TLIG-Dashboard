using TLIGDashboard.Models.ControlEngineering;

namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>Stable identifiers for a diagnosis — safe to send over the wire and log.</summary>
public enum PidDiagnosisCode
{
    Unstable,
    HighOvershoot,
    SteadyStateError,
    SlowResponse,
    Ideal,
}

/// <summary>
/// Rule-based diagnosis computed directly from the RK4 response metrics — no model,
/// no training, no inference.
///
/// <para>This replaces <see cref="PidDiagnosisAgent"/> as the diagnosis students see.
/// The classifier's own training labels turned out to encode a plain threshold
/// (overshoot &lt; 20% =&gt; ideal), so the model was only ever imitating arithmetic — and
/// imitating it imperfectly: measured against the rule its labels encode it agreed just
/// 93.7% of the time, calling responses with 20-42% overshoot "ideal" whenever Kd was
/// large (it had learned the shortcut "high Kd =&gt; well damped" from the gains being
/// features alongside the metrics). Computing the rule directly is exact, costs nothing,
/// and — the point for a teaching tool — can state <i>why</i>: "overshoot 41% exceeds the
/// 20% limit" rather than an opaque label.</para>
///
/// <para>Thresholds are deliberately constants rather than tuned parameters; they encode
/// ordinary step-response acceptance criteria and are the numbers quoted back to the
/// student, so they should stay easy to point at and justify.</para>
/// </summary>
public static class PidDiagnosisCalculator
{
    /// Textbook comfort limit for a well-damped step response. Also the exact boundary
    /// the training CSV's own labels used, so behaviour stays comparable to the classifier.
    public const double OvershootLimitPct = 20.0;

    /// Residual offset worth acting on. Below this the integral term has effectively
    /// closed the gap and the remainder is integration noise.
    public const double SteadyErrorLimitPct = 2.0;

    /// The identified plant Gp1 is slow: time constant 101.53 s plus 52.09 s of dead time,
    /// so it settles in ~458 s open-loop (θ + 4τ). A closed loop still taking longer than
    /// ~400 s is barely improving on doing nothing — a concrete, plant-derived anchor
    /// rather than an arbitrary "feels slow" number. A well-tuned loop reaches ~350 s.
    public const double SlowSettlingSeconds = 400.0;

    /// <summary>
    /// Picks the single most important thing wrong with a response, most severe first:
    /// an unstable loop makes every other metric meaningless, overshoot is the safety
    /// and quality concern, a standing offset means the setpoint is never actually met,
    /// and only then is mere slowness worth mentioning.
    /// </summary>
    public static PidDiagnosisCode Evaluate(PidMetricsPrediction metrics, bool stable)
    {
        if (!stable) return PidDiagnosisCode.Unstable;
        if (metrics.Overshoot > OvershootLimitPct) return PidDiagnosisCode.HighOvershoot;

        // Metrics.SteadyStateError is carried as a fraction (see PidDesignService), the
        // limit above is a percentage.
        if (Math.Abs(metrics.SteadyStateError) * 100.0 > SteadyErrorLimitPct)
            return PidDiagnosisCode.SteadyStateError;

        if (metrics.SettlingTime > SlowSettlingSeconds) return PidDiagnosisCode.SlowResponse;
        return PidDiagnosisCode.Ideal;
    }

    /// <summary>Parses a code that came back over the wire; unknown text falls back to Ideal.</summary>
    public static PidDiagnosisCode Parse(string? code) =>
        Enum.TryParse<PidDiagnosisCode>(code, ignoreCase: true, out var parsed)
            ? parsed
            : PidDiagnosisCode.Ideal;

    /// <summary>
    /// Localized sentence for the student, quoting the number that triggered the verdict.
    /// Localizing here — at display time, from the code — rather than baking a sentence
    /// into the result means a Client sees its own language, not the Server's.
    /// </summary>
    public static string Describe(PidDiagnosisCode code, PidMetricsPrediction metrics)
    {
        var lang = LocalizationManager.Instance;
        return code switch
        {
            PidDiagnosisCode.Unstable         => lang.Diag_Unstable,
            PidDiagnosisCode.HighOvershoot    => lang.Format(nameof(LocalizationManager.Diag_HighOvershoot),
                                                     metrics.Overshoot.ToString("0.0"), OvershootLimitPct.ToString("0")),
            PidDiagnosisCode.SteadyStateError => lang.Format(nameof(LocalizationManager.Diag_SteadyStateError),
                                                     (Math.Abs(metrics.SteadyStateError) * 100.0).ToString("0.00"),
                                                     SteadyErrorLimitPct.ToString("0")),
            PidDiagnosisCode.SlowResponse     => lang.Format(nameof(LocalizationManager.Diag_SlowResponse),
                                                     metrics.SettlingTime.ToString("0.0"),
                                                     SlowSettlingSeconds.ToString("0")),
            _                                 => lang.Format(nameof(LocalizationManager.Diag_Ideal),
                                                     metrics.Overshoot.ToString("0.0"),
                                                     metrics.SettlingTime.ToString("0.0")),
        };
    }

    /// <summary>Convenience for callers holding a raw wire string.</summary>
    public static string Describe(string? code, PidMetricsPrediction metrics)
        => Describe(Parse(code), metrics);
}
