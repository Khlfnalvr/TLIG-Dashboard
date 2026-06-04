#pragma warning disable CA1416  // Windows-only; this app targets win-x64 exclusively.

using System.Reflection;
using System.Runtime.InteropServices;

namespace TLIGDashboard.Services;

/// <summary>
/// OPC DA (Classic) client using COM IDispatch via Type.InvokeMember.
/// Avoids dynamic/DLR so it survives PublishTrimmed.
/// Connects to any local OPC DA server by ProgID (e.g. "National Instruments.OPCFieldPoint").
/// </summary>
public sealed class OpcDaService : IDisposable
{
    // ── Public events ─────────────────────────────────────────────────────────
    public event Action<string>? StatusChanged;
    public event Action<string>? ErrorOccurred;

    // ── Live state ────────────────────────────────────────────────────────────
    public bool   IsConnected { get; private set; }
    public string ProgId      { get; private set; } = "";

    // ── Internals ─────────────────────────────────────────────────────────────
    private object?  _serverObj;
    private DateTime _lastErrorTime = DateTime.MinValue;

    // ── Connect ───────────────────────────────────────────────────────────────

    public Task<bool> ConnectAsync(string progId)
    {
        try
        {
            Disconnect();
            ProgId = progId.Trim();

            var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)
                ?? throw new InvalidOperationException($"ProgID '{ProgId}' not found.");
            _serverObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Failed to create COM instance.");

            // Read ServerState via IDispatch — no DLR/dynamic needed.
            var stateObj = ComGet(_serverObj, "ServerState");
            int state = stateObj is null ? 0 : Convert.ToInt32(stateObj);
            // OPC_STATUS_RUNNING = 1; warn on other states but still consider connected.
            if (state != 1)
                FireError(LocalizationManager.Instance.Format("OpcDa_ServerNotRunning", state));

            IsConnected = true;
            StatusChanged?.Invoke(
                LocalizationManager.Instance.Format("OpcUa_StatusConnected", ProgId));
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            IsConnected = false;
            FireError(ex.Message);
            return Task.FromResult(false);
        }
    }

    // ── Disconnect ────────────────────────────────────────────────────────────

    public void Disconnect()
    {
        if (_serverObj is not null)
        {
            try
            {
                var groups = ComGet(_serverObj, "OPCGroups");
                if (groups is not null)
                    ComInvoke(groups, "RemoveAll");
            }
            catch { /* best-effort cleanup */ }

            Marshal.ReleaseComObject(_serverObj);
            _serverObj = null;
        }

        bool wasConnected = IsConnected;
        IsConnected = false;

        if (wasConnected)
            StatusChanged?.Invoke(
                LocalizationManager.Instance.Get("OpcUa_StatusDisconnected"));
    }

    // ── IDispatch helpers (no DLR, trim-safe) ────────────────────────────────

    private static object? ComGet(object comObj, string member) =>
        comObj.GetType().InvokeMember(
            member,
            BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
            null, comObj, null);

    private static void ComInvoke(object comObj, string method, params object[] args) =>
        comObj.GetType().InvokeMember(
            method,
            BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
            null, comObj, args);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FireError(string msg)
    {
        if ((DateTime.UtcNow - _lastErrorTime).TotalSeconds < 5) return;
        _lastErrorTime = DateTime.UtcNow;
        ErrorOccurred?.Invoke(msg);
    }

    public void Dispose() => Disconnect();
}
