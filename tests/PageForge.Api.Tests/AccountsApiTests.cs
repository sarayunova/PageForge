// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-ACC-01 integration tests over the accounts + auth, team, and AGPL
/// /source endpoints. Each test boots the full web host against an in-memory
/// store, so the auth code path (password hashing, token issuance) and the
/// controller wiring are exercised for real.
/// </summary>
public sealed class AccountsApiTests : IDisposable
{
    private readonly PageForgeApiFactory _factory = new();
    private readonly HttpClient _client;

    public AccountsApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Register_login_and_me_round_trip()
    {
        RegisterResponse reg = await RegisterAsync("alice@example.com", "Alice", "super-secret");

        Assert.False(string.IsNullOrEmpty(reg.AccessToken));
        Assert.False(string.IsNullOrEmpty(reg.RefreshToken));
        Assert.Equal("alice@example.com", reg.User.Email);
        Assert.Equal("local", reg.User.AuthProvider);

        // me with the issued access token
        var me = await SendAsync<JsonElement>(
            HttpMethod.Get, "/api/v1/accounts/me", reg.AccessToken);
        Assert.Equal("alice@example.com", me.GetProperty("email").GetString());

        // login with correct password
        var login = await SendAsync<JsonElement>(
            HttpMethod.Post, "/api/v1/accounts/login",
            token: null, new { email = "alice@example.com", password = "super-secret" },
            expected: HttpStatusCode.OK);
        Assert.Equal("alice@example.com", login.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task Register_duplicate_email_is_conflict()
    {
        await RegisterAsync("dup@example.com", "One", "password-1");

        var response = await _client.PostAsJsonAsync("/api/v1/accounts/register", new
        {
            email = "dup@example.com",
            displayName = "Two",
            password = "password-2"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("EMAIL_EXISTS", body.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Login_wrong_password_is_unauthorized()
    {
        await RegisterAsync("bob@example.com", "Bob", "right-password");

        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/v1/accounts/login", new
        {
            email = "bob@example.com",
            password = "wrong-password"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INVALID_CREDENTIALS", body.GetProperty("error").GetProperty("code").GetString());
        Assert.True(body.GetProperty("error").TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_pair()
    {
        RegisterResponse reg = await RegisterAsync("carol@example.com", "Carol", "pw");

        var refresh = await SendAsync<JsonElement>(
            HttpMethod.Post, "/api/v1/accounts/refresh",
            token: null, new { refreshToken = reg.RefreshToken },
            expected: HttpStatusCode.OK);

        Assert.False(string.IsNullOrEmpty(refresh.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrEmpty(refresh.GetProperty("refreshToken").GetString()));
        Assert.NotEqual(reg.RefreshToken, refresh.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task Refresh_revoked_token_is_unauthorized()
    {
        RegisterResponse reg = await RegisterAsync("dave@example.com", "Dave", "pw");

        // First rotation invalidates the original refresh token.
        await SendAsync<JsonElement>(
            HttpMethod.Post, "/api/v1/accounts/refresh",
            token: null, new { refreshToken = reg.RefreshToken },
            expected: HttpStatusCode.OK);

        await SendAsync<JsonElement>(
            HttpMethod.Post, "/api/v1/accounts/refresh",
            token: null, new { refreshToken = reg.RefreshToken },
            expected: HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_without_token_is_unauthorized()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/accounts/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_oauth_provider_is_bad_request()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/accounts/login/github");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("UNSUPPORTED_PROVIDER", body.GetProperty("error").GetProperty("code").GetString());
    }

    // --- Teams --------------------------------------------------------------

    [Fact]
    public async Task Create_and_list_team()
    {
        RegisterResponse owner = await RegisterAsync("team-owner@example.com", "Owner", "pw");

        var created = await SendAsync<JsonElement>(
            HttpMethod.Post, "/api/v1/teams",
            owner.AccessToken, new { name = "Acme" },
            expected: HttpStatusCode.Created);
        Assert.Equal("Acme", created.GetProperty("name").GetString());

        var listing = await SendAsync<JsonElement>(
            HttpMethod.Get, "/api/v1/teams", owner.AccessToken);
        Assert.Equal("Acme", listing[0].GetProperty("name").GetString());
        Assert.Equal(1, listing.GetArrayLength());
    }

    [Fact]
    public async Task Teams_list_requires_authentication()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/teams");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- AGPL source endpoint ----------------------------------------------

    [Fact]
    public async Task Source_endpoint_reports_agpl()
    {
        HttpResponseMessage response = await _client.GetAsync("/api/v1/source");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("AGPL-3.0-only", json.RootElement.GetProperty("license").GetString());
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<RegisterResponse> RegisterAsync(string email, string displayName, string password)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post, "/api/v1/accounts/register",
            token: null, new { email, displayName, password },
            expected: HttpStatusCode.OK);

        return new RegisterResponse(
            response.GetProperty("accessToken").GetString()!,
            response.GetProperty("refreshToken").GetString()!,
            new UserResponse(
                response.GetProperty("user").GetProperty("email").GetString()!,
                response.GetProperty("user").GetProperty("authProvider").GetString()!));
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string path, string? token, object? body = null,
        HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private sealed record RegisterResponse(string AccessToken, string RefreshToken, UserResponse User);
    private sealed record UserResponse(string Email, string AuthProvider);
}
