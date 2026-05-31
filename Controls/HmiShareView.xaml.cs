using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TLIGDashboard.Services;
using Windows.Storage.Streams;

namespace TLIGDashboard.Controls;

/// <summary>
/// HMI sharing panel. In the SERVER build it is a thin view over the app-wide
/// <see cref="HmiCaptureService"/>: every instance (dashboard panel + Live View page)
/// shares one source list, one selected source, and one capture loop, so selecting a
/// source in one place is immediately reflected everywhere. In the CLIENT build it
/// renders the HMI stream received from the server instead of capturing a screen.
/// </summary>
public sealed partial class HmiShareView : UserControl
{
    private readonly bool _clientMode = BuildInfo.IsClient;

    // True while we are programmatically syncing the ComboBox to the shared state, so the
    // SelectionChanged handler does not mistake that for a user pick and loop back.
    private bool _syncingSelection;

    private LocalizationManager Lang => App.Lang;
    private static HmiCaptureService Capture => HmiCaptureService.Instance;

    public HmiShareView()
    {
        InitializeComponent();
        RefreshLocalizedText();
        Lang.PropertyChanged += Lang_PropertyChanged;

        if (_clientMode)
        {
            // Client: display the HMI stream received from the server instead of
            // capturing a local screen.
            SourceSelector.Visibility       = Visibility.Collapsed;
            RefreshSourcesButton.Visibility = Visibility.Collapsed;
            ShowPlaceholder(Lang.Hmi_WaitingStream);
            // Subscribe on every load; unsubscribe on unload. (The hosting page is
            // cached, so hooking once in the ctor would be dropped after the first
            // navigation away.)
            Loaded   += (_, _) =>
            {
                ShareClient.Instance.FrameReceived -= OnRemoteFrame;
                ShareClient.Instance.FrameReceived += OnRemoteFrame;
            };
            Unloaded += (_, _) => ShareClient.Instance.FrameReceived -= OnRemoteFrame;
        }
        else
        {
            Loaded   += HmiShareView_Loaded;
            Unloaded += HmiShareView_Unloaded;
        }
    }

    // ── Client mode: render frames pushed from the server ─────────────────────
    private void OnRemoteFrame(byte channel, byte[] bytes)
    {
        if (channel != ShareProtocol.ChannelHmi) return;
        DispatcherQueue.TryEnqueue(async () => await RenderFrameAsync(bytes));
    }

    // ── Server mode: bind this view to the shared capture engine ──────────────
    private void HmiShareView_Loaded(object sender, RoutedEventArgs e)
    {
        // Unsubscribe first so a repeated Loaded (cached page) can't stack handlers.
        Capture.SourcesChanged      -= OnSourcesChanged;
        Capture.ActiveSourceChanged -= OnActiveSourceChanged;
        Capture.FrameAvailable      -= OnCaptureFrame;
        Capture.SourcesChanged      += OnSourcesChanged;
        Capture.ActiveSourceChanged += OnActiveSourceChanged;
        Capture.FrameAvailable      += OnCaptureFrame;

        SourceSelector.DisplayMemberPath = nameof(CaptureSource.Name);
        Capture.RefreshSources();   // rebuilds the list → OnSourcesChanged repopulates the combo
        OnActiveSourceChanged();    // adopt whatever source is already selected app-wide
    }

    private void HmiShareView_Unloaded(object sender, RoutedEventArgs e)
    {
        // Detach UI updates while this view is off-screen, but leave the capture engine
        // running so HMI sharing continues in the background and the selection persists
        // for the other view.
        Capture.SourcesChanged      -= OnSourcesChanged;
        Capture.ActiveSourceChanged -= OnActiveSourceChanged;
        Capture.FrameAvailable      -= OnCaptureFrame;
    }

    // Shared list changed (a refresh happened here or in the other view): repopulate
    // the combo and re-point it at the shared selection.
    private void OnSourcesChanged()
    {
        _syncingSelection = true;
        var snapshot = Capture.Sources;
        SourceSelector.ItemsSource = snapshot;
        SelectInCombo(snapshot, Capture.ActiveSource);
        _syncingSelection = false;
    }

    // Shared selection changed (here or in the other view): sync the combo + the
    // image/placeholder/stop-button visuals to match.
    private void OnActiveSourceChanged()
    {
        var snapshot = SourceSelector.ItemsSource as IReadOnlyList<CaptureSource> ?? Capture.Sources;
        var active   = Capture.ActiveSource;

        _syncingSelection = true;
        SelectInCombo(snapshot, active);
        _syncingSelection = false;

        if (active is null)
        {
            CaptureImage.Source        = null;
            CaptureImage.Visibility    = Visibility.Collapsed;
            StopShareButton.Visibility = Visibility.Collapsed;
            ShowPlaceholder(snapshot.Count == 0 ? Lang.Hmi_NoSource : Lang.Hmi_SelectPlaceholder);
        }
        else
        {
            CaptureImage.Visibility     = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
            StopShareButton.Visibility  = Visibility.Visible;
        }
    }

    private void SelectInCombo(IReadOnlyList<CaptureSource> sources, CaptureSource? active)
        => SourceSelector.SelectedItem = active is null
            ? null
            : sources.FirstOrDefault(s => s.Matches(active));

    private void OnCaptureFrame(byte[] png)
        => DispatcherQueue.TryEnqueue(async () => await RenderFrameAsync(png));

    private void SourceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || SourceSelector.SelectedItem is not CaptureSource source)
            return;
        Capture.Select(source);
    }

    private void RefreshSourcesButton_Click(object sender, RoutedEventArgs e)
        => Capture.RefreshSources();

    private void StopShareButton_Click(object sender, RoutedEventArgs e)
        => Capture.Stop();

    // ── Shared rendering (both modes decode a JPEG/PNG frame into the Image) ──
    private async Task RenderFrameAsync(byte[] bytes)
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
            CaptureImage.Source         = bitmap;
            CaptureImage.Visibility     = Visibility.Visible;
            PlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        catch { /* drop a bad frame */ }
    }

    // ── Localization ──────────────────────────────────────────────────────────
    private void Lang_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        RefreshLocalizedText();
        if (_clientMode)
        {
            if (CaptureImage.Source is null)
                ShowPlaceholder(Lang.Hmi_WaitingStream);
        }
        else if (Capture.ActiveSource is null)
        {
            ShowPlaceholder(Lang.Hmi_SelectPlaceholder);
        }
    }

    private void RefreshLocalizedText()
    {
        HeaderText.Text                = Lang.Hmi_Header;
        SourceSelector.PlaceholderText = Lang.Hmi_SelectSource;
        RefreshSourcesButton.Content   = Lang.Hmi_RefreshSources;
        StopShareButton.Content        = Lang.Hmi_StopShare;
    }

    private void ShowPlaceholder(string message)
    {
        PlaceholderText.Text        = message;
        PlaceholderPanel.Visibility = Visibility.Visible;
    }
}
