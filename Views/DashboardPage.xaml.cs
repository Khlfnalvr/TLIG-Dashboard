using System.Reflection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TLIGDashboard.Helpers;
using TLIGDashboard.Services;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.UI;

namespace TLIGDashboard.Views;

public sealed partial class DashboardPage : Page
{
    private LocalizationManager Lang => App.Lang;
    private SystemStatusService Status => App.Status;

    // Shared AI service — same instance as AIPage
    private AiService _ai => App.Ai;
    private CancellationTokenSource? _chatCts;

    private readonly bool _clientMode = BuildInfo.IsClient;

    private readonly SemaphoreSlim _dashboardCameraSwitchLock = new(1, 1);
    private DeviceInformationCollection? _dashboardCameraDevices;
    private MediaCapture? _dashboardMediaCapture;
    private MediaFrameSource? _dashboardFrameSource;
    private MediaPlayer? _dashboardMediaPlayer;
    private bool _isDashboardCameraPopulating;
    private bool _isDashboardPageActive;

    private bool _dragging1, _dragging2;
    private double _dragStartX;
    private double _leftStartW, _centerStartW, _rightStartW;

    private double _ratioL = 0.31, _ratioC = 0.40, _ratioR = 0.29;

    // Horizontal splitter inside center panel
    private bool _draggingH;
    private double _dragStartY;
    private double _topStartH, _bottomStartH;
    private double _ratioTop = 0.55, _ratioBottom = 0.45;

    public DashboardPage()
    {
        InitializeComponent();
        // Keep page cached so layout & chat bubbles survive navigation
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnLoaded;
    }

    // How many history entries are already rendered in ChatPanel.
    private int _renderedCount;
    private ElementTheme _renderedTheme = ElementTheme.Default;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var cursorH = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        SetCursor(Splitter1, cursorH);
        SetCursor(Splitter2, cursorH);
        var cursorV = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        SetCursor(HSplitter, cursorV);

        double total = AvailableWidth;
        if (total > 0)
        {
            double left = Math.Floor(total * _ratioL);
            double center = Math.Floor(total * _ratioC);
            SetColumnWidths(left, center, total - left - center);
        }

