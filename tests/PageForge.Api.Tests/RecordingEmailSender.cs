// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Api.Services.Email;

namespace PageForge.Api.Tests;

/// <summary>Captures email messages instead of sending them, so tests can assert
/// that reminders and completion certificates were produced without SMTP.</summary>
public sealed class RecordingEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _messages = new();

    public IReadOnlyList<EmailMessage> Messages => _messages;

    public Task SendAsync(EmailMessage message, CancellationToken ct)
    {
        lock (_messages)
        {
            _messages.Add(message);
        }
        return Task.CompletedTask;
    }
}