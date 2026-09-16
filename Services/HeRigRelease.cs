using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>Hasil satu upaya melepas giliran plant HE.</summary>
/// <param name="Released">Giliran benar-benar berpindah (atau memang tidak ada yang perlu dilepas).</param>
/// <param name="RigStopped">Perintah BERHENTI sampai ke plant.</param>
/// <param name="Problem">Alasan singkat kalau ada yang tidak beres — untuk ditampilkan apa adanya.</param>
public sealed record HeRigReleaseResult(bool Released, bool RigStopped, string? Problem)
{
    /// <summary>Pemanggilnya bukan pemegang kendali: tidak ada yang dikerjakan, dan itu bukan kegagalan.</summary>
    public static readonly HeRigReleaseResult NotHolder = new(false, true, null);
}

/// <summary>
/// Satu-satunya jalan melepas giliran plant HE di sisi <b>Server</b>, dan satu-satunya
/// tempat yang tahu urutannya: <b>hentikan rig dulu, baru lepas gilirannya</b>.
///
/// <para>Urutan itu yang penting. Melepas giliran lebih dulu berarti orang berikutnya
/// bisa mendapat rig yang masih menyala dengan setpoint dan gain milik orang
/// sebelumnya — di plant penukar kalor yang berisi air panas, itu bukan sekadar
/// data yang kotor.</para>
///
/// <para><b>Perintah berhentinya memakai jalur yang sudah terbukti</b>, yaitu
/// melatch CMD=0 lewat <see cref="PythonBridgeService.LatchCommand"/> — paket yang
/// sama persis dengan yang dikirim tombol BERHENTI di layar. Bukan
/// <see cref="PythonBridgeService.Stop"/>: itu mematikan proses Python, koneksi 6000
/// putus, dan VI hanya menerima satu koneksi per Run.</para>
///
/// <para><b>Dua jalur, sengaja berbeda saat BERHENTI gagal.</b></para>
/// <list type="bullet">
/// <item><b>Otomatis</b> (batas waktu terlampaui, aplikasi ditutup, logout) —
///   kalau rig tidak bisa dihentikan, giliran <b>tidak</b> dilepas. Menyerahkan rig
///   yang masih panas kepada mahasiswa berikutnya tanpa ada yang tahu adalah hal
///   terakhir yang boleh terjadi.</item>
/// <item><b>Cabut paksa oleh staf</b> — giliran <b>tetap</b> dilepas walau BERHENTI
///   gagal, dan stafnya diberi tahu bahwa rig mungkin masih menyala. Kalau tidak,
///   satu-satunya jalan keluar dari rig yang macet ikut terkunci, dan antrian tidak
///   punya pintu darurat sama sekali.</item>
/// </list>
/// </summary>
public static class HeRigRelease
{
    /// <summary>
    /// Kabar bahwa sebuah pelepasan otomatis tidak bisa menghentikan rig. Dipakai
    /// layar Server untuk memberi tahu operator — tanpa ini, satu-satunya jejaknya
    /// hanya di log debug, dan rig yang macet akan ditemukan jauh terlambat.
    /// </summary>
    public static event Action<string>? Trouble;

    private static LocalizationManager Lang => LocalizationManager.Instance;

    /// <summary>Catatan run untuk percobaan yang terputus, lengkap dengan penandanya.</summary>
    public static string InterruptedNote(string reason) =>
        $"{HeParameterRun.InterruptedMarker} {reason}";

    // ── Menghentikan rig ────────────────────────────────────────────────────

    /// <summary>
    /// Mengirim BERHENTI ke plant. <c>true</c> juga ketika memang tidak ada yang
    /// perlu dihentikan.
    ///
    /// <para>"Tidak ada yang perlu dihentikan" penting untuk tidak salah kaprah:
    /// giliran bisa jatuh ke tangan seseorang lewat antrian tanpa ia pernah menekan
    /// JALANKAN. Kalau keadaan itu dianggap "BERHENTI gagal", giliran tersebut tidak
    /// akan pernah bisa dilepas otomatis — antrian terkunci selamanya justru oleh
    /// penjagaan yang dimaksudkan untuk mengamankannya.</para>
    /// </summary>
    private static (bool Ok, string? Problem) StopRig()
    {
        var bridge = PythonBridgeService.Instance;
        bool running   = bridge.IsRunning;
        bool recording = HeRunRecorder.Instance.IsRecording;

        if (!running && !recording) return (true, null);   // tidak ada yang berjalan

        if (!running) return (false, Lang.HeQ_RigNoBridge);

        return bridge.LatchCommand(PythonBridgeService.CmdStop)
            ? (true, null)
            : (false, Lang.HeQ_RigWriteFailed);
    }

    // ── Jalur 1: pemegang giliran melepas sendiri ───────────────────────────

