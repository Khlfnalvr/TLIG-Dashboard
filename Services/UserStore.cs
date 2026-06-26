using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using TLIGDashboard.Models;

namespace TLIGDashboard.Services;

/// <summary>
/// Roles a user account can hold. Used for capability gating.
///
/// The taxonomy maps onto the campus roles requested by the product:
///   • <see cref="Dosen"/> (lecturer) and <see cref="Asisten"/> (assistant) are the
///     "staff"/admin accounts — full access. They may edit the learning analytics
///     and are the only accounts allowed to sign in to the <b>Server</b> application.
///   • <see cref="Mahasiswa"/> (student) is the restricted, client-only role: it can
///     use the client but only consume what the staff configure (read-only analytics).
/// </summary>
public static class UserRoles
{
    public const string Dosen     = "Dosen";     // lecturer  — full access (staff)
    public const string Asisten   = "Asisten";   // assistant — full access (staff)
    public const string Mahasiswa = "Mahasiswa"; // student   — restricted, client-only

    public static readonly string[] All = [Dosen, Asisten, Mahasiswa];

    // Legacy values that may still be present in older users.json files. Read only —
    // never written; migrated to the current taxonomy on load (see UserStore.Load).
    private const string LegacyAdmin    = "Admin";
    private const string LegacyOperator = "Operator";
    private const string LegacyViewer   = "Viewer";

    public static bool IsValid(string? role) => Array.IndexOf(All, role) >= 0;

    /// <summary>
    /// Staff ("admin") roles — lecturers and assistants. They have full access
    /// (edit analytics, manage users) and are the only accounts permitted to sign
    /// in to the Server application.
    /// </summary>
    public static bool IsStaff(string? role) => role is Dosen or Asisten;

    /// <summary>Students (Mahasiswa) — restricted, read-only client experience.</summary>
    public static bool IsStudent(string? role) => !IsStaff(role);

    /// <summary>Coerces any value to a valid role, defaulting to the least-privilege student role.</summary>
    public static string Normalize(string? role) => IsValid(role) ? role! : Mahasiswa;

    /// <summary>
    /// Maps a (possibly legacy) stored role onto the current taxonomy:
    /// Admin→Dosen, Operator→Asisten, Viewer→Mahasiswa. Already-current values pass through;
    /// anything unrecognised falls back to the least-privilege student role.
    /// </summary>
    public static string Migrate(string? role) => role switch
    {
        LegacyAdmin    => Dosen,
        LegacyOperator => Asisten,
        LegacyViewer   => Mahasiswa,
        _              => Normalize(role),
    };
}

/// <summary>A single account stored in the server's user database.</summary>
public sealed class UserAccount
{
    public string    Username     { get; set; } = "";
    public string    DisplayName  { get; set; } = "";
    public string    Email        { get; set; } = "";   // set for self-registered accounts (== Username)
    public string    Role         { get; set; } = UserRoles.Mahasiswa;
    public string    PasswordHash { get; set; } = "";   // base64 PBKDF2-SHA256 hash
    public string    Salt         { get; set; } = "";   // base64 random salt
    public bool      Enabled      { get; set; } = true;
    public DateTime  CreatedUtc   { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }

    /// <summary>Nomor Registrasi Pokok — student ID number (optional for staff).</summary>
    public string Nrp   { get; set; } = "";
    /// <summary>Class / cohort the student belongs to, e.g. "TK-3A".</summary>
    public string Kelas { get; set; } = "";
}

/// <summary>On-disk envelope for the user database (allows future migrations).</summary>
public sealed class UsersFile
{
    public int               Version { get; set; } = 1;
    public List<UserAccount> Users   { get; set; } = new();
}

/// <summary>
/// Server-side user database. Stores accounts (username + salted PBKDF2 password
/// hash + role) in <c>%LOCALAPPDATA%\TLIGDashboard\users.json</c> and authenticates
/// both the local server console login and remote clients connecting over the
/// share protocol. A default <c>admin / admin</c> Administrator is seeded on first
/// run so the server is usable out of the box.
/// </summary>
public sealed class UserStore
{
    public static UserStore Instance { get; } = new();

    // PBKDF2 parameters.
    private const int SaltSize   = 16;
    private const int HashSize   = 32;
    private const int Iterations = 120_000;

    private readonly object _lock = new();
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TLIGDashboard", "users.json");

