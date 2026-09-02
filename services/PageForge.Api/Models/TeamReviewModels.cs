// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

/// <summary>Body for sharing a document with a team (FR-TEAM-01).</summary>
public sealed class ShareDocumentRequest
{
    public System.Guid TeamId { get; init; }
}

/// <summary>Create or update a shared comment anchored to a page of a document version.</summary>
public sealed class CommentRequest
{
    public System.Guid DocumentVersionId { get; init; }
    public int PageNumber { get; init; }
    public string AnchorRect { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
}

/// <summary>Body for editing an existing comment's text.</summary>
public sealed class UpdateCommentRequest
{
    public string Body { get; init; } = string.Empty;
}

/// <summary>A comment as returned by the API, with the author resolved.</summary>
public sealed class CommentResponse
{
    public required System.Guid Id { get; init; }
    public required System.Guid DocumentVersionId { get; init; }
    public required System.Guid AuthorId { get; init; }
    public required string AuthorEmail { get; init; }
    public required string AuthorDisplayName { get; init; }
    public required int PageNumber { get; init; }
    public required string AnchorRect { get; init; }
    public required string Body { get; init; }
    public bool IsDeleted { get; init; }
    public required System.DateTime CreatedAt { get; init; }
    public System.DateTime? UpdatedAt { get; init; }
}

/// <summary>Paged / filtered list of a document's comments.</summary>
public sealed class CommentListResponse
{
    public required System.Collections.Generic.IReadOnlyList<CommentResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}