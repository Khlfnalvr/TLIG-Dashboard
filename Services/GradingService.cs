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
    /// Service penilaian mahasiswa. Data diisi dari aktivitas nyata (tuning,
    /// AI, simulasi, peer review) — tidak ada lagi data dummy; daftar mahasiswa
    /// berasal dari akun asli via <see cref="StudentDirectory"/>.
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

        private GradingService() { }

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
        //  NAME RESOLUTION
        // ─────────────────────────────────────────────────────────────────────

        private static string GetDemoStudentName(string id) =>
            StudentDirectory.GetName(id);
    }
}
