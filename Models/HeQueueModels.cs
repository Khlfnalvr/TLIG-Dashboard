using TLIGDashboard.Services;

namespace TLIGDashboard.Models;

/// <summary>
/// Prioritas giliran memakai HE. Angka lebih kecil = lebih didahulukan, sehingga
/// antrian cukup diurutkan "priority ASC, requested_at_utc ASC" (FIFO di dalam
/// prioritas yang sama).
/// </summary>
public enum HeQueuePriority
{
    /// <summary>Admin — paling didahulukan, boleh mengambil alih kendali dari siapa pun.</summary>
    Admin = 1,
    /// <summary>Dosen (termasuk Asisten) — boleh mengambil alih kendali dari Mahasiswa.</summary>
    Dosen = 2,
    /// <summary>Mahasiswa — hanya mengantre, tidak pernah mengambil alih kendali orang lain.</summary>
    Mahasiswa = 3,
}

/// <summary>Jenis permintaan yang butuh kendali plant.</summary>
public enum HeRequestType
{
    Run,
    Stop,
    Reset,
    UpdateParameter,
}

/// <summary>Perjalanan satu permintaan giliran, dari mengantre sampai selesai.</summary>
public enum HeQueueItemStatus
{
    /// <summary>Masih mengantre.</summary>
    Waiting,
    /// <summary>Sedang memegang kendali.</summary>
    Granted,
    /// <summary>Selesai, kendali dilepas sendiri.</summary>
    Released,
    /// <summary>Dibatalkan sebelum dapat giliran.</summary>
    Cancelled,
    /// <summary>Kendalinya diambil alih prioritas yang lebih tinggi.</summary>
    Overridden,
    /// <summary>Dilepas paksa karena melewati batas waktu pegang.</summary>
    Expired,
}

/// <summary>Apa yang terjadi pada satu permintaan giliran.</summary>
public enum HeQueueOutcome
{
    /// <summary>Plant sedang kosong — langsung boleh jalan.</summary>
    Granted,
    /// <summary>Kendali direbut dari pemegang berprioritas lebih rendah.</summary>
    GrantedByOverride,
    /// <summary>Pemohon memang sudah memegang kendali sejak tadi.</summary>
    AlreadyHolding,
    /// <summary>Plant sedang dipakai — pemohon masuk antrian.</summary>
    Queued,
    /// <summary>Pemohon sudah ada di antrian sebelumnya — posisinya tidak berubah.</summary>
    AlreadyQueued,
}

/// <summary>Pemetaan peran akun (<see cref="UserRoles"/>) ke prioritas antrian.</summary>
public static class HeQueuePriorityMap
{
    /// <summary>
    /// Peran akun → prioritas. Asisten disamakan dengan Dosen (keduanya staf
    /// pengajar); peran tak dikenal jatuh ke prioritas terendah supaya akun yang
    /// datanya rusak tidak pernah mendahului siapa pun.
    /// </summary>
    public static HeQueuePriority FromRole(string? role) => role?.Trim() switch
    {
        UserRoles.Admin   => HeQueuePriority.Admin,
        UserRoles.Dosen   => HeQueuePriority.Dosen,
        UserRoles.Asisten => HeQueuePriority.Dosen,
        _                 => HeQueuePriority.Mahasiswa,
    };

    /// <summary>Kolom INTEGER di database → prioritas.</summary>
    public static HeQueuePriority FromDbValue(int value) => value switch
    {
        1 => HeQueuePriority.Admin,
        2 => HeQueuePriority.Dosen,
        _ => HeQueuePriority.Mahasiswa,
    };

    /// <summary>
    /// Apakah <paramref name="requester"/> boleh mengambil alih kendali yang
    /// sedang dipegang <paramref name="holder"/>. Hanya prioritas yang benar-benar
    /// lebih tinggi yang boleh — sesama tingkat tidak saling merebut, jadi Dosen
    /// tidak bisa memotong Dosen lain yang sedang menjalankan percobaan.
    /// </summary>
    public static bool CanOverride(HeQueuePriority requester, HeQueuePriority holder)
        => requester < holder;

