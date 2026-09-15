using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Sisi Client dari antrian giliran HE: memanggil endpoint <c>/he/...</c> milik
/// <see cref="ShareServer"/> lewat HTTP dengan session token sebagai Bearer.
/// Gayanya mengikuti <see cref="TaskClient"/> — JSON dirakit/dibaca dengan
/// <see cref="JsonNode"/> (bebas refleksi, aman di-trim).
///
/// Client tidak punya database antrian sendiri dan tidak boleh punya: yang
/// benar-benar tersambung ke plant hanya Server, jadi hanya Server yang berhak
/// memutuskan giliran. Semua yang dikembalikan di sini adalah keputusan Server.
/// </summary>
public static class HeQueueClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // ── Antrian ─────────────────────────────────────────────────────────────

    public static async Task<HeQueueStatus?> GetStatusAsync(string host, string token)
    {
        var node = await GetAsync(host, token, ShareProtocol.HeQueuePath);
        return node is null ? null : HeQueueJson.ToStatus(node);
    }

    public static async Task<HeQueueDecision?> RequestAsync(string host, string token, HeRequestType type)
    {
        // 409 bukan kegagalan di sini: itulah jawaban "plant sedang dipakai, Anda
        // masuk antrian", lengkap dengan posisinya — jadi badannya tetap dibaca.
        var node = await PostAsync(host, token, ShareProtocol.HeQueueRequestPath,
            new JsonObject { ["type"] = type.ToString() }, acceptConflict: true);
        return node is null ? null : HeQueueJson.ToDecision(node);
    }

    public static async Task<bool> ReleaseAsync(string host, string token) =>
        await PostAsync(host, token, ShareProtocol.HeQueueReleasePath, new JsonObject()) is not null;

    public static async Task<bool> CancelAsync(string host, string token) =>
        await PostAsync(host, token, ShareProtocol.HeQueueCancelPath, new JsonObject()) is not null;

    public static async Task<bool> ForceReleaseAsync(string host, string token, string? note) =>
        await PostAsync(host, token, ShareProtocol.HeQueueForceReleasePath,
            new JsonObject { ["note"] = note }) is not null;

    public static async Task<IReadOnlyList<HeQueueLogEntry>> GetRecentLogAsync(string host, string token, int limit)
    {
        var node = await GetAsync(host, token, $"{ShareProtocol.HeQueueLogPath}?limit={limit}");
        if (node?["entries"] is not JsonArray arr) return [];

        var list = new List<HeQueueLogEntry>();
        foreach (var item in arr)
            if (item is not null) list.Add(HeQueueJson.ToLogEntry(item));
        return list;
    }

    // ── Cache hasil parameter ───────────────────────────────────────────────

    public static async Task<HeRunHistory> GetRecentRunsAsync(string host, string token, int limit)
    {
        var node = await GetAsync(host, token, $"{ShareProtocol.HeParamRunsPath}?limit={limit}");
        if (node is null) return new HeRunHistory();

        var runs = new List<HeParameterRun>();
        if (node["runs"] is JsonArray arr)
            foreach (var item in arr)
                if (item is not null) runs.Add(HeQueueJson.ToRun(item));

        return new HeRunHistory
        {
            Runs  = runs,
            Stats = node["stats"] is { } stats ? HeQueueJson.ToStats(stats) : null,
            Ok    = true,
        };
    }

    public static async Task<HeParameterRun?> FindCachedRunAsync(string host, string token, HeParameterInput input)
    {
        var node = await PostAsync(host, token, ShareProtocol.HeParamLookupPath, new JsonObject
        {
            ["sp"]   = input.Sp,
            ["kc"]   = input.Kc,
            ["ti"]   = input.Ti,
            ["td"]   = input.Td,
            ["pump"] = input.Pump,
        });

        if (node is null || (bool?)node["found"] != true) return null;
        return node["run"] is { } run ? HeQueueJson.ToRun(run) : null;
    }

    // ── Internals ───────────────────────────────────────────────────────────

    private static async Task<JsonNode?> GetAsync(string host, string token, string path)
    {
        if (!Usable(host, token)) return null;
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            using var req  = new HttpRequestMessage(HttpMethod.Get, $"{AuthClient.BaseUrl(host)}{path}");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private static async Task<JsonNode?> PostAsync(
        string host, string token, string path, JsonObject body, bool acceptConflict = false)
    {
        if (!Usable(host, token)) return null;
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            using var req  = new HttpRequestMessage(HttpMethod.Post, $"{AuthClient.BaseUrl(host)}{path}")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode &&
                !(acceptConflict && resp.StatusCode == HttpStatusCode.Conflict))
                return null;

            return JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    private static bool Usable(string host, string token) =>
        !string.IsNullOrWhiteSpace(AuthClient.NormalizeHost(host)) && !string.IsNullOrWhiteSpace(token);
}

