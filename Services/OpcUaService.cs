#pragma warning disable CS0618  // OPC UA v1.5 still exposes obsolete synchronous wrappers; async versions are used where practical.

using System.Text;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;

namespace TLIGDashboard.Services;

// ── Enums ─────────────────────────────────────────────────────────────────────

public enum OpcUaSecurityMode { None, Sign, SignAndEncrypt }
public enum OpcUaAuthMode     { Anonymous, UsernamePassword }

// ── Node-ID mapping for HMI data fields ─────────────────────────────────────────

public class OpcUaNodeConfig
{
    public string   PackVoltageNodeId    { get; set; } = "ns=2;s=HMI.MainValue";
    public string   CurrentNodeId        { get; set; } = "ns=2;s=HMI.Current";
    public string   SocNodeId            { get; set; } = "ns=2;s=HMI.Level";
    public string   StatusNodeId         { get; set; } = "ns=2;s=HMI.Status";
    public string[] CellNodeIds          { get; set; } = Enumerable.Range(1, 20).Select(i => $"ns=2;s=HMI.Channel{i}").ToArray();
    public string[] TempNodeIds          { get; set; } = Enumerable.Range(1, 10).Select(i => $"ns=2;s=HMI.Sensor{i}").ToArray();
    public string[] BalNodeIds           { get; set; } = Enumerable.Range(1, 20).Select(i => $"ns=2;s=HMI.Flag{i}").ToArray();
    public int      PublishingIntervalMs { get; set; } = 1000;
}

// ── Service ───────────────────────────────────────────────────────────────────

/// <summary>
/// OPC UA client service.
///
/// Backend mechanism:
///   1. ConnectAsync() builds an ApplicationConfiguration with a self-signed
///      client certificate stored in %LOCALAPPDATA%\TLIGDashboard\pki.
///      On first run the certificate is auto-generated via ApplicationInstance.
///   2. CoreClientUtils.SelectEndpoint() discovers the server's endpoint list
///      and selects the best match for the requested security policy.
///   3. Session.Create() opens a secure channel (UA Binary TCP) and negotiates
///      the UA session with the server.
///   4. SetupSubscription() creates one Subscription (push model) and registers
///      MonitoredItems for every HMI node configured in NodeConfig,
///      all sampled at NodeConfig.PublishingIntervalMs.
///   5. When the server detects a value change it sends a PublishResponse.
///      OnItemNotification() is called per item, merges the value into _last
///      (thread-safe via lock), clones the snapshot, and fires DataReceived.
///      MainViewModel routes DataReceived → ApplyData (identical to old Serial path).
///   6. Session_KeepAlive() monitors the connection; if the server goes away,
///      SessionReconnectHandler retries every 5 s and fires StatusChanged on
///      recovery without losing the subscription.
/// </summary>
public sealed class OpcUaService : IDisposable
{
    // ── Public events ─────────────────────────────────────────────────────────
    public event Action<string>?  StatusChanged;
    public event Action<string>?  ErrorOccurred;

    // ── Live state ────────────────────────────────────────────────────────────
    public bool   IsConnected    => _session?.Connected == true;
    public string EndpointUrl    { get; private set; } = "";
    public int    FramesReceived { get; private set; }
    public int    ErrorCount     { get; private set; }

    public OpcUaNodeConfig NodeConfig { get; set; } = new();

    // ── Internals ─────────────────────────────────────────────────────────────
    private Session?               _session;
    private SessionReconnectHandler? _reconnectHandler;
    private ApplicationConfiguration? _appConfig;

    private DateTime _lastErrorTime = DateTime.MinValue;

    // ── Application configuration ─────────────────────────────────────────────

