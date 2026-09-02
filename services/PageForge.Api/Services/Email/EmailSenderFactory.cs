// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PageForge.Api.Services.Email;

/// <summary>
/// Picks the email provider from configuration: "none" → <see cref="NoopEmailSender"/>,
/// "smtp" → <see cref="SmtpEmailSender"/>. Registered as the single <see cref="IEmailSender"/>.
/// </summary>
public static class EmailSenderFactory
{
    public static IEmailSender Create(IServiceProvider sp)
    {
        var optionsProvider = sp.GetRequiredService<IOptions<EmailOptions>>();
        return string.Equals(optionsProvider.Value.Provider, "smtp", StringComparison.OrdinalIgnoreCase)
            ? new SmtpEmailSender(optionsProvider)
            : new NoopEmailSender(optionsProvider);
    }
}