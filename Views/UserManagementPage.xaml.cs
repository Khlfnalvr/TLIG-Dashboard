using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TLIGDashboard.Services;

namespace TLIGDashboard.Views;

/// <summary>
/// Flattened, fully-localized view of a <see cref="UserAccount"/> for the list.
///
/// <para>Sebagian besar isinya dibangun sekali lalu tidak berubah, jadi dibiarkan
/// <c>init</c>. Yang berubah sendiri hanya <see cref="QueueStatusLabel"/> (+
/// <see cref="QueueStatusDetail"/>): kehadiran bergerak karena orang lain, dan
/// membangun ulang seluruh daftar tiap tiga detik akan mengacaukan gulir serta
/// tombol yang sedang ditekan staf.</para>
///
/// <para>Kolom Status diisi kehadiran + aktivitas plant (Aktif / Tidak aktif /
/// Dalam antrean). Status blokir akun tidak punya teks sendiri: akun yang
/// diblokir tidak bisa login sehingga denyutnya padam dan terbaca Tidak aktif —
/// keadaan blokirnya tetap terlihat di tombol Aktifkan/Nonaktifkan.</para>
/// </summary>
public sealed class UserRow : INotifyPropertyChanged
{
    public string Username      { get; init; } = "";
    public string DisplayName   { get; init; } = "";
    public string Nrp           { get; init; } = "";
    public string Kelas         { get; init; } = "";
    public string NrpKelasLabel { get; init; } = "";   // combined display string
    public string RoleLabel     { get; init; } = "";
    public string LastLoginText { get; init; } = "";

