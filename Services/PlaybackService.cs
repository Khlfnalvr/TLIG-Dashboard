using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using TLIGDashboard.Models;
using MiniExcelLibs;

namespace TLIGDashboard.Services;

/// <summary>
/// Loads a TLIG Dashboard log file (CSV, TSV, Excel, JSON) and replays its
/// frames through the same ApplyData() pipeline used for live serial data.
/// </summary>
public sealed class PlaybackService
{
    // ── State ─────────────────────────────────────────────────────────────
    private BmsData[] _frames     = [];
    private string[]  _timestamps = [];
    private Timer?    _timer;
    private int       _currentFrame;

    public bool   IsLoaded    { get; private set; }
    public bool   IsPlaying   { get; private set; }
    public int    TotalFrames => _frames.Length;
    public int    CurrentFrame => _currentFrame;
    public string FileName    { get; private set; } = "";

    /// <summary>HH:mm:ss of the current frame.</summary>
    public string CurrentTimestamp =>
        IsLoaded && _currentFrame < _timestamps.Length
            ? _timestamps[_currentFrame] : "";

    /// <summary>Playback frames per second (1 = realtime, 2 = 2×, …).</summary>
    public double PlaybackSpeed { get; set; } = 1.0;

    // ── Events ────────────────────────────────────────────────────────────
    public event Action<BmsData>?   FrameChanged;
    public event Action?            StateChanged;
    /// <summary>Fired once after a file is loaded, with every frame in order.</summary>
    public event Action<BmsData[]>? FileLoaded;
    /// <summary>Fired when a loaded file is dismissed (back to live mode).</summary>
    public event Action?            FileUnloaded;

    // ── Supported extensions ──────────────────────────────────────────────
    public static readonly string[] SupportedExtensions = [".csv", ".tsv", ".xlsx", ".json"];

