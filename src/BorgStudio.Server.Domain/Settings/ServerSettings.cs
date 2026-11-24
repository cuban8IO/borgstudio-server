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
