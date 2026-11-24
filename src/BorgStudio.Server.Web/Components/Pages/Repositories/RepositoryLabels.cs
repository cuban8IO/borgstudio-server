using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Web.Resources;

using MudBlazor;

namespace BorgStudio.Server.Web.Components.Pages.Repositories;

/// <summary>How the pages name access levels and roles.</summary>
internal static class RepositoryLabels
{
    public static string Access(RepositoryAccess access) => access switch
    {
        RepositoryAccess.AppendOnly => Strings.AccessAppendOnly,
        _ => Strings.AccessFull,
    };

    public static string AccessHint(RepositoryAccess access) => access switch
    {
        RepositoryAccess.AppendOnly => Strings.AccessAppendOnlyHint,
        _ => Strings.AccessFullHint,
    };

    /// <summary>The acting user's relation to the repository: owner, a share, or administration only.</summary>
    public static (string Text, Color Color) Role(RepositorySummary repository) =>
        repository.IsOwner ? (Strings.RepositoryRoleOwner, Color.Primary)
        : repository.Access is { } access ? (Access(access), Color.Info)
        : (Strings.RepositoryRoleAdministration, Color.Default);
}