/// <summary>
/// Bentuk JSON antrian HE di kabel — satu tempat untuk kedua sisi, supaya Server
/// dan Client tidak bisa diam-diam berbeda pendapat soal nama field. Server
/// memakai bagian <c>From*</c>, Client memakai bagian <c>To*</c>.
///
/// Waktu selalu UTC dalam format ISO-8601 ("O"), bukan waktu lokal: Server dan
/// Client bisa berada di zona waktu berbeda, dan yang dibandingkan di layar
/// adalah selisihnya ("sudah dipegang 3 menit").
/// </summary>
public static class HeQueueJson
{
    // ── Server → JSON ───────────────────────────────────────────────────────

    public static JsonObject? FromHolder(HeControlHolder? holder) => holder is null ? null : new JsonObject
    {
        ["userId"]      = holder.UserId,
        ["displayName"] = holder.DisplayName,
        ["priority"]    = (int)holder.Priority,
        ["requestType"] = holder.RequestType.ToString(),
        ["heldSince"]   = Time(holder.HeldSinceUtc),
    };

    public static JsonObject FromItem(HeQueueItem item) => new()
    {
        ["queueId"]     = item.QueueId,
        ["userId"]      = item.UserId,
        ["displayName"] = item.DisplayName,
        ["priority"]    = (int)item.Priority,
        ["requestType"] = item.RequestType.ToString(),
        ["requestedAt"] = Time(item.RequestedAtUtc),
        ["status"]      = item.Status.ToString(),
    };

    public static JsonObject FromSnapshot(HeQueueSnapshot snapshot)
    {
        var waiting = new JsonArray();
        foreach (var item in snapshot.Waiting) waiting.Add((JsonNode)FromItem(item));

        return new JsonObject
        {
            ["holder"]  = FromHolder(snapshot.Holder),
            ["waiting"] = waiting,
        };
    }

    public static JsonObject FromStatus(HeQueueStatus status)
    {
        var json = FromSnapshot(status.Snapshot);
        json["myPosition"]      = status.MyPosition;
        json["iAmHolder"]       = status.IAmHolder;
        json["canForceRelease"] = status.CanForceRelease;
        return json;
    }

    public static JsonObject FromDecision(HeQueueDecision decision) => new()
    {
        ["outcome"]    = decision.Outcome.ToString(),
        ["canRunNow"]  = decision.CanRunNow,
        ["position"]   = decision.Position,
        ["item"]       = FromItem(decision.Item),
        ["holder"]     = FromHolder(decision.Holder),
        ["overridden"] = decision.Overridden is null ? null : FromItem(decision.Overridden),
    };

    public static JsonObject FromLogEntry(HeQueueLogEntry entry) => new()
    {
        ["logId"]       = entry.LogId,
        ["eventType"]   = entry.EventType,
        ["userId"]      = entry.UserId,
        ["displayName"] = entry.DisplayName,
        ["priority"]    = entry.Priority is { } p ? JsonValue.Create((int)p) : null,
        ["requestType"] = entry.RequestType,
        ["queueId"]     = entry.QueueId,
        ["relatedUserId"] = entry.RelatedUserId,
        ["occurredAt"]  = Time(entry.OccurredAtUtc),
        ["note"]        = entry.Note,
    };

