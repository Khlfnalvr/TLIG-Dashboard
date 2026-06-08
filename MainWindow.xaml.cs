using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using TLIGDashboard.Services;
using TLIGDashboard.ViewModels;
using TLIGDashboard.Views;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinRT.Interop;

namespace TLIGDashboard;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private Services.LocalizationManager Lang => App.Lang;

    private static string AppProductName =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
        ?? "TLIG Dashboard";

    private static string AppVersion =>
        typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(App).Assembly.GetName().Version?.ToString(3)
        ?? "1.6.0";

    private string AboutProductText => $"{Lang.Ui_About_Product}: {AppProductName}";
    private string AboutVersionText => $"{Lang.Ui_About_Version}: {AppVersion}";
    private string AboutLicenseText => $"{Lang.Ui_About_License}: ICO Laboratory proprietary license";
    private string AboutCopyrightText => $"{Lang.Ui_About_Copyright}: (C) 2026 ICO Laboratory";


    private readonly Dictionary<string, Type> _pages = new()
    {
        { "Dashboard", typeof(DashboardPage) },
        { "Parameter", typeof(ParameterPage) },
        { "LiveView",  typeof(LiveViewPage) },
        { "LearningAnalytic", typeof(LearningAnalyticPage) },
        { "AI",        typeof(AIPage) },
        { "Broadcast", typeof(BroadcastSettingsPage) },
        { "UserManagement", typeof(UserManagementPage) }
    };

    private string _loggedInUser = "";
    private string _loggedInRole = "";

    private bool _initializing;
    private bool _earlyAccess;
    private IntPtr _hwnd;
    private IntPtr _oldWndProc;
    private WndProcDelegate? _newWndProc;

    private const double ZoomMin  = 0.5;
    private const double ZoomMax  = 2.5;
    private const double ZoomStep = 0.1;
    private double _zoomLevel = 1.0;

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        Title = AppProductName;

        ApplyMicaBackdrop();
        InitializeTitleBar();
        SetAppIcon();
        InstallMinimumWindowSizeHook();
        InitializeTheme();
        InitializeZoom();

        // Shrink the drag region whenever window size or toggle size changes.
        SizeChanged += (_, _) => UpdateTitleBarLayout();
        ThemeToggleArea.SizeChanged += (_, _) => UpdateTitleBarLayout();

        // The theme button's tooltip is set in code, so it doesn't refresh
        // through {x:Bind ...}. Refresh it whenever the language switches.
        // Same for the language button's tooltip and checked-state.
        Lang.PropertyChanged += (_, _) =>
        {
            ViewModel.RefreshLocalizedText();
            RefreshThemeButtonTooltip();
            UpdateLangMenuState();
            UpdateAccountFlyoutText();
            if (TourOverlay.Visibility == Visibility.Visible)
                ShowTourOverlay();
            Bindings.Update();
        };
        UpdateLangMenuState();

        InitOpcUaFlyout();
        InitLoginOverlay();

        _earlyAccess = AppSettingsService.Load().EarlyAccess;
        MenuEarlyAccess.IsChecked = _earlyAccess;

        _ = StartupUpdateCheckAsync();
    }

    /// <summary>Exposes ContentFrame so pages can inspect what is currently loaded.</summary>
    public Frame? GetContentFrame() => ContentFrame;

    public void MaximizeOnLaunch()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.Maximize();
    }

    private void InstallMinimumWindowSizeHook()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        if (_hwnd == IntPtr.Zero) return;

        _newWndProc = WindowProc;
        _oldWndProc = SetWindowLongPtr(
            _hwnd,
            GWLP_WNDPROC,
            Marshal.GetFunctionPointerForDelegate(_newWndProc));

        Closed += (_, _) =>
        {
            if (_hwnd != IntPtr.Zero && _oldWndProc != IntPtr.Zero)
                SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _oldWndProc);
        };
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_NCRBUTTONDOWN && wParam == new IntPtr(HTCAPTION))
            return IntPtr.Zero;

        if (msg == WM_NCRBUTTONUP && wParam == new IntPtr(HTCAPTION))
        {
            ShowTitleBarCustomizeMenuFromCursor();
            return IntPtr.Zero;
        }

        if (msg == WM_CONTEXTMENU && IsCursorInTitleBar())
        {
            ShowTitleBarCustomizeMenuFromCursor();
            return IntPtr.Zero;
        }

        var result = CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);

        if (msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            int minWidth = GetHalfScreenWidth(hWnd);
            if (minWidth > 0 && info.ptMinTrackSize.x < minWidth)
            {
                info.ptMinTrackSize.x = minWidth;
                Marshal.StructureToPtr(info, lParam, false);
            }
        }

        return result;
    }

    private bool IsCursorInTitleBar()
    {
        if (!GetCursorPos(out var screenPoint)) return false;
        if (!ScreenToClient(_hwnd, ref screenPoint)) return false;

        double scale = Content?.XamlRoot?.RasterizationScale ?? 1;
        return screenPoint.y / scale <= AppTitleBar.Height;
    }

    private void ShowTitleBarCustomizeMenuFromCursor()
    {
        if (!GetCursorPos(out var screenPoint)) return;
        if (!ScreenToClient(_hwnd, ref screenPoint)) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            double scale = Content?.XamlRoot?.RasterizationScale ?? 1;
            TitleBarCustomizeMenu.ShowAt(
                NavView,
                new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                {
                    Position = new Point(screenPoint.x / scale, screenPoint.y / scale)
                });
        });
    }

    private static int GetHalfScreenWidth(IntPtr hWnd)
    {
        IntPtr monitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return 0;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return 0;

        return Math.Max(0, (info.rcWork.right - info.rcWork.left) / 2);
    }

    // ── Icon ─────────────────────────────────────────────────────────────
    private void SetAppIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
        if (File.Exists(iconPath))
            AppWindow.SetIcon(iconPath);
    }

    // ── Mica ─────────────────────────────────────────────────────────────
    private void ApplyMicaBackdrop()
    {
        if (MicaController.IsSupported())
            SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
    }

    // ── Title bar ─────────────────────────────────────────────────────────
    private void InitializeTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
    }

    // Position toggle next to caption buttons and shrink AppTitleBar so the
    // toggle area is outside the drag region (no passthrough tricks needed).
    private void UpdateTitleBarLayout()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var root = Content?.XamlRoot;
        if (root == null) return;

        double scale      = root.RasterizationScale;
        double rightInset = AppWindow.TitleBar.RightInset / scale;
        if (rightInset <= 0) rightInset = 138; // Win11 three-button fallback

        // Shift toggle left of caption buttons
        ThemeToggleArea.Margin = new Thickness(0, 0, rightInset, 0);

        // Exclude toggle area from the drag region
        double toggleWidth = ThemeToggleArea.ActualWidth;
        if (toggleWidth > 0)
            AppTitleBar.Margin = new Thickness(0, 0, rightInset + toggleWidth, 0);
    }

    private void UpdateTitleBarColors(ElementTheme theme)
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var bar = AppWindow.TitleBar;
        bool dark = theme == ElementTheme.Dark;

        bar.ButtonBackgroundColor         = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor         = dark ? Colors.White : Colors.Black;
        bar.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x66, 0x00, 0x00, 0x00);
        bar.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x1A, 0x00, 0x00, 0x00);
        bar.ButtonHoverForegroundColor   = dark ? Colors.White : Colors.Black;
        bar.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x33, 0x00, 0x00, 0x00);
        bar.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
    }

    // ── Theme ─────────────────────────────────────────────────────────────
    private void InitializeTheme()
    {
        // Follow system dark/light preference on first launch.
        var uiSettings = new UISettings();
        var bg   = uiSettings.GetColorValue(UIColorType.Background);
        bool dark = bg.R < 128;

        _initializing = true;
        ApplyTheme(dark ? ElementTheme.Dark : ElementTheme.Light);
        _initializing = false;
    }

    private void ApplyTheme(ElementTheme theme)
    {
        if (Content is FrameworkElement fe)
            fe.RequestedTheme = theme;
        UpdateTitleBarColors(theme);
        UpdateThemeButton(theme);
        UpdateLogo(theme);
    }

    private void UpdateLogo(ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        string fileName = dark ? "logowhite.png" : "logoblack.png";
        string path = Path.Combine(AppContext.BaseDirectory, fileName);
        if (!File.Exists(path)) return;
        var source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(path));
        LogoImage.Source = source;
        if (TourLogoImage is not null)
            TourLogoImage.Source = source;
        if (LoginLogoImage is not null)
            LoginLogoImage.Source = source;
    }

    // Caption-style theme button: shows the icon for the mode the user would
    // switch INTO (sun when dark, moon when light) — matches the Visual
    // Studio Installer pattern.
    private void UpdateThemeButton(ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Dark;
        // Segoe Fluent Icons:
        //   E706 = Brightness (sun) — shown when dark mode is active
        //   E708 = QuietHours (moon) — shown when light mode is active
        ThemeIcon.Glyph = dark ? "" : "";
        RefreshThemeButtonTooltip();
    }

    private void RefreshThemeButtonTooltip()
    {
        if (ThemeBtn is null) return;
        bool dark = Content is FrameworkElement fe && fe.RequestedTheme == ElementTheme.Dark;
        ToolTipService.SetToolTip(ThemeBtn, dark ? Lang.Ui_SwitchToLight : Lang.Ui_SwitchToDark);
    }

    private void ThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        var current = Content is FrameworkElement fe ? fe.RequestedTheme : ElementTheme.Default;
        // Default counts as light for the toggle direction
        bool currentlyDark = current == ElementTheme.Dark;
        ApplyTheme(currentlyDark ? ElementTheme.Light : ElementTheme.Dark);
    }

    // ── Zoom ──────────────────────────────────────────────────────────────
    //
    // Font-only zoom: scaling RenderTransform on RootGrid distorted the
    // layout and clipped content at the window edges. Instead we walk the
    // visual tree of the page content + playback / status bars and multiply
    // each FontSize (and FontIcon glyph size) by the zoom level. Layout
    // reflows naturally; nothing gets cut off.
    //
    // The title-bar nav strip is intentionally excluded — its 32-px height
    // is fixed and larger fonts would clip the nav labels.
    private readonly Dictionary<DependencyObject, double> _baseFontSizes = new();

    private void InitializeZoom()
    {
        var s = AppSettingsService.Load();
        _zoomLevel = Math.Clamp(s.ZoomLevel, ZoomMin, ZoomMax);

        // Re-apply zoom after each page navigation — the new page's visual
        // tree only exists after Loaded fires.
        ContentFrame.Navigated += (_, _) =>
        {
            if (ContentFrame.Content is FrameworkElement page)
                page.Loaded += OnPageLoadedApplyZoom;
        };

        // Apply once the root visual tree (playback bar + status bar) is built.
        RootGrid.Loaded += (_, _) => ApplyZoom();

        // Main-keyboard +/- (top row) — handled via KeyDown to avoid the
        // automatic accelerator tooltip that KeyboardAccelerator shows randomly.
        if (Content is UIElement root)
            root.KeyDown += OnRootKeyDown;
    }

    private void OnRootKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        // OEM_PLUS (187 = '=+' key) and OEM_MINUS (189 = '-_' key) on the top row
        if (e.Key == (Windows.System.VirtualKey)187)
        {
            ZoomIn_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == (Windows.System.VirtualKey)189)
        {
            ZoomOut_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void OnPageLoadedApplyZoom(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            fe.Loaded -= OnPageLoadedApplyZoom;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (ContentFrame?.Content is FrameworkElement page)
            ScaleFontsInTree(page, _zoomLevel);
        if (StatusInfoBar is not null)
            ScaleFontsInTree(StatusInfoBar, _zoomLevel);
    }

    private void ScaleFontsInTree(DependencyObject? node, double scale)
    {
        if (node is null) return;

        // TextBlock is not a Control — it inherits FrameworkElement directly,
        // so it needs its own branch. Same story for FontIcon.
        if (node is TextBlock tb)
            ScaleFontSize(tb, () => tb.FontSize, v => tb.FontSize = v, scale);
        else if (node is FontIcon fi)
            ScaleFontSize(fi, () => fi.FontSize, v => fi.FontSize = v, scale);
        else if (node is Control ctrl)
            ScaleFontSize(ctrl, () => ctrl.FontSize, v => ctrl.FontSize = v, scale);

        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
            ScaleFontsInTree(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(node, i), scale);
    }

    private void ScaleFontSize(
        DependencyObject element,
        Func<double> getter,
        Action<double> setter,
        double scale)
    {
        if (!_baseFontSizes.TryGetValue(element, out var baseSize))
        {
            baseSize = getter();
            _baseFontSizes[element] = baseSize;
        }
        setter(baseSize * scale);
    }

    private void SetZoom(double zoom)
    {
        double clamped = Math.Round(Math.Clamp(zoom, ZoomMin, ZoomMax), 2);
        if (Math.Abs(clamped - _zoomLevel) < 0.001) return;

        _zoomLevel = clamped;
        ApplyZoom();

        var s = AppSettingsService.Load();
        s.ZoomLevel = _zoomLevel;
        s.Language  = Lang.CurrentLanguage;
        AppSettingsService.Save(s);
    }

    private void ZoomActualSize_Click(object sender, RoutedEventArgs e) => SetZoom(1.0);
    private void ZoomIn_Click(object sender, RoutedEventArgs e)         => SetZoom(_zoomLevel + ZoomStep);
    private void ZoomOut_Click(object sender, RoutedEventArgs e)        => SetZoom(_zoomLevel - ZoomStep);

    // ── Language picker ───────────────────────────────────────────────────
    private void UpdateLangMenuState()
    {
        if (LangBtn is null) return;
        string cur = Lang.CurrentLanguage;
        LangItemId.IsChecked = (cur == "id");
        LangItemEn.IsChecked = (cur == "en");
        ToolTipService.SetToolTip(LangBtn, Lang.Ui_ChangeLanguage);
    }

    private void LangItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is RadioMenuFlyoutItem item && item.Tag is string tag)
            Lang.CurrentLanguage = tag;
    }

    // ── Navigation ────────────────────────────────────────────────────────
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Apply persisted nav-item visibility, then select the first visible.
        ApplyNavVisibilityFromSettings();
        ApplyRoleNavVisibility();   // User Management stays hidden until an admin signs in
        NavView.SelectedItem = FirstVisibleNavItem() ?? NavView.MenuItems[0];
        UpdateTitleBarLayout();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
            if (_pages.TryGetValue(tag, out var pageType))
                ContentFrame.Navigate(pageType);
    }

    // ── Logo customize menu ──────────────────────────────────────────────
    private void TitleBar_RightTapped(
        object sender,
        Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        var position = e.GetPosition(NavView);
        if (position.Y > AppTitleBar.Height) return;

        e.Handled = true;
        TitleBarCustomizeMenu.ShowAt(
            NavView,
            new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
            {
                Position = position
            });
    }

    private (NavigationViewItem nav, ToggleMenuFlyoutItem toggle)[] NavToggles() =>
    [
        (NavDashboard, ViewNavDashboard),
        (NavParameter, ViewNavParameter),
        (NavLiveView,  ViewNavLiveView),
        (NavLearningAnalytic, ViewNavLearningAnalytic),
        (NavAI,        ViewNavAI),
    ];

    private void ApplyNavVisibilityFromSettings()
    {
        var s = AppSettingsService.Load();
        ApplyNavVisibility(NavDashboard, ViewNavDashboard, s.ShowNav_Dashboard);
        ApplyNavVisibility(NavParameter, ViewNavParameter, s.ShowNav_Parameter);
        ApplyNavVisibility(NavLiveView,  ViewNavLiveView,  s.ShowNav_LiveView);
        ApplyNavVisibility(NavLearningAnalytic, ViewNavLearningAnalytic, s.ShowNav_LearningAnalytic);
        ApplyNavVisibility(NavAI,        ViewNavAI,        s.ShowNav_AI);
    }

    private static void ApplyNavVisibility(NavigationViewItem item, ToggleMenuFlyoutItem toggle, bool show)
    {
        item.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        toggle.IsChecked = show;
    }

    private NavigationViewItem? FirstVisibleNavItem()
    {
        foreach (var (nav, _) in NavToggles())
            if (nav.Visibility == Visibility.Visible) return nav;
        return null;
    }

    private void ViewNavToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleMenuFlyoutItem toggle || toggle.Tag is not string tag)
            return;

        var match = NavToggles().FirstOrDefault(t => t.nav.Tag is string s && s == tag);
        if (match.nav is null) return;

        // Don't allow hiding the last visible item — there must always be at
        // least one page to land on.
        if (!toggle.IsChecked &&
            NavToggles().Count(t => t.nav.Visibility == Visibility.Visible) <= 1)
        {
            toggle.IsChecked = true;
            return;
        }

        match.nav.Visibility = toggle.IsChecked ? Visibility.Visible : Visibility.Collapsed;

        // If the hidden item was selected, jump to the first visible one.
        if (!toggle.IsChecked && ReferenceEquals(NavView.SelectedItem, match.nav))
            NavView.SelectedItem = FirstVisibleNavItem();

        SaveNavVisibility();
    }

    private void SaveNavVisibility()
    {
        var s = AppSettingsService.Load();
        s.ShowNav_Dashboard = ViewNavDashboard.IsChecked;
        s.ShowNav_Parameter = ViewNavParameter.IsChecked;
        s.ShowNav_LiveView  = ViewNavLiveView.IsChecked;
        s.ShowNav_LearningAnalytic = ViewNavLearningAnalytic.IsChecked;
        s.ShowNav_AI        = ViewNavAI.IsChecked;
        s.Language          = Lang.CurrentLanguage;
        AppSettingsService.Save(s);
    }

    public void NavigateToPage(string tag)
    {
        foreach (var (nav, _) in NavToggles())
        {
            if (nav.Tag is string navTag && navTag == tag)
            {
                // If this nav item is already selected (e.g. returning from the task
                // detail page, which left the selection on Learning Analytic), setting
                // SelectedItem again won't raise SelectionChanged — navigate directly.
                if (ReferenceEquals(NavView.SelectedItem, nav))
                {
                    if (_pages.TryGetValue(tag, out var pt)) ContentFrame.Navigate(pt);
                }
                else
                {
                    NavView.SelectedItem = nav;
                }
                return;
            }
        }
        if (_pages.TryGetValue(tag, out var pageType))
            ContentFrame.Navigate(pageType);
    }

    /// <summary>
    /// Opens the task detail page for a given task id. Not a nav item — navigated
    /// directly with the id as parameter; the detail page's Back returns to the
    /// Learning Analytic list.
    /// </summary>
    public void NavigateToTaskDetail(string taskId)
        => ContentFrame.Navigate(typeof(Views.TaskDetailPage), taskId);

    // ── Login ─────────────────────────────────────────────────────────────
    // The server flavor authenticates against the local user database; the client
    // flavor authenticates against a remote server (address entered in the popup)
    // and, on success, opens the live stream + points the AI proxy at that server.

    private void InitLoginOverlay()
    {
        // The server address field only applies to the client flavor.
        LoginServerPanel.Visibility = Services.BuildInfo.IsClient
            ? Visibility.Visible : Visibility.Collapsed;

        // Self-registration is a client→server action; the server flavor creates
        // accounts via the User Management page, so the entry point is client-only.
        LoginToSignupLink.Visibility = Services.BuildInfo.IsClient
            ? Visibility.Visible : Visibility.Collapsed;

        var s = AppSettingsService.Load();
        if (Services.BuildInfo.IsClient)
        {
            LoginServerBox.Text   = s.ServerHost;
            LoginUsernameBox.Text = s.ServerUsername;
        }
        else
        {
            LoginUsernameBox.Text = s.ServerUsername;
        }
    }

    private async void LoginSubmit_Click(object sender, RoutedEventArgs e)
    {
        // The post-signup "account created" note has served its purpose once the
        // user attempts to sign in — clear it so it can't sit next to an error.
        LoginInfoText.Visibility = Visibility.Collapsed;

        string user = LoginUsernameBox.Text.Trim();
        string pass = LoginPasswordBox.Password;

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            SetLoginError(Lang.Login_ErrorEmpty);
            return;
        }

        if (Services.BuildInfo.IsServer)
        {
            // Local console login against the server's own user database.
            var account = UserStore.Instance.Verify(user, pass);
            if (account is null) { LoginFailed(); return; }

            // Only staff (Dosen/Asisten) may operate the Server application. Students
            // can use the client against a server, but never sign in to the server itself.
            if (!UserRoles.IsStaff(account.Role))
            {
                SetLoginError(Lang.Login_ErrorStudentServer);
                LoginPasswordBox.Password = "";
                LoginPasswordBox.Focus(FocusState.Programmatic);
                return;
            }

            // Persist the last-used username so the login box is pre-filled next launch.
            var sv = AppSettingsService.Load();
            sv.ServerUsername = account.Username;
            AppSettingsService.Save(sv);

            OnLoginSucceeded(account.Username, account.DisplayName, account.Role);
            return;
        }

        // ── Client: authenticate against the remote server ──
        string host = LoginServerBox.Text.Trim();
        if (string.IsNullOrEmpty(host))
        {
            SetLoginError(Lang.Login_ErrorNoServer);
            return;
        }

        SetLoginBusy(true);
        var result = await AuthClient.LoginAsync(host, user, pass);
        SetLoginBusy(false);

        if (!result.Success)
        {
            SetLoginError(Lang.Get(result.ErrorKey ?? "Login_ErrorInvalid"));
            LoginPasswordBox.Password = "";
            LoginPasswordBox.Focus(FocusState.Programmatic);
            return;
        }

        // Persist the host + username + issued session token so the rest of the app
        // (WebSocket stream + AI proxy) can use the same credentials. The password
        // itself is never stored.
        var s = AppSettingsService.Load();
        s.ServerHost     = AuthClient.NormalizeHost(host);
        s.ServerUsername = user;
        s.ServerToken    = result.Token;
        AppSettingsService.Save(s);

        OnLoginSucceeded(user, result.DisplayName, result.Role);

        // Open the live stream and point the AI assistant at the server proxy.
        _ = ConnectClientStreamAsync(s.ServerHost, result.Token);
    }

    private void OnLoginSucceeded(string accountName, string displayName, string role)
    {
        _loggedInUser = string.IsNullOrWhiteSpace(displayName) ? accountName : displayName;
        _loggedInRole = role;
        App.Session.SignIn(accountName, displayName, role);
        UpdateAccountFlyoutText();
        ApplyRoleNavVisibility();
        LoginErrorText.Visibility = Visibility.Collapsed;
        LoginPasswordBox.Password = "";
        LoginOverlay.Visibility   = Visibility.Collapsed;
    }

    private void LoginFailed()
    {
        SetLoginError(Lang.Login_ErrorInvalid);
        LoginPasswordBox.Password = "";
        LoginPasswordBox.Focus(FocusState.Programmatic);
    }

    private void SetLoginBusy(bool busy)
    {
        LoginSubmitBtn.IsEnabled = !busy;
        LoginBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) LoginErrorText.Visibility = Visibility.Collapsed;
    }

    private async Task ConnectClientStreamAsync(string host, string token)
    {
        bool ok = await ShareClient.Instance.ConnectAsync(host, token);
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshClientStatus(ok, ok ? null : Lang.Share_Disconnected);
            if (ContentFrame?.Content is Views.AIPage aiPage)
                aiPage.ReloadSettings();
        });
    }

    private void SetLoginError(string message)
    {
        LoginErrorText.Text = message;
        LoginErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Prefills and shows the login overlay (used on logout / switch server).</summary>
    private void ShowLoginOverlay()
    {
        ShowLoginCard();
        if (Services.BuildInfo.IsClient)
        {
            var s = AppSettingsService.Load();
            LoginServerBox.Text   = s.ServerHost;
            LoginUsernameBox.Text = s.ServerUsername;
        }
        LoginPasswordBox.Password = "";
        LoginErrorText.Visibility = Visibility.Collapsed;
        LoginInfoText.Visibility  = Visibility.Collapsed;
        LoginOverlay.Visibility   = Visibility.Visible;

        if (Services.BuildInfo.IsClient && string.IsNullOrWhiteSpace(LoginServerBox.Text))
            LoginServerBox.Focus(FocusState.Programmatic);
        else
            LoginUsernameBox.Focus(FocusState.Programmatic);
    }

    /// <summary>
    /// Server-only nav items: Broadcast settings (any signed-in user) and User
    /// Management (staff — Dosen/Asisten). Both are hidden until the operator signs
    /// in. Note only staff can sign in to the server at all, so on the server a
    /// signed-in user is always staff.
    /// </summary>
    private void ApplyRoleNavVisibility()
    {
        bool signedIn = !string.IsNullOrWhiteSpace(_loggedInUser);
        bool showBroadcast = Services.BuildInfo.IsServer && signedIn;
        bool showUm        = Services.BuildInfo.IsServer && signedIn &&
                             UserRoles.IsStaff(_loggedInRole);

        NavBroadcast.Visibility      = showBroadcast ? Visibility.Visible : Visibility.Collapsed;
        NavUserManagement.Visibility = showUm        ? Visibility.Visible : Visibility.Collapsed;

        if (!showBroadcast && ReferenceEquals(NavView.SelectedItem, NavBroadcast))
            NavView.SelectedItem = FirstVisibleNavItem();
        if (!showUm && ReferenceEquals(NavView.SelectedItem, NavUserManagement))
            NavView.SelectedItem = FirstVisibleNavItem();
    }

    private void LoginServer_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            LoginUsernameBox.Focus(FocusState.Programmatic);
    }

    private void LoginUsername_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            LoginPasswordBox.Focus(FocusState.Programmatic);
    }

    private void LoginPassword_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            LoginSubmit_Click(sender, new RoutedEventArgs());
    }

    private void LoginClose_Click(object sender, RoutedEventArgs e)
    {
        LoginOverlay.Visibility = Visibility.Collapsed;
    }

    // ── Signup (client self-registration, @its.ac.id) ─────────────────────
    // Client-only: the user enters a server address + an @its.ac.id email +
    // password; the server creates a Viewer account they can then sign in with.
    // No email verification — a valid address + password registers immediately.

    private void ShowSignupCard()
    {
        // Carry over whatever server address was typed on the login card.
        SignupServerBox.Text       = LoginServerBox.Text;
        SignupPasswordBox.Password = "";
        SignupConfirmBox.Password  = "";
        SignupErrorText.Visibility = Visibility.Collapsed;
        LoginInfoText.Visibility   = Visibility.Collapsed;
        SetSignupBusy(false);

        LoginDialogCard.Visibility  = Visibility.Collapsed;
        SignupDialogCard.Visibility = Visibility.Visible;
        SignupEmailBox.Focus(FocusState.Programmatic);
    }

    private void ShowLoginCard()
    {
        SignupDialogCard.Visibility = Visibility.Collapsed;
        LoginDialogCard.Visibility  = Visibility.Visible;
    }

    private void LoginToSignup_Click(object sender, RoutedEventArgs e) => ShowSignupCard();

    private void SignupToLogin_Click(object sender, RoutedEventArgs e)
    {
        // Carry the server address back so it isn't lost when switching cards.
        if (!string.IsNullOrWhiteSpace(SignupServerBox.Text))
            LoginServerBox.Text = SignupServerBox.Text;
        ShowLoginCard();
    }

    private async void SignupSubmit_Click(object sender, RoutedEventArgs e)
    {
        string host  = SignupServerBox.Text.Trim();
        string email = SignupEmailBox.Text.Trim();
        string pass  = SignupPasswordBox.Password;
        string conf  = SignupConfirmBox.Password;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(pass))
        {
            SetSignupError(Lang.Signup_ErrEmpty);
            return;
        }
        if (string.IsNullOrEmpty(host))
        {
            SetSignupError(Lang.Login_ErrorNoServer);
            return;
        }

        // Instant client-side feedback; the server re-validates authoritatively.
        var policyError = EmailPolicy.Validate(email);
        if (policyError is not null)
        {
            SetSignupError(Lang.Get(policyError));
            return;
        }
        if (pass != conf)
        {
            SetSignupError(Lang.Signup_ErrPasswordMismatch);
            return;
        }

        SetSignupBusy(true);
        var result = await AuthClient.SignUpAsync(host, email, pass);
        SetSignupBusy(false);

        if (!result.Success)
        {
            SetSignupError(Lang.Get(result.ErrorKey ?? "Signup_ErrUnknown"));
            return;
        }

        // Account created. Persist host + email so the login card prefills, then
        // bounce the user back to sign in (explicit two-step, no auto-login).
        var s = AppSettingsService.Load();
        s.ServerHost     = AuthClient.NormalizeHost(host);
        s.ServerUsername = EmailPolicy.Normalize(email);
        AppSettingsService.Save(s);

        SignupPasswordBox.Password = "";
        SignupConfirmBox.Password  = "";
        SignupErrorText.Visibility = Visibility.Collapsed;

        LoginServerBox.Text       = s.ServerHost;
        LoginUsernameBox.Text     = s.ServerUsername;
        LoginPasswordBox.Password = "";
        LoginErrorText.Visibility = Visibility.Collapsed;
        LoginInfoText.Text        = Lang.Signup_Success;
        LoginInfoText.Visibility  = Visibility.Visible;

        ShowLoginCard();
        LoginPasswordBox.Focus(FocusState.Programmatic);
    }

    private void SetSignupError(string message)
    {
        SignupErrorText.Text = message;
        SignupErrorText.Visibility = Visibility.Visible;
    }

    private void SetSignupBusy(bool busy)
    {
        SignupSubmitBtn.IsEnabled  = !busy;
        SignupBusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) SignupErrorText.Visibility = Visibility.Collapsed;
    }

    private void SignupServer_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            SignupEmailBox.Focus(FocusState.Programmatic);
    }

    private void SignupEmail_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            SignupPasswordBox.Focus(FocusState.Programmatic);
    }

    private void SignupPassword_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            SignupConfirmBox.Focus(FocusState.Programmatic);
    }

    private void SignupConfirm_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            SignupSubmit_Click(sender, new RoutedEventArgs());
    }

    private void AccountFlyout_Opening(object sender, object e)
    {
        UpdateAccountFlyoutText();
    }

    private void UpdateAccountFlyoutText()
    {
        bool isLoggedIn = !string.IsNullOrWhiteSpace(_loggedInUser);
        AccountUsernameText.Text = isLoggedIn
            ? _loggedInUser
            : Lang.Account_NotLoggedInYet;
        LogoutBtn.Content = isLoggedIn ? (object)Lang.Account_Logout : Lang.Login_Submit;
    }

    private void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();

        if (string.IsNullOrWhiteSpace(_loggedInUser))
        {
            ShowLoginOverlay();
            return;
        }

        if (Services.BuildInfo.IsClient)
        {
            // Disconnect the stream, revoke the session on the server (best-effort),
            // and clear the stored token (host + username are kept to prefill login).
            var s = AppSettingsService.Load();
            ShareClient.Instance.Disconnect();
            _ = AuthClient.LogoutAsync(s.ServerHost, s.ServerToken);
            s.ServerToken = "";
            AppSettingsService.Save(s);
            App.Status.AiConnected = false;
            if (ContentFrame?.Content is Views.AIPage aiPage)
                aiPage.ReloadSettings();
        }

        _loggedInUser = "";
        _loggedInRole = "";
        App.Session.SignOut();
        UpdateAccountFlyoutText();
        ApplyRoleNavVisibility();
        ShowLoginOverlay();
    }

    private void RefreshApp_Click(object sender, RoutedEventArgs e)
    {
        // Re-navigate to the current page so it tears down and rebuilds —
        // covers stale chart visuals, ComboBox state, etc.
        if (NavView.SelectedItem is not NavigationViewItem item ||
            item.Tag is not string tag ||
            !_pages.TryGetValue(tag, out var pageType))
            return;

        ContentFrame.Content = null;
        ContentFrame.Navigate(pageType);
    }

    private void MenuTour_Click(object sender, RoutedEventArgs e)
    {
        ShowTourOverlay();
    }

    // ── Startup update check ───────────────────────────────────────────────

    private async Task StartupUpdateCheckAsync()
    {
        await Task.Delay(3000); // let the app finish loading first

        var info = await Services.UpdateService.CheckAsync(AppVersion, _earlyAccess);
        _cachedUpdateInfo = info;

        if (info.Result != Services.UpdateCheckResult.UpdateAvailable) return;

        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateNotifyBtn.Visibility = Visibility.Visible;
            ToolTipService.SetToolTip(UpdateNotifyBtn,
                $"{Lang.Upd_AvailableTitle}: v{info.LatestVersion}");
        });
    }

    private void UpdateNotifyBtn_Click(object sender, RoutedEventArgs e)
    {
        var info = _cachedUpdateInfo;
        if (info?.Result == Services.UpdateCheckResult.UpdateAvailable)
        {
            UpdateDialogTitle.Text        = Lang.Upd_AvailableTitle;
            UpdateContentHost.Content     = BuildUpdateAvailableContent(info);
            _pendingZipUrl                = info.UpdateZipUrl;
            _pendingReleaseUrl            = info.ReleaseUrl;
            _pendingUpdate                = null;
            UpdatePrimaryButton.Visibility = Visibility.Visible;
            UpdatePrimaryButton.Content    = info.UpdateZipUrl is not null
                                                ? Lang.Upd_Download
                                                : Lang.Upd_OpenPage;
            UpdatePrimaryButton.IsEnabled  = true;
            UpdateCloseButton.Content      = Lang.Upd_Later;
            UpdateCloseButton.IsEnabled    = true;
            UpdateOverlay.Visibility       = Visibility.Visible;
        }
        else
        {
            ShowUpdateChecking();
            _ = RunUpdateCheckAsync();
        }
    }

    // ── Update overlay ─────────────────────────────────────────────────────

    private string?                        _pendingZipUrl;
    private string?                        _pendingReleaseUrl;
    private Services.PreparedUpdate?       _pendingUpdate;      // downloaded payload after extract
    private Services.UpdateCheckInfo?      _cachedUpdateInfo;

    private void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        ShowUpdateChecking();
        _ = RunUpdateCheckAsync();
    }

    private void ShowUpdateChecking()
    {
        UpdateDialogTitle.Text = Lang.Upd_CheckingTitle;
        UpdateContentHost.Content = BuildUpdateSpinner();
        UpdatePrimaryButton.Visibility = Visibility.Collapsed;
        UpdateCloseButton.Content = Lang.Upd_Close;
        UpdateCloseButton.IsEnabled = true;
        _pendingZipUrl    = null;
        _pendingReleaseUrl = null;
        _pendingUpdate     = null;
        UpdateOverlay.Visibility = Visibility.Visible;
    }

    private async Task RunUpdateCheckAsync()
    {
        var info = await Services.UpdateService.CheckAsync(AppVersion, _earlyAccess);

        DispatcherQueue.TryEnqueue(() =>
        {
            if (UpdateOverlay.Visibility == Visibility.Collapsed) return;

            switch (info.Result)
            {
                case Services.UpdateCheckResult.UpToDate:
                    UpdateDialogTitle.Text = Lang.Upd_UpToDateTitle;
                    UpdateContentHost.Content = BuildUpdateStatusContent(
                        "", Lang.Upd_UpToDateMsg,
                        $"{Lang.Upd_LatestVersion}: {info.LatestVersion}");
                    UpdatePrimaryButton.Visibility = Visibility.Collapsed;
                    UpdateCloseButton.Content = Lang.Upd_Close;
                    break;

                case Services.UpdateCheckResult.UpdateAvailable:
                    UpdateDialogTitle.Text = Lang.Upd_AvailableTitle;
                    UpdateContentHost.Content = BuildUpdateAvailableContent(info);
                    _pendingZipUrl       = info.UpdateZipUrl;
                    _pendingReleaseUrl   = info.ReleaseUrl;
                    _pendingUpdate       = null;
                    UpdatePrimaryButton.Visibility = Visibility.Visible;
                    UpdatePrimaryButton.Content    = info.UpdateZipUrl is not null
                                                        ? Lang.Upd_Download
                                                        : Lang.Upd_OpenPage;
                    UpdatePrimaryButton.IsEnabled  = true;
                    UpdateCloseButton.Content      = Lang.Upd_Later;
                    break;

                case Services.UpdateCheckResult.Error:
                    UpdateDialogTitle.Text = Lang.Upd_ErrorTitle;
                    UpdateContentHost.Content = BuildUpdateStatusContent(
                        "", info.ErrorMessage ?? "Unknown error", null);
                    UpdatePrimaryButton.Visibility = Visibility.Collapsed;
                    UpdateCloseButton.Content = Lang.Upd_Close;
                    break;
            }
        });
    }

    private async void UpdatePrimary_Click(object sender, RoutedEventArgs e)
    {
        // ── Phase 2: apply a prepared update ──────────────────────────────
        if (_pendingUpdate is not null)
        {
            if (TryStartUpdateApply())
                Application.Current.Exit();
            return;
        }

        // ── Phase 1: download + extract ZIP ──────────────────────────────
        if (_pendingZipUrl is not null)
        {
            UpdatePrimaryButton.IsEnabled = false;
            UpdateCloseButton.IsEnabled   = false;
            UpdateDialogTitle.Text        = Lang.Upd_Downloading;

            var bar = new ProgressBar
            {
                IsIndeterminate = true,
                Margin          = new Thickness(0, 8, 0, 8)
            };
            UpdateContentHost.Content = bar;

            try
            {
                var update = await Services.UpdateService.DownloadAndExtractAsync(
                    _pendingZipUrl, AppVersion, p =>
                    {
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            bar.IsIndeterminate = false;
                            bar.Value           = p * 100;
                        });
                    });

                _pendingUpdate = update;

                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdateDialogTitle.Text        = Lang.Upd_Extracting;
                    UpdateContentHost.Content     = BuildUpdateStatusContent(
                        "",   // Segoe: Accept/Check glyph
                        Lang.Upd_Extracting,
                        null);
                    UpdatePrimaryButton.Content   = Lang.Upd_InstallNow;
                    UpdatePrimaryButton.IsEnabled = true;
                    UpdateCloseButton.IsEnabled   = true;
                });

                if (TryStartUpdateApply())
                    Application.Current.Exit();
            }
            catch (Exception ex)
            {
                UpdateDialogTitle.Text = Lang.Upd_ErrorTitle;
                UpdateContentHost.Content = BuildUpdateStatusContent(
                    "î¨¹", ex.Message, null);
                UpdatePrimaryButton.Visibility = _pendingReleaseUrl is not null
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdatePrimaryButton.Content = Lang.Upd_OpenPage;
                UpdatePrimaryButton.IsEnabled = true;
                UpdateCloseButton.Content = Lang.Upd_Close;
                UpdateCloseButton.IsEnabled = true;
                _pendingZipUrl = null;
                _pendingUpdate = null;
            }

            return;
        }

        // ── Fallback: open release page ───────────────────────────────────
        if (_pendingReleaseUrl is not null)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(_pendingReleaseUrl));
            UpdateOverlay.Visibility   = Visibility.Collapsed;
            UpdateNotifyBtn.Visibility = Visibility.Collapsed;
            _cachedUpdateInfo          = null;
        }
    }

    private bool TryStartUpdateApply()
    {
        if (_pendingUpdate is null)
            return false;

        try
        {
            var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            var scriptPath = Services.UpdateService.WriteApplyScript(
                _pendingUpdate.PayloadPath, _pendingUpdate.CleanupPath, appDir);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = true,
                Verb = "runas", // elevated: Program Files installs need write access.
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-WindowStyle");
            startInfo.ArgumentList.Add("Hidden");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(scriptPath);
            startInfo.ArgumentList.Add("-Payload");
            startInfo.ArgumentList.Add(_pendingUpdate.PayloadPath);
            startInfo.ArgumentList.Add("-AppDir");
            startInfo.ArgumentList.Add(appDir);
            startInfo.ArgumentList.Add("-Cleanup");
            startInfo.ArgumentList.Add(_pendingUpdate.CleanupPath);
            startInfo.ArgumentList.Add("-ParentProcessId");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            if (System.Diagnostics.Process.Start(startInfo) is null)
                throw new InvalidOperationException("Unable to start the update helper.");

            return true;
        }
        catch (Exception ex)
        {
            UpdateDialogTitle.Text = Lang.Upd_ErrorTitle;
            UpdateContentHost.Content = BuildUpdateStatusContent(
                "î¨¹", ex.Message, null);
            UpdatePrimaryButton.Visibility = _pendingReleaseUrl is not null
                ? Visibility.Visible
                : Visibility.Collapsed;
            UpdatePrimaryButton.Content = Lang.Upd_OpenPage;
            UpdatePrimaryButton.IsEnabled = true;
            UpdateCloseButton.Content = Lang.Upd_Close;
            UpdateCloseButton.IsEnabled = true;
            _pendingZipUrl = null;
            _pendingUpdate = null;
            return false;
        }
    }

    private void UpdateClose_Click(object sender, RoutedEventArgs e)
    {
        UpdateOverlay.Visibility = Visibility.Collapsed;
        _pendingUpdate           = null;
    }

    private static UIElement BuildUpdateSpinner()
    {
        var ring = new ProgressRing
        {
            IsActive = true,
            Width    = 36,
            Height   = 36,
            Margin   = new Thickness(0, 8, 0, 16),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        return ring;
    }

    private UIElement BuildUpdateStatusContent(string glyph, string line1, string? line2)
    {
        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new FontIcon
        {
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
            Glyph      = glyph,
            FontSize   = 28,
            HorizontalAlignment = HorizontalAlignment.Left
        });
        panel.Children.Add(new TextBlock
        {
            Text        = line1,
            FontSize    = 14,
            TextWrapping = TextWrapping.Wrap
        });
        if (line2 is not null)
            panel.Children.Add(new TextBlock
            {
                Text     = line2,
                FontSize = 13,
                Opacity  = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
        return panel;
    }

    private UIElement BuildUpdateAvailableContent(Services.UpdateCheckInfo info)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 0, 8) };

        var versionGrid = new Grid { ColumnSpacing = 12 };
        versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        versionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        versionGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddVersionRow(int row, string label, string? value, bool accent = false)
        {
            var lbl = new TextBlock { Text = label, FontSize = 13, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            var val = new TextBlock
            {
                Text        = value ?? "-",
                FontSize    = 13,
                FontWeight  = accent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
            Grid.SetRow(val, row); Grid.SetColumn(val, 1);
            versionGrid.Children.Add(lbl);
            versionGrid.Children.Add(val);
        }

        AddVersionRow(0, Lang.Upd_CurrentVersion + ":", AppVersion);
        AddVersionRow(1, Lang.Upd_LatestVersion  + ":", info.LatestVersion, accent: true);
        panel.Children.Add(versionGrid);

        if (!string.IsNullOrWhiteSpace(info.ReleaseNotes))
        {
            panel.Children.Add(new TextBlock
            {
                Text     = Lang.Upd_ReleaseNotes,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin   = new Thickness(0, 4, 0, 0)
            });

            var notes = info.ReleaseNotes.Length > 500
                ? info.ReleaseNotes[..500] + "…"
                : info.ReleaseNotes;

            var sv = new ScrollViewer
            {
                MaxHeight = 140,
                HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility   = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto,
                Content = new TextBlock
                {
                    Text         = notes,
                    FontSize     = 12,
                    Opacity      = 0.8,
                    TextWrapping = TextWrapping.Wrap
                }
            };
            panel.Children.Add(sv);
        }

        return panel;
    }

    private async void MenuGithub_Click(object sender, RoutedEventArgs e)
    {
        await Windows.System.Launcher.LaunchUriAsync(
            new Uri("https://github.com/Khlfnalvr/TLIG-Dashboard/"));
    }

    private void MenuOpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettingsService.FolderPath);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppSettingsService.FolderPath,
            UseShellExecute = true
        });
    }

    private void MenuOpenSettings_Click(object sender, RoutedEventArgs e)
    {
        // Materialize the file with current values if it doesn't exist yet —
        // otherwise Explorer/Notepad has nothing to open.
        if (!File.Exists(AppSettingsService.FilePath))
        {
            var s = AppSettingsService.Load();
            s.Language = Lang.CurrentLanguage;
            AppSettingsService.Save(s);
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = AppSettingsService.FilePath,
            UseShellExecute = true
        });
    }

    private void MenuEarlyAccess_Click(object sender, RoutedEventArgs e)
    {
        _earlyAccess = MenuEarlyAccess.IsChecked;
        var s = AppSettingsService.Load();
        s.EarlyAccess = _earlyAccess;
        AppSettingsService.Save(s);
    }

    private async void MenuReportBug_Click(object sender, RoutedEventArgs e)
    {
        string title = "[Bug] ";
        string body =
            $"**App version:** {AppVersion}\n" +
            $"**OS:** {Environment.OSVersion.VersionString}\n" +
            $"**Language:** {Lang.CurrentLanguage}\n\n" +
            "## Description\n\n" +
            "_What went wrong?_\n\n" +
            "## Steps to reproduce\n\n" +
            "1. \n2. \n3. \n\n" +
            "## Expected vs actual\n\n";

        string url = "https://github.com/Khlfnalvr/TLIG-Dashboard/issues/new" +
                     $"?title={Uri.EscapeDataString(title)}" +
                     $"&body={Uri.EscapeDataString(body)}";

        await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    private void ShowTourOverlay()
    {
        if (Content?.XamlRoot is null) return;

        double rootWidth = Content.XamlRoot.Size.Width;
        double rootHeight = Content.XamlRoot.Size.Height;
        double dialogWidth = Math.Min(620, Math.Max(320, rootWidth - 96));
        double contentWidth = Math.Max(300, dialogWidth - 60);

        TourDialogCard.Width = dialogWidth;
        TourDialogCard.MaxHeight = Math.Max(420, rootHeight - 96);
        TourDialogCard.Background = TourSurfaceBrush();
        TourDialogTitle.Text = TourText("Tour TLIG Dashboard", "TLIG Dashboard Tour");
        TourOpenControlPanelButton.Content = TourText("Buka Parameter", "Open Parameter");
        TourCloseButton.Content = TourText("Tutup", "Close");
        TourContentHost.Content = BuildTourContent(contentWidth, Math.Max(260, rootHeight - 250));
        TourOverlay.Visibility = Visibility.Visible;
    }

    private void TourOpenControlPanel_Click(object sender, RoutedEventArgs e)
    {
        HideTourOverlay();
        SelectControlPanelForTour();
    }

    private void TourClose_Click(object sender, RoutedEventArgs e)
    {
        HideTourOverlay();
    }

    private void HideTourOverlay()
    {
        TourOverlay.Visibility = Visibility.Collapsed;
        TourContentHost.Content = null;
    }

    private UIElement BuildTourContent(double availableWidth, double maxHeight)
    {
        const double scrollGutterWidth = 18;
        double scrollWidth = Math.Min(520, Math.Max(300, availableWidth));
        double panelWidth = Math.Max(280, scrollWidth - scrollGutterWidth);

        var panel = new StackPanel
        {
            Width = panelWidth,
            HorizontalAlignment = HorizontalAlignment.Left,
            Spacing = 14
        };

        panel.Children.Add(BuildTourHero(panelWidth));
        panel.Children.Add(BuildFlowCard(panelWidth));

        AddTourSection(
            panel,
            TourText("HALAMAN UTAMA", "MAIN PAGES"),
            ("\uE9D2", Lang.Nav_Dashboard, TourText(
                "Halaman utama dengan tiga panel yang dapat di-resize. Gunakan splitter untuk mengatur lebar panel dan tombol fullscreen untuk membuka halaman penuh.",
                "The main page with three resizable panels. Drag the splitters to adjust panel widths and use the fullscreen button to open a full page.")),
            ("\uE713", Lang.Nav_Parameter, TourText(
                "Atur parameter dan konfigurasi sistem.",
                "Configure system parameters and settings.")),
            ("\uE8EF", Lang.Nav_LiveView, TourText(
                "Tampilkan data secara real-time dari perangkat yang terhubung.",
                "Display real-time data from the connected device.")),
            ("\uE899", Lang.Nav_AI, TourText(
                "Analisis data menggunakan kecerdasan buatan.",
                "Analyze data using artificial intelligence.")));

        AddTourSection(
            panel,
            TourText("TOMBOL CEPAT DI TITLE BAR", "TITLE BAR QUICK BUTTONS"),
            ("\uE7E7", TourText("Alert", "Alerts"), TourText(
                "Ikon lonceng membuka riwayat alert dan badge merah menampilkan jumlah alert baru.",
                "The bell opens alert history, and the red badge shows the number of unread alerts.")),
            ("\uE839", TourText("Serial", "Serial"), TourText(
                "Akses cepat untuk refresh port, memilih COM dan baud rate, lalu connect atau disconnect tanpa pindah halaman.",
                "Quick access to refresh ports, choose COM and baud rate, then connect or disconnect without changing pages.")),
            ("\uE12B", TourText("Bahasa", "Language"), TourText(
                "Ganti bahasa aplikasi langsung dari title bar.",
                "Switch the application language directly from the title bar.")),
            ("\uE708", TourText("Tema", "Theme"), TourText(
                "Beralih antara mode terang dan gelap sesuai kondisi kerja.",
                "Switch between light and dark mode for the current workspace.")));

        AddTourSection(
            panel,
            TourText("MENU, PLAYBACK, DAN STATUS", "MENUS, PLAYBACK, AND STATUS"),
            ("\uE8A5", Lang.Ui_Menu_View, TourText(
                "Tampilkan atau sembunyikan halaman di navigation bar agar workspace tetap ringkas.",
                "Show or hide pages in the navigation bar to keep the workspace focused.")),
            ("\uE9D2", Lang.Ui_Menu_Unit, TourText(
                "Pilih satuan temperatur, tegangan, dan kapasitas yang paling nyaman untuk dibaca.",
                "Choose the temperature, voltage, and capacity units that are easiest to read.")),
            ("\uE946", Lang.Ui_Menu_About, TourText(
                "Lihat nama produk, versi aplikasi, lisensi, dan informasi dasar lainnya.",
                "View product name, app version, license, and other basic information.")),
            ("\uE946", Lang.Ui_Menu_Tour, TourText(
                "Buka panduan fitur ini kapan saja dari menu title bar.",
                "Open this feature guide anytime from the title bar menu.")),
            ("\uE72C", Lang.Ui_Menu_RefreshApp, TourText(
                "Muat ulang halaman aktif saat tampilan grafik, dropdown, atau state visual perlu disegarkan.",
                "Reload the active page when charts, dropdowns, or visual state need a refresh.")),
            ("\uE768", TourText("Playback bar", "Playback bar"), TourText(
                "Saat file log dimuat, gunakan first, play atau pause, last, slider frame, dan unload untuk kembali ke live mode.",
                "When a log is loaded, use first, play or pause, last, the frame slider, and unload to return to live mode.")),
            ("\uE81E", TourText("Status bar", "Status bar"), TourText(
                "Bagian bawah menampilkan sumber data, status koneksi, dan jam aplikasi.",
                "The bottom bar shows data source, connection status, and the app clock.")));

        panel.Children.Add(new Border
        {
            Height = 20,
            IsHitTestVisible = false,
            Opacity = 0
        });

        return new ScrollViewer
        {
            Width = scrollWidth,
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = panel,
            MaxHeight = maxHeight,
            HorizontalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = Microsoft.UI.Xaml.Controls.ScrollBarVisibility.Auto
        };
    }

    private FrameworkElement BuildTourHero(double panelWidth)
    {
        double textWidth = Math.Max(220, panelWidth - 42 - 14 - 32);
        var text = new StackPanel { Spacing = 4, Width = textWidth };
        text.Children.Add(new TextBlock
        {
            Text = TourText("Kenali area kerja utama", "Get familiar with the workspace"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = TourText(
                "Panduan ini merangkum halaman, tombol, menu, playback, dan status bar agar pengguna baru langsung tahu harus mulai dari mana.",
                "This guide summarizes pages, buttons, menus, playback, and the status bar so new users know where to start."),
            FontSize = 13,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        });

        var layout = new Grid { ColumnSpacing = 14 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildTourIconShell("\uE946", 42);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(icon);
        layout.Children.Add(text);

        return new Border
        {
            Width = panelWidth,
            Background = TourRaisedSurfaceBrush(),
            BorderBrush = TourStrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Child = layout
        };
    }

    private FrameworkElement BuildFlowCard(double panelWidth)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BuildTourSectionHeader(TourText("ALUR DISARANKAN", "RECOMMENDED FLOW")));
        panel.Children.Add(BuildTourFlowStep("1", TourText(
            "Hubungkan ke server lewat tombol Serial atau halaman Control Panel.",
            "Connect to server from the Serial button or Control Panel."), panelWidth));
        panel.Children.Add(BuildTourFlowStep("2", TourText(
            "Pantau kondisi di Dashboard, lalu buka Channel View untuk detail tiap kanal.",
            "Watch system status on Dashboard, then open Channel View for per-channel detail."), panelWidth));
        panel.Children.Add(BuildTourFlowStep("3", TourText(
            "Gunakan Logging untuk merekam sesi pengujian dan Playback untuk analisis ulang.",
            "Use Logging to record test sessions and Playback for later analysis."), panelWidth));

        return new Border
        {
            Width = panelWidth,
            Background = TourRaisedSurfaceBrush(),
            BorderBrush = TourStrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Child = panel
        };
    }

    private void AddTourSection(
        StackPanel panel,
        string title,
        params (string Glyph, string Title, string Body)[] items)
    {
        panel.Children.Add(BuildTourSectionHeader(title));
        panel.Children.Add(BuildTourGrid(panel.Width, items));
    }

    private FrameworkElement BuildTourSectionHeader(string text) => new TextBlock
    {
        Text = text,
        FontSize = 11,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing = 80,
        Opacity = 0.58,
        Margin = new Thickness(0, 4, 0, -4)
    };

    private FrameworkElement BuildTourGrid(
        double panelWidth,
        (string Glyph, string Title, string Body)[] items)
    {
        int columns = 1;
        var grid = new Grid { Width = panelWidth, ColumnSpacing = 10, RowSpacing = 10 };
        for (int i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < items.Length; i++)
        {
            int row = i / columns;
            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var card = BuildTourCard(items[i].Glyph, items[i].Title, items[i].Body, panelWidth);
            Grid.SetRow(card, row);
            Grid.SetColumn(card, i % columns);
            grid.Children.Add(card);
        }

        return grid;
    }

    private FrameworkElement BuildTourCard(string glyph, string title, string body, double panelWidth)
    {
        double textWidth = Math.Max(220, panelWidth - 34 - 10 - 24);
        var text = new StackPanel { Spacing = 2, Width = textWidth };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        text.Children.Add(new TextBlock
        {
            Text = body,
            FontSize = 12,
            Opacity = 0.68,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap
        });

        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = BuildTourIconShell(glyph, 34);
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(icon);
        layout.Children.Add(text);

        return new Border
        {
            Width = panelWidth,
            Background = TourRaisedSurfaceBrush(),
            BorderBrush = TourStrokeBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = layout
        };
    }

    private FrameworkElement BuildTourIconShell(string glyph, double size)
    {
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(6),
            Background = TourIconSurfaceBrush(),
            Child = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                Glyph = glyph,
                FontSize = size >= 40 ? 20 : 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    private FrameworkElement BuildTourFlowStep(string number, string body, double panelWidth)
    {
        double textWidth = Math.Max(220, panelWidth - 24 - 10 - 28);
        var layout = new Grid { ColumnSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = TourIconSurfaceBrush(),
            Child = new TextBlock
            {
                Text = number,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var text = new TextBlock
        {
            Text = body,
            Width = textWidth,
            FontSize = 12,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(badge, 0);
        Grid.SetColumn(text, 1);
        layout.Children.Add(badge);
        layout.Children.Add(text);
        return layout;
    }

    private void SelectControlPanelForTour()
    {
        foreach (var (nav, toggle) in NavToggles())
        {
            if (nav.Tag is not string tag || tag != "Parameter")
                continue;

            if (nav.Visibility != Visibility.Visible)
            {
                ApplyNavVisibility(nav, toggle, true);
                SaveNavVisibility();
            }

            NavView.SelectedItem = nav;
            return;
        }
    }

    private static readonly Dictionary<string, string> TourTranslations = new()
    {
        ["TLIG Dashboard Tour"] = "Tour TLIG Dashboard",
        ["Open Control Panel"] = "Buka Panel Kontrol",
        ["Close"] = "Tutup",
        ["MAIN PAGES"] = "HALAMAN UTAMA",
        ["Monitor main values, levels, currents, system status, channel summaries, and main history charts."] = "Pantau nilai utama, level, arus, status sistem, ringkasan kanal, dan grafik riwayat utama.",
        ["Inspect 20 channels and 10 sensors. Click a channel or sensor to open its history chart."] = "Periksa 20 kanal dan 10 sensor. Klik kanal atau sensor untuk membuka grafik riwayatnya.",
        ["Configure OPC UA connection, node IDs, thresholds, and display settings."] = "Konfigurasi koneksi OPC UA, node ID, ambang batas, dan pengaturan tampilan.",
        ["Record live data to CSV, TSV, Excel, or JSON, choose the output folder, and watch the latest 20 frames."] = "Rekam data langsung ke CSV, TSV, Excel, atau JSON, pilih folder output, dan pantau 20 frame terbaru.",
        ["Load a CSV log and replay it. Every page updates as if live data were coming in."] = "Muat log CSV dan putar ulang. Setiap halaman diperbarui seolah data langsung masuk.",
        ["TITLE BAR QUICK BUTTONS"] = "TOMBOL CEPAT TITLE BAR",
        ["Alerts"] = "Peringatan",
        ["The bell opens alert history, and the red badge shows the number of unread alerts."] = "Ikon bel membuka riwayat peringatan, dan lencana merah menunjukkan jumlah peringatan yang belum dibaca.",
        ["Serial"] = "Serial",
        ["Quick access to refresh ports, choose COM and baud rate, then connect or disconnect without changing pages."] = "Akses cepat untuk menyegarkan port, memilih COM dan baud rate, lalu connect atau disconnect tanpa berpindah halaman.",
        ["Language"] = "Bahasa",
        ["Switch the application language directly from the title bar."] = "Ganti bahasa aplikasi langsung dari title bar.",
        ["Theme"] = "Tema",
        ["Switch between light and dark mode for the current workspace."] = "Beralih antara mode terang dan gelap untuk workspace saat ini.",
        ["MENUS, PLAYBACK, AND STATUS"] = "MENU, PLAYBACK, DAN STATUS",
        ["Show or hide pages in the navigation bar to keep the workspace focused."] = "Tampilkan atau sembunyikan halaman di navigation bar agar workspace tetap fokus.",
        ["Choose the temperature, voltage, and capacity units that are easiest to read."] = "Pilih unit suhu, tegangan, dan kapasitas yang paling mudah dibaca.",
        ["View product name, app version, license, and other basic information."] = "Lihat nama produk, versi aplikasi, lisensi, dan informasi dasar lainnya.",
        ["Open this feature guide anytime from the title bar menu."] = "Buka panduan fitur ini kapan saja dari menu title bar.",
        ["Reload the active page when charts, dropdowns, or visual state need a refresh."] = "Muat ulang halaman aktif saat grafik, dropdown, atau tampilan perlu disegarkan.",
        ["Playback bar"] = "Bar Playback",
        ["When a log is loaded, use first, play or pause, last, the frame slider, and unload to return to live mode."] = "Saat log dimuat, gunakan tombol pertama, play atau pause, terakhir, slider frame, dan unload untuk kembali ke mode live.",
        ["Status bar"] = "Bar Status",
        ["The bottom bar shows data source and connection status."] = "Bar bawah menampilkan sumber data dan status koneksi.",
        ["Get familiar with the workspace"] = "Kenali ruang kerja utama",
        ["This guide summarizes pages, buttons, menus, playback, and the status bar so new users know where to start."] = "Panduan ini merangkum halaman, tombol, menu, playback, dan bar status agar pengguna baru tahu harus mulai dari mana.",
        ["RECOMMENDED FLOW"] = "ALUR YANG DISARANKAN",
        ["Connect to server from the Serial button or Control Panel."] = "Hubungkan ke server lewat tombol Serial atau halaman Control Panel.",
        ["Watch system status on Dashboard, then open Channel View for per-channel detail."] = "Pantau kondisi di Dashboard, lalu buka Channel View untuk detail tiap kanal.",
        ["Use Logging to record test sessions and Playback for later analysis."] = "Gunakan Logging untuk merekam sesi pengujian dan Playback untuk analisis ulang."
    };

    private string TourText(string id, string en)
    {
        if (Lang.CurrentLanguage == "id") return id;
        return en;
    }

    private bool IsDarkTheme()
        => Content is FrameworkElement fe && fe.RequestedTheme == ElementTheme.Dark;

    private Brush TourSurfaceBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x24, 0x24, 0x24)
            : Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA));

    private Brush TourRaisedSurfaceBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x32, 0x32, 0x32)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private Brush TourIconSurfaceBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x42, 0x42, 0x42)
            : Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE));

    private Brush TourStrokeBrush()
        => new SolidColorBrush(IsDarkTheme()
            ? Color.FromArgb(0xFF, 0x45, 0x45, 0x45)
            : Color.FromArgb(0xFF, 0xDD, 0xDD, 0xDD));

    // ── Caption-bar OPC UA picker ─────────────────────────────────────────
    private void InitOpcUaFlyout()
    {
        var saved = AppSettingsService.Load();
        OpcEndpointBox.Text = saved.OpcUaEndpointUrl;
        OpcProtocolCombo.SelectedIndex = saved.OpcProtocol == "DA" ? 1 : 0;
        OpcDaProgIdBox.Text = saved.OpcDaProgId;
        ApplyOpcProtocolPanel();

        // Security combo
        OpcSecNone.Content    = Lang.Ui_OpcUaSecNone;
        OpcSecSign.Content    = Lang.Ui_OpcUaSecSign;
        OpcSecSignEnc.Content = Lang.Ui_OpcUaSecSignEnc;
        OpcSecurityCombo.SelectedIndex = saved.OpcUaSecurityMode switch
        {
            "Sign"           => 1,
            "SignAndEncrypt" => 2,
            _                => 0
        };

        // Auth combo
        OpcAuthAnon.Content = Lang.Ui_OpcUaAnonymous;
        OpcAuthUser.Content = Lang.Ui_OpcUaUsernameAuth;
        OpcAuthCombo.SelectedIndex = saved.OpcUaUseAnonymous ? 0 : 1;
        OpcCredPanel.Visibility    = saved.OpcUaUseAnonymous
            ? Visibility.Collapsed : Visibility.Visible;

        UpdateOpcStatusDot();
        SyncOpcConnectButton();

        // Init AI panel values (server only — client routes through server proxy)
        if (Services.BuildInfo.IsServer)
            InitAiPanel();
        else
            TabAiApi.Visibility = Visibility.Collapsed;

        // Init sharing panel (broadcast on server, connect on client)
        InitSharePanel();

        // Default tab = OPC UA
        ConnAiTabs.SelectedItem = TabOpcUa;

        ViewModel.OpcUa.StatusChanged += _ => DispatcherQueue.TryEnqueue(() =>
        {
            SyncOpcConnectButton();
            UpdateOpcStatusDot();
            bool connected = ViewModel.OpcUa.IsConnected;
            App.Status.PlcConnected    = connected;
            App.Status.SensorConnected = connected;
        });

        ViewModel.OpcDa.StatusChanged += _ => DispatcherQueue.TryEnqueue(() =>
        {
            SyncOpcConnectButton();
            UpdateOpcStatusDot();
            bool connected = ViewModel.OpcDa.IsConnected;
            App.Status.PlcConnected    = connected;
            App.Status.SensorConnected = connected;
        });
    }

    private void OpcUaFlyout_Opened(object sender, object e)
    {
        SyncOpcConnectButton();
        UpdateOpcStatusDot();
        if (Services.BuildInfo.IsServer)
            InitAiPanel();
    }

    private void OpcAuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpcCredPanel == null) return;
        OpcCredPanel.Visibility = OpcAuthCombo.SelectedIndex == 1
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpcSecurityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void OpcProtocolCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyOpcProtocolPanel();
        SyncOpcConnectButton();
    }

    private void ApplyOpcProtocolPanel()
    {
        bool isDa = OpcProtocolCombo?.SelectedIndex == 1;
        if (OpcUaFields != null)
            OpcUaFields.Visibility = isDa ? Visibility.Collapsed : Visibility.Visible;
        if (OpcDaFields != null)
            OpcDaFields.Visibility = isDa ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SyncOpcConnectButton()
    {
        bool isDa      = OpcProtocolCombo?.SelectedIndex == 1;
        bool connected = isDa ? ViewModel.OpcDa.IsConnected : ViewModel.OpcUa.IsConnected;

        OpcConnectBtn.Content = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        if (OpcProtocolCombo != null)
            OpcProtocolCombo.IsEnabled = !connected;

        if (isDa)
        {
            if (OpcDaProgIdBox != null)
                OpcDaProgIdBox.IsEnabled = !connected;
            OpcStatusText.Text = connected
                ? LocalizationManager.Instance.Format("OpcUa_StatusConnected", ViewModel.OpcDa.ProgId)
                : LocalizationManager.Instance.Get("Ctrl_NotConnected");
        }
        else
        {
            OpcEndpointBox.IsEnabled       = !connected;
            OpcSecurityCombo.IsEnabled     = !connected;
            OpcAuthCombo.IsEnabled         = !connected;
            OpcCredPanel.IsHitTestVisible  = !connected;
            OpcStatusText.Text = connected
                ? LocalizationManager.Instance.Format("OpcUa_StatusConnected", ViewModel.OpcUa.EndpointUrl)
                : LocalizationManager.Instance.Get("Ctrl_NotConnected");
        }
    }

    private async void OpcConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (OpcProtocolCombo?.SelectedIndex == 1)
        {
            await ConnectOpcDaAsync();
            return;
        }

        if (ViewModel.OpcUa.IsConnected)
        {
            ViewModel.OpcUa.Disconnect();
            SyncOpcConnectButton();
            UpdateOpcStatusDot();
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
        OpcStatusText.Text      = LocalizationManager.Instance.Get("OpcUa_StatusConnecting");

        bool ok = await ViewModel.OpcUa.ConnectAsync(
            endpointUrl, authMode,
            OpcUsernameBox.Text,
            OpcPasswordBox.Password,
            secMode);

        if (ok)
        {
            var s = AppSettingsService.Load();
            s.OpcUaEndpointUrl  = endpointUrl;
            s.OpcUaSecurityMode = secMode.ToString();
            s.OpcUaUseAnonymous = authMode == OpcUaAuthMode.Anonymous;
            s.OpcUaUsername     = OpcUsernameBox.Text;
            s.OpcProtocol       = "UA";
            AppSettingsService.Save(s);
        }

        OpcConnectBtn.IsEnabled = true;
        SyncOpcConnectButton();
        UpdateOpcStatusDot();
    }

    private async Task ConnectOpcDaAsync()
    {
        if (ViewModel.OpcDa.IsConnected)
        {
            ViewModel.OpcDa.Disconnect();
            SyncOpcConnectButton();
            UpdateOpcStatusDot();
            return;
        }

        var progId = OpcDaProgIdBox?.Text.Trim() ?? "";
        if (string.IsNullOrEmpty(progId)) return;

        OpcConnectBtn.IsEnabled = false;
        OpcStatusText.Text      = LocalizationManager.Instance.Get("OpcUa_StatusConnecting");

        string? capturedError = null;
        void OnError(string msg) => capturedError = msg;
        ViewModel.OpcDa.ErrorOccurred += OnError;
        bool ok = await ViewModel.OpcDa.ConnectAsync(progId);
        ViewModel.OpcDa.ErrorOccurred -= OnError;

        if (ok)
        {
            var s = AppSettingsService.Load();
            s.OpcProtocol = "DA";
            s.OpcDaProgId = progId;
            AppSettingsService.Save(s);
        }
        else if (capturedError is not null)
        {
            OpcStatusText.Text = capturedError;
        }

        OpcConnectBtn.IsEnabled = true;
        if (ok) SyncOpcConnectButton();
        UpdateOpcStatusDot();
    }

    private void UpdateOpcStatusDot()
    {
        bool connected = ViewModel.OpcUa.IsConnected || ViewModel.OpcDa.IsConnected;
        OpcStatusDot.Fill = connected
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xC6, 0x85))
            : new SolidColorBrush(Color.FromArgb(0x00, 0x00, 0x00, 0x00));
    }

    private void OpcCertFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var certPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLIGDashboard", "pki", "own");
        Directory.CreateDirectory(certPath);
        Process.Start(new ProcessStartInfo("explorer.exe", certPath) { UseShellExecute = true });
    }

    // ── Tab switching (OPC UA ↔ AI API) ──────────────────────────────────────

    private void ConnAiTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool isAi    = ReferenceEquals(ConnAiTabs.SelectedItem, TabAiApi);
        bool isShare = ReferenceEquals(ConnAiTabs.SelectedItem, TabShare);

        PanelOpcUa.Visibility = (!isAi && !isShare) ? Visibility.Visible : Visibility.Collapsed;
        PanelAiApi.Visibility = isAi ? Visibility.Visible : Visibility.Collapsed;

        // The share tab is the client's session/connect panel. On the server flavor
        // the tab is hidden — broadcast settings live on the dedicated Broadcast page.
        PanelConnect.Visibility = (isShare && Services.BuildInfo.IsClient)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Sharing: client connect panel (server broadcast lives on its own page) ──

    private void InitSharePanel()
    {
        if (Services.BuildInfo.IsServer)
        {
            // Server broadcast settings moved to BroadcastSettingsPage — hide the
            // flyout's share tab so there is a single source of truth.
            TabShare.Visibility = Visibility.Collapsed;
            return;
        }

        TabShare.Text = Lang.Share_TabConnect;
        RefreshClientStatus();
        ShareClient.Instance.ConnectionChanged += (ok, info) =>
            DispatcherQueue.TryEnqueue(() => RefreshClientStatus(ok, info));
    }

    private void RefreshClientStatus(bool? ok = null, string? info = null)
    {
        bool connected = ShareClient.Instance.IsConnected;
        var  s         = AppSettingsService.Load();
        bool hasSession = !string.IsNullOrWhiteSpace(s.ServerHost) &&
                          !string.IsNullOrWhiteSpace(s.ServerToken);

        // Session summary: "username @ host", or a not-signed-in placeholder.
        ConnectServerInfoText.Text = string.IsNullOrWhiteSpace(s.ServerHost)
            ? Lang.Share_NotLoggedIn
            : $"{(string.IsNullOrWhiteSpace(s.ServerUsername) ? "?" : s.ServerUsername)} @ {s.ServerHost}";

        // Button: Disconnect when live; Connect when we hold a session; otherwise
        // it falls back to opening the login popup.
        ConnectServerBtn.Content = connected
            ? Lang.Share_Disconnect
            : (hasSession ? Lang.Share_Connect : Lang.Login_Submit);

        if (ok == false && info is not null)
            ClientStatusText.Text = Lang.Format(nameof(Lang.Share_ConnError), info);
        else
            ClientStatusText.Text = connected
                ? Lang.Format(nameof(Lang.Share_Connected), s.ServerHost)
                : Lang.Share_Disconnected;

        // The AI assistant is "active" on the client when connected to the server.
        App.Status.AiConnected = connected;
    }

    private async void ConnectServerBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ShareClient.Instance.IsConnected)
        {
            ShareClient.Instance.Disconnect();
            RefreshClientStatus();
            return;
        }

        var s     = AppSettingsService.Load();
        var host  = s.ServerHost;
        var token = s.ServerToken;

        // No active session → send the user through the credential login popup.
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
        {
            ShowLoginOverlay();
            return;
        }

        ConnectServerBtn.IsEnabled = false;
        bool ok = await ShareClient.Instance.ConnectAsync(host, token);
        ConnectServerBtn.IsEnabled = true;

        RefreshClientStatus(ok, ok ? null : Lang.Share_Disconnected);

        // Point the shared AI service at the server proxy.
        if (ContentFrame?.Content is Views.AIPage aiPage)
            aiPage.ReloadSettings();
    }

    private void SwitchServerBtn_Click(object sender, RoutedEventArgs e)
    {
        // Drop any current connection and reopen the login popup to sign in
        // (optionally to a different server).
        ShareClient.Instance.Disconnect();
        RefreshClientStatus();
        ShowLoginOverlay();
    }

    // ── AI API settings ───────────────────────────────────────────────────────

    private void InitAiPanel()
    {
        RefreshAiStatus();
    }

    /// <summary>
    /// Reflects the active provider/model in the flyout status line. "Configured"
    /// means the active provider is enabled and has a key — that's when the AI
    /// Assistant counts as active in the Status System panel.
    /// </summary>
    private void RefreshAiStatus()
    {
        var s    = AppSettingsService.Load();
        var info = Services.AiProviders.Resolve(s.AiActiveProvider);
        var cfg  = s.AiProviderConfigs.FirstOrDefault(c => c.Id == info.Id);
        bool configured = cfg is { Enabled: true } && !string.IsNullOrWhiteSpace(cfg.ApiKey);

        App.Status.AiConnected = configured;

        var model = !string.IsNullOrWhiteSpace(s.AiActiveModel)
            ? s.AiActiveModel
            : (cfg?.Models.FirstOrDefault() ?? "");
        AiStatusText.Text = configured
            ? $"✓  {info.Name} · {model}"
            : LocalizationManager.Instance.Get("Ai_ErrorNoKey");
        AiStatusText.Foreground = configured
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xC6, 0x85))
            : new SolidColorBrush(Color.FromArgb(0xFF, 0xCC, 0x6E, 0x00));
    }

    private async void AiConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        bool saved = await Views.AiConfigUi.ShowProviderConfigAsync(Content.XamlRoot);
        if (!saved) return;

        RefreshAiStatus();

        // Notify AIPage if it's currently loaded so it picks up new settings immediately.
        if (ContentFrame?.Content is Views.AIPage aiPage)
            aiPage.ReloadSettings();
    }

    // Flash the taskbar button (and caption when foreground) until the user
    // brings the window forward. Intentionally does NOT restore a minimized
    // window — the toast already surfaced the alert; flashing is the gentler
    // attention cue when toasts are suppressed.
    private void FlashTaskbarForCriticalAlert()
    {
        if (_hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize    = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd      = _hwnd,
            dwFlags   = FLASHW_ALL | FLASHW_TIMERNOFG,
            uCount    = 0,
            dwTimeout = 0,
        };
        FlashWindowEx(ref info);
    }

    private const int GWLP_WNDPROC = -4;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCRBUTTONDOWN = 0x00A4;
    private const uint WM_NCRBUTTONUP = 0x00A5;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const int HTCAPTION = 2;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    // FlashWindowEx flags
    private const uint FLASHW_CAPTION    = 0x00000001;
    private const uint FLASHW_TRAY       = 0x00000002;
    private const uint FLASHW_ALL        = FLASHW_CAPTION | FLASHW_TRAY;
    private const uint FLASHW_TIMERNOFG  = 0x0000000C;

    private delegate IntPtr WndProcDelegate(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(
        IntPtr hWnd,
        int nIndex,
        IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW", SetLastError = true)]
    private static extern IntPtr CallWindowProc(
        IntPtr lpPrevWndFunc,
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
}
