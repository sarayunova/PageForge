// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PageForge.Api.Models;
using PageForge.Api.Services;

namespace PageForge.Api.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/billing")]
public sealed class BillingController : ControllerBase
{
    private readonly BillingService _billing;

    public BillingController(BillingService billing) => _billing = billing;

    [HttpPost("subscribe")]
    public async Task<IActionResult> Subscribe([FromBody] SubscribeRequest request)
    {
        Guid userId = GetUserId();
        string? idempotencyKey = HttpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();

        try
        {
            BillingResult result = await _billing.SubscribeAsync(
                userId, request.Plan, request.Interval, idempotencyKey, HttpContext.RequestAborted);

            return Ok(new BillingResponse
            {
                Plan = result.PlanId,
                Status = result.Status,
                CurrentPeriodEnd = result.CurrentPeriodEnd,
                StripeSubscriptionId = result.StripeSubscriptionId
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "INVALID_PLAN",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
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
                    Code = "BILLING_UNAVAILABLE",
                    Message = ex.Message,
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpDelete("subscription")]
    public async Task<IActionResult> Cancel()
    {
        Guid userId = GetUserId();
        string? idempotencyKey = HttpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();

        try
        {
            BillingResult result = await _billing.CancelAsync(userId, idempotencyKey, HttpContext.RequestAborted);
            return Ok(new BillingResponse
            {
                Plan = result.PlanId,
                Status = result.Status,
                CurrentPeriodEnd = result.CurrentPeriodEnd,
                StripeSubscriptionId = result.StripeSubscriptionId
            });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NO_SUBSCRIPTION",
                    Message = "No active subscription to cancel.",
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpPost("portal")]
    public async Task<IActionResult> Portal()
    {
        Guid userId = GetUserId();

        try
        {
            string url = await _billing.CreatePortalSessionAsync(userId, HttpContext.RequestAborted);
            return Ok(new PortalResponse { Url = url });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new ErrorResponse
            {
                Error = new ErrorDetail
                {
                    Code = "NO_SUBSCRIPTION",
                    Message = "No subscription; the billing portal is unavailable.",
                    TraceId = HttpContext.TraceIdentifier
                }
            });
        }
    }

    [HttpGet("subscription")]
    public async Task<IActionResult> GetSubscription()
    {
        Guid userId = GetUserId();
        BillingResult? result = await _billing.GetAsync(userId, HttpContext.RequestAborted);

        if (result is null)
            return Ok(new BillingResponse { Plan = BillingPlans.Free, Status = "none" });

        return Ok(new BillingResponse
        {
            Plan = result.PlanId,
            Status = result.Status,
            CurrentPeriodEnd = result.CurrentPeriodEnd,
            StripeSubscriptionId = result.StripeSubscriptionId
        });
    }

    private Guid GetUserId()
    {
        string? sub = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.Parse(sub ?? throw new UnauthorizedAccessException());
    }
}
