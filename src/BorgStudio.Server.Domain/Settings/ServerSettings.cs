namespace BorgStudio.Server.Domain.Settings;

/// <summary>Settings of this server that administrators can change. There is exactly one row.</summary>
public sealed class ServerSettings
{
    public const int SingletonId = 1;

    public const string DefaultServerName = "BorgStudio Server";

    public const int MaxServerNameLength = 64;

    public int Id { get; private set; } = SingletonId;

    /// <summary>Name shown in the app bar and the browser tab, e.g. "Backups at home".</summary>
    public string ServerName { get; private set; } = DefaultServerName;

    public void Rename(string? serverName)
    {
        var name = serverName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ServerName = DefaultServerName;
            return;
        }

        if (name.Length > MaxServerNameLength)
        {
            throw new ArgumentException($"The server name may have at most {MaxServerNameLength} characters.", nameof(serverName));
        }

        ServerName = name;
    }

    /// <summary>Look for new releases on GitHub once a day (administrators see a hint).</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Apps (and API tokens) may see who else uses a repository and when it was last accessed.
    /// Off: they only learn their own access. Repositories can switch it off on their own too.
    /// </summary>
    public bool ShowParticipantsInApps { get; set; } = true;

    public const int MaxPublicUrlLength = 256;

    /// <summary>
    /// The address users reach the server at, e.g. <c>https://backup.example.org/</c>. Used for links
    /// such as invitations; needed when the server runs behind a reverse proxy. Without it, links use
    /// the address the administrator is using.
    /// </summary>
    public string? PublicUrl { get; private set; }

    public void SetPublicUrl(string? publicUrl)
    {
        if (!TryNormalizePublicUrl(publicUrl, out var normalized))
        {
            throw new ArgumentException("The public address must be an absolute http or https address.", nameof(publicUrl));
        }

        PublicUrl = normalized;
    }

    public const int MaxSshAddressLength = 256;

    /// <summary>
    /// Where clients reach the SSH service for borg, as "host" or "host:port", e.g. when a router
    /// forwards another port. Without it, clients use the host they paired with and the configured port.
    /// </summary>
    public string? SshPublicAddress { get; private set; }

    public void SetSshPublicAddress(string? address)
    {
        if (!TryParseSshAddress(address, out var host, out var port))
        {
            throw new ArgumentException("The SSH address must be a host name or address, optionally with a port.", nameof(address));
        }

        SshPublicAddress = host is null ? null : port is null ? host : $"{(host.Contains(':') ? $"[{host}]" : host)}:{port}";
    }

    /// <summary>Reads "host", "host:port", "[v6]" or "[v6]:port"; an empty value is valid and means "automatic".</summary>
    public static bool TryParseSshAddress(string? value, out string? host, out int? port)
    {
        host = null;
        port = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (text.Length > MaxSshAddressLength || text.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        string hostPart;
        string? portPart = null;
        if (text.StartsWith('['))
        {
            var end = text.IndexOf(']');
            if (end < 0)
            {
                return false;
            }

            hostPart = text[1..end];
            var rest = text[(end + 1)..];
            if (rest.Length > 0)
            {
                if (!rest.StartsWith(':'))
                {
                    return false;
                }

                portPart = rest[1..];
            }
        }
        else if (text.Count(c => c == ':') == 1)
        {
            hostPart = text[..text.IndexOf(':')];
            portPart = text[(text.IndexOf(':') + 1)..];
        }
        else
        {
            hostPart = text;
        }

        if (Uri.CheckHostName(hostPart) == UriHostNameType.Unknown)
        {
            return false;
        }

        if (portPart is not null)
        {
            if (!int.TryParse(portPart, out var number) || number is < 1 or > 65535)
            {
                return false;
            }

            port = number;
        }

        host = hostPart;
        return true;
    }

    /// <summary>An absolute http(s) address ending in a slash, or <c>null</c> for an empty value.</summary>
    public static bool TryNormalizePublicUrl(string? value, out string? normalized)
    {
        normalized = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        if (text.Length > MaxPublicUrlLength
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        normalized = uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        return true;
    }
}
