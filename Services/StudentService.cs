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

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    private List<StudentInfo>? _students;

    // ── Default seed data ────────────────────────────────────────────────────
    private static readonly List<StudentInfo> _defaults = new()
    {
        new() { Id = "STU001", Name = "Rizky Pratama",   Nrp = "05211940000001", Kelas = "TK-3A", GroupId = "GRP-01" },
        new() { Id = "STU002", Name = "Siti Nurhaliza",  Nrp = "05211940000002", Kelas = "TK-3A", GroupId = "GRP-01" },
        new() { Id = "STU003", Name = "Ahmad Fauzi",     Nrp = "05211940000003", Kelas = "TK-3B", GroupId = "GRP-01" },
        new() { Id = "STU004", Name = "Dewi Anggraini",  Nrp = "05211940000004", Kelas = "TK-3B", GroupId = "GRP-01" },
        new() { Id = "STU005", Name = "Budi Santoso",    Nrp = "05211940000005", Kelas = "TK-3A", GroupId = "GRP-01" },
    };

    // ── Load / Save ──────────────────────────────────────────────────────────

    public async Task EnsureLoadedAsync()
    {
        if (_students != null) return;
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path);
                _students = JsonSerializer.Deserialize<List<StudentInfo>>(json) ?? new();
            }
        }
        catch { /* ignore corrupt file */ }

        if (_students == null || _students.Count == 0)
        {
            _students = new(_defaults.Select(s => new StudentInfo
                { Id = s.Id, Name = s.Name, Nrp = s.Nrp, Kelas = s.Kelas, GroupId = s.GroupId }));
            await SaveAsync();
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var json = JsonSerializer.Serialize(_students, _json);
            await File.WriteAllTextAsync(_path, json);
        }
        catch { /* swallow — save is best-effort */ }
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    public IReadOnlyList<StudentInfo> GetAll() =>
        (_students ?? _defaults).AsReadOnly();

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
        var s = (_students ?? _defaults).FirstOrDefault(x => x.Id == id);
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
