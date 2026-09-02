// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PageForge.Api.Services;
using Stripe;
using BillingService = PageForge.Api.Services.BillingService;

namespace PageForge.Api.Controllers;

/// <summary>
/// Receives signed Stripe webhook events and keeps the cached
/// <see cref="Subscription"/> projection fresh. The request is
/// intentionally NOT [Authorize]d â€” authenticity is established by Stripe's
/// signature header verified against the configured webhook secret.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/billing/webhooks")]
public sealed class BillingWebhookController : ControllerBase
{
    private readonly StripeOptions _options;
    private readonly BillingService _billing;
    private readonly ILogger<BillingWebhookController> _logger;

    public BillingWebhookController(
        IOptions<StripeOptions> options,
        BillingService billing,
        ILogger<BillingWebhookController> logger)
    {
        _options = options.Value;
        _billing = billing;
        _logger = logger;
    }

    [HttpPost]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_options.WebhookSecret))
        {
            _logger.LogWarning("Stripe webhook received but WebhookSecret is not configured.");
            return StatusCode(503);
        }

        string json = await new StreamReader(Request.Body).ReadToEndAsync(ct);
        string signature = Request.Headers["Stripe-Signature"].ToString();

        Event? @event;
        try
        {
            @event = EventUtility.ConstructEvent(json, signature, _options.WebhookSecret);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning(ex, "Invalid Stripe webhook signature.");
            return BadRequest();
        }

        try
        {
            switch (@event.Type)
            {
                case EventTypes.CustomerSubscriptionUpdated:
                case EventTypes.CustomerSubscriptionCreated:
                case EventTypes.CustomerSubscriptionDeleted:
                    await HandleSubscriptionChangedAsync(@event, ct);
                    break;
                default:
                    _logger.LogInformation("Ignoring Stripe event type {Type}.", @event.Type);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile Stripe event {Type}.", @event.Type);
            return StatusCode(500);
        }

        return Ok();
    }

    private async Task HandleSubscriptionChangedAsync(Event @event, CancellationToken ct)
    {
        if (@event.Data.Object is not Subscription stripeSubscription)
            return;

        if (stripeSubscription.Metadata is not null &&
            stripeSubscription.Metadata.TryGetValue("pgUserId", out string? pgUserId) &&
            Guid.TryParse(pgUserId, out Guid userId))
        {
            await _billing.UpsertProjectionAsync(userId, stripeSubscription.CustomerId, stripeSubscription, ct);
        }
    }
}
