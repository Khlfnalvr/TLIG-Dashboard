using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Helpers;
using TLIGDashboard.Services;
using Windows.UI;

namespace TLIGDashboard.Views;

public sealed partial class AIPage : Page
{
    private LocalizationManager Lang => App.Lang;
    // Shared singleton — same instance as DashboardPage for persistent history
    private AiService _ai => App.Ai;
    private CancellationTokenSource? _cts;
    private TextBlock? _streamingBlock; // current AI bubble text block

    public AIPage()
    {
        InitializeComponent();
        // Keep page alive across navigation so chat history is preserved
        NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        Loaded += OnLoaded;
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double halfWidth = RootGrid.ActualWidth * 0.5;
        if (halfWidth > 0)
        {
            ChatPanel.Width      = halfWidth;
            InputGrid.Width      = halfWidth;
            SuggestionPanel.Width = halfWidth;
        }
    }

    private void QuickSuggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        string? prompt = button.Tag as string ?? button.Content?.ToString();
        if (string.IsNullOrWhiteSpace(prompt)) return;
        ChatInput.Text = prompt.Trim();
        _ = SendMessageAsync();
    }

    // ── Initialise ────────────────────────────────────────────────────────────

    // How many history entries are already rendered in ChatPanel.
    // When OnNavigatedTo fires, we only add the delta (new messages since last visit).
    private int _renderedCount;
    private ElementTheme _renderedTheme = ElementTheme.Default;

    private bool _initialized;
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLocalization();
        _ = ModelPicker.ReloadAsync();
        if (!_initialized)
        {
            _initialized = true;
            Lang.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyLocalization);
            ActualThemeChanged += OnActualThemeChanged;
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_cts != null) return; // don't disrupt active streaming
        ChatPanel.Children.Clear();
        _renderedCount = 0;
        SyncBubblesWithHistory();
    }

    protected override void OnNavigatedTo(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // If theme changed while this page was not in the visual tree (ActualThemeChanged
        // doesn't fire on detached pages), force a full re-render.
        if (_renderedCount > 0 && _renderedTheme != ActualTheme)
        {
            ChatPanel.Children.Clear();
            _renderedCount = 0;
        }
        SyncBubblesWithHistory();
        _ = ModelPicker.ReloadAsync();
    }

    /// <summary>
    /// Appends any history entries that are not yet rendered as chat bubbles.
    /// Called every time the AI page becomes active.
    /// </summary>
    private void SyncBubblesWithHistory()
    {
        var history = App.Ai.History;
        for (int i = _renderedCount; i < history.Count; i++)
        {
            var msg = history[i];
            switch (msg.Role)
            {
                case "user":
                    AddUserBubble(msg.Content);
                    break;
                case "assistant":
                {
                    var (border, _) = AddAiBubble(msg.Content);
                    border.Child = MarkdownRenderer.Render(
                        msg.Content, 13, ActualTheme == ElementTheme.Dark);
                    break;
                }
            }
        }
        _renderedCount = history.Count;
        _renderedTheme = ActualTheme;
        ScrollToBottom();
    }

    private void ApplyLocalization()
    {
        ChatInput.PlaceholderText = Lang.Ai_InputHintFull;
        ToolTipService.SetToolTip(StopBtn, Lang.Ai_StopGen);
    }

    /// <summary>
    /// Re-points the shared <see cref="AiService"/> at the active provider/model.
    /// The provider-aware logic lives in <see cref="AiConfigService.ApplyActive"/>
    /// so the AI page and the Dashboard chat stay in sync.
    /// </summary>
    public void ReloadSettings() => AiConfigService.ApplyActive(_ai);

    // ── Send / stop ───────────────────────────────────────────────────────────

    private void ChatSend_Click(object sender, RoutedEventArgs e) => _ = SendMessageAsync();

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter &&
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
                Windows.System.VirtualKey.Shift)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        {
            _ = SendMessageAsync();
            e.Handled = true;
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private async Task SendMessageAsync()
    {
        string text = ChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Reload settings fresh from disk on every send.
        ReloadSettings();

        if (string.IsNullOrEmpty(_ai.ApiKey))
        {
            AddErrorBubble(Lang.Ai_ErrorNoKey);
            return;
        }

        ChatInput.Text        = "";
        ChatSendBtn.IsEnabled = false;
        StopBtn.Visibility    = Visibility.Visible;

        AddUserBubble(text);
        var (aiBubbleBorder, aiBubble) = AddAiBubble(Lang.Ai_Thinking);
        _streamingBlock = aiBubble;

        _cts = new CancellationTokenSource();

        // Track whether any tokens arrived (used to detect empty response)
        bool hasContent = false;
        string? errorMsg = null;

        try
        {
            // Run the HTTP streaming on a background thread.
            // onToken is called from that background thread — we must
            // marshal every UI update back to the dispatcher.
            await Task.Run(async () =>
            {
                await _ai.StreamChatAsync(text, token =>
                {
                    // This callback runs on the background thread.
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (!hasContent)
                        {
                            aiBubble.Text = "";   // clear "Berpikir..."
                            hasContent    = true;
                        }
                        aiBubble.Text += token;
                        ScrollToBottom();
                    });
                }, _cts.Token);
            }, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            errorMsg = "[Dihentikan]";
        }
        catch (Exception ex)
        {
            errorMsg = $"⚠ {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[AiService] {ex}");
        }

        // Back on UI thread — finalise bubble
        if (errorMsg != null)
            aiBubble.Text = errorMsg;
        else if (!hasContent)
            aiBubble.Text = "⚠ Server tidak mengembalikan konten. " +
                            "Periksa nama model dan saldo API.";
        else
            aiBubbleBorder.Child = MarkdownRenderer.Render(
                aiBubble.Text, 13, ActualTheme == ElementTheme.Dark);

        _cts?.Dispose();
        _cts                  = null;
        _streamingBlock       = null;
        ChatSendBtn.IsEnabled = true;
        StopBtn.Visibility    = Visibility.Collapsed;
        ScrollToBottom();

        // Update rendered count to match new history size
        _renderedCount = App.Ai.History.Count;
    }

    // ── Bubble builders ───────────────────────────────────────────────────────

    private void AddUserBubble(string text)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        var bg = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x00, 0x4E, 0x9B)
            : Color.FromArgb(0xFF, 0xCC, 0xE4, 0xFF));

        var label = MakeLabel(Lang.Ai_UserLabel, HorizontalAlignment.Right);
        var tb    = MakeTextBlock(text);
        var bubble = MakeBubble(bg, new CornerRadius(10, 10, 2, 10),
                                HorizontalAlignment.Right, tb);

        var container = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Right };
        container.Children.Add(label);
        container.Children.Add(bubble);
        ChatPanel.Children.Add(container);
        ScrollToBottom();
    }

    private (Border border, TextBlock tb) AddAiBubble(string initialText)
    {
        bool isDark = ActualTheme == ElementTheme.Dark;
        var bg = new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C)
            : Color.FromArgb(0xFF, 0xF2, 0xF2, 0xF2));

        var label  = MakeLabel(Lang.Ai_AiLabel, HorizontalAlignment.Left);
        var tb     = MakeTextBlock(initialText);
        var bubble = MakeBubble(bg, new CornerRadius(10, 10, 10, 2),
                                HorizontalAlignment.Left, tb);

        var container = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Left };
        container.Children.Add(label);
        container.Children.Add(bubble);
        ChatPanel.Children.Add(container);
        ScrollToBottom();
        return (bubble, tb);
    }

    private void AddErrorBubble(string message)
    {
        var bg = new SolidColorBrush(Color.FromArgb(0x22, 0xCC, 0x26, 0x26));
        var tb = MakeTextBlock(message);
        tb.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0x26, 0x26));

        var bubble = MakeBubble(bg, new CornerRadius(8), HorizontalAlignment.Left, tb);
        ChatPanel.Children.Add(bubble);
        ScrollToBottom();
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private static TextBlock MakeLabel(string text, HorizontalAlignment align) => new()
    {
        Text                = text,
        FontSize            = 10,
        FontWeight          = Microsoft.UI.Text.FontWeights.SemiBold,
        CharacterSpacing    = 60,
        Opacity             = 0.55,
        HorizontalAlignment = align,
        Margin              = new Thickness(4, 0, 4, 2)
    };

    private static TextBlock MakeTextBlock(string text) => new()
    {
        Text                    = text,
        FontSize                = 13,
        TextWrapping            = TextWrapping.Wrap,
        IsTextSelectionEnabled  = true
    };

    private static Border MakeBubble(
        Brush bg, CornerRadius corner, HorizontalAlignment align, UIElement child) => new()
    {
        Background          = bg,
        CornerRadius        = corner,
        Padding             = new Thickness(12, 8, 12, 8),
        HorizontalAlignment = align,
        MaxWidth            = 560,
        Child               = child
    };

    private void ScrollToBottom() =>
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => ChatScroll.ChangeView(null, double.MaxValue, null, true));
}
