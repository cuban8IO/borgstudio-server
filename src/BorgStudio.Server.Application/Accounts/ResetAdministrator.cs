using System.Security.Cryptography;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Domain.Users;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Accounts;

/// <summary>
/// Emergency access from the server's console: gives an administrator a temporary password, turns
/// two-factor authentication off, lifts a lockout and enables the account again. At the next sign-in the administrator sets a new
/// password, is offered two-factor authentication again and gets new recovery codes.
/// </summary>
/// <param name="UserName">The administrator; may be left out when there is only one.</param>
public sealed record ResetAdministratorCommand(string? UserName) : ICommand<ResetAdministratorResult>;

public sealed record ResetAdministratorResult(
    ResetAdministratorStatus Status,
    string? UserName,
    string? TemporaryPassword,
    IReadOnlyList<string> Administrators);

public enum ResetAdministratorStatus
{
    Reset,
    NoAdministrators,
    NameRequired,
    NotAnAdministrator,
}

internal sealed class ResetAdministratorCommandHandler(
    IAccountService accounts,
    ILogger<ResetAdministratorCommandHandler> logger) : ICommandHandler<ResetAdministratorCommand, ResetAdministratorResult>
{
    public async Task<ResetAdministratorResult> HandleAsync(ResetAdministratorCommand command, CancellationToken cancellationToken)
    {
        var administrators = await accounts.GetInRoleAsync(Roles.Administrator, cancellationToken);
        var names = administrators.Select(a => a.UserName).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (administrators.Count == 0)
        {
            return new ResetAdministratorResult(ResetAdministratorStatus.NoAdministrators, null, null, names);
        }

        AccountInfo? account;
        if (string.IsNullOrWhiteSpace(command.UserName))
        {
            if (administrators.Count > 1)
            {
                return new ResetAdministratorResult(ResetAdministratorStatus.NameRequired, null, null, names);
            }

            account = administrators[0];
        }
        else
        {
            account = administrators.FirstOrDefault(
                a => string.Equals(a.UserName, command.UserName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (account is null)
            {
                return new ResetAdministratorResult(ResetAdministratorStatus.NotAnAdministrator, command.UserName, null, names);
            }
        }

        var password = TemporaryPassword.Generate();
        var result = await accounts.SetPasswordAsync(account.Id, password, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The temporary password was rejected: {string.Join(" ", result.Errors)}");
        }

        await accounts.SetTwoFactorEnabledAsync(account.Id, false, cancellationToken);
        await accounts.ResetAuthenticatorKeyAsync(account.Id, cancellationToken);
        await accounts.UnlockAsync(account.Id, cancellationToken);
        await accounts.SetDisabledAsync(account.Id, false, cancellationToken);
        await accounts.SetTasksAsync(
            account.Id,
            AccountTasks.ChangePassword | AccountTasks.OfferTwoFactor | AccountTasks.ShowRecoveryCodes,
            cancellationToken);
        await accounts.InvalidateSessionsAsync(account.Id, cancellationToken);

        logger.LogWarning("Administrator {UserName} was reset from the console.", account.UserName);
        return new ResetAdministratorResult(ResetAdministratorStatus.Reset, account.UserName, password, names);
    }
}

/// <summary>Random password that satisfies every password rule (upper, lower, digit, symbol).</summary>
internal static class TemporaryPassword
{
    private const string Upper = "ABCDEFGHJKMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghjkmnpqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+-=?@";
    private const int Length = 20;

    public static string Generate()
    {
        var all = Upper + Lower + Digits + Symbols;
        var characters = new List<char>
        {
            Pick(Upper),
            Pick(Lower),
            Pick(Digits),
            Pick(Symbols),
        };
        while (characters.Count < Length)
        {
            characters.Add(Pick(all));
        }

        var shuffled = characters.ToArray();
        RandomNumberGenerator.Shuffle(shuffled.AsSpan());
        return new string(shuffled);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}
