using System.ComponentModel.DataAnnotations;

namespace BorgStudio.Server.Application.Abstractions.Validation;

/// <summary>
/// An e-mail address that may be left empty. (<see cref="EmailAddressAttribute"/> rejects the empty
/// string that a blank form field sends.)
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class OptionalEmailAttribute : ValidationAttribute
{
    private static readonly EmailAddressAttribute Email = new();

    public override bool IsValid(object? value) =>
        value is null || (value is string text && (string.IsNullOrWhiteSpace(text) || Email.IsValid(text.Trim())));
}
