namespace BorgStudio.Server.Application.Abstractions.Messaging;

/// <summary>A request that changes state and returns nothing.</summary>
public interface ICommand;

/// <summary>A request that changes state and returns a result.</summary>
public interface ICommand<TResult>;

/// <summary>A request that only reads state.</summary>
public interface IQuery<TResult>;

/// <summary>Handles one command type that returns nothing.</summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles one command type that returns a result.</summary>
public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<TResult> HandleAsync(TCommand command, CancellationToken cancellationToken);
}

/// <summary>Handles one query type.</summary>
public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<TResult> HandleAsync(TQuery query, CancellationToken cancellationToken);
}
