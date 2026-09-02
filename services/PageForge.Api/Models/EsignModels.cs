// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Api.Data;

namespace PageForge.Api.Models;

public sealed class CreateSignatureRequest
{
    public required Guid DocumentVersionId { get; init; }

    public required string Title { get; init; }

    /// <summary>Recipients in signing order. First signer is the signer of record.</summary>
    public required List<SignerRequestItem> Signers { get; init; }
}

public sealed class SignerRequestItem
{
    public required string Email { get; init; }
    public string DisplayName { get; init; } = string.Empty;
}

public sealed class SignatureRequestResponse
{
    public required Guid Id { get; init; }
    public required Guid DocumentVersionId { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required DateTime CreatedAt { get; init; }
    public DateTime? SentAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public required List<SignerResponse> Signers { get; init; }
}

public sealed class SignerResponse
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public required string DisplayName { get; init; }
    public required string Status { get; init; }
    public int Order { get; init; }
    public DateTime? ViewedAt { get; init; }
    public DateTime? SignedAt { get; init; }
    public string? DeclineReason { get; init; }
}

public sealed class AuditEventResponse
{
    public required string ActorEmail { get; init; }
    public required string Action { get; init; }
    public required string Detail { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public sealed class SendSignatureRequest
{
    /// <summary>Number of days after which pending signers get a reminder (starts the reminder scheduler).</summary>
    public int ReminderDays { get; init; } = 3;
}

public sealed class SignActionRequest
{
    /// <summary>Optional; the signer email is identified by the URL route.</summary>
    public string? Email { get; init; }
    public string DeclineReason { get; init; } = string.Empty;
}

public sealed class CompletionCertificateResponse
{
    public required Guid SignatureRequestId { get; init; }
    public required string Title { get; init; }
    public required DateTime CompletedAt { get; init; }
    public required List<CertificateSignerRow> Signers { get; init; }
    public required List<CertificateAuditRow> AuditTrail { get; init; }
}

public sealed class CertificateSignerRow
{
    public required string Email { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required DateTime? SignedAt { get; init; }
    public required string Status { get; init; }
}

public sealed class CertificateAuditRow
{
    public required string ActorEmail { get; init; }
    public required string Action { get; init; }
    public required string Detail { get; init; }
    public required DateTime CreatedAt { get; init; }
}