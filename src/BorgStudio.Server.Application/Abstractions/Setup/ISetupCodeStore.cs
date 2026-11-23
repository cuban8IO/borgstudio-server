namespace BorgStudio.Server.Application.Abstractions.Setup;

/// <summary>The one-time code that unlocks the setup wizard while the server has no users.</summary>
public interface ISetupCodeStore
{
    /// <summary>The current code, or <c>null</c> when setup is done.</summary>
    string? Current { get; }

    /// <summary>Creates a code (or keeps the one from an earlier start) and announces it in the log.</summary>
    Task EnsureAsync(CancellationToken cancellationToken);

    /// <summary>Removes the code after the first administrator was created.</summary>
    Task RemoveAsync(CancellationToken cancellationToken);
}
