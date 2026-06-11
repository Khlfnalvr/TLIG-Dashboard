using System;
using System.Collections.Generic;
using TLIGDashboard.Services;

namespace TLIGDashboard.Models
{
    public enum ChallengeStatus   { Draft, Active, Closed }
    public enum SubmissionStatus  { NotSubmitted, Submitted, UnderReview, Graded }

    // ── Task inside a Challenge ──────────────────────────────────────────────

    /// <summary>
    /// One concrete task within a challenge — pairs a human-readable objective
    /// with an optional structured PID metric target (e.g. Rise Time &lt;= 2 ± 0.2 s).
    /// </summary>
    public class ChallengeTask
    {
        public Guid   Id          { get; set; } = Guid.NewGuid();
        public string Name        { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        // Structured metric target (matches TaskMetrics constants)
        public string Metric      { get; set; } = TaskMetrics.None;  // "" = no metric target
        public string Op          { get; set; } = TaskOps.Lte;       // <= / >= / ~
        public double TargetValue { get; set; }
        public double Tolerance   { get; set; }   // ±

        public bool HasMetricTarget =>
            TaskMetrics.IsValid(Metric) && Metric != TaskMetrics.None;

        /// <summary>e.g. "Rise Time &lt;= 2 ± 0.2 s"</summary>
        public string FormatTarget()
        {
            if (!HasMetricTarget) return string.Empty;
            string unit = Metric is TaskMetrics.RiseTime or TaskMetrics.Settling ? " s" : " %";
            string tol  = Tolerance > 0 ? $" ± {Tolerance:0.##}" : string.Empty;
            string label = Metric switch
            {
                TaskMetrics.RiseTime         => "Rise Time",
                TaskMetrics.Overshoot        => "Overshoot",
                TaskMetrics.Settling         => "Settling Time",
                TaskMetrics.SteadyStateError => "Steady-State Error",
                _                            => Metric
            };
            return $"{label} {Op} {TargetValue:0.##}{tol}{unit}";
        }

        /// <summary>Check if an actual value satisfies this task's target (within tolerance).</summary>
        public bool? IsAchieved(double? actual)
        {
            if (!HasMetricTarget || actual == null) return null;
            double v = actual.Value;
            return Op switch
            {
                TaskOps.Lte    => v <= TargetValue + Tolerance,
                TaskOps.Gte    => v >= TargetValue - Tolerance,
                TaskOps.Approx => Math.Abs(v - TargetValue) <= Tolerance,
                _              => null
            };
        }
    }

    // ── Challenge ────────────────────────────────────────────────────────────

    public class Challenge
    {
        public Guid            Id            { get; set; } = Guid.NewGuid();
        public string          Title         { get; set; } = string.Empty;
        public string          Description   { get; set; } = string.Empty;
        public string          Instructions  { get; set; } = string.Empty;
        public SimulationType  TargetSystem  { get; set; } = SimulationType.Flow;
        public DateTime        CreatedAt     { get; set; } = DateTime.Now;
        public DateTime?       Deadline      { get; set; }
        public ChallengeStatus Status        { get; set; } = ChallengeStatus.Draft;
        public string          CreatedByName { get; set; } = string.Empty;

        // Task list
        public List<ChallengeTask> Tasks { get; set; } = new();

        // Grading weights (must sum to 100)
        public int WeightDosen { get; set; } = 50;
        public int WeightAI    { get; set; } = 25;
        public int WeightPeer  { get; set; } = 25;

        public List<ChallengeSubmission> Submissions { get; set; } = new();

        public bool IsWeightValid =>
            WeightDosen + WeightAI + WeightPeer == 100
            && WeightDosen >= 0 && WeightAI >= 0 && WeightPeer >= 0;

        public string SystemLabel => TargetSystem switch
        {
            SimulationType.Flow        => "Flow",
            SimulationType.Level       => "Level",
            SimulationType.Temperature => "Temperature",
            _                          => "Flow"
        };
    }

    // ── Submission ───────────────────────────────────────────────────────────

    public class ChallengeSubmission
    {
        public Guid             Id                 { get; set; } = Guid.NewGuid();
        public Guid             ChallengeId        { get; set; }
        public string           StudentId          { get; set; } = string.Empty;
        public string           StudentName        { get; set; } = string.Empty;
        public DateTime         SubmittedAt        { get; set; } = DateTime.Now;
        public string           TextAnswer         { get; set; } = string.Empty;
        public string?          AttachmentPath     { get; set; }
        public string?          AttachmentFileName { get; set; }
        public SubmissionStatus Status             { get; set; } = SubmissionStatus.Submitted;

        /// <summary>Recorded PID metric values at time of submission (keyed by TaskMetrics.*).</summary>
        public Dictionary<string, double> MetricSnapshot { get; set; } = new();

        public GradeEntry?       DosenGrade  { get; set; }
        public GradeEntry?       AIGrade     { get; set; }
        public List<GradeEntry>  PeerGrades  { get; set; } = new();

        public double? ComputeFinalScore(Challenge challenge)
        {
            if (DosenGrade == null && AIGrade == null && PeerGrades.Count == 0) return null;

            double total = 0, weightUsed = 0;

            if (DosenGrade != null && challenge.WeightDosen > 0)
            { total += DosenGrade.Score * challenge.WeightDosen; weightUsed += challenge.WeightDosen; }

            if (AIGrade != null && challenge.WeightAI > 0)
            { total += AIGrade.Score * challenge.WeightAI; weightUsed += challenge.WeightAI; }

            if (PeerGrades.Count > 0 && challenge.WeightPeer > 0)
            {
                double avg = 0;
                foreach (var p in PeerGrades) avg += p.Score;
                avg /= PeerGrades.Count;
                total += avg * challenge.WeightPeer; weightUsed += challenge.WeightPeer;
            }

            return weightUsed > 0 ? total / weightUsed : null;
        }
    }

    // ── Grade entry ──────────────────────────────────────────────────────────

    public class GradeEntry
    {
        public Guid     Id          { get; set; } = Guid.NewGuid();
        public string   GraderName  { get; set; } = string.Empty;
        public double   Score       { get; set; }
        public string   Feedback    { get; set; } = string.Empty;
        public DateTime GradedAt    { get; set; } = DateTime.Now;
        public bool     IsAI        { get; set; }
    }
}
