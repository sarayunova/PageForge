// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services;

/// <summary>
/// Stable plan identifiers and their display metadata used on both sides of
/// the subscription offer. The Stripe <c>price_</c> IDs are mapped to these
/// identifiers in <see cref="StripeOptions"/>.
/// </summary>
public static class BillingPlans
{
    public const string Free = "free";
    public const string Pro = "pro";

    public static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Free] = "Free",
            [Pro] = "Pro"
        };

    public static bool IsKnown(string plan) =>
        string.Equals(plan, Free, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(plan, Pro, StringComparison.OrdinalIgnoreCase);
}
