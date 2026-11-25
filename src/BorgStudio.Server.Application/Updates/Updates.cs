using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Application.Abstractions.Updates;
using BorgStudio.Server.Application.Users;
using BorgStudio.Server.Domain;
using BorgStudio.Server.Domain.Changelogs;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Updates;

/// <summary>What the last update check found; kept in memory, the check runs daily and at start.</summary>
public sealed class UpdateStatusStore
{
    private readonly Lock gate = new();
    private UpdateStatus status = UpdateStatus.NotChecked;

    public UpdateStatus Current
    {
        get
        {
            lock (gate)
            {
                return status;
            }
        }

        set
        {
            lock (gate)
            {
                status = value;
            }
        }
    }
}

/// <param name="Newer">Releases newer than the running version, newest first.</param>
/// <param name="Failed">The last check could not reach GitHub.</param>
public sealed record UpdateStatus(DateTimeOffset? CheckedAt, IReadOnlyList<PublishedRelease> Newer, bool Failed)
{
    public static readonly UpdateStatus NotChecked = new(null, [], false);

    public PublishedRelease? Latest => Newer.Count > 0 ? Newer[0] : null;
}

/// <summary>Looks for newer releases on GitHub (when the setting allows it). Run by the daily background check.</summary>
public sealed record RunUpdateCheckCommand : ICommand<UpdateStatus>;

