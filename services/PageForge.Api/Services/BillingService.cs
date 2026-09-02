// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PageForge.Api.Data;
using Stripe;
using StripeSubscription = Stripe.Subscription;
using DomainSubscription = PageForge.Api.Data.Subscription;
using BillingPortalSessionCreateOptions = Stripe.BillingPortal.SessionCreateOptions;

namespace PageForge.Api.Services;

/// <summary>
/// FR-ACC-01 billing: creates and manages a user's Stripe subscription
/// (create, change plan, cancel, billing-portal session). The Idempotency-Key
/// header is threaded into every mutating Stripe call so a client retry never
/// double-charges. The local <see cref="Subscription"/> row is a projection
/// kept fresh by the Stripe webhook handler.
/// </summary>
public sealed class BillingService : IDisposable
{
    private readonly AppDbContext _db;
    private readonly StripeOptions _options;
    private StripeClient? _client;

    public BillingService(AppDbContext db, IOptions<StripeOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public void Dispose() { }

    /// <summary>
    /// Fail-fast guard for operations that actually talk to Stripe. Retained
    /// lazily so the read-only projection path (<see cref="GetAsync"/>) works
    /// even while credentials are missing.
    /// </summary>
    private StripeClient Client
    {
        get
        {
            if (!_options.IsConfigured)
                throw new InvalidOperationException("Stripe is not configured.");
            if (_options.ReturnUrl is { Length: 0 })
                throw new InvalidOperationException("Stripe:ReturnUrl is not configured.");
            return _client ??= new StripeClient(_options.ApiKey);
        }
    }

    /// <summary>
    /// Subscribes the user's existing-or-created Stripe customer to the named
    /// plan (pro monthly/yearly). Fail-fast: if Stripe cannot be reached or the
    /// price is missing the whole call reverts and returns an error.
    /// </summary>
    public async Task<BillingResult> SubscribeAsync(
        Guid userId, string plan, string interval, string? idempotencyKey,
        CancellationToken ct)
    {
        if (!BillingPlans.IsKnown(plan))
            throw new ArgumentException($"Unknown plan '{plan}'.", nameof(plan));

        string priceId = interval switch
        {
            "month" => _options.MonthlyPriceId,
            "year" => _options.YearlyPriceId,
            _ => throw new ArgumentException(
                $"Interval must be 'month' or 'year', got '{interval}'.", nameof(interval))
        };

        if (string.IsNullOrEmpty(priceId))
            throw new InvalidOperationException($"No Stripe price configured for interval '{interval}'.");

        User user = await _db.Users.FindAsync([userId], ct)
            ?? throw new KeyNotFoundException("User not found.");

        var requestOptions = new RequestOptions { IdempotencyKey = idempotencyKey };

        string customerId;
        DomainSubscription? existing = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        if (existing is not null && !string.IsNullOrEmpty(existing.StripeCustomerId))
        {
            customerId = existing.StripeCustomerId;
        }
        else
        {
            var customer = await Client.V1.Customers.CreateAsync(new CustomerCreateOptions
            {
                Email = user.Email,
                Name = user.DisplayName,
                Metadata = new Dictionary<string, string>
                {
                    ["pgUserId"] = user.Id.ToString()
                }
            }, requestOptions, ct);
            customerId = customer.Id;
        }

        if (existing is not null && !string.IsNullOrEmpty(existing.StripeSubscriptionId))
        {
            StripeSubscription current = await Client.V1.Subscriptions.GetAsync(
                existing.StripeSubscriptionId, null, null, ct);
            string currentItemId = current.Items?.Data?.FirstOrDefault()?.Id ?? string.Empty;

            // Change the existing subscription's plan item to the new price.
            var update = await Client.V1.Subscriptions.UpdateAsync(
                existing.StripeSubscriptionId,
                new SubscriptionUpdateOptions
                {
                    Items = new List<SubscriptionItemOptions>
                    {
                        new() { Id = currentItemId, Price = priceId }
                    },
                    ProrationBehavior = "create_prorations"
                },
                requestOptions, ct);

            await UpsertProjectionAsync(userId, customerId, update, ct);
            return ToResult(update);
        }

        var created = await Client.V1.Subscriptions.CreateAsync(new SubscriptionCreateOptions
        {
            Customer = customerId,
            Items = new List<SubscriptionItemOptions> { new() { Price = priceId } },
            Metadata = new Dictionary<string, string>
            {
                ["pgPlan"] = plan,
                ["pgInterval"] = interval
            }
        }, requestOptions, ct);

        await UpsertProjectionAsync(userId, customerId, created, ct);
        return ToResult(created);
    }

    /// <summary>
    /// Cancels the user's current subscription immediately (or at period end
    /// via Stripe settings). Returns the cancelled subscription projection.
    /// </summary>
    public async Task<BillingResult> CancelAsync(Guid userId, string? idempotencyKey, CancellationToken ct)
    {
        DomainSubscription? existing = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? throw new KeyNotFoundException("No subscription to cancel.");

        if (string.IsNullOrEmpty(existing.StripeSubscriptionId))
            throw new InvalidOperationException("No linked subscription.");

        var cancelled = await Client.V1.Subscriptions.CancelAsync(
            existing.StripeSubscriptionId,
            new SubscriptionCancelOptions(),
            new RequestOptions { IdempotencyKey = idempotencyKey },
            ct);

        await UpsertProjectionAsync(userId, existing.StripeCustomerId, cancelled, ct);
        return ToResult(cancelled);
    }

    /// <summary>
    /// Creates a Stripe billing-portal session so the user can self-manage
    /// payment method, invoices, and cancellation. The Stripe customer must
    /// already exist.
    /// </summary>
    public async Task<string> CreatePortalSessionAsync(Guid userId, CancellationToken ct)
    {
        DomainSubscription? existing = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, ct)
            ?? throw new KeyNotFoundException("No subscription; cannot open the billing portal.");

        if (string.IsNullOrEmpty(existing.StripeCustomerId))
            throw new InvalidOperationException("No Stripe customer linked.");

        var session = await Client.V1.BillingPortal.Sessions.CreateAsync(
            new BillingPortalSessionCreateOptions
            {
                Customer = existing.StripeCustomerId,
                ReturnUrl = _options.ReturnUrl
            },
            null,
            ct);

        return session.Url;
    }

