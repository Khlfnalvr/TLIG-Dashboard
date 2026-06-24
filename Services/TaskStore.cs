using System.IO;
using System.Text.Json;

namespace TLIGDashboard.Services;

/// <summary>Target metric a structured task can be checked against (matches the Dashboard cards).</summary>
public static class TaskMetrics
{
    public const string None             = "";
    public const string RiseTime         = "RiseTime";
    public const string Overshoot        = "Overshoot";
    public const string Settling         = "Settling";
    public const string SteadyStateError = "SteadyStateError";

    public static readonly string[] All = [None, RiseTime, Overshoot, Settling, SteadyStateError];

    public static bool IsValid(string? m) => Array.IndexOf(All, m ?? "") >= 0;
}

/// <summary>Comparison operator for a structured task target.</summary>
public static class TaskOps
{
    public const string Lte    = "<=";
    public const string Gte    = ">=";
    public const string Approx = "~";

    public static readonly string[] All = [Lte, Gte, Approx];

    public static bool IsValid(string? op) => Array.IndexOf(All, op ?? "") >= 0;
}

/// <summary>
/// A learning task authored by staff (Dosen/Asisten). It pairs a free-form
/// objective with an optional structured target (a control metric + operator +
/// value + tolerance) so completion can later be verified — manually for now,
/// AI-assisted in a future pass.
/// </summary>
public sealed class LearningTask
{
    public string   Id         { get; set; } = "";
    public string   Title      { get; set; } = "";
    public string   Objective  { get; set; } = "";   // free-form description of what to achieve
    public string   Metric     { get; set; } = "";   // TaskMetrics.* ("" = no structured target)
    public string   Op         { get; set; } = TaskOps.Lte;
    public double   Target     { get; set; }
    public double   Tolerance  { get; set; }
    public string   CreatedBy  { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    public bool HasStructuredTarget => TaskMetrics.IsValid(Metric) && Metric != TaskMetrics.None;

    /// <summary>
    /// Maximum time allotted for the simulation session in minutes.
    /// 0 = no time limit. Default = 45 minutes (recommended for a standard lab session).
    /// </summary>
    public int SimulationTimeMinutes { get; set; } = 45;
}

/// <summary>A record that a given user has completed a given task.</summary>
public sealed class TaskCompletion
{
    public string   Username     { get; set; } = "";
    public string   TaskId       { get; set; } = "";
    public DateTime CompletedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>On-disk envelope for the task database (allows future migrations).</summary>
public sealed class TasksFile
{
    public int                  Version     { get; set; } = 1;
    public List<LearningTask>   Tasks       { get; set; } = new();
    public List<TaskCompletion> Completions { get; set; } = new();
}

/// <summary>
/// Server-side database of learning tasks and per-student completion records,
/// persisted to <c>%LOCALAPPDATA%\TLIGDashboard\tasks.json</c>. Authored by staff
/// and consumed by students; clients reach it over the share protocol (see
/// <c>ShareServer</c> task endpoints + <c>TaskClient</c>), while the server flavor
/// talks to it directly via <see cref="LearningTaskService"/>.
/// </summary>
public sealed class TaskStore
{
    public static TaskStore Instance { get; } = new();

    private readonly object _lock = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard", "tasks.json");

    private TasksFile _file = new();

    /// <summary>Raised whenever tasks or completions change.</summary>
    public event Action? Changed;

    private TaskStore() { Load(); }

    public string FilePath => _path;

    // ── Reads ───────────────────────────────────────────────────────────────────

    public IReadOnlyList<LearningTask> GetTasks()
    {
        lock (_lock)
            return _file.Tasks.Select(Clone).OrderBy(t => t.CreatedUtc).ToList();
    }

    /// <summary>The set of task ids the given user has completed.</summary>
    public HashSet<string> GetCompletedIds(string username)
    {
        username = (username ?? "").Trim();
        lock (_lock)
            return _file.Completions
                .Where(c => string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.TaskId)
                .ToHashSet(StringComparer.Ordinal);
    }

    // ── Writes (task authoring — staff only, enforced at the call sites) ─────────

