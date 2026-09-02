// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-SYNC-01/02 integration tests over the documents + versions API. The blob
/// storage is the in-memory <see cref="FakeBlobStorage"/>; the DB is the same
/// in-memory provider shared via the factory, so pushes, conflict detection,
/// and content round-trips run hermetically.
/// </summary>
public sealed class SyncApiTests : IDisposable
{
    private readonly PageForgeApiFactory _factory = new();
    private readonly HttpClient _client;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public SyncApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Documents_require_authentication()
    {
        var response = await _client.GetAsync("/api/v1/documents");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Create_document_returns_metadata()
    {
        string token = await RegisterAsync("sync-create@example.com", "Create", "pw");

        var doc = await CreateDocumentAsync(token, "contract.pdf");

        Assert.Equal("contract.pdf", doc.GetProperty("name").GetString());
        Assert.Equal(0, doc.GetProperty("latestVersion").GetInt32());
    }

    [Fact]
    public async Task Push_version_then_latest_round_trip()
    {
        string token = await RegisterAsync("sync-push@example.com", "Push", "pw");
        var doc = await CreateDocumentAsync(token, "report.pdf");
        Guid docId = doc.GetProperty("id").GetGuid();
        byte[] v1 = Encoding.UTF8.GetBytes("version-one-content");

        var pushed = await PushVersionAsync(token, docId, baseVersion: 0, v1, expected: HttpStatusCode.Created);

        Assert.Equal(1, pushed.GetProperty("versionNumber").GetInt32());
        Assert.Equal(v1.Length, pushed.GetProperty("sizeBytes").GetInt64());

        // latest reflects the pushed version
        using (JsonDocument latest = await SendAsync("GET", $"/api/v1/documents/{docId}/versions/latest", token))
            Assert.Equal(1, latest.RootElement.GetProperty("latest").GetProperty("versionNumber").GetInt32());

        // raw content round-trips byte-for-byte
        using (var contentRequest = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/documents/{docId}/versions/1/content"))
        {
            contentRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var content = await _client.SendAsync(contentRequest);
            Assert.Equal(HttpStatusCode.OK, content.StatusCode);
            byte[] fetched = await content.Content.ReadAsByteArrayAsync();
            Assert.Equal(v1, fetched);
        }
    }

    [Fact]
    public async Task Push_from_stale_base_is_conflict()
    {
        string token = await RegisterAsync("sync-conflict@example.com", "Conflict", "pw");
        var doc = await CreateDocumentAsync(token, "shared.pdf");
        Guid docId = doc.GetProperty("id").GetGuid();

        await PushVersionAsync(token, docId, 0, Encoding.UTF8.GetBytes("v1"), HttpStatusCode.Created);
        await PushVersionAsync(token, docId, 1, Encoding.UTF8.GetBytes("v2"), HttpStatusCode.Created);

        // A second device still on v1 tries to push → the server head is v2.
        var conflict = await PushVersionAsync(token, docId, 1, Encoding.UTF8.GetBytes("stale-v2"), HttpStatusCode.Conflict);

        Assert.Equal("VERSION_CONFLICT", conflict.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Latest_marks_conflict_when_local_is_behind()
    {
        string token = await RegisterAsync("sync-latest@example.com", "Latest", "pw");
        var doc = await CreateDocumentAsync(token, "doc.pdf");
        Guid docId = doc.GetProperty("id").GetGuid();

        await PushVersionAsync(token, docId, 0, Encoding.UTF8.GetBytes("v1"), HttpStatusCode.Created);
        await PushVersionAsync(token, docId, 1, Encoding.UTF8.GetBytes("v2"), HttpStatusCode.Created);

        // Client is on v1 but server head is v2 → conflict metadata surfaced (FR-SYNC-02).
        using (JsonDocument latest = await SendAsync(
            "GET", $"/api/v1/documents/{docId}/versions/latest?localVersionNumber=1", token))
        {
            Assert.True(latest.RootElement.TryGetProperty("conflict", out JsonElement conflict));
            Assert.Equal(2, conflict.GetProperty("latestVersion").GetInt32());
        }
    }

    [Fact]
    public async Task List_versions_returns_history_descending()
    {
        string token = await RegisterAsync("sync-list@example.com", "List", "pw");
        var doc = await CreateDocumentAsync(token, "history.pdf");
        Guid docId = doc.GetProperty("id").GetGuid();

        await PushVersionAsync(token, docId, 0, Encoding.UTF8.GetBytes("v1"), HttpStatusCode.Created);
        await PushVersionAsync(token, docId, 1, Encoding.UTF8.GetBytes("v2"), HttpStatusCode.Created);

        using (JsonDocument versions = await SendAsync("GET", $"/api/v1/documents/{docId}/versions", token))
        {
            Assert.Equal(2, versions.RootElement.GetArrayLength());
            Assert.Equal(2, versions.RootElement[0].GetProperty("versionNumber").GetInt32());
            Assert.Equal(1, versions.RootElement[1].GetProperty("versionNumber").GetInt32());
        }
    }

    [Fact]
    public async Task Push_is_idempotent_on_same_key()
    {
        string token = await RegisterAsync("sync-idem@example.com", "Idem", "pw");
        var doc = await CreateDocumentAsync(token, "idem.pdf");
        Guid docId = doc.GetProperty("id").GetGuid();
        byte[] content = Encoding.UTF8.GetBytes("same-content");

        using var req1 = MakePushRequest(token, docId, 0, content, idempotencyKey: "key-1");
        var r1 = await _client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);

        using var req2 = MakePushRequest(token, docId, 0, content, idempotencyKey: "key-1");
        var r2 = await _client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);

        using (JsonDocument versions = await SendAsync("GET", $"/api/v1/documents/{docId}/versions", token))
            Assert.Equal(1, versions.RootElement.GetArrayLength()); // retry did NOT create a second version
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<string> RegisterAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync(
            "POST", "/api/v1/accounts/register",
            token: null,
            body: new { email, displayName, password });
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<JsonElement> CreateDocumentAsync(string token, string name)
    {
        using JsonDocument body = await SendAsync(
            "POST", "/api/v1/documents", token, new { name },
            expected: HttpStatusCode.Created);
        return body.RootElement.Clone();
    }

    private async Task<JsonElement> PushVersionAsync(
        string token, Guid docId, int baseVersion, byte[] content, HttpStatusCode expected)
    {
        using var request = MakePushRequest(token, docId, baseVersion, content, idempotencyKey: Guid.NewGuid().ToString());
        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private HttpRequestMessage MakePushRequest(
        string token, Guid docId, int baseVersion, byte[] content, string idempotencyKey)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/documents/{docId}/versions?baseVersionNumber={baseVersion}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new ByteArrayContent(content);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        return request;
    }

    private async Task<JsonDocument> SendAsync(
        string method, string path, string? token, object? body = null,
        HttpStatusCode? expected = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
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