    // ── Load / Unload ─────────────────────────────────────────────────────
    /// <returns>null on success; error message on failure.</returns>
    public string? LoadFile(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".tsv"  => LoadDelimited(path, '\t'),
                ".xlsx" => LoadExcel(path),
                ".json" => LoadJson(path),
                _       => LoadDelimited(path, ',')   // .csv and anything else
            };
        }
        catch (Exception ex) { return ex.Message; }
    }

    public void Unload()
    {
        StopTimer();
        _frames       = [];
        _timestamps   = [];
        _currentFrame = 0;
        FileName      = "";
        IsLoaded      = false;
        IsPlaying     = false;
        StateChanged?.Invoke();
        FileUnloaded?.Invoke();
    }

    // ── Transport ─────────────────────────────────────────────────────────
    public void Play()
    {
        if (!IsLoaded || IsPlaying) return;
        if (_currentFrame >= TotalFrames - 1) _currentFrame = 0;

        IsPlaying = true;
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(0.1, PlaybackSpeed));
        _timer = new Timer(OnTick, null, interval, interval);
        StateChanged?.Invoke();
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        StopTimer();
        IsPlaying = false;
        StateChanged?.Invoke();
    }

    public void SeekTo(int frameIndex)
    {
        if (!IsLoaded) return;
        _currentFrame = Math.Clamp(frameIndex, 0, TotalFrames - 1);
        FrameChanged?.Invoke(_frames[_currentFrame]);
        StateChanged?.Invoke();
    }

    // ── Loaders ───────────────────────────────────────────────────────────

    private string? LoadDelimited(string path, char delimiter)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 2) return LocalizationManager.Instance.Get("Pb_FileNoDataRows");

        var frames     = new List<BmsData>();
        var timestamps = new List<string>();

        for (int i = 1; i < lines.Length; i++)
        {
            var row = lines[i].Split(delimiter);
            if (row.Length < 55) continue;

            var ts = row[0];
            timestamps.Add(ts.Length >= 19 ? ts.Substring(11, 8) : ts);

            if (!P(row[1], out double v) || !P(row[2], out double s) || !P(row[3], out double c))
                continue;

            var data = new BmsData { PackVoltage = v, Soc = s, Current = c, Status = row[4] };

            bool ok = true;
            for (int j = 0; j < 20; j++)
                if (!P(row[5 + j], out data.Cells[j])) { ok = false; break; }
            if (!ok) continue;

            for (int j = 0; j < 20; j++) data.Balancing[j] = row[25 + j] == "1";
            for (int j = 0; j < 10; j++) P(row[45 + j], out data.Temps[j]);

            frames.Add(data);
        }

        return Commit(frames, timestamps, Path.GetFileName(path));
    }

    private string? LoadExcel(string path)
    {
        var excelRows = MiniExcel.Query(path, useHeaderRow: true)
                                 .Cast<IDictionary<string, object>>()
                                 .ToList();
        if (excelRows.Count == 0) return LocalizationManager.Instance.Get("Pb_ExcelNoDataRows");

        var frames     = new List<BmsData>(excelRows.Count);
        var timestamps = new List<string>(excelRows.Count);

        foreach (var row in excelRows)
        {
            var ts = row.TryGetValue("Timestamp", out var tsObj) ? tsObj?.ToString() ?? "" : "";
            timestamps.Add(ts.Length >= 19 ? ts.Substring(11, 8) : ts);

            if (!Pobj(row, "PackVoltage_V", out double v) ||
                !Pobj(row, "SOC_pct",       out double s) ||
                !Pobj(row, "Current_A",     out double c))
                continue;

            var data = new BmsData
            {
                PackVoltage = v, Soc = s, Current = c,
                Status = row.TryGetValue("Status", out var st) ? st?.ToString() ?? "idle" : "idle"
            };

            bool ok = true;
            for (int j = 0; j < 20; j++)
                if (!Pobj(row, $"Cell{j + 1}_V", out data.Cells[j])) { ok = false; break; }
            if (!ok) continue;

            for (int j = 0; j < 20; j++)
            {
                if (row.TryGetValue($"Bal{j + 1}", out var bal))
                    data.Balancing[j] = bal?.ToString()?.Trim() == "1";
            }
            for (int j = 0; j < 10; j++)
                Pobj(row, $"Temp{j + 1}_C", out data.Temps[j]);

            frames.Add(data);
        }

        return Commit(frames, timestamps, Path.GetFileName(path));
    }

    private string? LoadJson(string path)
    {
        var json = File.ReadAllText(path, Encoding.UTF8);
        var rows = JsonSerializer.Deserialize<JsonRow[]>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (rows == null || rows.Length == 0) return LocalizationManager.Instance.Get("Pb_JsonNoRows");

        var frames     = new List<BmsData>(rows.Length);
        var timestamps = new List<string>(rows.Length);

        foreach (var row in rows)
        {
            var ts = row.Timestamp ?? "";
            timestamps.Add(ts.Length >= 19 ? ts.Substring(11, 8) : ts);

            var data = new BmsData
            {
                PackVoltage = row.PackVoltage,
                Soc         = row.Soc,
                Current     = row.Current,
                Status      = row.Status ?? "idle"
            };

            var cells = row.Cells;
            if (cells?.Length == 20)
                for (int i = 0; i < 20; i++) data.Cells[i] = cells[i];

            var bal = row.Balancing;
            if (bal?.Length == 20)
                for (int i = 0; i < 20; i++) data.Balancing[i] = bal[i];

            var temps = row.Temps;
            if (temps?.Length == 10)
                for (int i = 0; i < 10; i++) data.Temps[i] = temps[i];

            frames.Add(data);
        }

        return Commit(frames, timestamps, Path.GetFileName(path));
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>Commits loaded data atomically; returns null on success.</summary>
    private string? Commit(List<BmsData> frames, List<string> timestamps, string fileName)
    {
        if (frames.Count == 0) return LocalizationManager.Instance.Get("Pb_NoValidDataRows");

        StopTimer();
        _frames       = [.. frames];
        _timestamps   = [.. timestamps];
        _currentFrame = 0;
        FileName      = fileName;
        IsLoaded      = true;
        IsPlaying     = false;

        StateChanged?.Invoke();
        // Bulk-load event first so subscribers can populate their history,
        // then FrameChanged for the first-frame UI update.
        FileLoaded?.Invoke(_frames);
        FrameChanged?.Invoke(_frames[0]);
        return null;
    }

    private void OnTick(object? _)
    {
        if (!IsLoaded || !IsPlaying) return;
        if (_currentFrame >= TotalFrames - 1)
        {
            StopTimer();
            IsPlaying = false;
            StateChanged?.Invoke();
            return;
        }
        Interlocked.Increment(ref _currentFrame);
        FrameChanged?.Invoke(_frames[_currentFrame]);
        StateChanged?.Invoke();
    }

    private void StopTimer()
    {
        var t = Interlocked.Exchange(ref _timer, null);
        t?.Dispose();
    }

    /// <summary>Parse a string to double with invariant culture.</summary>
    private static bool P(string s, out double v) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out v);

    /// <summary>Parse a MiniExcel dictionary value to double.</summary>
    private static bool Pobj(IDictionary<string, object> row, string key, out double v)
    {
        if (row.TryGetValue(key, out var obj) && obj is not null)
        {
            if (obj is double d) { v = d; return true; }   // numeric cell — already a double
            return P(obj.ToString()!, out v);               // string cell — parse
        }
        v = 0;
        return false;
    }

    // ── JSON deserialization model ─────────────────────────────────────────
    private sealed class JsonRow
    {
        [JsonPropertyName("timestamp")]   public string?   Timestamp   { get; init; }
        [JsonPropertyName("packVoltage")] public double    PackVoltage { get; init; }
        [JsonPropertyName("soc")]         public double    Soc         { get; init; }
        [JsonPropertyName("current")]     public double    Current     { get; init; }
        [JsonPropertyName("status")]      public string?   Status      { get; init; }
        [JsonPropertyName("cells")]       public double[]? Cells       { get; init; }
        [JsonPropertyName("balancing")]   public bool[]?   Balancing   { get; init; }
        [JsonPropertyName("temps")]       public double[]? Temps       { get; init; }
    }
}
