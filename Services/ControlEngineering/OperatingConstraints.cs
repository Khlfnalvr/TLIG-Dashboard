namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Batasan operasi rig (sama untuk Smart PID Designer &amp; Cascade):
/// bukaan valve 0–100% memetakan ke flow 0–4,65 L/min — flow maksimum yang terbaca
/// di LabVIEW pada valve 100%.
/// <code>
///   Flow(valve) = FlowMin + (FlowMax - FlowMin) * valve / 100
///               = 0.0465 * valve
///   Valve(flow) = (flow - FlowMin) / (FlowMax - FlowMin) * 100
/// </code>
/// Rekomendasi PID yang membuat valve / flow keluar batas DITOLAK
/// (tidak masuk kandidat), bukan sekadar di-clamp.
/// </summary>
public static class OperatingConstraints
{
    public const double ValveMin = 0.0;
    public const double ValveMax = 100.0;

    public const double FlowMin = 0.0;
    public const double FlowMax = 4.65;

    /// <summary>Flow (L/min) yang dihasilkan bukaan valve (%) tertentu.</summary>
    public static double FlowFromValve(double valvePct) =>
        FlowMin + (FlowMax - FlowMin) * valvePct / 100.0;

    /// <summary>Bukaan valve (%) yang menghasilkan flow (L/min) tertentu.</summary>
    public static double ValveFromFlow(double flow) =>
        (flow - FlowMin) / (FlowMax - FlowMin) * 100.0;

    public static bool IsValveWithin(double valvePct) =>
        valvePct >= ValveMin - 1e-9 && valvePct <= ValveMax + 1e-9;

    public static bool IsFlowWithin(double flow) =>
        flow >= FlowMin - 1e-9 && flow <= FlowMax + 1e-9;

    /// <summary>
    /// Slack rekayasa untuk PERMINTAAN MENTAH (sebelum saturasi): aktuator menutup
    /// sentuhan batas (mis. meminta 100.0035% → keluar 100% → flow tepat 4,65 L/min),
    /// jadi permintaan yang hanya menyentuh batas bukanlah pelanggaran. Yang ditolak
    /// adalah permintaan yang JELAS di luar batas (mis. 150% valve / 10.8 L/min).
    /// </summary>
    public const double RawValveTolerance = 0.5;   // % valve
    public const double RawFlowTolerance = 0.05;   // L/min (~0.8% span)

    /// <summary>True bila permintaan valve mentah masih dapat dipenuhi aktuator.</summary>
    public static bool IsRawValveOk(double valvePct) =>
        !double.IsNaN(valvePct) && !double.IsInfinity(valvePct)
        && valvePct >= ValveMin - RawValveTolerance
        && valvePct <= ValveMax + RawValveTolerance;

    /// <summary>True bila permintaan flow mentah masih di dalam jangkauan operasi.</summary>
    public static bool IsRawFlowOk(double flow) =>
        !double.IsNaN(flow) && !double.IsInfinity(flow)
        && flow >= FlowMin - RawFlowTolerance
        && flow <= FlowMax + RawFlowTolerance;

    /// <summary>
    /// True bila ada satu sampel pun di luar batas (dipakai filter recommender).
    /// NaN / Infinity ikut dihitung sebagai pelanggaran.
    /// </summary>
    public static bool TraceViolates(double[] valveTrace, double[] flowTrace)
    {
        if (valveTrace is not null)
            foreach (double v in valveTrace)
                if (double.IsNaN(v) || double.IsInfinity(v) || !IsValveWithin(v))
                    return true;
        if (flowTrace is not null)
            foreach (double f in flowTrace)
                if (double.IsNaN(f) || double.IsInfinity(f) || !IsFlowWithin(f))
                    return true;
        return false;
    }

    public static string Describe =>
        $"Valve {ValveMin:F0}–{ValveMax:F0}% ↔ Flow {FlowMin:0.##}–{FlowMax:0.##} L/min";
}
