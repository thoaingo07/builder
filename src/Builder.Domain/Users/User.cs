namespace Builder.Domain.Users;

public sealed class User
{
    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string UserName { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public string PasswordHash { get; private set; } = "";
    public bool IsAdmin { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    private User() { }

    public User(string userName, string displayName, string passwordHash, bool isAdmin, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(userName)) throw new DomainException("User name is required.");
        UserName = userName.Trim().ToLowerInvariant();
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? UserName : displayName.Trim();
        PasswordHash = passwordHash;
        IsAdmin = isAdmin;
        CreatedAt = now;
    }

    public void SetPasswordHash(string hash) => PasswordHash = hash;
}
