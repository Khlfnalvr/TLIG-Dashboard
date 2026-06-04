using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Services;
using Windows.UI;

namespace TLIGDashboard.Views;

/// <summary>
/// Server-only settings page: share-server broadcast controls, Cloudflare Tunnel
/// (public access without port forwarding), OPC UA connection, and AI settings.
/// Drives <see cref="ShareServer"/> and <see cref="CloudflareTunnelService"/>
/// singletons; subscriptions are added on Loaded and removed on Unloaded.
/// </summary>
public sealed partial class BroadcastSettingsPage : Page
{
    private LocalizationManager Lang => App.Lang;

    private string?  _cachedPublicIp;
    private DateTime _publicIpFetchedAt = DateTime.MinValue;

    public BroadcastSettingsPage()
    {
        InitializeComponent();
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = AppSettingsService.Load();
        PortBox.Text               = s.SharePort.ToString();
        ShareCameraCheck.IsChecked = s.ShareCamera;
        ShareHmiCheck.IsChecked    = s.ShareHmi;

        TunnelCustomDomainCheck.IsChecked = s.TunnelUseCustomDomain;
        TunnelDomainBox.Text              = s.TunnelCustomDomain;
        TunnelCustomDomainPanel.Visibility =
            s.TunnelUseCustomDomain ? Visibility.Visible : Visibility.Collapsed;

        ShareServer.Instance.StateChanged             += OnServerStateChanged;
        CloudflareTunnelService.Instance.StateChanged += OnTunnelStateChanged;
        Lang.PropertyChanged                          += OnLangChanged;
        if (App.ViewModel?.OpcUa is { } opc)
            opc.StatusChanged += OnOpcStatusChanged;

        RefreshServerStatus();
        RefreshTunnelUI();
        InitOpcSection(s);
        InitAiSection(s);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ShareServer.Instance.StateChanged             -= OnServerStateChanged;
        CloudflareTunnelService.Instance.StateChanged -= OnTunnelStateChanged;
        Lang.PropertyChanged                          -= OnLangChanged;
        if (App.ViewModel?.OpcUa is { } opc)
            opc.StatusChanged -= OnOpcStatusChanged;
    }

