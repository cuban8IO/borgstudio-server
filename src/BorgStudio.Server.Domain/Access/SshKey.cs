using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BorgStudio.Server.Domain.Access;

/// <summary>
/// A public SSH key that may open one repository with borg, with the rights of its user. Paired apps
/// register one key per repository; users can also add keys by hand (scripts, borgmatic, Vorta).
/// A key belongs to exactly one repository, so the server knows which one a connection means.
/// </summary>
public sealed class SshKey
{
    public const int MaxNameLength = 100;

    private SshKey()
    {
    }

    public Guid Id { get; private set; }

    public Guid RepositoryId { get; private set; }

    /// <summary>Whose rights the key has.</summary>
    public string UserId { get; private set; } = string.Empty;

    /// <summary>The paired app or token that registered it; <c>null</c> for keys added by hand.</summary>
    public Guid? DeviceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>e.g. "ssh-ed25519".</summary>
    public string Algorithm { get; private set; } = string.Empty;

    /// <summary>The key in base64, as in an authorized_keys line.</summary>
    public string Blob { get; private set; } = string.Empty;

    /// <summary>"SHA256:…", the form sshd and ssh-keygen show.</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public string AuthorizedKey => $"{Algorithm} {Blob}";

    public static SshKey Create(Guid repositoryId, string userId, Guid? deviceId, string name, SshPublicKey key, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = repositoryId,
        UserId = userId,
        DeviceId = deviceId,
        Name = name.Trim() is { Length: > 0 } n ? n[..Math.Min(n.Length, MaxNameLength)] : key.Comment ?? key.Algorithm,
        Algorithm = key.Algorithm,
        Blob = key.Blob,
        Fingerprint = key.Fingerprint,
        CreatedAt = now,
    };

    public void MarkUsed(DateTimeOffset now) => LastUsedAt = now;
}

/// <summary>A parsed OpenSSH public key ("ssh-ed25519 AAAA… comment").</summary>
public sealed record SshPublicKey(string Algorithm, string Blob, string? Comment)
{
    /// <summary>Key types borg's SSH clients use; DSA is too weak and left out.</summary>
    public static readonly IReadOnlyList<string> Algorithms =
    [
        "ssh-ed25519",
        "ssh-rsa",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "sk-ssh-ed25519@openssh.com",
        "sk-ecdsa-sha2-nistp256@openssh.com",
    ];

    public string Fingerprint => FingerprintOf(Convert.FromBase64String(Blob));

    /// <summary>
    /// Reads a public key in OpenSSH format. Options in front (as in authorized_keys) are not
    /// accepted: the server sets its own. Returns <c>null</c> if it is not a valid key.
    /// </summary>
    public static SshPublicKey? Parse(string? text)
    {
        var parts = (text ?? string.Empty).Trim().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !Algorithms.Contains(parts[0]))
        {
            return null;
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        // The key data starts with its own type name; it has to match the one in front.
        if (blob.Length < 4)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(blob);
        if (length > blob.Length - 4 || Encoding.ASCII.GetString(blob, 4, (int)length) != parts[0] || blob.Length < 4 + length + 16)
        {
            return null;
        }

        return new SshPublicKey(parts[0], parts[1], parts.Length > 2 ? parts[2].Trim() : null);
    }

    /// <summary>The fingerprint OpenSSH shows: "SHA256:" + base64 of the hash, without padding.</summary>
    public static string FingerprintOf(byte[] blob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
}
