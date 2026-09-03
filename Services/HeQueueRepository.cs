using System.Data.Common;
using Microsoft.Data.Sqlite;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Antrian giliran memakai plant HE, disimpan di SQLite
/// (<c>%LOCALAPPDATA%\TLIGDashboard\heQueue.db</c>).
///
/// Aturan mainnya cuma tiga:
///
///   1. Plant dipegang <b>satu orang</b> pada satu waktu.
///   2. Kalau plant sedang dipakai, pemohon masuk antrian; giliran berikutnya
///      diambil dari prioritas tertinggi lebih dulu — <b>Admin → Dosen (dan
///      Asisten) → Mahasiswa</b> — dan di dalam prioritas yang sama yang lebih
///      dulu meminta yang lebih dulu dilayani.
///   3. Prioritas yang lebih tinggi boleh <b>mengambil alih</b> kendali dari
///      yang lebih rendah. Yang diambil alih tidak dibuang: ia dikembalikan ke
///      antrian dengan waktu permintaan aslinya, jadi ia berada di depan
///      antrean tingkatnya dan dapat giliran lagi begitu plant bebas.
///
/// Kenapa di database dan bukan di memori: Server bisa di-restart di tengah
/// praktikum, dan antrian yang hilang berarti semua orang harus meminta ulang.
/// Selain itu setiap kejadian dicatat di <c>he_queue_log</c> sehingga bisa
/// ditelusuri siapa memegang plant kapan, dan siapa mengambil alih giliran siapa.
///
/// Seluruh keputusan (baca keadaan → putuskan → tulis) berjalan di dalam satu
/// transaksi dan satu gerbang tulis (lihat <see cref="HeSqliteDatabase"/>),
/// sehingga dua permintaan yang datang berbarengan tidak bisa sama-sama merasa
/// mendapat giliran.
/// </summary>
public sealed class HeQueueRepository : HeSqliteDatabase
{
    /// <summary>Instance yang dipakai aplikasi Server (lihat <c>App.HeQueue</c>).</summary>
    public static HeQueueRepository Instance { get; } = new();

    public HeQueueRepository(string? dbPath = null)
        : base("heQueue.db", "Database.HeQueueSchema.sql", dbPath) { }

    private const string ItemColumns =
        "queue_id, user_id, display_name, priority, request_type, requested_at_utc, granted_at_utc, ended_at_utc, status";

    // ── Baca keadaan ────────────────────────────────────────────────────────

    /// <summary>Pemegang kendali saat ini + seluruh antrian yang menunggu (sudah terurut).</summary>
    public Task<HeQueueSnapshot> GetSnapshotAsync(CancellationToken ct = default) =>
        ReadAsync<HeQueueSnapshot>(async (conn, token) => new HeQueueSnapshot
        {
            Holder  = await ReadHolderAsync(conn, null, token),
            Waiting = await ReadWaitingAsync(conn, null, token),
        }, ct);

    /// <summary>
    /// Posisi pengguna dalam antrian: 1 = paling depan (berikutnya dilayani),
    /// 0 = tidak sedang mengantre.
    /// </summary>
    public Task<int> GetPositionAsync(string userId, CancellationToken ct = default) =>
        ReadAsync<int>(async (conn, token) =>
        {
            var item = await FindItemAsync(conn, null, userId, HeQueueItemStatus.Waiting, token);
            return item is null ? 0 : await PositionOfAsync(conn, null, item, token);
        }, ct);

    /// <summary>Riwayat kejadian terbaru — untuk panel histori dan laporan praktikum.</summary>
    public Task<IReadOnlyList<HeQueueLogEntry>> GetRecentLogAsync(int limit = 100, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<HeQueueLogEntry>>(async (conn, token) =>
        {
            var rows = new List<HeQueueLogEntry>();
            await using var cmd = Command(conn, null, """
                SELECT log_id, event_type, user_id, display_name, priority, request_type,
                       queue_id, related_user_id, occurred_at_utc, note
                FROM he_queue_log
                ORDER BY occurred_at_utc DESC, log_id DESC
                LIMIT $limit;
                """);
            Bind(cmd, "$limit", limit);

            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add(new HeQueueLogEntry
                {
                    LogId         = reader.GetInt64(0),
                    EventType     = reader.GetString(1),
                    UserId        = reader.GetString(2),
                    DisplayName   = ReadString(reader, 3),
                    Priority      = reader.IsDBNull(4) ? null : HeQueuePriorityMap.FromDbValue(reader.GetInt32(4)),
                    RequestType   = ReadString(reader, 5),
                    QueueId       = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    RelatedUserId = ReadString(reader, 7),
                    OccurredAtUtc = FromDbTime(reader.GetString(8)),
                    Note          = ReadString(reader, 9),
                });
            }
            return rows;
        }, ct);

