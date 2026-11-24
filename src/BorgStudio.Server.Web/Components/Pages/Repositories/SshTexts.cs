using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Web.Resources;

namespace BorgStudio.Server.Web.Components.Pages.Repositories;

/// <summary>How the pages describe the state of the SSH service.</summary>
internal static class SshTexts
{
    public static string State(SshEndpoint endpoint) => endpoint.State switch
    {
        SshServiceState.Running => Strings.SshStateRunning,
        SshServiceState.Starting => Strings.SshStateStarting,
        SshServiceState.Disabled => Strings.SshStateDisabled,
        SshServiceState.Unsupported => Strings.RepositoriesNeedLinux,
        _ => string.Format(Strings.SshStateFailed, endpoint.Message),
    };
}
