using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;

namespace TLIGDashboard.Controls;

public sealed partial class ChatSessionBar : UserControl
{
    private bool _syncing;

    public ChatSessionBar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ChatSessionService.Instance.ListChanged += OnListChanged;
        Rebuild();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ChatSessionService.Instance.ListChanged -= OnListChanged;
    }

    private void OnListChanged() => DispatcherQueue.TryEnqueue(Rebuild);

    public void Rebuild()
    {
        _syncing = true;
        SessionCombo.Items.Clear();
        int selected = -1;
        int i = 0;
        foreach (var s in ChatSessionService.Instance.Sessions)
        {
            SessionCombo.Items.Add(s.Title);
            if (s == ChatSessionService.Instance.Active) selected = i;
            i++;
        }
        SessionCombo.SelectedIndex = selected;
        _syncing = false;
    }

    private void SessionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        int idx = SessionCombo.SelectedIndex;
        var sessions = ChatSessionService.Instance.Sessions;
        if (idx < 0 || idx >= sessions.Count) return;
        ChatSessionService.Instance.SwitchTo(sessions[idx].Id);
    }

    private void NewBtn_Click(object sender, RoutedEventArgs e) =>
        ChatSessionService.Instance.NewSession();

    private void DeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var active = ChatSessionService.Instance.Active;
        if (active is not null) ChatSessionService.Instance.Delete(active.Id);
    }
}