    // ── Meminta giliran ─────────────────────────────────────────────────────

    /// <summary>
    /// Meminta kendali plant. Hasilnya bisa langsung dapat giliran, dapat giliran
    /// dengan mengambil alih pemegang berprioritas lebih rendah, atau masuk
    /// antrian — lihat <see cref="HeQueueDecision.Outcome"/> dan
    /// <see cref="HeQueueDecision.CanRunNow"/>.
    /// </summary>
    /// <param name="userId">Username akun (sama dengan <c>UserAccount.Username</c>).</param>
    /// <param name="role">Peran akun; dipetakan ke prioritas oleh <see cref="HeQueuePriorityMap"/>.</param>
    public Task<HeQueueDecision> RequestControlAsync(
        string userId, string displayName, string role,
        HeRequestType requestType = HeRequestType.Run,
        CancellationToken ct = default) =>
        WriteAsync<HeQueueDecision>(async (conn, tx, token) =>
        {
            var priority = HeQueuePriorityMap.FromRole(role);
            await UpsertUserAsync(conn, tx, userId, displayName, role, priority, token);

            var holder = await ReadHolderAsync(conn, tx, token);

            // Plant bebas tapi masih ada yang mengantre (mis. Server sempat mati
            // saat giliran berpindah): layani antrian dulu supaya urutannya tidak
            // terlompati oleh siapa pun yang kebetulan menekan RUN duluan.
            HeQueueItem? promoted = null;
            if (holder is null)
            {
                promoted = await GrantNextAsync(conn, tx, token);
                holder   = await ReadHolderAsync(conn, tx, token);
            }

            // Sudah memegang kendali — permintaan berikutnya tidak mengubah apa pun.
            // Kecuali kalau kendali baru saja jatuh ke tangannya lewat antrian di
            // atas: itu memang giliran baru, bukan permintaan yang mubazir.
            if (holder is not null && holder.UserId == userId)
            {
                var current = await FindItemAsync(conn, tx, userId, HeQueueItemStatus.Granted, token);
                return new HeQueueDecision
                {
                    Outcome = promoted?.UserId == userId ? HeQueueOutcome.Granted : HeQueueOutcome.AlreadyHolding,
                    Item    = current ?? promoted ?? new HeQueueItem(),
                    Holder  = holder,
                };
            }

            // Plant benar-benar bebas → langsung jalan.
            if (holder is null)
            {
                var granted = await GrantToAsync(conn, tx, userId, displayName, priority, requestType, token);
                await LogAsync(conn, tx, "Granted", granted, token);
                return new HeQueueDecision
                {
                    Outcome = HeQueueOutcome.Granted,
                    Item    = granted,
                    Holder  = await ReadHolderAsync(conn, tx, token),
                };
            }

            // Plant dipakai orang lain, dan pemohon berhak mengambil alih.
            if (HeQueuePriorityMap.CanOverride(priority, holder.Priority))
            {
                var victim = await FindItemAsync(conn, tx, holder.UserId, HeQueueItemStatus.Granted, token);
                // Tutup baris giliran korban lewat id barisnya sendiri; id di
                // he_control_state hanya dipakai kalau baris itu tidak ketemu.
                await EndItemAsync(conn, tx, victim?.QueueId ?? holder.QueueId, HeQueueItemStatus.Overridden,
                    $"Diambil alih oleh {displayName} ({HeQueuePriorityMap.Label(priority)})", token);
                await ClearHolderAsync(conn, tx, token);

                if (victim is not null)
                {
                    await LogAsync(conn, tx, "Overridden", victim, token,
                        relatedUserId: userId,
                        note: $"Kendali diambil alih oleh {displayName}");

                    // Kembalikan yang diambil alih ke antrian dengan waktu permintaan
                    // ASLI-nya, jadi ia berada paling depan di tingkatnya, bukan
                    // dilempar ke belakang antrean.
                    await InsertItemAsync(conn, tx, victim.UserId, victim.DisplayName, victim.Priority,
                        victim.RequestType, victim.RequestedAtUtc, HeQueueItemStatus.Waiting, token);
                }

                var granted = await GrantToAsync(conn, tx, userId, displayName, priority, requestType, token);
                await LogAsync(conn, tx, "GrantedByOverride", granted, token, relatedUserId: holder.UserId);

                return new HeQueueDecision
                {
                    Outcome    = HeQueueOutcome.GrantedByOverride,
                    Item       = granted,
                    Holder     = await ReadHolderAsync(conn, tx, token),
                    Overridden = victim,
                };
            }

            // Sisanya: mengantre.
            var waiting = await FindItemAsync(conn, tx, userId, HeQueueItemStatus.Waiting, token);
            var outcome = waiting is null ? HeQueueOutcome.Queued : HeQueueOutcome.AlreadyQueued;

            if (waiting is null)
            {
                waiting = await InsertItemAsync(conn, tx, userId, displayName, priority,
                    requestType, DateTime.UtcNow, HeQueueItemStatus.Waiting, token);
                await LogAsync(conn, tx, "Queued", waiting, token, relatedUserId: holder.UserId);
            }

            return new HeQueueDecision
            {
                Outcome  = outcome,
                Item     = waiting,
                Position = await PositionOfAsync(conn, tx, waiting, token),
                Holder   = holder,
            };
        }, ct);

