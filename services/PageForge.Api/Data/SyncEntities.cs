// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.ComponentModel.DataAnnotations;

namespace PageForge.Api.Data;

/// <summary>
/// A shared document record that a user syncs across devices. The latest
/// content lives in the newest <see cref="DocumentVersion"/>; the original file
/// itself stays an ordinary file on the client. FR-SYNC-01.
/// </summary>
public sealed class Document
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    /// <summary>
    /// Optional team this document is shared with (FR-TEAM-01). When set, every
    /// member of <see cref="Team"/> can read/annotate the document's comments.
    /// Null when the document is private to its owner.
    /// </summary>
    public Guid? TeamId { get; set; }
    public Team? Team { get; set; }

    [Required, MaxLength(255)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Current head version number; 0 when the document has no versions yet.</summary>
    public int LatestVersion { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DocumentVersion> Versions { get; set; } = [];
}

/// <summary>
/// An immutable snapshot of a document at a point in time. <see cref="VersionNumber"/>
/// is a per-document monotonic sequence (1, 2, 3, ...). The raw bytes live in the
/// blob store under <see cref="BlobKey"/>; this row only holds metadata and the
/// SHA-256 checksum used for conflict detection (FR-SYNC-01/02).
/// </summary>
public sealed class DocumentVersion
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId { get; set; }
    public Document Document { get; set; } = null!;

    /// <summary>1-based per-document sequence number.</summary>
    public int VersionNumber { get; set; }

    public string BlobKey { get; set; } = string.Empty;

    /// <summary>Caller-supplied Idempotency-Key (nullable); unique per document so retries don't double-push.</summary>
    [MaxLength(128)]
    public string? IdempotencyKey { get; set; }

    [Required, MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UploadedAt { get; set; }
}