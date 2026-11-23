using BorgStudio.Server.Application;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Infrastructure;
using BorgStudio.Server.Infrastructure.Persistence;
using BorgStudio.Server.Web.Authentication;
using BorgStudio.Server.Web.Components;
using BorgStudio.Server.Web.Localization;

using Microsoft.AspNetCore.Identity;

using MudBlazor.Services;

// "BorgStudio.Server reset-admin [user name]" runs a console command instead of the server.
var isResetAdmin = args.Length > 0 && args[0] == ResetAdminConsole.CommandName;

var builder = WebApplication.CreateBuilder(isResetAdmin ? [] : args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();
builder.Services.AddLocalization();
builder.Services.AddMemoryCache();

builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration)
    .AddSignInManager()
    .AddErrorDescriber<LocalizedIdentityErrorDescriber>();
builder.Services.AddSingleton<IAccountTexts, AccountTexts>();
builder.Services.AddSingleton<RecoveryCodeFlash>();
builder.Services.AddWebAuthentication(ServerPaths.FromConfiguration(builder.Configuration));

var app = builder.Build();

if (isResetAdmin)
{
    return await ResetAdminConsole.RunAsync(app.Services, args.Skip(1).FirstOrDefault(), Console.Out);
}

await DatabaseInitializer.InitializeAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseRequestLocalization(options => options
    .SetDefaultCulture(SupportedCultures.Default)
    .AddSupportedCultures(SupportedCultures.Names)
    .AddSupportedUICultures(SupportedCultures.Names));

app.UseMiddleware<SetupRedirectMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets().AllowAnonymous();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();
return 0;

/// <summary>Entry point; public so the integration tests can host the server.</summary>
public partial class Program;