    /// <summary>
    /// Satu run cache lengkap dengan metrik dan kurvanya. Kurva dijarangkan dulu
    /// (<paramref name="maxSamples"/>): run plant bisa ribuan titik dan Client
    /// hanya perlu bentuk kurvanya, bukan tiap sampel mentahnya.
    /// </summary>
    public static JsonObject FromRun(HeParameterRun run, int maxSamples = 1200)
    {
        var samples = new JsonArray();
        // Pembagian dibulatkan ke ATAS: dibulatkan ke bawah, 5000 titik dengan
        // batas 1200 menghasilkan stride 4 dan 1251 titik terkirim — batasnya
        // jadi tidak berarti. Ke atas, stride 5 dan 1001 titik.
        int cap    = Math.Max(1, maxSamples);
        int stride = Math.Max(1, (run.Samples.Count + cap - 1) / cap);
        for (int i = 0; i < run.Samples.Count; i += stride) samples.Add((JsonNode)FromSample(run.Samples[i]));
        // Titik terakhir selalu ikut: nilai akhir itu yang dibaca sebagai steady state.
        if (run.Samples.Count > 0 && (run.Samples.Count - 1) % stride != 0)
            samples.Add((JsonNode)FromSample(run.Samples[^1]));

        return new JsonObject
        {
            ["runId"]       = run.RunId,
            ["sp"]          = run.Input.Sp,
            ["kc"]          = run.Input.Kc,
            ["ti"]          = run.Input.Ti,
            ["td"]          = run.Input.Td,
            ["pump"]        = run.Input.Pump,
            ["source"]      = run.Source.ToString(),
            ["status"]      = run.Status.ToString(),
            ["requestedByUserId"] = run.RequestedByUserId,
            ["requestedByName"]   = run.RequestedByName,
            ["startedAt"]   = Time(run.StartedAtUtc),
            ["finishedAt"]  = run.FinishedAtUtc is null ? null : Time(run.FinishedAtUtc.Value),
            ["durationSeconds"] = run.DurationSeconds,
            ["reuseCount"]  = run.ReuseCount,
            ["note"]        = run.Note,
            ["metrics"]     = run.Metrics is null ? null : FromMetrics(run.Metrics),
            ["samples"]     = samples,
        };
    }

    private static JsonObject FromMetrics(HeParameterRunMetrics m) => new()
    {
        ["finalPvShellOut"]    = m.FinalPvShellOut,
        ["finalPvShellIn"]     = m.FinalPvShellIn,
        ["finalFlowTube"]      = m.FinalFlowTube,
        ["finalFlowShell"]     = m.FinalFlowShell,
        ["finalSignalPercent"] = m.FinalSignalPercent,
        ["steadyStateError"]   = m.SteadyStateError,
        ["riseTimeSeconds"]    = m.RiseTimeSeconds,
        ["settlingTimeSeconds"] = m.SettlingTimeSeconds,
        ["overshootPercent"]   = m.OvershootPercent,
        ["peakValue"]          = m.PeakValue,
        ["ise"]                = m.Ise,
        ["iae"]                = m.Iae,
        ["itae"]               = m.Itae,
    };

    public static JsonObject FromStats(HeParameterCacheStats stats) => new()
    {
        ["totalRuns"]     = stats.TotalRuns,
        ["completedRuns"] = stats.CompletedRuns,
        ["totalReuses"]   = stats.TotalReuses,
        ["lastRunUtc"]    = stats.LastRunUtc is { } last ? Time(last) : null,
    };

    private static JsonObject FromSample(HeParameterRunSample s) => new()
    {
        ["t"]             = s.TSeconds,
        ["flowTube"]      = s.FlowTube,
        ["flowShell"]     = s.FlowShell,
        ["signalMa"]      = s.SignalMa,
        ["signalPercent"] = s.SignalPercent,
        ["pvShellIn"]     = s.PvShellIn,
        ["setPoint"]      = s.SetPoint,
        ["pvShellOut"]    = s.PvShellOut,
    };

    // ── JSON → Client ───────────────────────────────────────────────────────

    public static HeQueueStatus ToStatus(JsonNode node) => new()
    {
        Snapshot        = ToSnapshot(node),
        MyPosition      = (int?)node["myPosition"] ?? 0,
        IAmHolder       = (bool?)node["iAmHolder"] ?? false,
        CanForceRelease = (bool?)node["canForceRelease"] ?? false,
    };

