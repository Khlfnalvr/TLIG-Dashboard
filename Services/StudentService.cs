using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TLIGDashboard.Services;

public sealed class StudentInfo
{
    public string Id      { get; set; } = "";
    public string Name    { get; set; } = "";
    /// <summary>Nomor Registrasi Pokok, e.g. "05211940000001".</summary>
    public string Nrp     { get; set; } = "";
    /// <summary>Class / cohort, e.g. "TK-3A".</summary>
    public string Kelas   { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GroupId { get; set; }
}

/// <summary>
/// Persistent list of students, stored at %LOCALAPPDATA%\TLIGDashboard\students.json.
/// Call EnsureLoadedAsync() once before reading; changes are auto-saved.
/// </summary>
public sealed class StudentService
{
    public static readonly StudentService Instance = new();
    private StudentService() { }

    private static readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard", "students.json");

    private List<StudentInfo>? _students;

    // ── Load / Save ──────────────────────────────────────────────────────────

    public async Task EnsureLoadedAsync()
    {
        if (_students != null) return;
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path);
                _students = JsonSerializer.Deserialize(json, AppJsonContext.Default.ListStudentInfo) ?? new();
            }
        }
        catch { /* ignore corrupt file */ }

        _students ??= new();
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_students ?? new(), AppJsonContext.Default.ListStudentInfo);
            await File.WriteAllTextAsync(_path, json);
        }
        catch { /* swallow — save is best-effort */ }
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public IReadOnlyList<StudentInfo> GetAll() =>
        (_students ?? new List<StudentInfo>()).AsReadOnly();

    public IReadOnlyList<StudentInfo> GetByGroup(string groupId) =>
        GetAll().Where(s => s.GroupId == groupId).ToList();

    public IReadOnlyList<StudentInfo> GetByKelas(string kelas) =>
        GetAll().Where(s => string.Equals(s.Kelas, kelas, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Returns all distinct kelas values, sorted alphabetically.</summary>
    public IReadOnlyList<string> GetDistinctKelas() =>
        GetAll()
            .Select(s => s.Kelas)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k)
            .ToList();

    public string GetName(string id)
    {
        var s = _students?.FirstOrDefault(x => x.Id == id);
        return s?.Name ?? id;
    }

    // ── Mutations ────────────────────────────────────────────────────────────

    public async Task AddOrUpdateAsync(StudentInfo student)
    {
        await EnsureLoadedAsync();
        var existing = _students!.FirstOrDefault(s => s.Id == student.Id);
        if (existing != null)
        {
            existing.Name    = student.Name;
            existing.GroupId = student.GroupId;
        }
        else
        {
            _students!.Add(new StudentInfo
                { Id = student.Id, Name = student.Name, GroupId = student.GroupId });
        }
        await SaveAsync();
    }

    public async Task RemoveAsync(string id)
    {
        await EnsureLoadedAsync();
        _students!.RemoveAll(s => s.Id == id);
        await SaveAsync();
    }

    /// <summary>Replace the entire list, e.g. after bulk editing.</summary>
    public async Task ReplaceAllAsync(IEnumerable<StudentInfo> students)
    {
        await EnsureLoadedAsync();
        _students = new(students);
        await SaveAsync();
    }
}
