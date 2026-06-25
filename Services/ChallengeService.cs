using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services
{
    /// <summary>
    /// Service untuk pengelolaan Challenge Learning (in-memory demo).
    /// Ganti penyimpanan dengan database (SQLite/SQL Server) sesuai kebutuhan.
    /// </summary>
    public class ChallengeService
    {
        public static ChallengeService Instance { get; } = new();

        private readonly List<Challenge> _challenges = new();
        private readonly HttpClient _httpClient;

        // Ganti dengan API key yang valid atau ambil dari konfigurasi/environment
        private const string AnthropicApiKey = "YOUR_ANTHROPIC_API_KEY";
        private const string AnthropicModel = "claude-sonnet-4-20250514";
        private const string AnthropicApiUrl = "https://api.anthropic.com/v1/messages";

        public ChallengeService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", AnthropicApiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            SeedDemoData();
        }

        // ── CRUD Challenge ──────────────────────────────────────────────────

        public IReadOnlyList<Challenge> GetAllChallenges() => _challenges.AsReadOnly();

        public Challenge? GetById(Guid id) =>
            _challenges.FirstOrDefault(c => c.Id == id);

        public void AddChallenge(Challenge challenge)
        {
            if (!challenge.IsWeightValid)
                throw new InvalidOperationException("Total bobot penilaian harus = 100.");
            _challenges.Add(challenge);
        }

        public void UpdateChallenge(Challenge updated)
        {
            if (!updated.IsWeightValid)
                throw new InvalidOperationException("Total bobot penilaian harus = 100.");
            var idx = _challenges.FindIndex(c => c.Id == updated.Id);
            if (idx >= 0) _challenges[idx] = updated;
        }

        public void DeleteChallenge(Guid id) =>
            _challenges.RemoveAll(c => c.Id == id);

        // ── Submission ──────────────────────────────────────────────────────

        public void AddSubmission(Guid challengeId, ChallengeSubmission submission)
        {
            var challenge = GetById(challengeId)
                ?? throw new KeyNotFoundException("Challenge tidak ditemukan.");
            submission.ChallengeId = challengeId;
            challenge.Submissions.Add(submission);
        }

        public List<ChallengeSubmission> GetSubmissions(Guid challengeId) =>
            GetById(challengeId)?.Submissions ?? new();

        public ChallengeSubmission? GetSubmission(Guid challengeId, Guid submissionId) =>
            GetById(challengeId)?.Submissions.FirstOrDefault(s => s.Id == submissionId);

        // ── Penilaian Dosen ────────────────────────────────────────────────

        public void GradeByDosen(Guid challengeId, Guid submissionId,
                                  double score, string feedback, string dosenName)
        {
            var sub = GetSubmission(challengeId, submissionId)
                ?? throw new KeyNotFoundException("Submission tidak ditemukan.");

            sub.DosenGrade = new GradeEntry
            {
                GraderName = dosenName,
                Score = Math.Clamp(score, 0, 100),
                Feedback = feedback,
                IsAI = false
            };
            UpdateSubmissionStatus(sub);
        }

        // ── Peer Review ────────────────────────────────────────────────────

        public void GradeByPeer(Guid challengeId, Guid submissionId,
                                 double score, string feedback, string peerName)
        {
            var sub = GetSubmission(challengeId, submissionId)
                ?? throw new KeyNotFoundException("Submission tidak ditemukan.");

            // Satu peer hanya bisa menilai sekali
            if (sub.PeerGrades.Any(p => p.GraderName == peerName))
                throw new InvalidOperationException($"{peerName} sudah memberikan penilaian.");

            sub.PeerGrades.Add(new GradeEntry
            {
                GraderName = peerName,
                Score = Math.Clamp(score, 0, 100),
                Feedback = feedback,
                IsAI = false
            });
            UpdateSubmissionStatus(sub);
        }

        // ── AI Grading (Anthropic Claude) ──────────────────────────────────

        public async Task<GradeEntry> GradeByAIAsync(Guid challengeId, Guid submissionId)
        {
            var challenge = GetById(challengeId)
                ?? throw new KeyNotFoundException("Challenge tidak ditemukan.");
            var sub = GetSubmission(challengeId, submissionId)
                ?? throw new KeyNotFoundException("Submission tidak ditemukan.");

            string prompt = BuildAIGradingPrompt(challenge, sub);

            var requestBody = new
            {
                model = AnthropicModel,
                max_tokens = 1024,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var response = await _httpClient.PostAsJsonAsync(AnthropicApiUrl, requestBody);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<AnthropicResponse>();
            string rawText = result?.Content?.FirstOrDefault()?.Text ?? "";

            var aiGrade = ParseAIResponse(rawText);
            sub.AIGrade = aiGrade;
            UpdateSubmissionStatus(sub);

            return aiGrade;
        }

        // ── Helpers ────────────────────────────────────────────────────────

        private static string BuildAIGradingPrompt(Challenge challenge, ChallengeSubmission sub)
        {
            return $"""
                Kamu adalah asisten penilai akademik. Nilailah submission mahasiswa berikut secara objektif.

                === CHALLENGE ===
                Judul       : {challenge.Title}
                Deskripsi   : {challenge.Description}
                Instruksi   : {challenge.Instructions}

                === JAWABAN MAHASISWA ({sub.StudentName}) ===
                {sub.TextAnswer}
                {(sub.AttachmentFileName != null ? "[File terlampir: " + sub.AttachmentFileName + "]" : "")}

                === INSTRUKSI PENILAIAN ===
                Berikan penilaian dalam format JSON berikut (tanpa markdown):
                {"{"}
                  "score": <angka 0-100>,
                  "feedback": "<umpan balik konstruktif dalam Bahasa Indonesia, min. 3 kalimat>"
                {"}"}

                Kriteria: relevansi jawaban, kelengkapan, kedalaman analisis, kerapian penyampaian.
                """;
        }

        private static GradeEntry ParseAIResponse(string raw)
        {
            try
            {
                // Coba parse JSON dari response
                var cleaned = raw.Trim().TrimStart('`').TrimEnd('`');
                if (cleaned.StartsWith("json")) cleaned = cleaned[4..].Trim();

                var doc = JsonDocument.Parse(cleaned);
                double score = doc.RootElement.GetProperty("score").GetDouble();
                string feedback = doc.RootElement.GetProperty("feedback").GetString() ?? "";

                return new GradeEntry
                {
                    GraderName = "AI (Claude)",
                    Score = Math.Clamp(score, 0, 100),
                    Feedback = feedback,
                    IsAI = true
                };
            }
            catch
            {
                // Fallback jika parse gagal
                return new GradeEntry
                {
                    GraderName = "AI (Claude)",
                    Score = 0,
                    Feedback = $"[Gagal parse respons AI] {raw}",
                    IsAI = true
                };
            }
        }

        private static void UpdateSubmissionStatus(ChallengeSubmission sub)
        {
            bool hasAny = sub.DosenGrade != null || sub.AIGrade != null || sub.PeerGrades.Count > 0;
            sub.Status = hasAny ? SubmissionStatus.Graded : SubmissionStatus.UnderReview;
        }

        // ── Demo Data ──────────────────────────────────────────────────────

        private void SeedDemoData()
        {
            // ── Challenge 1: Flow PID Tuning ─────────────────────────────────
            var c1 = new Challenge
            {
                Title         = "Tune PID for Fast Rise Time",
                Description   = "Atur parameter PID pada sistem Flow agar step response memenuhi spesifikasi rise time yang cepat.",
                Instructions  = "1. Buka Dashboard → pilih sistem Flow.\n2. Atur Kp, Ki, Kd di panel PID Parameters.\n3. Klik RUN dan amati step response.\n4. Pastikan Rise Time dan Overshoot memenuhi target.\n5. Submit screenshot dan nilai parameter yang digunakan.",
                TargetSystem  = SimulationType.Flow,
                Deadline      = DateTime.Now.AddDays(7),
                Status        = ChallengeStatus.Active,
                WeightDosen   = 50,
                WeightAI      = 30,
                WeightPeer    = 20,
                CreatedByName = "Dr. Budi Santoso",
                Tasks = new()
                {
                    new ChallengeTask
                    {
                        Name        = "Fast Rise Time",
                        Description = "Atur Kp, Ki, Kd agar rise time singkat",
                        Metric      = TaskMetrics.RiseTime,
                        Op          = TaskOps.Lte,
                        TargetValue = 2.0,
                        Tolerance   = 0.2
                    },
                    new ChallengeTask
                    {
                        Name        = "Controlled Overshoot",
                        Description = "Pertahankan overshoot agar tidak terlalu besar",
                        Metric      = TaskMetrics.Overshoot,
                        Op          = TaskOps.Lte,
                        TargetValue = 10.0,
                        Tolerance   = 2.0
                    }
                }
            };

            var sub1 = new ChallengeSubmission
            {
                ChallengeId = c1.Id, StudentId = "S001", StudentName = "Andi Pratama",
                TextAnswer  = "Kp=8, Ki=0.5, Kd=2 menghasilkan rise time 1.8s dan overshoot 8%.",
                Status      = SubmissionStatus.Submitted,
                MetricSnapshot = new() { [TaskMetrics.RiseTime] = 1.8, [TaskMetrics.Overshoot] = 8.2 }
            };
            var sub2 = new ChallengeSubmission
            {
                ChallengeId = c1.Id, StudentId = "S002", StudentName = "Siti Rahma",
                TextAnswer  = "Dengan Kp=12, Ki=1, Kd=0.5 didapat rise time 1.5s namun overshoot 15%.",
                Status      = SubmissionStatus.Submitted,
                MetricSnapshot = new() { [TaskMetrics.RiseTime] = 1.5, [TaskMetrics.Overshoot] = 15.1 }
            };
            c1.Submissions.Add(sub1);
            c1.Submissions.Add(sub2);
            _challenges.Add(c1);

            // ── Challenge 2: Level Control ───────────────────────────────────
            var c2 = new Challenge
            {
                Title         = "Minimize Settling Time – Level",
                Description   = "Optimalkan PID untuk sistem Level Tank agar settling time minimal dan steady-state error kecil.",
                Instructions  = "1. Pilih sistem Level di SYSTEM MODEL.\n2. Set setpoint ke 50 cm.\n3. Tuning PID untuk mencapai target settling dan steady-state error.\n4. Submit parameter PID dan nilai metrik yang dicapai.",
                TargetSystem  = SimulationType.Level,
                Deadline      = DateTime.Now.AddDays(10),
                Status        = ChallengeStatus.Active,
                WeightDosen   = 40,
                WeightAI      = 40,
                WeightPeer    = 20,
                CreatedByName = "Dr. Budi Santoso",
                Tasks = new()
                {
                    new ChallengeTask
                    {
                        Name        = "Quick Settling",
                        Description = "Settling time harus di bawah 15 detik",
                        Metric      = TaskMetrics.Settling,
                        Op          = TaskOps.Lte,
                        TargetValue = 15.0,
                        Tolerance   = 1.0
                    },
                    new ChallengeTask
                    {
                        Name        = "Low Steady-State Error",
                        Description = "Steady-state error maksimal 2%",
                        Metric      = TaskMetrics.SteadyStateError,
                        Op          = TaskOps.Lte,
                        TargetValue = 2.0,
                        Tolerance   = 0.5
                    }
                }
            };
            _challenges.Add(c2);

            // ── Challenge 3: Draft ───────────────────────────────────────────
            _challenges.Add(new Challenge
            {
                Title         = "Temperature Control Design",
                Description   = "Desain kontroler PID untuk heat exchanger dengan respons stabil.",
                Instructions  = "Draft — instruksi belum lengkap.",
                TargetSystem  = SimulationType.Temperature,
                Deadline      = DateTime.Now.AddDays(21),
                Status        = ChallengeStatus.Draft,
                WeightDosen   = 50, WeightAI = 30, WeightPeer = 20,
                CreatedByName = "Dr. Budi Santoso",
                Tasks = new()
                {
                    new ChallengeTask
                    {
                        Name        = "Stable Overshoot",
                        Description = "Overshoot di bawah 5% agar sistem aman",
                        Metric      = TaskMetrics.Overshoot,
                        Op          = TaskOps.Lte,
                        TargetValue = 5.0,
                        Tolerance   = 1.0
                    }
                }
            });
        }
    }

    // ── DTO untuk parse response Anthropic ────────────────────────────────

    internal class AnthropicResponse
    {
        [JsonPropertyName("content")]
        public List<AnthropicContent>? Content { get; set; }
    }

    internal class AnthropicContent
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }
    }
}
