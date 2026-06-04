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

    public async Task<bool> ConnectAsync(string progId)
    {
        Disconnect();
        ProgId = progId.Trim();

        string? error = null;

        await Task.Run(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)
                    ?? throw new InvalidOperationException($"ProgID '{ProgId}' not found.");
                _serverObj = Activator.CreateInstance(type)
                    ?? throw new InvalidOperationException("Failed to create COM instance.");

                // Optionally probe ServerState — skip on failure so servers that
                // don't expose the property via IDispatch still work.
                try
                {
                    var stateObj = ComGet(_serverObj, "ServerState");
                    int state = stateObj is null ? 1 : Convert.ToInt32(stateObj);
                    if (state != 1)
                        error = LocalizationManager.Instance.Format("OpcDa_ServerNotRunning", state);
                }
                catch { /* ServerState not accessible via IDispatch — ignore */ }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                if (_serverObj is not null)
                {
                    try { Marshal.ReleaseComObject(_serverObj); } catch { }
                    _serverObj = null;
                }
            }
        });

        if (_serverObj is null)
        {
            IsConnected = false;
            if (error is not null) FireError(error);
            return false;
        }

        IsConnected = true;
        if (error is not null) FireError(error); // warn but stay connected
        StatusChanged?.Invoke(
            LocalizationManager.Instance.Format("OpcUa_StatusConnected", ProgId));
        return true;
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
