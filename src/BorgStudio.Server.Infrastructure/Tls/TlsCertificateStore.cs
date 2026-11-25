using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using BorgStudio.Server.Application.Abstractions.Tls;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Infrastructure.Tls;

/// <summary>
/// Holds the HTTPS certificate. Order: an uploaded certificate (data/tls/uploaded.pfx), else the one in
/// the configuration, else a self-signed one the server creates (data/tls/self-signed.pfx). The HTTPS
/// endpoint asks for <see cref="Certificate"/> on every new connection, so changes apply at once.
/// </summary>
public sealed class TlsCertificateStore : ITlsCertificateService
{
    private static readonly TimeSpan SelfSignedLifetime = TimeSpan.FromDays(5 * 365);

    // A self-signed certificate this close to its end is replaced on start.
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    private readonly ServerPaths paths;
    private readonly HttpsEndpointOptions options;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TlsCertificateStore> logger;
    private readonly Lock gate = new();
    private (X509Certificate2 Certificate, TlsCertificateSource Source)? current;

    public TlsCertificateStore(
        ServerPaths paths,
        IOptions<ServerEndpointOptions> options,
        TimeProvider timeProvider,
        ILogger<TlsCertificateStore> logger)
    {
        this.paths = paths;
        this.options = options.Value.Https;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    private string TlsDirectory => Path.Combine(paths.DataDirectory, "tls");

    private string UploadedFile => Path.Combine(TlsDirectory, "uploaded.pfx");

    private string SelfSignedFile => Path.Combine(TlsDirectory, "self-signed.pfx");

    /// <summary>The certificate to present; loaded (or created) on first use.</summary>
    public X509Certificate2 Certificate => Load(createSelfSigned: true)!.Value.Certificate;

    public bool HttpsEnabled => options.Enabled;

    public TlsCertificateInfo? Current
    {
        get
        {
            // Without HTTPS (e.g. a container behind a reverse proxy) no self-signed certificate is made.
            var loaded = Load(createSelfSigned: options.Enabled);
            return loaded is { } value ? Describe(value.Certificate, value.Source) : null;
        }
    }

    public Task<TlsCertificateError> UploadPfxAsync(byte[] pfx, string? password, CancellationToken cancellationToken)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.Exportable);
        }
        catch (CryptographicException)
        {
            return Task.FromResult(TlsCertificateError.Unreadable);
        }

        return AcceptUploadAsync(certificate);
    }

    public Task<TlsCertificateError> UploadPemAsync(string certificatePem, string keyPem, CancellationToken cancellationToken)
    {
        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException)
        {
            return Task.FromResult(TlsCertificateError.Unreadable);
        }

        return AcceptUploadAsync(certificate);
    }

    public Task RemoveUploadedAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (File.Exists(UploadedFile))
            {
                File.Delete(UploadedFile);
            }

            current = null;
        }

        Load(createSelfSigned: options.Enabled);
        return Task.CompletedTask;
    }

    private Task<TlsCertificateError> AcceptUploadAsync(X509Certificate2 certificate)
    {
        using (certificate)
        {
            if (!certificate.HasPrivateKey)
            {
                return Task.FromResult(TlsCertificateError.NoPrivateKey);
            }

            if (certificate.NotAfter.ToUniversalTime() <= timeProvider.GetUtcNow().UtcDateTime)
            {
                return Task.FromResult(TlsCertificateError.Expired);
            }

            // Stored without a password, protected like the rest of the data folder.
            WriteProtected(UploadedFile, certificate.Export(X509ContentType.Pkcs12));
        }

        lock (gate)
        {
            current = null;
        }

        Load(createSelfSigned: options.Enabled);
        return Task.FromResult(TlsCertificateError.None);
    }

    private (X509Certificate2 Certificate, TlsCertificateSource Source)? Load(bool createSelfSigned)
    {
        lock (gate)
        {
            if (current is { } loaded)
            {
                return loaded;
            }

            current = LoadUploaded() ?? LoadConfigured() ?? (createSelfSigned ? LoadSelfSigned() : null);
            if (current is null)
            {
                return null;
            }

            var (certificate, source) = current.Value;
            logger.LogInformation(
                "HTTPS certificate: {Subject} ({Source}), valid until {NotAfter:yyyy-MM-dd}.",
                certificate.Subject,
                source,
                certificate.NotAfter);
            return current.Value;
        }
    }

    private (X509Certificate2, TlsCertificateSource)? LoadUploaded()
    {
        if (!File.Exists(UploadedFile))
        {
            return null;
        }

        try
        {
            return (X509CertificateLoader.LoadPkcs12FromFile(UploadedFile, null), TlsCertificateSource.Uploaded);
        }
        catch (CryptographicException exception)
        {
            logger.LogError(exception, "The uploaded HTTPS certificate in {File} cannot be read; it is ignored.", UploadedFile);
            return null;
        }
    }

    private (X509Certificate2, TlsCertificateSource)? LoadConfigured()
    {
        if (string.IsNullOrWhiteSpace(options.CertificatePath))
        {
            return null;
        }

        var path = Path.GetFullPath(options.CertificatePath, AppContext.BaseDirectory);
        try
        {
            X509Certificate2 certificate;
            if (!string.IsNullOrWhiteSpace(options.CertificateKeyPath))
            {
                var keyPath = Path.GetFullPath(options.CertificateKeyPath, AppContext.BaseDirectory);
                using var pem = X509Certificate2.CreateFromPemFile(path, keyPath);
                // Windows cannot use the in-memory key of a PEM certificate for TLS; a PKCS#12 round trip fixes that.
                certificate = X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pkcs12), null);
            }
            else
            {
                certificate = X509CertificateLoader.LoadPkcs12FromFile(path, options.CertificatePassword);
            }

            if (!certificate.HasPrivateKey)
            {
                logger.LogError("The configured HTTPS certificate {File} has no private key; it is ignored.", path);
                return null;
            }

            return (certificate, TlsCertificateSource.Configured);
        }
        catch (Exception exception) when (exception is CryptographicException or IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The configured HTTPS certificate {File} cannot be read; it is ignored.", path);
            return null;
        }
    }

    private (X509Certificate2, TlsCertificateSource) LoadSelfSigned()
    {
        if (File.Exists(SelfSignedFile))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(SelfSignedFile, null);
                if (existing.NotAfter.ToUniversalTime() - timeProvider.GetUtcNow().UtcDateTime > RenewBefore)
                {
                    return (existing, TlsCertificateSource.SelfSigned);
                }

                existing.Dispose();
            }
            catch (CryptographicException exception)
            {
                logger.LogWarning(exception, "The self-signed certificate cannot be read; a new one is created.");
            }
        }

        var pfx = CreateSelfSigned(Dns.GetHostName(), LocalAddresses(), timeProvider.GetUtcNow());
        WriteProtected(SelfSignedFile, pfx);
        logger.LogInformation("A self-signed HTTPS certificate was created in {File}.", SelfSignedFile);
        return (X509CertificateLoader.LoadPkcs12(pfx, null), TlsCertificateSource.SelfSigned);
    }

    /// <summary>A self-signed server certificate for the host name, localhost and the given addresses, as PKCS#12.</summary>
    internal static byte[] CreateSelfSigned(string hostName, IEnumerable<IPAddress> addresses, DateTimeOffset now)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={hostName}, O=BorgStudio Server", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(hostName);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        foreach (var address in addresses.Distinct())
        {
            names.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1", "Server Authentication")], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        using var certificate = request.CreateSelfSigned(now.AddDays(-1), now.Add(SelfSignedLifetime));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork
                    || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static TlsCertificateInfo Describe(X509Certificate2 certificate, TlsCertificateSource source)
    {
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames().Concat(e.EnumerateIPAddresses().Select(ip => ip.ToString())))
            .ToList();
        return new TlsCertificateInfo(
            certificate.Subject,
            certificate.Issuer,
            new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero),
            certificate.Thumbprint,
            names,
            source,
            TlsCertificateInfo.Sha256Of(certificate.RawData));
    }

    /// <summary>
    /// Synchronous on purpose: this runs inside a lock, possibly while a Blazor page renders, where
    /// blocking on an asynchronous write would deadlock.
    /// </summary>
    private void WriteProtected(string path, byte[] content)
    {
        paths.EnsureDataDirectory();
        Directory.CreateDirectory(TlsDirectory);
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, fileOptions);
        stream.Write(content);
    }
}
