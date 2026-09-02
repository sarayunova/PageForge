// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PageForge.Api.Tests;

/// <summary>
/// FR-ESIGN-01 integration tests over the send-for-signature workflow: the
/// Draft → Sent → Viewed → Completed/Declined state machine, idempotent create,
/// per-signer decisions, audit trail, completion certificate, and captured
/// outbound email (reminders + certificate) via <see cref="RecordingEmailSender"/>.
/// </summary>
public sealed class EsignApiTests : IDisposable
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public EsignApiTests()
    {
        // A shared factory keeps one in-memory DB/email sink across tests in this run.
        Factory = new PageForgeApiFactory();
        Client = Factory.CreateClient();
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
    }

    private PageForgeApiFactory Factory { get; }
    private HttpClient Client { get; }

    [Fact]
    public async Task Create_request_is_draft_with_signers()
    {
        string token = await RegisterAsync("esign-create@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);

        var req = await CreateRequestAsync(token, versionId, ["a@example.com", "b@example.com"]);

        Assert.Equal("Draft", req.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, req.RootElement.GetProperty("signers").GetArrayLength());
    }

    [Fact]
    public async Task Create_is_idempotent_on_same_key()
    {
        string token = await RegisterAsync("esign-idem@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);

        Guid first = await CreateRequestIdAsync(token, versionId, key: "key-1", ["a@example.com"]);
        Guid second = await CreateRequestIdAsync(token, versionId, key: "key-1", ["a@example.com"]);

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Send_emails_signers_and_marks_sent()
    {
        string token = await RegisterAsync("esign-send@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);
        Guid reqId = await CreateRequestIdAsync(token, versionId, key: "send-1", ["a@example.com", "b@example.com"]);

        var sent = await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/send", token,
            new { reminderDays = 3 });

        Assert.Equal("Sent", sent.RootElement.GetProperty("status").GetString());

        Assert.Equal(2, Factory.Email.Messages.Count);
        Assert.All(Factory.Email.Messages, m =>
        {
            Assert.Contains("sign", m.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.True(m.To is "a@example.com" or "b@example.com");
        });
    }

    [Fact]
    public async Task All_signers_signed_completes_request_and_emails_certificate()
    {
        string token = await RegisterAsync("esign-sign@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);
        Guid reqId = await CreateRequestIdAsync(token, versionId, key: "sign-1", ["a@example.com", "b@example.com"]);

        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/send", token, new { reminderDays = 3 });

        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/signers/a@example.com/sign", token, expected: HttpStatusCode.OK);
        var completed = await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/signers/b@example.com/sign", token, expected: HttpStatusCode.OK);

        Assert.Equal("Completed", completed.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, completed.RootElement.GetProperty("completedAt").ValueKind);

        // Completion certificate email was sent to the owner.
        Assert.Contains(Factory.Email.Messages, m =>
            m.Subject.Contains("Completed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Decline_closes_request_as_declined()
    {
        string token = await RegisterAsync("esign-decline@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);
        Guid reqId = await CreateRequestIdAsync(token, versionId, key: "decl-1", ["a@example.com"]);

        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/send", token, new { reminderDays = 3 });
        var declined = await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/signers/a@example.com/decline", token,
            new { declineReason = "Not my agreement" });

        Assert.Equal("Declined", declined.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, declined.RootElement.GetProperty("completedAt").ValueKind);
    }

    [Fact]
    public async Task Audit_trail_records_the_lifecycle()
    {
        string token = await RegisterAsync("esign-audit@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);
        Guid reqId = await CreateRequestIdAsync(token, versionId, key: "audit-1", ["a@example.com"]);

        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/send", token, new { reminderDays = 3 });
        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/signers/a@example.com/sign", token, expected: HttpStatusCode.OK);

        using JsonDocument audit = await SendAsync("GET", $"/api/v1/signature-requests/{reqId}/audit", token);

        string[] actions = audit.RootElement.EnumerateArray()
            .Select(x => x.GetProperty("action").GetString()!).ToArray();

        Assert.Contains("request.created", actions);
        Assert.Contains("request.sent", actions);
        Assert.Contains("signer.signed", actions);
        Assert.Contains("request.completed", actions);
    }

    [Fact]
    public async Task Certificate_returns_signers_and_audit_trail()
    {
        string token = await RegisterAsync("esign-cert@example.com", "Esign", "pw");
        Guid versionId = await CreateVersionAsync(token);
        Guid reqId = await CreateRequestIdAsync(token, versionId, key: "cert-1", ["a@example.com"]);

        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/send", token, new { reminderDays = 3 });
        await SendAsync("POST", $"/api/v1/signature-requests/{reqId}/signers/a@example.com/sign", token, expected: HttpStatusCode.OK);

        using JsonDocument cert = await SendAsync("GET", $"/api/v1/signature-requests/{reqId}/certificate", token);

        Assert.Equal(1, cert.RootElement.GetProperty("signers").GetArrayLength());
        Assert.True(cert.RootElement.GetProperty("auditTrail").GetArrayLength() >= 3);
    }

    [Fact]
    public async Task Requests_require_authentication()
    {
        var response = await Client.GetAsync("/api/v1/signature-requests");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ------------------------------------------------------------

    private async Task<string> RegisterAsync(string email, string displayName, string password)
    {
        using JsonDocument body = await SendAsync("POST", "/api/v1/accounts/register",
            token: null, body: new { email, displayName, password });
        return body.RootElement.GetProperty("accessToken").GetString()!;
    }

    /// <summary>Creates a doc, pushes version v1, and returns the version's GUID.</summary>
    private async Task<Guid> CreateVersionAsync(string token)
    {
        using JsonDocument created = await SendAsync("POST", "/api/v1/documents", token,
            new { name = "esign.pdf" }, expected: HttpStatusCode.Created);
        Guid docId = created.RootElement.GetProperty("id").GetGuid();

        using var push = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/documents/{docId}/versions?baseVersionNumber=0");
        push.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        push.Headers.Add("Idempotency-Key", $"push-{Guid.NewGuid():N}");
        push.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("esign-content"));
        push.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        HttpResponseMessage pushResp = await Client.SendAsync(push);
        Assert.Equal(HttpStatusCode.Created, pushResp.StatusCode);

        using JsonDocument versions = await SendAsync("GET", $"/api/v1/documents/{docId}/versions", token);
        return versions.RootElement[0].GetProperty("id").GetGuid();
    }

    private async Task<Guid> CreateRequestIdAsync(string token, Guid versionId, string key, string[] emails)
    {
        using JsonDocument created = await CreateRequestAsync(token, versionId, emails, idempotencyKey: key);
        return created.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<JsonDocument> CreateRequestAsync(string token, Guid versionId, string[] emails, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/signature-requests");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        var body = new
        {
            documentVersionId = versionId,
            title = "Demo agreement",
            signers = emails.Select(e => new { email = e, displayName = "" }).ToList()
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<JsonDocument> SendAsync(string method, string path, string? token, object? body = null, HttpStatusCode? expected = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");

        HttpResponseMessage response = await Client.SendAsync(request);
        if (expected is not null)
            Assert.Equal(expected.Value, response.StatusCode);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}