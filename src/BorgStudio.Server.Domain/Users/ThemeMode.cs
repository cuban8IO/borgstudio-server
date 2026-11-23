namespace BorgStudio.Server.Domain.Users;

/// <summary>The color scheme a user picked for the web interface.</summary>
public enum ThemeMode
{
    /// <summary>Follow the browser's or operating system's light/dark setting.</summary>
    System = 0,
    Light = 1,
    Dark = 2,
}
