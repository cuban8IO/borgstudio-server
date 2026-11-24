using System.Security.Cryptography;
using System.Text;

namespace BorgStudio.Server.Domain.Access;

/// <summary>
/// Something that uses the server's interface in a user's name: a paired BorgStudio app or an API
/// token created in the web interface. It is identified by a token, of which only a hash is stored;
/// it stays valid until it is revoked (API tokens may also expire).
/// </summary>
public sealed class Device
{
    public const int MaxNameLength = 100;

    /// <summary>Prefix of every token, so a leaked one is easy to recognize (e.g. by secret scanners).</summary>
    public const string TokenPrefix = "bss_";

    private Device()
    {
    }

    public Guid Id { get; private set; }

    public string UserId { get; private set; } = string.Empty;

    public DeviceKind Kind { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>Operating system of a paired app, e.g. "Windows 11".</summary>
    public string? Platform { get; private set; }

    /// <summary>Version of a paired app.</summary>
    public string? AppVersion { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Only API tokens can have an end; paired apps stay until they are revoked.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } end && now >= end;

    /// <summary>Creates a device and returns its token (shown or handed over once).</summary>
    public static (Device Device, string Token) Create(
        string userId,
        DeviceKind kind,
        string name,
        string? platform,
        string? appVersion,
        DateTimeOffset? expiresAt,
        DateTimeOffset now)
    {
        var token = TokenPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var device = new Device
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Kind = kind,
            Name = Trim(name, MaxNameLength),
            Platform = platform is null ? null : Trim(platform, 100),
            AppVersion = appVersion is null ? null : Trim(appVersion, 50),
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = kind == DeviceKind.ApiToken ? expiresAt : null,
        };
        return (device, token);
    }

    /// <summary>Notes a use; to spare the database, at most once a minute.</summary>
    public bool Touch(DateTimeOffset now)
    {
        if (LastUsedAt is { } last && now - last < TimeSpan.FromMinutes(1))
        {
            return false;
        }

        LastUsedAt = now;
        return true;
    }

    /// <summary>A paired app reports its current version and system with every pairing.</summary>
    public void Describe(string? platform, string? appVersion)
    {
        Platform = platform is null ? Platform : Trim(platform, 100);
        AppVersion = appVersion is null ? AppVersion : Trim(appVersion, 50);
    }

    public static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    private static string Trim(string value, int max)
    {
        var trimmed = value.Trim();
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}

public enum DeviceKind
{
    /// <summary>A BorgStudio app paired through the browser.</summary>
    App = 0,

    /// <summary>A token created in the web interface, e.g. for scripts.</summary>
    ApiToken = 1,
}
