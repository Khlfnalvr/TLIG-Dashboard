#pragma warning disable CA1416  // Windows-only; this app targets win-x64 exclusively.

using System.Runtime.InteropServices;

namespace TLIGDashboard.Services;

/// <summary>
/// OPC DA (Classic) client using COM automation.
/// Connects to any local OPC DA server by ProgID (e.g. "National Instruments.NIOPCServers.V5").
/// Requires the target OPC server to be registered on this machine.
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

            // Activate the OPC DA COM server by ProgID.
            var type = Type.GetTypeFromProgID(ProgId, throwOnError: true)
                ?? throw new InvalidOperationException($"ProgID '{ProgId}' not found.");
            _serverObj = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Failed to create COM instance.");

            // Verify the server responds via the OPC Automation interface.
            dynamic srv = _serverObj;
            int state = (int)srv.ServerState;
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
                dynamic srv = _serverObj;
                srv.OPCGroups.RemoveAll();
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void FireError(string msg)
    {
        if ((DateTime.UtcNow - _lastErrorTime).TotalSeconds < 5) return;
        _lastErrorTime = DateTime.UtcNow;
        ErrorOccurred?.Invoke(msg);
    }

    public void Dispose() => Disconnect();
}
