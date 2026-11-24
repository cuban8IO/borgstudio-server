using System.Security.Cryptography;

namespace BorgStudio.Server.Domain.Access;

/// <summary>
/// An app asks to be paired (like OAuth's device flow): it gets a secret pairing code it polls with
/// and a short user code the user confirms in the browser after signing in. Only hashes of the
/// pairing code are stored.
/// </summary>
public sealed class PairingRequest
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>No 0/O, 1/I/L, 5/S, 8/B: easy to read and type.</summary>
    private const string UserCodeAlphabet = "ACDEFGHJKMNPQRTUVWXYZ234679";

    private PairingRequest()
    {
    }

    public Guid Id { get; private set; }

    public string PairingCodeHash { get; private set; } = string.Empty;

    /// <summary>Shown by the app and on the confirmation page, e.g. "KMTD-4XQA".</summary>
    public string UserCode { get; private set; } = string.Empty;

    public string DeviceName { get; private set; } = string.Empty;

    public string? Platform { get; private set; }

    public string? AppVersion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The user who confirmed it; the app gets its token at its next poll.</summary>
    public string? ApprovedUserId { get; private set; }

    public bool IsDenied { get; private set; }

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public static (PairingRequest Request, string PairingCode) Create(
        string deviceName, string? platform, string? appVersion, DateTimeOffset now)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var request = new PairingRequest
        {
            Id = Guid.NewGuid(),
            PairingCodeHash = Device.HashToken(code),
            UserCode = NewUserCode(),
            DeviceName = deviceName.Trim() is { Length: > 0 } name ? name[..Math.Min(name.Length, Device.MaxNameLength)] : "BorgStudio",
            Platform = platform?.Trim() is { Length: > 0 } p ? p[..Math.Min(p.Length, 100)] : null,
            AppVersion = appVersion?.Trim() is { Length: > 0 } v ? v[..Math.Min(v.Length, 50)] : null,
            CreatedAt = now,
            ExpiresAt = now + Lifetime,
        };
        return (request, code);
    }

    public void Approve(string userId)
    {
        ApprovedUserId = userId;
        IsDenied = false;
    }

    public void Deny()
    {
        ApprovedUserId = null;
        IsDenied = true;
    }

    /// <summary>"kmtd 4xqa", "KMTD4XQA" and "KMTD-4XQA" all mean the same code.</summary>
    public static string NormalizeUserCode(string? code)
    {
        var letters = new string((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return letters.Length == 8 ? $"{letters[..4]}-{letters[4..]}" : letters;
    }

    private static string NewUserCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = UserCodeAlphabet[RandomNumberGenerator.GetInt32(UserCodeAlphabet.Length)];
        }

        return $"{new string(chars, 0, 4)}-{new string(chars, 4, 4)}";
    }
}
