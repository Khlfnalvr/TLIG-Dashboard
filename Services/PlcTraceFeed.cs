using Microsoft.UI.Xaml;

namespace TLIGDashboard.Services;

/// <summary>
/// Jawaban <c>GET /hmi/trace</c> dalam bentuk objek (sisi Client).
/// <see cref="Allowed"/> = pemanggil berhak melihat; <see cref="Available"/> = ada kurva yang
/// boleh digambar untuknya. Larik sudah berupa potongan mulai <see cref="PlcTrace.Offset"/>.
/// </summary>
public sealed record PlcTraceReply(bool Allowed, bool Available, PlcTrace? Trace);

/// <summary>
/// Sumber tunggal kurva hasil PLC untuk grafik, apa pun flavor-nya:
///
/// <list type="bullet">
/// <item><b>Server</b> — meneruskan <see cref="PlcTraceService"/> yang diisi langsung dari
///   listener LabVIEW. Semua run tampil, dari mana pun dimulai.</item>
/// <item><b>Client</b> — menanyakan <c>/hmi/trace</c> ke Server tiap detik, hanya mengambil
///   titik yang belum dimiliki. Server hanya menjawab run yang dimulai dari Client, jadi
///   run dari Server tidak pernah sampai ke sini.</item>
/// </list>
///
/// Polanya mengikuti <see cref="HmiRelayService"/>: pelanggan dihitung supaya beberapa
/// grafik (Dashboard dan Cascade) berbagi satu timer.
/// </summary>
public sealed class PlcTraceFeed
{
    public static PlcTraceFeed Instance { get; } = new();
    private PlcTraceFeed() { }

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    /// <summary>Kurva terakhir; <c>null</c> kalau tidak ada yang boleh/perlu digambar.</summary>
    public PlcTrace? Current { get; private set; }

    /// <summary>
    /// Setiap kali <see cref="Current"/> berubah. Bisa dipanggil dari thread latar (Server);
    /// pendengar wajib memindahkannya ke thread UI.
    /// </summary>
    public event Action<PlcTrace?>? Changed;

    private int              _subscribers;
    private DispatcherTimer? _timer;
    private bool             _polling;

    // Akumulasi sisi Client: potongan dari Server ditempel ke sini.
    private long      _runId = -1;
    private bool      _running;
    private string    _origin = PlcTraceService.OriginClient;
    private string?   _owner;
    private double    _setpoint;
    private readonly List<double> _t  = [];
    private readonly List<double> _pv = [];
    private readonly List<double> _fs = [];
    private readonly List<double> _ft = [];

    public void Attach()
    {
        _subscribers++;

        if (BuildInfo.IsServer)
        {
            if (_subscribers == 1)
            {
                PlcTraceService.Instance.Updated -= OnServerUpdated;
                PlcTraceService.Instance.Updated += OnServerUpdated;
            }
            Publish(PlcTraceService.Instance.Snapshot());
            return;
        }

        _timer ??= new DispatcherTimer { Interval = Interval };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Detach()
    {
        if (_subscribers > 0) _subscribers--;
        if (_subscribers > 0) return;

        if (BuildInfo.IsServer) PlcTraceService.Instance.Updated -= OnServerUpdated;
        else _timer?.Stop();
    }

    private void OnServerUpdated() => Publish(PlcTraceService.Instance.Snapshot());

    private void OnTick(object? sender, object e) => _ = RefreshAsync();

    /// <summary>Satu putaran tanya-Server. Putaran yang menumpuk diabaikan.</summary>
    public async Task RefreshAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            if (!SessionService.Instance.IsSignedIn) return;

            var cfg   = AppSettingsService.Load();
            var reply = await HeQueueClient.GetPlcTraceAsync(cfg.ServerHost, cfg.ServerToken, _runId, _t.Count);

            // Server tidak terjangkau: biarkan kurva terakhir apa adanya.
            if (reply is null) return;

            // Belum/tidak lagi berhak (mis. mahasiswa yang gilirannya sudah lepas): kurva yang
            // sudah dimiliki tetap dipajang, hanya saja tidak diperbarui lagi.
            if (!reply.Allowed) return;

            if (!reply.Available || reply.Trace is not { } slice)
            {
                // Tidak ada kurva yang boleh dilihat (belum ada run, atau run terakhir dimulai
                // dari Server): kosongkan, jangan biarkan run lama berpura-pura jadi yang sekarang.
                if (_runId != -1 || Current is not null)
                {
                    ResetAccumulator();
                    Publish(null);
                }
                return;
            }

            Apply(slice);
        }
        catch { /* jaringan: coba lagi di putaran berikutnya */ }
        finally { _polling = false; }
    }

    private void Apply(PlcTrace slice)
    {
        // Run baru, atau potongan tidak menyambung dengan yang sudah ada → mulai dari awal.
        // Offset 0 yang dijawab Server untuk run yang tidak kita kenal otomatis lolos di sini.
        if (slice.RunId != _runId || slice.Offset != _t.Count)
        {
            ResetAccumulator();
            _runId = slice.RunId;
            if (slice.Offset != 0)
            {
                // Potongan bukan dari awal run, padahal kita kosong: minta ulang dari 0 di putaran berikut.
                _runId = -1;
                return;
            }
        }

        bool changed = slice.Count > 0 || slice.Running != _running || slice.Setpoint != _setpoint;

        _t.AddRange(slice.Time);
        _pv.AddRange(slice.PvShellOut);
        _fs.AddRange(slice.FlowShell);
        _ft.AddRange(slice.FlowTube);
        _running  = slice.Running;
        _origin   = slice.Origin;
        _owner    = slice.Owner;
        _setpoint = slice.Setpoint;

        if (changed || Current is null)
            Publish(new PlcTrace
            {
                RunId      = _runId,
                Running    = _running,
                Origin     = _origin,
                Owner      = _owner,
                Setpoint   = _setpoint,
                Total      = _t.Count,
                Time       = _t.ToArray(),
                PvShellOut = _pv.ToArray(),
                FlowShell  = _fs.ToArray(),
                FlowTube   = _ft.ToArray(),
            });
    }

    private void ResetAccumulator()
    {
        _runId = -1;
        _running = false;
        _t.Clear(); _pv.Clear(); _fs.Clear(); _ft.Clear();
    }

    private void Publish(PlcTrace? trace)
    {
        Current = trace;
        try { Changed?.Invoke(trace); } catch { }
    }
}
