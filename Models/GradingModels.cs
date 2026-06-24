// Models/GradingModels.cs
// Tambahkan file ini ke folder Models/ yang sudah ada di project TLIG-Dashboard

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace TLIGDashboard.Models
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Enum
    // ─────────────────────────────────────────────────────────────────────────

    public enum EvaluatorType
    {
        Peer,       // Mahasiswa menilai mahasiswa
        System,     // Sistem menilai mahasiswa (otomatis dari aktivitas)
        Lecturer    // Dosen menilai mahasiswa
    }

    public enum AssignmentStatus
    {
        NotStarted,
        InProgress,
        Submitted,
        Graded
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PeerEvaluation — penilaian mahasiswa ke mahasiswa
    // ─────────────────────────────────────────────────────────────────────────

    public class PeerEvaluation : INotifyPropertyChanged
    {
        private double _score;
        private string _comment = string.Empty;

        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>NIM mahasiswa penilai</summary>
        public string EvaluatorId { get; set; } = string.Empty;
        public string EvaluatorName { get; set; } = string.Empty;

        /// <summary>NIM mahasiswa yang dinilai</summary>
        public string EvaluateeId { get; set; } = string.Empty;
        public string EvaluateeName { get; set; } = string.Empty;

        public string AssignmentId { get; set; } = string.Empty;
        public string AssignmentTitle { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;

        public double Score
        {
            get => _score;
            set { _score = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
        }

        public string Comment
        {
            get => _comment;
            set { _comment = value; OnPropertyChanged(); }
        }

        public DateTime EvaluatedAt { get; set; } = DateTime.Now;

        // Kriteria penilaian peer
        public double CriteriaContribution { get; set; }   // Kontribusi kerja (0-100)
        public double CriteriaCooperation { get; set; }    // Kerjasama (0-100)
        public double CriteriaResponsibility { get; set; } // Tanggung jawab (0-100)
        public double CriteriaCreativity { get; set; }     // Kreativitas (0-100)

        /// <summary>Rata-rata dari keempat kriteria</summary>
        public double AverageScore =>
            (CriteriaContribution + CriteriaCooperation + CriteriaResponsibility + CriteriaCreativity) / 4.0;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SystemEvaluation — penilaian otomatis oleh sistem
    // ─────────────────────────────────────────────────────────────────────────

    public class SystemEvaluation : INotifyPropertyChanged
    {
        private double _score;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string StudentId { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string AssignmentId { get; set; } = string.Empty;
        public string AssignmentTitle { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;

        public double Score
        {
            get => _score;
            set { _score = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
        }

        // Komponen skor sistem
        public int CommitsCount { get; set; }           // Jumlah commit/submit
        public int FilesModified { get; set; }          // File yang diubah
        public TimeSpan TotalActiveTime { get; set; }   // Total waktu aktif
        public bool SubmittedOnTime { get; set; }       // Tepat waktu
        public int TasksCompleted { get; set; }         // Task diselesaikan
        public int TasksTotal { get; set; }             // Total task

        public double CompletionRate => TasksTotal > 0 ? (double)TasksCompleted / TasksTotal * 100 : 0;

        public DateTime GeneratedAt { get; set; } = DateTime.Now;

        public string ScoreBadge => Score >= 85 ? "A" :
                                    Score >= 75 ? "B" :
                                    Score >= 65 ? "C" :
                                    Score >= 55 ? "D" : "E";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LecturerGrade — penilaian dosen ke mahasiswa
    // ─────────────────────────────────────────────────────────────────────────

    public class LecturerGrade : INotifyPropertyChanged
    {
        private double _score;
        private string _feedback = string.Empty;
        private bool _isFinalized;

        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string LecturerId { get; set; } = string.Empty;
        public string LecturerName { get; set; } = string.Empty;
        public string StudentId { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string AssignmentId { get; set; } = string.Empty;
        public string AssignmentTitle { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;

        public double Score
        {
            get => _score;
            set { _score = Math.Clamp(value, 0, 100); OnPropertyChanged(); OnPropertyChanged(nameof(LetterGrade)); }
        }

        public string Feedback
        {
            get => _feedback;
            set { _feedback = value; OnPropertyChanged(); }
        }

        public bool IsFinalized
        {
            get => _isFinalized;
            set { _isFinalized = value; OnPropertyChanged(); }
        }

        // Komponen nilai dosen
        public double ScorePresentation { get; set; }  // Nilai presentasi
        public double ScoreReport { get; set; }        // Nilai laporan
        public double ScoreImplementation { get; set; }// Nilai implementasi
        public double ScoreDefense { get; set; }       // Nilai saat ujian/defence

        public DateTime GradedAt { get; set; } = DateTime.Now;

        public string LetterGrade => Score >= 85 ? "A" :
                                     Score >= 80 ? "A-" :
                                     Score >= 75 ? "B+" :
                                     Score >= 70 ? "B" :
                                     Score >= 65 ? "B-" :
                                     Score >= 60 ? "C+" :
                                     Score >= 55 ? "C" :
                                     Score >= 50 ? "D" : "E";

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  StudentGradeSummary — ringkasan nilai gabungan satu mahasiswa
    // ─────────────────────────────────────────────────────────────────────────

    public class StudentGradeSummary : INotifyPropertyChanged
    {
        public string StudentId { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string AssignmentId { get; set; } = string.Empty;
        public string AssignmentTitle { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;
        public string GroupName { get; set; } = string.Empty;

        // Bobot nilai (bisa dikonfigurasi dosen)
        public double WeightPeer { get; set; } = 0.20;       // 20% peer
        public double WeightSystem { get; set; } = 0.30;     // 30% sistem
        public double WeightLecturer { get; set; } = 0.50;   // 50% dosen

        public double? PeerScore { get; set; }
        public double? SystemScore { get; set; }
        public double? LecturerScore { get; set; }

        public double? FinalScore
        {
            get
            {
                if (LecturerScore == null) return null;
                double peer = PeerScore ?? 0;
                double sys = SystemScore ?? 0;
                double lec = LecturerScore ?? 0;

                // Jika ada komponen yang kosong, sesuaikan bobot
                double totalWeight = (PeerScore.HasValue ? WeightPeer : 0)
                                   + (SystemScore.HasValue ? WeightSystem : 0)
                                   + WeightLecturer;

                return (peer * (PeerScore.HasValue ? WeightPeer : 0)
                      + sys * (SystemScore.HasValue ? WeightSystem : 0)
                      + lec * WeightLecturer) / totalWeight;
            }
        }

        public string LetterGrade
        {
            get
            {
                var s = FinalScore;
                if (s == null) return "-";
                return s >= 85 ? "A" : s >= 80 ? "A-" : s >= 75 ? "B+" :
                       s >= 70 ? "B" : s >= 65 ? "B-" : s >= 60 ? "C+" :
                       s >= 55 ? "C" : s >= 50 ? "D" : "E";
            }
        }

        public bool IsComplete => PeerScore.HasValue && SystemScore.HasValue && LecturerScore.HasValue;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  SimulationResult — hasil sesi simulasi plant per mahasiswa
    // ─────────────────────────────────────────────────────────────────────────

    public class SimulationResult
    {
        public string   Id             { get; set; } = Guid.NewGuid().ToString();
        public string   StudentId      { get; set; } = "";
        public string   StudentName    { get; set; } = "";
        public string   SessionName    { get; set; } = "";
        public string   AssignmentId   { get; set; } = "";
        public DateTime StartedAt      { get; set; } = DateTime.Now;
        public DateTime FinishedAt     { get; set; } = DateTime.Now;
        public double   Score          { get; set; }            // 0–100
        public int      ParametersHit  { get; set; }
        public int      ParametersTotal { get; set; }
        public double   StabilityIndex { get; set; }            // 0–1
        public string   Notes          { get; set; } = "";

        public TimeSpan Duration      => FinishedAt > StartedAt ? FinishedAt - StartedAt : TimeSpan.Zero;
        public string   DurationStr   => $"{(int)Duration.TotalMinutes}m {Duration.Seconds}s";
        public string   ScoreStr      => Score.ToString("F1");
        public string   ParameterStr  => $"{ParametersHit}/{ParametersTotal}";
        public string   StartedAtStr  => StartedAt.ToString("dd MMM yyyy");
        public string   StabilityStr  => $"{StabilityIndex * 100:F0}%";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  TuningRecord — rekam jejak tuning parameter PID mahasiswa
    // ─────────────────────────────────────────────────────────────────────────

    public class TuningRecord
    {
        public string   Id           { get; set; } = Guid.NewGuid().ToString();
        public string   StudentId    { get; set; } = "";
        public string   AssignmentId { get; set; } = "";
        public string   PlantType    { get; set; } = "Flow";  // Flow / Level / Temperature

        // Parameter PID yang dicoba
        public double Kp { get; set; }
        public double Ki { get; set; }
        public double Kd { get; set; }

        // Metrik performa yang dicapai
        public double? RiseTime         { get; set; }  // detik
        public double? Overshoot        { get; set; }  // %
        public double? SettlingTime     { get; set; }  // detik
        public double? SteadyStateError { get; set; }  // %

        /// <summary>Skor kualitas tuning 0–100. Semakin mendekati parameter ideal, semakin tinggi.</summary>
        public double QualityScore { get; set; }

        public DateTime RecordedAt { get; set; } = DateTime.Now;

        public string KpKiKdStr  => $"Kp={Kp:F2} Ki={Ki:F3} Kd={Kd:F3}";
        public string PlantLabel => PlantType switch
        {
            "Level"       => "Level Control",
            "Temperature" => "Temperature Control",
            _             => "Flow Control",
        };
        public string QualityStr => QualityScore >= 85 ? "Sangat Baik"
                                  : QualityScore >= 70 ? "Baik"
                                  : QualityScore >= 55 ? "Cukup" : "Perlu Perbaikan";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  AiUsageRecord — rekam penggunaan AI oleh mahasiswa
    // ─────────────────────────────────────────────────────────────────────────

    public class AiUsageRecord
    {
        public string   Id           { get; set; } = Guid.NewGuid().ToString();
        public string   StudentId    { get; set; } = "";
        public string   AssignmentId { get; set; } = "";
        public string   Topic        { get; set; } = "";    // topik pertanyaan/sesi
        public int      MessageCount { get; set; }          // jumlah pesan dalam sesi
        public bool     IsProductive { get; set; }          // apakah sesi menghasilkan output bermakna
        public string   AiProvider   { get; set; } = "";    // DeepSeek / Claude / dsb
        public DateTime SessionAt    { get; set; } = DateTime.Now;

        public string ProductivityStr => IsProductive ? "Produktif" : "Eksplorasi";
        public string SessionAtStr    => SessionAt.ToString("dd MMM HH:mm");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  GroupActivity — aktivitas per mahasiswa dalam tugas kelompok
    // ─────────────────────────────────────────────────────────────────────────

    public class GroupActivity : INotifyPropertyChanged
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string StudentId { get; set; } = string.Empty;
        public string StudentName { get; set; } = string.Empty;
        public string AssignmentId { get; set; } = string.Empty;
        public string GroupId { get; set; } = string.Empty;

        public string ActivityType { get; set; } = string.Empty;  // "Submit", "Edit", "Comment", "Upload", dsb.
        public string Description { get; set; } = string.Empty;
        public DateTime ActivityTime { get; set; } = DateTime.Now;
        public double? AutoScore { get; set; }  // Skor yang dihasilkan dari aktivitas ini
        public bool CountsToSystemScore { get; set; } = true;

        public string TimeAgo
        {
            get
            {
                var diff = DateTime.Now - ActivityTime;
                if (diff.TotalMinutes < 1) return "Baru saja";
                if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} menit lalu";
                if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} jam lalu";
                return $"{(int)diff.TotalDays} hari lalu";
            }
        }

        public string ActivityIcon => ActivityType switch
        {
            "Submit" => "\uE73E",       // Checkmark
            "Edit" => "\uE70F",         // Edit
            "Comment" => "\uE8F2",      // Comment
            "Upload" => "\uE898",       // Upload
            "Download" => "\uE896",     // Download
            "Review" => "\uE7C3",       // Review
            _ => "\uE7C3"
        };

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
