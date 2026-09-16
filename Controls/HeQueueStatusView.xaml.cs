using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TLIGDashboard.Models;
using TLIGDashboard.Services;

namespace TLIGDashboard.Controls;

/// <summary>
/// Strip status antrian plant HE, dipasang di kartu Control halaman Dashboard.
/// Menjawab tiga pertanyaan yang selalu muncul di lab: plant sedang dipakai
/// siapa, saya di antrean ke berapa, dan apa yang bisa saya lakukan sekarang.
///
/// Sumber datanya <see cref="HeQueueWatcher"/> — satu-satunya yang menanyakan
/// keadaan antrian di seluruh aplikasi — jadi kontrol ini sama saja di kedua
/// flavor: di Server pengamat itu membaca database antrian langsung, di Client ia
/// membaca jawaban Server lewat HTTP. Tidak ada aturan antrian yang dihitung di
/// sini — keputusan selalu milik Server.
///
/// <para>Giliran yang dibatasi waktu (Mahasiswa) ikut menampilkan hitung mundur
/// sisa waktunya. Peringatan 5 dan 1 menit sengaja <b>tidak</b> dimunculkan dari
/// sini melainkan dari pengamatnya, karena peringatan itu harus tetap sampai walau
/// halaman Dashboard sedang tidak terbuka.</para>
/// </summary>
public sealed partial class HeQueueStatusView : UserControl
{
    private static readonly SolidColorBrush DotFree    = new(Microsoft.UI.Colors.LimeGreen);
    private static readonly SolidColorBrush DotMine    = new(Microsoft.UI.Colors.DodgerBlue);
    private static readonly SolidColorBrush DotBusy    = new(Microsoft.UI.Colors.Orange);
    private static readonly SolidColorBrush DotUnknown = new(Microsoft.UI.Colors.Gray);

    private LocalizationManager Lang => App.Lang;

    private static HeQueueWatcher Watcher => HeQueueWatcher.Instance;

    /// <summary>Keadaan antrian yang terakhir terbaca — <c>null</c> kalau belum/ tidak terbaca.</summary>
    public HeQueueStatus? Status => Watcher.Status;

    public HeQueueStatusView()
    {
        InitializeComponent();
        RefreshLocalizedText();
        Lang.PropertyChanged += Lang_PropertyChanged;

        Loaded   += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Watcher.Changed -= OnWatcherChanged;
        Watcher.Changed += OnWatcherChanged;
        Apply(Status);
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Halaman induk di-cache, jadi kontrol ini bisa dimuat ulang nanti.
        // Pengamatnya sendiri tetap berjalan: peringatan sisa waktu harus tetap
        // sampai walau halaman ini sedang tidak terlihat.
        Watcher.Changed -= OnWatcherChanged;
    }

    private void OnWatcherChanged(HeQueueStatus? status) => Apply(status);

