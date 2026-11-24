using System.Security.Claims;

using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Access;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Access;
using BorgStudio.Server.Application.Settings;
using BorgStudio.Server.Application.Storage;
using BorgStudio.Server.Domain;
using BorgStudio.Server.Domain.Storage;
using BorgStudio.Server.Web.Authentication;

using Microsoft.AspNetCore.Http.HttpResults;

namespace BorgStudio.Server.Web.Api;

/// <summary>
/// The interface the BorgStudio app uses (JSON over HTTPS, version 1): pairing, the repositories it
/// can back up to, registering its SSH key, and statistics. Only what backing up needs: deleting,
/// sharing and quotas stay in the web interface, so a lost token cannot delete anything.
/// </summary>
internal static class ApiEndpoints
{
    public const string Prefix = "/api/v1";

    /// <summary>How often an app should ask whether the user confirmed the pairing.</summary>
    private const int PairingPollSeconds = 3;

    public static IEndpointRouteBuilder MapApiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup(Prefix);

        var pairing = api.MapGroup("/pairing").AllowAnonymous();
        pairing.MapPost("/", StartPairingAsync);
        pairing.MapPost("/complete", CompletePairingAsync);

        var secured = api.MapGroup(string.Empty).RequireAuthorization(Policies.Api);
        secured.MapGet("/server", GetServerAsync);
        secured.MapGet("/datastores", GetDatastoresAsync);
        secured.MapGet("/repositories", GetRepositoriesAsync);
        secured.MapPost("/repositories", CreateRepositoryAsync);
        secured.MapGet("/repositories/{id:guid}", GetRepositoryAsync);
        secured.MapPut("/repositories/{id:guid}/ssh-key", SetSshKeyAsync);

