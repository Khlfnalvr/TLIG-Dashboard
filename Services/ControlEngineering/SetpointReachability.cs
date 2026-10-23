namespace TLIGDashboard.Services.ControlEngineering;

/// <summary>
/// Menjelaskan mengapa sebuah setpoint tidak bisa dicapai, dari persamaan plant dan
/// teori pengendalian — bukan dari nilai gain. Dipakai saat setpoint berada di luar
/// jangkauan tunak aktuator (lihat <see cref="CascadeSimulator.MinReachableTemperature(double)"/>
/// dan <see cref="PidSimulator.MaxReachableTemperature"/>): pada kondisi itu tidak ada
/// Kp/Ki/Kd yang bisa menutup error, karena valve sudah di batas.
///
/// <para>Teksnya deterministik dan dilokalkan di sini (bukan hasil LLM), supaya angka
/// dan persamaan yang dibaca siswa selalu sama dengan yang diintegrasikan simulator.
/// Teks yang sama juga dikirim ke advisor sebagai konteks, agar ulasan LLM tidak
/// menyarankan retuning yang mustahil.</para>
/// </summary>
public static class SetpointReachability
{
    public static bool IsCascadeReachable(double setpoint, double inletTemperature) =>
        CascadeSimulator.IsSetpointReachable(setpoint, inletTemperature);

    public static bool IsSingleLoopReachable(double setpoint) =>
        PidSimulator.IsSetpointReachable(setpoint);

    /// <summary>Penjelasan lengkap (persamaan + teori) untuk plant cascade.</summary>
    public static string ExplainCascade(double setpoint, double inletTemperature)
    {
        double flowMax = CascadeSimulator.FlowBias + CascadeSimulator.K2 * CascadeSimulator.ValveMax;
        double tMin = CascadeSimulator.MinReachableTemperature(inletTemperature);
        double tMax = CascadeSimulator.MaxReachableTemperature(inletTemperature);
        double minError = setpoint > tMax ? setpoint - tMax : tMin - setpoint;

        return LocalizationManager.Instance.Format(nameof(LocalizationManager.Reach_Cascade),
            Num(setpoint),
            Num(CascadeSimulator.FlowBias),
            Num(CascadeSimulator.K2),
            Num(CascadeSimulator.ValveMin),
            Num(CascadeSimulator.ValveMax),
            Num(inletTemperature),
            Num(CascadeSimulator.K1),
            Num(flowMax),
            Num(tMax),
            Num(tMin),
            Num(minError));
    }

    /// <summary>Penjelasan lengkap (persamaan + teori) untuk plant loop tunggal.</summary>
    public static string ExplainSingleLoop(double setpoint)
    {
        var (gain, _) = PidSimulator.PlantCharacteristics();
        double tMin = PidSimulator.MinReachableTemperature;
        double tMax = PidSimulator.MaxReachableTemperature;
        double minError = setpoint > tMax ? setpoint - tMax : tMin - setpoint;

        return LocalizationManager.Instance.Format(nameof(LocalizationManager.Reach_SingleLoop),
            Num(setpoint),
            Num(gain),
            Num(PidSimulator.OutputMin),
            Num(PidSimulator.OutputMax),
            Num(tMax),
            Num(minError));
    }

    /// <summary>
    /// Label diagnosis singkat saat setpoint di luar jangkauan: menggantikan saran
    /// "naikkan Ki" yang tidak mungkin berhasil pada kondisi jenuh.
    /// </summary>
    public static string DescribeUnreachable(double setpoint, double minReachable, double maxReachable) =>
        LocalizationManager.Instance.Format(nameof(LocalizationManager.Diag_Unreachable),
            Num(setpoint), Num(minReachable), Num(maxReachable));

    /// <summary>
    /// Penjelasan operating point yang dulu ikut ditulis di kartu transfer function:
    /// hubungan suhu/flow, jangkauan plant untuk inlet saat ini, dan flow yang diminta
    /// setpoint. Deterministik, jadi aman dikirim ke AI chat sebagai konteks.
    /// </summary>
    public static string ExplainModel(Models.ControlEngineering.CascadeInput input)
    {
        double inlet = input.InletTemperature;
        double tMax = CascadeSimulator.MaxReachableTemperature(inlet);
        double flowForSp = 1.0 + (input.Setpoint - inlet) / CascadeSimulator.K1;
        return
            $"Penjelasan model cascade (Gp1/Gp2): T(t) = T_masuk + ΔT(t), dengan T_masuk = " +
            $"{inlet:F1} °C. Flow mengikuti F(t) = 1 + 0,06·valve (valve 0–100% → 1–7 L/min), " +
            $"dan suhu tunak T_ss = T_masuk + 10·(F − 1), jadi jangkauan tunak plant = " +
            $"{inlet:F1}–{tMax:F1} °C. Untuk setpoint {input.Setpoint:F1} °C, flow tunak yang " +
            $"diminta = {flowForSp:F2} L/min.";
    }

    public static string DescribeUnreachableCascade(double setpoint, double inletTemperature) =>
        DescribeUnreachable(setpoint,
            CascadeSimulator.MinReachableTemperature(inletTemperature),
            CascadeSimulator.MaxReachableTemperature(inletTemperature));

    public static string DescribeUnreachableSingleLoop(double setpoint) =>
        DescribeUnreachable(setpoint,
            PidSimulator.MinReachableTemperature, PidSimulator.MaxReachableTemperature);

    // Dua desimal tanpa nol ekor; mengikuti budaya angka UI seperti kartu metrik lain.
    private static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
}
