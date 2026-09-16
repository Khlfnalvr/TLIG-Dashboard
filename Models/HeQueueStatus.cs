namespace TLIGDashboard.Models;

/// <summary>
/// Keadaan antrian dari sudut pandang <b>satu pengguna</b> — yang dipakai panel
/// status di layar dan yang dikirim lewat <c>GET /he/queue</c>.
///
/// Bedanya dengan <see cref="HeQueueSnapshot"/>: snapshot hanya bercerita tentang
/// plant (siapa memegang, siapa mengantre), sedangkan status ini menambahkan
/// jawaban atas pertanyaan yang ditanyakan layar — "saya di posisi berapa?",
/// "apakah saya yang memegang kendali?", "boleh tidak saya mencabut paksa?".
/// Client tidak boleh menghitungnya sendiri: peran dan identitas yang dipakai
/// adalah yang tercatat di sesi milik Server.
/// </summary>
public sealed class HeQueueStatus
{
    public HeQueueSnapshot Snapshot { get; init; } = new();

    /// <summary>Posisi pemohon dalam antrian (1 = berikutnya); 0 kalau tidak mengantre.</summary>
    public int MyPosition { get; init; }

    /// <summary>True kalau pemohon sendiri yang sedang memegang kendali plant.</summary>
    public bool IAmHolder { get; init; }

    /// <summary>True kalau peran pemohon (staf: Admin/Dosen/Asisten) boleh mencabut paksa kendali orang lain.</summary>
    public bool CanForceRelease { get; init; }

    public HeControlHolder? Holder => Snapshot.Holder;
    public bool IsBusy => Snapshot.IsBusy;
    public bool IAmWaiting => MyPosition > 0;
}
