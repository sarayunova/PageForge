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

[Authorize]
[ApiController]
[Route("api/v1/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly SyncService _sync;

    public DocumentsController(SyncService sync) => _sync = sync;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDocumentRequest request)
    {
        Guid userId = GetUserId();
        var doc = await _sync.CreateDocumentAsync(userId, request.Name);

        return CreatedAtAction(
            nameof(GetDocument),
            new { id = doc.Id },
            ToResponse(doc));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? cursor, [FromQuery] int limit = 20)
    {
        Guid userId = GetUserId();
        int boundedLimit = Math.Clamp(limit, 1, 100);

        var (items, next) = await _sync.ListDocumentsAsync(userId, cursor, boundedLimit, HttpContext.RequestAborted);

        return Ok(new DocumentListResponse
        {
            Items = items.Select(ToResponse).ToList(),
            NextCursor = next
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetDocument(Guid id)
    {
        Guid userId = GetUserId();

        var doc = await _sync.GetDocumentAsync(userId, id, HttpContext.RequestAborted);
        if (doc is null) return NotFound();

        DocumentVersion latest = doc.Versions.OrderByDescending(v => v.VersionNumber).FirstOrDefault()!;

        return Ok(new DocumentSummaryResponse
        {
            Id = doc.Id,
            Name = doc.Name,
            LatestVersion = doc.LatestVersion,
            LatestSha256 = latest?.Sha256 ?? string.Empty,
            LatestVersionCreatedAt = latest?.CreatedAt,
            CreatedAt = doc.CreatedAt,
            UpdatedAt = doc.UpdatedAt
        });
    }

    [HttpPost("{id:guid}/versions")]
    public async Task<IActionResult> PushVersion(Guid id, [FromQuery] int baseVersionNumber)
    {
        Guid userId = GetUserId();
        string? idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        string name = Request.Headers["X-PageForge-Name"].FirstOrDefault() ?? "document";
        string contentType = Request.ContentType ?? "application/octet-stream";

        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, HttpContext.RequestAborted);
        byte[] bytes = ms.ToArray();

        try
        {
            var pushed = await _sync.PushVersionAsync(
                userId, id, baseVersionNumber, name, bytes, contentType, idempotencyKey,
                HttpContext.RequestAborted);

            return StatusCode(StatusCodes.Status201Created, new VersionResponse
            {
                Id = pushed.Version.Id,
                VersionNumber = pushed.Version.VersionNumber,
                Sha256 = pushed.Version.Sha256,
                SizeBytes = pushed.Version.SizeBytes,
                CreatedAt = pushed.Version.CreatedAt
            });
        }
        catch (VersionConflictException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "VERSION_CONFLICT",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpGet("{id:guid}/versions/latest")]
    public async Task<IActionResult> GetLatestVersion(Guid id, [FromQuery] int? localVersionNumber)
    {
        Guid userId = GetUserId();

        DocumentVersion? latest;
        try
        {
            latest = await _sync.GetLatestVersionAsync(userId, id, HttpContext.RequestAborted);
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

        var response = new LatestVersionResponse
        {
            Latest = latest is null
                ? null
                : new VersionResponse
                {
                    Id = latest.Id,
                    VersionNumber = latest.VersionNumber,
                    Sha256 = latest.Sha256,
                    SizeBytes = latest.SizeBytes,
                    CreatedAt = latest.CreatedAt
                },
            DocumentLatestVersion = latest?.VersionNumber ?? 0,
            Conflict = latest is not null && localVersionNumber is not null &&
                       localVersionNumber < latest.VersionNumber
                ? new VersionConflictDetail
                {
                    LatestVersion = latest.VersionNumber,
                    LatestSha256 = latest.Sha256
                }
                : null
        };

        return Ok(response);
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> ListVersions(Guid id)
    {
        Guid userId = GetUserId();

        IReadOnlyList<DocumentVersion> versions;
        try
        {
            versions = await _sync.ListVersionsAsync(userId, id, HttpContext.RequestAborted);
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

        return Ok(versions.Select(v => new VersionResponse
        {
            Id = v.Id,
            VersionNumber = v.VersionNumber,
            Sha256 = v.Sha256,
            SizeBytes = v.SizeBytes,
            CreatedAt = v.CreatedAt
        }));
    }

    [HttpGet("{id:guid}/versions/{versionNumber:int}/content")]
    public async Task<IActionResult> GetVersionContent(Guid id, int versionNumber)
    {
        Guid userId = GetUserId();

        VersionContent content;
        try
        {
            content = await _sync.GetVersionContentAsync(userId, id, versionNumber, HttpContext.RequestAborted);
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

        return File(content.Content, "application/octet-stream", fileDownloadName: $"version-{versionNumber}.bin");
    }

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }

    private static DocumentResponse ToResponse(Document doc) => new()
    {
        Id = doc.Id,
        Name = doc.Name,
        LatestVersion = doc.LatestVersion,
        CreatedAt = doc.CreatedAt,
        UpdatedAt = doc.UpdatedAt
    };
}