    public static HeQueueSnapshot ToSnapshot(JsonNode node)
    {
        var waiting = new List<HeQueueItem>();
        if (node["waiting"] is JsonArray arr)
            foreach (var item in arr)
                if (item is not null) waiting.Add(ToItem(item));

        return new HeQueueSnapshot
        {
            Holder  = node["holder"] is { } h ? ToHolder(h) : null,
            Waiting = waiting,
        };
    }

    public static HeQueueDecision ToDecision(JsonNode node) => new()
    {
        Outcome    = Enum.TryParse<HeQueueOutcome>((string?)node["outcome"], out var o) ? o : HeQueueOutcome.Queued,
        Item       = node["item"] is { } i ? ToItem(i) : new HeQueueItem(),
        Position   = (int?)node["position"] ?? 0,
        Holder     = node["holder"] is { } h ? ToHolder(h) : null,
        Overridden = node["overridden"] is { } v ? ToItem(v) : null,
    };

    public static HeQueueLogEntry ToLogEntry(JsonNode node) => new()
    {
        LogId         = (long?)node["logId"] ?? 0,
        EventType     = (string?)node["eventType"] ?? "",
        UserId        = (string?)node["userId"] ?? "",
        DisplayName   = (string?)node["displayName"],
        Priority      = (int?)node["priority"] is { } p ? HeQueuePriorityMap.FromDbValue(p) : null,
        RequestType   = (string?)node["requestType"],
        QueueId       = (long?)node["queueId"],
        RelatedUserId = (string?)node["relatedUserId"],
        OccurredAtUtc = ParseTime(node["occurredAt"]),
        Note          = (string?)node["note"],
    };

    public static HeParameterRun ToRun(JsonNode node)
    {
        var run = new HeParameterRun
        {
            RunId = (long?)node["runId"] ?? 0,
            Input = new HeParameterInput
            {
                Sp   = (double?)node["sp"]   ?? 0,
                Kc   = (double?)node["kc"]   ?? 0,
                Ti   = (double?)node["ti"]   ?? 0,
                Td   = (double?)node["td"]   ?? 0,
                Pump = (double?)node["pump"] ?? 0,
            },
            Source = Enum.TryParse<HeParameterRunSource>((string?)node["source"], out var src) ? src : HeParameterRunSource.Plant,
            Status = Enum.TryParse<HeParameterRunStatus>((string?)node["status"], out var st) ? st : HeParameterRunStatus.Completed,
            RequestedByUserId = (string?)node["requestedByUserId"],
            RequestedByName   = (string?)node["requestedByName"],
            StartedAtUtc      = ParseTime(node["startedAt"]),
            FinishedAtUtc     = node["finishedAt"] is { } f ? ParseTime(f) : null,
            DurationSeconds   = (double?)node["durationSeconds"],
            ReuseCount        = (int?)node["reuseCount"] ?? 0,
            Note              = (string?)node["note"],
        };

        if (node["metrics"] is { } m)
            run.Metrics = new HeParameterRunMetrics
            {
                FinalPvShellOut     = (double?)m["finalPvShellOut"],
                FinalPvShellIn      = (double?)m["finalPvShellIn"],
                FinalFlowTube       = (double?)m["finalFlowTube"],
                FinalFlowShell      = (double?)m["finalFlowShell"],
                FinalSignalPercent  = (double?)m["finalSignalPercent"],
                SteadyStateError    = (double?)m["steadyStateError"],
                RiseTimeSeconds     = (double?)m["riseTimeSeconds"],
                SettlingTimeSeconds = (double?)m["settlingTimeSeconds"],
                OvershootPercent    = (double?)m["overshootPercent"],
                PeakValue           = (double?)m["peakValue"],
                Ise                 = (double?)m["ise"],
                Iae                 = (double?)m["iae"],
                Itae                = (double?)m["itae"],
            };

        if (node["samples"] is JsonArray samples)
            foreach (var s in samples)
            {
                if (s is null) continue;
                run.Samples.Add(new HeParameterRunSample
                {
                    TSeconds      = (double?)s["t"] ?? 0,
                    FlowTube      = (double?)s["flowTube"],
                    FlowShell     = (double?)s["flowShell"],
                    SignalMa      = (double?)s["signalMa"],
                    SignalPercent = (double?)s["signalPercent"],
                    PvShellIn     = (double?)s["pvShellIn"],
                    SetPoint      = (double?)s["setPoint"],
                    PvShellOut    = (double?)s["pvShellOut"],
                });
            }

        return run;
    }

