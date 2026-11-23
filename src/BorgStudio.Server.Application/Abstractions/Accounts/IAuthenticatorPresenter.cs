namespace BorgStudio.Server.Application.Abstractions.Accounts;

/// <summary>Turns an authenticator key into what the user sees: a readable key and a QR code.</summary>
public interface IAuthenticatorPresenter
{
    /// <summary>The key in groups of four, e.g. <c>abcd efgh ijkl …</c>.</summary>
    string FormatKey(string key);

    /// <summary>The <c>otpauth://</c> link authenticator apps understand.</summary>
    string CreateUri(string issuer, string userName, string key);

    /// <summary>A QR code of <paramref name="uri"/> as a <c>data:</c> URI for an <c>img</c> element.</summary>
    string CreateQrCodeDataUri(string uri);
}