        ActualThemeChanged += OnActualThemeChanged;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_chatCts != null) return; // don't disrupt active streaming
        ClearChatPanel();
        SyncBubblesWithHistory();
    }

    protected override async void OnNavigatedTo(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _isDashboardPageActive = true;
        // If theme changed while this page was not in the visual tree, force full re-render.
        if (_renderedCount > 0 && _renderedTheme != ActualTheme)
            ClearChatPanel();
        SyncBubblesWithHistory();
        _ = ModelPicker.ReloadAsync();

        if (_clientMode)
            EnterDashboardCameraClientMode();
        else
            await PopulateDashboardCameraListAsync();
    }

    protected override async void OnNavigatedFrom(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _isDashboardPageActive = false;

        if (_clientMode)
        {
            ShareClient.Instance.FrameReceived -= OnRemoteDashboardCameraFrame;
            return;
        }

        await _dashboardCameraSwitchLock.WaitAsync();
        try
        {
            StopDashboardCameraPreview(updateUi: false);
        }
        finally
        {
            _dashboardCameraSwitchLock.Release();
        }
    }

    // ── Client mode: render camera frames received from the server ────────────

    private void EnterDashboardCameraClientMode()
    {
        DashboardCameraSelector.Visibility = Visibility.Collapsed;
        DashboardCameraInfoText.Text = "-";
        if (DashboardCameraReceiveImage.Source is null)
            ShowDashboardCameraPlaceholder(Lang.Hmi_WaitingStream);

        ShareClient.Instance.FrameReceived -= OnRemoteDashboardCameraFrame;
        ShareClient.Instance.FrameReceived += OnRemoteDashboardCameraFrame;
    }

    private void OnRemoteDashboardCameraFrame(byte channel, byte[] bytes)
    {
        if (channel != ShareProtocol.ChannelCamera) return;
        DispatcherQueue.TryEnqueue(async () => await RenderRemoteDashboardCameraAsync(bytes));
    }

    private async Task RenderRemoteDashboardCameraAsync(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bitmap.SetSourceAsync(stream);
            DashboardCameraReceiveImage.Source = bitmap;
            DashboardCameraReceiveImage.Visibility = Visibility.Visible;
            DashboardCameraPlaceholder.Visibility = Visibility.Collapsed;
            App.Status.CameraConnected = true;
        }
        catch { /* drop a bad frame */ }
    }

    /// <summary>Appends any history not yet shown in the Dashboard chat panel.</summary>
    private void SyncBubblesWithHistory()
    {
        var history = App.Ai.History;
        for (int i = _renderedCount; i < history.Count; i++)
        {
            var msg = history[i];
            if (msg.Role == "user")
            {
                AddChatBubble("user", msg.Content);
            }
            else if (msg.Role == "assistant")
            {
                var (border, _) = AddChatBubble("ai", msg.Content);
                border.Child = MarkdownRenderer.Render(
                    msg.Content, 12, ActualTheme == ElementTheme.Dark);
            }
        }
        _renderedCount = history.Count;
        _renderedTheme = ActualTheme;
    }

    /// <summary>Called by AIPage clear button to reset this panel too.</summary>
    public void ClearChatPanel()
    {
        while (ChatPanel.Children.Count > 0)
            ChatPanel.Children.RemoveAt(ChatPanel.Children.Count - 1);
        _renderedCount = 0;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double total = AvailableWidth;
        if (total <= 0) return;

        double minL = total * 0.25, minC = total * 0.32, minR = total * 0.23;
        double L = Math.Max(_ratioL * total, minL);
        double R = Math.Max(_ratioR * total, minR);
        double C = total - L - R;

        if (C < minC)
        {
            C = minC;
            double lrTotal = L + R;
            if (lrTotal > total - minC)
            {
                double excess = lrTotal - (total - minC);
                L = Math.Max(minL, L - excess * (L / lrTotal));
                R = Math.Max(minR, R - excess * (R / lrTotal));
            }
        }

        LeftColumn.Width   = new GridLength(L, GridUnitType.Pixel);
        CenterColumn.Width = new GridLength(C, GridUnitType.Pixel);
        RightColumn.Width  = new GridLength(R, GridUnitType.Pixel);

        // Re-apply vertical split ratio whenever the center panel height changes
        ApplyCenterRowHeights();
    }

    private double AvailableWidth =>
        RootGrid.ActualWidth > 8 ? RootGrid.ActualWidth - 8 : 0;

    // ── Horizontal splitter (top/bottom inside CenterPanel) ─────────────

    private double CenterAvailableH =>
        CenterPanel.ActualHeight > 38 ? CenterPanel.ActualHeight - 4 - 30 : 0; // subtract splitter(4) + header(30)

    private void ApplyCenterRowHeights()
    {
        double avail = CenterAvailableH;
        if (avail <= 0) return;
        double minTop    = avail * 0.20;
        double minBottom = avail * 0.15;
        double top    = Math.Max(_ratioTop    * avail, minTop);
        double bottom = Math.Max(_ratioBottom * avail, minBottom);
        double sum = top + bottom;
        if (sum > 0) { top = top / sum * avail; bottom = avail - top; }
        CenterTopRow.Height    = new GridLength(top,    GridUnitType.Pixel);
        CenterBottomRow.Height = new GridLength(bottom, GridUnitType.Pixel);
    }

    private void SetCenterRowHeights(double top, double bottom)
    {
        double sum = top + bottom;
        if (sum > 0) { _ratioTop = top / sum; _ratioBottom = bottom / sum; }
        CenterTopRow.Height    = new GridLength(top,    GridUnitType.Pixel);
        CenterBottomRow.Height = new GridLength(bottom, GridUnitType.Pixel);
    }

    private void HSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(HSplitter).Properties.IsLeftButtonPressed) return;
        _draggingH    = true;
        _dragStartY   = e.GetCurrentPoint(CenterPanel).Position.Y;
        _topStartH    = CenterTopRow.ActualHeight;
        _bottomStartH = CenterBottomRow.ActualHeight;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void HSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingH) return;
        double delta = e.GetCurrentPoint(CenterPanel).Position.Y - _dragStartY;
        ApplyHSplitter(delta);
        e.Handled = true;
    }

    private void HSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingH = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void HSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _draggingH = false;

    private void ApplyHSplitter(double delta)
    {
        double avail = CenterAvailableH;
        if (avail <= 0) return;
        double minTop    = avail * 0.20;
        double minBottom = avail * 0.15;

        double top    = Math.Clamp(_topStartH    + delta, minTop,    avail - minBottom);
        double bottom = Math.Clamp(_bottomStartH - delta, minBottom, avail - minTop);
        // Correct floating-point drift
        if (top + bottom != avail) bottom = avail - top;
        SetCenterRowHeights(top, bottom);
    }

    private void SetColumnWidths(double L, double C, double R)
    {
        double sum = L + C + R;
        if (sum > 0) { _ratioL = L / sum; _ratioC = C / sum; _ratioR = R / sum; }
        LeftColumn.Width   = new GridLength(L, GridUnitType.Pixel);
        CenterColumn.Width = new GridLength(C, GridUnitType.Pixel);
        RightColumn.Width  = new GridLength(R, GridUnitType.Pixel);
    }

    // ── Splitter 1 (between Left and Center) ────────────────────────────
    private void Splitter1_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Splitter1).Properties.IsLeftButtonPressed) return;
        _dragging1    = true;
        _dragStartX   = e.GetCurrentPoint(RootGrid).Position.X;
        _leftStartW   = LeftPanel.ActualWidth;
        _centerStartW = CenterPanel.ActualWidth;
        _rightStartW  = RightPanel.ActualWidth;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter1_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging1) return;
        ApplySplitter1(e.GetCurrentPoint(RootGrid).Position.X - _dragStartX);
        e.Handled = true;
    }

    private void Splitter1_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging1 = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Splitter1_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _dragging1 = false;

    private void ApplySplitter1(double delta)
    {
        double total = AvailableWidth;
        if (total <= 0) return;
        double minL = total * 0.25, minC = total * 0.32, minR = total * 0.23;

        double L = _leftStartW + delta;

        if (delta <= 0)
        {
            // Drag left: left shrinks → only center grows, right fixed
            L = Math.Max(L, minL);
            double C = Math.Max(_centerStartW + (_leftStartW - L), minC);
            double R = _rightStartW;
            C = Math.Min(C, total - L - R);
            if (C < minC) C = minC;
            L = total - C - R;
            SetColumnWidths(L, C, R);
        }
        else
        {
            // Drag right: left grows → center + right shrink proportionally
            L = Math.Min(L, total - minC - minR);
            double actualGain = L - _leftStartW;
            double crSum = _centerStartW + _rightStartW;
            if (crSum <= 0) return;
            double C = _centerStartW - actualGain * (_centerStartW / crSum);
            double R = _rightStartW  - actualGain * (_rightStartW  / crSum);
            if (C < minC) { C = minC; R = total - L - C; }
            if (R < minR) { R = minR; C = total - L - R; }
            if (C < minC) { C = minC; L = total - C - R; }
            SetColumnWidths(L, C, R);
        }
    }

    // ── Splitter 2 (between Center and Right) ───────────────────────────
    private void Splitter2_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Splitter2).Properties.IsLeftButtonPressed) return;
        _dragging2    = true;
        _dragStartX   = e.GetCurrentPoint(RootGrid).Position.X;
        _leftStartW   = LeftPanel.ActualWidth;
        _centerStartW = CenterPanel.ActualWidth;
        _rightStartW  = RightPanel.ActualWidth;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void Splitter2_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging2) return;
        ApplySplitter2(e.GetCurrentPoint(RootGrid).Position.X - _dragStartX);
        e.Handled = true;
    }

    private void Splitter2_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging2 = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void Splitter2_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _dragging2 = false;

    private void ApplySplitter2(double delta)
    {
        double total = AvailableWidth;
        if (total <= 0) return;
        double minL = total * 0.25, minC = total * 0.32, minR = total * 0.23;

        // delta > 0: splitter moved right → right shrinks
        // delta < 0: splitter moved left  → right grows
        double R = _rightStartW - delta;

        if (delta >= 0)
        {
            // Drag right: right shrinks → only center grows, left fixed
            R = Math.Max(R, minR);
            double C = Math.Max(_centerStartW + (_rightStartW - R), minC);
            double L = _leftStartW;
            C = Math.Min(C, total - L - R);
            if (C < minC) C = minC;
            R = total - L - C;
            SetColumnWidths(L, C, R);
        }
        else
        {
            // Drag left: right grows → left + center shrink proportionally
            R = Math.Min(R, total - minL - minC);
            double actualGain = R - _rightStartW;
            double lcSum = _leftStartW + _centerStartW;
            if (lcSum <= 0) return;
            double L = _leftStartW   - actualGain * (_leftStartW   / lcSum);
            double C = _centerStartW - actualGain * (_centerStartW / lcSum);
            if (L < minL) { L = minL; C = total - L - R; }
            if (C < minC) { C = minC; L = total - C - R; }
            if (L < minL) { L = minL; R = total - L - C; }
            SetColumnWidths(L, C, R);
        }
    }

    // ── Chat (right panel) ───────────────────────────────────────────────
    // ── Dashboard camera preview ──────────────────────────────────────────
    private async Task PopulateDashboardCameraListAsync()
    {
        _isDashboardCameraPopulating = true;
        DashboardCameraSelector.Items.Clear();
        DashboardCameraSelector.IsEnabled = false;
        ShowDashboardCameraPlaceholder(Lang.Live_Waiting);

        try
        {
            _dashboardCameraDevices = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector());

            if (_dashboardCameraDevices.Count == 0)
            {
                DashboardCameraSelector.Items.Add(Lang.Live_NoCamera);
                DashboardCameraSelector.SelectedIndex = 0;
                ShowDashboardCameraPlaceholder(Lang.Live_NoCamera);
                return;
            }

            foreach (var device in _dashboardCameraDevices)
                DashboardCameraSelector.Items.Add(device.Name);

            DashboardCameraSelector.IsEnabled = true;
            DashboardCameraSelector.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            DashboardCameraSelector.Items.Add(Lang.Live_NoCamera);
            DashboardCameraSelector.SelectedIndex = 0;
            ShowDashboardCameraPlaceholder(Lang.Format(nameof(LocalizationManager.Live_CameraError), ex.Message));
            return;
        }
        finally
        {
            _isDashboardCameraPopulating = false;
        }

        await StartSelectedDashboardCameraAsync(0);
    }

    private async void DashboardCameraSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isDashboardCameraPopulating ||
            DashboardCameraSelector.SelectedIndex < 0 ||
            _dashboardCameraDevices is null ||
            DashboardCameraSelector.SelectedIndex >= _dashboardCameraDevices.Count)
        {
            return;
        }

        await StartSelectedDashboardCameraAsync(DashboardCameraSelector.SelectedIndex);
    }

    private async Task StartSelectedDashboardCameraAsync(int cameraIndex)
    {
        if (!_isDashboardPageActive ||
            _dashboardCameraDevices is null ||
            cameraIndex < 0 ||
            cameraIndex >= _dashboardCameraDevices.Count)
        {
            return;
        }

        await _dashboardCameraSwitchLock.WaitAsync();
        try
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Live_Waiting);

            var selectedCamera = _dashboardCameraDevices[cameraIndex];
            _dashboardMediaCapture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = selectedCamera.Id,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Auto
            };

            await _dashboardMediaCapture.InitializeAsync(settings);

            _dashboardFrameSource = FindDashboardPreviewFrameSource(_dashboardMediaCapture);
            if (_dashboardFrameSource is null)
            {
                ShowDashboardCameraPlaceholder(Lang.Live_NoCamera);
                StopDashboardCameraPreview(updateUi: false);
                return;
            }

            _dashboardMediaPlayer = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false,
                Source = MediaSource.CreateFromMediaFrameSource(_dashboardFrameSource)
            };
            _dashboardMediaPlayer.MediaFailed += DashboardMediaPlayer_MediaFailed;

            DashboardCameraPreview.SetMediaPlayer(_dashboardMediaPlayer);
            _dashboardMediaPlayer.Play();

            if (!_isDashboardPageActive)
            {
                StopDashboardCameraPreview(updateUi: false);
                return;
            }

            DashboardCameraPreview.Visibility = Visibility.Visible;
            DashboardCameraPlaceholder.Visibility = Visibility.Collapsed;
            DashboardCameraInfoText.Text = FormatDashboardCameraInfo(_dashboardFrameSource);
            App.Status.CameraConnected = true;
        }
        catch (UnauthorizedAccessException)
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Live_CameraDenied);
        }
        catch (Exception ex)
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Format(nameof(LocalizationManager.Live_CameraError), ex.Message));
        }
        finally
        {
            _dashboardCameraSwitchLock.Release();
        }
    }

    private static MediaFrameSource? FindDashboardPreviewFrameSource(MediaCapture mediaCapture)
    {
        var previewSource = mediaCapture.FrameSources
            .FirstOrDefault(source =>
                source.Value.Info.MediaStreamType == MediaStreamType.VideoPreview &&
                source.Value.Info.SourceKind == MediaFrameSourceKind.Color)
            .Value;

        if (previewSource is not null)
            return previewSource;

        return mediaCapture.FrameSources
            .FirstOrDefault(source =>
                source.Value.Info.MediaStreamType == MediaStreamType.VideoRecord &&
                source.Value.Info.SourceKind == MediaFrameSourceKind.Color)
            .Value;
    }

    private static string FormatDashboardCameraInfo(MediaFrameSource frameSource)
    {
        var format = frameSource.CurrentFormat;
        double fps = 0;
        if (format.FrameRate.Denominator != 0)
            fps = (double)format.FrameRate.Numerator / format.FrameRate.Denominator;

        string fpsText = fps > 0
            ? Math.Round(fps).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "0";

        return $"{format.VideoFormat.Width}x{format.VideoFormat.Height} - {fpsText}fps";
    }

    private void StopDashboardCameraPreview(bool updateUi)
    {
        if (_dashboardMediaPlayer is not null)
        {
            _dashboardMediaPlayer.MediaFailed -= DashboardMediaPlayer_MediaFailed;
            _dashboardMediaPlayer.Pause();
            DashboardCameraPreview.SetMediaPlayer(null);
            _dashboardMediaPlayer.Dispose();
            _dashboardMediaPlayer = null;
        }

        _dashboardMediaCapture?.Dispose();
        _dashboardMediaCapture = null;
        _dashboardFrameSource = null;
        App.Status.CameraConnected = false;

        if (updateUi)
            ShowDashboardCameraPlaceholder(Lang.Live_Waiting);
    }

    private void ShowDashboardCameraPlaceholder(string message)
    {
        DashboardCameraPreview.Visibility = Visibility.Collapsed;
        DashboardCameraPlaceholder.Visibility = Visibility.Visible;
        DashboardCameraPlaceholderText.Text = message;
        DashboardCameraInfoText.Text = "-";
        App.Status.CameraConnected = false;
    }

    private void DashboardMediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            StopDashboardCameraPreview(updateUi: false);
            ShowDashboardCameraPlaceholder(Lang.Format(nameof(LocalizationManager.Live_CameraError), args.ErrorMessage));
        });
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e) => _ = SendChatAsync();

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _ = SendChatAsync();
            e.Handled = true;
        }
    }

    private void QuickSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        string? prompt = button.Tag as string ?? button.Content?.ToString();
        if (string.IsNullOrWhiteSpace(prompt)) return;

        ChatInput.Text = prompt.Trim();
        _ = SendChatAsync();
    }

    private async Task SendChatAsync()
    {
        string text = ChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Re-point the shared AI service at the active provider/model (same as AIPage).
        AiConfigService.ApplyActive(_ai);

        if (string.IsNullOrEmpty(_ai.ApiKey))
        {
            AddChatBubble("ai", Lang.Ai_ErrorNoKey);
            return;
        }

        ChatInput.Text        = "";
        ChatSendBtn.IsEnabled = false;

        AddChatBubble("user", text);
        var (aiBubbleBorder, aiBubble) = AddChatBubble("ai", Lang.Ai_Thinking);

        _chatCts = new CancellationTokenSource();
        bool hasContent = false;
        string? errorMsg = null;

        try
        {
            await Task.Run(async () =>
            {
                await _ai.StreamChatAsync(text, token =>
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!hasContent) { aiBubble.Text = ""; hasContent = true; }
                        aiBubble.Text += token;
                        ScrollChat();
                    });
                }, _chatCts.Token);
            }, _chatCts.Token);
        }
        catch (OperationCanceledException) { errorMsg = "[Dihentikan]"; }
        catch (Exception ex)               { errorMsg = $"⚠ {ex.Message}"; }

        if (errorMsg != null)
            aiBubble.Text = errorMsg;
        else if (!hasContent)
            aiBubble.Text = "⚠ Tidak ada konten — periksa model & API key.";
        else
            aiBubbleBorder.Child = MarkdownRenderer.Render(
                aiBubble.Text, 12, ActualTheme == ElementTheme.Dark);

        _chatCts?.Dispose();
        _chatCts = null;
        ChatSendBtn.IsEnabled = true;
        ScrollChat();

        // Keep rendered count in sync with history
        _renderedCount = App.Ai.History.Count;
    }

    // Returns the bubble Border and the streaming TextBlock so callers can replace content after streaming.
    private (Border border, TextBlock tb) AddChatBubble(string role, string text)
    {
        bool isUser = role == "user";
        bool isDark = ActualTheme == ElementTheme.Dark;

        var label = new TextBlock
        {
            Text             = isUser ? Lang.Get("Ai_UserLabel") : Lang.Get("Ai_AiLabel"),
            FontSize         = 10,
            FontWeight       = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 60,
            Opacity          = 0.55,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin           = new Thickness(2, 0, 2, 2)
        };

        var bg = isUser
            ? new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x00, 0x4E, 0x9B)
                : Color.FromArgb(0xFF, 0xCC, 0xE4, 0xFF))
            : new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C)
                : Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));

        var tb = new TextBlock
        {
            Text                   = text,
            FontSize               = 12,
            TextWrapping           = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };

        var bubble = new Border
        {
            Background          = bg,
            CornerRadius        = isUser ? new CornerRadius(10,10,2,10) : new CornerRadius(10,10,10,2),
            Padding             = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth            = 240,
            Child               = tb
        };

        var container = new StackPanel
        {
            Spacing             = 3,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        container.Children.Add(label);
        container.Children.Add(bubble);
        ChatPanel.Children.Add(container);
        ScrollChat();
        return (bubble, tb);
    }

    private void ScrollChat() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => ChatScroll.ChangeView(null, double.MaxValue, null, true));

    // ── Fullscreen buttons ───────────────────────────────────────────────
    private void LeftFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("Parameter");

    private void CenterFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("LiveView");

    private void LearningAnalyticFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("LearningAnalytic");

    private void RightFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("AI");

    // ── Cursor helper (ProtectedCursor is non-public in WinUI 3) ────────
    private static void SetCursor(UIElement element, InputCursor cursor)
        => typeof(UIElement)
            .GetProperty("ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(element, cursor);
}
