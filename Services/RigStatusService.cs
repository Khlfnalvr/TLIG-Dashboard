using Microsoft.UI.Xaml;

namespace TLIGDashboard.Services;

/// <summary>
/// Mengisi baris PLC, Sensor, dan Kamera di panel "Status Sistem" (<see cref="SystemStatusService"/>)
/// dari sumber yang benar untuk tiap flavor:
///
/// <list type="bullet">
/// <item><b>Server</b> — dibaca langsung dari jalur LabVIEW miliknya. <b>PLC online</b> bila
///   tautan TCP ke HMI tersambung (tombol Connect) <i>atau</i> VI sedang tersambung ke
///   listener data 6001 — jalur yang benar-benar dipakai VI sekarang. <b>Sensor aktif</b>
///   bila VI tersambung dan bacaan terakhirnya masih baru (≤ <see cref="FreshWindow"/>).</item>
/// <item><b>Client</b> — tidak punya TCP ke LabVIEW sama sekali, jadi status PLC/Sensor
///   disalin apa adanya dari panel Server lewat <c>GET /hmi/latest</c> (field <c>rig</c>),
///   memakai <see cref="HmiRelayService"/> yang dijaga tetap hidup selama aplikasi berjalan.
///   Server tidak terjangkau = keduanya offline. <b>Kamera</b> online selama siaran kamera
///   dari Server masih mengalir.</item>
/// </list>
///
/// Semua penulisan ke <see cref="SystemStatusService"/> terjadi di thread UI, karena panelnya
/// terikat lewat x:Bind.
/// </summary>
public sealed class RigStatusService
{
    public static RigStatusService Instance { get; } = new();
    private RigStatusService() { }

    /// <summary>Bacaan LabVIEW yang lebih tua dari ini dianggap basi (sama dengan batas kartu LabVIEW Data).</summary>
    public static readonly TimeSpan FreshWindow = TimeSpan.FromSeconds(3);

    /// <summary>Siaran kamera dianggap putus kalau tidak ada frame selama ini.</summary>
    private static readonly TimeSpan CameraWindow = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Server: status tautan TCP ke HMI (<c>MainViewModel.Plc.IsConnected</c>). Disuntikkan
    /// <c>MainWindow</c> karena tautannya milik view-model, bukan layanan statis.
    /// </summary>
    public Func<bool>? PlcTcpConnected { get; set; }

    private DispatcherTimer? _timer;
    private Microsoft.UI.Dispatching.DispatcherQueue? _ui;
    private DateTime _lastCameraFrameUtc = DateTime.MinValue;

    /// <summary>Mulai memantau. Dipanggil sekali dari thread UI saat jendela utama dibuat.</summary>
    public void Start()
    {
        if (_timer is not null) return;
        _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        if (BuildInfo.IsClient)
        {
            HmiRelayService.Instance.Updated += OnRelayUpdated;
            HmiRelayService.Instance.Attach();   // pelanggan permanen: panel status selalu ada
            ShareClient.Instance.FrameReceived += OnRemoteFrame;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>Hitung ulang sekarang (thread UI). Aman dipanggil kapan saja.</summary>
    public void Refresh()
    {
        var status = App.Status;

        if (BuildInfo.IsServer)
        {
            var data   = HmiDataService.Instance;
            bool viLinked = data.HasClient;
            bool fresh    = data.LastReceivedUtc != DateTime.MinValue &&
                            DateTime.UtcNow - data.LastReceivedUtc <= FreshWindow;

            status.PlcConnected    = (PlcTcpConnected?.Invoke() ?? false) || viLinked;
            status.SensorConnected = viLinked && fresh;
            return;
        }

        // Client: PLC/Sensor diperbarui oleh OnRelayUpdated; di sini hanya kamera yang
        // perlu dijatuhkan saat siarannya berhenti (halaman hanya pernah menyalakannya).
        if (DateTime.UtcNow - _lastCameraFrameUtc > CameraWindow)
            status.CameraConnected = false;
    }

    private void OnRelayUpdated(HmiLatest? latest) => _ui?.TryEnqueue(() =>
    {
        App.Status.PlcConnected    = latest?.RigPlc    ?? false;
        App.Status.SensorConnected = latest?.RigSensor ?? false;
    });

    private void OnRemoteFrame(byte channel, byte[] _)
    {
        if (channel != ShareProtocol.ChannelCamera) return;

        // Hanya frame pertama setelah putus yang perlu ke thread UI; sisanya cukup memperbarui waktu.
        var now = DateTime.UtcNow;
        bool resumed = now - _lastCameraFrameUtc > CameraWindow;
        _lastCameraFrameUtc = now;
        if (resumed) _ui?.TryEnqueue(() => App.Status.CameraConnected = true);
    }
}
