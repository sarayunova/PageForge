// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace PageForge.Api.Services.Email;

/// <summary>
/// SMTP email provider used only when <c>Email:Provider</c> is <c>smtp</c>.
/// System.Net.Mail's SmtpClient is deprecated (SYSLIB0014) but remains the
/// dependency-free standard library option; the deprecation is confined to this
/// single file, which is instantiated solely by <see cref="EmailSenderFactory"/>.
/// </summary>
#pragma warning disable SYSLIB0014
public sealed class SmtpEmailSender : IEmailSender
{
    private readonly IOptions<EmailOptions> _options;

    public SmtpEmailSender(IOptions<EmailOptions> options) => _options = options;

    public async Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        EmailOptions o = _options.Value;

        using var smtp = new SmtpClient
        {
            Host = o.Host,
            Port = o.Port,
            EnableSsl = o.UseSsl,
            Timeout = 30_000
        };

        if (!string.IsNullOrEmpty(o.Username))
        {
            smtp.Credentials = new NetworkCredential(o.Username, o.Password);
            smtp.UseDefaultCredentials = false;
        }

        using var mail = new MailMessage
        {
            From = new MailAddress(o.FromAddress, o.FromName),
            Subject = message.Subject,
            Body = message.PlainTextBody,
            IsBodyHtml = !string.IsNullOrEmpty(message.HtmlBody)
        };
        if (!string.IsNullOrEmpty(message.HtmlBody))
            mail.Body = message.HtmlBody;

        mail.To.Add(message.To);

        await smtp.SendMailAsync(mail, ct);
    }
}
#pragma warning restore SYSLIB0014