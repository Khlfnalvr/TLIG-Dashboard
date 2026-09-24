using System.Text.Json.Nodes;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Potret telemetri LabVIEW yang direlay Server, dari sudut pandang <b>satu</b>
/// pemanggil — jawaban <c>GET /hmi/latest</c> dalam bentuk objek.
///
/// <para>Sengaja berdiri di berkasnya sendiri, tanpa sentuhan WinUI: tipenya dipakai
/// <see cref="HeQueueClient"/>, dan pemetaan JSON-nya perlu bisa diuji di luar
/// aplikasi.</para>
/// </summary>
public sealed class HmiLatest
{
    /// <summary>
    /// Pemanggil berhak melihat angkanya. Yang memutuskan Server: staf selalu boleh,
    /// Mahasiswa hanya saat dirinya yang memegang giliran. Saat <c>false</c>,
    /// <see cref="Values"/> memang tidak pernah dikirim — bukan dikirim lalu
    /// disembunyikan layar.
    /// </summary>
    public bool Allowed { get; init; }

    /// <summary>Ada VI yang benar-benar tersambung ke listener 6001 milik Server.</summary>
    public bool Linked { get; init; }

    /// <summary>Umur bacaan terakhir dalam milidetik; <c>-1</c> = Server belum pernah menerima apa pun.</summary>
    public long AgeMs { get; init; } = -1;

    public IReadOnlyList<HmiDatum> Values { get; init; } = [];

    /// <summary>
    /// Angkanya layak ditampilkan: berhak, VI tersambung, ada isinya, dan bacaannya
    /// masih baru.
    ///
    /// <para>Batas 3 detik dipilih longgar terhadap polling 1 detik — dua putaran yang
    /// meleset masih ditoleransi, yang ketiga baru dianggap putus. Yang gagal di sini
    /// menjadi "Tidak tersambung" di layar, bukan angka lama yang diam.</para>
    /// </summary>
    public bool IsLive => Allowed && Linked && Values.Count > 0 && AgeMs >= 0 && AgeMs <= 3000;

    /// <summary>
    /// Siapa yang boleh melihat angka LabVIEW. Aturan tunggal, dipakai handler
    /// <c>GET /hmi/latest</c> — ditaruh di sini supaya bisa diuji apa adanya, bukan
    /// disalin ke tes.
    ///
    /// <list type="bullet">
    /// <item><b>Staf</b> (Admin/Dosen/Asisten) selalu boleh: mereka pengawas, dan
    ///   sulit mengambil langkah tanpa melihat suhu rig.</item>
    /// <item><b>Mahasiswa</b> hanya saat dirinya sendiri yang memegang giliran. Yang
    ///   mengantre, yang gilirannya habis kena batas 30 menit, dan yang kendalinya
    ///   dicabut — ketiganya bukan pemegang giliran lagi, jadi berhenti melihat
    ///   angkanya.</item>
    /// </list>
    ///
    /// <para>Perbandingannya <see cref="StringComparison.Ordinal"/>, sama dengan
    /// pemeriksaan kepemilikan lain di sisi Server: identitas login bukan tempat untuk
    /// pencocokan yang longgar.</para>
    /// </summary>
    /// <param name="role">Peran dari sesi login, bukan dari permintaan Client.</param>
    /// <param name="username">Username dari sesi login, bukan dari permintaan Client.</param>
    /// <param name="holder">Pemegang giliran saat ini, atau <c>null</c> kalau plant bebas.</param>
    public static bool MayView(string? role, string? username, HeControlHolder? holder)
    {
        if (UserRoles.IsStaff(role)) return true;
        if (string.IsNullOrEmpty(username) || holder is null) return false;
        return string.Equals(holder.UserId, username, StringComparison.Ordinal);
    }

    /// <summary>
    /// Membaca jawaban Server. Ditulis defensif: field yang hilang atau bertipe aneh
    /// menghasilkan "tidak berhak / tidak hidup", bukan pengecualian — jawaban rusak
    /// tidak boleh membuat kartu di Client ikut mati.
    /// </summary>
    public static HmiLatest FromJson(JsonNode node)
    {
        if ((bool?)node["allowed"] != true) return new HmiLatest { Allowed = false };

        var values = new List<HmiDatum>();
        if (node["values"] is JsonArray arr)
            foreach (var item in arr)
            {
                var key = (string?)item?["k"];
                if (string.IsNullOrEmpty(key)) continue;
                values.Add(new HmiDatum(key, (string?)item?["v"] ?? ""));
            }

        return new HmiLatest
        {
            Allowed = true,
            Linked  = (bool?)node["linked"] ?? false,
            AgeMs   = (long?)node["ageMs"] ?? -1,
            Values  = values,
        };
    }
}
