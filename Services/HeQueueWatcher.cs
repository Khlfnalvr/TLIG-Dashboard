using Microsoft.UI.Xaml;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>Seberapa mendesak sebuah pemberitahuan antrian.</summary>
public enum HeQueueNoticeKind { Info, Warning, Error }

/// <summary>Satu pemberitahuan antrian yang perlu dilihat pengguna.</summary>
public sealed record HeQueueNotice(HeQueueNoticeKind Kind, string Title, string Message);

/// <summary>
/// Pengamat antrian plant HE tingkat aplikasi: satu-satunya yang menanyakan keadaan
/// antrian secara berkala, dan satu-satunya yang tahu kapan pengguna perlu diberi
/// tahu sesuatu.
///
/// <para><b>Kenapa di tingkat aplikasi, bukan di dalam panel antriannya.</b> Hal-hal
/// yang harus sampai ke pengguna justru terjadi saat ia sedang <i>tidak</i> melihat
/// panel itu: giliran yang akhirnya tiba setelah menunggu lama, dan peringatan sisa
/// waktu sebelum rig dimatikan otomatis. Panel antrian hanya hidup selama halaman
/// Dashboard terbuka, jadi pemberitahuan yang dipasang di sana akan hilang persis
/// ketika paling dibutuhkan. Panel itu kini ikut membaca dari sini, sehingga seluruh
/// aplikasi hanya punya satu penanya — lebih ringan untuk Server yang juga sedang
/// menggerakkan plant.</para>
/// </summary>
public sealed class HeQueueWatcher
{
    public static HeQueueWatcher Instance { get; } = new();
    private HeQueueWatcher() { }

    /// <summary>
    /// Selang penyegaran. Antrian berubah karena orang lain, jadi satu-satunya cara
    /// tahu adalah bertanya berkala; 3 detik cukup terasa langsung tanpa membanjiri
    /// Server.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _timer;
    private bool             _polling;
    private HeQueueStatus?   _previous;
    private DateTime?        _warnedHold;      // giliran yang peringatannya sudah dihitung
    private readonly HashSet<int> _warnedMinutes = [];
    private bool _ownReleasePending;

    /// <summary>Keadaan antrian yang terakhir terbaca; <c>null</c> kalau tidak terbaca.</summary>
    public HeQueueStatus? Status { get; private set; }

    /// <summary>Setiap kali keadaan antrian selesai dibaca — dipakai panel antrian untuk menggambar.</summary>
    public event Action<HeQueueStatus?>? Changed;

    /// <summary>Ada yang perlu diberitahukan ke pengguna.</summary>
    public event Action<HeQueueNotice>? Notice;

    private static LocalizationManager Lang => LocalizationManager.Instance;

