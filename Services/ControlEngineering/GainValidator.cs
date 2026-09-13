using System;

namespace TLIGDashboard.Services.ControlEngineering;

public static class GainValidator
{
    public const double SingleKpMax = 50.0;
    public const double SingleKiMax = 10.0;
    public const double SingleKdMax = 100.0;

    public const double OuterKpMax = 50.0;
    public const double OuterKiMax = 5.0;
    public const double OuterKdMax = 100.0;

    public const double InnerKpMax = 10.0;
    public const double InnerKiMax = 10.0;

    public static (bool ok, string reason) ValidateSingle(double kp, double ki, double kd) =>
        Check("Kp", kp, SingleKpMax) ?? Check("Ki", ki, SingleKiMax) ?? Check("Kd", kd, SingleKdMax)
            ?? (true, "");

    public static (bool ok, string reason) ValidateOuter(double kp, double ki, double kd) =>
        Check("Kp", kp, OuterKpMax) ?? Check("Ki", ki, OuterKiMax) ?? Check("Kd", kd, OuterKdMax)
            ?? (true, "");

    public static (bool ok, string reason) ValidateInner(double kp, double ki) =>
        Check("Kp", kp, InnerKpMax) ?? Check("Ki", ki, InnerKiMax)
            ?? (true, "");

    public static double Sanitize(double v) => double.IsFinite(v) ? Math.Max(0, v) : 0;

    private static (bool ok, string reason)? Check(string name, double v, double max)
    {
        if (!double.IsFinite(v))
            return (false, $"{name} bukan angka valid ({FormatNum(v)}).");
        if (v < 0)
            return (false, $"{name}={FormatNum(v)} negatif — gain PID tidak boleh negatif.");
        if (v > max)
            return (false, $"{name}={FormatNum(v)} melebihi batas aman {FormatNum(max)} untuk plant ini.");
        return null;
    }

    private static string FormatNum(double v) =>
        double.IsFinite(v) ? v.ToString("0.###") : v.ToString();
}
