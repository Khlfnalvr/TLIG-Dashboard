using Microsoft.UI.Xaml.Controls;

namespace TLIGDashboard.Views;

public sealed partial class ParameterPage : Page
{
    private Services.LocalizationManager Lang => App.Lang;

    public ParameterPage()
    {
        InitializeComponent();
    }
}
