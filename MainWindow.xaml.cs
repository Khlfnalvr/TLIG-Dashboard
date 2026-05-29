using System.Collections.ObjectModel;
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
        ?? "Software BMS ICO";

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
        { "AI",        typeof(AIPage) }
    };

    private bool _pbSeeking; // suppress slider feedback loop
    private string _loggedInUser = "";

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clock;
    private bool _initializing;
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
        Title = "TLIG Dashboard";

        ApplyMicaBackdrop();
        InitializeTitleBar();
        SetAppIcon();
        InstallMinimumWindowSizeHook();
        InitializeTheme();
        InitializeZoom();

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => ClockText.Text = DateTime.Now.ToString("HH:mm:ss");
        _clock.Start();

        // Playback bar — subscribe so it appears/updates whenever the service fires
        ViewModel.Playback.StateChanged += OnPlaybackStateChanged;

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
            RefreshSerialButtonTooltip();
            SyncCapConnectButton();
            UpdateUnitMenuState();
            if (TourOverlay.Visibility == Visibility.Visible)
                ShowTourOverlay();
            OnPlaybackStateChanged();
            Bindings.Update();
        };
        UpdateLangMenuState();
        UpdateUnitMenuState();

        InitSerialFlyout();

        _ = StartupUpdateCheckAsync();
    }

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
        if (PlaybackBar is not null)
            ScaleFontsInTree(PlaybackBar, _zoomLevel);
        if (StatusBar is not null)
            ScaleFontsInTree(StatusBar, _zoomLevel);
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

    // ── Unit menu ─────────────────────────────────────────────────────────
    private void UpdateUnitMenuState()
    {
        UnitTempC.IsChecked = ViewModel.TemperatureUnit == "C";
        UnitTempF.IsChecked = ViewModel.TemperatureUnit == "F";
        UnitVoltageV.IsChecked = ViewModel.VoltageUnit == "V";
        UnitVoltageMv.IsChecked = ViewModel.VoltageUnit == "mV";
        UnitCapacityMah.IsChecked = ViewModel.CapacityUnit == "mAh";
        UnitCapacityAh.IsChecked = ViewModel.CapacityUnit == "Ah";
    }

    private void UnitTemperature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string unit)
            return;

        ViewModel.SetTemperatureUnit(unit);
        UpdateUnitMenuState();
    }

    private void UnitVoltage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string unit)
            return;

        ViewModel.SetVoltageUnit(unit);
        UpdateUnitMenuState();
    }

    private void UnitCapacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioMenuFlyoutItem item || item.Tag is not string unit)
            return;

        ViewModel.SetCapacityUnit(unit);
        UpdateUnitMenuState();
    }

    // ── Navigation ────────────────────────────────────────────────────────
    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        // Apply persisted nav-item visibility, then select the first visible.
        ApplyNavVisibilityFromSettings();
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
        (NavAI,        ViewNavAI),
    ];

    private void ApplyNavVisibilityFromSettings()
    {
        var s = AppSettingsService.Load();
        ApplyNavVisibility(NavDashboard, ViewNavDashboard, s.ShowNav_Dashboard);
        ApplyNavVisibility(NavParameter, ViewNavParameter, s.ShowNav_Parameter);
        ApplyNavVisibility(NavLiveView,  ViewNavLiveView,  s.ShowNav_LiveView);
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
                NavView.SelectedItem = nav;
                return;
            }
        }
        if (_pages.TryGetValue(tag, out var pageType))
            ContentFrame.Navigate(pageType);
    }

    // ── Login ─────────────────────────────────────────────────────────────
    private void LoginSubmit_Click(object sender, RoutedEventArgs e)
    {
        string user = LoginUsernameBox.Text.Trim();
        string pass = LoginPasswordBox.Password;

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            SetLoginError(Lang.Login_ErrorEmpty);
            return;
        }

        if (user == "admin" && pass == "admin")
        {
            _loggedInUser = user;
            LoginOverlay.Visibility = Visibility.Collapsed;
            LoginErrorText.Visibility = Visibility.Collapsed;
        }
        else
        {
            SetLoginError(Lang.Login_ErrorInvalid);
            LoginPasswordBox.Password = "";
            LoginPasswordBox.Focus(FocusState.Programmatic);
        }
    }

    private void SetLoginError(string message)
    {
        LoginErrorText.Text = message;
        LoginErrorText.Visibility = Visibility.Visible;
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

    private void AccountFlyout_Opening(object sender, object e)
    {
        AccountUsernameText.Text = _loggedInUser;
    }

    private void LogoutBtn_Click(object sender, RoutedEventArgs e)
    {
        AccountFlyout.Hide();
        _loggedInUser = "";
        LoginUsernameBox.Text     = "";
        LoginPasswordBox.Password = "";
        LoginErrorText.Visibility = Visibility.Collapsed;
        LoginOverlay.Visibility   = Visibility.Visible;
        LoginUsernameBox.Focus(FocusState.Programmatic);
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

        var info = await Services.UpdateService.CheckAsync(AppVersion);
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
        var info = await Services.UpdateService.CheckAsync(AppVersion);

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
            new Uri("https://github.com/Khlfnalvr/TLIGDashboard/"));
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

        string url = "https://github.com/Khlfnalvr/TLIGDashboard/issues/new" +
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
            "Hubungkan ESP32 lewat tombol Serial atau halaman Control Panel.",
            "Connect ESP32 from the Serial button or Control Panel."), panelWidth));
        panel.Children.Add(BuildTourFlowStep("2", TourText(
            "Pantau kondisi pack di Dashboard, lalu buka Cell View untuk detail tiap sel.",
            "Watch pack condition on Dashboard, then open Cell View for per-cell detail."), panelWidth));
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

    private static readonly Dictionary<string, (string Ms, string Nl, string Zh)> TourTranslations = new()
    {
        ["TLIG Dashboard Tour"] = ("Tour TLIG Dashboard", "TLIG Dashboard rondleiding", "TLIG Dashboard 导览"),
        ["Open Control Panel"] = ("Buka Panel Kawalan", "Configuratiescherm openen", "打开控制面板"),
        ["Close"] = ("Tutup", "Sluiten", "关闭"),
        ["MAIN PAGES"] = ("HALAMAN UTAMA", "HOOFDPAGINA'S", "主要页面"),
        ["Monitor pack voltage, SOC, current, pack status, cell summaries, and the main history charts."] = ("Pantau voltan pek, SOC, arus, status pek, ringkasan sel, dan carta sejarah utama.", "Bewaak pakketspanning, SOC, stroom, pakketstatus, celoverzichten en de belangrijkste geschiedenisgrafieken.", "监控电池组电压、SOC、电流、电池组状态、电芯摘要和主要历史图表。"),
        ["Inspect 20 cells and 10 NTC sensors. Click a cell or sensor to open its history chart."] = ("Periksa 20 sel dan 10 sensor NTC. Klik sel atau sensor untuk membuka carta sejarahnya.", "Inspecteer 20 cellen en 10 NTC-sensoren. Klik op een cel of sensor om de geschiedenisgrafiek te openen.", "查看 20 个电芯和 10 个 NTC 传感器。点击电芯或传感器可打开其历史图表。"),
        ["Configure COM port, baud rate, auto-connect, battery capacity, protection thresholds, current limits, and balancing."] = ("Konfigurasikan port COM, kadar baud, auto-sambung, kapasiti bateri, ambang perlindungan, had arus, dan pengimbangan.", "Configureer COM-poort, baudrate, automatisch verbinden, batterijcapaciteit, beveiligingsdrempels, stroomlimieten en balanceren.", "配置 COM 端口、波特率、自动连接、电池容量、保护阈值、电流限制和均衡。"),
        ["Record live data to CSV, TSV, Excel, or JSON, choose the output folder, and watch the latest 20 frames."] = ("Rakam data langsung ke CSV, TSV, Excel, atau JSON, pilih folder output, dan pantau 20 bingkai terkini.", "Neem live data op naar CSV, TSV, Excel of JSON, kies de uitvoermap en bekijk de nieuwste 20 frames.", "将实时数据记录为 CSV、TSV、Excel 或 JSON，选择输出文件夹，并查看最新 20 帧。"),
        ["Load a CSV log and replay it. Every page updates as if live data were coming in."] = ("Muat log CSV dan mainkan semula. Semua halaman dikemas kini seolah-olah data langsung sedang masuk.", "Laad een CSV-log en speel deze af. Elke pagina werkt bij alsof er live data binnenkomt.", "加载 CSV 日志并回放。所有页面都会像接收实时数据一样更新。"),
        ["TITLE BAR QUICK BUTTONS"] = ("BUTANG PANTAS BAR TAJUK", "SNELKNOPPEN IN TITELBALK", "标题栏快捷按钮"),
        ["Alerts"] = ("Amaran", "Meldingen", "警报"),
        ["The bell opens alert history, and the red badge shows the number of unread alerts."] = ("Ikon loceng membuka sejarah amaran, dan lencana merah menunjukkan bilangan amaran belum dibaca.", "De bel opent de meldingengeschiedenis en de rode badge toont het aantal ongelezen meldingen.", "铃铛会打开警报历史，红色徽标显示未读警报数量。"),
        ["Serial"] = ("Siri", "Serieel", "串口"),
        ["Quick access to refresh ports, choose COM and baud rate, then connect or disconnect without changing pages."] = ("Akses pantas untuk menyegar semula port, memilih COM dan kadar baud, kemudian sambung atau putuskan tanpa menukar halaman.", "Snelle toegang om poorten te vernieuwen, COM en baudrate te kiezen en te verbinden of los te koppelen zonder van pagina te wisselen.", "快速刷新端口、选择 COM 和波特率，然后无需切换页面即可连接或断开。"),
        ["Language"] = ("Bahasa", "Taal", "语言"),
        ["Switch the application language directly from the title bar."] = ("Tukar bahasa aplikasi terus dari bar tajuk.", "Wijzig de applicatietaal direct vanuit de titelbalk.", "直接从标题栏切换应用语言。"),
        ["Theme"] = ("Tema", "Thema", "主题"),
        ["Switch between light and dark mode for the current workspace."] = ("Beralih antara mod cerah dan gelap untuk ruang kerja semasa.", "Schakel tussen lichte en donkere modus voor de huidige werkruimte.", "为当前工作区切换浅色和深色模式。"),
        ["MENUS, PLAYBACK, AND STATUS"] = ("MENU, MAIN BALIK, DAN STATUS", "MENU'S, AFSPELEN EN STATUS", "菜单、回放和状态"),
        ["Show or hide pages in the navigation bar to keep the workspace focused."] = ("Tunjuk atau sembunyikan halaman dalam bar navigasi supaya ruang kerja kekal ringkas.", "Toon of verberg pagina's in de navigatiebalk om de werkruimte overzichtelijk te houden.", "在导航栏中显示或隐藏页面，让工作区更聚焦。"),
        ["Choose the temperature, voltage, and capacity units that are easiest to read."] = ("Pilih unit suhu, voltan, dan kapasiti yang paling mudah dibaca.", "Kies de temperatuur-, spannings- en capaciteitseenheden die het prettigst leesbaar zijn.", "选择最便于读取的温度、电压和容量单位。"),
        ["View product name, app version, license, and other basic information."] = ("Lihat nama produk, versi aplikasi, lesen, dan maklumat asas lain.", "Bekijk productnaam, appversie, licentie en andere basisinformatie.", "查看产品名称、应用版本、许可证和其他基本信息。"),
        ["Open this feature guide anytime from the title bar menu."] = ("Buka panduan fitur ini bila-bila masa dari menu bar tajuk.", "Open deze functiegids op elk moment vanuit het titelbalkmenu.", "可随时从标题栏菜单打开此功能指南。"),
        ["Reload the active page when charts, dropdowns, or visual state need a refresh."] = ("Muat semula halaman aktif apabila carta, senarai dropdown, atau keadaan visual perlu disegarkan.", "Herlaad de actieve pagina wanneer grafieken, keuzelijsten of visuele status moeten worden vernieuwd.", "当图表、下拉框或视觉状态需要刷新时，重新加载当前页面。"),
        ["Playback bar"] = ("Bar main balik", "Afspeelbalk", "回放栏"),
        ["When a log is loaded, use first, play or pause, last, the frame slider, and unload to return to live mode."] = ("Apabila log dimuat, gunakan pertama, main atau jeda, terakhir, slider bingkai, dan nyahmuat untuk kembali ke mod langsung.", "Wanneer een log is geladen, gebruik je eerste, afspelen of pauzeren, laatste, de frameschuif en ontladen om terug te keren naar live modus.", "加载日志后，可使用首帧、播放或暂停、末帧、帧滑块和卸载返回实时模式。"),
        ["Status bar"] = ("Bar status", "Statusbalk", "状态栏"),
        ["The bottom bar shows data source, connection status, and the app clock."] = ("Bar bawah memaparkan sumber data, status sambungan, dan jam aplikasi.", "De onderste balk toont gegevensbron, verbindingsstatus en de appklok.", "底部栏显示数据源、连接状态和应用时钟。"),
        ["Get familiar with the workspace"] = ("Kenali ruang kerja utama", "Maak kennis met de werkruimte", "熟悉工作区"),
        ["This guide summarizes pages, buttons, menus, playback, and the status bar so new users know where to start."] = ("Panduan ini merangkum halaman, butang, menu, main balik, dan bar status supaya pengguna baharu tahu tempat untuk bermula.", "Deze gids vat pagina's, knoppen, menu's, afspelen en de statusbalk samen zodat nieuwe gebruikers weten waar ze moeten beginnen.", "本指南概述页面、按钮、菜单、回放和状态栏，帮助新用户快速上手。"),
        ["RECOMMENDED FLOW"] = ("ALIRAN DISARANKAN", "AANBEVOLEN WERKWIJZE", "推荐流程"),
        ["Connect ESP32 from the Serial button or Control Panel."] = ("Sambungkan ESP32 melalui butang Siri atau Panel Kawalan.", "Verbind de ESP32 via de knop Serieel of het Configuratiescherm.", "通过串口按钮或控制面板连接 ESP32。"),
        ["Watch pack condition on Dashboard, then open Cell View for per-cell detail."] = ("Pantau keadaan pek pada Dashboard, kemudian buka Paparan Sel untuk butiran setiap sel.", "Bekijk de pakketconditie op het Dashboard en open daarna Celweergave voor details per cel.", "在仪表盘查看电池组状态，然后打开电芯视图查看每个电芯的详情。"),
        ["Use Logging to record test sessions and Playback for later analysis."] = ("Gunakan Logging untuk merakam sesi ujian dan Main Balik untuk analisis kemudian.", "Gebruik Logging om testsessies op te nemen en Afspelen voor latere analyse.", "使用日志记录测试会话，并通过回放进行后续分析。")
    };

    private string TourText(string id, string en)
    {
        if (Lang.CurrentLanguage == "id") return id;
        if (!TourTranslations.TryGetValue(en, out var translated)) return en;

        return Lang.CurrentLanguage switch
        {
            "ms" => translated.Ms,
            "nl" => translated.Nl,
            "zh" => translated.Zh,
            _    => en
        };
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

    // ── Playback bar ──────────────────────────────────────────────────────
    private void OnPlaybackStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var pb = ViewModel.Playback;
            PlaybackBar.Visibility = pb.IsLoaded
                ? Microsoft.UI.Xaml.Visibility.Visible
                : Microsoft.UI.Xaml.Visibility.Collapsed;

            if (!pb.IsLoaded) return;

            PbFileText.Text    = pb.FileName;
            PbFrameText.Text   = $"{pb.CurrentFrame + 1} / {pb.TotalFrames}  ·  {pb.CurrentTimestamp}";
            // E769 = Play, E103 = Pause  (Segoe MDL2 Assets)
            PbPlayPauseIcon.Glyph = pb.IsPlaying ? "" : "";

            _pbSeeking = true;
            PbSlider.Maximum = Math.Max(1, pb.TotalFrames - 1);
            PbSlider.Value   = pb.CurrentFrame;
            _pbSeeking = false;
        });
    }

    private void PbPlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Playback.IsPlaying) ViewModel.Playback.Pause();
        else                              ViewModel.Playback.Play();
    }

    private void PbFirst_Click(object sender, RoutedEventArgs e)
        => ViewModel.Playback.SeekTo(0);

    private void PbLast_Click(object sender, RoutedEventArgs e)
        => ViewModel.Playback.SeekTo(ViewModel.Playback.TotalFrames - 1);

    private void PbClose_Click(object sender, RoutedEventArgs e)
        => ViewModel.Playback.Unload();

    private void PbSlider_ValueChanged(object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_pbSeeking) return;
        ViewModel.Playback.SeekTo((int)Math.Round(e.NewValue));
    }

    // ── Caption-bar serial picker ────────────────────────────────────────
    private void InitSerialFlyout()
    {
        PopulateCapBauds();
        RefreshCapChannels();
        RefreshSerialButtonTooltip();
        UpdateSerialStatusDot();

        // Default tab: Serial. SelectorBar.SelectedItem must be assigned
        // explicitly — leaving it null fires no SelectionChanged on open.
        ConnTabs.SelectedItem = ConnTabSerial;
        ApplyConnTab();

        // Mirror connection status into the flyout text + the status dot.
        ViewModel.Serial.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            CapConnStatus.Text = msg;
            SyncCapConnectButton();
            UpdateSerialStatusDot();
        });

        // Bluetooth-tab live wiring.
        ViewModel.Bluetooth.StatusChanged += msg => DispatcherQueue.TryEnqueue(() =>
        {
            CapBtStatus.Text = msg;
            SyncCapBtConnectButton();
            UpdateSerialStatusDot();
        });
        ViewModel.Bluetooth.DevicesChanged += () => DispatcherQueue.TryEnqueue(RefreshCapBtDevices);

        // Re-sync whenever the flyout opens so the dropdowns reflect live
        // state even if the user toggled connection from the Control Panel.
        SerialFlyout.Opening += (_, _) =>
        {
            RefreshCapChannels();
            RefreshCapBtDevices();
            SyncCapConnectButton();
            SyncCapBtConnectButton();
        };
    }

    private void ConnTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        ApplyConnTab();
    }

    private void ApplyConnTab()
    {
        if (CapSerialPanel is null || CapBtPanel is null) return;
        bool serial = ConnTabs.SelectedItem == ConnTabSerial || ConnTabs.SelectedItem is null;
        CapSerialPanel.Visibility = serial ? Visibility.Visible : Visibility.Collapsed;
        CapBtPanel.Visibility     = serial ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshCapBtDevices()
    {
        if (CapBtDevice is null) return;
        var previous = (CapBtDevice.SelectedItem as ComboBoxItem)?.Tag as BluetoothDeviceInfo;
        CapBtDevice.Items.Clear();
        foreach (var d in ViewModel.Bluetooth.Devices)
            CapBtDevice.Items.Add(new ComboBoxItem { Content = d.DisplayName, Tag = d, FontSize = 12 });

        // Prefer the live device, then the previous selection, then the
        // last paired device from settings.
        string preferredId = ViewModel.Bluetooth.IsConnected
            ? ViewModel.Bluetooth.DeviceId
            : previous?.DeviceId ?? AppSettingsService.Load().LastBluetoothDeviceId;

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            for (int i = 0; i < CapBtDevice.Items.Count; i++)
            {
                if (CapBtDevice.Items[i] is ComboBoxItem it &&
                    it.Tag is BluetoothDeviceInfo c &&
                    c.DeviceId == preferredId)
                {
                    CapBtDevice.SelectedIndex = i;
                    return;
                }
            }
        }
        if (CapBtDevice.SelectedIndex < 0 && CapBtDevice.Items.Count > 0)
            CapBtDevice.SelectedIndex = 0;
    }

    private void SyncCapBtConnectButton()
    {
        if (CapBtConnectBtn is null) return;
        bool connected = ViewModel.Bluetooth.IsConnected;
        bool scanning  = ViewModel.Bluetooth.IsScanning;
        CapBtConnectBtn.Content = connected ? Lang.Ctrl_BtDisconnect : Lang.Ctrl_BtConnect;
        CapBtDevice.IsEnabled   = !connected;
        ToolTipService.SetToolTip(CapBtScanBtn,
            scanning ? Lang.Ctrl_BtStopScan : Lang.Ctrl_BtScan);
        CapBtStatus.Text = connected
            ? Lang.Format("Bt_StatusConnected", ViewModel.Bluetooth.DeviceName)
            : Lang.Ctrl_NotConnected;
    }

    private void CapBtScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Bluetooth.IsScanning) ViewModel.Bluetooth.StopScan();
        else                                ViewModel.Bluetooth.StartScan();
        SyncCapBtConnectButton();
    }

    private async void CapBtConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Bluetooth.IsConnected)
        {
            ViewModel.Bluetooth.Disconnect();
            return;
        }

        if (CapBtDevice.SelectedItem is not ComboBoxItem item ||
            item.Tag is not BluetoothDeviceInfo device)
        {
            CapBtStatus.Text = Lang.Get("Bt_FbSelectMsg");
            return;
        }

        // Hand-off from USB: drop the existing serial link and freeze the
        // scanner so the source label stays honest while BT is the source.
        ViewModel.AutoConnect.SuspendReconnect();
        if (ViewModel.Serial.IsConnected) ViewModel.Serial.Disconnect();

        bool ok = await ViewModel.Bluetooth.ConnectAsync(device);
        if (ok)
        {
            var s = AppSettingsService.Load();
            s.LastBluetoothDeviceId   = device.DeviceId;
            s.LastBluetoothDeviceName = device.DisplayName;
            AppSettingsService.Save(s);
        }
        SyncCapBtConnectButton();
    }

    private void RefreshSerialButtonTooltip()
    {
        if (SerialBtn is null) return;
        ToolTipService.SetToolTip(SerialBtn, Lang.Ui_SerialQuickAccess);
    }

    private void UpdateSerialStatusDot()
    {
        if (SerialStatusDot is null) return;
        // Green for serial, blue for Bluetooth, dimmed when idle. Picking
        // distinct hues makes it obvious at a glance which transport is live.
        bool serial = ViewModel.Serial.IsConnected;
        bool bt     = ViewModel.Bluetooth.IsConnected;
        SerialStatusDot.Fill = serial
            ? new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xC6, 0x85))     // green
            : bt
                ? new SolidColorBrush(Color.FromArgb(0xFF, 0x3D, 0x9C, 0xFD))  // sky blue
                : new SolidColorBrush(Color.FromArgb(0xCC, 0x9E, 0x9E, 0x9E));   // grey
    }

    private void PopulateCapBauds()
    {
        CapSerialBaud.Items.Clear();
        foreach (var b in ViewModel.Serial.Bitrates)
            CapSerialBaud.Items.Add(new ComboBoxItem { Content = b.DisplayName, Tag = b });

        int defBaud = ViewModel.Serial.DefaultBitrate;
        for (int i = 0; i < CapSerialBaud.Items.Count; i++)
        {
            if (CapSerialBaud.Items[i] is ComboBoxItem item &&
                item.Tag is SerialBaud br &&
                br.Baud == defBaud)
            {
                CapSerialBaud.SelectedIndex = i;
                return;
            }
        }
        if (CapSerialBaud.SelectedIndex < 0 && CapSerialBaud.Items.Count > 0)
            CapSerialBaud.SelectedIndex = 0;
    }

    private void RefreshCapChannels()
    {
        var previous = (CapSerialPort.SelectedItem as ComboBoxItem)?.Tag as SerialPortInfo;
        CapSerialPort.Items.Clear();

        if (!ViewModel.Serial.IsDriverAvailable)
        {
            CapSerialPort.PlaceholderText = Lang.Ctrl_PhNoPorts;
            return;
        }

        foreach (var ch in ViewModel.Serial.Channels)
            CapSerialPort.Items.Add(new ComboBoxItem { Content = ch.DisplayName, Tag = ch });

        CapSerialPort.PlaceholderText = Lang.Ctrl_PhScanning;

        // Prefer the live channel — fall back to whatever the user picked last,
        // then default to the first entry.
        string live = ViewModel.Serial.Channel;     // "" when not connected
        for (int i = 0; i < CapSerialPort.Items.Count; i++)
        {
            if (CapSerialPort.Items[i] is ComboBoxItem it &&
                it.Tag is SerialPortInfo c &&
                (string.Equals(c.PortName, live, StringComparison.OrdinalIgnoreCase)
                 || (string.IsNullOrEmpty(live) && previous != null
                     && string.Equals(c.PortName, previous.PortName, StringComparison.OrdinalIgnoreCase))))
            {
                CapSerialPort.SelectedIndex = i;
                return;
            }
        }
        if (CapSerialPort.Items.Count > 0)
            CapSerialPort.SelectedIndex = 0;
    }

    private void SyncCapConnectButton()
    {
        if (CapConnectBtn is null) return;
        bool connected = ViewModel.Serial.IsConnected;
        CapConnectBtn.Content   = connected ? Lang.Ctrl_Disconnect : Lang.Ctrl_Connect;
        CapSerialPort.IsEnabled = !connected;
        CapSerialBaud.IsEnabled = !connected;
        CapConnStatus.Text = connected
            ? Lang.Format("Serial_StatusConnected", ViewModel.Serial.ChannelName, ViewModel.Serial.Bitrate)
            : Lang.Ctrl_NotConnected;
    }

    private void CapRefresh_Click(object sender, RoutedEventArgs e) => RefreshCapChannels();

    private void CapConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Serial.IsConnected)
        {
            ViewModel.AutoConnect.SuspendReconnect();
            ViewModel.Serial.Disconnect();
            return;
        }

        if (CapSerialPort.SelectedItem is not ComboBoxItem chItem ||
            chItem.Tag is not SerialPortInfo channel)
        {
            CapConnStatus.Text = Lang.Fb_SelectChannelMsg;
            return;
        }

        if (CapSerialBaud.SelectedItem is not ComboBoxItem brItem ||
            brItem.Tag is not SerialBaud bitrate)
        {
            CapConnStatus.Text = Lang.Fb_SelectChannelMsg;
            return;
        }

        ViewModel.AutoConnect.Baud = bitrate.Baud;
        ViewModel.AutoConnect.ResumeReconnect();
        ViewModel.Serial.Connect(channel, bitrate);
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
