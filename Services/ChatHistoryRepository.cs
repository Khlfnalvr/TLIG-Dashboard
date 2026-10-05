using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>Satu baris <c>chat_sessions</c> — sesi chat milik satu akun.</summary>
public sealed class ChatSessionRow
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Summary { get; set; } = "";
    public string MessagesJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Penyimpanan riwayat chat AI per akun di sisi Server (sumber kebenaran untuk
/// sync lintas device). Batas yang sama dengan cache lokal diberlakukan di sini
/// (20 sesi; 200 pesan per sesi; ringkasan ≤ 2000 char) supaya satu payload
/// nakal tidak membengkakkan DB maupun respons GET.
/// </summary>
public sealed class ChatHistoryRepository : HeSqliteDatabase
{
    /// <summary>Instance yang dipakai aplikasi Server (lihat <c>App.ChatHistory</c>).</summary>
    public static ChatHistoryRepository Instance { get; } = new();

    public ChatHistoryRepository(string? dbPath = null)
        : base("chatHistory.db", "Database.ChatHistorySchema.sql", dbPath) { }

    public const int MaxSessions = 20;
    public const int MaxMessages = 200;
    public const int MaxSummaryChars = 2000;
    public const int MaxMessageChars = 8000;

    public async Task<List<ChatSessionRow>> GetForUserAsync(
        string username, CancellationToken ct = default)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        return await ReadAsync(async (conn, token) =>
        {
            var list = new List<ChatSessionRow>();
            await using var cmd = Command(conn, null,
                "SELECT session_id, title, summary, messages_json, updated_at_utc " +
                "FROM chat_sessions WHERE username = $u ORDER BY updated_at_utc DESC LIMIT $n");
            Bind(cmd, "$u", username);
            Bind(cmd, "$n", MaxSessions);
            await using var r = await cmd.ExecuteReaderAsync(token);
            while (await r.ReadAsync(token))
            {
                list.Add(new ChatSessionRow
                {
                    SessionId = r.GetString(0),
                    Title = r.IsDBNull(1) ? "" : r.GetString(1),
                    Summary = r.IsDBNull(2) ? "" : r.GetString(2),
                    MessagesJson = r.IsDBNull(3) ? "[]" : r.GetString(3),
                    UpdatedAt = r.IsDBNull(4) ? DateTime.UnixEpoch : FromDbTime(r.GetString(4)),
                });
            }
            return list;
        }, ct);
    }

    /// <summary>
    /// Simpan/overwritesatu sesi. Menang-tulis-terakhir: hanya menimpa bila
    /// baris masuk lebih baru dari yang tersimpan (perbandingan teks ISO-8601
    /// panjang-tetap = perbandingan waktu).
    /// </summary>
    public async Task UpsertAsync(
        string username, ChatSessionRow row, CancellationToken ct = default)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(row.SessionId))
            return;
        Sanitize(row);
        string updated = ToDbTime(row.UpdatedAt);

        await WriteAsync(async (conn, tx, token) =>
        {
            await using var cmd = Command(conn, tx,
                "INSERT INTO chat_sessions (username, session_id, title, summary, messages_json, updated_at_utc) " +
                "VALUES ($u, $s, $t, $sum, $m, $up) " +
                "ON CONFLICT (username, session_id) DO UPDATE SET " +
                "title = excluded.title, summary = excluded.summary, " +
                "messages_json = excluded.messages_json, updated_at_utc = excluded.updated_at_utc " +
                "WHERE excluded.updated_at_utc > chat_sessions.updated_at_utc");
            Bind(cmd, "$u", username);
            Bind(cmd, "$s", row.SessionId);
            Bind(cmd, "$t", row.Title);
            Bind(cmd, "$sum", row.Summary);
            Bind(cmd, "$m", row.MessagesJson);
            Bind(cmd, "$up", updated);
            await cmd.ExecuteNonQueryAsync(token);

            // Jaga 20 sesi per akun: buang yang terlama.
            await using var prune = Command(conn, tx,
                "DELETE FROM chat_sessions WHERE username = $u AND session_id NOT IN " +
                "(SELECT session_id FROM chat_sessions WHERE username = $u " +
                "ORDER BY updated_at_utc DESC LIMIT $n)");
            Bind(prune, "$u", username);
            Bind(prune, "$n", MaxSessions);
            await prune.ExecuteNonQueryAsync(token);
            return 0;
        }, ct);
    }

    /// <summary>Hapus satu sesi (propagasi tombol hapus di Client).</summary>
    public async Task DeleteAsync(
        string username, string sessionId, CancellationToken ct = default)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(sessionId))
            return;
        await WriteAsync(async (conn, tx, token) =>
        {
            await using var cmd = Command(conn, tx,
                "DELETE FROM chat_sessions WHERE username = $u AND session_id = $s");
            Bind(cmd, "$u", username);
            Bind(cmd, "$s", sessionId);
            await cmd.ExecuteNonQueryAsync(token);
            return 0;
        }, ct);
    }
    /// <summary>Potong payload sesuai batas (judul, ringkasan, 200 pesan, 8000 char/pesan).</summary>
    public static void Sanitize(ChatSessionRow row)
    {
        row.Title = (row.Title ?? "").Trim();
        if (row.Title.Length > 100) row.Title = row.Title[..100];
        row.Summary = (row.Summary ?? "").Trim();
        if (row.Summary.Length > MaxSummaryChars)
            row.Summary = row.Summary[^MaxSummaryChars..];
        try
        {
            var arr = JsonNode.Parse(string.IsNullOrWhiteSpace(row.MessagesJson) ? "[]" : row.MessagesJson)?.AsArray()
                      ?? new JsonArray();
            var kept = new JsonArray();
            foreach (var n in arr.TakeLast(MaxMessages))
            {
                string role = (string?)n?["role"] ?? "";
                string content = (string?)n?["content"] ?? "";
                if (role is not ("user" or "assistant") || content.Length == 0) continue;
                if (content.Length > MaxMessageChars) content = content[..MaxMessageChars] + "…";
                kept.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
            row.MessagesJson = kept.ToJsonString();
        }
        catch { row.MessagesJson = "[]"; }
    }
}
