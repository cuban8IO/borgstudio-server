namespace BorgStudio.Server.Domain.Users;

/// <summary>
/// The password rules (ASP.NET Core Identity's defaults, at least 8 characters). The sign-in system
/// enforces them; the web interface shows them while the user types.
/// </summary>
public static class PasswordPolicy
{
    public const int MinimumLength = 8;

    public static bool HasMinimumLength(string? password) => password?.Length >= MinimumLength;

    public static bool HasUpper(string? password) => password?.Any(char.IsUpper) == true;

    public static bool HasLower(string? password) => password?.Any(char.IsLower) == true;

    public static bool HasDigit(string? password) => password?.Any(char.IsDigit) == true;

    /// <summary>Anything that is neither a letter nor a digit, as Identity counts it.</summary>
    public static bool HasSymbol(string? password) => password?.Any(c => !char.IsLetterOrDigit(c)) == true;

    public static bool IsMet(string? password) =>
        HasMinimumLength(password) && HasUpper(password) && HasLower(password) && HasDigit(password) && HasSymbol(password);
}
