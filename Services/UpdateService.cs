using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TLIGDashboard.Services;

public record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")]     string? Body,
    [property: JsonPropertyName("assets")]   GitHubAsset[] Assets
);

public record GitHubAsset(
    [property: JsonPropertyName("name")]                 string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")]                 long   Size
);

public enum UpdateCheckResult { UpToDate, UpdateAvailable, Error }

public sealed class UpdateCheckInfo
{
    public UpdateCheckResult Result        { get; init; }
    public string?           LatestVersion { get; init; }
    public string?           ReleaseUrl    { get; init; }
    public string?           UpdateZipUrl  { get; init; }
    public string?           ReleaseNotes  { get; init; }
    public string?           ErrorMessage  { get; init; }
}

public sealed class PreparedUpdate
{
    public required string PayloadPath { get; init; }
    public required string CleanupPath { get; init; }
}

public static class UpdateService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Default: only stable (non-prerelease) releases.
    private const string ApiUrl =
        "https://api.github.com/repos/Khlfnalvr/TLIG-Dashboard/releases/latest";

    // Early Access: most recent release of any kind (prerelease included).
    // Returns a JSON array; we take the first element.
    private const string ApiUrlEarlyAccess =
        "https://api.github.com/repos/Khlfnalvr/TLIG-Dashboard/releases?per_page=1";

    private static string ExeName =>
        BuildInfo.IsServer ? "TLIGDashboard.Server.exe" : "TLIGDashboard.Client.exe";

    public static async Task<UpdateCheckInfo> CheckAsync(
        string currentVersion, bool earlyAccess = false)
    {
        try
        {
            // Early Access mode uses the /releases list endpoint (includes prereleases).
            // Default mode uses /releases/latest (stable only).
            var url = earlyAccess ? ApiUrlEarlyAccess : ApiUrl;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("TLIGDashboard/" + currentVersion);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();

            GitHubRelease? release;
            if (earlyAccess)
            {
                // /releases returns an array; take the first (most recent) element.
                var releases = JsonSerializer.Deserialize(
                    json, AppJsonContext.Default.GitHubReleaseArray);
                release = releases is { Length: > 0 } ? releases[0] : null;
            }
            else
            {
                release = JsonSerializer.Deserialize(
                    json, AppJsonContext.Default.GitHubRelease);
            }

            if (release is null)
                return Err("Invalid API response");

            var latestTag = release.TagName.TrimStart('v');
            var current   = currentVersion.TrimStart('v');

            // Pick the ZIP that matches this flavor (Server or Client) and contains
            // "Update" in its name. Fall back to any flavor-matched ZIP, then any ZIP.
            var flavor   = BuildInfo.Flavor; // "Server" or "Client"
            var zipAsset =
                release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains("Update", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains(flavor,   StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.Contains(flavor, StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a =>
                    a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            return new UpdateCheckInfo
            {
                Result        = IsNewer(latestTag, current)
                                    ? UpdateCheckResult.UpdateAvailable
                                    : UpdateCheckResult.UpToDate,
                LatestVersion = latestTag,
                ReleaseUrl    = release.HtmlUrl,
                UpdateZipUrl  = zipAsset?.BrowserDownloadUrl,
                ReleaseNotes  = release.Body
            };
        }
        catch (Exception ex)
        {
            return Err(ex.Message);
        }
    }

    // Downloads the update ZIP and extracts it to an isolated staging folder.
    // Returns the app payload path plus the temporary folder to remove later.
    public static async Task<PreparedUpdate> DownloadAndExtractAsync(
        string url, string currentVersion, Action<double>? progress = null)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), "TLIGDashboardUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var zipPath = Path.Combine(tempDir, "update.zip");

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("TLIGDashboard/" + currentVersion);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? -1L;
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using (var dst = File.Create(zipPath))
        {
            var buf  = new byte[81920];
            long done = 0;
            int  read;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Invoke((double)done / total * 0.85);
            }
        }

        var stagePath = Path.Combine(tempDir, "stage");
        Directory.CreateDirectory(stagePath);
        ZipFile.ExtractToDirectory(zipPath, stagePath);
        File.Delete(zipPath);

        var payloadPath = FindPayloadPath(stagePath);

        progress?.Invoke(1.0);
        return new PreparedUpdate
        {
            PayloadPath = payloadPath,
            CleanupPath = tempDir
        };
    }

    // Writes a PowerShell script that, once launched, waits for TLIGDashboard
    // to exit, copies all staged files over the installed app, then restarts it.
    // Returns the path to the written script.
    public static string WriteApplyScript(string payloadPath, string cleanupPath, string appDir)
    {
        Directory.CreateDirectory(cleanupPath);
        var scriptPath = Path.Combine(cleanupPath, "apply.ps1");

        var sb = new StringBuilder();
        sb.AppendLine("param(");
        sb.AppendLine("    [Parameter(Mandatory=$true)][string]$Payload,");
        sb.AppendLine("    [Parameter(Mandatory=$true)][string]$AppDir,");
        sb.AppendLine("    [Parameter(Mandatory=$true)][string]$Cleanup,");
        sb.AppendLine("    [int]$ParentProcessId = 0");
        sb.AppendLine(")");
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine($"$exe = Join-Path $AppDir '{ExeName}'");
        sb.AppendLine("$logDir = Join-Path $env:TEMP 'TLIGDashboardUpdate'");
        sb.AppendLine("$logPath = Join-Path $logDir 'last-update-error.log'");
        sb.AppendLine("try {");
        sb.AppendLine("    if ($ParentProcessId -gt 0) {");
        sb.AppendLine("        Wait-Process -Id $ParentProcessId -ErrorAction SilentlyContinue");
        sb.AppendLine("    } else {");
        sb.AppendLine($"        while (Get-Process -Name '{Path.GetFileNameWithoutExtension(ExeName)}' -ErrorAction SilentlyContinue) {{");
        sb.AppendLine("            Start-Sleep -Milliseconds 500");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("    Start-Sleep -Milliseconds 300");
        sb.AppendLine($"    if (-not (Test-Path (Join-Path $Payload '{ExeName}'))) {{");
        sb.AppendLine($"        throw \"Update payload does not contain {ExeName}: $Payload\"");
        sb.AppendLine("    }");
        sb.AppendLine("    $robocopyArgs = @(");
        sb.AppendLine("        $Payload, $AppDir,");
        sb.AppendLine("        '/E', '/IS', '/IT', '/R:5', '/W:1',");
        sb.AppendLine("        '/NJH', '/NJS', '/NFL', '/NDL', '/NP', '/COPY:DAT'");
        sb.AppendLine("    )");
        sb.AppendLine("    & robocopy @robocopyArgs | Out-Null");
        sb.AppendLine("    if ($LASTEXITCODE -ge 8) {");
        sb.AppendLine("        throw \"robocopy failed with exit code $LASTEXITCODE\"");
        sb.AppendLine("    }");
        sb.AppendLine("    Start-Process -FilePath $exe -WorkingDirectory $AppDir");
        sb.AppendLine("}");
        sb.AppendLine("catch {");
        sb.AppendLine("    New-Item -ItemType Directory -Path $logDir -Force | Out-Null");
        sb.AppendLine("    $_ | Out-File -FilePath $logPath -Encoding UTF8");
        sb.AppendLine("    if (Test-Path $exe) {");
        sb.AppendLine("        Start-Process -FilePath $exe -WorkingDirectory $AppDir");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine("finally {");
        sb.AppendLine("    Remove-Item $Cleanup -Recurse -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("    Remove-Item $PSCommandPath -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("}");

        File.WriteAllText(scriptPath, sb.ToString(), Encoding.UTF8);
        return scriptPath;
    }

    private static UpdateCheckInfo Err(string msg) =>
        new() { Result = UpdateCheckResult.Error, ErrorMessage = msg };

    private static bool IsNewer(string latest, string current)
    {
        if (Version.TryParse(latest, out var l) && Version.TryParse(current, out var c))
            return l > c;
        return string.Compare(latest, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string FindPayloadPath(string stagePath)
    {
        var exe = ExeName;
        if (File.Exists(Path.Combine(stagePath, exe)))
            return stagePath;

        var payloadPath = Directory
            .EnumerateFiles(stagePath, exe, SearchOption.AllDirectories)
            .Select(Path.GetDirectoryName)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .OrderBy(path => path!.Length)
            .FirstOrDefault();

        if (payloadPath is null)
            throw new InvalidDataException($"The update ZIP does not contain {exe}.");

        return payloadPath;
    }
}
