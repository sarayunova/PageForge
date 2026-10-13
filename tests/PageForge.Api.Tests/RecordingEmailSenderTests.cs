// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Api.Services.Email;

namespace PageForge.Api.Tests;

/// <summary>
/// The OCR worker sends completion email from its own thread while a test polls the
/// recorder. What the test reads must therefore be a stable snapshot, not the list the
/// worker is still appending to (enumerating that threw InvalidOperationException).
/// A snapshot that cannot change is checkable deterministically, unlike the race itself.
/// </summary>
public sealed class RecordingEmailSenderTests
{
    private static EmailMessage Message(string subject) =>
        new() { To = "a@example.com", Subject = subject, PlainTextBody = "x" };

    [Fact]
    public async Task Messages_is_a_snapshot_that_later_sends_do_not_change()
    {
        var sender = new RecordingEmailSender();
        await sender.SendAsync(Message("first"), CancellationToken.None);

        IReadOnlyList<EmailMessage> snapshot = sender.Messages;
        await sender.SendAsync(Message("second"), CancellationToken.None);

        Assert.Single(snapshot);
        Assert.Equal(2, sender.Messages.Count);
    }
}
