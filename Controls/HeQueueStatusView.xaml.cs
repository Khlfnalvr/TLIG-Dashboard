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
/// Sumber datanya <see cref="HeControlService"/>, jadi kontrol ini sama saja di
/// kedua flavor: di Server ia membaca database antrian langsung, di Client ia
/// membaca jawaban Server lewat HTTP. Tidak ada aturan antrian yang dihitung di
/// sini — keputusan selalu milik Server.
/// </summary>
public sealed partial class HeQueueStatusView : UserControl
{
    /// <summary>
    /// Selang penyegaran. Antrian berubah karena orang lain, bukan karena layar
    /// ini, jadi satu-satunya cara tahu adalah bertanya berkala; 3 detik cukup
    /// terasa langsung tanpa membanjiri Server yang juga sedang menggerakkan plant.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);

    private static readonly SolidColorBrush DotFree    = new(Microsoft.UI.Colors.LimeGreen);
    private static readonly SolidColorBrush DotMine    = new(Microsoft.UI.Colors.DodgerBlue);
    private static readonly SolidColorBrush DotBusy    = new(Microsoft.UI.Colors.Orange);
    private static readonly SolidColorBrush DotUnknown = new(Microsoft.UI.Colors.Gray);

    private LocalizationManager Lang => App.Lang;

    private DispatcherTimer? _timer;
    private bool _refreshing;

    /// <summary>Keadaan antrian yang terakhir terbaca — <c>null</c> kalau belum/ tidak terbaca.</summary>
    public HeQueueStatus? Status { get; private set; }

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
        _timer ??= new DispatcherTimer { Interval = RefreshInterval };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Halaman induk di-cache, jadi kontrol ini bisa dimuat ulang nanti —
        // timernya cukup dihentikan, tidak dibuang.
        _timer?.Stop();
    }

    private void OnTick(object? sender, object e) => _ = RefreshAsync();

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
    /// Membaca ulang keadaan antrian dan menggambarnya. Panggilan yang menumpuk
    /// diabaikan: penyegaran lambat (Client yang server-nya jauh) tidak boleh
    /// menumpuk permintaan setiap kali timer berdetak.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (!HeControlService.Enabled)
            {
                // Belum ada yang login: antrian tidak berlaku, jadi jangan
                // memakan tempat di kartu Control.
                Root.Visibility = Visibility.Collapsed;
                Status = null;
                return;
            }

            Root.Visibility = Visibility.Visible;
            Status = await HeControlService.GetStatusAsync();
            Apply(Status);
        }
        catch
        {
            Apply(null);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void Apply(HeQueueStatus? status)
    {
        if (status is null)
        {
            StateDot.Fill      = DotUnknown;
            StatusText.Text    = Lang.HeQ_Unreachable;
            DetailText.Visibility  = Visibility.Collapsed;
            LeaveButton.Visibility = Visibility.Collapsed;
            ForceButton.Visibility = Visibility.Collapsed;
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

        LeaveButton.Visibility = status.IAmWaiting ? Visibility.Visible : Visibility.Collapsed;
        // Cabut paksa hanya masuk akal kalau ada kendali milik ORANG LAIN yang
        // perlu dicabut; Admin yang sedang memegang sendiri cukup menekan STOP.
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
        ForceButton.IsEnabled = false;
        try { await HeControlService.ForceReleaseAsync($"Dicabut lewat panel antrian oleh {App.Session.DisplayName}"); }
        finally { ForceButton.IsEnabled = true; }
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
