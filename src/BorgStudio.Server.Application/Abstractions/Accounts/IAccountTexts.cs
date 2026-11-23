namespace BorgStudio.Server.Application.Abstractions.Accounts;

/// <summary>Messages the account use cases show to users, in the user's language.</summary>
public interface IAccountTexts
{
    string InvalidSetupCode { get; }

    string SetupAlreadyDone { get; }

    string PasswordsDoNotMatch { get; }

    string WrongPassword { get; }

    string InvalidAuthenticatorCode { get; }

    string LockedOut { get; }
}
