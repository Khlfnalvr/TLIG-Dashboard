using Microsoft.UI.Xaml.Controls;

namespace TLIGDashboard.Controls;

/// <summary>
/// Theme-aware cascade block diagram (outer temperature PID → inner flow PI → valve → heat
/// exchanger, with both feedback loops). Shared by the Dashboard and the Parameter page; all
/// drawing is in the XAML, coloured through theme resources so it needs no background.
/// </summary>
public sealed partial class CascadeBlockDiagram : UserControl
{
    public CascadeBlockDiagram()
    {
        InitializeComponent();
    }
}