    // ── Melepas / membatalkan ───────────────────────────────────────────────

    /// <summary>
    /// Melepas kendali (tombol STOP / selesai percobaan) lalu memberikan giliran
    /// kepada antrean berikutnya. Mengembalikan pemegang giliran yang baru, atau
    /// <c>null</c> kalau antrian kosong. Bukan pemegang kendali → tidak terjadi apa-apa.
    /// </summary>
    public Task<HeQueueItem?> ReleaseControlAsync(string userId, CancellationToken ct = default) =>
        WriteAsync<HeQueueItem?>(async (conn, tx, token) =>
        {
            var holder = await ReadHolderAsync(conn, tx, token);
            if (holder is null || holder.UserId != userId) return null;

            var item = await FindItemAsync(conn, tx, userId, HeQueueItemStatus.Granted, token);
            await EndItemAsync(conn, tx, item?.QueueId ?? holder.QueueId, HeQueueItemStatus.Released, null, token);
            await ClearHolderAsync(conn, tx, token);
            if (item is not null) await LogAsync(conn, tx, "Released", item, token);

            return await GrantNextAsync(conn, tx, token);
        }, ct);

    /// <summary>Keluar dari antrian sebelum giliran datang. <c>false</c> kalau memang tidak sedang mengantre.</summary>
    public Task<bool> CancelRequestAsync(string userId, CancellationToken ct = default) =>
        WriteAsync<bool>(async (conn, tx, token) =>
        {
            var item = await FindItemAsync(conn, tx, userId, HeQueueItemStatus.Waiting, token);
            if (item is null) return false;

            await EndItemAsync(conn, tx, item.QueueId, HeQueueItemStatus.Cancelled, null, token);
            await LogAsync(conn, tx, "Cancelled", item, token);
            return true;
        }, ct);

    /// <summary>
    /// Mencabut paksa kendali dari pemegangnya (dipakai Admin saat seseorang lupa
    /// menekan STOP atau kliennya mati), lalu melanjutkan ke antrean berikutnya.
    /// </summary>
    public Task<HeQueueItem?> ForceReleaseAsync(string byUserId, string? note = null, CancellationToken ct = default) =>
        WriteAsync<HeQueueItem?>(async (conn, tx, token) =>
        {
            var holder = await ReadHolderAsync(conn, tx, token);
            if (holder is null) return null;

            var item = await FindItemAsync(conn, tx, holder.UserId, HeQueueItemStatus.Granted, token);
            await EndItemAsync(conn, tx, item?.QueueId ?? holder.QueueId, HeQueueItemStatus.Released,
                note ?? "Dilepas paksa", token);
            await ClearHolderAsync(conn, tx, token);
            if (item is not null)
                await LogAsync(conn, tx, "ForceReleased", item, token, relatedUserId: byUserId, note: note);

            return await GrantNextAsync(conn, tx, token);
        }, ct);

