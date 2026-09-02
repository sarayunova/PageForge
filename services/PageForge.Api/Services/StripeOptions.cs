// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services;

/// <summary>
/// Strongly-typed Stripe configuration read from the "Stripe" section of
/// appsettings.json / environment. The API key and webhook secret are never
/// logged or returned to clients.
/// </summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    public string ApiKey { get; init; } = string.Empty;

    public string WebhookSecret { get; init; } = string.Empty;

    /// <summary>Price IDs offered to users, keyed by a stable plan identifier.</summary>
    public string MonthlyPriceId { get; init; } = string.Empty;

    public string YearlyPriceId { get; init; } = string.Empty;

    /// <summary>Front-end URL the billing-portal session returns to after logout.</summary>
    public string ReturnUrl { get; init; } = string.Empty;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(MonthlyPriceId);
}
