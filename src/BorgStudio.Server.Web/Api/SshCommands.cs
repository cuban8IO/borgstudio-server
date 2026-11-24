using BorgStudio.Server.Application;
using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Storage;
using BorgStudio.Server.Infrastructure;
using BorgStudio.Server.Infrastructure.Ssh;
using BorgStudio.Server.Web.Hosting;
using BorgStudio.Server.Web.Localization;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace BorgStudio.Server.Web.Api;

/// <summary>
/// The program as sshd runs it for borg connections: without the web server, with only what the two
/// commands need. Nothing goes to the console but the answer: sshd hands it to the client.
/// </summary>
internal static class SshCommands
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (dataDirectory, arguments) = SshConsole.ParseArguments(args);
        if (dataDirectory is null || arguments.Length != 1)
        {
            await Console.Error.WriteLineAsync($"Usage: BorgStudio.Server {args[0]} --data <data folder> <argument>");
            return 2;
        }

        try
        {
            if (args[0] == SshConsole.AuthorizedKeysCommand)
            {
                return await SshConsole.AuthorizedKeysAsync(dataDirectory, arguments[0], Console.Out);
            }

            if (!Guid.TryParse(arguments[0], out var keyId))
            {
                await Console.Error.WriteLineAsync("BorgStudio Server: unknown key.");
                return 2;
            }

            var configuration = new ConfigurationBuilder()
                .AddJsonFile(Path.Combine(AppContext.BaseDirectory, ServerHosting.ConfigurationFile), optional: true, reloadOnChange: false)
                .AddInMemoryCollection(new Dictionary<string, string?> { [ServerPaths.ConfigurationKey] = dataDirectory })
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddApplication();
            services.AddInfrastructure(configuration);
            services.AddSingleton<IAccountTexts, AccountTexts>();
            services.AddSingleton<IStorageTexts, StorageTexts>();
            // Identity's token providers need it; nothing here protects data that outlives the process.
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            await using var provider = services.BuildServiceProvider();
            var borg = provider.GetRequiredService<IOptions<SshOptions>>().Value.ResolvedBorgPath;
            return await SshConsole.BorgServeAsync(provider, keyId, borg, provider.GetRequiredService<ServerPaths>(), Console.Error);
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"BorgStudio Server: {exception.Message}");
            return 1;
        }
    }
}