    public static HeParameterCacheStats ToStats(JsonNode node) => new(
        (int?)node["totalRuns"]     ?? 0,
        (int?)node["completedRuns"] ?? 0,
        (int?)node["totalReuses"]   ?? 0,
        node["lastRunUtc"] is { } last ? ParseTime(last) : null);

    private static HeControlHolder ToHolder(JsonNode node) => new()
    {
        UserId       = (string?)node["userId"]      ?? "",
        DisplayName  = (string?)node["displayName"] ?? "",
        Priority     = HeQueuePriorityMap.FromDbValue((int?)node["priority"] ?? 3),
        RequestType  = Enum.TryParse<HeRequestType>((string?)node["requestType"], out var t) ? t : HeRequestType.Run,
        HeldSinceUtc = ParseTime(node["heldSince"]),
    };

    private static HeQueueItem ToItem(JsonNode node) => new()
    {
        QueueId        = (long?)node["queueId"] ?? 0,
        UserId         = (string?)node["userId"]      ?? "",
        DisplayName    = (string?)node["displayName"] ?? "",
        Priority       = HeQueuePriorityMap.FromDbValue((int?)node["priority"] ?? 3),
        RequestType    = Enum.TryParse<HeRequestType>((string?)node["requestType"], out var t) ? t : HeRequestType.Run,
        RequestedAtUtc = ParseTime(node["requestedAt"]),
        Status         = Enum.TryParse<HeQueueItemStatus>((string?)node["status"], out var s) ? s : HeQueueItemStatus.Waiting,
    };

    private static string Time(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(JsonNode? node)
    {
        var text = (string?)node;
        if (string.IsNullOrWhiteSpace(text)) return DateTime.UtcNow;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value : DateTime.UtcNow;
    }
}

/// <summary>
/// Satu pintu yang dipakai layar untuk urusan giliran plant HE, menyembunyikan
/// beda Server/Client persis seperti <see cref="LearningTaskService"/> melakukannya
/// untuk tugas:
///
///   • <b>Server</b> — langsung ke <see cref="HeQueueRepository"/> dan
///     <see cref="HeParameterCacheRepository"/> miliknya sendiri.
///   • <b>Client</b> — lewat HTTP ke Server tempat pengguna login
///     (<see cref="HeQueueClient"/>).
///
/// Kalau belum ada yang login, antrian tidak bisa mengidentifikasi siapa pun.
/// Dalam keadaan itu method di sini mengembalikan <c>null</c> yang berarti
/// "antrian tidak berlaku" — bukan "ditolak" — supaya rig tidak jadi mati total
/// hanya karena lapisan antriannya tidak bisa dipakai. Penjaga sebenarnya tetap
/// ada di Server: <c>/sim/pid/run</c> menolak perintah dari Client yang tidak
/// memegang kendali, jadi Client yang nakal tidak bisa menyelinap lewat sini.
/// </summary>
public static class HeControlService
{
    /// <summary>Batas waktu memegang kendali tanpa aktivitas sebelum dilepas otomatis.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(30);

    /// <summary>Antrian hanya berjalan kalau ada identitas yang bisa dicatat.</summary>
    public static bool Enabled => SessionService.Instance.IsSignedIn;

    public static string MyUserId => SessionService.Instance.Username;

    public static async Task<HeQueueDecision?> RequestAsync(HeRequestType type = HeRequestType.Run)
    {
        if (!Enabled) return null;

        var s = SessionService.Instance;
        if (BuildInfo.IsServer)
            return await App.HeQueue.RequestControlAsync(s.Username, s.DisplayName, s.Role, type);

        var cfg = AppSettingsService.Load();
        return await HeQueueClient.RequestAsync(cfg.ServerHost, cfg.ServerToken, type);
    }

    public static async Task<HeQueueStatus?> GetStatusAsync()
    {
        if (!Enabled) return null;

        var s = SessionService.Instance;
        if (BuildInfo.IsServer)
            return await BuildStatusAsync(App.HeQueue, s.Username, s.Role);

        var cfg = AppSettingsService.Load();
        return await HeQueueClient.GetStatusAsync(cfg.ServerHost, cfg.ServerToken);
    }

