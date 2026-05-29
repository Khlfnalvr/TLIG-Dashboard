using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TLIGDashboard.Models;
using MiniExcelLibs;

namespace TLIGDashboard.Services;

public class LoggingService
{
    // ── Private state ────────────────────────────────────────────────────
    private StreamWriter?   _writer;          // CSV / TSV — streaming
    private List<LogEntry>? _buffer;          // Excel / JSON — buffered until Stop()
    private DateTime        _startTime;
    private DateTime        _endTime;
    private LogFormat       _format;
    private LogColumn[]     _activeColumns = []; // snapshot at Start()

    // Lightweight record that pairs a timestamp with each incoming frame
    private readonly record struct LogEntry(DateTime Timestamp, BmsData Data);

    // ── Public state ─────────────────────────────────────────────────────
    public bool     IsLogging   { get; private set; }
    public string?  FilePath    { get; private set; }
    public int      SampleCount { get; private set; }

    /// <summary>
    /// Live duration while recording; total session duration after Stop();
    /// zero when no session has been recorded yet.
    /// </summary>
    public TimeSpan Duration =>
        IsLogging       ? DateTime.Now  - _startTime :
        SampleCount > 0 ? _endTime      - _startTime :
                          TimeSpan.Zero;

    public event Action? StateChanged;

    // ── Statics ───────────────────────────────────────────────────────────
    public static string DefaultLogsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BMSLogs");

    public static string ExtensionFor(LogFormat fmt) => fmt switch
    {
        LogFormat.Tsv   => ".tsv",
        LogFormat.Excel => ".xlsx",
        LogFormat.Json  => ".json",
        _               => ".csv"
    };

    public static string GenerateFileName(LogFormat fmt = LogFormat.Csv) =>
        $"BMS_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}{ExtensionFor(fmt)}";

    // ── Session control ───────────────────────────────────────────────────

    /// <param name="columns">
    /// Ordered list of columns to log. Only enabled columns are written.
    /// Pass LogColumn.CreateDefaults() for the standard 55-column layout.
    /// </param>
    public void Start(string filePath, LogFormat format, IEnumerable<LogColumn> columns)
    {
        if (IsLogging) return;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Snapshot enabled columns in the caller's order
        _activeColumns = columns.Where(c => c.IsEnabled).ToArray();
        _format        = format;
        FilePath       = filePath;
        _startTime     = DateTime.Now;
        _endTime       = default;
        SampleCount    = 0;

        if (format is LogFormat.Csv or LogFormat.Tsv)
        {
            _writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            char sep = format == LogFormat.Tsv ? '\t' : ',';
            _lineBuilder.Clear();
            for (int i = 0; i < _activeColumns.Length; i++)
            {
                if (i > 0) _lineBuilder.Append(sep);
                _lineBuilder.Append(_activeColumns[i].Key);
            }
            _writer.WriteLine(_lineBuilder);
        }
        else
        {
            _buffer = new List<LogEntry>(4096);
        }

        IsLogging = true;
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Stops the session. For Excel / JSON this finalises and writes the file.
    /// </summary>
    public void Stop()
    {
        if (!IsLogging) return;
        _endTime  = DateTime.Now;
        IsLogging = false;

        if (_format is LogFormat.Csv or LogFormat.Tsv)
        {
            _writer?.Flush();
            _writer?.Dispose();
            _writer = null;
        }
        else if (_format == LogFormat.Excel)
        {
            WriteExcel();
            _buffer = null;
        }
        else   // JSON
        {
            WriteJson();
            _buffer = null;
        }

        StateChanged?.Invoke();
    }

    // Reused per-line StringBuilder — avoids the per-frame LINQ Select +
    // string.Join allocation that ran for every CSV/TSV row logged.
    private readonly StringBuilder _lineBuilder = new(512);

    public void Log(BmsData data)
    {
        if (!IsLogging) return;

        if (_format is LogFormat.Csv or LogFormat.Tsv)
        {
            char sep = _format == LogFormat.Tsv ? '\t' : ',';
            var  ts  = DateTime.Now;
            _lineBuilder.Clear();
            var cols = _activeColumns;
            for (int i = 0; i < cols.Length; i++)
            {
                if (i > 0) _lineBuilder.Append(sep);
                _lineBuilder.Append(cols[i].GetString(ts, data));
            }
            _writer!.WriteLine(_lineBuilder);
        }
        else
        {
            _buffer!.Add(new LogEntry(DateTime.Now, data));
        }

        SampleCount++;
        StateChanged?.Invoke();
    }

    // ── Internal: Excel ───────────────────────────────────────────────────
    private void WriteExcel()
    {
        var rows = new List<Dictionary<string, object>>(_buffer!.Count);

        foreach (var (ts, d) in _buffer!)
        {
            var row = new Dictionary<string, object>();
            foreach (var col in _activeColumns)
                row[col.Key] = col.GetObject(ts, d);
            rows.Add(row);
        }

        MiniExcel.SaveAs(FilePath!, rows, overwriteFile: true);
    }

    // ── Internal: JSON ────────────────────────────────────────────────────
    private void WriteJson()
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };

        var rows = _buffer!.Select(e =>
        {
            var dict = new Dictionary<string, object>();
            foreach (var col in _activeColumns)
                dict[col.Key] = col.GetObject(e.Timestamp, e.Data);
            return dict;
        }).ToList();

        File.WriteAllText(FilePath!, JsonSerializer.Serialize(rows, opts), Encoding.UTF8);
    }
}
