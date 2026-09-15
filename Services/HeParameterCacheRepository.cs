using System.Data.Common;
using Microsoft.Data.Sqlite;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Cache hasil percobaan parameter HE, disimpan di SQLite
/// (<c>%LOCALAPPDATA%\TLIGDashboard\heParamCache.db</c>).
///
/// Menjalankan satu kombinasi parameter ke plant fisik itu mahal: harus antre,
/// menunggu prosesnya stabil, dan hasilnya toh sama saja kalau parameternya
/// sama. Karena itu setiap run yang selesai disimpan utuh di sini — parameter
/// masukan, metrik ringkasan, dan seluruh kurva responsnya. Sebelum menjalankan
/// RUN berikutnya, tanyakan dulu ke <see cref="FindCachedRunAsync"/>:
///
/// <code>
/// var input  = new HeParameterInput { Sp = sp, Kc = kc, Ti = ti, Td = td, Pump = pump };
/// var cached = await App.HeParamCache.FindCachedRunAsync(input);
/// if (cached is not null)
/// {
///     TampilkanHasil(cached.Metrics, cached.Samples);   // tidak menyentuh plant sama sekali
///     return;
/// }
///
/// // belum pernah dicoba → jalankan seperti biasa, kumpulkan sample, lalu:
/// await App.HeParamCache.SaveRunAsync(run);
/// </code>
///
/// Cache ini milik Server (satu untuk semua pengguna), jadi percobaan yang sudah
/// dilakukan seorang mahasiswa langsung bisa dipakai mahasiswa lain.
///
/// Hanya run berstatus <see cref="HeParameterRunStatus.Completed"/> yang pernah
/// dikembalikan sebagai hasil cache — run yang gagal atau dihentikan di tengah
/// tetap disimpan sebagai catatan, tapi tidak akan disodorkan sebagai hasil.
/// </summary>
public sealed class HeParameterCacheRepository : HeSqliteDatabase
{
    /// <summary>Instance yang dipakai aplikasi Server (lihat <c>App.HeParamCache</c>).</summary>
    public static HeParameterCacheRepository Instance { get; } = new();

    public HeParameterCacheRepository(string? dbPath = null)
        : base("heParamCache.db", "Database.HeParameterCacheSchema.sql", dbPath) { }

    private const string RunColumns =
        "run_id, sp, kc, ti, td, pump, source, status, requested_by_user_id, requested_by_name, " +
        "started_at_utc, finished_at_utc, duration_seconds, reuse_count, last_reused_at_utc, note";

    // ── Ambil dari cache ────────────────────────────────────────────────────

    /// <summary>
    /// Mencari run yang sudah pernah dijalankan dengan kombinasi parameter yang
    /// sama (lihat <see cref="HeParameterInput.ParamKey"/>). Yang dikembalikan
    /// adalah run <b>terbaru</b> yang selesai wajar, lengkap dengan metrik dan
    /// kurva responsnya. <c>null</c> berarti kombinasi ini memang belum pernah
    /// dicoba — plant harus dijalankan.
    /// </summary>
    /// <param name="countReuse">
    /// Menaikkan penghitung pemakaian ulang run tersebut (dipakai untuk laporan
    /// "berapa kali plant tidak perlu dijalankan"). Set <c>false</c> kalau hanya
    /// ingin mengintip isi cache, mis. saat menampilkan daftar.
    /// </param>
    public Task<HeParameterRun?> FindCachedRunAsync(
        HeParameterInput input, bool countReuse = true, CancellationToken ct = default) =>
        WriteAsync<HeParameterRun?>(async (conn, tx, token) =>
        {
            HeParameterRun? run;
            await using (var cmd = Command(conn, tx, $"""
                SELECT {RunColumns}
                FROM he_parameter_runs
                WHERE param_key = $paramKey AND status = 'Completed'
                ORDER BY started_at_utc DESC, run_id DESC
                LIMIT 1;
                """))
            {
                Bind(cmd, "$paramKey", input.ParamKey);
                await using var reader = await cmd.ExecuteReaderAsync(token);
                run = await reader.ReadAsync(token) ? ReadRun(reader) : null;
            }

            if (run is null) return null;

            if (countReuse)
            {
                await using var bump = Command(conn, tx, """
                    UPDATE he_parameter_runs
                    SET reuse_count = reuse_count + 1, last_reused_at_utc = $now
                    WHERE run_id = $runId;
                    """);
                Bind(bump, "$now", ToDbTime(DateTime.UtcNow));
                Bind(bump, "$runId", run.RunId);
                await bump.ExecuteNonQueryAsync(token);
            }

            run.Metrics = await ReadMetricsAsync(conn, tx, run.RunId, token);
            run.Samples.AddRange(await ReadSamplesAsync(conn, tx, run.RunId, token));
            return run;
        }, ct);

