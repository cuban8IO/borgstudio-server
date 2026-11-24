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

    string LastAdministrator { get; }

    string NotOnYourOwnAccount { get; }

    string InvalidPublicUrl { get; }

    string InvitationInvalid { get; }

    string CertificateUnreadable { get; }

    string CertificateNoPrivateKey { get; }

    string CertificateExpired { get; }
}
