using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TLIGDashboard.Views;

public sealed partial class AIPage : Page
{
    private Services.LocalizationManager Lang => App.Lang;

    public AIPage()
    {
        InitializeComponent();
    }

    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendMessage();

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SendMessage();
            e.Handled = true;
        }
    }

    private void SendMessage()
    {
        string text = ChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInput.Text = "";
        AddMessage("user", text);
        AddMessage("ai", "...");
    }

    private void AddMessage(string role, string text)
    {
        bool isUser = role == "user";
        bool isDark = ActualTheme == ElementTheme.Dark;

        var label = new TextBlock
        {
            Text = isUser ? App.Lang.Get("Ai_UserLabel") : App.Lang.Get("Ai_AiLabel"),
            FontSize = 10,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            CharacterSpacing = 60,
            Opacity = 0.55,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 2, 2)
        };

        var bg = isUser
            ? new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x00, 0x4E, 0x9B)
                : Color.FromArgb(0xFF, 0xCC, 0xE4, 0xFF))
            : new SolidColorBrush(isDark
                ? Color.FromArgb(0xFF, 0x32, 0x32, 0x32)
                : Color.FromArgb(0xFF, 0xF0, 0xF0, 0xF0));

        var bubble = new Border
        {
            Background = bg,
            CornerRadius = isUser
                ? new CornerRadius(10, 10, 2, 10)
                : new CornerRadius(10, 10, 10, 2),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 520,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            }
        };

        var container = new StackPanel
        {
            Spacing = 3,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left
        };
        container.Children.Add(label);
        container.Children.Add(bubble);
        ChatPanel.Children.Add(container);

        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => ChatScroll.ChangeView(null, double.MaxValue, null, true));
    }
}
