namespace BorgStudio.Server.Infrastructure.Tls;

/// <summary>
/// Where the server listens, from the "BorgStudio" configuration section (borgstudio-server.json,
/// environment variables such as <c>BorgStudio__Http__Enabled=true</c>).
/// Defaults: HTTPS on 8443, HTTP off.
/// </summary>
public sealed class ServerEndpointOptions
{
    public const string SectionName = "BorgStudio";

    public HttpsEndpointOptions Https { get; set; } = new();

    public HttpEndpointOptions Http { get; set; } = new();

    /// <summary>
    /// Trust X-Forwarded-For/-Proto/-Host from any sender. Only for a server that is reachable through
    /// its reverse proxy alone.
    /// </summary>
    public bool ForwardedHeaders { get; set; }
}

public sealed class HttpsEndpointOptions
{
    public bool Enabled { get; set; } = true;

    public int Port { get; set; } = 8443;

    /// <summary>A .pfx/.p12 file, or a PEM certificate (then <see cref="CertificateKeyPath"/> names the key).</summary>
    public string? CertificatePath { get; set; }

    /// <summary>The password of a .pfx/.p12 file.</summary>
    public string? CertificatePassword { get; set; }

    /// <summary>The unencrypted PEM private key that belongs to a PEM certificate.</summary>
    public string? CertificateKeyPath { get; set; }
}

public sealed class HttpEndpointOptions
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = 8080;
}
