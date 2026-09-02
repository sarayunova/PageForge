// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

public sealed class SubscribeRequest
{
    public required string Plan { get; init; }
    public string Interval { get; init; } = "month";
}

public sealed class BillingResponse
{
    public required string Plan { get; init; }
    public required string Status { get; init; }
    public DateTime CurrentPeriodEnd { get; init; }
    public string? StripeSubscriptionId { get; init; }
}

public sealed class PortalResponse
{
    public required string Url { get; init; }
}
