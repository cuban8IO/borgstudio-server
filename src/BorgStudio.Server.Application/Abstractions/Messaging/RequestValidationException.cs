namespace BorgStudio.Server.Application.Abstractions.Messaging;

/// <summary>A command or query failed the validation of its data annotations.</summary>
public sealed class RequestValidationException : Exception
{
    public RequestValidationException(Type requestType, IReadOnlyDictionary<string, string[]> errors)
        : base($"{requestType.Name} is invalid: {string.Join("; ", errors.SelectMany(e => e.Value))}")
    {
        RequestType = requestType;
        Errors = errors;
    }

    public Type RequestType { get; }

    /// <summary>Error messages per property name; errors that belong to no property use an empty key.</summary>
    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
