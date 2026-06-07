using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace TLIGDashboard.Views;

/// <summary>
/// Full-page Learning Analytic view. A thin host around the shared
/// <see cref="Controls.LearningAnalyticView"/> (the same control the Dashboard
/// embeds), so both surfaces show identical content. Reloads the task list each
/// time the page is navigated to.
/// </summary>
public sealed partial class LearningAnalyticPage : Page
{
    public LearningAnalyticPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = AnalyticView.ReloadAsync();
    }
}
