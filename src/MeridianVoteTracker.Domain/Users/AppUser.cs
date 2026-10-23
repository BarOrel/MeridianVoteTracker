namespace MeridianVoteTracker.Domain.Users;

public enum UserRole
{
    Analyst,
    Admin,
}

/// <summary>Pre-defined user (simplified identity, assignment §3). Roles drive API authorization.</summary>
public sealed class AppUser
{
    public string Id { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public UserRole Role { get; private set; }

    private AppUser() { }

    public AppUser(string id, string displayName, UserRole role)
    {
        Id = id;
        DisplayName = displayName;
        Role = role;
    }
}
