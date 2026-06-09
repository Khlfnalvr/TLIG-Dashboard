namespace TLIGDashboard.Services;

/// <summary>
/// App-wide identity of the currently signed-in user. Set once at login
/// (<c>MainWindow.OnLoginSucceeded</c>) and cleared on logout, it lets any page
/// adapt its capabilities to the user's role without threading the role through
/// navigation.
///
/// The two client access levels requested by the product map directly onto
/// <see cref="UserRoles.IsStaff(string)"/>:
///   • <b>Staff</b> (Dosen/Asisten) → full access: may edit the learning analytics.
///   • <b>Student</b> (Mahasiswa)   → restricted: read-only, can only consume what
///     staff configure.
///
/// On the Server flavor only staff can sign in at all (students are blocked at the
/// login screen), so on the server <see cref="IsStaff"/> is effectively always true.
/// </summary>
public sealed class SessionService
{
    public static SessionService Instance { get; } = new();
    private SessionService() { }

    public string Username    { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string Role        { get; private set; } = "";

    public bool IsSignedIn => !string.IsNullOrWhiteSpace(Username);

    /// <summary>True for Dosen/Asisten — the full-access "admin" accounts.</summary>
    public bool IsStaff => UserRoles.IsStaff(Role);

    /// <summary>True for a signed-in Mahasiswa — the restricted, read-only role.</summary>
    public bool IsStudent => IsSignedIn && !IsStaff;

    /// <summary>
    /// Whether the user may edit the learning-analytics content (progress, scores,
    /// status, course list). Staff only; students get a read-only view.
    /// </summary>
    public bool CanEditAnalytics => IsStaff;

    /// <summary>Raised whenever the signed-in identity changes (sign in / sign out).</summary>
    public event Action? Changed;

    public void SignIn(string username, string displayName, string role)
    {
        Username    = (username ?? "").Trim();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Username : displayName.Trim();
        Role        = role ?? "";
        Changed?.Invoke();
    }

    /// <summary>Updates display name in-place without re-authenticating.</summary>
    public void UpdateDisplayName(string displayName)
    {
        if (!IsSignedIn) return;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Username : displayName.Trim();
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Username = DisplayName = Role = "";
        Changed?.Invoke();
    }
}