    /// <summary>Satu run berdasarkan id, lengkap dengan metrik dan kurva. <c>null</c> kalau sudah dihapus.</summary>
    public Task<HeParameterRun?> GetRunAsync(long runId, CancellationToken ct = default) =>
        ReadAsync<HeParameterRun?>(async (conn, token) =>
        {
            HeParameterRun? run;
            await using (var cmd = Command(conn, null, $"SELECT {RunColumns} FROM he_parameter_runs WHERE run_id = $runId;"))
            {
                Bind(cmd, "$runId", runId);
                await using var reader = await cmd.ExecuteReaderAsync(token);
                run = await reader.ReadAsync(token) ? ReadRun(reader) : null;
            }

            if (run is null) return null;

            run.Metrics = await ReadMetricsAsync(conn, null, runId, token);
            run.Samples.AddRange(await ReadSamplesAsync(conn, null, runId, token));
            return run;
        }, ct);

    /// <summary>
    /// Daftar run terbaru <b>tanpa</b> kurva respons (kurva bisa ribuan baris) —
    /// untuk tabel riwayat. Ambil detailnya dengan <see cref="GetRunAsync"/>.
    /// </summary>
    public Task<IReadOnlyList<HeParameterRun>> ListRunsAsync(
        int limit = 100, int offset = 0, CancellationToken ct = default) =>
        ReadAsync<IReadOnlyList<HeParameterRun>>(async (conn, token) =>
        {
            var runs = new List<HeParameterRun>();
            await using var cmd = Command(conn, null, $"""
                SELECT {RunColumns}
                FROM he_parameter_runs
                ORDER BY started_at_utc DESC, run_id DESC
                LIMIT $limit OFFSET $offset;
                """);
            Bind(cmd, "$limit", limit);
            Bind(cmd, "$offset", offset);

            await using var reader = await cmd.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) runs.Add(ReadRun(reader));

            foreach (var run in runs)
                run.Metrics = await ReadMetricsAsync(conn, null, run.RunId, token);

            return runs;
        }, ct);

    /// <summary>Ringkasan isi cache untuk panel status Server.</summary>
    public Task<HeParameterCacheStats> GetStatsAsync(CancellationToken ct = default) =>
        ReadAsync<HeParameterCacheStats>(async (conn, token) =>
        {
            await using var cmd = Command(conn, null, """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN status = 'Completed' THEN 1 ELSE 0 END), 0),
                       COALESCE(SUM(reuse_count), 0),
                       MAX(started_at_utc)
                FROM he_parameter_runs;
                """);
            await using var reader = await cmd.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token)) return new HeParameterCacheStats(0, 0, 0, null);

            return new HeParameterCacheStats(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                ReadTime(reader, 3));
        }, ct);

    // ── Simpan hasil run ────────────────────────────────────────────────────

    /// <summary>
    /// Menyimpan satu run beserta metrik dan seluruh kurva responsnya, dalam satu
    /// transaksi. Mengembalikan <c>run_id</c> yang baru dibuat.
    ///
    /// Run lama dengan parameter yang sama tidak ditimpa — riwayatnya disimpan,
    /// dan pencarian cache selalu memakai yang paling baru. Jadi kalau plant
    /// dikalibrasi ulang, cukup jalankan sekali lagi untuk memperbarui hasilnya.
    /// </summary>
    public Task<long> SaveRunAsync(HeParameterRun run, CancellationToken ct = default) =>
        WriteAsync<long>(async (conn, tx, token) =>
        {
            long runId;
            await using (var cmd = Command(conn, tx, """
                INSERT INTO he_parameter_runs
                    (sp, kc, ti, td, pump, param_key, source, status,
                     requested_by_user_id, requested_by_name,
                     started_at_utc, finished_at_utc, duration_seconds, note)
                VALUES
                    ($sp, $kc, $ti, $td, $pump, $paramKey, $source, $status,
                     $userId, $userName,
                     $startedAt, $finishedAt, $duration, $note)
                RETURNING run_id;
                """))
            {
                Bind(cmd, "$sp",   run.Input.Sp);
                Bind(cmd, "$kc",   run.Input.Kc);
                Bind(cmd, "$ti",   run.Input.Ti);
                Bind(cmd, "$td",   run.Input.Td);
                Bind(cmd, "$pump", run.Input.Pump);
                Bind(cmd, "$paramKey", run.Input.ParamKey);
                Bind(cmd, "$source", run.Source.ToString());
                Bind(cmd, "$status", run.Status.ToString());
                Bind(cmd, "$userId", run.RequestedByUserId);
                Bind(cmd, "$userName", run.RequestedByName);
                Bind(cmd, "$startedAt", ToDbTime(run.StartedAtUtc));
                Bind(cmd, "$finishedAt", run.FinishedAtUtc is null ? null : ToDbTime(run.FinishedAtUtc.Value));
                Bind(cmd, "$duration", run.DurationSeconds);
                Bind(cmd, "$note", run.Note);

                runId = (long)(await cmd.ExecuteScalarAsync(token))!;
            }

            if (run.Metrics is not null)
                await WriteMetricsAsync(conn, tx, runId, run.Metrics, token);

            if (run.Samples.Count > 0)
                await WriteSamplesAsync(conn, tx, runId, run.Samples, token);

            return runId;
        }, ct);

    /// <summary>Menghapus satu run (metrik dan kurvanya ikut terhapus lewat ON DELETE CASCADE).</summary>
    public Task<bool> DeleteRunAsync(long runId, CancellationToken ct = default) =>
        WriteAsync<bool>(async (conn, tx, token) =>
        {
            await using var cmd = Command(conn, tx, "DELETE FROM he_parameter_runs WHERE run_id = $runId;");
            Bind(cmd, "$runId", runId);
            return await cmd.ExecuteNonQueryAsync(token) > 0;
        }, ct);

    /// <summary>
    /// Mengosongkan cache (dipakai kalau plant dikalibrasi ulang sehingga hasil
    /// lama tidak lagi mewakili keadaan alat). Mengembalikan jumlah run yang terhapus.
    /// </summary>
    public Task<int> ClearAsync(CancellationToken ct = default) =>
        WriteAsync<int>(async (conn, tx, token) =>
        {
            await using var cmd = Command(conn, tx, "DELETE FROM he_parameter_runs;");
            return await cmd.ExecuteNonQueryAsync(token);
        }, ct);

    // ── Internal: baca ──────────────────────────────────────────────────────

    private static HeParameterRun ReadRun(DbDataReader reader) => new()
    {
        RunId = reader.GetInt64(0),
        Input = new HeParameterInput
        {
            Sp   = reader.GetDouble(1),
            Kc   = reader.GetDouble(2),
            Ti   = reader.GetDouble(3),
            Td   = reader.GetDouble(4),
            Pump = reader.GetDouble(5),
        },
        Source            = Enum.Parse<HeParameterRunSource>(reader.GetString(6)),
        Status            = Enum.Parse<HeParameterRunStatus>(reader.GetString(7)),
        RequestedByUserId = ReadString(reader, 8),
        RequestedByName   = ReadString(reader, 9),
        StartedAtUtc      = FromDbTime(reader.GetString(10)),
        FinishedAtUtc     = ReadTime(reader, 11),
        DurationSeconds   = ReadDouble(reader, 12),
        ReuseCount        = reader.GetInt32(13),
        LastReusedAtUtc   = ReadTime(reader, 14),
        Note              = ReadString(reader, 15),
    };

    private static async Task<HeParameterRunMetrics?> ReadMetricsAsync(
        SqliteConnection conn, SqliteTransaction? tx, long runId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            SELECT final_pv_shell_out, final_pv_shell_in, final_flow_tube, final_flow_shell,
                   final_signal_percent, steady_state_error, rise_time_seconds,
                   settling_time_seconds, overshoot_percent, peak_value, ise, iae, itae
            FROM he_parameter_run_metrics
            WHERE run_id = $runId;
            """);
        Bind(cmd, "$runId", runId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new HeParameterRunMetrics
        {
            FinalPvShellOut     = ReadDouble(reader, 0),
            FinalPvShellIn      = ReadDouble(reader, 1),
            FinalFlowTube       = ReadDouble(reader, 2),
            FinalFlowShell      = ReadDouble(reader, 3),
            FinalSignalPercent  = ReadDouble(reader, 4),
            SteadyStateError    = ReadDouble(reader, 5),
            RiseTimeSeconds     = ReadDouble(reader, 6),
            SettlingTimeSeconds = ReadDouble(reader, 7),
            OvershootPercent    = ReadDouble(reader, 8),
            PeakValue           = ReadDouble(reader, 9),
            Ise                 = ReadDouble(reader, 10),
            Iae                 = ReadDouble(reader, 11),
            Itae                = ReadDouble(reader, 12),
        };
    }

    private static async Task<List<HeParameterRunSample>> ReadSamplesAsync(
        SqliteConnection conn, SqliteTransaction? tx, long runId, CancellationToken ct)
    {
        var samples = new List<HeParameterRunSample>();
        await using var cmd = Command(conn, tx, """
            SELECT t_seconds, flow_tube, flow_shell, signal_ma, signal_percent,
                   pv_shell_in, set_point, pv_shell_out
            FROM he_parameter_run_samples
            WHERE run_id = $runId
            ORDER BY t_seconds ASC;
            """);
        Bind(cmd, "$runId", runId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            samples.Add(new HeParameterRunSample
            {
                TSeconds      = reader.GetDouble(0),
                FlowTube      = ReadDouble(reader, 1),
                FlowShell     = ReadDouble(reader, 2),
                SignalMa      = ReadDouble(reader, 3),
                SignalPercent = ReadDouble(reader, 4),
                PvShellIn     = ReadDouble(reader, 5),
                SetPoint      = ReadDouble(reader, 6),
                PvShellOut    = ReadDouble(reader, 7),
            });
        }
        return samples;
    }

    // ── Internal: tulis ─────────────────────────────────────────────────────

    private static async Task WriteMetricsAsync(
        SqliteConnection conn, SqliteTransaction? tx, long runId, HeParameterRunMetrics m, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            INSERT OR REPLACE INTO he_parameter_run_metrics
                (run_id, final_pv_shell_out, final_pv_shell_in, final_flow_tube, final_flow_shell,
                 final_signal_percent, steady_state_error, rise_time_seconds, settling_time_seconds,
                 overshoot_percent, peak_value, ise, iae, itae)
            VALUES
                ($runId, $finalPvOut, $finalPvIn, $finalFlowTube, $finalFlowShell,
                 $finalSignal, $sse, $rise, $settling,
                 $overshoot, $peak, $ise, $iae, $itae);
            """);
        Bind(cmd, "$runId", runId);
        Bind(cmd, "$finalPvOut", m.FinalPvShellOut);
        Bind(cmd, "$finalPvIn", m.FinalPvShellIn);
        Bind(cmd, "$finalFlowTube", m.FinalFlowTube);
        Bind(cmd, "$finalFlowShell", m.FinalFlowShell);
        Bind(cmd, "$finalSignal", m.FinalSignalPercent);
        Bind(cmd, "$sse", m.SteadyStateError);
        Bind(cmd, "$rise", m.RiseTimeSeconds);
        Bind(cmd, "$settling", m.SettlingTimeSeconds);
        Bind(cmd, "$overshoot", m.OvershootPercent);
        Bind(cmd, "$peak", m.PeakValue);
        Bind(cmd, "$ise", m.Ise);
        Bind(cmd, "$iae", m.Iae);
        Bind(cmd, "$itae", m.Itae);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Menulis seluruh kurva respons. Satu command disiapkan sekali lalu
    /// nilainya diganti per baris — kurva satu run bisa ribuan titik, dan
    /// menyiapkan ulang perintah SQL untuk tiap titik jauh lebih lambat.
    /// </summary>
    private static async Task WriteSamplesAsync(
        SqliteConnection conn, SqliteTransaction? tx, long runId,
        IReadOnlyList<HeParameterRunSample> samples, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            INSERT OR REPLACE INTO he_parameter_run_samples
                (run_id, t_seconds, flow_tube, flow_shell, signal_ma, signal_percent,
                 pv_shell_in, set_point, pv_shell_out)
            VALUES
                ($runId, $t, $flowTube, $flowShell, $signalMa, $signalPercent,
                 $pvIn, $setPoint, $pvOut);
            """);

        var pRunId  = cmd.Parameters.Add("$runId", SqliteType.Integer);
        var pT      = cmd.Parameters.Add("$t", SqliteType.Real);
        var pTube   = cmd.Parameters.Add("$flowTube", SqliteType.Real);
        var pShell  = cmd.Parameters.Add("$flowShell", SqliteType.Real);
        var pMa     = cmd.Parameters.Add("$signalMa", SqliteType.Real);
        var pPct    = cmd.Parameters.Add("$signalPercent", SqliteType.Real);
        var pPvIn   = cmd.Parameters.Add("$pvIn", SqliteType.Real);
        var pSp     = cmd.Parameters.Add("$setPoint", SqliteType.Real);
        var pPvOut  = cmd.Parameters.Add("$pvOut", SqliteType.Real);

        pRunId.Value = runId;
        foreach (var s in samples)
        {
            pT.Value     = s.TSeconds;
            pTube.Value  = (object?)s.FlowTube      ?? DBNull.Value;
            pShell.Value = (object?)s.FlowShell     ?? DBNull.Value;
            pMa.Value    = (object?)s.SignalMa      ?? DBNull.Value;
            pPct.Value   = (object?)s.SignalPercent ?? DBNull.Value;
            pPvIn.Value  = (object?)s.PvShellIn     ?? DBNull.Value;
            pSp.Value    = (object?)s.SetPoint      ?? DBNull.Value;
            pPvOut.Value = (object?)s.PvShellOut    ?? DBNull.Value;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
