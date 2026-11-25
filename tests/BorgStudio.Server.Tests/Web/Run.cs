using System.Globalization;

using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Storage;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Web;

/// <summary>Commands and queries against a test server, with English messages.</summary>
internal static class Run
{
    public static async Task<TResult> SendAsync<TResult>(TestServer server, ICommand<TResult> command)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        return await server.Services.GetRequiredService<IDispatcher>().SendAsync(command);
    }

    public static async Task SendAsync(TestServer server, ICommand command)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        await server.Services.GetRequiredService<IDispatcher>().SendAsync(command);
    }

    public static async Task<TResult> QueryAsync<TResult>(TestServer server, IQuery<TResult> query)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        return await server.Services.GetRequiredService<IDispatcher>().QueryAsync(query);
    }

    public static async Task<Guid> CreateRepositoryAsync(TestServer server, string userId, string name, Guid? datastoreId = null)
    {
        var created = await SendAsync(server, new CreateRepositoryCommand(userId, name, datastoreId, null));
        Assert.True(created.Succeeded, string.Join(" ", created.Errors));
        return created.Value;
    }
}
