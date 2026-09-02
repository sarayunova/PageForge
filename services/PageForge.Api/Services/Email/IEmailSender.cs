// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services.Email;

/// <summary>
/// An outbound email. Addresses are never returned to clients; this is the
/// internal carrier handed to an <see cref="IEmailSender"/> provider.
/// </summary>
public sealed class EmailMessage
{
    public required string To { get; init; }
    public required string Subject { get; init; }
    public required string PlainTextBody { get; init; }
    public string? HtmlBody { get; init; }
}

/// <summary>
/// Abstraction over email delivery (e-sign reminders and completion
/// certificates). Implementations are swappable via configuration so dev and
/// test hosts never need a real SMTP endpoint (see <see cref="NoopEmailSender"/>
/// and <see cref="EmailOptions.Provider"/>).
/// </summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}