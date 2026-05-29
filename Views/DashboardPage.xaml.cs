using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TLIGDashboard.Views;

public sealed partial class DashboardPage : Page
{
    private Services.LocalizationManager Lang => App.Lang;
    private bool _dragging1, _dragging2;
    private double _dragStartX;
    private double _leftStartW, _centerStartW, _rightStartW;

    private double _ratioL = 1.0 / 3, _ratioC = 1.0 / 3, _ratioR = 1.0 / 3;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var cursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        SetCursor(Splitter1, cursor);
        SetCursor(Splitter2, cursor);

        double total = AvailableWidth;
        if (total > 0)
        {
            double third = Math.Floor(total / 3.0);
            SetColumnWidths(third, third, total - third * 2);
        }
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double total = AvailableWidth;
        if (total <= 0) return;

        double minL = total / 3.0, minC = total / 4.0, minR = total / 4.0;
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
    }

    private double AvailableWidth =>
        RootGrid.ActualWidth > 8 ? RootGrid.ActualWidth - 8 : 0;

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
        double minL = total / 3.0, minC = total / 4.0, minR = total / 4.0;

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
        double minL = total / 3.0, minC = total / 4.0, minR = total / 4.0;

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
    private void ChatSend_Click(object sender, RoutedEventArgs e) => SendChatMessage();

    private void ChatInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            SendChatMessage();
            e.Handled = true;
        }
    }

    private void SendChatMessage()
    {
        string text = ChatInput.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ChatInput.Text = "";
        AddChatMessage("user", text);
        AddChatMessage("ai", "...");
    }

    private void AddChatMessage(string role, string text)
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
            Padding = new Thickness(10, 7, 10, 7),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            MaxWidth = 220,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
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

    // ── Fullscreen buttons ───────────────────────────────────────────────
    private void LeftFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("Parameter");

    private void CenterFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("LiveView");

    private void RightFullscreen_Click(object sender, RoutedEventArgs e)
        => App.CurrentWindow?.NavigateToPage("AI");

    // ── Cursor helper (ProtectedCursor is non-public in WinUI 3) ────────
    private static void SetCursor(UIElement element, InputCursor cursor)
        => typeof(UIElement)
            .GetProperty("ProtectedCursor",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(element, cursor);
}