    private void OnServerStateChanged() => DispatcherQueue.TryEnqueue(RefreshServerStatus);
    private void OnTunnelStateChanged() => DispatcherQueue.TryEnqueue(RefreshTunnelUI);
    private void OnOpcStatusChanged(string _) => DispatcherQueue.TryEnqueue(SyncOpcConnectButton);
    private void OnLangChanged(object? s, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshServerStatus();
            RefreshTunnelUI();
            SyncOpcConnectButton();
        });

    private int CurrentPort() => ShareServer.Instance.IsRunning
        ? ShareServer.Instance.Port
        : (int.TryParse(PortBox?.Text?.Trim(), out var p) && p is > 0 and <= 65535 ? p : 8088);

    // ── Server start/stop ────────────────────────────────────────────────────

    private void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ShareServer.Instance.IsRunning)
        {
            // Stop the tunnel first so it doesn't linger pointing at a dead port.
            if (CloudflareTunnelService.Instance.IsRunning)
                CloudflareTunnelService.Instance.Stop();
            ShareServer.Instance.Stop();
            RefreshServerStatus();
            return;
        }

        int  port     = CurrentPort();
        bool shareCam = ShareCameraCheck.IsChecked == true;
        bool shareHmi = ShareHmiCheck.IsChecked == true;

        ShareServer.Instance.Start(port, shareCam, shareHmi);

        var s = AppSettingsService.Load();
        s.SharePort   = port;
        s.ShareCamera = shareCam;
        s.ShareHmi    = shareHmi;
        AppSettingsService.Save(s);

        RefreshServerStatus();
    }

    private void RefreshServerStatus()
    {
        bool running = ShareServer.Instance.IsRunning;
        StartBtn.Content = running ? Lang.Share_Stop : Lang.Share_Start;
        ServerStatusText.Text = running
            ? $"{Lang.Format(nameof(Lang.Share_Running), ShareServer.Instance.Port)} · " +
              Lang.Format(nameof(Lang.Share_Clients), ShareServer.Instance.ClientCount)
            : Lang.Share_Stopped;
        PortBox.IsEnabled = !running;

        RefreshLocalAddress();
        RefreshTunnelUI();
    }

    // ── Addresses (LAN + public IP) ──────────────────────────────────────────

    private void RefreshLocalAddress()
    {
        var ips         = LocalIPv4Addresses();
        int displayPort = CurrentPort();

        LocalAddrText.Text = ips.Length switch
        {
            0 => Lang.Format(nameof(Lang.Share_LocalAddress), $"127.0.0.1:{displayPort}"),
            1 => Lang.Format(nameof(Lang.Share_LocalAddress), $"{ips[0]}:{displayPort}"),
            _ => Lang.Format(nameof(Lang.Share_LocalAddress),
                     string.Join("  /  ", ips.Select(ip => $"{ip}:{displayPort}")))
        };

        PublicAddrText.Visibility = Visibility.Visible;
        PublicAddrText.Text       = Lang.Share_PublicFetching;
        _ = FetchAndShowPublicIpAsync(displayPort);
    }

    private static string[] LocalIPv4Addresses()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(nic =>
                    nic.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                    nic.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Select(ua => ua.Address.ToString())
                .ToArray();
        }
        catch { }
        return ["127.0.0.1"];
    }

    private async Task FetchAndShowPublicIpAsync(int port)
    {
        if (_cachedPublicIp is not null &&
            (DateTime.UtcNow - _publicIpFetchedAt).TotalMinutes < 5)
        {
            DispatcherQueue.TryEnqueue(() =>
                PublicAddrText.Text =
                    Lang.Format(nameof(Lang.Share_PublicAddress), $"{_cachedPublicIp}:{port}"));
            return;
        }

        string? ip = null;
        string[] endpoints = ["https://api.ipify.org", "https://checkip.amazonaws.com"];

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        foreach (var url in endpoints)
        {
            try
            {
                ip = (await http.GetStringAsync(url)).Trim();
                if (System.Net.IPAddress.TryParse(ip, out _)) break;
                ip = null;
            }
            catch { ip = null; }
        }

        _cachedPublicIp    = ip;
        _publicIpFetchedAt = DateTime.UtcNow;

        DispatcherQueue.TryEnqueue(() =>
        {
            PublicAddrText.Text = ip is null
                ? Lang.Share_PublicFailed
                : Lang.Format(nameof(Lang.Share_PublicAddress), $"{ip}:{port}");
        });
    }

    // ── Cloudflare Tunnel ────────────────────────────────────────────────────

    private void RefreshTunnelUI()
    {
        var  svc     = CloudflareTunnelService.Instance;
        bool running = ShareServer.Instance.IsRunning;

        TunnelToggleBtn.Content   = svc.State == TunnelState.Running ? Lang.Tunnel_Stop : Lang.Tunnel_Start;
        TunnelToggleBtn.IsEnabled = svc.State != TunnelState.Downloading &&
                                    svc.State != TunnelState.Starting    &&
                                    (running || svc.State == TunnelState.Running);

        bool needsDownload = !svc.IsInstalled &&
                             svc.State is TunnelState.Stopped or TunnelState.Error;
        TunnelDownloadPanel.Visibility = needsDownload ? Visibility.Visible : Visibility.Collapsed;

        if (svc.State == TunnelState.Downloading)
        {
            TunnelDownloadPanel.Visibility = Visibility.Visible;
            TunnelDownloadBar.Visibility   = Visibility.Visible;
            TunnelDownloadBar.Value        = svc.DownloadProgress;
            TunnelDownloadBtn.IsEnabled    = false;
        }
        else
        {
            TunnelDownloadBar.Visibility = Visibility.Collapsed;
            TunnelDownloadBtn.IsEnabled  = true;
        }

        TunnelStatusText.Text = svc.State switch
        {
            TunnelState.Starting    => Lang.Tunnel_Starting,
            TunnelState.Running     => Lang.Tunnel_Running,
            TunnelState.Downloading => Lang.Format(nameof(Lang.Tunnel_Downloading),
                                           (int)(svc.DownloadProgress * 100)),
            TunnelState.Error       => Lang.Format(nameof(Lang.Tunnel_Error), svc.LastError),
            _                       => Lang.Tunnel_Stopped,
        };

        bool hasUrl = svc.State == TunnelState.Running && !string.IsNullOrEmpty(svc.TunnelUrl);
        TunnelUrlRow.Visibility        = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        TunnelShareHintText.Visibility = hasUrl ? Visibility.Visible : Visibility.Collapsed;
        if (hasUrl)
        {
            TunnelUrlText.Text       = svc.TunnelUrl;
            TunnelUrlTooltip.Content = svc.TunnelUrl;
        }

        RefreshTunnelLoginUI();
    }

    // ── Cloudflare Tunnel: custom-domain (named tunnel) sign-in ───────────────
    private void RefreshTunnelLoginUI()
    {
        var svc = CloudflareTunnelService.Instance;

        TunnelCustomDomainPanel.Visibility =
            TunnelCustomDomainCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (svc.IsLoggingIn)
        {
            TunnelLoginBtn.IsEnabled = false;
            TunnelLoginStatus.Text   = Lang.Tunnel_LoggingIn;
        }
        else
        {
            TunnelLoginBtn.IsEnabled = true;
            TunnelLoginBtn.Content   = svc.IsLoggedIn ? Lang.Tunnel_Relogin : Lang.Tunnel_Login;
            TunnelLoginStatus.Text   = svc.IsLoggedIn ? Lang.Tunnel_LoggedIn : Lang.Tunnel_NotLoggedIn;
        }

        bool showUrl = svc.IsLoggingIn && !string.IsNullOrEmpty(svc.LoginUrl);
        TunnelLoginUrlText.Visibility = showUrl ? Visibility.Visible : Visibility.Collapsed;
        if (showUrl)
            TunnelLoginUrlText.Text = Lang.Format(nameof(Lang.Tunnel_LoginUrlHint), svc.LoginUrl);
    }

    private void TunnelCustomDomainCheck_Click(object sender, RoutedEventArgs e)
    {
        SaveTunnelSettings();
        RefreshTunnelLoginUI();
    }

    private async void TunnelLogin_Click(object sender, RoutedEventArgs e)
    {
        SaveTunnelSettings();
        await CloudflareTunnelService.Instance.LoginAsync();
        RefreshTunnelUI();
    }

    private void SaveTunnelSettings()
    {
        var s = AppSettingsService.Load();
        s.TunnelUseCustomDomain = TunnelCustomDomainCheck.IsChecked == true;
        s.TunnelCustomDomain    = TunnelDomainBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(s.TunnelName))
            s.TunnelName = CloudflareTunnelService.DefaultTunnelName;
        AppSettingsService.Save(s);
    }

    private void TunnelToggle_Click(object sender, RoutedEventArgs e)
    {
        var svc = CloudflareTunnelService.Instance;
        if (svc.State == TunnelState.Running) { svc.Stop(); return; }

        // A named tunnel needs a domain + a completed sign-in before it can start.
        if (TunnelCustomDomainCheck.IsChecked == true)
        {
            if (string.IsNullOrWhiteSpace(TunnelDomainBox.Text))
            {
                TunnelStatusText.Text = Lang.Tunnel_DomainRequired;
                return;
            }
            if (!svc.IsLoggedIn)
            {
                TunnelStatusText.Text = Lang.Tunnel_NotLoggedIn;
                return;
            }
        }

        SaveTunnelSettings();
        svc.Start(CurrentPort());
    }

    private async void TunnelDownload_Click(object sender, RoutedEventArgs e)
    {
        TunnelDownloadBtn.IsEnabled = false;
        await CloudflareTunnelService.Instance.DownloadAsync();
    }

    private async void TunnelCopy_Click(object sender, RoutedEventArgs e)
    {
        var url = CloudflareTunnelService.Instance.TunnelUrl;
        if (string.IsNullOrEmpty(url)) return;

        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
        pkg.SetText(url);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);

        TunnelCopyBtn.Content = Lang.Tunnel_Copied;
        await Task.Delay(1500);
        TunnelCopyBtn.Content = Lang.Tunnel_CopyUrl;
    }

    // ── OPC UA connection (shared service; flyout offers the same as quick config) ──

    private void InitOpcSection(AppSettings s)
    {
        OpcEndpointBox.Text = s.OpcUaEndpointUrl;

        OpcSecNone.Content    = Lang.Ui_OpcUaSecNone;
        OpcSecSign.Content    = Lang.Ui_OpcUaSecSign;
        OpcSecSignEnc.Content = Lang.Ui_OpcUaSecSignEnc;
        OpcSecurityCombo.SelectedIndex = s.OpcUaSecurityMode switch
        {
            "Sign"           => 1,
            "SignAndEncrypt" => 2,
            _                => 0
        };

        OpcAuthAnon.Content = Lang.Ui_OpcUaAnonymous;
        OpcAuthUser.Content = Lang.Ui_OpcUaUsernameAuth;
        OpcAuthCombo.SelectedIndex = s.OpcUaUseAnonymous ? 0 : 1;
        OpcUsernameBox.Text        = s.OpcUaUsername;
        OpcCredPanel.Visibility    = s.OpcUaUseAnonymous ? Visibility.Collapsed : Visibility.Visible;

        SyncOpcConnectButton();
    }

    private void OpcAuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpcCredPanel is null) return;
        OpcCredPanel.Visibility = OpcAuthCombo.SelectedIndex == 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncOpcConnectButton()
    {
        var  opc       = App.ViewModel?.OpcUa;
        bool connected = opc?.IsConnected == true;

        OpcConnectBtn.Content         = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        OpcEndpointBox.IsEnabled      = !connected;
        OpcSecurityCombo.IsEnabled    = !connected;
        OpcAuthCombo.IsEnabled        = !connected;
        OpcCredPanel.IsHitTestVisible = !connected;

        OpcStatusText.Text = connected
            ? Lang.Format("OpcUa_StatusConnected", opc!.EndpointUrl)
            : Lang.Get("Ctrl_NotConnected");
    }

    private void OpcCertFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLIGDashboard", "pki", "own");
        Directory.CreateDirectory(certPath);
        Process.Start(new ProcessStartInfo("explorer.exe", certPath) { UseShellExecute = true });
    }

    private async void OpcConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        var opc = App.ViewModel?.OpcUa;
        if (opc is null) return;

        if (opc.IsConnected)
        {
            opc.Disconnect();
            SyncOpcConnectButton();
            return;
        }

        var endpointUrl = OpcEndpointBox.Text.Trim();
        if (string.IsNullOrEmpty(endpointUrl)) return;

        var secMode = (OpcSecurityCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Sign"           => OpcUaSecurityMode.Sign,
            "SignAndEncrypt" => OpcUaSecurityMode.SignAndEncrypt,
            _                => OpcUaSecurityMode.None
        };
        var authMode = OpcAuthCombo.SelectedIndex == 1
            ? OpcUaAuthMode.UsernamePassword
            : OpcUaAuthMode.Anonymous;

        OpcConnectBtn.IsEnabled = false;
        OpcStatusText.Text      = Lang.Get("OpcUa_StatusConnecting");

        bool ok = await opc.ConnectAsync(
            endpointUrl, authMode, OpcUsernameBox.Text, OpcPasswordBox.Password, secMode);

        if (ok)
        {
            var s = AppSettingsService.Load();
            s.OpcUaEndpointUrl  = endpointUrl;
            s.OpcUaSecurityMode = secMode.ToString();
            s.OpcUaUseAnonymous = authMode == OpcUaAuthMode.Anonymous;
            s.OpcUaUsername     = OpcUsernameBox.Text;
            AppSettingsService.Save(s);
        }

        OpcConnectBtn.IsEnabled = true;
        SyncOpcConnectButton();
    }

    // ── AI API settings (server provider key; same as the flyout's quick config) ─

    private void InitAiSection(AppSettings s)
    {
        AiApiUrlBox.Text     = s.AiApiUrl;
        AiApiKeyBox.Password = s.AiApiKey;
        AiModelBox.Text      = s.AiModel;
        AiSysPromptBox.Text  = s.AiSystemPrompt;
        RefreshAiStatus(s.AiApiKey);
    }

    private void RefreshAiStatus(string key)
    {
        bool configured = !string.IsNullOrWhiteSpace(key);
        App.Status.AiConnected = configured;
        AiStatusText.Text = configured
            ? Lang.Format("Ai_ModelLabel", AppSettingsService.Load().AiModel)
            : Lang.Get("Ai_ErrorNoKey");
        AiStatusText.Foreground = new SolidColorBrush(configured
            ? Color.FromArgb(0xFF, 0x25, 0xC6, 0x85)
            : Color.FromArgb(0xFF, 0xCC, 0x6E, 0x00));
    }

    private void AiSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = AppSettingsService.Load();
        s.AiApiUrl       = AiApiUrlBox.Text.Trim();
        s.AiApiKey       = AiApiKeyBox.Password.Trim();
        s.AiModel        = AiModelBox.Text.Trim();
        s.AiSystemPrompt = AiSysPromptBox.Text.Trim();
        AppSettingsService.Save(s);

        RefreshAiStatus(s.AiApiKey);

        // Notify a loaded AIPage so it picks up the new settings immediately.
        if (App.CurrentWindow?.GetContentFrame()?.Content is AIPage aiPage)
            aiPage.ReloadSettings();
    }
}
