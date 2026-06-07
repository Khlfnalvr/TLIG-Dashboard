using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>
/// Client-side helper that talks to a <see cref="ShareServer"/>'s task endpoints
/// over HTTP, presenting the session token as the Bearer credential. Mirrors the
/// style of <see cref="AuthClient"/>. JSON is built/parsed with <see cref="JsonNode"/>
/// (reflection-free, trim-safe).
/// </summary>
public static class TaskClient
{
    public static async Task<LearningTaskService.LoadResult> GetTasksAsync(string host, string token)
    {
        var result = new LearningTaskService.LoadResult();
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return result;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{AuthClient.BaseUrl(host)}{ShareProtocol.TasksPath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return result;

            var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
            result.CanEdit = (bool?)node?["canEdit"] ?? false;
            if (node?["tasks"] is JsonArray arr)
            {
                foreach (var item in arr)
                {
                    if (item is null) continue;
                    var t = TaskFromJson(item);
                    result.Tasks.Add(t);
                    if ((bool?)item["completed"] ?? false) result.Completed.Add(t.Id);
                }
            }
            result.Ok = true;
        }
        catch { /* unreachable / malformed → return what we have (Ok stays false) */ }
        return result;
    }

    public static Task<bool> SaveAsync(string host, string token, LearningTask t) =>
        PostAsync(host, token, ShareProtocol.TasksPath, new JsonObject
        {
            ["id"]        = t.Id,
            ["title"]     = t.Title,
            ["objective"] = t.Objective,
            ["metric"]    = t.Metric,
            ["op"]        = t.Op,
            ["target"]    = t.Target,
            ["tolerance"] = t.Tolerance,
        });

    public static Task<bool> DeleteAsync(string host, string token, string id) =>
        PostAsync(host, token, ShareProtocol.TasksDeletePath, new JsonObject { ["id"] = id });

    public static Task<bool> SetCompletedAsync(string host, string token, string id, bool completed) =>
        PostAsync(host, token, ShareProtocol.TasksCompletePath, new JsonObject { ["id"] = id, ["completed"] = completed });

    // ── Internals ───────────────────────────────────────────────────────────────

    private static async Task<bool> PostAsync(string host, string token, string path, JsonObject body)
    {
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) || string.IsNullOrWhiteSpace(token))
            return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{path}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
            using var resp = await http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static LearningTask TaskFromJson(JsonNode item) => new()
    {
        Id        = (string?)item["id"]        ?? "",
        Title     = (string?)item["title"]     ?? "",
        Objective = (string?)item["objective"] ?? "",
        Metric    = (string?)item["metric"]    ?? "",
        Op        = (string?)item["op"]        ?? TaskOps.Lte,
        Target    = (double?)item["target"]    ?? 0,
        Tolerance = (double?)item["tolerance"] ?? 0,
        CreatedBy = (string?)item["createdBy"] ?? "",
    };
}

/// <summary>
/// Single integration point the UI uses for tasks, hiding the server-vs-client
/// difference: on the <b>Server</b> flavor it talks to the local <see cref="TaskStore"/>
/// directly; on the <b>Client</b> flavor it goes over HTTP via <see cref="TaskClient"/>
/// to the server the user is signed in to. Authoring (save/delete) is staff-only —
/// enforced both here and authoritatively on the server.
/// </summary>
public static class LearningTaskService
{
    public sealed class LoadResult
    {
        public List<LearningTask> Tasks     { get; } = new();
        public HashSet<string>    Completed { get; } = new(StringComparer.Ordinal);
        public bool               CanEdit   { get; set; }
        public bool               Ok        { get; set; }
    }

    public static async Task<LoadResult> LoadAsync()
    {
        if (BuildInfo.IsServer)
        {
            var user   = SessionService.Instance.Username;
            var result = new LoadResult { CanEdit = SessionService.Instance.IsStaff, Ok = true };
            result.Tasks.AddRange(TaskStore.Instance.GetTasks());
            foreach (var id in TaskStore.Instance.GetCompletedIds(user)) result.Completed.Add(id);
            return result;
        }

        var s = AppSettingsService.Load();
        return await TaskClient.GetTasksAsync(s.ServerHost, s.ServerToken);
    }

    public static async Task<bool> SaveAsync(LearningTask task)
    {
        if (!SessionService.Instance.IsStaff) return false;

        if (BuildInfo.IsServer)
        {
            if (string.IsNullOrWhiteSpace(task.CreatedBy)) task.CreatedBy = SessionService.Instance.Username;
            if (!TaskMetrics.IsValid(task.Metric)) task.Metric = TaskMetrics.None;
            if (!TaskOps.IsValid(task.Op))         task.Op     = TaskOps.Lte;
            TaskStore.Instance.Upsert(task);
            return true;
        }

        var s = AppSettingsService.Load();
        return await TaskClient.SaveAsync(s.ServerHost, s.ServerToken, task);
    }

    public static async Task<bool> DeleteAsync(string id)
    {
        if (!SessionService.Instance.IsStaff) return false;

        if (BuildInfo.IsServer) { return TaskStore.Instance.Delete(id); }

        var s = AppSettingsService.Load();
        return await TaskClient.DeleteAsync(s.ServerHost, s.ServerToken, id);
    }

    public static async Task<bool> SetCompletedAsync(string id, bool completed)
    {
        if (BuildInfo.IsServer)
        {
            TaskStore.Instance.SetCompleted(SessionService.Instance.Username, id, completed);
            return true;
        }

        var s = AppSettingsService.Load();
        return await TaskClient.SetCompletedAsync(s.ServerHost, s.ServerToken, id, completed);
    }
}
