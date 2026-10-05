using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>
/// Sync riwayat chat per akun — membuat akun yang sama melihat sesi + summary
/// yang sama di device mana pun. Server adalah sumber kebenaran
/// (<c>chatHistory.db</c>); file JSON lokal di tiap device hanya cache/offline
/// fallback.
///
///   • Build Server: baca/tulis langsung ke <see cref="ChatHistoryRepository"/>.
///   • Build Client: lewat endpoint HTTP (<c>GET /chat/sessions</c> pull,
///     <c>POST /chat/sync</c> push satu sesi), auth Bearer session token —
///     pola yang sama dengan <see cref="SyncClient"/>.
///
/// Aturan merge (dua arah, dipakai Server maupun Client): gabung per session id,
/// yang <c>updatedAt</c> lebih baru menang utuh; cap 20 sesi teratas. Konflik
/// edit dua device offline = sesi utuh milik penulis terakhir yang menang
/// (sederhana, bisa diprediksi; tidak ada merge per-baris).
/// </summary>
public static class ChatHistorySync
{
    public const int MaxSessions = 20;

    // ── Serialisasi sesi (trim-friendly via JsonNode) ──────────────────────

    public static JsonObject ToJson(ChatSession s) => new()
    {
        ["id"] = s.Id,
        ["title"] = s.Title ?? "",
        ["summary"] = s.Summary ?? "",
        ["messages"] = new JsonArray(s.Messages
            .Where(m => m.Role is "user" or "assistant" && m.Content.Length > 0)
            .TakeLast(ChatHistoryRepository.MaxMessages)
            .Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role,
                ["content"] = m.Content.Length > ChatHistoryRepository.MaxMessageChars
                    ? m.Content[..ChatHistoryRepository.MaxMessageChars] + "…"
                    : m.Content,
            }).ToArray()),
        ["updatedAt"] = s.UpdatedAt.ToUniversalTime().ToString("O"),
    };

    public static ChatSession? FromJson(JsonNode? n)
    {
        string id = (string?)n?["id"] ?? "";
        if (id.Length == 0 || id.Length > 64) return null;
        var msgs = new List<ChatMessage>();
        foreach (var m in n?["messages"]?.AsArray() ?? new JsonArray())
        {
            string role = (string?)m?["role"] ?? "";
            string content = (string?)m?["content"] ?? "";
            if (role is "user" or "assistant" && content.Length > 0)
                msgs.Add(new ChatMessage(role, content));
        }
        if (msgs.Count == 0) return null;
        return new ChatSession
        {
            Id = id,
            Title = ((string?)n?["title"] ?? "").Trim(),
            Summary = ((string?)n?["summary"] ?? "").Trim(),
            UpdatedAt = DateTime.TryParse((string?)n?["updatedAt"] ?? "", null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
                ? dt.ToUniversalTime() : DateTime.UtcNow,
            Messages = msgs.TakeLast(ChatHistoryRepository.MaxMessages).ToList(),
        };
    }

    /// <summary>
    /// Gabung daftar lokal + remote. Per id: yang updatedAt lebih baru menang.
    /// Hasil: maksimal <see cref="MaxSessions"/> sesi, terbaru dulu.
    /// </summary>
    public static List<ChatSession> Merge(
        IEnumerable<ChatSession> local, IEnumerable<ChatSession> remote)
    {
        var byId = new Dictionary<string, ChatSession>(StringComparer.Ordinal);
        foreach (var s in local)
        {
            if (!string.IsNullOrEmpty(s.Id) && !byId.ContainsKey(s.Id))
                byId[s.Id] = s;
        }
        foreach (var s in remote)
        {
            if (string.IsNullOrEmpty(s.Id)) continue;
            if (!byId.TryGetValue(s.Id, out var cur) || s.UpdatedAt > cur.UpdatedAt)
                byId[s.Id] = s;
        }
        return byId.Values
            .OrderByDescending(s => s.UpdatedAt)
            .Take(MaxSessions)
            .ToList();
    }

    // ── Pull (server → daftar sesi milik username) ─────────────────────────

    public static async Task<List<ChatSession>> PullAsync(
        string username, CancellationToken ct = default)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        var result = new List<ChatSession>();
        if (username.Length == 0) return result;

        try
        {
            if (BuildInfo.IsServer)
            {
                foreach (var row in await TLIGDashboard.App.ChatHistory
                             .GetForUserAsync(username, ct).ConfigureAwait(false))
                {
                    var s = FromRow(row);
                    if (s is not null) result.Add(s);
                }
                return result;
            }

            var s2 = AppSettingsService.Load();
            if (string.IsNullOrWhiteSpace(s2.ServerHost) || string.IsNullOrWhiteSpace(s2.ServerToken))
                return result;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"{AuthClient.BaseUrl(s2.ServerHost)}{ShareProtocol.ChatSessionsPath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {s2.ServerToken}");
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return result;
            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            foreach (var n in node?["sessions"]?.AsArray() ?? new JsonArray())
            {
                var s = FromJson(n);
                if (s is not null) result.Add(s);
            }
        }
        catch { }
        return result;
    }

    // ── Push (satu sesi → server) ──────────────────────────────────────────

    public static async Task PushAsync(
        string username, ChatSession snapshot, CancellationToken ct = default)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        if (username.Length == 0) return;
        try
        {
            if (BuildInfo.IsServer)
            {
                // Staf di aplikasi Server: tulis langsung ke DB sumber kebenaran.
                await TLIGDashboard.App.ChatHistory.UpsertAsync(
                    username, ToRow(snapshot), ct).ConfigureAwait(false);
                return;
            }

            var s = AppSettingsService.Load();
            if (string.IsNullOrWhiteSpace(s.ServerHost) || string.IsNullOrWhiteSpace(s.ServerToken))
                return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{AuthClient.BaseUrl(s.ServerHost)}{ShareProtocol.ChatSyncPath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {s.ServerToken}");
            req.Content = new StringContent(ToJson(snapshot).ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch { /* fire-and-forget: lokal tetap jadi cache */ }
    }

    // ── Delete (propagasi tombol hapus) ────────────────────────────────────

    public static async Task DeleteAsync(
        string username, string sessionId, CancellationToken ct = default)
    {
        username = (username ?? "").Trim().ToLowerInvariant();
        if (username.Length == 0 || string.IsNullOrWhiteSpace(sessionId)) return;
        try
        {
            if (BuildInfo.IsServer)
            {
                await TLIGDashboard.App.ChatHistory.DeleteAsync(username, sessionId, ct)
                    .ConfigureAwait(false);
                return;
            }

            var s = AppSettingsService.Load();
            if (string.IsNullOrWhiteSpace(s.ServerHost) || string.IsNullOrWhiteSpace(s.ServerToken))
                return;
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{AuthClient.BaseUrl(s.ServerHost)}{ShareProtocol.ChatDeletePath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {s.ServerToken}");
            req.Content = new StringContent(
                new JsonObject { ["id"] = sessionId }.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
            await http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch { }
    }

    // ── Konversi DB row ────────────────────────────────────────────────────

    // ── Konversi DB row ────────────────────────────────────────────────────

    public static ChatSession? FromRow(ChatSessionRow row)
    {
        JsonArray msgs;
        try { msgs = JsonNode.Parse(row.MessagesJson)?.AsArray() ?? new JsonArray(); }
        catch { msgs = new JsonArray(); }
        return FromJson(new JsonObject
        {
            ["id"] = row.SessionId,
            ["title"] = row.Title,
            ["summary"] = row.Summary,
            ["messages"] = msgs,
            ["updatedAt"] = row.UpdatedAt.ToString("O"),
        });
    }

    public static ChatSessionRow ToRow(ChatSession s)
    {
        var row = new ChatSessionRow
        {
            SessionId = s.Id,
            Title = s.Title ?? "",
            Summary = s.Summary ?? "",
            MessagesJson = new JsonArray(s.Messages
                .Where(m => m.Role is "user" or "assistant" && m.Content.Length > 0)
                .TakeLast(ChatHistoryRepository.MaxMessages)
                .Select(m => (JsonNode)new JsonObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content,
                }).ToArray()).ToJsonString(),
            UpdatedAt = s.UpdatedAt,
        };
        ChatHistoryRepository.Sanitize(row);
        return row;
    }
}
