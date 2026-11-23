using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application;
using BorgStudio.Server.Application.Abstractions.Messaging;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Tests.Application;

public sealed class DispatcherTests
{
    [Fact]
    public async Task QueryAsync_ReturnsTheHandlersResult()
    {
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var result = await dispatcher.QueryAsync(new Add(2, 3));

        Assert.Equal(5, result);
    }

    [Fact]
    public async Task SendAsync_RunsTheCommandHandler()
    {
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new Remember("borg"));

        Assert.Equal(["borg"], services.GetRequiredService<Memory>().Items);
    }

    [Fact]
    public async Task SendAsync_WithResult_ReturnsTheHandlersResult()
    {
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var count = await dispatcher.SendAsync(new RememberAndCount("a"));
        count = await dispatcher.SendAsync(new RememberAndCount("b"));

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task SendAsync_InvalidCommand_ThrowsWithErrorsPerProperty()
    {
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var exception = await Assert.ThrowsAsync<RequestValidationException>(
            () => dispatcher.SendAsync(new Remember(string.Empty)));

        Assert.Equal(typeof(Remember), exception.RequestType);
        Assert.Contains(nameof(Remember.Text), exception.Errors.Keys);
        Assert.Empty(services.GetRequiredService<Memory>().Items);
    }

    [Fact]
    public async Task QueryAsync_WithoutHandler_ThrowsAClearError()
    {
        await using var services = CreateServices();
        await using var scope = services.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.QueryAsync(new Unhandled()));

        Assert.Contains(nameof(Unhandled), exception.Message);
    }

    [Fact]
    public async Task EveryRequest_RunsInAScopeOfItsOwn()
    {
        await using var services = CreateServices();
        var dispatcher = services.GetRequiredService<IDispatcher>();

        await dispatcher.SendAsync(new CountInScope());
        await dispatcher.SendAsync(new CountInScope());

        // A fresh scoped counter each time: both handlers saw it at 1.
        Assert.Equal(["1", "1"], services.GetRequiredService<Memory>().Items);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddRequestHandlers(typeof(DispatcherTests).Assembly);
        services.AddSingleton<Memory>();
        services.AddScoped<ScopedCounter>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public sealed record Add(int Left, int Right) : IQuery<int>;

    public sealed record Remember([property: Required] string Text) : ICommand;

    public sealed record RememberAndCount(string Text) : ICommand<int>;

    public sealed record CountInScope : ICommand;

    public sealed record Unhandled : IQuery<string>;

    public sealed class Memory
    {
        public List<string> Items { get; } = [];
    }

    public sealed class ScopedCounter
    {
        public int Value { get; set; }
    }

    public sealed class AddHandler : IQueryHandler<Add, int>
    {
        public Task<int> HandleAsync(Add query, CancellationToken cancellationToken)
            => Task.FromResult(query.Left + query.Right);
    }

    public sealed class RememberHandler(Memory memory) : ICommandHandler<Remember>
    {
        public Task HandleAsync(Remember command, CancellationToken cancellationToken)
        {
            memory.Items.Add(command.Text);
            return Task.CompletedTask;
        }
    }

    public sealed class RememberAndCountHandler(Memory memory) : ICommandHandler<RememberAndCount, int>
    {
        public Task<int> HandleAsync(RememberAndCount command, CancellationToken cancellationToken)
        {
            memory.Items.Add(command.Text);
            return Task.FromResult(memory.Items.Count);
        }
    }

    public sealed class CountInScopeHandler(ScopedCounter counter, Memory memory) : ICommandHandler<CountInScope>
    {
        public Task HandleAsync(CountInScope command, CancellationToken cancellationToken)
        {
            counter.Value++;
            memory.Items.Add(counter.Value.ToString());
            return Task.CompletedTask;
        }
    }
}