    /// <summary>
    /// Melepas kendali yang sudah dipegang melebihi <paramref name="maxHold"/>
    /// (mis. klien putus tanpa menekan STOP) supaya antrian tidak macet selamanya.
    /// Panggil berkala dari timer Server. <c>null</c> kalau tidak ada yang perlu dilepas.
    /// </summary>
    public Task<HeQueueItem?> ExpireStaleHolderAsync(TimeSpan maxHold, CancellationToken ct = default) =>
        WriteAsync<HeQueueItem?>(async (conn, tx, token) =>
        {
            var holder = await ReadHolderAsync(conn, tx, token);
            if (holder is null || holder.HeldFor <= maxHold) return null;

            var item = await FindItemAsync(conn, tx, holder.UserId, HeQueueItemStatus.Granted, token);
            await EndItemAsync(conn, tx, item?.QueueId ?? holder.QueueId, HeQueueItemStatus.Expired,
                $"Melewati batas pegang {maxHold.TotalMinutes:0} menit", token);
            await ClearHolderAsync(conn, tx, token);
            if (item is not null)
                await LogAsync(conn, tx, "Expired", item, token,
                    note: $"Melewati batas pegang {maxHold.TotalMinutes:0} menit");

            return await GrantNextAsync(conn, tx, token);
        }, ct);

    // ── Internal: pengguna ──────────────────────────────────────────────────