    /// <summary>
    /// Mulai mengamati. Dipanggil sekali dari jendela utama; sebelum ada yang login
    /// setiap putaran hanya memeriksa lalu berhenti, jadi tidak ada biayanya.
    /// </summary>
    public void Start()
    {
        _timer ??= new DispatcherTimer { Interval = Interval };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>
    /// Melupakan keadaan sebelumnya — dipanggil saat berganti pengguna, supaya
    /// giliran milik pengguna lama tidak salah dibaca sebagai peristiwa baru.
    /// </summary>
    public void Reset()
    {
        _previous = Status = null;
        _warnedHold = null;
        _warnedMinutes.Clear();
        _ownReleasePending = false;
    }

    /// <summary>
    /// Memberi tahu pengamat bahwa pelepasan giliran berikutnya adalah kehendak
    /// pengguna sendiri (tombol BERHENTI), jadi tidak perlu diberitakan sebagai
    /// "giliran Anda berakhir" — layar yang menekan tombolnya sudah memberi kabar.
    /// </summary>
    public void ExpectOwnRelease() => _ownReleasePending = true;

    /// <summary>Menyiarkan pemberitahuan dari tempat lain (mis. hasil cabut paksa).</summary>
    public void Announce(HeQueueNoticeKind kind, string title, string message) =>
        Notice?.Invoke(new HeQueueNotice(kind, title, message));

    private void OnTick(object? sender, object e) => _ = RefreshAsync();

    /// <summary>
    /// Membaca ulang keadaan antrian lalu membandingkannya dengan bacaan sebelumnya.
    /// Panggilan yang menumpuk diabaikan: penyegaran lambat (Client yang server-nya
    /// jauh) tidak boleh menumpuk permintaan setiap kali timer berdetak.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (!HeControlService.Enabled)
            {
                if (Status is not null) Reset();
                Changed?.Invoke(null);
                return;
            }

            var now = await HeControlService.GetStatusAsync();
            var previous = _previous;

            Status = now;
            _previous = now;
            Changed?.Invoke(now);

            if (now is not null) Compare(previous, now);
        }
        catch
        {
            Status = null;
            Changed?.Invoke(null);
        }
        finally
        {
            _polling = false;
        }
    }

    // ── Yang layak diberitahukan ────────────────────────────────────────────

    private void Compare(HeQueueStatus? before, HeQueueStatus now)
    {
        bool holdJustStarted = now.IAmHolder && before?.IAmHolder != true;

        // Giliran baru menghapus sisa penanda "pelepasan atas kehendak sendiri".
        // Tanpa ini, penanda yang tidak jadi terpakai (mis. permintaan lepasnya
        // gagal) akan membungkam satu pemberitahuan pemutusan di giliran berikutnya.
        if (holdJustStarted) _ownReleasePending = false;

        // Giliran tiba. Syaratnya "tadinya mengantre", bukan sekadar "sekarang
        // memegang": menekan JALANKAN saat plant bebas juga membuat orang memegang
        // kendali, dan itu jelas bukan kabar yang perlu diumumkan.
        if (holdJustStarted && before is { IAmWaiting: true })
            Notify(HeQueueNoticeKind.Info, Lang.HeQ_TurnArrivedTitle, Lang.HeQ_TurnArrivedMsg);

        // Giliran berakhir bukan atas kehendak sendiri: diputus batas waktu,
        // dicabut staf, atau diambil alih prioritas yang lebih tinggi.
        if (!now.IAmHolder && before is { IAmHolder: true })
        {
            if (_ownReleasePending) _ownReleasePending = false;
            else Notify(HeQueueNoticeKind.Warning, Lang.HeQ_HoldEndedTitle, Lang.HeQ_HoldEndedMsg);
        }

        WarnBeforeCutoff(now);
    }

    /// <summary>
    /// Peringatan sisa waktu untuk giliran yang dibatasi. Tiap ambang diberikan
    /// sekali saja per giliran — <see cref="HeControlHolder.HeldSinceUtc"/> yang
    /// menandai "giliran yang mana", jadi giliran berikutnya (termasuk giliran baru
    /// milik orang yang sama) mendapat peringatannya sendiri.
    /// </summary>
    private void WarnBeforeCutoff(HeQueueStatus now)
    {
        if (!now.IAmHolder || now.Holder is not { } holder) return;
        if (HeControlService.RemainingHold(holder) is not { } left) return;

        if (_warnedHold != holder.HeldSinceUtc)
        {
            _warnedHold = holder.HeldSinceUtc;
            _warnedMinutes.Clear();
        }

        // Kalau beberapa ambang terlewati sekaligus (mis. bacaan pertama datang saat
        // sisa waktunya tinggal setengah menit), semuanya ditandai sudah diberikan
        // tapi yang diumumkan hanya yang paling mendesak — dua kotak peringatan
        // berturut-turut untuk giliran yang sama hanya akan saling menutupi.
        int? announce = null;
        foreach (var threshold in HeControlService.HoldWarnings)
        {
            int minutes = (int)Math.Round(threshold.TotalMinutes);
            if (left > threshold) continue;
            if (!_warnedMinutes.Add(minutes)) continue;
            announce = announce is { } already ? Math.Min(already, minutes) : minutes;
        }

        if (announce is not { } leftMinutes) return;

        Notify(leftMinutes <= 1 ? HeQueueNoticeKind.Error : HeQueueNoticeKind.Warning,
            Lang.HeQ_TimeLeftTitle,
            Lang.Format(nameof(Lang.HeQ_TimeLeftMsg), leftMinutes,
                        (int)Math.Round(HeControlService.MaxHold.TotalMinutes)));
    }

    private void Notify(HeQueueNoticeKind kind, string title, string message) =>
        Notice?.Invoke(new HeQueueNotice(kind, title, message));
}
