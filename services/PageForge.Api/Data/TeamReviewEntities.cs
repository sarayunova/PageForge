// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.ComponentModel.DataAnnotations;

namespace PageForge.Api.Data;

/// <summary>
/// A shared comment/annotation anchored to a specific page of a specific
/// document version (FR-TEAM-01). Visible to every member of the document's
/// bound team. Polling consumers pull changes via <c>?since</c>.
/// </summary>
public sealed class Comment
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentVersionId { get; set; }
    public DocumentVersion DocumentVersion { get; set; } = null!;

    /// <summary>Authoring user; must be a member of the document's team.</summary>
    public Guid AuthorId { get; set; }
    public User Author { get; set; } = null!;

    /// <summary>1-based page number the comment is anchored to.</summary>
    public int PageNumber { get; set; }

    /// <summary>Anchor rectangle in PDF points as normalized comma-separated "x0,y0,x1,y1".</summary>
    [Required, MaxLength(128)]
    public string AnchorRect { get; set; } = string.Empty;

    [Required, MaxLength(2000)]
    public string Body { get; set; } = string.Empty;

    /// <summary>True when the author has removed the comment (soft delete keeps history).</summary>
    public bool IsDeleted { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAt { get; set; }
}