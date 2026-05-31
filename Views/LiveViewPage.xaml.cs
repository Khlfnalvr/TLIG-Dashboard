using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using TLIGDashboard.Services;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace TLIGDashboard.Views;

public sealed partial class LiveViewPage : Page
{
    private LocalizationManager Lang => App.Lang;

    private readonly bool _clientMode = BuildInfo.IsClient;

    private readonly SemaphoreSlim _cameraSwitchLock = new(1, 1);
    private DeviceInformationCollection? _cameraDevices;
    private MediaCapture? _mediaCapture;
    private MediaFrameSource? _frameSource;
    private MediaPlayer? _mediaPlayer;

    // Server-side broadcast: a SEPARATE capture + frame reader feeds JPEG frames to
    // ShareServer. It is independent of the MediaPlayer preview (sharing one frame
    // source between a MediaPlayer and a MediaFrameReader does not reliably deliver
    // frames), and forces CPU memory so SoftwareBitmap is always available.
    private MediaCapture? _broadcastCapture;
    private MediaFrameReader? _frameReader;
    private string? _currentCameraId;
    private long _lastCamPushMs;
    private bool _encodingFrame;

    private bool _draggingSplitter;
    private bool _isPopulatingCameras;
    private bool _isPageLoaded;
    private bool _cursorSet;
    private double _dragStartX;
    private double _liveStartW;
    private double _hmiStartW;
    private double _liveRatio = 0.5;

    public LiveViewPage()
    {
        InitializeComponent();
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void RefreshCameraStopBtn()
    {
        bool active = CameraPreview.Visibility == Visibility.Visible;
        CameraStopBtn.Content    = Lang.Live_CameraStop;
        CameraStopBtn.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void CameraStopBtn_Click(object sender, RoutedEventArgs e)
    {
        await _cameraSwitchLock.WaitAsync();
        try { StopCameraPreview(updateUi: true); }
        finally { _cameraSwitchLock.Release(); }
        CameraSelector.SelectedItem = null;
        RefreshCameraStopBtn();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = true;
        if (!_cursorSet)
        {
            SetCursor(LiveHmiSplitter, InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
            _cursorSet = true;
        }

        ApplyColumnRatio();

        if (_clientMode)
            EnterCameraClientMode();
        else if (_mediaCapture is null)
            await PopulateCameraListAsync();
        else
            RefreshCameraStopBtn(); // returning to page — camera already running
    }

    // ── Client mode: render camera frames received from the server ────────────

    private void EnterCameraClientMode()
    {
        CameraSelector.Visibility  = Visibility.Collapsed;
        CameraStopBtn.Visibility   = Visibility.Collapsed;
        CameraInfoText.Text = "-";
        if (CameraReceiveImage.Source is null)
            ShowCameraPlaceholder(Lang.Hmi_WaitingStream);

        // Re-subscribe on every load; OnUnloaded removes it. (The page is cached,
        // so a one-time hook would be dropped the first time we navigate away.)
        ShareClient.Instance.FrameReceived -= OnRemoteCameraFrame;
        ShareClient.Instance.FrameReceived += OnRemoteCameraFrame;
    }

    private void OnRemoteCameraFrame(byte channel, byte[] bytes)
    {
        if (channel != ShareProtocol.ChannelCamera) return;
        DispatcherQueue.TryEnqueue(async () => await RenderRemoteCameraAsync(bytes));
    }

    private async Task RenderRemoteCameraAsync(byte[] bytes)
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
            CameraReceiveImage.Source = bitmap;
            CameraReceiveImage.Visibility = Visibility.Visible;
            CameraPlaceholder.Visibility = Visibility.Collapsed;
            App.Status.CameraConnected = true;
        }
        catch { /* drop a bad frame */ }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isPageLoaded = false;

        if (_clientMode)
            ShareClient.Instance.FrameReceived -= OnRemoteCameraFrame;
        // Camera preview and broadcast keep running in the background.
    }

    private double AvailableWidth =>
        LiveHmiGrid.ActualWidth > 8 ? LiveHmiGrid.ActualWidth - 8 : 0;

    private void LiveHmiGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyColumnRatio();

    private void ApplyColumnRatio()
    {
        double total = AvailableWidth;
        if (total <= 0) return;

        double minPanel = Math.Min(320, total * 0.42);
        double liveWidth = Math.Clamp(total * _liveRatio, minPanel, total - minPanel);
        SetColumnWidths(liveWidth, total - liveWidth);
    }

    private void SetColumnWidths(double liveWidth, double hmiWidth)
    {
        double total = liveWidth + hmiWidth;
        if (total > 0)
            _liveRatio = liveWidth / total;

        LiveColumn.Width = new GridLength(liveWidth, GridUnitType.Pixel);
        HmiColumn.Width = new GridLength(hmiWidth, GridUnitType.Pixel);
    }

    private void LiveHmiSplitter_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(LiveHmiSplitter).Properties.IsLeftButtonPressed) return;

