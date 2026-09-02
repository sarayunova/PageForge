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
[Route("api/v1/signature-requests")]
public sealed class SignatureRequestsController : ControllerBase
{
    private readonly EsignService _esign;

    public SignatureRequestsController(EsignService esign) => _esign = esign;

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSignatureRequest request)
    {
        string? idempotencyKey = Request.Headers["Idempotency-Key"].FirstOrDefault();
        Guid userId = GetUserId();

        SignatureRequest created;
        try
        {
            created = await _esign.CreateAsync(
                userId, request.DocumentVersionId, request.Title,
                request.Signers.Select(s => (s.Email, s.DisplayName)).ToList(),
                idempotencyKey, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_REQUEST",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }

        SignatureRequest? loaded = await _esign.GetAsync(userId, created.Id, HttpContext.RequestAborted);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, ToResponse(loaded!));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? cursor, [FromQuery] int limit = 20)
    {
        Guid userId = GetUserId();
        int bounded = Math.Clamp(limit, 1, 100);
        var items = await _esign.ListAsync(userId, cursor, bounded, HttpContext.RequestAborted);
        bool hasMore = items.Count > bounded;

        return Ok(new
        {
            Items = items.Take(bounded).Select(ToResponse),
            NextCursor = hasMore ? (cursor ?? 0) + bounded : null as int?
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        return Ok(ToResponse(await LoadAsync(id)));
    }

    [HttpPost("{id:guid}/send")]
    public async Task<IActionResult> Send(Guid id, [FromBody] SendSignatureRequest request)
    {
        Guid userId = GetUserId();
        try
        {
            await _esign.SendAsync(userId, id, request.ReminderDays, HttpContext.RequestAborted);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_STATE",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }

        return Ok(ToResponse(await LoadAsync(id)));
    }

    [HttpPost("{id:guid}/signers/{email}/view")]
    public async Task<IActionResult> MarkViewed(Guid id, string email)
    {
        Guid userId = GetUserId();
        try
        {
            await _esign.MarkViewedAsync(userId, id, email, HttpContext.RequestAborted);
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

        return Ok();
    }

    [HttpPost("{id:guid}/signers/{email}/sign")]
    public async Task<IActionResult> Sign(Guid id, string email)
    {
        Guid userId = GetUserId();
        try
        {
            var updated = await _esign.SignAsync(userId, id, email, HttpContext.RequestAborted);
            return Ok(ToResponse(updated));
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_STATE",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpPost("{id:guid}/signers/{email}/decline")]
    public async Task<IActionResult> Decline(Guid id, string email, [FromBody] SignActionRequest request)
    {
        Guid userId = GetUserId();
        try
        {
            var updated = await _esign.DeclineAsync(userId, id, email, request.DeclineReason, HttpContext.RequestAborted);
            return Ok(ToResponse(updated));
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
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_STATE",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpGet("{id:guid}/audit")]
    public async Task<IActionResult> GetAudit(Guid id)
    {
        Guid userId = GetUserId();
        var events = await _esign.GetAuditTrailAsync(userId, id, HttpContext.RequestAborted);

        return Ok(events.Select(a => new AuditEventResponse
        {
            ActorEmail = a.ActorEmail,
            Action = a.Action,
            Detail = a.Detail,
            CreatedAt = a.CreatedAt
        }));
    }

    [HttpGet("{id:guid}/certificate")]
    public async Task<IActionResult> GetCertificate(Guid id)
    {
        Guid userId = GetUserId();
        var request = await _esign.GetCertificateAsync(userId, id, HttpContext.RequestAborted);

        return Ok(new CompletionCertificateResponse
        {
            SignatureRequestId = request.Id,
            Title = request.Title,
            CompletedAt = request.CompletedAt ?? request.CreatedAt,
            Signers = request.Signers.OrderBy(s => s.Order).Select(s => new CertificateSignerRow
            {
                Email = s.Email,
                DisplayName = s.DisplayName,
                SignedAt = s.SignedAt,
                Status = s.Status.ToString()
            }).ToList(),
            AuditTrail = request.AuditEvents.OrderBy(a => a.CreatedAt).Select(a => new CertificateAuditRow
            {
                ActorEmail = a.ActorEmail,
                Action = a.Action,
                Detail = a.Detail,
                CreatedAt = a.CreatedAt
            }).ToList()
        });
    }

    private async Task<SignatureRequest> LoadAsync(Guid id)
        => await _esign.GetAsync(GetUserId(), id, HttpContext.RequestAborted);

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }

    private static SignatureRequestResponse ToResponse(SignatureRequest r) => new()
    {
        Id = r.Id,
        DocumentVersionId = r.DocumentVersionId,
        Title = r.Title,
        Status = r.Status.ToString(),
        CreatedAt = r.CreatedAt,
        SentAt = r.SentAt,
        CompletedAt = r.CompletedAt,
        Signers = r.Signers.OrderBy(s => s.Order).Select(ToSigner).ToList()
    };

    private static SignerResponse ToSigner(Signer s) => new()
    {
        Id = s.Id,
        Email = s.Email,
        DisplayName = s.DisplayName,
        Status = s.Status.ToString(),
        Order = s.Order,
        ViewedAt = s.ViewedAt,
        SignedAt = s.SignedAt,
        DeclineReason = s.DeclineReason
    };
}