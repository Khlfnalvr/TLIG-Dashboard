using System.Data.Common;
using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace TLIGDashboard.Services;

/// <summary>
/// Fondasi bersama untuk database SQLite milik fitur HE (antrian giliran dan
/// cache hasil parameter). Menangani hal-hal yang sama untuk keduanya:
///
///   • lokasi file DB di <c>%LOCALAPPDATA%\TLIGDashboard\</c> (dibuat otomatis);
///   • pemasangan skema dari file <c>Database\*.sql</c> yang ditanam sebagai
///     <i>embedded resource</i>, jadi tidak ada file lepas yang bisa hilang saat
///     publish/trim — skema ikut di dalam .exe;
///   • PRAGMA per koneksi: WAL (baca tetap jalan saat ada yang menulis),
///     <c>busy_timeout</c> (menunggu, bukan langsung "database is locked"), dan
///     <c>foreign_keys</c> yang di SQLite memang mati secara default;
///   • satu gerbang tulis (<see cref="WriteAsync{T}"/>) supaya beberapa
///     permintaan klien yang datang bersamaan tidak saling menimpa.
///
/// Semua waktu disimpan sebagai teks UTC ISO-8601 dengan panjang tetap
/// (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>). Ini disengaja: urutan antrian dan
/// pencarian run terbaru mengandalkan perbandingan teks, sehingga format harus
/// seragam — jangan pakai <c>datetime('now')</c> milik SQLite (formatnya
/// memakai spasi, bukan 'T', jadi urutannya bisa tercampur).
/// </summary>
public abstract class HeSqliteDatabase
{
    /// <summary>Format teks waktu di seluruh tabel HE — UTC, panjang tetap, urut secara leksikografis.</summary>
    private const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private readonly string        _connectionString;
    private readonly string        _schemaResourceSuffix;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private          bool          _initialized;

    /// <param name="fileName">Nama file DB di folder data aplikasi, mis. "heQueue.db".</param>
    /// <param name="schemaResourceSuffix">Akhiran nama embedded resource skema, mis. "Database.HeQueueSchema.sql".</param>
    /// <param name="dbPath">Override lokasi file DB — dipakai oleh tes/alat bantu.</param>
    protected HeSqliteDatabase(string fileName, string schemaResourceSuffix, string? dbPath = null)
    {
        DatabasePath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLIGDashboard", fileName);

        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        _schemaResourceSuffix = schemaResourceSuffix;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode       = SqliteOpenMode.ReadWriteCreate,
            Pooling    = true,
        }.ToString();
    }

    /// <summary>Lokasi file .db di disk — ditampilkan di UI/log agar mudah dicari saat backup.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Memasang skema kalau belum ada (semua DDL memakai <c>IF NOT EXISTS</c>, jadi
    /// aman dipanggil setiap kali aplikasi start). Panggil sekali di startup Server.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            var schema = ReadSchemaSql(_schemaResourceSuffix);
            await using var conn = await OpenAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = schema;
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Koneksi baru yang sudah terbuka dan PRAGMA-nya terpasang.</summary>
    protected async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        try
        {
            await using var pragma = conn.CreateCommand();
            // journal_mode WAL bersifat permanen di file DB; dua PRAGMA lainnya
            // berlaku per koneksi sehingga harus dipasang di setiap Open.
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
            await pragma.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Menjalankan satu operasi tulis di dalam transaksi, satu per satu.
    /// Keputusan antrian ("siapa yang dapat giliran") harus baca-lalu-tulis
    /// secara utuh, jadi seluruh badan operasi dikunci — bukan hanya perintah
    /// SQL-nya.
    /// </summary>
    protected async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task<T>> body,
        CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var tx   = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            var result = await body(conn, tx, ct);

            await tx.CommitAsync(ct);
            return result;
        }
        finally { _writeGate.Release(); }
    }

    /// <summary>Menjalankan satu operasi baca (tanpa transaksi tulis, tidak ikut antre gerbang tulis).</summary>
    protected async Task<T> ReadAsync<T>(
        Func<SqliteConnection, CancellationToken, Task<T>> body,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        return await body(conn, ct);
    }

    // ── Helper query ────────────────────────────────────────────────────────

    /// <summary>Membuat command yang sudah terikat pada transaksi berjalan (kalau ada).</summary>
    protected static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>Menambah parameter; <c>null</c> dikirim sebagai NULL SQL, bukan string kosong.</summary>
    protected static void Bind(SqliteCommand cmd, string name, object? value)
        => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

    // ── Konversi waktu ──────────────────────────────────────────────────────

    /// <summary>DateTime → teks UTC yang tersimpan di kolom *_utc.</summary>
    public static string ToDbTime(DateTime value) =>
        (value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime())
            .ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>
    /// Teks kolom *_utc → DateTime UTC. Menerima juga format lama
    /// <c>datetime('now')</c> ("YYYY-MM-DD HH:MM:SS") supaya file DB yang dibuat
    /// versi sebelumnya tetap terbaca.
    /// </summary>
    public static DateTime FromDbTime(string text) =>
        DateTime.TryParseExact(text, TimeFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var exact)
            ? exact
            : DateTime.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var loose)
                ? loose
                : DateTime.UnixEpoch;

    protected static DateTime? ReadTime(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : FromDbTime(reader.GetString(ordinal));

    protected static double? ReadDouble(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    protected static string? ReadString(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    // ── Skema ───────────────────────────────────────────────────────────────

    private static string ReadSchemaSql(string resourceSuffix)
    {
        var assembly = typeof(HeSqliteDatabase).Assembly;
        var name = Array.Find(assembly.GetManifestResourceNames(),
            n => n.EndsWith(resourceSuffix, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"Skema '{resourceSuffix}' tidak ditemukan sebagai embedded resource. " +
                "Pastikan file .sql terdaftar sebagai <EmbeddedResource> di TLIGDashboard.csproj.");

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
