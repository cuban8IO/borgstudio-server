using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Web.Resources;

using Microsoft.AspNetCore.Identity;

namespace BorgStudio.Server.Web.Localization;

internal sealed class AccountTexts : IAccountTexts
{
    public string InvalidSetupCode => Strings.ErrorInvalidSetupCode;

    public string SetupAlreadyDone => Strings.ErrorSetupAlreadyDone;

    public string PasswordsDoNotMatch => Strings.ErrorPasswordsDoNotMatch;

    public string WrongPassword => Strings.ErrorWrongPassword;

    public string InvalidAuthenticatorCode => Strings.ErrorInvalidAuthenticatorCode;

    public string LockedOut => Strings.ErrorLockedOut;

    public string LastAdministrator => Strings.ErrorLastAdministrator;

    public string NotOnYourOwnAccount => Strings.ErrorNotOnYourOwnAccount;

    public string InvalidPublicUrl => Strings.ErrorInvalidPublicUrl;

    public string InvitationInvalid => Strings.ErrorInvitationInvalid;

    public string CertificateUnreadable => Strings.ErrorCertificateUnreadable;

    public string CertificateNoPrivateKey => Strings.ErrorCertificateNoPrivateKey;

    public string CertificateExpired => Strings.ErrorCertificateExpired;
}

/// <summary>ASP.NET Core Identity's messages in the user's language (English and German).</summary>
internal sealed class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    public override IdentityError DefaultError() => Error(nameof(DefaultError), Strings.IdentityDefaultError);

    public override IdentityError ConcurrencyFailure() => Error(nameof(ConcurrencyFailure), Strings.IdentityConcurrencyFailure);

    public override IdentityError PasswordMismatch() => Error(nameof(PasswordMismatch), Strings.ErrorWrongPassword);

    public override IdentityError InvalidUserName(string? userName) =>
        Error(nameof(InvalidUserName), string.Format(Strings.IdentityInvalidUserName, userName));

    public override IdentityError DuplicateUserName(string userName) =>
        Error(nameof(DuplicateUserName), string.Format(Strings.IdentityDuplicateUserName, userName));

    public override IdentityError InvalidEmail(string? email) =>
        Error(nameof(InvalidEmail), string.Format(Strings.IdentityInvalidEmail, email));

    public override IdentityError PasswordTooShort(int length) =>
        Error(nameof(PasswordTooShort), string.Format(Strings.IdentityPasswordTooShort, length));

    public override IdentityError PasswordRequiresNonAlphanumeric() =>
        Error(nameof(PasswordRequiresNonAlphanumeric), Strings.IdentityPasswordRequiresNonAlphanumeric);

    public override IdentityError PasswordRequiresDigit() => Error(nameof(PasswordRequiresDigit), Strings.IdentityPasswordRequiresDigit);

    public override IdentityError PasswordRequiresLower() => Error(nameof(PasswordRequiresLower), Strings.IdentityPasswordRequiresLower);

    public override IdentityError PasswordRequiresUpper() => Error(nameof(PasswordRequiresUpper), Strings.IdentityPasswordRequiresUpper);

    public override IdentityError RecoveryCodeRedemptionFailed() =>
        Error(nameof(RecoveryCodeRedemptionFailed), Strings.ErrorInvalidRecoveryCode);

    private static IdentityError Error(string code, string description) => new() { Code = code, Description = description };
}
