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
        private readonly List<PeerEvaluation> _peerEvaluations = new();
        private readonly List<SystemEvaluation> _systemEvaluations = new();
        private readonly List<LecturerGrade> _lecturerGrades = new();
        private readonly List<GroupActivity> _groupActivities = new();

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

        /// <summary>Hitung skor sistem berdasarkan aktivitas kelompok</summary>
        public Task<SystemEvaluation> GenerateSystemScoreAsync(string studentId, string assignmentId)
        {
            var activities = _groupActivities
                .Where(a => a.StudentId == studentId && a.AssignmentId == assignmentId && a.CountsToSystemScore)
                .ToList();

            var student = GetDemoStudentName(studentId);

            // Algoritma skor sistem sederhana
            double score = 0;
            int commits = activities.Count(a => a.ActivityType == "Submit");
            int edits = activities.Count(a => a.ActivityType == "Edit");
            int uploads = activities.Count(a => a.ActivityType == "Upload");
            bool onTime = activities.Any(a => a.ActivityType == "Submit");

            score += Math.Min(commits * 10, 30);  // Maks 30 poin dari submit
            score += Math.Min(edits * 5, 25);     // Maks 25 poin dari edit
            score += Math.Min(uploads * 8, 20);   // Maks 20 poin dari upload
            score += onTime ? 15 : 0;             // 15 poin tepat waktu
            score += Math.Min(activities.Count * 2, 10); // Maks 10 poin aktivitas umum

            var eval = new SystemEvaluation
            {
                StudentId = studentId,
                StudentName = student,
                AssignmentId = assignmentId,
                Score = Math.Min(score, 100),
                CommitsCount = commits,
                FilesModified = edits + uploads,
                SubmittedOnTime = onTime,
                TasksCompleted = activities.Count,
                TasksTotal = activities.Count + 2
            };

            // Simpan/update
            _systemEvaluations.RemoveAll(e => e.StudentId == studentId && e.AssignmentId == assignmentId);
            _systemEvaluations.Add(eval);

            RecalculateSummaryScore(studentId, assignmentId);
            return Task.FromResult(eval);
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

        public Task AddGroupActivityAsync(GroupActivity activity)
        {
            _groupActivities.Add(activity);
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
            var actTypes = new[] { "Submit", "Edit", "Upload", "Comment", "Review" };
            var descriptions = new Dictionary<string, string[]>
            {
                ["Submit"] = new[] { "Mensubmit draft laporan", "Push kode implementasi", "Submit hasil akhir" },
                ["Edit"] = new[] { "Mengedit bagian pendahuluan", "Update diagram blok", "Revisi bab 3" },
                ["Upload"] = new[] { "Upload video demo", "Upload data percobaan", "Upload foto komponen" },
                ["Comment"] = new[] { "Memberi komentar review", "Feedback diagram sistem", "Review kode teman" },
                ["Review"] = new[] { "Review laporan kelompok", "Cek hasil simulasi", "Verifikasi data" },
            };

            foreach (var sid in students)
            {
                int actCount = rnd.Next(4, 12);
                for (int i = 0; i < actCount; i++)
                {
                    var type = actTypes[rnd.Next(actTypes.Length)];
                    var desc = descriptions[type][rnd.Next(descriptions[type].Length)];
                    _groupActivities.Add(new GroupActivity
                    {
                        StudentId = sid,
                        StudentName = GetDemoStudentName(sid),
                        AssignmentId = assignId,
                        GroupId = "GRP-01",
                        ActivityType = type,
                        Description = desc,
                        ActivityTime = DateTime.Now.AddHours(-rnd.Next(1, 72)),
                        CountsToSystemScore = true,
                    });
                }
            }

            // ── System Evaluations ───────────────────────────────────────────
            foreach (var sid in students)
            {
                var acts = _groupActivities.Where(a => a.StudentId == sid).ToList();
                _systemEvaluations.Add(new SystemEvaluation
                {
                    StudentId = sid,
                    StudentName = GetDemoStudentName(sid),
                    AssignmentId = assignId,
                    AssignmentTitle = "Tugas Kelompok - Sistem Kontrol PID",
                    GroupId = "GRP-01",
                    Score = 55 + rnd.NextDouble() * 40,
                    CommitsCount = acts.Count(a => a.ActivityType == "Submit"),
                    FilesModified = acts.Count(a => a.ActivityType is "Edit" or "Upload"),
                    SubmittedOnTime = rnd.NextDouble() > 0.2,
                    TasksCompleted = acts.Count,
                    TasksTotal = acts.Count + rnd.Next(0, 3),
                    GeneratedAt = DateTime.Now.AddHours(-2)
                });
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
