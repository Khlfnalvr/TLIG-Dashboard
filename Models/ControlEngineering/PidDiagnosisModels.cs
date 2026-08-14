namespace TLIGDashboard.Models.ControlEngineering;

public class PidDiagnosisInput
{
    public float Kp_Saat_Ini { get; set; }
    public float Ki_Saat_Ini { get; set; }
    public float Kd_Saat_Ini { get; set; }
    public float Overshoot_Riil { get; set; }
    public float RiseTime_Riil { get; set; }
    public float SettlingTime_Riil { get; set; }
    public float SteadyStateError { get; set; }
    public float Is_Stable { get; set; }
}

public class PidDiagnosisResult
{
    public string Rekomendasi_Aksi { get; set; } = "";
}
