using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;

namespace TLIGDashboard.Services;

/// <summary>Jawaban <c>GET /system/status</c>: keadaan sambungan plant di PC Server.</summary>
public sealed record ServerSystemStatus(bool Plc, bool Sensor, bool LabView, bool Bridge)
{
    /// <summary>Field yang hilang dibaca <c>false</c> — jawaban rusak tidak boleh menyalakan lampu.</summary>
    public static ServerSystemStatus FromJson(JsonNode node) => new(
        (bool?)node["plc"]     ?? false,
        (bool?)node["sensor"]  ?? false,
        (bool?)node["labview"] ?? false,
        (bool?)node["bridge"]  ?? false);
}

/// <summary>
/// Sisi Client dari panel "Status System": lampu PLC dan Sensor menyalin keadaan yang
/// dilaporkan Server lewat <c>GET /system/status</c>.
///
/// <para>Client tidak pernah membuka TCP sendiri ke LabVIEW/PLC — Server (lewat LAN atau
/// Cloudflare Tunnel) adalah satu-satunya relay. Jadi "PLC Online" di Client berarti
/// Server tersambung ke PLC, bukan laptop ini.</para>
///
/// <para>Polanya mengikuti <see cref="HmiRelayService"/>: timer UI, putaran yang menumpuk
/// diabaikan, dan Server yang tidak terjangkau / belum login memadamkan lampunya.</para>
/// </summary>
public sealed class ServerStatusRelay
{
    public static ServerStatusRelay Instance { get; } = new();
    private ServerStatusRelay() { }

    /// <summary>Hanya boolean sambungan; tiga detik cukup untuk lampu indikator.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _timer;
    private bool _polling;

    /// <summary>Bacaan terakhir; <c>null</c> kalau Server tidak terjangkau.</summary>
    public ServerSystemStatus? Latest { get; private set; }

    /// <summary>Mulai mengamati (idempoten). Hanya berlaku di Client.</summary>
    public void Start()
    {
        if (!BuildInfo.IsClient || _timer is not null) return;

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            ServerSystemStatus? status = null;
            if (SessionService.Instance.IsSignedIn)
            {
                var cfg = AppSettingsService.Load();
                status = await HeQueueClient.GetSystemStatusAsync(cfg.ServerHost, cfg.ServerToken);
            }
            Apply(status);
        }
        catch
        {
            Apply(null);
        }
        finally
        {
            _polling = false;
        }
    }

    // Dipanggil di thread UI (timer + kelanjutan await), aman untuk binding x:Bind.
    private void Apply(ServerSystemStatus? status)
    {
        Latest = status;
        var panel = SystemStatusService.Instance;
        panel.PlcConnected    = status?.Plc ?? false;
        // Disalin apa adanya: Server sudah memperhitungkan VI di 6001 (dan kesegaran
        // bacaannya, lihat RigStatusService), jadi lampu Client sama persis dengan lampu Server.
        panel.SensorConnected = status?.Sensor ?? false;
    }
}
