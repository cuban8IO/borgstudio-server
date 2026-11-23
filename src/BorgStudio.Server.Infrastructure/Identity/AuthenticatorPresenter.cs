using System.Text;

using BorgStudio.Server.Application.Abstractions.Accounts;

using QRCoder;

namespace BorgStudio.Server.Infrastructure.Identity;

internal sealed class AuthenticatorPresenter : IAuthenticatorPresenter
{
    public string FormatKey(string key)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < key.Length; i += 4)
        {
            if (i > 0)
            {
                builder.Append(' ');
            }

            builder.Append(key.AsSpan(i, Math.Min(4, key.Length - i)));
        }

        return builder.ToString().ToLowerInvariant();
    }

    public string CreateUri(string issuer, string userName, string key)
    {
        var label = Uri.EscapeDataString($"{issuer}:{userName}");
        return $"otpauth://totp/{label}?secret={key}&issuer={Uri.EscapeDataString(issuer)}&digits=6";
    }

    public string CreateQrCodeDataUri(string uri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(pixelsPerModule: 5);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }
}
