// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PageForge.Api.Data;
using PageForge.Api.Models;
using PageForge.Api.Services;

namespace PageForge.Api.Controllers;

/// <summary>
/// Team review (FR-TEAM-01): shared comments/annotations on a document that has
/// been shared with a team. Endpoints live under a document id so the version the
/// comment is anchored to can be validated. Consumers poll <c>GET .../comments?since=</c>
/// for near-real-time updates (SignalR/WebSocket is a later upgrade).
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/documents")]
public sealed class TeamReviewController : ControllerBase
{
    private readonly TeamReviewService _review;
    private readonly TeamService _teams;

    public TeamReviewController(TeamReviewService review, TeamService teams)
    {
        _review = review;
        _teams = teams;
    }

    [HttpPost("{documentId:guid}/share")]
    public async Task<IActionResult> Share(Guid documentId, [FromBody] ShareDocumentRequest request)
    {
        Guid userId = GetUserId();
        try
        {
            Document doc = await _teams.ShareDocumentAsync(documentId, userId, request.TeamId);
            return Ok(new { id = doc.Id, teamId = doc.TeamId });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
    }

    [HttpPost("{documentId:guid}/comments")]
    public async Task<IActionResult> Create(Guid documentId, [FromBody] CommentRequest request)
    {
        Guid userId = GetUserId();
        try
        {
            var spec = new Comment
            {
                DocumentVersionId = request.DocumentVersionId,
                PageNumber = request.PageNumber,
                AnchorRect = request.AnchorRect,
                Body = request.Body
            };
            Comment created = await _review.AddCommentAsync(documentId, userId, spec);
            Comment? loaded = await _review.GetCommentAsync(documentId, created.Id, userId);
            return CreatedAtAction(
                nameof(Get),
                new { documentId, commentId = created.Id },
                ToResponse(loaded!));
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_REQUEST",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpGet("{documentId:guid}/comments")]
    public async Task<IActionResult> List(
        Guid documentId,
        [FromQuery] DateTime? since,
        [FromQuery] int? limit)
    {
        Guid userId = GetUserId();
        int bounded = Math.Clamp(limit ?? 50, 1, 200);

        try
        {
            IReadOnlyList<Comment> items = await _review.ListCommentsAsync(documentId, userId, since, bounded);
            return Ok(new CommentListResponse
            {
                Items = items.Select(ToResponse).ToList(),
                NextCursor = null
            });
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpGet("{documentId:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> Get(Guid documentId, Guid commentId)
    {
        Guid userId = GetUserId();
        try
        {
            Comment? comment = await _review.GetCommentAsync(documentId, commentId, userId);
            return comment is null
                ? (IActionResult)NotFound(new ErrorResponse
                {
                    Error = new ErrorDetail
                    {
                        Code = "NOT_FOUND",
                        Message = "Comment not found.",
                        TraceId = HttpContext.TraceIdentifier
                    }
                })
                : Ok(ToResponse(comment));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpPatch("{documentId:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> Update(Guid documentId, Guid commentId, [FromBody] UpdateCommentRequest request)
    {
        Guid userId = GetUserId();
        try
        {
            Comment updated = await _review.UpdateCommentAsync(documentId, commentId, userId, request.Body);
            Comment? loaded = await _review.GetCommentAsync(documentId, commentId, userId);
            return Ok(ToResponse(loaded ?? updated));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpDelete("{documentId:guid}/comments/{commentId:guid}")]
    public async Task<IActionResult> Delete(Guid documentId, Guid commentId)
    {
        Guid userId = GetUserId();
        try
        {
            await _review.DeleteCommentAsync(documentId, commentId, userId);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NOT_FOUND",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    private static CommentResponse ToResponse(Comment c) => new()
    {
        Id = c.Id,
        DocumentVersionId = c.DocumentVersionId,
        AuthorId = c.AuthorId,
        AuthorEmail = c.Author?.Email ?? string.Empty,
        AuthorDisplayName = c.Author?.DisplayName ?? string.Empty,
        PageNumber = c.PageNumber,
        AnchorRect = c.AnchorRect,
        Body = c.Body,
        IsDeleted = c.IsDeleted,
        CreatedAt = c.CreatedAt,
        UpdatedAt = c.UpdatedAt
    };

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }
}