    /// <summary>
    /// Inserts or updates a task. A blank <see cref="LearningTask.Id"/> creates a new
    /// task (a fresh id is generated); otherwise the matching task is replaced.
    /// Returns the stored task (with its id populated).
    /// </summary>
    public LearningTask Upsert(LearningTask task)
    {
        lock (_lock)
        {
            var stored = string.IsNullOrEmpty(task.Id) ? null : FindLocked(task.Id);
            if (stored is null)
            {
                task.Id         = string.IsNullOrEmpty(task.Id) ? Guid.NewGuid().ToString("N") : task.Id;
                task.CreatedUtc = DateTime.UtcNow;
                task.UpdatedUtc = task.CreatedUtc;
                _file.Tasks.Add(Clone(task));
            }
            else
            {
                stored.Title                 = task.Title;
                stored.Objective             = task.Objective;
                stored.Metric                = task.Metric;
                stored.Op                    = task.Op;
                stored.Target                = task.Target;
                stored.Tolerance             = task.Tolerance;
                stored.SimulationTimeMinutes = task.SimulationTimeMinutes;
                if (!string.IsNullOrWhiteSpace(task.CreatedBy)) stored.CreatedBy = task.CreatedBy;
                stored.UpdatedUtc = DateTime.UtcNow;
            }
            SaveLocked();
        }
        Changed?.Invoke();
        return task;
    }

    public bool Delete(string taskId)
    {
        bool removed;
        lock (_lock)
        {
            var t = FindLocked(taskId);
            if (t is null) return false;
            _file.Tasks.Remove(t);
            _file.Completions.RemoveAll(c => c.TaskId == taskId);
            removed = true;
            SaveLocked();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    // ── Completion (per student) ────────────────────────────────────────────────

    public void SetCompleted(string username, string taskId, bool completed)
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(taskId)) return;

        lock (_lock)
        {
            if (FindLocked(taskId) is null) return; // ignore unknown task ids

            _file.Completions.RemoveAll(c =>
                string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase) && c.TaskId == taskId);

            if (completed)
                _file.Completions.Add(new TaskCompletion
                {
                    Username     = username,
                    TaskId       = taskId,
                    CompletedUtc = DateTime.UtcNow,
                });

            SaveLocked();
        }
        Changed?.Invoke();
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private LearningTask? FindLocked(string id) =>
        _file.Tasks.FirstOrDefault(t => t.Id == id);

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _file = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.TasksFile) ?? new();
            }
            catch { _file = new(); }

            // Seed a couple of example tasks so the page is not empty on first run.
            if (_file.Tasks.Count == 0)
            {
                _file.Tasks.Add(new LearningTask
                {
                    Id        = Guid.NewGuid().ToString("N"),
                    Title     = "Tune PID for a fast rise time",
                    Objective = "Adjust Kp, Ki and Kd on the Dashboard so the step response reaches the setpoint quickly.",
                    Metric    = TaskMetrics.RiseTime,
                    Op        = TaskOps.Lte,
                    Target    = 2.0,
                    Tolerance = 0.2,
                    CreatedBy = "admin",
                });
                _file.Tasks.Add(new LearningTask
                {
                    Id        = Guid.NewGuid().ToString("N"),
                    Title     = "Keep overshoot under control",
                    Objective = "Find PID gains that keep the overshoot small while still responding reasonably fast.",
                    Metric    = TaskMetrics.Overshoot,
                    Op        = TaskOps.Lte,
                    Target    = 10.0,
                    Tolerance = 2.0,
                    CreatedBy = "admin",
                });
                SaveLocked();
            }
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_file, AppJsonContext.Default.TasksFile));
        }
        catch { }
    }

    private static LearningTask Clone(LearningTask t) => new()
    {
        Id                    = t.Id,
        Title                 = t.Title,
        Objective             = t.Objective,
        Metric                = t.Metric,
        Op                    = t.Op,
        Target                = t.Target,
        Tolerance             = t.Tolerance,
        CreatedBy             = t.CreatedBy,
        CreatedUtc            = t.CreatedUtc,
        UpdatedUtc            = t.UpdatedUtc,
        SimulationTimeMinutes = t.SimulationTimeMinutes,
    };
}
