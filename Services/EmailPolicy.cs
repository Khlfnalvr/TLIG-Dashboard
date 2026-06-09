using System.Net.Mail;

namespace TLIGDashboard.Services;

/// <summary>
/// Single source of truth for the e-mail rule used by client self-registration.
/// Accepted domains:
/// <list type="bullet">
///   <item><c>its.ac.id</c> and any sub-domain (e.g. <c>mhs.its.ac.id</c>) — general campus</item>
///   <item><c>ep.itc.ac.id</c> — department domain for academic staff</item>
/// </list>
/// Shared by both flavors: the <b>client</b> calls it for instant feedback in the
/// signup form, and the <b>server</b> calls it from <see cref="UserStore.SignUp"/>
/// as the authoritative gate (a hand-crafted request could otherwise bypass the
/// client-side check).
/// </summary>
public static class EmailPolicy
{
    /// <summary>The root campus domain. Sub-domains of this are also accepted.</summary>
    public const string RootDomain = "its.ac.id";

    /// <summary>Additional allowed domain for academic staff (Dosen).</summary>
    public const string StaffDomain = "ep.itc.ac.id";

    /// <summary>Trimmed, lower-invariant form used everywhere as the canonical key.</summary>
    public static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// True when the string is a single, plain e-mail address. Uses the built-in
    /// <see cref="MailAddress"/> parser (no extra dependency) and rejects
    /// display-name forms like <c>"Budi &lt;budi@its.ac.id&gt;"</c> by requiring the
    /// parsed address to equal the normalized input.
    /// </summary>
    public static bool IsValidFormat(string? email)
    {
        var e = Normalize(email);
        if (string.IsNullOrEmpty(e) || e.Contains(' ') || !e.Contains('@')) return false;
        try
        {
            var addr = new MailAddress(e);
            return string.Equals(addr.Address, e, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// True when the address domain is <c>its.ac.id</c> / any sub-domain, OR
    /// the staff domain <c>ep.itc.ac.id</c>.
    /// </summary>
    public static bool IsAllowedDomain(string? email)
    {
        var e = Normalize(email);
        int at = e.LastIndexOf('@');
        if (at < 0) return false;
        var domain = e[(at + 1)..];
        return domain == RootDomain
            || domain.EndsWith("." + RootDomain, StringComparison.Ordinal)
            || domain == StaffDomain;
    }

    /// <summary>
    /// Returns <c>null</c> when the address is acceptable, otherwise a localization
    /// key describing the problem (<c>Signup_ErrEmailFormat</c> /
    /// <c>Signup_ErrEmailDomain</c>).
    /// </summary>
    public static string? Validate(string? email)
    {
        if (!IsValidFormat(email))   return "Signup_ErrEmailFormat";
        if (!IsAllowedDomain(email)) return "Signup_ErrEmailDomain";
        return null;
    }
}
