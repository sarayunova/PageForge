// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PageForge.Api.Data;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-ACC-01 billing integration tests. Stripe is intentionally NOT configured
/// in the test environment (no live key, no network), so these tests exercise
/// the fail-fast validation paths, the auth gate, and the local subscription
/// projection read-end-to-end through the controller + in-memory store.
/// </summary>
public sealed class BillingApiTests : IDisposable
{
    private readonly PageForgeApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public BillingApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Subscribe_requires_authentication()
    {
        var response = await _client.PostAsync("/api/v1/billing/subscribe", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Get_subscription_requires_authentication()
    {
        var response = await _client.GetAsync("/api/v1/billing/subscription");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Subscribe_unknown_plan_is_bad_request()
    {
        string token = await RegisterAsync("bill@example.com", "Bill", "pw");

        using JsonDocument body = await SendAsync(
            HttpMethod.Post, "/api/v1/billing/subscribe", token,
            body: new { plan = "enterprise", interval = "month" },
            expected: HttpStatusCode.BadRequest);

        Assert.Equal("INVALID_PLAN", body.RootElement
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Subscribe_valid_plan_without_stripe_config_is_conflict()
    {
        // Stripe is unconfigured in the test host, so subscribe fails fast at
        // the "no price configured" guard and returns 409 rather than calling out.
        string token = await RegisterAsync("bill2@example.com", "Bill", "pw");

        using JsonDocument body = await SendAsync(
            HttpMethod.Post, "/api/v1/billing/subscribe", token,
            body: new { plan = "pro", interval = "month" },
            expected: HttpStatusCode.Conflict);

        Assert.Equal("BILLING_UNAVAILABLE", body.RootElement
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task User_with_no_subscription_maps_to_free()
    {
        string token = await RegisterAsync("free@example.com", "Free", "pw");

        using JsonDocument body = await SendAsync(
            HttpMethod.Get, "/api/v1/billing/subscription", token);

        Assert.Equal("free", body.RootElement.GetProperty("plan").GetString());
        Assert.Equal("none", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Persisted_subscription_is_returned_by_get()
    {
        string token = await RegisterAsync("pro@example.com", "Pro", "pw");
        string stripeSubId = $"sub_{Guid.NewGuid():N}";
        string stripeCustId = $"cus_{Guid.NewGuid():N}";
        DateTime periodEnd = DateTime.UtcNow.AddDays(30);

        await SeedSubscriptionAsync("pro@example.com", stripeCustId, stripeSubId, "pro", periodEnd);

        using JsonDocument body = await SendAsync(
            HttpMethod.Get, "/api/v1/billing/subscription", token);

        Assert.Equal("pro", body.RootElement.GetProperty("plan").GetString());
        Assert.Equal(stripeSubId, body.RootElement.GetProperty("stripeSubscriptionId").GetString());
        Assert.Equal(periodEnd, body.RootElement
            .GetProperty("currentPeriodEnd").GetDateTime().ToUniversalTime());
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<string> RegisterAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync(
            HttpMethod.Post, "/api/v1/accounts/register",
            token: null,
            body: new { email, displayName, password });

        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task SeedSubscriptionAsync(
        string email, string customerId, string subscriptionId, string planId, DateTime periodEnd)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var user = await db.Users.FirstAsync(u => u.Email == email);

        db.Subscriptions.Add(new Subscription
        {
            UserId = user.Id,
            StripeCustomerId = customerId,
            StripeSubscriptionId = subscriptionId,
            PlanId = planId,
            Status = "active",
            CurrentPeriodEnd = periodEnd
        });
        await db.SaveChangesAsync();
    }

    private async Task<JsonDocument> SendAsync(
        HttpMethod method, string path, string? token, object? body = null,
        HttpStatusCode? expected = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.SendAsync(request);
        if (expected is not null)
            Assert.Equal(expected.Value, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