        // Unknown paths below /api answer in JSON too, not with the HTML page for "not found".
        api.MapFallback(() => TypedResults.Json(new ApiError("not_found", "There is no such interface."), statusCode: StatusCodes.Status404NotFound))
            .AllowAnonymous();
        return endpoints;
    }

    public sealed record StartPairingRequest(string DeviceName, string? Platform, string? AppVersion);

    /// <param name="VerificationUrl">The page the user opens (with the code filled in).</param>
    public sealed record StartPairingResponse(
        string PairingCode, string UserCode, string VerificationUrl, string VerificationUrlComplete, DateTimeOffset ExpiresAt, int Interval);

    private static async Task<Results<Ok<StartPairingResponse>, JsonHttpResult<ApiError>>> StartPairingAsync(
        StartPairingRequest request, IDispatcher dispatcher, HttpContext context)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceName) || request.DeviceName.Length > 100)
        {
            return TypedResults.Json(new ApiError("invalid_request", "deviceName is required (at most 100 characters)."), statusCode: 400);
        }

        var started = await dispatcher.SendAsync(new StartPairingCommand(request.DeviceName, Cut(request.Platform, 100), Cut(request.AppVersion, 50)));
        var baseUrl = await BaseUrlAsync(dispatcher, context);
        return TypedResults.Ok(new StartPairingResponse(
            started.PairingCode,
            started.UserCode,
            $"{baseUrl}pair",
            $"{baseUrl}pair?code={Uri.EscapeDataString(started.UserCode)}",
            started.ExpiresAt,
            PairingPollSeconds));
    }

    public sealed record CompletePairingRequest(string PairingCode);

    /// <param name="State">"pending", "approved", "denied" or "expired".</param>
    public sealed record CompletePairingResponse(string State, string? Token = null, Guid? DeviceId = null, string? UserName = null);

    private static async Task<JsonHttpResult<CompletePairingResponse>> CompletePairingAsync(CompletePairingRequest request, IDispatcher dispatcher)
    {
        if (string.IsNullOrWhiteSpace(request.PairingCode))
        {
            return TypedResults.Json(new CompletePairingResponse("expired"), statusCode: StatusCodes.Status410Gone);
        }

        var outcome = await dispatcher.SendAsync(new CompletePairingCommand(request.PairingCode));
        return outcome.State switch
        {
            PairingState.Approved => TypedResults.Json(new CompletePairingResponse("approved", outcome.Token, outcome.DeviceId, outcome.UserName)),
            PairingState.Pending => TypedResults.Json(new CompletePairingResponse("pending"), statusCode: StatusCodes.Status202Accepted),
            PairingState.Denied => TypedResults.Json(new CompletePairingResponse("denied"), statusCode: StatusCodes.Status403Forbidden),
            _ => TypedResults.Json(new CompletePairingResponse("expired"), statusCode: StatusCodes.Status410Gone),
        };
    }

    public sealed record SshInfo(string Host, int Port, string User, string State, string? Message, IReadOnlyList<SshHostKey> HostKeys);

    public sealed record ServerResponse(string Name, string Version, string UserName, Guid DeviceId, SshInfo Ssh);

    private static async Task<Ok<ServerResponse>> GetServerAsync(IDispatcher dispatcher, IServerEnvironment environment, HttpContext context)
    {
        var settings = await dispatcher.QueryAsync(new GetServerSettingsQuery());
        var endpoint = await dispatcher.QueryAsync(new GetSshEndpointQuery(context.Request.Host.Host));
        return TypedResults.Ok(new ServerResponse(
            settings.ServerName,
            ProductVersion.Parse(environment.InformationalVersion).Version,
            context.User.Identity?.Name ?? string.Empty,
            DeviceId(context.User),
            Ssh(endpoint)));
    }

    public sealed record DatastoreResponse(Guid Id, string Name, long? FreeBytes);

    public sealed record DatastoresResponse(bool MayCreate, long UsedBytes, long? QuotaBytes, IReadOnlyList<DatastoreResponse> Datastores);

    private static async Task<Ok<DatastoresResponse>> GetDatastoresAsync(IDispatcher dispatcher, HttpContext context)
    {
        var view = await dispatcher.QueryAsync(new GetRepositoriesQuery(UserId(context.User)));
        return TypedResults.Ok(new DatastoresResponse(
            view.Allowance.MayCreate,
            view.Allowance.UsedBytes,
            view.Allowance.QuotaBytes,
            [.. view.Datastores.Select(d => new DatastoreResponse(d.Id, d.Name, d.FreeBytes))]));
    }

    private static async Task<Ok<IReadOnlyList<RepositoryResponse>>> GetRepositoriesAsync(IDispatcher dispatcher, HttpContext context)
    {
        var stats = await dispatcher.QueryAsync(new GetRepositoryStatsQuery(UserId(context.User), context.Request.Host.Host));
        return TypedResults.Ok<IReadOnlyList<RepositoryResponse>>([.. stats.Select(RepositoryResponse.From)]);
    }

    private static async Task<Results<Ok<RepositoryResponse>, JsonHttpResult<ApiError>>> GetRepositoryAsync(
        Guid id, IDispatcher dispatcher, HttpContext context)
    {
        var stats = await dispatcher.QueryAsync(new GetRepositoryStatsQuery(UserId(context.User), context.Request.Host.Host, id));
        return stats.Count == 1
            ? TypedResults.Ok(RepositoryResponse.From(stats[0]))
            : TypedResults.Json(new ApiError("not_found", "The repository does not exist or this account has no access to it."), statusCode: 404);
    }

    public sealed record CreateRepositoryRequest(string Name, Guid? DatastoreId, long? QuotaBytes);

    private static async Task<Results<Created<RepositoryResponse>, JsonHttpResult<ApiError>>> CreateRepositoryAsync(
        CreateRepositoryRequest request, IDispatcher dispatcher, HttpContext context)
    {
        var userId = UserId(context.User);
        var created = await dispatcher.SendAsync(new CreateRepositoryCommand(userId, request.Name ?? string.Empty, request.DatastoreId, request.QuotaBytes));
        if (!created.Succeeded)
        {
            return TypedResults.Json(new ApiError("rejected", string.Join(" ", created.Errors)), statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var stats = await dispatcher.QueryAsync(new GetRepositoryStatsQuery(userId, context.Request.Host.Host, created.Value));
        return TypedResults.Created($"{Prefix}/repositories/{created.Value}", RepositoryResponse.From(stats[0]));
    }

    public sealed record SetSshKeyRequest(string PublicKey);

    /// <param name="Url">The borg repository address to use with this key.</param>
    public sealed record SetSshKeyResponse(string Url, SshInfo Ssh);

    private static async Task<Results<Ok<SetSshKeyResponse>, JsonHttpResult<ApiError>>> SetSshKeyAsync(
        Guid id, SetSshKeyRequest request, IDispatcher dispatcher, HttpContext context)
    {
        var result = await dispatcher.SendAsync(new SetDeviceKeyCommand(
            UserId(context.User), DeviceId(context.User), id, request.PublicKey ?? string.Empty, context.Request.Host.Host));
        return result.Succeeded
            ? TypedResults.Ok(new SetSshKeyResponse(result.Value!.Url, Ssh(result.Value.Endpoint)))
            : TypedResults.Json(new ApiError("rejected", string.Join(" ", result.Errors)), statusCode: StatusCodes.Status422UnprocessableEntity);
    }

    /// <param name="Access">"full" or "appendOnly".</param>
    public sealed record RepositoryResponse(
        Guid Id,
        string Owner,
        string Name,
        string FullName,
        string Access,
        bool IsOwner,
        string Url,
        long UsedBytes,
        long? QuotaBytes,
        DateTimeOffset? UsageMeasuredAt,
        string Datastore,
        long? DatastoreFreeBytes,
        OwnerQuotaResponse? OwnerQuota,
        LastAccessResponse? LastAccess,
        IReadOnlyList<ParticipantResponse> Participants,
        bool IsMoving)
    {
        public static RepositoryResponse From(RepositoryStats stats) => new(
            stats.Id,
            stats.OwnerName,
            stats.Name,
            stats.FullName,
            AccessName(stats.Access),
            stats.IsOwner,
            stats.Url,
            stats.UsedBytes,
            stats.QuotaBytes,
            stats.UsageMeasuredAt,
            stats.DatastoreName,
            stats.DatastoreFreeBytes,
            stats.OwnerQuota is { } q ? new OwnerQuotaResponse(q.UsedBytes, q.QuotaBytes!.Value) : null,
            stats.LastAccess is { } a ? new LastAccessResponse(a.At, a.UserName, a.By) : null,
            [.. stats.Participants.Select(p => new ParticipantResponse(p.UserName, AccessName(p.Access), p.IsOwner))],
            stats.IsMoving);
    }

    public sealed record OwnerQuotaResponse(long UsedBytes, long QuotaBytes);

    public sealed record LastAccessResponse(DateTimeOffset At, string UserName, string By);

    public sealed record ParticipantResponse(string UserName, string Access, bool IsOwner);

    private static string AccessName(RepositoryAccess access) => access == RepositoryAccess.AppendOnly ? "appendOnly" : "full";

    private static SshInfo Ssh(SshEndpoint endpoint) => new(
        endpoint.Host,
        endpoint.Port,
        endpoint.UserName,
        endpoint.State.ToString().ToLowerInvariant(),
        endpoint.Message,
        endpoint.HostKeys);

    private static string UserId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static Guid DeviceId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ApiTokenAuthenticationHandler.DeviceClaim), out var id) ? id : Guid.Empty;

    private static async Task<string> BaseUrlAsync(IDispatcher dispatcher, HttpContext context) =>
        (await dispatcher.QueryAsync(new GetServerSettingsQuery())).PublicUrl
        ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/";

    private static string? Cut(string? value, int max) => value is null ? null : value.Length > max ? value[..max] : value;
}
