// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.EntityFrameworkCore;
using PageForge.Api.Data;

namespace PageForge.Api.Services;

/// <summary>
/// Shared commenting/annotation on documents shared with a team (FR-TEAM-01).
/// A document is made visible to a team via <see cref="TeamService.ShareDocumentAsync"/>;
/// every member of that team can then read and add comments. Consumers poll with
/// <c>?since=</c> to pull changes near-real-time (SignalR/WebSocket is a later upgrade).
/// </summary>
public sealed class TeamReviewService
{
    private readonly AppDbContext _db;
    private readonly TeamService _teams;

    public TeamReviewService(AppDbContext db, TeamService teams)
    {
        _db = db;
        _teams = teams;
    }

    /// <summary>
    /// Resolves the team whose comment scope the caller shares. The caller must be
    /// (a) the document's owner, or (b) a member of the document's shared team.
    /// Throws <see cref="InvalidOperationException"/> when the document is private or
    /// the caller has no access.
    /// </summary>
    private async Task<Guid?> ResolveSharedTeamAsync(Guid documentId, Guid userId)
    {
        var scope = await _db.Documents
            .AsNoTracking()
            .Where(d => d.Id == documentId)
            .Select(d => new { d.OwnerId, TeamId = (Guid?)d.TeamId })
            .SingleOrDefaultAsync()
            ?? throw new InvalidOperationException("Document not found.");

        if (scope.OwnerId == userId)
            return scope.TeamId; // owner may post/list even before sharing

        if (scope.TeamId is Guid teamId && await _teams.IsMemberAsync(teamId, userId))
            return teamId;

        throw new InvalidOperationException("You do not have access to this document.");
    }

    public async Task<Comment> AddCommentAsync(Guid documentId, Guid authorId, Data.Comment spec)
    {
        Guid? teamId = await ResolveSharedTeamAsync(documentId, authorId);

        bool versionBelongsToDocument = await _db.DocumentVersions
            .AnyAsync(v => v.Id == spec.DocumentVersionId && v.DocumentId == documentId);
        if (!versionBelongsToDocument)
            throw new InvalidOperationException("The target version does not belong to this document.");

        if (teamId is null)
            throw new InvalidOperationException("Share this document with a team before commenting.");

        var comment = new Data.Comment
        {
            DocumentVersionId = spec.DocumentVersionId,
            AuthorId = authorId,
            PageNumber = spec.PageNumber,
            AnchorRect = spec.AnchorRect,
            Body = spec.Body,
            CreatedAt = DateTime.UtcNow
        };

        _db.Comments.Add(comment);
        await _db.SaveChangesAsync();
        return comment;
    }

    public async Task<Data.Comment?> GetCommentAsync(Guid documentId, Guid commentId, Guid userId)
    {
        await ResolveSharedTeamAsync(documentId, userId);

        return await _db.Comments
            .AsNoTracking()
            .Include(c => c.Author)
            .Include(c => c.DocumentVersion)
            .Where(c => c.Id == commentId && c.DocumentVersion.DocumentId == documentId)
            .SingleOrDefaultAsync();
    }

    public async Task<IReadOnlyList<Data.Comment>> ListCommentsAsync(
        Guid documentId, Guid userId, DateTime? since, int? limit)
    {
        await ResolveSharedTeamAsync(documentId, userId);

        IQueryable<Data.Comment> query = _db.Comments
            .AsNoTracking()
            .Include(c => c.Author)
            .Where(c => c.DocumentVersion.DocumentId == documentId && !c.IsDeleted);

        if (since is not null)
            query = query.Where(c => c.CreatedAt > since || (c.UpdatedAt != null && c.UpdatedAt > since));

        query = query.OrderBy(c => c.CreatedAt);

        if (limit is > 0)
            query = query.Take(limit.Value);

        return await query.ToListAsync();
    }

    public async Task<Data.Comment> UpdateCommentAsync(Guid documentId, Guid commentId, Guid userId, string body)
    {
        await ResolveSharedTeamAsync(documentId, userId);

        Data.Comment comment = await _db.Comments
            .Where(c => c.Id == commentId && c.DocumentVersion.DocumentId == documentId)
            .SingleOrDefaultAsync()
            ?? throw new KeyNotFoundException("Comment not found.");

        if (comment.AuthorId != userId)
            throw new UnauthorizedAccessException("Only the author can edit a comment.");

        comment.Body = body;
        comment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return comment;
    }

    public async Task DeleteCommentAsync(Guid documentId, Guid commentId, Guid userId)
    {
        await ResolveSharedTeamAsync(documentId, userId);

        Data.Comment comment = await _db.Comments
            .Where(c => c.Id == commentId && c.DocumentVersion.DocumentId == documentId)
            .SingleOrDefaultAsync()
            ?? throw new KeyNotFoundException("Comment not found.");

        if (comment.AuthorId != userId)
            throw new UnauthorizedAccessException("Only the author can delete a comment.");

        comment.IsDeleted = true;
        comment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }
}