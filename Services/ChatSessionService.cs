using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Windows.Storage;

namespace TLIGDashboard.Services;

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ChatMessage> Messages { get; set; } = new();
    /// <summary>
    /// Rolling long-term memory: ringkasan padat dari pesan-pesan lama yang sudah
    /// dipangkas dari <see cref="Messages"/> agar konteks muat dalam limit token.
    /// Dikelola <see cref="AiService"/> (compact) dan disimpan di sini agar
    /// bertahan antar restart. Disuntik ke system prompt tiap request.
    /// </summary>
    public string Summary { get; set; } = "";
}

public sealed class ChatSessionService
{
    public static ChatSessionService Instance { get; } = new();
    private ChatSessionService() { }

    private const int MaxSessions = 20;
    private const int MaxMessages = 200;

    // ── Isolasi antar pengguna ──────────────────────────────────────────
    // File riwayat di-scope per akun in-app (bukan per device): user A logout
    // lalu user B login di device yang sama TIDAK boleh melihat chat + summary
    // milik A. Antar device beda (laptop client 1 vs 2) memang sudah terpisah
    // karena filenya lokal per device; yang ditutup di sini adalah bocor di
    // device bersama + memory in-memory yang tertinggal sehabis logout.
    private string _owner = "";

    private static string FileNameFor(string owner)
    {
        if (string.IsNullOrWhiteSpace(owner))
            return "chat-sessions.json"; // pra-login / legacy
        foreach (char c in Path.GetInvalidFileNameChars())
            owner = owner.Replace(c, '_');
        return $"chat-sessions-{owner.ToLowerInvariant()}.json";
    }

    private string FileName => FileNameFor(_owner);

    /// <summary>
    /// Ganti pemilik riwayat (login/logout). Menyimpan milik lama, membersihkan
    /// memory, lalu memuat milik yang baru. Aman dipanggil berulang.
    /// </summary>
    public async Task SwitchOwnerAsync(string username)
    {
        string next = (username ?? "").Trim().ToLowerInvariant();
        if (next == _owner && _loaded) return;
        try { SaveActive(); } catch { }
        _owner = next;
        _loaded = false;
        _sessions.Clear();
        Active = null;
        try
        {
            App.Ai.ClearHistory();
            App.Ai.ConversationSummary = "";
        }
        catch { }
        await EnsureLoadedAsync().ConfigureAwait(false);
    }

    private readonly List<ChatSession> _sessions = new();
    private bool _loaded;

    public IReadOnlyList<ChatSession> Sessions => _sessions;
    public ChatSession? Active { get; private set; }

    public event Action? ContentChanged;
    public event Action? ListChanged;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
            if (file is StorageFile sf)
            {
                string json = await FileIO.ReadTextAsync(sf);
                var arr = JsonNode.Parse(json)?.AsArray();
                if (arr is not null)
                {
                    foreach (var n in arr)
                    {
                        var msgs = new List<ChatMessage>();
                        foreach (var m in n?["messages"]?.AsArray() ?? new JsonArray())
                        {
                            string role = (string?)m?["role"] ?? "";
                            string content = (string?)m?["content"] ?? "";
                            if (role is "user" or "assistant" && content.Length > 0)
                                msgs.Add(new ChatMessage(role, content));
                        }
                        if (msgs.Count == 0) continue;
                        _sessions.Add(new ChatSession
                        {
                            Id = (string?)n?["id"] ?? Guid.NewGuid().ToString("N"),
                            Title = (string?)n?["title"] ?? DefaultTitle(),
                            UpdatedAt = DateTime.TryParse((string?)n?["updatedAt"] ?? "", out var dt) ? dt : DateTime.UtcNow,
                            Messages = msgs.TakeLast(MaxMessages).ToList(),
                            Summary = (string?)n?["summary"] ?? "",
                        });
                    }
                    _sessions.Sort((a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
                    while (_sessions.Count > MaxSessions) _sessions.RemoveAt(_sessions.Count - 1);
                }
            }
        }
        catch { }

        if (_sessions.Count == 0)
            _sessions.Add(new ChatSession { Title = DefaultTitle() });
        Activate(_sessions[0], loadMessages: true);
        ContentChanged?.Invoke();
        ListChanged?.Invoke();

        // Sync lintas device: akun yang sama melihat sesi yang sama di mana pun.
        // Lokal tampil dulu (cepat, offline-safe); hasil server di-merge setelahnya.
        if (_owner.Length > 0)
            _ = PullAndMergeAsync(_owner);
    }

    /// <summary>
    /// Tarik milik <paramref name="username"/> dari server (atau DB langsung di
    /// build Server), gabung dengan cache lokal (yang updatedAt lebih baru
    /// menang), lalu tampilkan. Tidak pernah menghapus: gagal jaringan = cache
    /// lokal tetap dipakai apa adanya.
    /// </summary>
    private async Task PullAndMergeAsync(string username)
    {
        List<ChatSession> remote;
        try { remote = await ChatHistorySync.PullAsync(username).ConfigureAwait(false); }
        catch { return; }
        if (remote.Count == 0) return;

        string? activeId = Active?.Id;
        var merged = ChatHistorySync.Merge(_sessions, remote);
        if (merged.Count == 0) return;
        _sessions.Clear();
        _sessions.AddRange(merged);
        // Pertahankan sesi aktif (bukan lompat ke sesi lain), tapi pakai versi
        // gabungannya yang mungkin lebih baru dari remote.
        var target = _sessions.FirstOrDefault(s => s.Id == activeId) ?? _sessions[0];
        Activate(target, loadMessages: true);
        Persist();
        ContentChanged?.Invoke();
        ListChanged?.Invoke();
    }