    /// <summary>
    /// Melepas giliran atas nama pemegangnya sendiri — tombol BERHENTI, logout,
    /// atau aplikasi ditutup.
    /// </summary>
    /// <param name="userId">Yang melepas. Bukan pemegang kendali → tidak terjadi apa-apa.</param>
    /// <param name="status">Status akhir rekaman run-nya.</param>
    /// <param name="note">Catatan yang ikut tersimpan pada run.</param>
    /// <param name="requireRigStop">
    /// <c>true</c> untuk jalur otomatis (logout / aplikasi ditutup): giliran hanya
    /// dilepas kalau rig benar-benar berhenti. <c>false</c> untuk tombol BERHENTI
    /// yang ditekan sendiri — orangnya ada di depan layar dan melihat pesannya,
    /// jadi menahan giliran di sana hanya akan mengunci antrian pada jalur yang
    /// paling sering dipakai.
    /// </param>
    public static async Task<HeRigReleaseResult> ByHolderAsync(
        string userId, HeParameterRunStatus status, string? note,
        bool requireRigStop, CancellationToken ct = default)
    {
        var holder = (await App.HeQueue.GetSnapshotAsync(ct)).Holder;
        if (holder is null || holder.UserId != userId) return HeRigReleaseResult.NotHolder;

        var (stopped, problem) = StopRig();
        if (!stopped && requireRigStop)
        {
            Report(Lang.Format(nameof(Lang.HeQ_TroubleHoldKept), holder.DisplayName, problem ?? "-"));
            return new HeRigReleaseResult(false, false, problem);
        }

        await HeRunRecorder.Instance.FinishAsync(status, note, userId);
        await App.HeQueue.ReleaseControlAsync(userId, ct);
        return new HeRigReleaseResult(true, stopped, problem);
    }

    // ── Jalur 2: pemutusan otomatis karena batas waktu ──────────────────────

    /// <summary>
    /// Memutus giliran yang sudah melewati <paramref name="maxHold"/>. Rig
    /// dihentikan dulu; kalau itu gagal, gilirannya sengaja <b>tidak</b> dilepas.
    /// </summary>
    /// <param name="onlyPriority">
    /// Batas waktu ini hanya berlaku untuk satu tingkat prioritas (Mahasiswa).
    /// Pemeriksaannya diulang di dalam transaksi pelepasan, jadi giliran yang
    /// berpindah tangan tepat saat penjaga berjalan tidak ikut terputus.
    /// </param>
    public static async Task<HeRigReleaseResult> ExpireAsync(
        TimeSpan maxHold, HeQueuePriority onlyPriority, string reason, CancellationToken ct = default)
    {
        var holder = (await App.HeQueue.GetSnapshotAsync(ct)).Holder;
        if (holder is null || holder.Priority != onlyPriority || holder.HeldFor <= maxHold)
            return HeRigReleaseResult.NotHolder;

        var (stopped, problem) = StopRig();
        if (!stopped)
        {
            Report(Lang.Format(nameof(Lang.HeQ_TroubleExpired), holder.DisplayName, problem ?? "-"));
            return new HeRigReleaseResult(false, false, problem);
        }

        await HeRunRecorder.Instance.FinishAsync(
            HeParameterRunStatus.Aborted, InterruptedNote(reason), holder.UserId);

        // Nilai baliknya adalah pemegang giliran BERIKUTNYA (null kalau antrian
        // kosong), bukan tanda berhasil — syarat batas waktunya sudah diperiksa
        // ulang di dalam transaksi repository, jadi tidak ada yang perlu dinilai
        // lagi di sini.
        await App.HeQueue.ExpireStaleHolderAsync(maxHold, onlyPriority, ct);
        return new HeRigReleaseResult(true, true, null);
    }

    // ── Jalur 3: cabut paksa oleh staf ──────────────────────────────────────

    /// <summary>
    /// Staf mencabut giliran yang tertinggal. Rig dicoba dihentikan lebih dulu,
    /// tapi gilirannya <b>tetap</b> dilepas walau perintah itu gagal — inilah pintu
    /// darurat antrian, dan pintu darurat yang bisa ikut macet bukan pintu darurat.
    /// <see cref="HeRigReleaseResult.RigStopped"/> bernilai <c>false</c> di situ,
    /// supaya stafnya diberi tahu rig mungkin masih menyala.
    /// </summary>
    public static async Task<HeRigReleaseResult> ForceAsync(
        string byUserId, string? note, CancellationToken ct = default)
    {
        var holder = (await App.HeQueue.GetSnapshotAsync(ct)).Holder;
        if (holder is null) return HeRigReleaseResult.NotHolder;

        var (stopped, problem) = StopRig();
        if (!stopped) Report(Lang.Format(nameof(Lang.HeQ_TroubleForced), problem ?? "-"));

        await HeRunRecorder.Instance.FinishAsync(HeParameterRunStatus.Aborted,
            InterruptedNote(note ?? "Dicabut paksa oleh staf"), holder.UserId);
        await App.HeQueue.ForceReleaseAsync(byUserId, note, ct);

        return new HeRigReleaseResult(true, stopped, problem);
    }

    private static void Report(string message)
    {
        System.Diagnostics.Debug.WriteLine($"HE rig release: {message}");
        try { Trouble?.Invoke(message); } catch { }
    }
}
