using Microsoft.UI.Xaml;

namespace TLIGDashboard.Services;

/// <summary>
/// Sisi Client dari kartu "LabVIEW Data": menanyakan telemetri terkini ke Server
/// secara berkala lewat <c>GET /hmi/latest</c>.
///
/// <para><b>Kenapa harus direlay.</b> VI membuka koneksinya ke <c>127.0.0.1:6001</c>,
/// alamat loopback — jadi hanya dashboard yang berjalan di PC yang sama dengan VI
/// (yaitu Server) yang bisa berada di jalur itu. <see cref="HmiDataService"/> milik
/// Client punya listener sendiri yang di laptop mahasiswa selamanya sepi. Polanya
/// mengikuti <c>LiveProgressService</c>: Server membaca state lokalnya, Client
/// memakai jawaban Server apa adanya.</para>
///
/// <para><b>Sengaja berdiri sendiri, tidak menyuntik ke <see cref="HmiDataService"/>.</b>
/// Snapshot layanan itu juga dibaca <c>ProcessErrorService</c> tanpa penjagaan flavor;
/// menyuntikkan nilai relay ke sana akan menghidupkan fitur-fitur lain di Client yang
/// selama ini memang mati. Kartu ini tidak perlu sejauh itu.</para>
///
/// <para>Siapa yang berhak melihat angkanya diputuskan <b>Server</b> dari identitas
/// sesi. Yang tidak berhak tidak pernah menerima <c>values</c> sama sekali, jadi
/// tidak ada yang bisa dibuka dengan mengutak-atik sisi Client.</para>
/// </summary>
public sealed class HmiRelayService
{
    public static HmiRelayService Instance { get; } = new();
    private HmiRelayService() { }

    /// <summary>
    /// Selang penyegaran. Satu detik memberi jeda yang jauh di bawah syarat dua detik,
    /// dan badan jawabannya hanya ratusan byte — jauh lebih ringan daripada frame JPEG
    /// yang sudah mengalir di soket siaran yang sama.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private DispatcherTimer? _timer;
    private bool _polling;
    private int  _subscribers;

    /// <summary>Bacaan terakhir; <c>null</c> kalau Server tidak terjangkau.</summary>
    public HmiLatest? Latest { get; private set; }

    /// <summary>Setiap kali satu putaran selesai — <c>null</c> berarti Server tidak terjangkau.</summary>
    public event Action<HmiLatest?>? Updated;

    /// <summary>
    /// Mulai mengamati. Memakai hitungan pelanggan supaya dua kartu (Dashboard dan
    /// Live View) berbagi satu timer, dan timernya baru berhenti saat keduanya lepas.
    /// Hanya berlaku di Client; di Server tidak ada yang perlu direlay.
    /// </summary>
    public void Attach()
    {
        if (!BuildInfo.IsClient) return;

        _subscribers++;
        _timer ??= new DispatcherTimer { Interval = Interval };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Detach()
    {
        if (!BuildInfo.IsClient) return;

        if (_subscribers > 0) _subscribers--;
        if (_subscribers == 0) _timer?.Stop();
    }

    private void OnTick(object? sender, object e) => _ = RefreshAsync();

    /// <summary>
    /// Menanyakan keadaan terkini ke Server. Panggilan yang menumpuk diabaikan:
    /// Server yang jauh tidak boleh membuat permintaan menumpuk tiap detak timer.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (!SessionService.Instance.IsSignedIn)
            {
                Publish(null);
                return;
            }

            var cfg = AppSettingsService.Load();
            Publish(await HeQueueClient.GetHmiLatestAsync(cfg.ServerHost, cfg.ServerToken));
        }
        catch
        {
            Publish(null);
        }
        finally
        {
            _polling = false;
        }
    }

    private void Publish(HmiLatest? latest)
    {
        Latest = latest;
        try { Updated?.Invoke(latest); } catch { }
    }
}
