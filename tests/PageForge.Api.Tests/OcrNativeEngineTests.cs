// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-BATCH-01 "live" integration test: with the real MuPDF+Tesseract engine
/// enabled, an OCR job produces a genuine searchable PDF that is persisted as a
/// new document version and downloadable. Runs hermetically using the vendored
/// <c>pageforge_mupdf.dll</c> + <c>tessdata/eng.traineddata</c> bundled into the
/// test output (guarded so the suite stays green on hosts without the native
/// toolchain).
/// </summary>
public sealed class OcrNativeEngineTests : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public OcrNativeEngineTests()
    {
        _factory = new PageForgeApiFactory().WithWebHostBuilder(builder =>
            builder.UseSetting("Ocr:EnableNativeEngine", "true"));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Ocr_job_persists_a_downloadable_searchable_pdf()
    {
        // Skip on hosts without the native OCR toolchain so the suite stays green.
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "pageforge_mupdf.dll"))
            || !File.Exists(Path.Combine(AppContext.BaseDirectory, "tessdata", "eng.traineddata")))
        {
            return;
        }

        string token = await RegisterAsync("ocr-native@example.com", "Native", "pw");
        Guid versionId = await PushFixtureVersionAsync(token);

        Guid jobId = await SubmitJobAsync(token, versionId);
        JsonElement done = await WaitForStatusAsync(token, jobId, "Completed", TimeSpan.FromSeconds(30));

        Assert.Equal("SearchablePdf", done.GetProperty("targetFormat").GetString());
        Assert.True(done.GetProperty("pagesProcessed").GetInt32() > 0);

        // A produced artifact must be exposed on the job item, persisted as a new
        // document version, and downloadable from the result endpoint.
        JsonElement item = done.GetProperty("items")[0];
        Assert.Equal("application/pdf", item.GetProperty("outputContentType").GetString());
        Assert.NotEqual(Guid.Empty, item.GetProperty("outputVersionId").GetGuid());

        Guid itemId = item.GetProperty("id").GetGuid();
        using var dl = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/ocr-jobs/{jobId}/items/{itemId}/result");
        dl.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage download = await _client.SendAsync(dl);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        byte[] body = await download.Content.ReadAsByteArrayAsync();
        Assert.True(body.Length > 0, "Downloaded OCR output must not be empty.");
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(body.AsSpan(0, Math.Min(4, body.Length))));
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<JsonElement> WaitForStatusAsync(
        string token, Guid jobId, string expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            using JsonDocument job = await SendAsync("GET", $"/api/v1/ocr-jobs/{jobId}", token);
            string status = job.RootElement.GetProperty("status").GetString()!;
            if (status == expected)
                return job.RootElement.Clone();
            await Task.Delay(100);
        }

        using JsonDocument final = await SendAsync("GET", $"/api/v1/ocr-jobs/{jobId}", token);
        throw new Xunit.Sdk.XunitException(
            $"Job {jobId} did not reach '{expected}'. Final: {final.RootElement.GetRawText()}");
    }

    private async Task<string> RegisterAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync("POST", "/api/v1/accounts/register",
            token: null, body: new { email, displayName, password });
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    private async Task<Guid> PushFixtureVersionAsync(string token)
    {
        byte[] pdf = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "fixtures", "scan-letters.pdf"));

        using JsonDocument created = await SendAsync("POST", "/api/v1/documents", token,
            new { name = "scan-letters.pdf" }, expected: HttpStatusCode.Created);
        Guid docId = created.RootElement.GetProperty("id").GetGuid();

        using var push = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/documents/{docId}/versions?baseVersionNumber=0");
        push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        push.Headers.Add("Idempotency-Key", $"push-native-{Guid.NewGuid():N}");
        push.Content = new ByteArrayContent(pdf);
        push.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        HttpResponseMessage pushResp = await _client.SendAsync(push);
        Assert.Equal(HttpStatusCode.Created, pushResp.StatusCode);
        using (var _ = pushResp) { }

        using JsonDocument versions = await SendAsync("GET", $"/api/v1/documents/{docId}/versions", token);
        return versions.RootElement[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> SubmitJobAsync(string token, Guid versionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/ocr-jobs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                documentVersionIds = new[] { versionId },
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
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await _client.SendAsync(request);
        if (expected is not null)
            Assert.Equal(expected.Value, response.StatusCode);
        string content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}