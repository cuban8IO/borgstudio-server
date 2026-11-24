using BorgStudio.Server.Infrastructure.Tls;

using Microsoft.AspNetCore.HttpOverrides;

namespace BorgStudio.Server.Web.Hosting;

/// <summary>How the server runs as a program: configuration file, service integration and listeners.</summary>
internal static class ServerHosting
{
    /// <summary>The administrator's settings next to the program; updates never overwrite it.</summary>
    public const string ConfigurationFile = "borgstudio-server.json";

    public const string WindowsServiceName = "BorgStudioServer";

    public static WebApplicationBuilder ConfigureServerHosting(this WebApplicationBuilder builder, string[] args)
    {
        // borgstudio-server.json overrides appsettings.json; environment variables and the command line
        // still override both (they are added again, so they come last).
        builder.Configuration
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, ConfigurationFile), optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .AddCommandLine(args);

        // Both do nothing unless the program runs as a systemd or Windows service.
        builder.Services.AddSystemd();
        builder.Services.AddWindowsService(options => options.ServiceName = WindowsServiceName);

        ConfigureListeners(builder);
        return builder;
    }

    /// <summary>Loads (or creates) the HTTPS certificate at start, so problems show up in the log right away.</summary>
    public static WebApplication PrepareHttpsCertificate(this WebApplication app)
    {
        var options = app.Configuration.GetSection(ServerEndpointOptions.SectionName).Get<ServerEndpointOptions>() ?? new();
        if (string.IsNullOrWhiteSpace(app.Configuration["urls"]) && options.Https.Enabled)
        {
            _ = app.Services.GetRequiredService<TlsCertificateStore>().Certificate;
        }

        return app;
    }

    public static WebApplication UseForwardedHeadersIfConfigured(this WebApplication app)
    {
        var options = app.Configuration.GetSection(ServerEndpointOptions.SectionName).Get<ServerEndpointOptions>() ?? new();
        if (options.ForwardedHeaders)
        {
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            app.UseForwardedHeaders(forwarded);
        }

        return app;
    }

    /// <summary>
    /// HTTPS (default 8443) and/or HTTP (default off) on all addresses. Explicit URLs (ASPNETCORE_URLS,
    /// --urls, the development launch profile) take precedence and leave Kestrel's own set-up alone.
    /// </summary>
    private static void ConfigureListeners(WebApplicationBuilder builder)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            return;
        }

        var options = builder.Configuration.GetSection(ServerEndpointOptions.SectionName).Get<ServerEndpointOptions>() ?? new();
        if (!options.Https.Enabled && !options.Http.Enabled)
        {
            throw new InvalidOperationException(
                "Neither HTTPS nor HTTP is enabled. Turn on BorgStudio:Https:Enabled or BorgStudio:Http:Enabled.");
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (options.Https.Enabled)
            {
                kestrel.ListenAnyIP(options.Https.Port, listen => listen.UseHttps(https =>
                {
                    // Asked on every new connection, so an uploaded certificate applies without a restart.
                    https.ServerCertificateSelector = (_, _) =>
                        kestrel.ApplicationServices.GetRequiredService<TlsCertificateStore>().Certificate;
                }));
            }

            if (options.Http.Enabled)
            {
                kestrel.ListenAnyIP(options.Http.Port);
            }
        });
    }
}
