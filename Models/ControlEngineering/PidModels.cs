using System;
using System.Collections.Generic;

namespace TLIGDashboard.Models.ControlEngineering;

public class PidInput
{
    public float Kp { get; set; }
    public float Ki { get; set; }
    public float Kd { get; set; }
    /// Reference value the step response should settle at. Defaults to 1 so
    /// requests from older clients that don't send it keep the original behavior.
    public float Setpoint { get; set; } = 1f;
    /// UI language of the requester ("id" / "en"). The LLM advisor runs on the server
    /// even for Client-flavor users, so without carrying this the review would come back
    /// in the server's language instead of the student's. Empty = use the local setting.
    public string Language { get; set; } = "";
    /// <summary>
    /// Earlier attempts in this tuning session, oldest first — the gains tried and the
    /// response they actually produced. Sent so the LLM advisor reviews the current
    /// attempt against what came before (and can see that a gain set it already suggested
    /// did not improve things) instead of treating every RUN as the first. The history is
    /// carried in the request rather than kept on the server so a server handling several
    /// students at once never mixes one student's trajectory into another's. Empty on the
    /// first run and for older clients that don't send it.
    /// </summary>
    public List<PidAttempt> History { get; set; } = new();
}

/// <summary>
/// One past RUN in a tuning session: the gains that were tried and the metrics the RK4
/// simulator measured for them. Carried in <see cref="PidInput.History"/> so the advisor
/// reviews the current attempt against the ones before it.
/// </summary>
public class PidAttempt
{
    public float Kp { get; set; }
    public float Ki { get; set; }
    public float Kd { get; set; }
    public float Overshoot { get; set; }
    public float RiseTime { get; set; }
    public float SettlingTime { get; set; }
    public float SteadyStateError { get; set; }
    /// <summary>PidDiagnosisCode name for this attempt (see PidDiagnosisCalculator).</summary>
    public string Diagnosis { get; set; } = "";
}

public class PidPrediction
{
    public float Kp { get; set; }
    public float Ki { get; set; }
    public float Kd { get; set; }
}

public class SimulationResult
{
    public double[] Time { get; set; } = Array.Empty<double>();
    public double[] Amplitude { get; set; } = Array.Empty<double>();
    public PidPrediction UsedParameters { get; set; } = new();
}

/// Step-response metrics (Overshoot, RiseTime, SettlingTime, SteadyStateError) read
/// off PidSimulator's exact RK4 curve — the single source of truth shown to the
/// student and fed to the diagnosis (PidDiagnosisCalculator) and the LLM advisor.
public class PidMetricsPrediction
{
    public float Overshoot { get; set; }
    public float RiseTime { get; set; }
    public float SettlingTime { get; set; }
    public float SteadyStateError { get; set; }
}
