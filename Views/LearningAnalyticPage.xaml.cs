using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace TLIGDashboard.Views;

/// <summary>
/// Combined Learning Analytic + Challenge page.
/// Two tabs share the same navigation entry so the nav bar stays clean.
/// </summary>
public sealed partial class LearningAnalyticPage : Page
{
    private bool _challengeLoaded;

    public LearningAnalyticPage()
    {
        InitializeComponent();
        ApplyTabStyle(activeAnalytic: true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // If arriving with "challenge" hint (e.g. deep-link), open that tab.
        if (e.Parameter is string hint && hint == "challenge")
            SwitchToChallenge();
        else
            _ = AnalyticView.ReloadAsync();
    }

    // ── Tab switching ────────────────────────────────────────────────────

    private void TabAnalyticBtn_Click(object sender, RoutedEventArgs e) => SwitchToAnalytic();
    private void TabChallengeBtn_Click(object sender, RoutedEventArgs e) => SwitchToChallenge();

    private void SwitchToAnalytic()
    {
        AnalyticView.Visibility  = Visibility.Visible;
        ChallengeFrame.Visibility = Visibility.Collapsed;
        ApplyTabStyle(activeAnalytic: true);
        _ = AnalyticView.ReloadAsync();
    }

    private void SwitchToChallenge()
    {
        AnalyticView.Visibility  = Visibility.Collapsed;
        ChallengeFrame.Visibility = Visibility.Visible;
        ApplyTabStyle(activeAnalytic: false);

        // Lazy-navigate the Frame the first time only.
        if (!_challengeLoaded)
        {
            ChallengeFrame.Navigate(typeof(ChallengeLearningPage));
            _challengeLoaded = true;
        }
    }

    // ── Visual tab style (underline indicator) ───────────────────────────

    private void ApplyTabStyle(bool activeAnalytic)
    {
        SetTabActive(TabAnalyticBtn,  activeAnalytic);
        SetTabActive(TabChallengeBtn, !activeAnalytic);
    }

    private static void SetTabActive(Button btn, bool active)
    {
        if (active)
        {
            btn.Foreground = new SolidColorBrush(
                (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
            btn.BorderBrush     = new SolidColorBrush(
                (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
            btn.BorderThickness = new Thickness(0, 0, 0, 2);
            btn.FontWeight      = Microsoft.UI.Text.FontWeights.SemiBold;
        }
        else
        {
            btn.ClearValue(Button.ForegroundProperty);
            btn.BorderBrush     = new SolidColorBrush(Colors.Transparent);
            btn.BorderThickness = new Thickness(0, 0, 0, 2);
            btn.FontWeight      = Microsoft.UI.Text.FontWeights.Normal;
        }
    }
}
