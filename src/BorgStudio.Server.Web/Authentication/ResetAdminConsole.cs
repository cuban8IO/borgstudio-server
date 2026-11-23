using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Accounts;
using BorgStudio.Server.Infrastructure.Persistence;

namespace BorgStudio.Server.Web.Authentication;

/// <summary>
/// <c>BorgStudio.Server reset-admin [user name]</c>: emergency access when no administrator can sign in.
/// Run it on the server, as the account the server runs under (e.g. with <c>docker exec</c>).
/// </summary>
internal static class ResetAdminConsole
{
    public const string CommandName = "reset-admin";

    public static async Task<int> RunAsync(IServiceProvider services, string? userName, TextWriter output)
    {
        await DatabaseInitializer.InitializeAsync(services);
        var dispatcher = services.GetRequiredService<IDispatcher>();
        var result = await dispatcher.SendAsync(new ResetAdministratorCommand(userName));

        switch (result.Status)
        {
            case ResetAdministratorStatus.Reset:
                await output.WriteLineAsync($"Administrator \"{result.UserName}\" was reset.");
                await output.WriteLineAsync($"Temporary password: {result.TemporaryPassword}");
                await output.WriteLineAsync(
                    "Two-factor authentication is off and the account is unlocked. At the next sign-in a new "
                    + "password has to be set; new recovery codes are shown after that.");
                return 0;

            case ResetAdministratorStatus.NoAdministrators:
                await output.WriteLineAsync("There is no administrator yet. Open the web interface to run the setup.");
                return 1;

            case ResetAdministratorStatus.NameRequired:
                await output.WriteLineAsync("There are several administrators. Name the one to reset:");
                await WriteAdministratorsAsync(output, result.Administrators);
                return 1;

            default:
                await output.WriteLineAsync($"\"{userName}\" is not an administrator. Administrators:");
                await WriteAdministratorsAsync(output, result.Administrators);
                return 1;
        }
    }

    private static async Task WriteAdministratorsAsync(TextWriter output, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            await output.WriteLineAsync($"  {CommandName} {name}");
        }
    }
}
