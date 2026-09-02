// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-TEAM-01 integration tests: shared commenting/annotation on a document
/// shared with a team. Covers posting/list/update/delete comments, the team-scope
/// authorization gate (owner and team members may participate, outsiders may not),
/// and the polling <c>?since=</c> cursor used for near-real-time propagation.
/// </summary>
public sealed class TeamReviewApiTests : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly PageForgeApiFactory _factory;
    private readonly HttpClient _client;

    public TeamReviewApiTests()
    {
        _factory = new PageForgeApiFactory();
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Owner_posts_comment_on_shared_document()
    {
        string token = await RegisterAsync("rev-owner@example.com", "Owner", "pw");
        var (docId, versionId) = await CreateSharedDocumentAsync(token, "OwnerOrg", token);

        var created = await SendAsync("POST", $"/api/v1/documents/{docId}/comments", token, new
        {
            documentVersionId = versionId,
            pageNumber = 1,
            anchorRect = "10,20,30,40",
            body = "First draft looks good on page 1."
        });

        Assert.Equal("First draft looks good on page 1.",
            created.RootElement.GetProperty("body").GetString());
        Assert.Equal(1, created.RootElement.GetProperty("pageNumber").GetInt32());
        Assert.Equal("Owner", created.RootElement.GetProperty("authorDisplayName").GetString());
    }

    [Fact]
    public async Task Comment_list_scopes_to_document_and_returns_author()
    {
        string token = await RegisterAsync("rev-list@example.com", "List", "pw");
        var (docId, versionId) = await CreateSharedDocumentAsync(token, "ListOrg", token);

        await SendAsync("POST", $"/api/v1/documents/{docId}/comments", token, new
        {
            documentVersionId = versionId, pageNumber = 2, anchorRect = "1,1,5,5", body = "Note A"
        });
        await SendAsync("POST", $"/api/v1/documents/{docId}/comments", token, new
        {
            documentVersionId = versionId, pageNumber = 3, anchorRect = "2,2,6,6", body = "Note B"
        });

        var listing = await SendAsync("GET", $"/api/v1/documents/{docId}/comments", token);
        Assert.Equal(2, listing.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal("List",
            listing.RootElement.GetProperty("items")[0].GetProperty("authorDisplayName").GetString());
        Assert.Equal("List",
            listing.RootElement.GetProperty("items")[1].GetProperty("authorDisplayName").GetString());
    }

    [Fact]
    public async Task Team_member_can_comment_and_an_outsider_cannot()
    {
        string ownerToken = await RegisterAsync("rev-mem-owner@example.com", "MemberOwner", "pw");
        (Guid memberId, string memberToken) = await RegisterWithIdAsync("rev-member@example.com", "Member", "pw");

        var (docId, versionId) = await CreateSharedDocumentAsync(ownerToken, "MemberOrg", ownerToken);
        await AddMemberAsync(ownerToken, "MemberOrg", memberId);

        // Team member posts successfully.
        var memberComment = await SendAsync("POST", $"/api/v1/documents/{docId}/comments", memberToken, new
        {
            documentVersionId = versionId, pageNumber = 1, anchorRect = "0,0,1,1", body = "Member note"
        });
        Assert.Equal("Member note", memberComment.RootElement.GetProperty("body").GetString());

        // Outsider is denied.
        (_, string outsiderToken) = await RegisterWithIdAsync("rev-outsider@example.com", "Outsider", "pw");
        var denied = await SendRawAsync(HttpMethod.Post, $"/api/v1/documents/{docId}/comments", outsiderToken, new
        {
            documentVersionId = versionId, pageNumber = 1, anchorRect = "0,0,1,1", body = "sneak"
        });
        Assert.Equal(HttpStatusCode.Conflict, denied.StatusCode);
    }

    [Fact]
    public async Task Author_can_update_and_delete_a_comment()
    {
        string token = await RegisterAsync("rev-edit@example.com", "Editor", "pw");
        var (docId, versionId) = await CreateSharedDocumentAsync(token, "EditOrg", token);

        var created = await SendAsync("POST", $"/api/v1/documents/{docId}/comments", token, new
        {
            documentVersionId = versionId, pageNumber = 1, anchorRect = "1,1,2,2", body = "Original"
        });
        Guid commentId = created.RootElement.GetProperty("id").GetGuid();

        var patched = await SendAsync("PATCH", $"/api/v1/documents/{docId}/comments/{commentId}", token, new
        {
            body = "Edited text"
        });
        Assert.Equal("Edited text", patched.RootElement.GetProperty("body").GetString());

        var deleteResponse = await SendRawAsync(HttpMethod.Delete, $"/api/v1/documents/{docId}/comments/{commentId}", token);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var listing = await SendAsync("GET", $"/api/v1/documents/{docId}/comments", token);
        Assert.Equal(0, listing.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Since_cursor_filters_polled_updates()
    {
        string token = await RegisterAsync("rev-poll@example.com", "Poll", "pw");
        var (docId, versionId) = await CreateSharedDocumentAsync(token, "PollOrg", token);

        await SendAsync("POST", $"/api/v1/documents/{docId}/comments", token, new
        {
            documentVersionId = versionId, pageNumber = 1, anchorRect = "1,1,2,2", body = "old comment"
        });

        // Poll with a future 'since' returns nothing new.
        string future = DateTime.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ");
        var empty = await SendAsync("GET", $"/api/v1/documents/{docId}/comments?since={future}", token);
        Assert.Equal(0, empty.RootElement.GetProperty("items").GetArrayLength());

        // Poll without 'since' returns everything.
        var all = await SendAsync("GET", $"/api/v1/documents/{docId}/comments", token);
        Assert.Equal(1, all.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Comments_require_authentication()
    {
        var response = await _client.GetAsync("/api/v1/documents/00000000-0000-0000-0000-000000000000/comments");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<string> RegisterAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync("POST", "/api/v1/accounts/register",
            token: null, body: new { email, displayName, password });
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<(Guid Id, string Token)> RegisterWithIdAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync("POST", "/api/v1/accounts/register",
            token: null, body: new { email, displayName, password });
        Guid id = body.RootElement.GetProperty("user").GetProperty("id").GetGuid();
        return (id, body.RootElement.GetProperty("accessToken").GetString()!);
    }

    /// <summary>
    /// Creates a document owned by <paramref name="ownerToken"/>, pushes version v1,
    /// creates a team owned by the same user, and shares the document with it.
    /// </summary>
    private async Task<(Guid DocId, Guid VersionId)> CreateSharedDocumentAsync(
        string ownerToken, string teamName, string shareToken)
    {
        using JsonDocument doc = await SendAsync("POST", "/api/v1/documents", ownerToken,
            new { name = "review.pdf" }, expected: HttpStatusCode.Created);
        Guid docId = doc.RootElement.GetProperty("id").GetGuid();

        Guid versionId = await PushVersionAsync(ownerToken, docId);

        using JsonDocument team = await SendAsync("POST", "/api/v1/teams", ownerToken,
            new { name = teamName }, expected: HttpStatusCode.Created);
        Guid teamId = team.RootElement.GetProperty("id").GetGuid();

        await SendAsync("POST", $"/api/v1/documents/{docId}/share", shareToken,
            new { teamId }, expected: HttpStatusCode.OK);

        return (docId, versionId);
    }

    private async Task<Guid> PushVersionAsync(string token, Guid docId)
    {
        using var push = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/documents/{docId}/versions?baseVersionNumber=0");
        push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        push.Headers.Add("Idempotency-Key", $"push-{Guid.NewGuid():N}");
        push.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("review-content"));
        push.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        HttpResponseMessage pushResp = await _client.SendAsync(push);
        Assert.Equal(HttpStatusCode.Created, pushResp.StatusCode);

        using JsonDocument versions = await SendAsync("GET", $"/api/v1/documents/{docId}/versions", token);
        return versions.RootElement[0].GetProperty("id").GetGuid();
    }

    private async Task AddMemberAsync(string ownerToken, string teamName, Guid memberId)
    {
        using JsonDocument listing = await SendAsync("GET", "/api/v1/teams", ownerToken);
        Guid teamId = listing.RootElement.EnumerateArray()
            .First(t => t.GetProperty("name").GetString() == teamName)
            .GetProperty("id").GetGuid();

        await SendAsync("POST", $"/api/v1/teams/{teamId}/members", ownerToken,
            new { userId = memberId, role = "Member" }, expected: HttpStatusCode.OK);
    }

    private async Task<JsonDocument> SendAsync(string method, string path, string? token,
        object? body = null, HttpStatusCode? expected = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.SendAsync(request);
        if (expected is not null)
            Assert.Equal(expected.Value, response.StatusCode);

        if (response.StatusCode == HttpStatusCode.NoContent)
            return JsonDocument.Parse("{}");

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, string? token,
        object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
        return await _client.SendAsync(request);
    }
}