    public static async Task<bool> ReleaseAsync()
    {
        if (!Enabled) return false;

        if (BuildInfo.IsServer)
        {
            await App.HeQueue.ReleaseControlAsync(SessionService.Instance.Username);
            return true;
        }

        var cfg = AppSettingsService.Load();
        return await HeQueueClient.ReleaseAsync(cfg.ServerHost, cfg.ServerToken);
    }

    public static async Task<bool> CancelAsync()
    {
        if (!Enabled) return false;

        if (BuildInfo.IsServer)
            return await App.HeQueue.CancelRequestAsync(SessionService.Instance.Username);

        var cfg = AppSettingsService.Load();
        return await HeQueueClient.CancelAsync(cfg.ServerHost, cfg.ServerToken);
    }

    /// <summary>Mencabut paksa kendali orang lain — hanya Admin.</summary>
    public static async Task<bool> ForceReleaseAsync(string? note = null)
    {
        if (!Enabled || !UserRoles.IsAdmin(SessionService.Instance.Role)) return false;

        if (BuildInfo.IsServer)
        {
            await App.HeQueue.ForceReleaseAsync(SessionService.Instance.Username, note);
            return true;
        }

        var cfg = AppSettingsService.Load();
        return await HeQueueClient.ForceReleaseAsync(cfg.ServerHost, cfg.ServerToken, note);
    }

    public static async Task<IReadOnlyList<HeQueueLogEntry>> GetRecentLogAsync(int limit = 50)
    {
        if (!Enabled) return [];

        if (BuildInfo.IsServer)
            return await App.HeQueue.GetRecentLogAsync(limit);

        var cfg = AppSettingsService.Load();
        return await HeQueueClient.GetRecentLogAsync(cfg.ServerHost, cfg.ServerToken, limit);
    }

    /// <summary>
    /// Daftar percobaan terbaru beserta ringkasan cache — isi halaman riwayat.
    /// Di Server dibaca langsung dari repository, di Client lewat HTTP.
    /// </summary>
    public static async Task<HeRunHistory> GetRecentRunsAsync(int limit = 100)
    {
        if (BuildInfo.IsServer)
            return new HeRunHistory
            {
                Runs  = await App.HeParamCache.ListRunsAsync(limit),
                Stats = await App.HeParamCache.GetStatsAsync(),
                Ok    = true,
            };

        if (!Enabled) return new HeRunHistory();
        var cfg = AppSettingsService.Load();
        return await HeQueueClient.GetRecentRunsAsync(cfg.ServerHost, cfg.ServerToken, limit);
    }

    /// <summary>
    /// Hasil yang sudah pernah dijalankan untuk kombinasi parameter ini, atau
    /// <c>null</c> kalau memang belum pernah dicoba (plant harus dijalankan).
    /// </summary>
    public static async Task<HeParameterRun?> FindCachedRunAsync(HeParameterInput input)
    {
        if (BuildInfo.IsServer)
            return await App.HeParamCache.FindCachedRunAsync(input);

        if (!Enabled) return null;
        var cfg = AppSettingsService.Load();
        return await HeQueueClient.FindCachedRunAsync(cfg.ServerHost, cfg.ServerToken, input);
    }

    /// <summary>
    /// Merakit <see cref="HeQueueStatus"/> dari repository — dipakai sisi Server,
    /// baik untuk layarnya sendiri maupun untuk menjawab <c>GET /he/queue</c>
    /// milik Client, supaya keduanya membaca aturan yang sama persis.
    /// </summary>
    public static async Task<HeQueueStatus> BuildStatusAsync(
        HeQueueRepository queue, string userId, string role, CancellationToken ct = default)
    {
        var snapshot = await queue.GetSnapshotAsync(ct);
        return new HeQueueStatus
        {
            Snapshot        = snapshot,
            MyPosition      = await queue.GetPositionAsync(userId, ct),
            IAmHolder       = snapshot.Holder?.UserId == userId,
            CanForceRelease = UserRoles.IsAdmin(role),
        };
    }
}
