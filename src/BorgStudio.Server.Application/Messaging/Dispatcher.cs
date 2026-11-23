using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Messaging;

using Microsoft.Extensions.DependencyInjection;

namespace BorgStudio.Server.Application.Messaging;

/// <summary>
/// Resolves the handler of a request from the current service scope. The typed call into the
/// handler is built once per request type and cached, so dispatching needs no reflection afterwards.
/// </summary>
internal sealed class Dispatcher(IServiceProvider services) : IDispatcher
{
    // One cache per request kind: a type could in theory be both a command and a query.
    private static readonly ConcurrentDictionary<Type, CommandInvoker> CommandInvokers = new();
    private static readonly ConcurrentDictionary<Type, object> ResultCommandInvokers = new();
    private static readonly ConcurrentDictionary<Type, object> QueryInvokers = new();

    public Task SendAsync(ICommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        var invoker = CommandInvokers.GetOrAdd(
            command.GetType(),
            static type => (CommandInvoker)Create(typeof(CommandInvoker<>).MakeGenericType(type)));
        return invoker.InvokeAsync(command, services, cancellationToken);
    }

    public Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        var invoker = (ResultCommandInvoker<TResult>)ResultCommandInvokers.GetOrAdd(
            command.GetType(),
            static type => Create(typeof(ResultCommandInvoker<,>).MakeGenericType(type, typeof(TResult))));
        return invoker.InvokeAsync(command, services, cancellationToken);
    }

    public Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        Validate(query);
        var invoker = (QueryInvoker<TResult>)QueryInvokers.GetOrAdd(
            query.GetType(),
            static type => Create(typeof(QueryInvoker<,>).MakeGenericType(type, typeof(TResult))));
        return invoker.InvokeAsync(query, services, cancellationToken);
    }

    private static object Create(Type invokerType) => Activator.CreateInstance(invokerType)!;

    private static void Validate(object request)
    {
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true))
        {
            return;
        }

        var errors = results
            .SelectMany(r => (r.MemberNames.Any() ? r.MemberNames : [string.Empty])
                .Select(member => (Member: member, Message: r.ErrorMessage ?? "Invalid value.")))
            .GroupBy(e => e.Member)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray());
        throw new RequestValidationException(request.GetType(), errors);
    }

    private static THandler GetHandler<THandler>(IServiceProvider services, Type requestType)
        where THandler : notnull
        => services.GetService<THandler>()
            ?? throw new InvalidOperationException(
                $"No handler is registered for {requestType.Name}. Handlers are registered by AddApplication().");

    private abstract class CommandInvoker
    {
        public abstract Task InvokeAsync(ICommand command, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class CommandInvoker<TCommand> : CommandInvoker
        where TCommand : ICommand
    {
        public override Task InvokeAsync(ICommand command, IServiceProvider services, CancellationToken cancellationToken)
            => GetHandler<ICommandHandler<TCommand>>(services, typeof(TCommand))
                .HandleAsync((TCommand)command, cancellationToken);
    }

    private abstract class ResultCommandInvoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(
            ICommand<TResult> command, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class ResultCommandInvoker<TCommand, TResult> : ResultCommandInvoker<TResult>
        where TCommand : ICommand<TResult>
    {
        public override Task<TResult> InvokeAsync(
            ICommand<TResult> command, IServiceProvider services, CancellationToken cancellationToken)
            => GetHandler<ICommandHandler<TCommand, TResult>>(services, typeof(TCommand))
                .HandleAsync((TCommand)command, cancellationToken);
    }

    private abstract class QueryInvoker<TResult>
    {
        public abstract Task<TResult> InvokeAsync(
            IQuery<TResult> query, IServiceProvider services, CancellationToken cancellationToken);
    }

    private sealed class QueryInvoker<TQuery, TResult> : QueryInvoker<TResult>
        where TQuery : IQuery<TResult>
    {
        public override Task<TResult> InvokeAsync(
            IQuery<TResult> query, IServiceProvider services, CancellationToken cancellationToken)
            => GetHandler<IQueryHandler<TQuery, TResult>>(services, typeof(TQuery))
                .HandleAsync((TQuery)query, cancellationToken);
    }
}