    private static async Task UpsertUserAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string userId, string displayName, string role, HeQueuePriority priority, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            INSERT INTO he_queue_users (user_id, display_name, role, priority, updated_at_utc)
            VALUES ($userId, $displayName, $role, $priority, $now)
            ON CONFLICT(user_id) DO UPDATE SET
                display_name   = excluded.display_name,
                role           = excluded.role,
                priority       = excluded.priority,
                updated_at_utc = excluded.updated_at_utc;
            """);
        Bind(cmd, "$userId", userId);
        Bind(cmd, "$displayName", displayName);
        Bind(cmd, "$role", role);
        Bind(cmd, "$priority", (int)priority);
        Bind(cmd, "$now", ToDbTime(DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Internal: baris antrian ─────────────────────────────────────────────

    private static async Task<HeQueueItem> InsertItemAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string userId, string displayName, HeQueuePriority priority, HeRequestType requestType,
        DateTime requestedAtUtc, HeQueueItemStatus status, CancellationToken ct)
    {
        var grantedAt = status == HeQueueItemStatus.Granted ? DateTime.UtcNow : (DateTime?)null;

        await using var cmd = Command(conn, tx, """
            INSERT INTO he_queue_items
                (user_id, display_name, priority, request_type, requested_at_utc, granted_at_utc, status)
            VALUES
                ($userId, $displayName, $priority, $requestType, $requestedAt, $grantedAt, $status)
            RETURNING queue_id;
            """);
        Bind(cmd, "$userId", userId);
        Bind(cmd, "$displayName", displayName);
        Bind(cmd, "$priority", (int)priority);
        Bind(cmd, "$requestType", requestType.ToString());
        Bind(cmd, "$requestedAt", ToDbTime(requestedAtUtc));
        Bind(cmd, "$grantedAt", grantedAt is null ? null : ToDbTime(grantedAt.Value));
        Bind(cmd, "$status", status.ToString());

        var queueId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        return new HeQueueItem
        {
            QueueId        = queueId,
            UserId         = userId,
            DisplayName    = displayName,
            Priority       = priority,
            RequestType    = requestType,
            RequestedAtUtc = requestedAtUtc,
            GrantedAtUtc   = grantedAt,
            Status         = status,
        };
    }

    /// <summary>
    /// Memberi kendali kepada satu pengguna: memakai baris antriannya yang sudah
    /// ada kalau ia memang sedang mengantre (waktu permintaan aslinya terjaga),
    /// atau membuat baris baru kalau ia langsung dapat giliran.
    /// </summary>
    private static async Task<HeQueueItem> GrantToAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string userId, string displayName, HeQueuePriority priority, HeRequestType requestType,
        CancellationToken ct)
    {
        var waiting = await FindItemAsync(conn, tx, userId, HeQueueItemStatus.Waiting, ct);
        var item = waiting is null
            ? await InsertItemAsync(conn, tx, userId, displayName, priority, requestType,
                                    DateTime.UtcNow, HeQueueItemStatus.Granted, ct)
            : await PromoteToGrantedAsync(conn, tx, waiting, ct);

        await SetHolderAsync(conn, tx, item, ct);
        return item;
    }

    private static async Task<HeQueueItem> PromoteToGrantedAsync(
        SqliteConnection conn, SqliteTransaction? tx, HeQueueItem waiting, CancellationToken ct)
    {
        var grantedAt = DateTime.UtcNow;
        await using var cmd = Command(conn, tx, """
            UPDATE he_queue_items
            SET status = 'Granted', granted_at_utc = $grantedAt
            WHERE queue_id = $queueId;
            """);
        Bind(cmd, "$grantedAt", ToDbTime(grantedAt));
        Bind(cmd, "$queueId", waiting.QueueId);
        await cmd.ExecuteNonQueryAsync(ct);

        return new HeQueueItem
        {
            QueueId        = waiting.QueueId,
            UserId         = waiting.UserId,
            DisplayName    = waiting.DisplayName,
            Priority       = waiting.Priority,
            RequestType    = waiting.RequestType,
            RequestedAtUtc = waiting.RequestedAtUtc,
            GrantedAtUtc   = grantedAt,
            Status         = HeQueueItemStatus.Granted,
        };
    }

    /// <summary>Memberikan giliran kepada antrean paling depan. <c>null</c> kalau antrian kosong.</summary>
    private static async Task<HeQueueItem?> GrantNextAsync(
        SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        HeQueueItem? next;
        await using (var cmd = Command(conn, tx, $"""
            SELECT {ItemColumns}
            FROM he_queue_items
            WHERE status = 'Waiting'
            ORDER BY priority ASC, requested_at_utc ASC, queue_id ASC
            LIMIT 1;
            """))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            next = await reader.ReadAsync(ct) ? ReadItem(reader) : null;
        }

        if (next is null) return null;

        var granted = await PromoteToGrantedAsync(conn, tx, next, ct);
        await SetHolderAsync(conn, tx, granted, ct);
        await LogAsync(conn, tx, "GrantedFromQueue", granted, ct);
        return granted;
    }

    private static async Task EndItemAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        long queueId, HeQueueItemStatus status, string? note, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            UPDATE he_queue_items
            SET status = $status, ended_at_utc = $endedAt, note = COALESCE($note, note)
            WHERE queue_id = $queueId;
            """);
        Bind(cmd, "$status", status.ToString());
        Bind(cmd, "$endedAt", ToDbTime(DateTime.UtcNow));
        Bind(cmd, "$note", note);
        Bind(cmd, "$queueId", queueId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<HeQueueItem?> FindItemAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string userId, HeQueueItemStatus status, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, $"""
            SELECT {ItemColumns}
            FROM he_queue_items
            WHERE user_id = $userId AND status = $status
            LIMIT 1;
            """);
        Bind(cmd, "$userId", userId);
        Bind(cmd, "$status", status.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadItem(reader) : null;
    }

    private static async Task<IReadOnlyList<HeQueueItem>> ReadWaitingAsync(
        SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        var rows = new List<HeQueueItem>();
        await using var cmd = Command(conn, tx, $"""
            SELECT {ItemColumns}
            FROM he_queue_items
            WHERE status = 'Waiting'
            ORDER BY priority ASC, requested_at_utc ASC, queue_id ASC;
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadItem(reader));
        return rows;
    }

    private static async Task<int> PositionOfAsync(
        SqliteConnection conn, SqliteTransaction? tx, HeQueueItem item, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            SELECT COUNT(*) + 1
            FROM he_queue_items
            WHERE status = 'Waiting'
              AND (priority < $priority
                   OR (priority = $priority AND requested_at_utc < $requestedAt)
                   OR (priority = $priority AND requested_at_utc = $requestedAt AND queue_id < $queueId));
            """);
        Bind(cmd, "$priority", (int)item.Priority);
        Bind(cmd, "$requestedAt", ToDbTime(item.RequestedAtUtc));
        Bind(cmd, "$queueId", item.QueueId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private static HeQueueItem ReadItem(DbDataReader reader) => new()
    {
        QueueId        = reader.GetInt64(0),
        UserId         = reader.GetString(1),
        DisplayName    = reader.GetString(2),
        Priority       = HeQueuePriorityMap.FromDbValue(reader.GetInt32(3)),
        RequestType    = Enum.Parse<HeRequestType>(reader.GetString(4)),
        RequestedAtUtc = FromDbTime(reader.GetString(5)),
        GrantedAtUtc   = ReadTime(reader, 6),
        EndedAtUtc     = ReadTime(reader, 7),
        Status         = Enum.Parse<HeQueueItemStatus>(reader.GetString(8)),
    };

    // ── Internal: status kendali (baris tunggal) ────────────────────────────

    private static async Task<HeControlHolder?> ReadHolderAsync(
        SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            SELECT holder_user_id, holder_display_name, holder_priority,
                   holder_queue_id, holder_request_type, held_since_utc
            FROM he_control_state
            WHERE id = 1;
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || reader.IsDBNull(0)) return null;

        return new HeControlHolder
        {
            UserId       = reader.GetString(0),
            DisplayName  = ReadString(reader, 1) ?? reader.GetString(0),
            Priority     = reader.IsDBNull(2) ? HeQueuePriority.Mahasiswa : HeQueuePriorityMap.FromDbValue(reader.GetInt32(2)),
            QueueId      = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
            RequestType  = reader.IsDBNull(4) ? HeRequestType.Run : Enum.Parse<HeRequestType>(reader.GetString(4)),
            HeldSinceUtc = ReadTime(reader, 5) ?? DateTime.UtcNow,
        };
    }

    private static async Task SetHolderAsync(
        SqliteConnection conn, SqliteTransaction? tx, HeQueueItem item, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            UPDATE he_control_state
            SET holder_user_id      = $userId,
                holder_display_name = $displayName,
                holder_priority     = $priority,
                holder_queue_id     = $queueId,
                holder_request_type = $requestType,
                held_since_utc      = $heldSince
            WHERE id = 1;
            """);
        Bind(cmd, "$userId", item.UserId);
        Bind(cmd, "$displayName", item.DisplayName);
        Bind(cmd, "$priority", (int)item.Priority);
        Bind(cmd, "$queueId", item.QueueId);
        Bind(cmd, "$requestType", item.RequestType.ToString());
        Bind(cmd, "$heldSince", ToDbTime(item.GrantedAtUtc ?? DateTime.UtcNow));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task ClearHolderAsync(
        SqliteConnection conn, SqliteTransaction? tx, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            UPDATE he_control_state
            SET holder_user_id      = NULL,
                holder_display_name = NULL,
                holder_priority     = NULL,
                holder_queue_id     = NULL,
                holder_request_type = NULL,
                held_since_utc      = NULL
            WHERE id = 1;
            """);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Internal: audit log ─────────────────────────────────────────────────

    private static async Task LogAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string eventType, HeQueueItem item, CancellationToken ct,
        string? relatedUserId = null, string? note = null)
    {
        await using var cmd = Command(conn, tx, """
            INSERT INTO he_queue_log
                (event_type, user_id, display_name, priority, request_type,
                 queue_id, related_user_id, occurred_at_utc, note)
            VALUES
                ($eventType, $userId, $displayName, $priority, $requestType,
                 $queueId, $relatedUserId, $now, $note);
            """);
        Bind(cmd, "$eventType", eventType);
        Bind(cmd, "$userId", item.UserId);
        Bind(cmd, "$displayName", item.DisplayName);
        Bind(cmd, "$priority", (int)item.Priority);
        Bind(cmd, "$requestType", item.RequestType.ToString());
        Bind(cmd, "$queueId", item.QueueId);
        Bind(cmd, "$relatedUserId", relatedUserId);
        Bind(cmd, "$now", ToDbTime(DateTime.UtcNow));
        Bind(cmd, "$note", note);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
