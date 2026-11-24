using System.ComponentModel.DataAnnotations;

using BorgStudio.Server.Application.Abstractions.Accounts;
using BorgStudio.Server.Application.Abstractions.Messaging;
using BorgStudio.Server.Application.Abstractions.Tls;
using BorgStudio.Server.Application.Users;

using Microsoft.Extensions.Logging;

namespace BorgStudio.Server.Application.Settings;

/// <summary>The HTTPS certificate in use (administrators only).</summary>
public sealed record GetTlsCertificateQuery([property: Required] string ActingUserId) : IQuery<TlsCertificateInfo>;

internal sealed class GetTlsCertificateQueryHandler(UserAdministration admin, ITlsCertificateService certificates)
    : IQueryHandler<GetTlsCertificateQuery, TlsCertificateInfo>
{
    public async Task<TlsCertificateInfo> HandleAsync(GetTlsCertificateQuery query, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(query.ActingUserId, cancellationToken);
        return certificates.Current;
    }
}

/// <summary>
/// Uploads a certificate for HTTPS: either a PKCS#12 file (with its password) or a PEM certificate with
/// its private key. It applies to new connections at once.
/// </summary>
public sealed record UploadTlsCertificateCommand(
    [property: Required] string ActingUserId,
    byte[]? Pfx,
    string? Password,
    string? CertificatePem,
    string? KeyPem) : ICommand<AccountResult>;

internal sealed class UploadTlsCertificateCommandHandler(
    UserAdministration admin,
    ITlsCertificateService certificates,
    ILogger<UploadTlsCertificateCommandHandler> logger) : ICommandHandler<UploadTlsCertificateCommand, AccountResult>
{
    public async Task<AccountResult> HandleAsync(UploadTlsCertificateCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);

        TlsCertificateError error;
        if (command.Pfx is { Length: > 0 })
        {
            error = await certificates.UploadPfxAsync(command.Pfx, command.Password, cancellationToken);
        }
        else if (!string.IsNullOrWhiteSpace(command.CertificatePem) && !string.IsNullOrWhiteSpace(command.KeyPem))
        {
            error = await certificates.UploadPemAsync(command.CertificatePem, command.KeyPem, cancellationToken);
        }
        else
        {
            error = TlsCertificateError.Unreadable;
        }

        if (error != TlsCertificateError.None)
        {
            return AccountResult.Failed(error switch
            {
                TlsCertificateError.NoPrivateKey => admin.Texts.CertificateNoPrivateKey,
                TlsCertificateError.Expired => admin.Texts.CertificateExpired,
                _ => admin.Texts.CertificateUnreadable,
            });
        }

        logger.LogInformation("A new HTTPS certificate was uploaded: {Subject}.", certificates.Current.Subject);
        return AccountResult.Success;
    }
}

public sealed record RemoveUploadedTlsCertificateCommand([property: Required] string ActingUserId) : ICommand;

internal sealed class RemoveUploadedTlsCertificateCommandHandler(UserAdministration admin, ITlsCertificateService certificates)
    : ICommandHandler<RemoveUploadedTlsCertificateCommand>
{
    public async Task HandleAsync(RemoveUploadedTlsCertificateCommand command, CancellationToken cancellationToken)
    {
        await admin.RequireAdministratorAsync(command.ActingUserId, cancellationToken);
        await certificates.RemoveUploadedAsync(cancellationToken);
    }
}
