using System.Text.RegularExpressions;

namespace BorgStudio.Server.Domain.Storage;

/// <summary>
/// Names and folders of repositories. A repository is shown as "owner/name" and lives in the
/// folder &lt;datastore&gt;/&lt;owner&gt;/&lt;name&gt;, so an administrator can find it on disk.
/// </summary>
public static partial class RepositoryNames
{
    public const int MaxLength = 64;

    /// <summary>The folder of a datastore that holds deleted repositories.</summary>
    public const string TrashFolder = ".trash";

    /// <summary>Letters, digits, '.', '-' and '_', starting with a letter or digit, so it is a safe folder name everywhere.</summary>
    public static bool IsValid(string? name) => name is not null && ValidName().IsMatch(name);

    /// <summary>Names are unique per owner regardless of case, so they also work on case-insensitive file systems.</summary>
    public static bool AreSame(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The owner's folder. User names may contain characters like '.', '@' and '+'; a leading dot
    /// would make the folder hidden (or "." / ".."), so such names get a '_' in front.
    /// </summary>
    public static string OwnerFolder(string userName) => userName.StartsWith('.') ? "_" + userName : userName;

    public static string RelativePath(string ownerName, string name) => $"{OwnerFolder(ownerName)}/{name}";

    public static string TrashPath(Guid repositoryId) => $"{TrashFolder}/{repositoryId:N}";

    /// <summary>The folder of a repository on an external datastore: its id, right in the account's home.</summary>
    public static string IdFolder(Guid repositoryId) => repositoryId.ToString("N");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex ValidName();
}
