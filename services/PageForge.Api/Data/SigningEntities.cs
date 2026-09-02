// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PageForge.Api.Data;

public enum SignatureStatus
{
    Draft = 0,
    Sent = 1,
    Viewed = 2,
    Completed = 3,
    Declined = 4
}

public enum SignerStatus
{
    Pending = 0,
    Viewed = 1,
    Signed = 2,
    Declined = 3
}

/// <summary>
/// A send-for-signature workflow over a specific document version (FR-ESIGN-01).
/// The request carries the lifecycle DRAFT → SENT → VIEWED → COMPLETED/DECLINED
/// plus a full audit trail of <see cref="SignatureAuditEvent"/> rows.
/// </summary>
public sealed class SignatureRequest
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public Guid DocumentVersionId { get; set; }
    public DocumentVersion DocumentVersion { get; set; } = null!;

    [Required, MaxLength(255)]
    public string Title { get; set; } = string.Empty;

    public SignatureStatus Status { get; set; } = SignatureStatus.Draft;

    /// <summary>Caller-supplied Idempotency-Key (nullable); unique per owner so retries don't double-create.</summary>
    [MaxLength(128)]
    public string? IdempotencyKey { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? SentAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>Monotonic UTC clock within this request, used by the reminder scheduler.</summary>
    public DateTime NextReminderAt { get; set; } = DateTime.UtcNow;
    public int ReminderCount { get; set; }

    public ICollection<Signer> Signers { get; set; } = [];
    public ICollection<SignatureAuditEvent> AuditEvents { get; set; } = [];
}

/// <summary>
/// An individual recipient of a <see cref="SignatureRequest"/>. Signed/declined
/// decisions are per-signer; the request completes when every signer has signed.
/// </summary>
public sealed class Signer
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SignatureRequestId { get; set; }
    public SignatureRequest SignatureRequest { get; set; } = null!;

    [Required, MaxLength(320)]
    public string Email { get; set; } = string.Empty;

    [MaxLength(255)]
    public string DisplayName { get; set; } = string.Empty;

    public SignerStatus Status { get; set; } = SignerStatus.Pending;
    public int Order { get; set; }

    public DateTime? ViewedAt { get; set; }
    public DateTime? SignedAt { get; set; }

    /// <summary>Set when the signer declines; value recorded for audit.</summary>
    [MaxLength(255)]
    public string? DeclineReason { get; set; }
}

/// <summary>
/// Append-only audit record for a <see cref="SignatureRequest"/> (FR-ESIGN-01
/// "produce a completion certificate/audit trail"). Never updated or deleted.
/// </summary>
public sealed class SignatureAuditEvent
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SignatureRequestId { get; set; }
    public SignatureRequest SignatureRequest { get; set; } = null!;

    /// <summary>Who performed the action (email, or "system").</summary>
    [Required, MaxLength(320)]
    public string ActorEmail { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string Action { get; set; } = string.Empty;

    [Required, MaxLength(2048)]
    public string Detail { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}