    private static async Task<ApplicationConfiguration> BuildAppConfigAsync()
    {
        var pkiRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLIGDashboard", "pki");

        var config = new ApplicationConfiguration
        {
            ApplicationName = "TLIGDashboard",
            ApplicationUri  = "urn:TLIGDashboard:Client",
            ApplicationType = ApplicationType.Client,
            SecurityConfiguration = new SecurityConfiguration
            {
                ApplicationCertificate = new CertificateIdentifier
                {
                    StoreType   = "Directory",
                    StorePath   = Path.Combine(pkiRoot, "own"),
                    SubjectName = "CN=TLIGDashboard"
                },
                TrustedIssuerCertificates = new CertificateTrustList
                    { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "issuer") },
                TrustedPeerCertificates = new CertificateTrustList
                    { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "trusted") },
                RejectedCertificateStore = new CertificateTrustList
                    { StoreType = "Directory", StorePath = Path.Combine(pkiRoot, "rejected") },
                AutoAcceptUntrustedCertificates = true,
                AddAppCertToTrustedStore        = true,
                NonceLength                     = 32
            },
            TransportConfigurations = new TransportConfigurationCollection(),
            TransportQuotas = new TransportQuotas { OperationTimeout = 15_000 },
            ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = 60_000 }
        };

        await config.Validate(ApplicationType.Client);

        // Create self-signed client certificate on first run if it doesn't exist.
        var appInstance = new ApplicationInstance(config);
        await appInstance.CheckApplicationInstanceCertificatesAsync(false, 2048);

        // Accept all server certificates (appropriate for closed industrial LAN).
        config.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

        return config;
    }

    // ── Connect / Disconnect ──────────────────────────────────────────────────

    public async Task<bool> ConnectAsync(
        string            endpointUrl,
        OpcUaAuthMode     authMode  = OpcUaAuthMode.Anonymous,
        string            username  = "",
        string            password  = "",
        OpcUaSecurityMode secMode   = OpcUaSecurityMode.None)
    {
        try
        {
            Disconnect();
            EndpointUrl = endpointUrl.Trim();
            _appConfig  = await BuildAppConfigAsync();

            var msgSecMode = secMode switch
            {
                OpcUaSecurityMode.Sign          => MessageSecurityMode.Sign,
                OpcUaSecurityMode.SignAndEncrypt => MessageSecurityMode.SignAndEncrypt,
                _                               => MessageSecurityMode.None
            };

            // Discover endpoint on the server and pick the best security match.
            var endpointDesc = CoreClientUtils.SelectEndpoint(
                _appConfig, EndpointUrl, msgSecMode != MessageSecurityMode.None);
            var endpoint = new ConfiguredEndpoint(
                null, endpointDesc, EndpointConfiguration.Create(_appConfig));

            // Build user identity.
            IUserIdentity identity;
            if (authMode == OpcUaAuthMode.UsernamePassword)
            {
                var token = new UserNameIdentityToken
                {
                    UserName          = username,
                    DecryptedPassword = Encoding.UTF8.GetBytes(password)
                };
                identity = new UserIdentity(token);
            }
            else
            {
                identity = new UserIdentity(new AnonymousIdentityToken());
            }

            _session = await Session.Create(
                _appConfig,
                endpoint,
                false,
                "TLIGDashboard",
                60_000,
                identity,
                null);

            _session.KeepAlive += Session_KeepAlive;

            StatusChanged?.Invoke(
                LocalizationManager.Instance.Format("OpcUa_StatusConnected", EndpointUrl));
            return true;
        }
        catch (Exception ex)
        {
            FireError(ex.Message);
            return false;
        }
    }

    public void Disconnect()
    {
        _reconnectHandler?.Dispose();
        _reconnectHandler = null;

        if (_session != null)
        {
            _session.KeepAlive -= Session_KeepAlive;
            try { _session.Close(); } catch { /* best-effort */ }
            _session.Dispose();
            _session = null;
        }

        StatusChanged?.Invoke(LocalizationManager.Instance.Get("OpcUa_StatusDisconnected"));
    }

    // ── Keep-alive / reconnect ────────────────────────────────────────────────

    private void Session_KeepAlive(ISession session, KeepAliveEventArgs e)
    {
        if (!ServiceResult.IsBad(e.Status)) return;
        if (_reconnectHandler != null)       return;

        StatusChanged?.Invoke(LocalizationManager.Instance.Get("OpcUa_StatusReconnecting"));
        _reconnectHandler = new SessionReconnectHandler(true);
        _reconnectHandler.BeginReconnect(_session!, 5_000, Client_ReconnectComplete);
    }

    private void Client_ReconnectComplete(object? sender, EventArgs e)
    {
        if (_reconnectHandler?.Session == null) return;
        if (!ReferenceEquals(_session, _reconnectHandler.Session))
            _session = (Session)_reconnectHandler.Session;
        _reconnectHandler.Dispose();
        _reconnectHandler = null;
        StatusChanged?.Invoke(
            LocalizationManager.Instance.Format("OpcUa_StatusConnected", EndpointUrl));
    }

    private void FireError(string msg)
    {
        ErrorCount++;
        if ((DateTime.UtcNow - _lastErrorTime).TotalSeconds < 5) return;
        _lastErrorTime = DateTime.UtcNow;
        ErrorOccurred?.Invoke(msg);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _reconnectHandler?.Dispose();
        if (_session != null)
        {
            _session.KeepAlive -= Session_KeepAlive;
            try { _session.Close(); } catch { }
            _session.Dispose();
        }
    }
}