    private void Lang_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshLocalizedText();
        Apply(Status);
    }

    private void RefreshLocalizedText()
    {
        LeaveButton.Content = Lang.HeQ_Leave;
        ForceButton.Content = Lang.HeQ_ForceRelease;
    }

    /// <summary>
    /// Meminta pengamat membaca ulang keadaan antrian sekarang juga — dipakai
    /// layar setelah menekan JALANKAN/BERHENTI supaya stripnya tidak menunggu
    /// detak timer berikutnya.
    /// </summary>
    public Task RefreshAsync() => Watcher.RefreshAsync();

    private void Apply(HeQueueStatus? status)
    {
        if (!HeControlService.Enabled)
        {
            // Belum ada yang login: antrian tidak berlaku, jadi jangan memakan
            // tempat di kartu Control.
            Root.Visibility = Visibility.Collapsed;
            return;
        }

        Root.Visibility = Visibility.Visible;

        if (status is null)
        {
            StateDot.Fill      = DotUnknown;
            StatusText.Text    = Lang.HeQ_Unreachable;
            DetailText.Visibility    = Visibility.Collapsed;
            TimeLeftBadge.Visibility = Visibility.Collapsed;
            LeaveButton.Visibility   = Visibility.Collapsed;
            ForceButton.Visibility   = Visibility.Collapsed;
            return;
        }

        var holder  = status.Holder;
        int waiting = status.Snapshot.Waiting.Count;

        if (status.IAmHolder)
        {
            StateDot.Fill   = DotMine;
            StatusText.Text = Lang.HeQ_YouHolding;
        }
        else if (holder is not null)
        {
            StateDot.Fill   = DotBusy;
            StatusText.Text = Lang.Format(nameof(Lang.HeQ_HeldBy),
                holder.DisplayName,
                HeQueuePriorityMap.Label(holder.Priority),
                FormatDuration(holder.HeldFor));
        }
        else
        {
            StateDot.Fill   = DotFree;
            StatusText.Text = Lang.HeQ_Free;
        }

        string detail =
            status.IAmWaiting ? Lang.Format(nameof(Lang.HeQ_WaitingYou), status.MyPosition, waiting)
            : waiting > 0     ? Lang.Format(nameof(Lang.HeQ_WaitingCount), waiting)
            : "";

        DetailText.Text       = detail;
        DetailText.Visibility = detail.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // Hitung mundur sisa giliran. Hanya ditampilkan untuk giliran yang memang
        // dibatasi (Mahasiswa) — memasang "sisa 30:00" pada giliran Dosen hanya
        // akan membuat orang percaya ada batas yang sebenarnya tidak ada.
        var left = HeControlService.RemainingHold(holder);
        if (left is { } remaining)
        {
            TimeLeftText.Text        = Lang.Format(nameof(Lang.HeQ_TimeLeftLabel), FormatDuration(remaining));
            TimeLeftBadge.Visibility = Visibility.Visible;
        }
        else
        {
            TimeLeftBadge.Visibility = Visibility.Collapsed;
        }

        LeaveButton.Visibility = status.IAmWaiting ? Visibility.Visible : Visibility.Collapsed;
        // Cabut paksa hanya masuk akal kalau ada kendali milik ORANG LAIN yang
        // perlu dicabut; staf yang sedang memegang sendiri cukup menekan STOP.
        ForceButton.Visibility = status.CanForceRelease && holder is not null && !status.IAmHolder
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void LeaveButton_Click(object sender, RoutedEventArgs e)
    {
        LeaveButton.IsEnabled = false;
        try { await HeControlService.CancelAsync(); }
        finally { LeaveButton.IsEnabled = true; }
        await RefreshAsync();
    }

    private async void ForceButton_Click(object sender, RoutedEventArgs e)
    {
        string who = Status?.Holder?.DisplayName ?? "-";

        ForceButton.IsEnabled = false;
        HeRigReleaseResult result;
        try
        {
            result = await HeControlService.ForceReleaseAsync(
                $"Dicabut lewat panel antrian oleh {App.Session.DisplayName}");
        }
        finally { ForceButton.IsEnabled = true; }

        // Giliran memang dilepas walau rig gagal dihentikan — itu yang membuat ini
        // pintu darurat. Justru karena itu stafnya harus diberi tahu bahwa plant
        // bisa jadi masih menyala, supaya rignya diperiksa langsung.
        if (!result.Released)
            Watcher.Announce(HeQueueNoticeKind.Error, Lang.HeQ_ForceDoneTitle, Lang.HeQ_ForceFailedMsg);
        else if (!result.RigStopped)
            Watcher.Announce(HeQueueNoticeKind.Error, Lang.HeQ_ForceDoneTitle,
                Lang.Format(nameof(Lang.HeQ_ForceRigWarnMsg), who, result.Problem ?? "-"));
        else
            Watcher.Announce(HeQueueNoticeKind.Info, Lang.HeQ_ForceDoneTitle,
                Lang.Format(nameof(Lang.HeQ_ForceDoneMsg), who));

        await RefreshAsync();
    }

    /// <summary>Lama pegang dalam bentuk mm:ss (atau h:mm:ss kalau sudah lewat sejam).</summary>
    private static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
    }
}
