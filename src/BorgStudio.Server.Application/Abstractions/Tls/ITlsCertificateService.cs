namespace BorgStudio.Server.Application.Abstractions.Tls;

/// <summary>The certificate the server's HTTPS endpoint presents.</summary>
public interface ITlsCertificateService
{
    /// <summary>Whether the server listens on HTTPS at all (off e.g. in a container behind a reverse proxy).</summary>
    bool HttpsEnabled { get; }

    /// <summary>The certificate in use and where it comes from; <c>null</c> while HTTPS is off and none was given.</summary>
    TlsCertificateInfo? Current { get; }

    /// <summary>Checks a PKCS#12 file (.pfx/.p12) and, if it is usable, makes it the server's certificate.</summary>
    Task<TlsCertificateError> UploadPfxAsync(byte[] pfx, string? password, CancellationToken cancellationToken);

    /// <summary>Same for a PEM certificate (chain) and its unencrypted private key.</summary>
    Task<TlsCertificateError> UploadPemAsync(string certificatePem, string keyPem, CancellationToken cancellationToken);

    /// <summary>Goes back to the configured or the self-signed certificate.</summary>
    Task RemoveUploadedAsync(CancellationToken cancellationToken);
}

public enum TlsCertificateSource
{
    /// <summary>Uploaded on the settings page; stored in the data folder.</summary>
    Uploaded,

    /// <summary>Named in the configuration file (or environment variables).</summary>
    Configured,

    /// <summary>Created by the server itself on the first start.</summary>
    SelfSigned,
}

public enum TlsCertificateError
{
    None,

    /// <summary>Not a certificate, or the password is wrong.</summary>
    Unreadable,

    NoPrivateKey,

    Expired,
}

public sealed record TlsCertificateStatus(bool HttpsEnabled, TlsCertificateInfo? Certificate);

public sealed record TlsCertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Thumbprint,
    IReadOnlyList<string> Names,
    TlsCertificateSource Source,
    string Sha256Fingerprint = "")
{
    /// <summary>SHA-256 of the certificate as "AB:CD:…" – what browsers and the BorgStudio app show to compare.</summary>
    public static string Sha256Of(byte[] rawData) =>
        string.Join(':', System.Security.Cryptography.SHA256.HashData(rawData).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
}
