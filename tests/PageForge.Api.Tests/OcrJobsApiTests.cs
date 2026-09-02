// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-BATCH-01 integration tests over the batch OCR/conversion job lifecycle:
/// submit a multi-version job, polling until the queue worker completes it, and
/// asserting pages processed, per-account usage metering, the completion
/// notification email, idempotent resubmission, and the authorization gate.
/// </summary>
public sealed class OcrJobsApiTests : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly PageForgeApiFactory _factory;
    private readonly HttpClient _client;

    public OcrJobsApiTests()
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
    public async Task Submit_job_completes_and_notifies_owner()
    {
        string token = await RegisterAsync("ocr-submit@example.com", "Submit", "pw");
        Guid versionId = await CreateVersionAsync(token);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ocr-jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", "ocr-submit-1");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                documentVersionIds = new[] { versionId },
                jobType = "ocr",
                targetFormat = "searchablePdf"
            }, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage submitResponse = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, submitResponse.StatusCode);
        var submitted = JsonDocument.Parse(await submitResponse.Content.ReadAsStringAsync()).RootElement;
        Guid jobId = submitted.GetProperty("id").GetGuid();
        Assert.Equal("Queued", submitted.GetProperty("status").GetString());

        // The queue worker advances the job asynchronously; poll until complete.
        JsonElement completed = await WaitForStatusAsync(token, jobId, "Completed");

        Assert.Equal(1, completed.GetProperty("pagesProcessed").GetInt32());
        Assert.Equal(JsonValueKind.String, completed.GetProperty("completedAt").ValueKind);
        Assert.Equal(1, completed.GetProperty("items").GetArrayLength());

        // Completion notification email reached the owner.
        Assert.Contains(_factory.Email.Messages, m =>
            m.Subject.Contains("complete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Submit_is_idempotent_on_same_key()
    {
        string token = await RegisterAsync("ocr-idem@example.com", "Idem", "pw");
        Guid versionId = await CreateVersionAsync(token);

        Guid first = await SubmitJobAsync(token, versionId, key: "ocr-idem-1");
        Guid second = await SubmitJobAsync(token, versionId, key: "ocr-idem-1");

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Multi_version_job_accrues_usage_against_quota()
    {
        string token = await RegisterAsync("ocr-usage@example.com", "Usage", "pw");
        Guid v1 = await CreateVersionAsync(token);
        Guid v2 = await CreateVersionAsync(token);

        Guid jobId = await SubmitJobAsync(token, [v1, v2], key: "ocr-usage-1");
        JsonElement done = await WaitForStatusAsync(token, jobId, "Completed");

        // Two items, one page each (no-op processor) → two pages processed.
        Assert.Equal(2, done.GetProperty("pagesProcessed").GetInt32());
        Assert.Equal(2, done.GetProperty("items").GetArrayLength());
        Assert.Equal(2, done.GetProperty("usagePages").GetInt64());

        // Free quota is 50 (from appsettings via the host); recorded usage fits under it.
        Assert.True(done.GetProperty("usagePages").GetInt64() <= done.GetProperty("usageQuota").GetInt64());
    }

    [Fact]
    public async Task Jobs_are_scoped_to_the_owner()
    {
        string ownerToken = await RegisterAsync("ocr-owner@example.com", "Owner", "pw");
        Guid versionId = await CreateVersionAsync(ownerToken);
        Guid jobId = await SubmitJobAsync(ownerToken, versionId, key: "ocr-scope-1");

        string outsiderToken = await RegisterAsync("ocr-outsider@example.com", "Outsider", "pw");
        var forbidden = await SendRawAsync(HttpMethod.Get, $"/api/v1/ocr-jobs/{jobId}", outsiderToken);

        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task Jobs_require_authentication()
    {
        var response = await _client.GetAsync("/api/v1/ocr-jobs");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<JsonElement> WaitForStatusAsync(string token, Guid jobId, string expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using JsonDocument job = await SendAsync("GET", $"/api/v1/ocr-jobs/{jobId}", token);
            string status = job.RootElement.GetProperty("status").GetString()!;
            if (status == expected)
                return job.RootElement.Clone();
            await Task.Delay(50);
        }

        using JsonDocument final = await SendAsync("GET", $"/api/v1/ocr-jobs/{jobId}", token);
        throw new Xunit.Sdk.XunitException(
            $"Job {jobId} did not reach '{expected}' within the timeout. Final: {final.RootElement.GetRawText()}");
    }

    private async Task<string> RegisterAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync("POST", "/api/v1/accounts/register",
            token: null, body: new { email, displayName, password });
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<Guid> CreateVersionAsync(string token)
    {
        using JsonDocument created = await SendAsync("POST", "/api/v1/documents", token,
            new { name = "ocr.pdf" }, expected: HttpStatusCode.Created);
        Guid docId = created.RootElement.GetProperty("id").GetGuid();

        using var push = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/documents/{docId}/versions?baseVersionNumber=0");
        push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        push.Headers.Add("Idempotency-Key", $"push-{Guid.NewGuid():N}");
        push.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ocr-content"));
        push.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        HttpResponseMessage pushResp = await _client.SendAsync(push);
        Assert.Equal(HttpStatusCode.Created, pushResp.StatusCode);

        using JsonDocument versions = await SendAsync("GET", $"/api/v1/documents/{docId}/versions", token);
        return versions.RootElement[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> SubmitJobAsync(string token, Guid versionId, string? key)
        => await SubmitJobAsync(token, [versionId], key);

    private async Task<Guid> SubmitJobAsync(string token, Guid[] versionIds, string? key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ocr-jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                documentVersionIds = versionIds,
                jobType = "ocr",
                targetFormat = "searchablePdf"
            }, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
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