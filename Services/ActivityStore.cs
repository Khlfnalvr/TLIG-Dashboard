using System.IO;
using System.Text.Json;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Persists user activity events to <c>%LOCALAPPDATA%\TLIGDashboard\activities.json</c>
/// for Learning Analytics. Call <see cref="Log"/> or <see cref="LogSession"/> from any
/// service or page when a trackable action occurs.
/// </summary>
public sealed class ActivityStore
{
    public static ActivityStore Instance { get; } = new();

    private readonly object _lock = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard", "activities.json");

    private ActivityLogFile _file = new();

    /// <summary>Raised on the calling thread after each new activity is persisted.</summary>
    public event Action? Changed;

    private ActivityStore() { Load(); }

    public string FilePath => _path;

    // ── Logging API ───────────────────────────────────────────────────────────

    /// <summary>Logs an activity with explicit identity fields.</summary>
    public void Log(
        string           username,
        string           displayName,
        string           role,
        ActivityCategory category,
        string           action,
        string           description,
        string?          relatedId = null,
        Dictionary<string, string>? metadata = null)
    {
        var entry = new ActivityLog
        {
            Id           = Guid.NewGuid().ToString("N")[..12],
            TimestampUtc = DateTime.UtcNow,
            Username     = username    ?? "",
            DisplayName  = string.IsNullOrWhiteSpace(displayName) ? (username ?? "") : displayName,
            Role         = role        ?? "",
            Category     = category,
            Action       = action,
            Description  = description ?? "",
            RelatedId    = relatedId   ?? "",
            Metadata     = metadata    ?? new(),
        };

        lock (_lock)
        {
            _file.Activities.Add(entry);
            SaveLocked();
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// Logs an activity using the identity of the currently signed-in session.
    /// Convenience wrapper around <see cref="Log"/>.
    /// </summary>
    public void LogSession(
        ActivityCategory category,
        string           action,
        string           description,
        string?          relatedId = null,
        Dictionary<string, string>? metadata = null)
    {
        var s = SessionService.Instance;
        Log(s.Username, s.DisplayName, s.Role, category, action, description, relatedId, metadata);
    }

    /// <summary>Inserts a pre-built ActivityLog entry (used for server-side sync from CLIENT).</summary>
    public void LogExternal(ActivityLog entry)
    {
        lock (_lock)
        {
            if (_file.Activities.Any(a => a.Id == entry.Id)) return;
            _file.Activities.Add(entry);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    // ── Query API ─────────────────────────────────────────────────────────────

    public IReadOnlyList<ActivityLog> GetAll()
    {
        lock (_lock)
            return _file.Activities.OrderByDescending(a => a.TimestampUtc).ToList();
    }

    public IReadOnlyList<ActivityLog> GetByUser(string username)
    {
        lock (_lock)
            return _file.Activities
                .Where(a => string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.TimestampUtc)
                .ToList();
    }

    public IReadOnlyList<ActivityLog> GetFiltered(
        string?           userQuery = null,
        ActivityCategory? category  = null,
        DateTime?         fromUtc   = null,
        DateTime?         toUtc     = null)
    {
        lock (_lock)
        {
            IEnumerable<ActivityLog> q = _file.Activities;

            if (!string.IsNullOrWhiteSpace(userQuery))
                q = q.Where(a =>
                    a.Username.Contains(userQuery, StringComparison.OrdinalIgnoreCase) ||
                    a.DisplayName.Contains(userQuery, StringComparison.OrdinalIgnoreCase));

            if (category.HasValue)
                q = q.Where(a => a.Category == category.Value);

            if (fromUtc.HasValue)
                q = q.Where(a => a.TimestampUtc >= fromUtc.Value);

            if (toUtc.HasValue)
                q = q.Where(a => a.TimestampUtc <= toUtc.Value);

            return q.OrderByDescending(a => a.TimestampUtc).ToList();
        }
    }

    /// <summary>Total number of recorded activities.</summary>
    public int Count
    {
        get { lock (_lock) return _file.Activities.Count; }
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _file = JsonSerializer.Deserialize(
                        File.ReadAllText(_path),
                        AppJsonContext.Default.ActivityLogFile) ?? new();
            }
            catch { _file = new(); }
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_file, AppJsonContext.Default.ActivityLogFile));
        }
        catch { }
    }
}