internal sealed class RunUpdateCheckCommandHandler(
    IServerSettingsStore settings,
    IServerEnvironment environment,
    IReleaseFeed feed,
    UpdateStatusStore store,
    TimeProvider timeProvider,
    ILogger<RunUpdateCheckCommandHandler> logger) : ICommandHandler<RunUpdateCheckCommand, UpdateStatus>
{
    public async Task<UpdateStatus> HandleAsync(RunUpdateCheckCommand command, CancellationToken cancellationToken)
    {
        var running = ProductVersion.Parse(environment.InformationalVersion).Version;
        var developmentBuild = Changelog.ParseDevelopmentBuild(running);
        var current = developmentBuild ?? Changelog.ParseVersion(running);
        if (!(await settings.GetAsync(cancellationToken)).CheckForUpdates || current is null)
        {
            // Switched off, or a local build without a release version to compare.
            return store.Current = UpdateStatus.NotChecked;
        }

        try
        {
            // A development build looks for the next development build, a release for newer releases.
            List<PublishedRelease> newer;
            if (developmentBuild is not null)
            {
                newer = await feed.GetDevelopmentBuildAsync(cancellationToken) is { } build && build.Version > current ? [build] : [];
            }
            else
            {
                var releases = await feed.GetReleasesAsync(cancellationToken);
                newer = [.. releases.Where(r => r.Version > current).OrderByDescending(r => r.Version)];
            }

            if (newer.Count > 0)
            {
                logger.LogInformation("BorgStudio Server {Version} is available (running {Current}).", newer[0].Label, running);
            }

            return store.Current = new UpdateStatus(timeProvider.GetUtcNow(), newer, false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or FormatException
            or System.Text.Json.JsonException)
        {
            logger.LogWarning("Checking for updates failed: {Message}", exception.Message);
            var previous = store.Current;
            return store.Current = previous with { CheckedAt = timeProvider.GetUtcNow(), Failed = true };
        }
    }
}

/// <summary>"Check now" on the settings page.</summary>
public sealed record CheckForUpdatesNowCommand([property: Required] string ActingUserId) : ICommand<UpdateStatus>;

internal sealed class CheckForUpdatesNowCommandHandler(UserAdministration admin, IDispatcher dispatcher)
    : ICommandHandler<CheckForUpdatesNowCommand, UpdateStatus>
{
    public async Task<UpdateStatus> HandleAsync(CheckForUpdatesNowCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        return await dispatcher.SendAsync(new RunUpdateCheckCommand(), cancellationToken);
    }
}

public sealed record GetUpdateStatusQuery([property: Required] string ActingUserId) : IQuery<UpdateStatus>;

internal sealed class GetUpdateStatusQueryHandler(UserAdministration admin, UpdateStatusStore store)
    : IQueryHandler<GetUpdateStatusQuery, UpdateStatus>
{
    public async Task<UpdateStatus> HandleAsync(GetUpdateStatusQuery query, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        return store.Current;
    }
}

/// <summary>
/// The hints an administrator gets: "updated to x.y.z" once after an update, and "x.y.z is available".
/// Other users get none. The first time an administrator signs in, the running version counts as seen.
/// </summary>
public sealed record GetUpdateNoticesCommand([property: Required] string UserId) : ICommand<UpdateNotices>;

public sealed record UpdateNotices(string? UpdatedTo, string? Available)
{
    public static readonly UpdateNotices None = new(null, null);
}

internal sealed class GetUpdateNoticesCommandHandler(
    IAccountService accounts,
    IServerEnvironment environment,
    UpdateStatusStore store) : ICommandHandler<GetUpdateNoticesCommand, UpdateNotices>
{
    public async Task<UpdateNotices> HandleAsync(GetUpdateNoticesCommand command, CancellationToken cancellationToken)
    {
        var account = await accounts.GetAsync(command.UserId, cancellationToken);
        if (account is null || !account.IsAdministrator)
        {
            return UpdateNotices.None;
        }

        var running = ProductVersion.Parse(environment.InformationalVersion);
        string? updatedTo = null;
        if (!running.IsDevelopmentBuild)
        {
            if (account.ChangelogSeenVersion is null)
            {
                await accounts.SetChangelogSeenVersionAsync(account.Id, running.Version, cancellationToken);
            }
            else if (account.ChangelogSeenVersion != running.Version)
            {
                updatedTo = running.Version;
            }
        }

        return new UpdateNotices(updatedTo, store.Current.Latest?.Label);
    }
}

/// <summary>The administrator has seen what changed in the running version.</summary>
public sealed record DismissUpdateNoticeCommand([property: Required] string UserId) : ICommand;

internal sealed class DismissUpdateNoticeCommandHandler(IAccountService accounts, IServerEnvironment environment)
    : ICommandHandler<DismissUpdateNoticeCommand>
{
    public Task HandleAsync(DismissUpdateNoticeCommand command, CancellationToken cancellationToken) =>
        accounts.SetChangelogSeenVersionAsync(
            command.UserId, ProductVersion.Parse(environment.InformationalVersion).Version, cancellationToken);
}

/// <summary>The changelog of the running build, plus, for administrators, the releases that are newer.</summary>
public sealed record GetChangelogQuery([property: Required] string UserId) : IQuery<ChangelogView>;

public sealed record ChangelogView(
    ProductVersion Running,
    IReadOnlyList<ChangelogRelease> Releases,
    AvailableUpdate? Update);

/// <param name="Latest">The newest version, e.g. "0.5.0" or "0.5.0-dev.42".</param>
/// <param name="Releases">What the newer releases bring, newest first.</param>
public sealed record AvailableUpdate(
    string Latest,
    string Url,
    IReadOnlyList<ChangelogRelease> Releases,
    InstallationKind Installation);

internal sealed class GetChangelogQueryHandler(
    IAccountService accounts,
    IServerEnvironment environment,
    IChangelogSource changelog,
    IInstallationInfo installation,
    UpdateStatusStore store) : IQueryHandler<GetChangelogQuery, ChangelogView>
{
    public async Task<ChangelogView> HandleAsync(GetChangelogQuery query, CancellationToken cancellationToken)
    {
        var running = ProductVersion.Parse(environment.InformationalVersion);
        var releases = Changelog.Parse(changelog.Read());

        AvailableUpdate? update = null;
        var account = await accounts.GetAsync(query.UserId, cancellationToken);
        if (account?.IsAdministrator == true && store.Current.Latest is { } latest)
        {
            var newer = store.Current.Newer
                .Select(r => new ChangelogRelease(r.Version, DateOnly.FromDateTime(r.PublishedAt.UtcDateTime), Changelog.ParseNotes(r.Notes), r.Name))
                .ToList();
            update = new AvailableUpdate(latest.Label, latest.Url, newer, installation.Kind);
        }

        return new ChangelogView(running, releases, update);
    }
}

/// <summary>Which builds this server follows: "stable" (releases) or "dev" (development builds).</summary>
public sealed record GetUpdateChannelQuery : IQuery<UpdateChannel>;

/// <param name="Name">"stable" or "dev".</param>
public sealed record UpdateChannel(string Name, InstallationKind Installation)
{
    public bool IsDevelopment => Name == "dev";
}

internal sealed class GetUpdateChannelQueryHandler(IServerEnvironment environment, IInstallationInfo installation)
    : IQueryHandler<GetUpdateChannelQuery, UpdateChannel>
{
    public Task<UpdateChannel> HandleAsync(GetUpdateChannelQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(new UpdateChannel(
            Changelog.ParseDevelopmentBuild(ProductVersion.Parse(environment.InformationalVersion).Version) is null ? "stable" : "dev",
            installation.Kind));
}
