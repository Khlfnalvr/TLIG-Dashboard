using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

/// <summary>Flattened, fully-localized view of a <see cref="UserAccount"/> for the list.</summary>
public sealed class UserRow
{
    public string Username      { get; init; } = "";
    public string DisplayName   { get; init; } = "";
    public string RoleLabel     { get; init; } = "";
    public string StatusLabel   { get; init; } = "";
    public string LastLoginText { get; init; } = "";

    // Action button captions (resolved at build time, re-resolved on language change).
    public string ResetLabel    { get; init; } = "";
    public string EditLabel     { get; init; } = "";
    public string ToggleLabel   { get; init; } = "";
    public string DeleteLabel   { get; init; } = "";
}

/// <summary>
/// Server-only page (shown to signed-in staff — Dosen/Asisten) for managing the
/// user accounts that can sign in to this server. Backed by <see cref="UserStore"/>.
/// </summary>
public sealed partial class UserManagementPage : Page
{
    private LocalizationManager Lang => App.Lang;
    private readonly ObservableCollection<UserRow> _rows = new();

    public UserManagementPage()
    {
        InitializeComponent();
        UsersList.ItemsSource = _rows;
        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UserStore.Instance.Changed += OnStoreChanged;
        Lang.PropertyChanged       += OnLangChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UserStore.Instance.Changed -= OnStoreChanged;
        Lang.PropertyChanged       -= OnLangChanged;
    }

    private void OnStoreChanged() => DispatcherQueue.TryEnqueue(Refresh);
    private void OnLangChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        _rows.Clear();
        foreach (var u in UserStore.Instance.GetUsers()
                     .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase))
        {
            _rows.Add(new UserRow
            {
                Username      = u.Username,
                DisplayName   = u.DisplayName,
                RoleLabel     = RoleLabel(u.Role),
                StatusLabel   = u.Enabled ? Lang.Um_Enabled : Lang.Um_Disabled,
                LastLoginText = u.LastLoginUtc is null
                    ? Lang.Um_Never
                    : u.LastLoginUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                ResetLabel    = Lang.Um_ResetPassword,
                EditLabel     = Lang.Um_Edit,
                ToggleLabel   = u.Enabled ? Lang.Um_Disable : Lang.Um_Enable,
                DeleteLabel   = Lang.Um_Delete,
            });
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string RoleLabel(string role) => role switch
    {
        UserRoles.Dosen     => Lang.Um_RoleDosen,
        UserRoles.Asisten   => Lang.Um_RoleAsisten,
        _                   => Lang.Um_RoleMahasiswa,
    };

    // ── Add ─────────────────────────────────────────────────────────────────────

    private async void AddUser_Click(object sender, RoutedEventArgs e)
    {
        var userBox   = new TextBox     { Header = Lang.Um_FieldUsername };
        var nameBox   = new TextBox     { Header = Lang.Um_FieldDisplayName };
        var passBox   = new PasswordBox { Header = Lang.Um_FieldPassword };
        var roleCombo = BuildRoleCombo(UserRoles.Mahasiswa);

        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(userBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(passBox);
        panel.Children.Add(roleCombo);

        if (await ShowFormAsync(Lang.Um_DlgAddTitle, panel) != ContentDialogResult.Primary) return;

        var (ok, err) = UserStore.Instance.AddUser(
            userBox.Text, passBox.Password, nameBox.Text, SelectedRole(roleCombo));
        if (!ok) await ShowErrorAsync(err);
    }

    // ── Reset password ────────────────────────────────────────────────────────────

    private async void ResetPwd_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } username) return;

        var passBox = new PasswordBox { Header = Lang.Um_FieldNewPassword, MinWidth = 320 };
        if (await ShowFormAsync(Lang.Um_DlgResetTitle, passBox) != ContentDialogResult.Primary) return;

        var (ok, err) = UserStore.Instance.ResetPassword(username, passBox.Password);
        if (!ok) await ShowErrorAsync(err);
    }

    // ── Edit (display name + role) ──────────────────────────────────────────────

    private async void EditUser_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } username) return;
        var u = UserStore.Instance.GetUsers()
            .FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
        if (u is null) return;

        var nameBox   = new TextBox { Header = Lang.Um_FieldDisplayName, Text = u.DisplayName };
        var roleCombo = BuildRoleCombo(u.Role);

        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(roleCombo);

        if (await ShowFormAsync(Lang.Um_DlgEditTitle, panel) != ContentDialogResult.Primary) return;

        var (ok, err) = UserStore.Instance.UpdateUser(username, nameBox.Text, SelectedRole(roleCombo));
        if (!ok) await ShowErrorAsync(err);
    }

    // ── Enable / disable ────────────────────────────────────────────────────────

    private async void ToggleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } username) return;
        var u = UserStore.Instance.GetUsers()
            .FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
        if (u is null) return;

        var (ok, err) = UserStore.Instance.SetEnabled(username, !u.Enabled);
        if (!ok) await ShowErrorAsync(err);
    }

    // ── Delete ────────────────────────────────────────────────────────────────────

    private async void DeleteUser_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf(sender) is not { } username) return;

        var dialog = new ContentDialog
        {
            Title             = Lang.Um_DlgDeleteTitle,
            Content           = Lang.Format(nameof(Lang.Um_DlgDeleteMsg), username),
            PrimaryButtonText = Lang.Um_Delete,
            CloseButtonText   = Lang.Um_Cancel,
            DefaultButton     = ContentDialogButton.Close,
            XamlRoot          = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, err) = UserStore.Instance.DeleteUser(username);
        if (!ok) await ShowErrorAsync(err);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private ComboBox BuildRoleCombo(string selectedRole)
    {
        var combo = new ComboBox
        {
            Header = Lang.Um_FieldRole,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleDosen,     Tag = UserRoles.Dosen });
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleAsisten,   Tag = UserRoles.Asisten });
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleMahasiswa, Tag = UserRoles.Mahasiswa });
        combo.SelectedIndex = selectedRole switch
        {
            UserRoles.Dosen   => 0,
            UserRoles.Asisten => 1,
            _                 => 2,
        };
        return combo;
    }

    private static string SelectedRole(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? UserRoles.Mahasiswa;

    private static string? TagOf(object sender) => (sender as FrameworkElement)?.Tag as string;

    private async Task<ContentDialogResult> ShowFormAsync(string title, UIElement content)
    {
        var dialog = new ContentDialog
        {
            Title             = title,
            Content           = content,
            PrimaryButtonText = Lang.Um_Save,
            CloseButtonText   = Lang.Um_Cancel,
            DefaultButton     = ContentDialogButton.Primary,
            XamlRoot          = XamlRoot,
        };
        try { return await dialog.ShowAsync(); }
        catch { return ContentDialogResult.None; }
    }

    private async Task ShowErrorAsync(string? errorKey)
    {
        var dialog = new ContentDialog
        {
            Title           = Lang.Um_Title,
            Content         = Lang.Get(errorKey ?? ""),
            CloseButtonText = Lang.Um_Confirm,
            XamlRoot        = XamlRoot,
        };
        try { await dialog.ShowAsync(); } catch { }
    }
}
