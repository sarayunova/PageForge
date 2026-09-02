// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Data;

/// <summary>
/// Persisted view of a user's Stripe subscription, mirroring the fields the
/// API needs for FR-ACC-01 billing: which customer/subscription Stripe owns,
/// what plan is held, and its lifecycle status. The source of truth is Stripe;
/// this table is a cached projection updated from webhook events.
/// </summary>
public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public string StripeCustomerId { get; set; } = string.Empty;

    public string StripeSubscriptionId { get; set; } = string.Empty;

    public string PlanId { get; set; } = string.Empty;

    public string Status { get; set; } = "incomplete";

    public DateTime CurrentPeriodEnd { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}
