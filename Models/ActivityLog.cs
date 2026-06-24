namespace TLIGDashboard.Models;

public enum ActivityCategory
{
    Authentication,
    ControlParameter,
    AIInteraction,
    Simulation,
    RealSystem,
    TaskSubmission,
    Reflection,
    General
}

public static class ActivityActions
{
    // Authentication
    public const string Login   = "Login";
    public const string Logout  = "Logout";
    public const string SignUp  = "SignUp";

    // Control parameters
    public const string ParameterChanged = "ParameterChanged";

    // Simulation
    public const string SimulationStarted   = "SimulationStarted";
    public const string SimulationCompleted = "SimulationCompleted";

    // Real system
    public const string RealSystemOperated = "RealSystemOperated";

    // AI
    public const string AiQuery = "AiQuery";

    // Tasks / challenges
    public const string TaskStarted        = "TaskStarted";
    public const string TaskCompleted      = "TaskCompleted";
    public const string ChallengeSubmitted = "ChallengeSubmitted";

    // Reflection
    public const string ReflectionAdded = "ReflectionAdded";

    // General
    public const string PageVisited = "PageVisited";
}

/// <summary>A single recorded user activity entry.</summary>
public sealed class ActivityLog
{
    public string           Id           { get; set; } = "";
    public DateTime         TimestampUtc { get; set; } = DateTime.UtcNow;
    public string           Username     { get; set; } = "";
    public string           DisplayName  { get; set; } = "";
    public string           Role         { get; set; } = "";
    public ActivityCategory Category     { get; set; }
    public string           Action       { get; set; } = "";
    public string           Description  { get; set; } = "";
    public string           RelatedId    { get; set; } = "";
    public Dictionary<string, string> Metadata { get; set; } = new();

    // ── Display helpers (not persisted) ──────────────────────────────────────

    public string TimestampLocal => TimestampUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

    public string CategoryLabel => Category switch
    {
        ActivityCategory.Authentication  => "Autentikasi",
        ActivityCategory.ControlParameter => "Parameter",
        ActivityCategory.AIInteraction   => "AI",
        ActivityCategory.Simulation      => "Simulasi",
        ActivityCategory.RealSystem      => "Sistem Nyata",
        ActivityCategory.TaskSubmission  => "Tugas",
        ActivityCategory.Reflection      => "Refleksi",
        _                                => "Umum",
    };

    public string CategoryIcon => Category switch
    {
        ActivityCategory.Authentication  => "",  // lock
        ActivityCategory.ControlParameter => "", // settings
        ActivityCategory.AIInteraction   => "",  // message
        ActivityCategory.Simulation      => "",  // game
        ActivityCategory.RealSystem      => "",  // robot
        ActivityCategory.TaskSubmission  => "",  // attach
        ActivityCategory.Reflection      => "",  // pen
        _                                => "",  // document
    };
}

/// <summary>On-disk envelope for the activity log database.</summary>
public sealed class ActivityLogFile
{
    public int               Version    { get; set; } = 1;
    public List<ActivityLog> Activities { get; set; } = new();
}
