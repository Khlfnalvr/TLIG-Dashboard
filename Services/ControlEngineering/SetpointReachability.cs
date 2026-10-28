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
    public static bool IsCascadeReachable(double setpoint, double inletTemperature,
                                          double valveLimit = CascadeSimulator.ValveMax) =>
        CascadeSimulator.IsSetpointReachable(setpoint, inletTemperature, valveLimit);

    public static bool IsSingleLoopReachable(double setpoint) =>
        PidSimulator.IsSetpointReachable(setpoint);

    /// <summary>Penjelasan lengkap (persamaan + teori) untuk plant cascade.</summary>
    /// <param name="valveLimit">
    /// Batas bukaan valve (kotak "Bukaan Valve"). Dipakai sebagai "valve penuh" di
    /// penjelasan, karena di atas batas itu valve memang tidak dibuka.
    /// </param>
    public static string ExplainCascade(double setpoint, double inletTemperature,
                                        double valveLimit = CascadeSimulator.ValveMax)
    {
        double cap = CascadeSimulator.NormalizeValveLimit(valveLimit);
        double flowMax = CascadeSimulator.MaxFlow(cap);
        double tMin = CascadeSimulator.MinReachableTemperature(inletTemperature);
        double tMax = CascadeSimulator.MaxReachableTemperature(inletTemperature, cap);
        double minError = setpoint > tMax ? setpoint - tMax : tMin - setpoint;

        string text = LocalizationManager.Instance.Format(nameof(LocalizationManager.Reach_Cascade),
            Num(setpoint),
            Num(CascadeSimulator.FlowBias),
            Num4(CascadeSimulator.K2),
            Num(CascadeSimulator.ValveMin),
            Num(cap),
            Num(inletTemperature),
            Num(CascadeSimulator.K1),
            Num(flowMax),
            Num(tMax),
            Num(tMin),
            Num(minError));

        // Bila yang membatasi adalah kotak valve (bukan rig), sebutkan bukaan yang dibutuhkan.
        double need = CascadeSimulator.RequiredValve(setpoint, inletTemperature);
        if (setpoint > tMax && cap < CascadeSimulator.ValveMax && need <= CascadeSimulator.ValveMax)
            text += "\n\n" + LocalizationManager.Instance.Format(nameof(LocalizationManager.Reach_OpenValve),
                Num(cap), Num(setpoint), Num(Math.Ceiling(need)));
        return text;
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
        double tMaxRig = CascadeSimulator.MaxReachableTemperature(inlet);
        double flowForSp = CascadeSimulator.FlowBias + (input.Setpoint - inlet) / CascadeSimulator.K1;
        double valveForSp = CascadeSimulator.RequiredValve(input.Setpoint, inlet);
        string k2 = CascadeSimulator.K2.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
        string fMax = CascadeSimulator.FlowSpMax.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        string k1 = CascadeSimulator.K1.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
        return
            $"Penjelasan model cascade (Gp1/Gp2): T(t) = T_masuk + ΔT(t), dengan T_masuk = " +
            $"{inlet:F1} °C. Flow mengikuti F(t) = {k2}·valve (valve 0–100% → 0–{fMax} L/min, " +
            $"sama dengan flow maksimum di LabVIEW), dan suhu tunak T_ss = T_masuk + {k1}·F, jadi " +
            $"jangkauan tunak rig = {inlet:F1}–{tMaxRig:F1} °C. Untuk setpoint {input.Setpoint:F1} °C, " +
            $"flow tunak yang diminta = {flowForSp:F2} L/min, yaitu bukaan valve ≈ {valveForSp:F0}%. " +
            $"Di mode Auto PID yang mencari bukaan itu sendiri (bebas 0–100%, dan sementara " +
            $"boleh membuka lebih lebar supaya suhu naik lebih cepat); di mode Manual valve " +
            $"ditahan di nilai Bukaan Valve tanpa PID.";
    }

    /// <summary>
    /// Mode Manual: valve ditahan di <see cref="Models.ControlEngineering.CascadeInput.ManualValve"/>
    /// tanpa PID. Menjelaskan flow dan suhu tunak yang dihasilkan bukaan itu, dan bukaan yang
    /// dibutuhkan setpoint — pengganti ulasan advisor, karena tidak ada gain yang bekerja.
    /// </summary>
    public static string ExplainManual(Models.ControlEngineering.CascadeInput input)
    {
        double valve = CascadeSimulator.NormalizeValveLimit(input.ManualValve ?? 0f);
        double flow  = CascadeSimulator.MaxFlow(valve);
        double tSs   = CascadeSimulator.MaxReachableTemperature(input.InletTemperature, valve);
        double need  = CascadeSimulator.RequiredValve(input.Setpoint, input.InletTemperature);
        return LocalizationManager.Instance.Format(nameof(LocalizationManager.Manual_Explain),
            Num(valve), Num(flow), Num(tSs), Num(input.InletTemperature),
            Num(input.Setpoint), need <= CascadeSimulator.ValveMax ? Num(Math.Ceiling(need)) : ">100");
    }

    /// <summary>Label singkat kartu diagnosis untuk run manual.</summary>
    public static string DescribeManual(Models.ControlEngineering.CascadeInput input)
    {
        double valve = CascadeSimulator.NormalizeValveLimit(input.ManualValve ?? 0f);
        return LocalizationManager.Instance.Format(nameof(LocalizationManager.Diag_Manual),
            Num(valve), Num(CascadeSimulator.MaxReachableTemperature(input.InletTemperature, valve)));
    }

    public static string DescribeUnreachableCascade(double setpoint, double inletTemperature,
                                                    double valveLimit = CascadeSimulator.ValveMax)
    {
        double cap  = CascadeSimulator.NormalizeValveLimit(valveLimit);
        double tMin = CascadeSimulator.MinReachableTemperature(inletTemperature);
        double tMax = CascadeSimulator.MaxReachableTemperature(inletTemperature, cap);
        double need = CascadeSimulator.RequiredValve(setpoint, inletTemperature);

        // Yang membatasi kotak Bukaan Valve, bukan rig: sebutkan bukaan yang dibutuhkan.
        if (setpoint > tMax && cap < CascadeSimulator.ValveMax && need <= CascadeSimulator.ValveMax)
            return LocalizationManager.Instance.Format(nameof(LocalizationManager.Diag_UnreachableValve),
                Num(setpoint), Num(tMin), Num(tMax), Num(cap), Num(Math.Ceiling(need)));

        return DescribeUnreachable(setpoint, tMin, tMax);
    }

    public static string DescribeUnreachableSingleLoop(double setpoint) =>
        DescribeUnreachable(setpoint,
            PidSimulator.MinReachableTemperature, PidSimulator.MaxReachableTemperature);

    // Dua desimal tanpa nol ekor; mengikuti budaya angka UI seperti kartu metrik lain.
    private static string Num(double v) => v.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    private static string Num4(double v) => v.ToString("0.####", System.Globalization.CultureInfo.CurrentCulture);
}