        _draggingSplitter = true;
        _dragStartX = e.GetCurrentPoint(LiveHmiGrid).Position.X;
        _liveStartW = LiveCameraPanel.ActualWidth;
        _hmiStartW = HmiPanel.ActualWidth;
        (sender as UIElement)?.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void LiveHmiSplitter_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_draggingSplitter) return;

        double delta = e.GetCurrentPoint(LiveHmiGrid).Position.X - _dragStartX;
        ApplySplitter(delta);
        e.Handled = true;
    }

    private void LiveHmiSplitter_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _draggingSplitter = false;
        (sender as UIElement)?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void LiveHmiSplitter_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        => _draggingSplitter = false;

    private void ApplySplitter(double delta)
    {
        double total = AvailableWidth;
        if (total <= 0) return;

        double minPanel = Math.Min(320, total * 0.42);
        double liveWidth = Math.Clamp(_liveStartW + delta, minPanel, total - minPanel);
        double hmiWidth = _liveStartW + _hmiStartW - liveWidth;

        if (hmiWidth < minPanel)
        {
            hmiWidth = minPanel;
            liveWidth = total - hmiWidth;
        }

        SetColumnWidths(liveWidth, hmiWidth);
    }

    private static void SetCursor(UIElement element, InputCursor cursor)
        => typeof(UIElement)
            .GetProperty("ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(element, cursor);

    private async Task PopulateCameraListAsync()
    {
        _isPopulatingCameras = true;
        CameraSelector.Items.Clear();
        CameraSelector.IsEnabled = false;
        ShowCameraPlaceholder(Lang.Live_Waiting);

        try
        {
            _cameraDevices = await DeviceInformation.FindAllAsync(MediaDevice.GetVideoCaptureSelector());

            if (_cameraDevices.Count == 0)
            {
                CameraSelector.Items.Add(Lang.Live_NoCamera);
                CameraSelector.SelectedIndex = 0;
                ShowCameraPlaceholder(Lang.Live_NoCamera);
                return;
            }

            foreach (var device in _cameraDevices)
                CameraSelector.Items.Add(device.Name);

            CameraSelector.IsEnabled = true;
            CameraSelector.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            CameraSelector.Items.Add(Lang.Live_NoCamera);
            CameraSelector.SelectedIndex = 0;
            ShowCameraPlaceholder(Lang.Format(nameof(Lang.Live_CameraError), ex.Message));
            return;
        }
        finally
        {
            _isPopulatingCameras = false;
        }

        await StartSelectedCameraAsync(0);
    }

    private async void CameraSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isPopulatingCameras ||
            CameraSelector.SelectedIndex < 0 ||
            _cameraDevices is null ||
            CameraSelector.SelectedIndex >= _cameraDevices.Count)
        {
            return;
        }

        await StartSelectedCameraAsync(CameraSelector.SelectedIndex);
    }

    private async Task StartSelectedCameraAsync(int cameraIndex)
    {
        if (!_isPageLoaded ||
            _cameraDevices is null ||
            cameraIndex < 0 ||
            cameraIndex >= _cameraDevices.Count)
        {
            return;
        }

        await _cameraSwitchLock.WaitAsync();
        try
        {
            StopCameraPreview(updateUi: false);
            ShowCameraPlaceholder(Lang.Live_Waiting);

            var selectedCamera = _cameraDevices[cameraIndex];
            _currentCameraId = selectedCamera.Id;
            _mediaCapture = new MediaCapture();

            var settings = new MediaCaptureInitializationSettings
            {
                VideoDeviceId = selectedCamera.Id,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Auto
            };

            await _mediaCapture.InitializeAsync(settings);

            _frameSource = FindPreviewFrameSource(_mediaCapture);
            if (_frameSource is null)
            {
                ShowCameraPlaceholder(Lang.Live_NoCamera);
                StopCameraPreview(updateUi: false);
                return;
            }

            _mediaPlayer = new MediaPlayer
            {
                RealTimePlayback = true,
                AutoPlay = false,
                Source = MediaSource.CreateFromMediaFrameSource(_frameSource)
            };
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

            CameraPreview.SetMediaPlayer(_mediaPlayer);
            _mediaPlayer.Play();

            if (!_isPageLoaded)
            {
                StopCameraPreview(updateUi: false);
                return;
            }

            CameraPreview.Visibility = Visibility.Visible;
            CameraPlaceholder.Visibility = Visibility.Collapsed;
            CameraInfoText.Text = FormatCameraInfo(_frameSource);
            App.Status.CameraConnected = true;
            RefreshCameraStopBtn();

            // Server flavor: also pull frames for broadcasting to clients.
            await StartFrameReaderAsync();
        }
        catch (UnauthorizedAccessException)
        {
            StopCameraPreview(updateUi: false);
            ShowCameraPlaceholder(Lang.Live_CameraDenied);
        }
        catch (Exception ex)
        {
            StopCameraPreview(updateUi: false);
            ShowCameraPlaceholder(Lang.Format(nameof(Lang.Live_CameraError), ex.Message));
        }
        finally
        {
            _cameraSwitchLock.Release();
        }
    }

    private static MediaFrameSource? FindPreviewFrameSource(MediaCapture mediaCapture)
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

    private static string FormatCameraInfo(MediaFrameSource frameSource)
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

    // ── Server-side camera broadcast ──────────────────────────────────────────

    private async Task StartFrameReaderAsync()
    {
        if (_clientMode || !BuildInfo.IsServer || string.IsNullOrEmpty(_currentCameraId)) return;
        try
        {
            // Dedicated capture so the broadcast never competes with the preview.
            _broadcastCapture = new MediaCapture();
            await _broadcastCapture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId        = _currentCameraId,
                SharingMode          = MediaCaptureSharingMode.SharedReadOnly,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference     = MediaCaptureMemoryPreference.Cpu  // guarantees SoftwareBitmap
            });

            var src = FindPreviewFrameSource(_broadcastCapture);
            if (src is null) { StopFrameReader(); return; }

            _frameReader = await _broadcastCapture.CreateFrameReaderAsync(src);
            _frameReader.FrameArrived += FrameReader_FrameArrived;
            var status = await _frameReader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
                StopFrameReader();
        }
        catch
        {
            // Broadcast is best-effort; local preview keeps working without it.
            StopFrameReader();
        }
    }

    private void FrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (!ShareServer.Instance.IsRunning || !ShareServer.Instance.ShareCamera) return;
        if (_encodingFrame) return;

        long now = Environment.TickCount64;
        if (now - _lastCamPushMs < 80) return;   // ~12 fps cap
        _lastCamPushMs = now;

        using var frame = sender.TryAcquireLatestFrame();
        var sb = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (sb is null) return;

        // Copy out so the encode can run after the frame is released.
        var copy = SoftwareBitmap.Copy(sb);
        _encodingFrame = true;
        _ = EncodeAndPushAsync(copy);
    }

    private async Task EncodeAndPushAsync(SoftwareBitmap sb)
    {
        try
        {
            using var owned = sb;
            using var converted = owned.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? null
                : SoftwareBitmap.Convert(owned, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var toEncode = converted ?? owned;

            using var ras = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ras);
            encoder.SetSoftwareBitmap(toEncode);
            await encoder.FlushAsync();

            ras.Seek(0);
            var bytes = new byte[ras.Size];
            using (var reader = new DataReader(ras.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)ras.Size);
                reader.ReadBytes(bytes);
            }
            ShareServer.Instance.PushCameraFrame(bytes);
        }
        catch { /* skip this frame */ }
        finally { _encodingFrame = false; }
    }

    private void StopFrameReader()
    {
        if (_frameReader is not null)
        {
            try
            {
                _frameReader.FrameArrived -= FrameReader_FrameArrived;
                _ = _frameReader.StopAsync();
                _frameReader.Dispose();
            }
            catch { }
            _frameReader = null;
        }

        if (_broadcastCapture is not null)
        {
            try { _broadcastCapture.Dispose(); } catch { }
            _broadcastCapture = null;
        }
    }

    private void StopCameraPreview(bool updateUi)
    {
        StopFrameReader();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.Pause();
            CameraPreview.SetMediaPlayer(null);
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _mediaCapture?.Dispose();
        _mediaCapture = null;
        _frameSource = null;
        App.Status.CameraConnected = false;

        if (updateUi)
            ShowCameraPlaceholder(Lang.Live_Waiting);
    }

    private void ShowCameraPlaceholder(string message)
    {
        CameraPreview.Visibility = Visibility.Collapsed;
        CameraPlaceholder.Visibility = Visibility.Visible;
        CameraPlaceholderText.Text = message;
        CameraInfoText.Text = "-";
        App.Status.CameraConnected = false;
        if (!_clientMode) RefreshCameraStopBtn();
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        _ = DispatcherQueue.TryEnqueue(() =>
        {
            StopCameraPreview(updateUi: false);
            ShowCameraPlaceholder(Lang.Format(nameof(Lang.Live_CameraError), args.ErrorMessage));
        });
    }
}
