using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using BorgStudio.Server.Application.Abstractions.Tls;
using BorgStudio.Server.Infrastructure;
using BorgStudio.Server.Infrastructure.Tls;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Tests.Infrastructure;

public sealed class TlsCertificateStoreTests : IDisposable
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "borgstudio-server-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SelfSigned_IsCreatedOnce_AndKept()
    {
        var first = CreateStore().Current!;

        Assert.Equal(TlsCertificateSource.SelfSigned, first.Source);
        Assert.Contains("localhost", first.Names);
        Assert.Contains("127.0.0.1", first.Names);
        Assert.True(first.NotAfter > DateTimeOffset.UtcNow.AddYears(4));
        Assert.True(File.Exists(Path.Combine(dataDirectory, "tls", "self-signed.pfx")));

        Assert.Equal(first.Thumbprint, CreateStore().Current!.Thumbprint);

        // The SHA-256 fingerprint the BorgStudio app asks to compare: 32 bytes as "AB:CD:…".
        Assert.Matches("^([0-9A-F]{2}:){31}[0-9A-F]{2}$", first.Sha256Fingerprint);
    }

    [Fact]
    public async Task Pfx_WithPassword_IsUsed_UntilRemoved()
    {
        var store = CreateStore();
        var pfx = ExportWithPassword(NewCertificate(), "secret");

        Assert.Equal(TlsCertificateError.Unreadable, await store.UploadPfxAsync(pfx, "wrong", CancellationToken.None));
        Assert.Equal(TlsCertificateError.None, await store.UploadPfxAsync(pfx, "secret", CancellationToken.None));
        Assert.Equal(TlsCertificateSource.Uploaded, store.Current!.Source);
        Assert.Equal("CN=backup.example.org", store.Current!.Subject);
        Assert.True(store.Certificate.HasPrivateKey);

        // Survives a restart.
        Assert.Equal(TlsCertificateSource.Uploaded, CreateStore().Current!.Source);

        await store.RemoveUploadedAsync(CancellationToken.None);
        Assert.Equal(TlsCertificateSource.SelfSigned, store.Current!.Source);
    }

    [Fact]
    public async Task Pem_CertificateAndKey_IsUsed()
    {
        var store = CreateStore();
        using var key = RSA.Create(2048);
        using var certificate = NewCertificate(key);

        var error = await store.UploadPemAsync(certificate.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem(), CancellationToken.None);

        Assert.Equal(TlsCertificateError.None, error);
        Assert.Equal(certificate.Thumbprint, store.Current!.Thumbprint);
        Assert.True(store.Certificate.HasPrivateKey);
    }

    [Fact]
    public async Task Uploads_WithoutKey_OrExpired_OrGarbage_AreRefused()
    {
        var store = CreateStore();
        using var withKey = NewCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        using var expired = NewCertificate(notAfter: DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(TlsCertificateError.NoPrivateKey,
            await store.UploadPfxAsync(publicOnly.Export(X509ContentType.Pkcs12), null, CancellationToken.None));
        Assert.Equal(TlsCertificateError.Expired,
            await store.UploadPfxAsync(expired.Export(X509ContentType.Pkcs12), null, CancellationToken.None));
        Assert.Equal(TlsCertificateError.Unreadable,
            await store.UploadPfxAsync("not a certificate"u8.ToArray(), null, CancellationToken.None));
        Assert.Equal(TlsCertificateError.Unreadable,
            await store.UploadPemAsync("-----BEGIN CERTIFICATE-----", "nonsense", CancellationToken.None));

        Assert.Equal(TlsCertificateSource.SelfSigned, store.Current!.Source);
    }

    [Fact]
    public void ConfiguredPfx_IsUsed()
    {
        Directory.CreateDirectory(dataDirectory);
        var file = Path.Combine(dataDirectory, "configured.pfx");
        File.WriteAllBytes(file, ExportWithPassword(NewCertificate(), "pw"));

        var store = CreateStore(new HttpsEndpointOptions { CertificatePath = file, CertificatePassword = "pw" });

        Assert.Equal(TlsCertificateSource.Configured, store.Current!.Source);
    }

    [Fact]
    public void BrokenConfiguration_FallsBackToSelfSigned()
    {
        var store = CreateStore(new HttpsEndpointOptions { CertificatePath = Path.Combine(dataDirectory, "missing.pfx") });

        Assert.Equal(TlsCertificateSource.SelfSigned, store.Current!.Source);
    }

    [Fact]
    public void SelfSigned_NamesTheGivenAddresses()
    {
        var pfx = TlsCertificateStore.CreateSelfSigned("nas", [IPAddress.Parse("192.168.1.20")], DateTimeOffset.UtcNow);
        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
        var names = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();

        Assert.Contains("nas", names.EnumerateDnsNames());
        Assert.Contains(IPAddress.Parse("192.168.1.20"), names.EnumerateIPAddresses());
        Assert.True(certificate.HasPrivateKey);
    }

    [Fact]
    public async Task HttpsOff_CreatesNoCertificate_ButTakesAnUpload()
    {
        var store = CreateStore(new HttpsEndpointOptions { Enabled = false });

        Assert.False(store.HttpsEnabled);
        Assert.Null(store.Current);
        Assert.False(Directory.Exists(Path.Combine(dataDirectory, "tls")));

        Assert.Equal(TlsCertificateError.None,
            await store.UploadPfxAsync(ExportWithPassword(NewCertificate(), "pw"), "pw", CancellationToken.None));
        Assert.Equal(TlsCertificateSource.Uploaded, store.Current!.Source);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private TlsCertificateStore CreateStore(HttpsEndpointOptions? https = null) => new(
        new ServerPaths(dataDirectory),
        Options.Create(new ServerEndpointOptions { Https = https ?? new HttpsEndpointOptions() }),
        TimeProvider.System,
        NullLogger<TlsCertificateStore>.Instance);

    private static X509Certificate2 NewCertificate(RSA? key = null, DateTimeOffset? notAfter = null)
    {
        using var ownKey = key is null ? RSA.Create(2048) : null;
        var request = new CertificateRequest("CN=backup.example.org", key ?? ownKey!, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var end = notAfter ?? DateTimeOffset.UtcNow.AddYears(1);
        return request.CreateSelfSigned(end.AddYears(-1), end);
    }

    private static byte[] ExportWithPassword(X509Certificate2 certificate, string password)
    {
        using (certificate)
        {
            return certificate.Export(X509ContentType.Pkcs12, password);
        }
    }
}
