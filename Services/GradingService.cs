// Services/GradingService.cs
// Tambahkan file ini ke folder Services/ yang sudah ada di project TLIG-Dashboard

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services
{
    /// <summary>
    /// Service penilaian mahasiswa.
    /// Dalam implementasi nyata, ganti metode-metode ini dengan panggilan ke API/database.
    /// Saat ini menggunakan data dummy untuk demonstrasi UI.
    /// </summary>
    public class GradingService
    {
        // ── Singleton sederhana ───────────────────────────────────────────────
        private static GradingService? _instance;
        public static GradingService Instance => _instance ??= new GradingService();

        // ── In-memory storage (ganti dengan DB/API call di produksi) ─────────
        private readonly List<PeerEvaluation>   _peerEvaluations   = new();
        private readonly List<SystemEvaluation> _systemEvaluations = new();
        private readonly List<LecturerGrade>    _lecturerGrades    = new();
        private readonly List<GroupActivity>    _groupActivities   = new();
        private readonly List<SimulationResult> _simulationResults = new();
        private readonly List<TuningRecord>     _tuningRecords     = new();
        private readonly List<AiUsageRecord>    _aiUsageRecords    = new();

        private GradingService()
        {
            SeedDemoData();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  PEER EVALUATION
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<PeerEvaluation>> GetPeerEvaluationsForStudentAsync(string evaluateeId)
            => Task.FromResult(_peerEvaluations.Where(e => e.EvaluateeId == evaluateeId).ToList());

        public Task<List<PeerEvaluation>> GetPeerEvaluationsByEvaluatorAsync(string evaluatorId)
            => Task.FromResult(_peerEvaluations.Where(e => e.EvaluatorId == evaluatorId).ToList());

        public Task<List<PeerEvaluation>> GetPeerEvaluationsByAssignmentAsync(string assignmentId)
            => Task.FromResult(_peerEvaluations.Where(e => e.AssignmentId == assignmentId).ToList());

        /// <summary>Mahasiswa submit penilaian peer</summary>
        public Task<bool> SubmitPeerEvaluationAsync(PeerEvaluation evaluation)
        {
            // Cegah double-submit
            var existing = _peerEvaluations.FirstOrDefault(e =>
                e.EvaluatorId == evaluation.EvaluatorId &&
                e.EvaluateeId == evaluation.EvaluateeId &&
                e.AssignmentId == evaluation.AssignmentId);

            if (existing != null)
                _peerEvaluations.Remove(existing);

            evaluation.Score = evaluation.AverageScore;
            _peerEvaluations.Add(evaluation);

            // Trigger: update system score jika ada
            RecalculateSummaryScore(evaluation.EvaluateeId, evaluation.AssignmentId);
            return Task.FromResult(true);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SYSTEM EVALUATION
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<SystemEvaluation>> GetSystemEvaluationsByAssignmentAsync(string assignmentId)
            => Task.FromResult(_systemEvaluations.Where(e => e.AssignmentId == assignmentId).ToList());

        public Task<SystemEvaluation?> GetSystemEvaluationForStudentAsync(string studentId, string assignmentId)
            => Task.FromResult(_systemEvaluations.FirstOrDefault(e =>
                e.StudentId == studentId && e.AssignmentId == assignmentId));

        // ── Algoritma skor sistem — berdasarkan 3 parameter nyata ──────────────

        /// <summary>
        /// Hitung skor sistem dari 3 parameter nyata:
        ///   1. Rekam Tuning Parameter  (maks 40 poin)
        ///   2. Hasil Penggunaan AI     (maks 20 poin)
        ///   3. Hasil Simulasi          (maks 40 poin)
        /// </summary>
        private SystemEvaluation ComputeSystemScore(
            string studentId, string assignmentId, DateTime? deadline = null)
        {
            // ── 1. Tuning Parameter (maks 40 poin) ───────────────────────────
            var tunings = _tuningRecords
                .Where(t => t.StudentId == studentId && t.AssignmentId == assignmentId)
                .ToList();
            // Eksplorasi: variasi percobaan Kp/Ki/Kd yang berbeda (maks 15 poin, 3 poin/percobaan unik)
            int uniqueTunings = tunings.Select(t => $"{t.Kp:F1}{t.Ki:F2}{t.Kd:F2}").Distinct().Count();
            double tuningExploration = Math.Min(uniqueTunings * 3, 15);
            // Kualitas: skor tuning terbaik yang dicapai (maks 25 poin)
            double bestTuningQuality = tunings.Any() ? tunings.Max(t => t.QualityScore) : 0;
            double tuningQuality = bestTuningQuality * 0.25;
            double tuningScore   = tuningExploration + tuningQuality;

            // ── 2. Penggunaan AI (maks 20 poin) ──────────────────────────────
            var aiUsages = _aiUsageRecords
                .Where(a => a.StudentId == studentId && a.AssignmentId == assignmentId)
                .ToList();
            int productiveSessions = aiUsages.Count(a => a.IsProductive);
            double aiScore = Math.Min(productiveSessions * 5, 20);

            // ── 3. Hasil Simulasi (maks 40 poin) ─────────────────────────────
            var sims = _simulationResults
                .Where(s => s.StudentId == studentId && s.AssignmentId == assignmentId)
                .ToList();
            double bestSimScore  = sims.Any() ? sims.Max(s => s.Score) : 0;
            double simScore = bestSimScore * 0.40;

            // ── Final ─────────────────────────────────────────────────────────
            double finalScore = Math.Min(tuningScore + aiScore + simScore, 100);

            // Tepat waktu: ada tuning/simulasi sebelum deadline
            bool onTime = deadline.HasValue
                ? tunings.Any(t => t.RecordedAt <= deadline.Value) ||
                  sims.Any(s => s.FinishedAt <= deadline.Value)
                : tunings.Any() || sims.Any();

            return new SystemEvaluation
            {
                StudentId       = studentId,
                StudentName     = GetDemoStudentName(studentId),
                AssignmentId    = assignmentId,
                AssignmentTitle = "Tugas Kelompok - Sistem Kontrol PID",
                GroupId         = "GRP-01",
                Score           = finalScore,
                CommitsCount    = uniqueTunings,        // direpurpose: jumlah percobaan tuning unik
                FilesModified   = sims.Count,           // direpurpose: jumlah sesi simulasi
                SubmittedOnTime = onTime,
                TasksCompleted  = tunings.Count + aiUsages.Count + sims.Count,
                TasksTotal      = Math.Max(tunings.Count + aiUsages.Count + sims.Count, 3),
                GeneratedAt     = DateTime.Now,
            };
        }

        /// <summary>Hitung dan simpan skor sistem untuk satu mahasiswa berdasarkan aktivitas nyata.</summary>
        public Task<SystemEvaluation> GenerateSystemScoreAsync(
            string studentId, string assignmentId, DateTime? deadline = null)
        {
            var eval = ComputeSystemScore(studentId, assignmentId, deadline);
            _systemEvaluations.RemoveAll(e =>
                e.StudentId == studentId && e.AssignmentId == assignmentId);
            _systemEvaluations.Add(eval);
            RecalculateSummaryScore(studentId, assignmentId);
            return Task.FromResult(eval);
        }

        /// <summary>
        /// Regenerasi skor sistem seluruh mahasiswa dalam satu tugas berdasarkan aktivitas nyata.
        /// Dipanggil dosen saat membuka halaman penilaian agar data selalu up-to-date.
        /// </summary>
        public async Task RegenerateSystemScoresAsync(string assignmentId, DateTime? deadline = null)
        {
            var studentIds = _groupActivities
                .Where(a => a.AssignmentId == assignmentId)
                .Select(a => a.StudentId)
                .Distinct()
                .ToList();

            foreach (var sid in studentIds)
                await GenerateSystemScoreAsync(sid, assignmentId, deadline);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  LECTURER GRADE
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<LecturerGrade>> GetLecturerGradesByAssignmentAsync(string assignmentId)
            => Task.FromResult(_lecturerGrades.Where(g => g.AssignmentId == assignmentId).ToList());

        public Task<LecturerGrade?> GetLecturerGradeForStudentAsync(string studentId, string assignmentId)
            => Task.FromResult(_lecturerGrades.FirstOrDefault(g =>
                g.StudentId == studentId && g.AssignmentId == assignmentId));

        /// <summary>Dosen submit/update nilai mahasiswa</summary>
        public Task<bool> SaveLecturerGradeAsync(LecturerGrade grade)
        {
            _lecturerGrades.RemoveAll(g => g.StudentId == grade.StudentId && g.AssignmentId == grade.AssignmentId);
            grade.GradedAt = DateTime.Now;
            _lecturerGrades.Add(grade);

            RecalculateSummaryScore(grade.StudentId, grade.AssignmentId);
            return Task.FromResult(true);
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SUMMARY
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<StudentGradeSummary>> GetGradeSummaryByAssignmentAsync(string assignmentId)
        {
            // Kumpulkan semua mahasiswa yg terlibat
            var studentIds = _peerEvaluations.Where(e => e.AssignmentId == assignmentId)
                .Select(e => e.EvaluateeId)
                .Union(_systemEvaluations.Where(e => e.AssignmentId == assignmentId).Select(e => e.StudentId))
                .Union(_lecturerGrades.Where(e => e.AssignmentId == assignmentId).Select(e => e.StudentId))
                .Distinct();

            var summaries = studentIds.Select(sid => BuildSummary(sid, assignmentId)).ToList();
            return Task.FromResult(summaries);
        }

        private StudentGradeSummary BuildSummary(string studentId, string assignmentId)
        {
            var peerScores = _peerEvaluations
                .Where(e => e.EvaluateeId == studentId && e.AssignmentId == assignmentId)
                .Select(e => e.Score).ToList();

            var sysEval = _systemEvaluations.FirstOrDefault(e =>
                e.StudentId == studentId && e.AssignmentId == assignmentId);

            var lecGrade = _lecturerGrades.FirstOrDefault(e =>
                e.StudentId == studentId && e.AssignmentId == assignmentId);

            return new StudentGradeSummary
            {
                StudentId = studentId,
                StudentName = GetDemoStudentName(studentId),
                AssignmentId = assignmentId,
                AssignmentTitle = "Tugas Kelompok - Sistem Kontrol",
                GroupId = "GRP-01",
                GroupName = "Kelompok 1",
                PeerScore = peerScores.Any() ? peerScores.Average() : null,
                SystemScore = sysEval?.Score,
                LecturerScore = lecGrade?.Score,
            };
        }

        private void RecalculateSummaryScore(string studentId, string assignmentId)
        {
            // Hook ini bisa dipakai untuk auto-push ke dosen / notifikasi
            // Di implementasi nyata: trigger SignalR / WebSocket / local event
        }

        // ─────────────────────────────────────────────────────────────────────
        //  GROUP ACTIVITIES
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<GroupActivity>> GetGroupActivitiesAsync(string assignmentId, int limit = 30)
        {
            var result = _groupActivities
                .Where(a => a.AssignmentId == assignmentId)
                .OrderByDescending(a => a.ActivityTime)
                .Take(limit)
                .ToList();
            return Task.FromResult(result);
        }

        /// <summary>File yang diunggah mahasiswa (ActivityType == "Upload") beserta metadata file.</summary>
        public Task<List<GroupActivity>> GetUploadedFilesAsync(string studentId, string assignmentId)
            => Task.FromResult(_groupActivities
                .Where(a => a.StudentId == studentId && a.AssignmentId == assignmentId
                         && a.ActivityType == "Upload" && a.FileName != null)
                .OrderByDescending(a => a.ActivityTime).ToList());

        public Task<List<GroupActivity>> GetAllUploadedFilesAsync(string assignmentId)
            => Task.FromResult(_groupActivities
                .Where(a => a.AssignmentId == assignmentId
                         && a.ActivityType == "Upload" && a.FileName != null)
                .OrderByDescending(a => a.ActivityTime).ToList());

        public Task AddGroupActivityAsync(GroupActivity activity)
        {
            _groupActivities.Add(activity);
            return Task.CompletedTask;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SIMULATION RESULTS
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<SimulationResult>> GetSimulationResultsForStudentAsync(string studentId)
            => Task.FromResult(_simulationResults.Where(r => r.StudentId == studentId).ToList());

        public Task<List<SimulationResult>> GetSimulationResultsAsync(string studentId, string assignmentId)
            => Task.FromResult(_simulationResults
                .Where(r => r.StudentId == studentId && r.AssignmentId == assignmentId)
                .OrderByDescending(r => r.StartedAt).ToList());

        // ─────────────────────────────────────────────────────────────────────
        //  TUNING RECORDS
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<TuningRecord>> GetTuningRecordsAsync(string studentId, string assignmentId)
            => Task.FromResult(_tuningRecords
                .Where(r => r.StudentId == studentId && r.AssignmentId == assignmentId)
                .OrderByDescending(r => r.RecordedAt).ToList());

        public Task<List<TuningRecord>> GetAllTuningRecordsAsync(string assignmentId)
            => Task.FromResult(_tuningRecords
                .Where(r => r.AssignmentId == assignmentId).ToList());

        public Task AddTuningRecordAsync(TuningRecord record)
        {
            _tuningRecords.Add(record);
            return Task.CompletedTask;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  AI USAGE RECORDS
        // ─────────────────────────────────────────────────────────────────────

        public Task<List<AiUsageRecord>> GetAiUsageAsync(string studentId, string assignmentId)
            => Task.FromResult(_aiUsageRecords
                .Where(r => r.StudentId == studentId && r.AssignmentId == assignmentId)
                .OrderByDescending(r => r.SessionAt).ToList());

        public Task<List<AiUsageRecord>> GetAllAiUsageAsync(string assignmentId)
            => Task.FromResult(_aiUsageRecords
                .Where(r => r.AssignmentId == assignmentId).ToList());

        public Task AddAiUsageAsync(AiUsageRecord record)
        {
            _aiUsageRecords.Add(record);
            return Task.CompletedTask;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  DEMO DATA SEED
        // ─────────────────────────────────────────────────────────────────────

        private static string GetDemoStudentName(string id) =>
            StudentService.Instance.GetName(id);

        private void SeedDemoData()
        {
            var students = new[] { "STU001", "STU002", "STU003", "STU004", "STU005" };
            const string assignId = "ASGN-001";
            var rnd = new Random(42);

            // ── Group Activities ─────────────────────────────────────────────
            // AutoScore per jenis aktivitas (digunakan juga untuk skor sistem)
            var actAutoScore = new Dictionary<string, double>
            {
                ["Submit"]  = 10,
                ["Edit"]    =  5,
                ["Upload"]  =  8,
                ["Comment"] =  3,
                ["Review"]  =  4,
            };

            var actTypes = new[] { "Submit", "Edit", "Upload", "Comment", "Review" };
            var descriptions = new Dictionary<string, string[]>
            {
                ["Submit"]  = new[] { "Mensubmit draft laporan", "Push kode implementasi", "Submit hasil akhir" },
                ["Edit"]    = new[] { "Mengedit bagian pendahuluan", "Update diagram blok", "Revisi bab 3" },
                ["Upload"]  = new[] { "Upload video demo", "Upload data percobaan", "Upload foto komponen" },
                ["Comment"] = new[] { "Memberi komentar review", "Feedback diagram sistem", "Review kode teman" },
                ["Review"]  = new[] { "Review laporan kelompok", "Cek hasil simulasi", "Verifikasi data" },
            };
            // Nama file demo untuk aktivitas Upload
            var demoFiles = new[]
            {
                ("Laporan_PID_Control.pdf",   "laporan"),
                ("Video_Demo_Simulasi.mp4",   "video"),
                ("Data_Percobaan.xlsx",       "data"),
                ("Diagram_Sistem.png",        "gambar"),
                ("Kode_Kontroler.zip",        "arsip"),
                ("Presentasi_Kelompok.pptx",  "presentasi"),
                ("Hasil_Tuning_Parameter.pdf","laporan"),
                ("Screenshot_Simulasi.png",   "gambar"),
            };

            foreach (var sid in students)
            {
                int actCount = rnd.Next(5, 13);
                for (int i = 0; i < actCount; i++)
                {
                    var type = actTypes[rnd.Next(actTypes.Length)];
                    var desc = descriptions[type][rnd.Next(descriptions[type].Length)];
                    var act  = new GroupActivity
                    {
                        StudentId           = sid,
                        StudentName         = GetDemoStudentName(sid),
                        AssignmentId        = assignId,
                        GroupId             = "GRP-01",
                        ActivityType        = type,
                        Description         = desc,
                        ActivityTime        = DateTime.Now.AddHours(-rnd.Next(1, 80)),
                        AutoScore           = actAutoScore[type],
                        CountsToSystemScore = true,
                    };
                    // Untuk Upload: tambahkan nama file demo
                    if (type == "Upload")
                    {
                        var (fn, _) = demoFiles[rnd.Next(demoFiles.Length)];
                        act.FileName = fn;
                        act.FilePath = $"C:\\Demo\\{sid}\\{fn}";   // path demo (tidak nyata)
                        act.Description = $"Upload: {fn}";
                    }
                    _groupActivities.Add(act);
                }
            }

            // ── System Evaluations — dihitung dari aktivitas nyata (bukan random) ──
            foreach (var sid in students)
                _systemEvaluations.Add(ComputeSystemScore(sid, assignId));

            // ── Tuning Records ───────────────────────────────────────────────
            // Setiap mahasiswa mencoba beberapa konfigurasi Kp/Ki/Kd
            var tuningAttempts = new[]
            {
                // (Kp, Ki, Kd, RiseTime, Overshoot, Settling, SSE, QualityScore)
                (1.2, 0.05, 0.01, 3.2, 18.5, 12.4, 2.1, 72.0),
                (1.8, 0.08, 0.02, 2.8, 12.0,  9.6, 1.5, 84.0),
                (2.5, 0.10, 0.05, 2.1,  8.0,  7.2, 0.8, 91.0),
                (0.8, 0.03, 0.00, 4.8, 25.0, 18.0, 4.0, 52.0),
                (1.5, 0.06, 0.03, 3.0, 15.0, 11.0, 1.8, 77.0),
                (3.0, 0.12, 0.08, 1.8,  6.5,  6.0, 0.5, 95.0),
            };
            var plantTypes = new[] { "Flow", "Level", "Temperature" };

            foreach (var sid in students)
            {
                int tuningCount = rnd.Next(2, 6);
                var picked = tuningAttempts.OrderBy(_ => rnd.Next()).Take(tuningCount);
                foreach (var (kp, ki, kd, rt, os, st, sse, qs) in picked)
                {
                    _tuningRecords.Add(new TuningRecord
                    {
                        StudentId    = sid,
                        AssignmentId = assignId,
                        PlantType    = plantTypes[rnd.Next(plantTypes.Length)],
                        Kp           = kp + rnd.NextDouble() * 0.2 - 0.1,
                        Ki           = ki + rnd.NextDouble() * 0.01 - 0.005,
                        Kd           = kd + rnd.NextDouble() * 0.01 - 0.005,
                        RiseTime         = rt   + rnd.NextDouble() * 0.4 - 0.2,
                        Overshoot        = os   + rnd.NextDouble() * 3   - 1.5,
                        SettlingTime     = st   + rnd.NextDouble() * 1   - 0.5,
                        SteadyStateError = sse  + rnd.NextDouble() * 0.4 - 0.2,
                        QualityScore     = Math.Clamp(qs + rnd.NextDouble() * 8 - 4, 0, 100),
                        RecordedAt   = DateTime.Now.AddHours(-rnd.Next(2, 70)),
                    });
                }
            }

            // ── AI Usage Records ─────────────────────────────────────────────
            var aiTopics = new[]
            {
                "Cara tuning PID untuk sistem Flow Control",
                "Penjelasan rise time dan overshoot",
                "Perbedaan kontroler P, PI, PID",
                "Cara menganalisis respons step",
                "Metode Ziegler-Nichols untuk tuning PID",
                "Kenapa steady-state error tidak nol pada kontroler P?",
                "Cara membaca grafik respons transien",
                "Apa itu stability margin?",
            };
            var aiProviders = new[] { "DeepSeek", "Claude", "GPT-4o" };

            foreach (var sid in students)
            {
                int sessionCount = rnd.Next(1, 6);
                for (int i = 0; i < sessionCount; i++)
                {
                    bool productive = rnd.NextDouble() > 0.25;
                    _aiUsageRecords.Add(new AiUsageRecord
                    {
                        StudentId    = sid,
                        AssignmentId = assignId,
                        Topic        = aiTopics[rnd.Next(aiTopics.Length)],
                        MessageCount = rnd.Next(2, 12),
                        IsProductive = productive,
                        AiProvider   = aiProviders[rnd.Next(aiProviders.Length)],
                        SessionAt    = DateTime.Now.AddHours(-rnd.Next(1, 96)),
                    });
                }
            }

            // ── Simulation Results ───────────────────────────────────────────
            var sessions = new[]
            {
                ("PID Control — Sesi 1",      6, 8, 0.85, -48),
                ("Heat Exchanger Simulation",  4, 6, 0.72, -72),
                ("PID Control — Sesi 2",      7, 8, 0.91, -24),
                ("Level Control",             5, 6, 0.78, -96),
                ("Pressure Control",          3, 6, 0.60, -120),
            };

            foreach (var sid in students)
            {
                int sessCount = rnd.Next(1, 4);
                var picked = sessions.OrderBy(_ => rnd.Next()).Take(sessCount);
                foreach (var (name, hit, total, stab, hoursAgo) in picked)
                {
                    var start = DateTime.Now.AddHours(hoursAgo + rnd.Next(-4, 4));
                    int durMin = rnd.Next(18, 55);
                    double score = 55 + rnd.NextDouble() * 40;
                    _simulationResults.Add(new SimulationResult
                    {
                        StudentId      = sid,
                        StudentName    = GetDemoStudentName(sid),
                        SessionName    = name,
                        AssignmentId   = assignId,
                        StartedAt      = start,
                        FinishedAt     = start.AddMinutes(durMin),
                        Score          = score,
                        ParametersHit  = hit,
                        ParametersTotal = total,
                        StabilityIndex = stab + rnd.NextDouble() * 0.08 - 0.04,
                    });
                }
            }

            // ── Peer Evaluations (sebagian sudah ada) ────────────────────────
            // STU001 menilai STU002, STU003
            _peerEvaluations.Add(new PeerEvaluation
            {
                EvaluatorId = "STU001", EvaluatorName = GetDemoStudentName("STU001"),
                EvaluateeId = "STU002", EvaluateeName = GetDemoStudentName("STU002"),
                AssignmentId = assignId, AssignmentTitle = "Tugas Kelompok - Sistem Kontrol PID",
                GroupId = "GRP-01",
                CriteriaContribution = 80, CriteriaCooperation = 85,
                CriteriaResponsibility = 75, CriteriaCreativity = 78,
                Comment = "Aktif berkontribusi, komunikasi baik.",
                EvaluatedAt = DateTime.Now.AddHours(-5),
            });
            _peerEvaluations.Add(new PeerEvaluation
            {
                EvaluatorId = "STU001", EvaluatorName = GetDemoStudentName("STU001"),
                EvaluateeId = "STU003", EvaluateeName = GetDemoStudentName("STU003"),
                AssignmentId = assignId, AssignmentTitle = "Tugas Kelompok - Sistem Kontrol PID",
                GroupId = "GRP-01",
                CriteriaContribution = 70, CriteriaCooperation = 65,
                CriteriaResponsibility = 72, CriteriaCreativity = 68,
                Comment = "Perlu lebih aktif dalam diskusi.",
                EvaluatedAt = DateTime.Now.AddHours(-4),
            });

            // STU002 menilai STU001
            _peerEvaluations.Add(new PeerEvaluation
            {
                EvaluatorId = "STU002", EvaluatorName = GetDemoStudentName("STU002"),
                EvaluateeId = "STU001", EvaluateeName = GetDemoStudentName("STU001"),
                AssignmentId = assignId, AssignmentTitle = "Tugas Kelompok - Sistem Kontrol PID",
                GroupId = "GRP-01",
                CriteriaContribution = 90, CriteriaCooperation = 88,
                CriteriaResponsibility = 92, CriteriaCreativity = 85,
                Comment = "Pemimpin kelompok yang baik, sangat aktif.",
                EvaluatedAt = DateTime.Now.AddHours(-3),
            });
        }
    }
}