    public void NewSession()
    {
        SaveActive();
        var s = new ChatSession { Title = DefaultTitle() };
        _sessions.Insert(0, s);
        while (_sessions.Count > MaxSessions) _sessions.RemoveAt(_sessions.Count - 1);
        Activate(s, loadMessages: false);
        Persist();
        ContentChanged?.Invoke();
        ListChanged?.Invoke();
    }

    public void SwitchTo(string id)
    {
        var target = _sessions.FirstOrDefault(s => s.Id == id);
        if (target is null || target == Active) return;
        SaveActive();
        _sessions.Remove(target);
        _sessions.Insert(0, target);
        Activate(target, loadMessages: true);
        ContentChanged?.Invoke();
        ListChanged?.Invoke();
    }

    public void Delete(string id)
    {
        var target = _sessions.FirstOrDefault(s => s.Id == id);
        if (target is null) return;
        _sessions.Remove(target);
        if (target == Active)
        {
            if (_sessions.Count == 0)
                _sessions.Add(new ChatSession { Title = DefaultTitle() });
            Activate(_sessions[0], loadMessages: true);
            ContentChanged?.Invoke();
        }
        Persist();
        // Hapus juga di server agar sesi tidak hidup lagi saat pull berikutnya.
        if (_owner.Length > 0)
            _ = ChatHistorySync.DeleteAsync(_owner, id);
        ListChanged?.Invoke();
    }

    public void SaveActive()
    {
        var active = Active;
        if (active is null) return;
        var history = App.Ai.History;
        active.Messages = history
            .Where(m => m.Role is "user" or "assistant")
            .TakeLast(MaxMessages)
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();
        active.Summary = App.Ai.ConversationSummary ?? "";
        active.UpdatedAt = DateTime.UtcNow;
        string? firstUser = active.Messages.FirstOrDefault(m => m.Role == "user")?.Content;
        if (!string.IsNullOrWhiteSpace(firstUser) && active.Title == DefaultTitle())
        {
            active.Title = firstUser.Trim().Replace("\n", " ");
            if (active.Title.Length > 34) active.Title = active.Title[..34] + "…";
            ListChanged?.Invoke();
        }
        Persist();
        // Sync lintas device: dorong sesi aktif ke server (sumber kebenaran)
        // agar akun yang sama melihatnya di device lain. Snapshot disalin agar
        // aman dari balapan dengan turn berikutnya. Fire-and-forget: gagal =
        // cache lokal tetap utuh, didorong lagi saat turn berikutnya.
        if (_owner.Length > 0)
        {
            var snapshot = new ChatSession
            {
                Id = active.Id,
                Title = active.Title,
                UpdatedAt = active.UpdatedAt,
                Summary = active.Summary,
                Messages = active.Messages.ToList(),
            };
            _ = ChatHistorySync.PushAsync(_owner, snapshot);
        }
        // Long-term memory: padatkan pesan lama jadi ringkasan di background.
        // Menutup semua jalur (chat, routed answer, advisor) karena semuanya
        // bermuara ke SaveActive. Fire-and-forget: gagal = riwayat tetap utuh.
        _ = CompactInBackgroundAsync(active.Id);
    }

    private async Task CompactInBackgroundAsync(string sessionId)
    {
        string? newSummary;
        int dropped;
        try
        {
            (newSummary, dropped) = await App.Ai.MaybeCompactAsync().ConfigureAwait(false);
        }
        catch { return; }
        if (newSummary is null || dropped <= 0) return;

        var target = _sessions.FirstOrDefault(s => s.Id == sessionId);
        if (target is null) return;
        target.Summary = newSummary;
        // Selaraskan pesan tersimpan dengan history yang sudah dipangkas.
        var history = App.Ai.History;
        target.Messages = history
            .Where(m => m.Role is "user" or "assistant")
            .TakeLast(MaxMessages)
            .Select(m => new ChatMessage(m.Role, m.Content))
            .ToList();
        Persist();
        // Ringkasan baru juga harus naik ke server, kalau tidak device lain
        // dapat pesannya tanpa memorinya.
        if (_owner.Length > 0)
        {
            var snapshot = new ChatSession
            {
                Id = target.Id,
                Title = target.Title,
                UpdatedAt = target.UpdatedAt,
                Summary = target.Summary,
                Messages = target.Messages.ToList(),
            };
            _ = ChatHistorySync.PushAsync(_owner, snapshot);
        }
    }

    private void Activate(ChatSession session, bool loadMessages)
    {
        Active = session;
        App.Ai.ClearHistory();
        App.Ai.ConversationSummary = session.Summary ?? "";
        if (loadMessages)
            foreach (var m in session.Messages)
                App.Ai.AddHistoryEntry(m.Role, m.Content);
    }

    private static string DefaultTitle() =>
        LocalizationManager.Instance.CurrentLanguage == "id" ? "Percakapan baru" : "New chat";

    private void Persist()
    {
        var snapshot = _sessions.Select(s => new ChatSession
        {
            Id = s.Id,
            Title = s.Title,
            UpdatedAt = s.UpdatedAt,
            Messages = s.Messages.ToList(),
            Summary = s.Summary ?? "",
        }).ToList();
        _ = Task.Run(async () =>
        {
            try
            {
                var arr = new JsonArray();
                foreach (var s in snapshot)
                {
                    var msgs = new JsonArray();
                    foreach (var m in s.Messages)
                        msgs.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
                    arr.Add(new JsonObject
                    {
                        ["id"] = s.Id,
                        ["title"] = s.Title,
                        ["updatedAt"] = s.UpdatedAt.ToString("O"),
                        ["messages"] = msgs,
                        ["summary"] = s.Summary ?? "",
                    });
                }
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, arr.ToJsonString());
            }
            catch { }
        });
    }
}