    /// <summary>
    /// Returns the persisted subscription for a user, or null if they have
    /// never subscribed. A null result maps to the Free plan.
    /// </summary>
    public async Task<BillingResult?> GetAsync(Guid userId, CancellationToken ct)
    {
        DomainSubscription? existing = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        return existing is null ? null : new BillingResult
        {
            PlanId = existing.PlanId,
            Status = existing.Status,
            CurrentPeriodEnd = existing.CurrentPeriodEnd,
            StripeSubscriptionId = existing.StripeSubscriptionId
        };
    }

    /// <summary>Patches the cached projection from a Stripe subscription object.</summary>
    public async Task UpsertProjectionAsync(
        Guid userId, string customerId, StripeSubscription stripeSubscription, CancellationToken ct)
    {
        DomainSubscription? row = await _db.Subscriptions
            .FirstOrDefaultAsync(s => s.UserId == userId, ct);

        string planId =
            stripeSubscription.Items?.Data?.FirstOrDefault()?.Price?.ProductId ?? string.Empty;

        if (row is null)
        {
            row = new DomainSubscription
            {
                UserId = userId,
                StripeCustomerId = customerId,
                StripeSubscriptionId = stripeSubscription.Id,
                PlanId = planId,
                Status = stripeSubscription.Status,
                CurrentPeriodEnd = stripeSubscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd ?? default,
                LastUpdatedAt = DateTime.UtcNow
            };
            _db.Subscriptions.Add(row);
        }
        else
        {
            row.StripeSubscriptionId = stripeSubscription.Id;
            row.StripeCustomerId = customerId;
            row.PlanId = string.IsNullOrEmpty(planId) ? row.PlanId : planId;
            row.Status = stripeSubscription.Status;
            row.CurrentPeriodEnd = stripeSubscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd ?? default;
            row.LastUpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    private static BillingResult ToResult(StripeSubscription stripeSubscription) => new()
    {
        PlanId = stripeSubscription.Items?.Data?.FirstOrDefault()?.Price?.ProductId ?? string.Empty,
        Status = stripeSubscription.Status,
        CurrentPeriodEnd = stripeSubscription.Items?.Data?.FirstOrDefault()?.CurrentPeriodEnd ?? default,
        StripeSubscriptionId = stripeSubscription.Id
    };
}

public sealed class BillingResult
{
    public string PlanId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public DateTime CurrentPeriodEnd { get; init; }
    public string StripeSubscriptionId { get; init; } = string.Empty;
    public string PortalUrl { get; init; } = string.Empty;
}
