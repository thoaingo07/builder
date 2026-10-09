namespace Builder.Domain.Users;

public sealed class User
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string UserName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    /// <summary>Lower-cased e-mail used to match external (Google) sign-ins; null for local-only users.</summary>
    public string? Email { get; private set; }
    /// <summary>Null for users who can only sign in with an external provider.</summary>
    public string? PasswordHash { get; private set; }
    public bool IsAdmin { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private User() { }

    public User(string userName, string displayName, string? passwordHash, bool isAdmin, DateTimeOffset now, string? email = null)
    {
        if (string.IsNullOrWhiteSpace(userName)) throw new DomainException("User name is required.");
        UserName = userName.Trim().ToLowerInvariant();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? UserName : displayName.Trim();
        PasswordHash = passwordHash;
        IsAdmin = isAdmin;
        CreatedAt = now;
        Email = NormalizeEmail(email);
    }

    /// <summary>A user who may sign in with Google (or another provider) using this e-mail address.</summary>
    public static User External(string email, string? displayName, bool isAdmin, DateTimeOffset now)
    {
        var normalized = NormalizeEmail(email) ?? throw new DomainException("E-mail is required.");
        return new User(normalized, displayName ?? normalized, null, isAdmin, now, normalized);
    }

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void UpdateProfile(string? displayName, bool isAdmin)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) DisplayName = displayName.Trim();
        IsAdmin = isAdmin;
    }

    public static string? NormalizeEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var e = email.Trim().ToLowerInvariant();
        if (!e.Contains('@')) throw new DomainException($"'{email}' is not an e-mail address.");
        return e;
    }
}
