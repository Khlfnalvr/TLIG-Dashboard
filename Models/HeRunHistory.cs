namespace TLIGDashboard.Models;

/// <summary>
/// Isi halaman riwayat percobaan HE: daftar run terbaru beserta ringkasan
/// cache-nya, dalam satu bungkus supaya layar cukup satu kali meminta.
///
/// Kurva respons sengaja TIDAK ikut — satu run bisa ribuan titik dan halaman
/// riwayat hanya menampilkan parameter serta metrik ringkasnya. Detail
/// kurvanya diambil terpisah lewat <see cref="Services.HeParameterCacheRepository.GetRunAsync"/>
/// kalau suatu saat ada layar yang menggambarnya.
/// </summary>
public sealed class HeRunHistory
{
    public IReadOnlyList<HeParameterRun> Runs { get; init; } = [];

    /// <summary>Ringkasan isi cache; <c>null</c> kalau tidak terbaca.</summary>
    public HeParameterCacheStats? Stats { get; init; }

    /// <summary>
    /// False kalau datanya tidak terbaca sama sekali (Server tidak terjangkau,
    /// atau pemanggilnya tidak berhak). Dibedakan dari "terbaca tapi memang
    /// masih kosong", supaya layar bisa berkata jujur mana yang mana.
    /// </summary>
    public bool Ok { get; init; }
}
