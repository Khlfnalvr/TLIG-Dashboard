using System.Collections.Generic;
using System.Net;
using Microsoft.Windows.AppNotifications;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

public class NotificationService
{
    private readonly Dictionary<string, DateTime> _lastNotified = new();
    private readonly TimeSpan _cooldown = TimeSpan.FromSeconds(30);
    private bool _registered;

    private readonly List<AlertRecord> _history  = new();
    private readonly object            _histLock = new();
    private const int MaxHistory = 200;

    public event Action<AlertRecord>? AlertFired;
    /// <summary>
    /// Fired for Error/Alert-severity records only. UI layer hooks this to
    /// flash the taskbar so a minimized window still draws the user's eye.
    /// </summary>
    public event Action<AlertRecord>? CriticalAlertFired;

    public IReadOnlyList<AlertRecord> GetHistory()
    {
        lock (_histLock) return _history.ToList();
    }

    public void ClearHistory()
    {
        lock (_histLock) _history.Clear();
    }

    public void Register()
    {
        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch
        {
            _registered = false;
        }
    }

    /// <summary>
    /// Logs an application diagnostic event (parse error, connection change, etc.)
    /// directly to the alert history without a Windows toast and without cooldown.
    /// </summary>
    public void LogDiagnostic(AlertSeverity severity, string title, string body)
    {
        var rec = new AlertRecord(DateTime.Now, title, body, severity);
        lock (_histLock)
        {
            if (_history.Count >= MaxHistory) _history.RemoveAt(0);
            _history.Add(rec);
        }
        AlertFired?.Invoke(rec);
    }

    public void CheckAndNotify(BmsData data, BmsConfig config)
    {
        if (!_registered) return;
        EvaluateAndFire(data, config);
    }

    // Inlines the previous list-allocating EvaluateConditions: each alert is
    // dispatched directly on detection so the hot per-frame call no longer
    // allocates a List<(string,string,string)> and tuple boxes per frame.
    private void EvaluateAndFire(BmsData data, BmsConfig config)
    {
        var cells = data.Cells;
        var temps = data.Temps;

        double cellMin = cells[0], cellMax = cells[0];

        // Cell voltages — check hard cutoffs first, then soft warnings.
        for (int i = 0; i < cells.Length; i++)
        {
            double v = cells[i];
            if (v < cellMin) cellMin = v;
            if (v > cellMax) cellMax = v;

            if (v >= config.OvervoltageThreshold)
                Fire($"ov_{i}", "Alert_OvervoltageTitle", "Alert_CellOvervoltageBody",
                    [i + 1, v, config.OvervoltageThreshold],
                    AlertSeverity.Alert);
            else if (v >= config.HighVoltageWarning)
                Fire($"ovw_{i}", "Alert_HighVoltageTitle", "Alert_CellHighVoltageBody",
                    [i + 1, v, config.HighVoltageWarning],
                    AlertSeverity.Warning);
            else if (v <= config.UndervoltageThreshold)
                Fire($"uv_{i}", "Alert_UndervoltageTitle", "Alert_CellUndervoltageBody",
                    [i + 1, v, config.UndervoltageThreshold],
                    AlertSeverity.Alert);
            else if (v > 0 && v <= config.LowVoltageWarning)
                Fire($"uvw_{i}", "Alert_LowVoltageTitle", "Alert_CellLowVoltageBody",
                    [i + 1, v, config.LowVoltageWarning],
                    AlertSeverity.Warning);
        }

        // Current
        if (data.Current >= config.MaxChargeCurrent)
            Fire("oc_chg", "Alert_OvercurrentTitle", "Alert_ChargeCurrentBody",
                [data.Current, config.MaxChargeCurrent],
                AlertSeverity.Alert);
        if (Math.Abs(data.Current) >= config.MaxDischargeCurrent)
            Fire("oc_dsg", "Alert_OvercurrentTitle", "Alert_DischargeCurrentBody",
                [Math.Abs(data.Current), config.MaxDischargeCurrent],
                AlertSeverity.Alert);

        // Temperatures
        for (int i = 0; i < temps.Length; i++)
        {
            double t = temps[i];
            if (t >= config.OverTempCutoff)
                Fire($"otc_{i}", "Alert_TempCriticalTitle", "Alert_TempCriticalBody",
                    [i + 1, t, config.OverTempCutoff],
                    AlertSeverity.Alert);
            else if (t >= config.OverTempWarning)
                Fire($"otw_{i}", "Alert_TempWarningTitle", "Alert_TempWarningBody",
                    [i + 1, t, config.OverTempWarning],
                    AlertSeverity.Warning);
        }

        // Cell imbalance — uses the min/max we already computed above
        double delta = cellMax - cellMin;
        if (delta >= config.BalancingStartDelta)
            Fire("imb", "Alert_ImbalanceTitle", "Alert_ImbalanceBody",
                [delta * 1000, config.BalancingStartDelta * 1000],
                AlertSeverity.Warning);
    }

    private void Fire(string key, string titleKey, string bodyKey, object[] bodyArgs,
                      AlertSeverity severity = AlertSeverity.Alert)
    {
        if (!CanNotify(key)) return;

        var rec = AlertRecord.Localized(DateTime.Now, titleKey, bodyKey, bodyArgs, severity);
        lock (_histLock)
        {
            if (_history.Count >= MaxHistory) _history.RemoveAt(0);
            _history.Add(rec);
        }
        AlertFired?.Invoke(rec);

        bool critical = severity is AlertSeverity.Error or AlertSeverity.Alert;
        if (critical) CriticalAlertFired?.Invoke(rec);

        try
        {
            // scenario="urgent" (Win11) bypasses Focus Assist when the app is
            // on the priority list and keeps the toast sticky in Action Center
            // — gives the user a chance to react even when minimized.
            string scenarioAttr = critical ? " scenario='urgent'" : string.Empty;
            string xml =
                $"<toast{scenarioAttr}>" +
                  $"<visual><binding template='ToastGeneric'>" +
                    $"<text>{Escape(rec.Title)}</text>" +
                    $"<text>{Escape(rec.Body)}</text>" +
                  $"</binding></visual>" +
                $"</toast>";
            AppNotificationManager.Default.Show(new AppNotification(xml));
        }
        catch
        {
            // Notification failed — will retry on next cycle
        }
    }

    private static string Escape(string s) => WebUtility.HtmlEncode(s);

    private bool CanNotify(string key)
    {
        if (_lastNotified.TryGetValue(key, out var last) && DateTime.Now - last < _cooldown)
            return false;
        _lastNotified[key] = DateTime.Now;
        return true;
    }
}
