// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PageForge.Api.Data;
using PageForge.Api.Models;
using PageForge.Api.Services;

namespace PageForge.Api.Controllers;

/// <summary>
/// Batch OCR / conversion jobs (FR-BATCH-01): submit a multi-document job, poll it
/// for status, list past jobs, and download produced artifacts. Usage is metered
/// per account.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/ocr-jobs")]
public sealed class OcrJobsController : ControllerBase
{
    private readonly OcrJobsService _jobs;
    private readonly IBlobStorage _blobs;
    private readonly SyncOptions _sync;

    public OcrJobsController(OcrJobsService jobs, IBlobStorage blobs, IOptions<SyncOptions> sync)
    {
        _jobs = jobs;
        _blobs = blobs;
        _sync = sync.Value;
    }

    [HttpPost]
    public async Task<IActionResult> Submit([FromBody] SubmitOcrJobRequest request)
    {
        Guid userId = GetUserId();
        string? idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();

        if (!TryParse(request.JobType, out OcrJobType type))
            return BadRequest(Error("INVALID_REQUEST", $"Unknown job type '{request.JobType}'."));
        if (!TryParseFormat(request.TargetFormat, out OcrTargetFormat format))
            return BadRequest(Error("INVALID_REQUEST", $"Unknown target format '{request.TargetFormat}'."));

        OcrJob job;
        try
        {
            job = await _jobs.SubmitAsync(
                userId, request.DocumentVersionIds, type, format, idempotencyKey, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "QUOTA_OR_VALIDATION",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }

        var (jobView, usage, quota) = await _jobs.GetForOwnerAsync(userId, job.Id, HttpContext.RequestAborted);
        var created = ToResponse(jobView!);
        created.UsagePages = usage;
        created.UsageQuota = quota;
        return CreatedAtAction(nameof(Get), new { id = job.Id }, created);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        Guid userId = GetUserId();
        var (job, usage, quota) = await _jobs.GetForOwnerAsync(userId, id, HttpContext.RequestAborted);
        if (job is null)
            return NotFound(Error("NOT_FOUND", "Job not found."));

        var response = ToResponse(job);
        response.UsagePages = usage;
        response.UsageQuota = quota;
        return Ok(response);
    }

    /// <summary>Downloads a produced artifact (e.g. an OCR'd searchable PDF).</summary>
    [HttpGet("{id:guid}/items/{itemId:guid}/result")]
    public async Task<IActionResult> DownloadResult(Guid id, Guid itemId)
    {
        Guid userId = GetUserId();
        var (job, _, _) = await _jobs.GetForOwnerAsync(userId, id, HttpContext.RequestAborted);
        OcrJobItem? item = job?.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is null || item.OutputVersionId is null || item.OutputVersion is null)
            return NotFound(Error("NOT_FOUND", "No produced result is available for this item."));

        Stream content = await _blobs.GetAsync(_sync.Bucket, item.OutputVersion.BlobKey, HttpContext.RequestAborted);
        return new FileStreamResult(content, item.OutputContentType ?? "application/octet-stream")
        {
            FileDownloadName = item.OutputFileName ?? "result"
        };
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? cursor, [FromQuery] int limit = 20)
    {
        Guid userId = GetUserId();
        int bounded = Math.Clamp(limit, 1, 100);
        IReadOnlyList<OcrJob> jobs = await _jobs.ListForOwnerAsync(userId, bounded, HttpContext.RequestAborted);

        return Ok(new OcrJobListResponse
        {
            Items = jobs.Select(ToResponse).ToList(),
            NextCursor = null
        });
    }

    private static bool TryParse(string value, out OcrJobType type)
    {
        if (string.Equals(value, "ocr", StringComparison.OrdinalIgnoreCase))
        {
            type = OcrJobType.Ocr;
            return true;
        }
        if (string.Equals(value, "convert", StringComparison.OrdinalIgnoreCase))
        {
            type = OcrJobType.Convert;
            return true;
        }
        type = default;
        return false;
    }

    private static bool TryParseFormat(string value, out OcrTargetFormat format)
    {
        foreach (OcrTargetFormat f in Enum.GetValues<OcrTargetFormat>())
        {
            if (string.Equals(value, f.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                format = f;
                return true;
            }
        }
        format = default;
        return false;
    }

    private static OcrJobResponse ToResponse(OcrJob j) => new()
    {
        Id = j.Id,
        JobType = j.JobType.ToString().ToLowerInvariant(),
        TargetFormat = j.TargetFormat.ToString(),
        Status = j.Status.ToString(),
        PagesProcessed = j.PagesProcessed,
        CreatedAt = j.CreatedAt,
        CompletedAt = j.CompletedAt,
        FailedAt = j.FailedAt,
        ErrorMessage = j.ErrorMessage,
        Items = j.Items.Select(i => new OcrJobItemResponse
        {
            Id = i.Id,
            DocumentVersionId = i.DocumentVersionId,
            OutputVersionId = i.OutputVersionId,
            OutputFileName = i.OutputFileName,
            OutputContentType = i.OutputContentType,
            Status = i.Status.ToString(),
            PagesProcessed = i.PagesProcessed,
            CreatedAt = i.CreatedAt,
            CompletedAt = i.CompletedAt,
            ErrorMessage = i.ErrorMessage
        }).ToList()
    };

    private ErrorResponse Error(string code, string message) => new()
    {
        Error = new ErrorDetail
        {
            Code = code,
            Message = message,
            TraceId = HttpContext.TraceIdentifier
        }
    };

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }
}