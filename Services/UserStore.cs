using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace TLIGDashboard.Services;

/// <summary>Roles a user account can hold. Used for capability gating.</summary>
public static class UserRoles
{
    public const string Admin    = "Admin";
    public const string Operator = "Operator";
    public const string Viewer   = "Viewer";

    public static readonly string[] All = [Admin, Operator, Viewer];

    public static bool   IsValid(string? role) => Array.IndexOf(All, role) >= 0;
    public static string Normalize(string? role) => IsValid(role) ? role! : Operator;
}

/// <summary>A single account stored in the server's user database.</summary>
public sealed class UserAccount
{
    public string    Username     { get; set; } = "";
    public string    DisplayName  { get; set; } = "";
    public string    Email        { get; set; } = "";   // set for self-registered accounts (== Username)
    public string    Role         { get; set; } = UserRoles.Operator;
    public string    PasswordHash { get; set; } = "";   // base64 PBKDF2-SHA256 hash
    public string    Salt         { get; set; } = "";   // base64 random salt
    public bool      Enabled      { get; set; } = true;
    public DateTime  CreatedUtc   { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
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
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null || !u.Enabled) return null;
            if (!VerifyHash(password ?? "", u.PasswordHash, u.Salt)) return null;
            u.LastLoginUtc = DateTime.UtcNow;
            SaveLocked();
            return Clone(u);
        }
    }

    // ── Management operations (used by the User Management page) ────────────────
    // Each returns (ok, errorKey) where errorKey is a localization key on failure.

    public (bool ok, string? error) AddUser(string username, string password, string displayName, string role)
    {
        username = (username ?? "").Trim();
        if (string.IsNullOrWhiteSpace(username)) return (false, "Um_ErrUsernameEmpty");
        if (string.IsNullOrEmpty(password))      return (false, "Um_ErrPasswordEmpty");

        lock (_lock)
        {
            if (FindLocked(username) is not null) return (false, "Um_ErrUserExists");
            _file.Users.Add(CreateUser(username, password, displayName, role));
            SaveLocked();
        }
        Changed?.Invoke();
        return (true, null);
    }

    /// <summary>
    /// Self-registration from a remote client (the <c>/auth/signup</c> endpoint).
    /// Creates a verified account whose username is the normalized e-mail, after
    /// enforcing the <see cref="EmailPolicy"/> domain rule and a minimum password
    /// length. The account is granted the least-privilege <see cref="UserRoles.Viewer"/>
    /// role; an Admin can promote it later via the User Management page.
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

            var user = CreateUser(normalized, password, displayName, UserRoles.Viewer);
            user.Email = normalized;
            _file.Users.Add(user);
            SaveLocked();
        }
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

    public (bool ok, string? error) UpdateUser(string username, string displayName, string role)
    {
        if (!UserRoles.IsValid(role)) return (false, "Um_ErrInvalidRole");
        lock (_lock)
        {
            var u = FindLocked(username);
            if (u is null) return (false, "Um_ErrUserNotFound");
            // Don't strip the last enabled administrator of its role.
            if (u.Role == UserRoles.Admin && role != UserRoles.Admin && u.Enabled && EnabledAdminCountLocked() <= 1)
                return (false, "Um_ErrLastAdmin");
            u.DisplayName = string.IsNullOrWhiteSpace(displayName) ? u.Username : displayName.Trim();
            u.Role        = role;
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
            if (!enabled && u.Role == UserRoles.Admin && u.Enabled && EnabledAdminCountLocked() <= 1)
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
            if (u.Role == UserRoles.Admin && u.Enabled && EnabledAdminCountLocked() <= 1)
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

    private int EnabledAdminCountLocked() =>
        _file.Users.Count(x => x.Enabled && x.Role == UserRoles.Admin);

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

            // Seed a default administrator so a fresh server can be signed into.
            if (_file.Users.Count == 0)
            {
                _file.Users.Add(CreateUser("admin", "admin", "Administrator", UserRoles.Admin));
                SaveLocked();
            }
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
