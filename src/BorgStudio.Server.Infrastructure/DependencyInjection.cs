using BorgStudio.Server.Application.Abstractions;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Settings;
using BorgStudio.Server.Application.Abstractions.Setup;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Application.Abstractions.Tls;
using BorgStudio.Server.Application.Abstractions.Updates;
using BorgStudio.Server.Domain.Users;
using BorgStudio.Server.Infrastructure.Identity;
using BorgStudio.Server.Infrastructure.Persistence;
using BorgStudio.Server.Infrastructure.Platform;
using BorgStudio.Server.Infrastructure.Setup;
using BorgStudio.Server.Infrastructure.Storage;
using BorgStudio.Server.Infrastructure.Tls;
using BorgStudio.Server.Infrastructure.Updates;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BorgStudio.Server.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the implementations of the application layer's interfaces: database, user accounts
    /// (ASP.NET Core Identity without sign-in; the web host adds cookies and the sign-in manager).
    /// </summary>
    public static IdentityBuilder AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var paths = ServerPaths.FromConfiguration(configuration);
        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IServerEnvironment, ServerEnvironment>();

        services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={paths.DatabaseFile}"));
        services.AddScoped<IServerSettingsStore, ServerSettingsStore>();
        services.AddSingleton<ISetupCodeStore, SetupCodeStore>();

        services.Configure<ServerEndpointOptions>(configuration.GetSection(ServerEndpointOptions.SectionName));
        services.AddSingleton<TlsCertificateStore>();
        services.AddSingleton<ITlsCertificateService>(provider => provider.GetRequiredService<TlsCertificateStore>());

        services.AddScoped<IStorageStore, StorageStore>();
        services.AddSingleton<IRepositoryFolders, RepositoryFolders>();
        services.AddHostedService<StorageWorker>();

        services.AddHttpClient<IReleaseFeed, GitHubReleaseFeed>(GitHubReleaseFeed.Configure);
        services.AddSingleton<IChangelogSource, EmbeddedChangelog>();
        services.AddSingleton<IInstallationInfo, InstallationInfo>();
        services.AddHostedService<UpdateCheckWorker>();

        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IInvitationStore, InvitationStore>();
        services.AddSingleton<IAuthenticatorPresenter, AuthenticatorPresenter>();

        return services
            .AddIdentityCore<ApplicationUser>(ConfigureIdentity)
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders()
            .AddUserConfirmation<AccountConfirmation>()
            .AddClaimsPrincipalFactory<AccountClaimsPrincipalFactory>();
    }

    private static void ConfigureIdentity(IdentityOptions options)
    {
        // Identity's default rules, at least 8 characters.
        options.Password.RequiredLength = PasswordPolicy.MinimumLength;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;

        // Wrong passwords and codes: 5 tries, then 15 minutes locked.
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);

        // Sign-in is by user name; an e-mail address is optional and not confirmed.
        options.User.RequireUniqueEmail = false;
        // "Confirmed" means "not disabled by an administrator" here (see AccountConfirmation).
        options.SignIn.RequireConfirmedAccount = true;
        options.SignIn.RequireConfirmedEmail = false;
    }
}
