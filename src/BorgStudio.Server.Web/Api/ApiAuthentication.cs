using System.Security.Claims;
using System.Text.Encodings.Web;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Access;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Web.Api;

/// <summary>
/// Sign-in for the interface: "Authorization: Bearer bss_…" with the token of a paired app or an
/// API token. Cookies do not count there, so pages and the interface cannot be mixed up.
/// </summary>
internal sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IDispatcher dispatcher) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiToken";

    public const string DeviceClaim = "bss:device";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header["Bearer ".Length..].Trim();
        var identity = token.Length == 0 ? null : await dispatcher.SendAsync(new AuthenticateTokenCommand(token), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail("The token is not valid.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.UserId),
            new Claim(ClaimTypes.Name, identity.UserName),
            new Claim(DeviceClaim, identity.DeviceId.ToString()),
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        WriteErrorAsync(StatusCodes.Status401Unauthorized, "unauthorized", "A valid token is needed: Authorization: Bearer <token>.");

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteErrorAsync(StatusCodes.Status403Forbidden, "forbidden", "The token may not do this.");

    private Task WriteErrorAsync(int status, string code, string message)
    {
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(new ApiError(code, message));
    }
}

/// <summary>Every error of the interface: a stable code for programs and a message for people.</summary>
public sealed record ApiError(string Error, string Message);
