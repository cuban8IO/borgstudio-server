using System.Security.Cryptography;
using System.Text;

namespace BorgStudio.Server.Domain.Users;

/// <summary>
/// A one-time link that lets an invited user set their own password. Only a hash of the token is
/// stored, so the link cannot be read back from the database.
/// </summary>
public sealed class Invitation
{
    /// <summary>The validity periods an administrator can pick, in days.</summary>
    public static readonly IReadOnlyList<int> ValidityDays = [1, 7, 30];

    private Invitation()
    {
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    /// <summary>Creates an invitation and returns the token that goes into the link (shown once).</summary>
    public static (Invitation Invitation, string Token) Create(string userId, int validityDays, DateTimeOffset now)
    {
        if (!ValidityDays.Contains(validityDays))
        {
            throw new ArgumentOutOfRangeException(nameof(validityDays), validityDays, "Unsupported validity.");
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.AddDays(validityDays),
        };
        return (invitation, token);
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));
}
