using System.Net.Http;
using System.Text.Json.Nodes;

namespace TLIGDashboard.Services;

/// <summary>
/// Single source of truth for the student roster, derived from the real user
/// database (accounts with role Mahasiswa) instead of dummy seed data.
///   • <b>Server</b> flavor: reads <see cref="UserStore"/> directly.
///   • <b>Client</b> flavor: fetches <c>GET /students</c> from the signed-in
///     server (staff-only endpoint), mirroring <see cref="TaskClient"/>.
/// The last successful result is cached so name lookups stay synchronous.
/// </summary>
public static class StudentDirectory
{
    private static List<StudentInfo> _cache = new();

    /// <summary>Last roster fetched by <see cref="GetStudentsAsync"/> (may be empty).</summary>
    public static IReadOnlyList<StudentInfo> Cached => _cache;

    public static async Task<IReadOnlyList<StudentInfo>> GetStudentsAsync()
    {
        if (BuildInfo.IsServer)
        {
            _cache = UserStore.Instance.GetUsers()
                .Where(u => u.Enabled && !UserRoles.IsStaff(u.Role))
                .Select(u => new StudentInfo
                {
                    Id    = u.Username,
                    Name  = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName,
                    Nrp   = u.Nrp,
                    Kelas = u.Kelas,
                })
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return _cache;
        }

        var s = AppSettingsService.Load();
        if (string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(s.ServerHost)) ||
            string.IsNullOrWhiteSpace(s.ServerToken))
            return _cache;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req  = new HttpRequestMessage(HttpMethod.Get,
                $"{AuthClient.BaseUrl(s.ServerHost)}{ShareProtocol.StudentsPath}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {s.ServerToken}");
            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var node = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
                if (node?["students"] is JsonArray arr)
                {
                    var list = new List<StudentInfo>();
                    foreach (var item in arr)
                    {
                        if (item is null) continue;
                        list.Add(new StudentInfo
                        {
                            Id    = (string?)item["id"]    ?? "",
                            Name  = (string?)item["name"]  ?? "",
                            Nrp   = (string?)item["nrp"]   ?? "",
                            Kelas = (string?)item["kelas"] ?? "",
                        });
                    }
                    _cache = list;
                }
            }
        }
        catch { /* server unreachable → keep last cache */ }
        return _cache;
    }

    /// <summary>Display name for a student id (username); falls back to the id itself.</summary>
    public static string GetName(string id)
    {
        var m = _cache.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        return m?.Name ?? id;
    }
}
