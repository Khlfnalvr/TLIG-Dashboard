using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            ChatPanel.Width = halfWidth;
            InputGrid.Width = halfWidth;
        }
    }

    // ── Initialise ────────────────────────────────────────────────────────────

    // How many history entries are already rendered in ChatPanel.
    // When OnNavigatedTo fires, we only add the delta (new messages since last visit).
    private int _renderedCount;

    private bool _initialized;
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLocalization();
        ReloadSettings();
        if (!_initialized)
        {
            _initialized = true;
            Lang.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyLocalization);
        }
    }

    protected override void OnNavigatedTo(
        Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Sync bubbles with App.Ai.History so messages sent from Dashboard
        // (or a previous session) are visible when opening the AI page.
        SyncBubblesWithHistory();
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
                    // Render as completed bubble (no streaming needed for history)
                    AddAiBubble(msg.Content);
                    break;
            }
        }
        _renderedCount = history.Count;
        ScrollToBottom();
    }

    private void ApplyLocalization()
    {
        ChatInput.PlaceholderText = Lang.Ai_InputHintFull;
        ClearLabel.Text      = Lang.Ai_ClearChat;
        SettingsLabel.Text   = Lang.Ai_Settings;
        LblApiUrl.Text       = Lang.Ai_ApiUrl;
        LblApiKey.Text       = Lang.Ai_ApiKey;
        LblModel.Text        = Lang.Ai_Model;
        LblSysPrompt.Text    = Lang.Ai_SystemPrompt;
        SaveSettingsBtn.Content = Lang.Ai_SaveSettings;
        ToolTipService.SetToolTip(StopBtn, Lang.Ai_StopGen);
        RefreshModelLabel();
    }

    private void RefreshModelLabel() =>
        ModelLabel.Text = Lang.Format("Ai_ModelLabel", _ai.Model);

    // ── Settings persistence ──────────────────────────────────────────────────

    private void LoadAiSettings()
    {
        var s = AppSettingsService.Load();
        _ai.ApiUrl       = s.AiApiUrl;
        _ai.ApiKey       = s.AiApiKey;
        _ai.Model        = s.AiModel;
        _ai.SystemPrompt = s.AiSystemPrompt;

        ApiUrlBox.Text      = s.AiApiUrl;
        ApiKeyBox.Password  = s.AiApiKey;
        ModelBox.Text       = s.AiModel;
        SysPromptBox.Text   = s.AiSystemPrompt;

        RefreshModelLabel();
    }

    /// <summary>Called by MainWindow when AI settings are saved from the flyout.</summary>
    public void ReloadSettings()
    {
        var s = AppSettingsService.Load();
        _ai.ApiUrl       = s.AiApiUrl;
        _ai.ApiKey       = s.AiApiKey;
        _ai.Model        = s.AiModel;
        _ai.SystemPrompt = s.AiSystemPrompt;
        RefreshModelLabel();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        _ai.ApiUrl       = ApiUrlBox.Text.Trim();
        _ai.ApiKey       = ApiKeyBox.Password.Trim();
        _ai.Model        = ModelBox.Text.Trim();
        _ai.SystemPrompt = SysPromptBox.Text.Trim();

        var s = AppSettingsService.Load();
        s.AiApiUrl       = _ai.ApiUrl;
        s.AiApiKey       = _ai.ApiKey;
        s.AiModel        = _ai.Model;
        s.AiSystemPrompt = _ai.SystemPrompt;
        AppSettingsService.Save(s);

        RefreshModelLabel();
        SettingsFlyout.Hide();
    }

    // ── Clear chat ────────────────────────────────────────────────────────────

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        _ai.ClearHistory();
        ChatPanel.Children.Clear();
        _renderedCount = 0;

        // Also clear DashboardPage's chat panel if it's cached in the Frame
        if (App.CurrentWindow?.GetContentFrame()?.Content is DashboardPage dash)
            dash.ClearChatPanel();
    }

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
        var aiBubble = AddAiBubble(Lang.Ai_Thinking);
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

        // Back on UI thread — finalise bubble text
        if (errorMsg != null)
            aiBubble.Text = errorMsg;
        else if (!hasContent)
            aiBubble.Text = "⚠ Server tidak mengembalikan konten. " +
                            "Periksa nama model dan saldo API.";

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

    private TextBlock AddAiBubble(string initialText)
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
        return tb;
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
