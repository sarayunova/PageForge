// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.Extensions.Options;

namespace PageForge.Api.Services.Email;

/// <summary>
/// No-op email provider used by default in dev and test hosts. It performs no
/// I/O, so the e-sign flow is fully exercisable offline; set
/// <c>Email:Provider</c> to <c>smtp</c> in a real deployment to send mail.
/// </summary>
public sealed class NoopEmailSender : IEmailSender
{
    public NoopEmailSender(IOptions<EmailOptions> options)
    {
        _ = options;
    }

    public Task SendAsync(EmailMessage message, CancellationToken ct) => Task.CompletedTask;
}