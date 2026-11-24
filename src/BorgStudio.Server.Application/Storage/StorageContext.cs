using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Domain.Storage;

namespace BorgStudio.Server.Application.Storage;

/// <summary>What every storage use case needs: data, folders, texts, and who may do what.</summary>
internal sealed class StorageContext(
    IStorageStore store,
    IAccountService accounts,
    IRepositoryFolders folders,
    IStorageTexts texts,
    TimeProvider timeProvider)
{
    public IStorageStore Store => store;

    public IAccountService Accounts => accounts;

    public IRepositoryFolders Folders => folders;

    public IStorageTexts Texts => texts;

    public DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>The acting user must exist and be enabled; anything else is a programming or forging error.</summary>
    public async Task<AccountInfo> RequireActingAsync(string actingUserId, CancellationToken cancellationToken)
    {
        var acting = await accounts.GetAsync(actingUserId, cancellationToken);
        return acting is { IsDisabled: false }
            ? acting
            : throw new UnauthorizedAccessException("Only enabled users may use repositories.");
    }

    public async Task<AccountInfo> RequireAdministratorAsync(string actingUserId, CancellationToken cancellationToken)
    {
        var acting = await RequireActingAsync(actingUserId, cancellationToken);
        return acting.IsAdministrator
            ? acting
            : throw new UnauthorizedAccessException("Only administrators may do this.");
    }

    /// <summary>A repository in use (not in the trash) that <paramref name="acting"/> may manage.</summary>
    public async Task<BackupRepository> RequireManagedAsync(AccountInfo acting, Guid repositoryId, CancellationToken cancellationToken)
    {
        var repository = await store.FindRepositoryAsync(repositoryId, cancellationToken);
        return repository is { IsDeleted: false } && CanManage(acting, repository)
            ? repository
            : throw new UnauthorizedAccessException("The repository does not exist or may not be managed by this user.");
    }

    /// <summary>Owners and administrators manage a repository: rename, quota, shares, delete.</summary>
    public static bool CanManage(AccountInfo acting, BackupRepository repository) =>
        acting.IsAdministrator || repository.OwnerId == acting.Id;

    public static bool CanSee(AccountInfo acting, BackupRepository repository) =>
        CanManage(acting, repository) || repository.AccessFor(acting.Id) is not null;

    public async Task<IReadOnlyDictionary<string, AccountInfo>> UsersByIdAsync(CancellationToken cancellationToken) =>
        (await accounts.ListAsync(cancellationToken)).ToDictionary(a => a.Id);

    public async Task<IReadOnlyDictionary<Guid, Datastore>> DatastoresByIdAsync(CancellationToken cancellationToken) =>
        (await store.GetDatastoresAsync(cancellationToken)).ToDictionary(d => d.Id);

    /// <summary>Whether <paramref name="ownerId"/> already has a repository (in use) called <paramref name="name"/>.</summary>
    public async Task<bool> NameTakenAsync(string ownerId, string name, Guid? except, CancellationToken cancellationToken) =>
        (await store.GetRepositoriesAsync(cancellationToken)).Any(r =>
            r.OwnerId == ownerId && !r.IsDeleted && r.Id != except && RepositoryNames.AreSame(r.Name, name));

    /// <summary>Space the repositories of a user take (those in the trash do not count).</summary>
    public static long UsedBy(IEnumerable<BackupRepository> repositories, string userId) =>
        repositories.Where(r => r.OwnerId == userId && !r.IsDeleted).Sum(r => r.UsedBytes);

    /// <summary>Runs a folder change; problems with the disk become a message instead of an error page.</summary>
    public string? TryFolder(Datastore datastore, string relativePath, Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Format(texts.FolderProblem, $"{datastore.Path}/{relativePath}") + " " + e.Message;
        }
    }
}
