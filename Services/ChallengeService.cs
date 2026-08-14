using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services
{
    /// <summary>On-disk envelope for the challenge database (allows future migrations).</summary>
    public sealed class ChallengesFile
    {
        public int             Version    { get; set; } = 1;
        public List<Challenge> Challenges { get; set; } = new();
    }

    /// <summary>
    /// Service untuk pengelolaan Challenge Learning. Challenge + submission
    /// dipersist ke <c>%LOCALAPPDATA%\TLIGDashboard\challenges.json</c> sehingga
    /// data penilaian tidak hilang saat aplikasi ditutup.
    /// </summary>
    public class ChallengeService
    {
        public static ChallengeService Instance { get; } = new();

        private readonly object _lock = new();
        private readonly string _path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TLIGDashboard", "challenges.json");

        private List<Challenge> _challenges = new();
        private readonly HttpClient _httpClient;

        // Ganti dengan API key yang valid atau ambil dari konfigurasi/environment
        private const string AnthropicApiKey = "YOUR_ANTHROPIC_API_KEY";
        private const string AnthropicModel = "claude-sonnet-5";
        private const string AnthropicApiUrl = "https://api.anthropic.com/v1/messages";

        public ChallengeService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("x-api-key", AnthropicApiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            Load();
        }

        // ── Persistence ────────────────────────────────────────────────────

        private void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_path))
                    {
                        var file = JsonSerializer.Deserialize(
                            File.ReadAllText(_path), AppJsonContext.Default.ChallengesFile);
                        _challenges = file?.Challenges ?? new();
                        return;
                    }
                }
                catch { /* corrupt file → reseed below */ }

                // First run: seed sample challenges (no submissions) so the
                // system is usable out of the box.
                _challenges = SeedChallenges();
                SaveLocked();
            }
        }

        private void Save() { lock (_lock) SaveLocked(); }

        private void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, JsonSerializer.Serialize(
                    new ChallengesFile { Challenges = _challenges },
                    AppJsonContext.Default.ChallengesFile));
            }
            catch { /* best-effort */ }
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
            Save();
        }

        public void UpdateChallenge(Challenge updated)
        {
            if (!updated.IsWeightValid)
                throw new InvalidOperationException("Total bobot penilaian harus = 100.");
            var idx = _challenges.FindIndex(c => c.Id == updated.Id);
            if (idx >= 0) _challenges[idx] = updated;
            Save();
        }

        public void DeleteChallenge(Guid id)
        {
            _challenges.RemoveAll(c => c.Id == id);
            Save();
        }

        // ── Submission ──────────────────────────────────────────────────────

        public void AddSubmission(Guid challengeId, ChallengeSubmission submission)
        {
            var challenge = GetById(challengeId)
                ?? throw new KeyNotFoundException("Challenge tidak ditemukan.");
            submission.ChallengeId = challengeId;
            challenge.Submissions.Add(submission);
            Save();
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
            Save();
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
            Save();
        }

        // ── AI Grading (Anthropic Claude) ──────────────────────────────────

        public async Task<GradeEntry> GradeByAIAsync(Guid challengeId, Guid submissionId)
        {
            var challenge = GetById(challengeId)
                ?? throw new KeyNotFoundException("Challenge tidak ditemukan.");
            var sub = GetSubmission(challengeId, submissionId)
                ?? throw new KeyNotFoundException("Submission tidak ditemukan.");

            string prompt = BuildAIGradingPrompt(challenge, sub);

            // Body dibangun dengan JsonObject (bukan anonymous type) agar
            // trim-safe: reflection-based JSON dimatikan pada publish Release.
            var messages = new JsonArray();
            messages.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = prompt });
            var requestBody = new JsonObject
            {
                ["model"]      = AnthropicModel,
                ["max_tokens"] = 1024,
                ["messages"]   = messages,
            }.ToJsonString();

            using var content  = new StringContent(requestBody, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(AnthropicApiUrl, content);
            response.EnsureSuccessStatusCode();

            string raw = await response.Content.ReadAsStringAsync();
            string rawText = "";
            try
            {
                var node = JsonNode.Parse(raw);
                rawText  = (string?)node?["content"]?[0]?["text"] ?? "";
            }
            catch { }

            var aiGrade = ParseAIResponse(rawText);
            sub.AIGrade = aiGrade;
            UpdateSubmissionStatus(sub);
            Save();

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

        // ── Seed (first run only — no dummy submissions) ──────────────────

        // Fixed Guids so CLIENT and SERVER always have matching challenge IDs
        private static readonly Guid _c1Id = new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        private static readonly Guid _c2Id = new("b2c3d4e5-f6a7-8901-bcde-f12345678901");
        private static readonly Guid _c3Id = new("c3d4e5f6-a7b8-9012-cdef-123456789012");

        private static List<Challenge> SeedChallenges() => new()
        {
            new Challenge
            {
                Id            = _c1Id,
                Title         = "Tune PID for Fast Rise Time",
                Description   = "Atur parameter PID pada sistem Flow agar step response memenuhi spesifikasi rise time yang cepat.",
                Instructions  = "1. Buka Dashboard → pilih sistem Flow.\n2. Atur Kp, Ki, Kd di panel PID Parameters.\n3. Klik RUN dan amati step response.\n4. Pastikan Rise Time dan Overshoot memenuhi target.\n5. Submit screenshot dan nilai parameter yang digunakan.",
                TargetSystem  = SimulationType.Flow,
                Deadline      = DateTime.Now.AddDays(7),
                Status        = ChallengeStatus.Active,
                WeightDosen   = 50,
                WeightAI      = 30,
                WeightPeer    = 20,
                CreatedByName = "System",
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
            },
            new Challenge
            {
                Id            = _c2Id,
                Title         = "Minimize Settling Time – Level",
                Description   = "Optimalkan PID untuk sistem Level Tank agar settling time minimal dan steady-state error kecil.",
                Instructions  = "1. Pilih sistem Level di SYSTEM MODEL.\n2. Set setpoint ke 50 cm.\n3. Tuning PID untuk mencapai target settling dan steady-state error.\n4. Submit parameter PID dan nilai metrik yang dicapai.",
                TargetSystem  = SimulationType.Level,
                Deadline      = DateTime.Now.AddDays(10),
                Status        = ChallengeStatus.Active,
                WeightDosen   = 40,
                WeightAI      = 40,
                WeightPeer    = 20,
                CreatedByName = "System",
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
            },
            new Challenge
            {
                Id            = _c3Id,
                Title         = "Temperature Control Design",
                Description   = "Desain kontroler PID untuk heat exchanger dengan respons stabil.",
                Instructions  = "Draft — instruksi belum lengkap.",
                TargetSystem  = SimulationType.Temperature,
                Deadline      = DateTime.Now.AddDays(21),
                Status        = ChallengeStatus.Draft,
                WeightDosen   = 50, WeightAI = 30, WeightPeer = 20,
                CreatedByName = "System",
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
            }
        };
    }
}