    private UsersFile _file = new();

    /// <summary>Raised whenever the account list changes (add/edit/reset/delete).</summary>
    public event Action? Changed;

    private UserStore() { Load(); }

    public string FilePath => _path;

    /// <summary>Returns a snapshot copy of every account (newest activity is not sorted here).</summary>
    public IReadOnlyList<UserAccount> GetUsers()
    {
        lock (_lock)
            return _file.Users.Select(Clone).ToList();
    }

    // ── Authentication ────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a username/password pair. Returns the account (a copy) when the
    /// credentials are correct AND the account is enabled; otherwise <c>null</c>.
    /// Updates the account's last-login timestamp on success.
    /// </summary>
    public UserAccount? Verify(string username, string password)
    {
        username = (username ?? "").Trim();
        UserAccount? result = null;
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null || !u.Enabled) return null;
            if (!VerifyHash(password ?? "", u.PasswordHash, u.Salt)) return null;
            u.LastLoginUtc = DateTime.UtcNow;
            SaveLocked();
            result = Clone(u);
        }
        if (result != null)
            ActivityStore.Instance.Log(
                result.Username, result.DisplayName, result.Role,
                Models.ActivityCategory.Authentication, Models.ActivityActions.Login,
                $"Login berhasil");
        return result;
    }

    // ── Management operations (used by the User Management page) ────────────────
    // Each returns (ok, errorKey) where errorKey is a localization key on failure.

    public (bool ok, string? error) AddUser(
        string username, string password, string displayName, string role,
        string nrp = "", string kelas = "")
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username)) return (false, "Um_ErrUsernameEmpty");
        if (string.IsNullOrEmpty(password))      return (false, "Um_ErrPasswordEmpty");

        lock (_lock)
        {
            if (FindLocked(username) is not null) return (false, "Um_ErrUserExists");
            var u = CreateUser(username, password, displayName, role);
            u.Nrp   = (nrp   ?? "").Trim();
            u.Kelas = (kelas  ?? "").Trim();
            _file.Users.Add(u);
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    /// <summary>
    /// Self-registration from a remote client (the <c>/auth/signup</c> endpoint).
    /// Creates a verified account whose username is the normalized e-mail, after
    /// enforcing the <see cref="EmailPolicy"/> domain rule and a minimum password
    /// length. The account is granted the least-privilege <see cref="UserRoles.Mahasiswa"/>
    /// role; a staff member can promote it later via the User Management page.
    /// Returns <c>(true, null)</c> on success or <c>(false, errorKey)</c> where the
    /// key is a localization string the client can display.
    /// </summary>
    public (bool ok, string? error) SignUp(string email, string password)
    {
        var policyError = EmailPolicy.Validate(email);
        if (policyError is not null)             return (false, policyError);
        if (string.IsNullOrEmpty(password))      return (false, "Um_ErrPasswordEmpty");
        if (password.Length < 6)                 return (false, "Signup_ErrPasswordShort");

        var normalized  = EmailPolicy.Normalize(email);
        var displayName = normalized.Split('@')[0];

        lock (_lock)
        {
            if (FindLocked(normalized) is not null) return (false, "Signup_ErrExists");

            var user = CreateUser(normalized, password, displayName, UserRoles.Mahasiswa);
            user.Email = normalized;
            _file.Users.Add(user);
            SaveLocked();
        }
        ActivityStore.Instance.Log(
            normalized, displayName, UserRoles.Mahasiswa,
            Models.ActivityCategory.Authentication, Models.ActivityActions.SignUp,
            $"Akun baru terdaftar: {normalized}");
        Changed?.Invoke();
        return (true, null);
    }

    public (bool ok, string? error) ResetPassword(string username, string newPassword)
    {
        if (string.IsNullOrEmpty(newPassword)) return (false, "Um_ErrPasswordEmpty");
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null) return (false, "Um_ErrUserNotFound");
            (u.PasswordHash, u.Salt) = Hash(newPassword);
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    public (bool ok, string? error) UpdateUser(
        string username, string displayName, string role,
        string nrp = "", string kelas = "")
    {
        if (!UserRoles.IsValid(role)) return (false, "Um_ErrInvalidRole");
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null) return (false, "Um_ErrUserNotFound");
            // Don't demote the last enabled staff (Dosen/Asisten) account to a student.
            if (UserRoles.IsStaff(u.Role) && !UserRoles.IsStaff(role) && u.Enabled && EnabledStaffCountLocked() <= 1)
                return (false, "Um_ErrLastAdmin");
            u.DisplayName = string.IsNullOrWhiteSpace(displayName) ? u.Username : displayName.Trim();
            u.Role        = role;
            u.Nrp         = (nrp   ?? "").Trim();
            u.Kelas       = (kelas  ?? "").Trim();
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    /// <summary>
    /// Updates the logged-in user's own display name and / or email.
    /// Email is validated via <see cref="EmailPolicy"/> before being stored.
    /// Pass <c>null</c> for any field you don't want to change.
    /// </summary>
    public (bool ok, string? error) UpdateProfile(string username, string? displayName, string? email)
    {
        if (email != null)
        {
            var domainErr = EmailPolicy.Validate(email);
            if (domainErr != null) return (false, domainErr);
        }
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null) return (false, "Um_ErrUserNotFound");
            if (!string.IsNullOrWhiteSpace(displayName))
                u.DisplayName = displayName.Trim();
            if (email != null)
                u.Email = EmailPolicy.Normalize(email);
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    public (bool ok, string? error) SetEnabled(string username, bool enabled)
    {
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null) return (false, "Um_ErrUserNotFound");
            if (!enabled && UserRoles.IsStaff(u.Role) && u.Enabled && EnabledStaffCountLocked() <= 1)
                return (false, "Um_ErrLastAdmin");
            u.Enabled = enabled;
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    public (bool ok, string? error) DeleteUser(string username)
    {
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null) return (false, "Um_ErrUserNotFound");
            if (UserRoles.IsStaff(u.Role) && u.Enabled && EnabledStaffCountLocked() <= 1)
                return (false, "Um_ErrLastAdmin");
            _file.Users.Remove(u);
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    // ── Internals ───────────────────────────────────────────────────────────────

    private UserAccount? FindLocked(string username) =>
        _file.Users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));

    private int EnabledStaffCountLocked() =>
        _file.Users.Count(x => x.Enabled && UserRoles.IsStaff(x.Role));

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                    _file = JsonSerializer.Deserialize(File.ReadAllText(_path), AppJsonContext.Default.UsersFile) ?? new();
            }
            catch { _file = new(); }

            // Migrate any legacy roles (Admin/Operator/Viewer) to the current
            // taxonomy (Dosen/Asisten/Mahasiswa) so older databases keep working.
            bool migrated = false;
            foreach (var u in _file.Users)
            {
                var mapped = UserRoles.Migrate(u.Role);
                if (!string.Equals(mapped, u.Role, StringComparison.Ordinal))
                {
                    u.Role = mapped;
                    migrated = true;
                }
            }

            // Seed a default staff account (Dosen) so a fresh server can be signed into.
            if (_file.Users.Count == 0)
            {
                _file.Users.Add(CreateUser("admin", "admin", "Administrator", UserRoles.Dosen));
                migrated = true;
            }

            if (migrated) SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_file, AppJsonContext.Default.UsersFile));
        }
        catch { }
    }

    private static UserAccount CreateUser(string username, string password, string displayName, string role)
    {
        var (hash, salt) = Hash(password);
        return new UserAccount
        {
            Username     = username.Trim(),
            DisplayName  = string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
            Role         = UserRoles.Normalize(role),
            PasswordHash = hash,
            Salt         = salt,
            Enabled      = true,
            CreatedUtc   = DateTime.UtcNow,
        };
    }

    private static UserAccount Clone(UserAccount u) => new()
    {
        Username     = u.Username,
        DisplayName  = u.DisplayName,
        Email        = u.Email,
        Role         = u.Role,
        PasswordHash = u.PasswordHash,
        Salt         = u.Salt,
        Enabled      = u.Enabled,
        CreatedUtc   = u.CreatedUtc,
        LastLoginUtc = u.LastLoginUtc,
        Nrp          = u.Nrp,
        Kelas        = u.Kelas,
    };

    // ── Password hashing (PBKDF2-SHA256, per-user random salt) ──────────────────

    private static (string hash, string salt) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    private static bool VerifyHash(string password, string hashB64, string saltB64)
    {
        try
        {
            var salt     = Convert.FromBase64String(saltB64);
            var expected = Convert.FromBase64String(hashB64);
            var actual   = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }
}
