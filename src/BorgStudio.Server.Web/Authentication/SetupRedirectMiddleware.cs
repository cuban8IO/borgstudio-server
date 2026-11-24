using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Setup;

namespace BorgStudio.Server.Web.Authentication;

/// <summary>
/// While the server has no users, every page leads to the setup wizard; afterwards the wizard is gone.
/// Once setup is done this costs nothing: the answer is remembered.
/// </summary>
internal sealed class SetupRedirectMiddleware(RequestDelegate next)
{
    public const string SetupPath = "/setup";

    // The middleware lives as long as the server, so the remembered answer does too.
    private volatile bool setupDone;

    public async Task InvokeAsync(HttpContext context, IDispatcher dispatcher)
    {
        var path = context.Request.Path;
        // The interface answers for itself (without users nobody can pair or sign in anyway).
        if (IsAsset(path) || path.StartsWithSegments(Api.ApiEndpoints.Prefix))
        {
            await next(context);
            return;
        }

        if (!setupDone)
        {
            var state = await dispatcher.QueryAsync(new GetSetupStateQuery(), context.RequestAborted);
            setupDone = !state.IsRequired;
        }

        var isSetupPage = path.StartsWithSegments(SetupPath, StringComparison.OrdinalIgnoreCase);
        if (!setupDone && !isSetupPage)
        {
            context.Response.Redirect(SetupPath);
            return;
        }

        if (setupDone && isSetupPage)
        {
            context.Response.Redirect("/");
            return;
        }

        await next(context);
    }

    private static bool IsAsset(PathString path)
    {
        if (path.StartsWithSegments("/_framework") || path.StartsWithSegments("/_content") || path.StartsWithSegments("/_blazor"))
        {
            return true;
        }

        var value = path.Value ?? string.Empty;
        var lastSegment = value[(value.LastIndexOf('/') + 1)..];
        return lastSegment.Contains('.');
    }
}