    /// <summary>
    /// Kehadiran + aktivitas plant HE pengguna ini: Aktif, Tidak aktif, Dalam
    /// antrean, atau Aktif dengan sub-keterangan Simulasi berjalan. Inilah isi
    /// kolom Status.
    /// </summary>
    public string QueueStatusLabel
    {
        get => _queueStatusLabel;
        set
        {
            if (_queueStatusLabel == value) return;
            _queueStatusLabel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueStatusLabel)));
        }
    }
    private string _queueStatusLabel = "";

    /// <summary>
    /// Keterangan di bawah <see cref="QueueStatusLabel"/>: "Simulasi berjalan" /
    /// "Memegang giliran" untuk Aktif yang memegang giliran, "Antrean ke-N" untuk
    /// Dalam antrean. Kosong untuk Aktif polosan dan Tidak aktif — baris keduanya
    /// disembunyikan lewat <see cref="QueueStatusDetailVisible"/>.
    /// </summary>
    public string QueueStatusDetail
    {
        get => _queueStatusDetail;
        set
        {
            if (_queueStatusDetail == value) return;
            _queueStatusDetail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueStatusDetail)));
            QueueStatusDetailVisible = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }
    private string _queueStatusDetail = "";

    public Visibility QueueStatusDetailVisible
    {
        get => _queueStatusDetailVisible;
        private set
        {
            if (_queueStatusDetailVisible == value) return;
            _queueStatusDetailVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueStatusDetailVisible)));
        }
    }
    private Visibility _queueStatusDetailVisible = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Visibility helpers for XAML x:Bind
    public Visibility NrpVisible          { get; init; } = Visibility.Collapsed;
    public Visibility KelasVisible        { get; init; } = Visibility.Collapsed;
    public Visibility EmptyNrpKelasVisible { get; init; } = Visibility.Visible;

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
        StartQueuePolling();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UserStore.Instance.Changed -= OnStoreChanged;
        Lang.PropertyChanged       -= OnLangChanged;
        _queueTimer?.Stop();
    }

    // ── Kolom antrian plant ─────────────────────────────────────────────────

    /// <summary>
    /// Selang penyegaran kolom antrian. Sama dengan strip antrian di Dashboard:
    /// cukup terasa langsung, dan bacaannya murah — satu snapshot antrian untuk
    /// seluruh tabel, bukan satu permintaan per baris.
    /// </summary>
    private static readonly TimeSpan QueueRefreshInterval = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _queueTimer;
    private bool _queueRefreshing;

    /// <summary>
    /// Menyegarkan kolom antrian secara berkala. Hanya di Server: di sanalah
    /// database antrian berada, dan halaman ini memang halaman Server.
    /// </summary>
    private void StartQueuePolling()
    {
        if (!BuildInfo.IsServer) return;

        _queueTimer ??= new DispatcherTimer { Interval = QueueRefreshInterval };
        _queueTimer.Tick -= OnQueueTick;
        _queueTimer.Tick += OnQueueTick;
        _queueTimer.Start();
        _ = RefreshQueueColumnAsync();
    }

    private void OnQueueTick(object? sender, object e) => _ = RefreshQueueColumnAsync();

    /// <summary>
    /// Mengisi ulang kolom kehadiran di tempat — barisnya tidak dibangun ulang,
    /// supaya gulir dan tombol yang sedang dipakai staf tidak tersentak tiap tiga
    /// detik.
    ///
    /// Aturannya empat status: pemegang giliran selalu Aktif (sub-nya membedakan
    /// "Simulasi berjalan" dari sekadar "Memegang giliran"), pengantre tampil
    /// "Dalam antrean" + nomornya, sisanya Aktif kalau denyutnya segar (terlihat
    /// dalam semenit terakhir) dan Tidak aktif kalau tidak.
    /// </summary>
    private async Task RefreshQueueColumnAsync()
    {
        if (_queueRefreshing) return;
        _queueRefreshing = true;
        try
        {
            // Denyut penampil sendiri: staf yang membuka halaman ini juga sedang
            // "di dashboard", jadi kehadirannya ikut tercatat.
            try
            {
                var me = App.Session;
                if (me.IsSignedIn)
                    await App.HeQueue.TouchPresenceAsync(me.Username, me.DisplayName, me.Role);
            }
            catch { }

            var snapshot = await App.HeQueue.GetSnapshotAsync();
            var seen     = await App.HeQueue.GetLastSeenAsync();
            var now      = DateTime.UtcNow;

            var holder = snapshot.Holder;
            string? simulatingUser = HeRunRecorder.Instance.RecordingUserId;
            var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < snapshot.Waiting.Count; i++)
                positions[snapshot.Waiting[i].UserId] = i + 1;

            foreach (var row in _rows)
            {
                bool isHolder = holder is not null &&
                    string.Equals(holder.UserId, row.Username, StringComparison.OrdinalIgnoreCase);
                if (isHolder)
                {
                    row.QueueStatusLabel = Lang.Um_PresActive;
                    row.QueueStatusDetail =
                        simulatingUser is not null &&
                        string.Equals(simulatingUser, row.Username, StringComparison.OrdinalIgnoreCase)
                            ? Lang.Um_PresSimulating
                            : Lang.Um_PresHolding;
                }
                else if (positions.TryGetValue(row.Username, out int pos))
                {
                    row.QueueStatusLabel  = Lang.Um_PresInLine;
                    row.QueueStatusDetail = Lang.Format(nameof(Lang.Um_PresQueuePos), pos);
                }
                else if (seen.TryGetValue(row.Username, out var lastSeen) &&
                         HeQueueRepository.IsPresent(lastSeen, now))
                {
                    row.QueueStatusLabel  = Lang.Um_PresActive;
                    row.QueueStatusDetail = "";
                }
                else
                {
                    row.QueueStatusLabel  = Lang.Um_PresInactive;
                    row.QueueStatusDetail = "";
                }
            }
        }
        catch
        {
            // Database antrian belum siap atau sedang terkunci: kolomnya dibiarkan
            // apa adanya dan dicoba lagi tiga detik kemudian. Menghapus isinya hanya
            // akan membuat tabel berkedip tanpa alasan.
        }
        finally
        {
            _queueRefreshing = false;
        }
    }

    private void OnStoreChanged() => DispatcherQueue.TryEnqueue(Refresh);
    private void OnLangChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Refresh);

    private void Refresh()
    {
        _rows.Clear();
        foreach (var u in UserStore.Instance.GetUsers()
                     .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase))
        {
            var nrpKelas = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(u.Nrp))   nrpKelas.Append(u.Nrp);
            if (!string.IsNullOrWhiteSpace(u.Kelas))  nrpKelas.Append(nrpKelas.Length > 0 ? $" · {u.Kelas}" : u.Kelas);

            bool hasNrp   = !string.IsNullOrWhiteSpace(u.Nrp);
            bool hasKelas = !string.IsNullOrWhiteSpace(u.Kelas);
            _rows.Add(new UserRow
            {
                Username      = u.Username,
                DisplayName   = u.DisplayName,
                Nrp           = u.Nrp,
                Kelas         = u.Kelas,
                NrpKelasLabel = nrpKelas.ToString(),
                NrpVisible            = hasNrp   ? Visibility.Visible : Visibility.Collapsed,
                KelasVisible          = hasKelas ? Visibility.Visible : Visibility.Collapsed,
                EmptyNrpKelasVisible  = (!hasNrp && !hasKelas) ? Visibility.Visible : Visibility.Collapsed,
                RoleLabel     = RoleLabel(u.Role),
                LastLoginText = u.LastLoginUtc is null
                    ? Lang.Um_Never
                    : u.LastLoginUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                QueueStatusLabel = Lang.Um_PresInactive,
                ResetLabel    = Lang.Um_ResetPassword,
                EditLabel     = Lang.Um_Edit,
                ToggleLabel   = u.Enabled ? Lang.Um_Disable : Lang.Um_Enable,
                DeleteLabel   = Lang.Um_Delete,
            });
        }
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Baris baru dibangun dengan kolom antrian kosong; diisi segera supaya tidak
        // menunggu detak timer berikutnya (mis. sesudah menambah pengguna).
        if (BuildInfo.IsServer) _ = RefreshQueueColumnAsync();
    }

    private string RoleLabel(string role) => role switch
    {
        UserRoles.Admin     => Lang.Um_RoleAdmin,
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
        var nrpBox    = new TextBox     { Header = "NRP (Nomor Registrasi Pokok)", PlaceholderText = "mis. 05211940000001" };
        var kelasBox  = new TextBox     { Header = "Kelas", PlaceholderText = "mis. TK-3A" };
        var roleCombo = BuildRoleCombo(UserRoles.Mahasiswa);

        var nrpKelasRow = new Grid { ColumnSpacing = 12 };
        nrpKelasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        nrpKelasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        Grid.SetColumn(nrpBox,   0);
        Grid.SetColumn(kelasBox, 1);
        nrpKelasRow.Children.Add(nrpBox);
        nrpKelasRow.Children.Add(kelasBox);

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(userBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(passBox);
        panel.Children.Add(nrpKelasRow);
        panel.Children.Add(roleCombo);

        if (await ShowFormAsync(Lang.Um_DlgAddTitle, panel) != ContentDialogResult.Primary) return;

        var (ok, err) = UserStore.Instance.AddUser(
            userBox.Text, passBox.Password, nameBox.Text, SelectedRole(roleCombo),
            nrpBox.Text, kelasBox.Text);
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
        var nrpBox    = new TextBox { Header = "NRP", Text = u.Nrp,   PlaceholderText = "mis. 05211940000001" };
        var kelasBox  = new TextBox { Header = "Kelas", Text = u.Kelas, PlaceholderText = "mis. TK-3A" };
        var roleCombo = BuildRoleCombo(u.Role);

        var nrpKelasRow = new Grid { ColumnSpacing = 12 };
        nrpKelasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        nrpKelasRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new Microsoft.UI.Xaml.GridLength(1, Microsoft.UI.Xaml.GridUnitType.Star) });
        Grid.SetColumn(nrpBox,   0);
        Grid.SetColumn(kelasBox, 1);
        nrpKelasRow.Children.Add(nrpBox);
        nrpKelasRow.Children.Add(kelasBox);

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(nrpKelasRow);
        panel.Children.Add(roleCombo);

        if (await ShowFormAsync(Lang.Um_DlgEditTitle, panel) != ContentDialogResult.Primary) return;

        var (ok, err) = UserStore.Instance.UpdateUser(
            username, nameBox.Text, SelectedRole(roleCombo), nrpBox.Text, kelasBox.Text);
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
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleAdmin,     Tag = UserRoles.Admin });
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleDosen,     Tag = UserRoles.Dosen });
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleAsisten,   Tag = UserRoles.Asisten });
        combo.Items.Add(new ComboBoxItem { Content = Lang.Um_RoleMahasiswa, Tag = UserRoles.Mahasiswa });
        combo.SelectedIndex = selectedRole switch
        {
            UserRoles.Admin   => 0,
            UserRoles.Dosen   => 1,
            UserRoles.Asisten => 2,
            _                 => 3,
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
