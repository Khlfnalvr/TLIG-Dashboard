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
}

public sealed class ChatSessionService
{
    public static ChatSessionService Instance { get; } = new();
    private ChatSessionService() { }

    private const int MaxSessions = 20;
    private const int MaxMessages = 200;
    private const string FileName = "chat-sessions.json";

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
        active.UpdatedAt = DateTime.UtcNow;
        string? firstUser = active.Messages.FirstOrDefault(m => m.Role == "user")?.Content;
        if (!string.IsNullOrWhiteSpace(firstUser) && active.Title == DefaultTitle())
        {
            active.Title = firstUser.Trim().Replace("\n", " ");
            if (active.Title.Length > 34) active.Title = active.Title[..34] + "…";
            ListChanged?.Invoke();
        }
        Persist();
    }

    private void Activate(ChatSession session, bool loadMessages)
    {
        Active = session;
        App.Ai.ClearHistory();
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