    /// <summary>Label prioritas untuk log/laporan.</summary>
    public static string Label(HeQueuePriority priority) => priority switch
    {
        HeQueuePriority.Admin => "Admin",
        HeQueuePriority.Dosen => "Dosen",
        _                     => "Mahasiswa",
    };
}

/// <summary>Satu baris antrian: satu permintaan giliran dari satu pengguna.</summary>
public sealed class HeQueueItem
{
    public long              QueueId        { get; init; }
    public string            UserId         { get; init; } = "";
    public string            DisplayName    { get; init; } = "";
    public HeQueuePriority   Priority       { get; init; }
    public HeRequestType     RequestType    { get; init; }
    public DateTime          RequestedAtUtc { get; init; } = DateTime.UtcNow;
    public DateTime?         GrantedAtUtc   { get; init; }
    public DateTime?         EndedAtUtc     { get; init; }
    public HeQueueItemStatus Status         { get; init; } = HeQueueItemStatus.Waiting;

    /// <summary>Sudah berapa lama menunggu (atau sudah berapa lama memegang kendali).</summary>
    public TimeSpan WaitingFor => DateTime.UtcNow - RequestedAtUtc;
}

/// <summary>Siapa yang sedang memegang kendali plant saat ini.</summary>
public sealed class HeControlHolder
{
    public string          UserId        { get; init; } = "";
    public string          DisplayName   { get; init; } = "";
    public HeQueuePriority Priority      { get; init; }
    public long            QueueId       { get; init; }
    public HeRequestType   RequestType   { get; init; }
    public DateTime        HeldSinceUtc  { get; init; }

    public TimeSpan HeldFor => DateTime.UtcNow - HeldSinceUtc;
}

/// <summary>Potret lengkap keadaan antrian — dipakai untuk menampilkan panel status.</summary>
public sealed class HeQueueSnapshot
{
    /// <summary>Pemegang kendali, atau <c>null</c> kalau plant sedang bebas.</summary>
    public HeControlHolder? Holder { get; init; }

    /// <summary>Antrian yang menunggu, sudah terurut (prioritas dulu, lalu waktu masuk).</summary>
    public IReadOnlyList<HeQueueItem> Waiting { get; init; } = [];

    public bool IsBusy => Holder is not null;
}

/// <summary>Hasil satu permintaan giliran, lengkap dengan keadaan antrian setelahnya.</summary>
public sealed class HeQueueDecision
{
    public HeQueueOutcome Outcome { get; init; }

    /// <summary>Baris antrian milik pemohon (status Granted atau Waiting).</summary>
    public HeQueueItem Item { get; init; } = new();

    /// <summary>Posisi dalam antrian (1 = berikutnya). 0 kalau pemohon sedang memegang kendali.</summary>
    public int Position { get; init; }

    /// <summary>Pemegang kendali setelah keputusan ini diambil.</summary>
    public HeControlHolder? Holder { get; init; }

    /// <summary>Pengguna yang kendalinya direbut, kalau keputusannya override.</summary>
    public HeQueueItem? Overridden { get; init; }

    public bool CanRunNow => Outcome is HeQueueOutcome.Granted
                                     or HeQueueOutcome.GrantedByOverride
                                     or HeQueueOutcome.AlreadyHolding;
}

/// <summary>Satu baris audit log antrian.</summary>
public sealed class HeQueueLogEntry
{
    public long            LogId         { get; init; }
    public string          EventType     { get; init; } = "";
    public string          UserId        { get; init; } = "";
    public string?         DisplayName   { get; init; }
    public HeQueuePriority? Priority     { get; init; }
    public string?         RequestType   { get; init; }
    public long?           QueueId       { get; init; }
    public string?         RelatedUserId { get; init; }
    public DateTime        OccurredAtUtc { get; init; }
    public string?         Note          { get; init; }
}
