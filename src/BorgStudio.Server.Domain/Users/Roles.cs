namespace BorgStudio.Server.Domain.Users;

/// <summary>Roles of web interface users.</summary>
public static class Roles
{
    /// <summary>May change everything, including users and server settings.</summary>
    public const string Administrator = "Administrator";

    /// <summary>May look at everything, but not change it.</summary>
    public const string User = "User";

    public static readonly IReadOnlyList<string> All = [Administrator, User];
}
