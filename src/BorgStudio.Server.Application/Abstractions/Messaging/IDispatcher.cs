namespace BorgStudio.Server.Application.Abstractions.Messaging;

/// <summary>
/// Sends commands and queries to their handler. Requests are validated first with their
/// data annotations; invalid requests throw <see cref="RequestValidationException"/>.
/// </summary>
public interface IDispatcher
{
    Task SendAsync(ICommand command, CancellationToken cancellationToken = default);

    Task<TResult> SendAsync<TResult>(ICommand<TResult> command, CancellationToken cancellationToken = default);

    Task<TResult> QueryAsync<TResult>(IQuery<TResult> query, CancellationToken cancellationToken = default);
}
