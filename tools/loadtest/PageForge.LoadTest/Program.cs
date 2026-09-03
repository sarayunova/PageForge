// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PageForge.LoadTest;

/// <summary>
/// Phase 6 FR-BATCH load harness. Boots the hosted API on an in-memory
/// EF/minio-free host and drives a realistic concurrent user flow — register,
/// create a document, push a version, submit a batch OCR job, poll it to
/// completion, download the result — reporting throughput and latency
/// percentiles against the Phase 6 load-test targets (p95 below a threshold,
/// 100% success rate).
///
/// Usage:
///   dotnet run --project tools/loadtest/PageForge.LoadTest
///       [--users 20] [--iterations 2] [--p95-ms 450] [--verbose]
/// Exit code is 0 when targets are met, 1 otherwise.
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // A tiny valid PDF (minimal "%PDF-1.4 ... %%EOF" header) used as version bytes.
    private static readonly byte[] SamplePdf = Encoding.ASCII.GetBytes(
        "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

    public static async Task<int> Main(string[] args)
    {
        int users = GetInt(args, "users", 20);
        int iterations = GetInt(args, "iterations", 2);
        int p95TargetMs = GetInt(args, "p95-ms", 450);
        bool verbose = args.Contains("--verbose", StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"PageForge load test: {users} virtual users x {iterations} iterations, " +
                          $"target p95 < {p95TargetMs} ms, 100% success.");

        var latencies = new BoxedConcurrentQueue();
        int totalRequests = 0;
        int failures = 0;

        // Host the API once; all VUs share the same in-memory store and worker.
        using var factory = new LoadHostFactory();
        using HttpClient client = factory.CreateClient();

        var sw = Stopwatch.StartNew();

        var tasks = new Task[users];
        for (int u = 0; u < users; u++)
        {
            int vu = u;
            tasks[u] = Task.Run(() => RunUserFlowAsync(
                client, vu, iterations, latencies, () => Interlocked.Increment(ref totalRequests),
                () => Interlocked.Increment(ref failures), verbose));
        }
        await Task.WhenAll(tasks);

        sw.Stop();

        return Report(sw.Elapsed, users, iterations, totalRequests, failures, latencies, p95TargetMs);
    }

    private static async Task RunUserFlowAsync(
        HttpClient client,
        int vu,
        int iterations,
        BoxedConcurrentQueue latencies,
        Action onRequest,
        Action onFailure,
        bool verbose)
    {
        string email = $"vu{vu:D3}-{Guid.NewGuid():N}@load.example";
        string token = await RegisterAsync(client, email, $"User {vu}", "pw-12345", latencies, onRequest, onFailure);

        for (int i = 0; i < iterations; i++)
        {
            Guid docId = await CreateDocumentAsync(client, token, $"vu{vu}-doc-{i}", latencies, onRequest, onFailure);
            Guid versionId = await PushVersionAsync(client, token, docId, latencies, onRequest, onFailure);
            Guid jobId = await SubmitOcrAsync(client, token, versionId, latencies, onRequest, onFailure);
            bool hasOutput = await WaitForCompletionAsync(client, token, jobId, latencies, onRequest, onFailure);
            if (hasOutput)
                await DownloadResultAsync(client, token, jobId, latencies, onRequest, onFailure);

            if (verbose)
                Console.WriteLine($"  VU {vu:D3} iter {i}: doc {docId:N}, job {jobId:N} done.");
        }
    }

    private static async Task<string> RegisterAsync(
        HttpClient client, string email, string displayName, string password,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure)
    {
        using var json = await SendAsync(client, HttpMethod.Post, "/api/v1/accounts/register",
            null, new { email, displayName, password }, latencies, onRequest, onFailure, expected: 200);
        return json.Root.GetProperty("accessToken").GetString()!;
    }

    private static async Task<Guid> CreateDocumentAsync(
        HttpClient client, string token, string name,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure)
    {
        using var json = await SendAsync(client, HttpMethod.Post, "/api/v1/documents",
            token, new { name }, latencies, onRequest, onFailure, expected: 201);
        return json.Root.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> PushVersionAsync(
        HttpClient client, string token, Guid docId,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/documents/{docId}/versions?baseVersionNumber=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-PageForge-Name", "scan-letters.pdf");
        request.Content = new ByteArrayContent(SamplePdf);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        string body = await SendRawAsync(client, request, latencies, onRequest, onFailure, expected: 201);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SubmitOcrAsync(
        HttpClient client, string token, Guid versionId,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure)
    {
        using var json = await SendAsync(client, HttpMethod.Post, "/api/v1/ocr-jobs",
            token, new { documentVersionIds = new[] { versionId }, jobType = "ocr", targetFormat = "searchablePdf" },
            latencies, onRequest, onFailure, expected: 201);
        return json.Root.GetProperty("id").GetGuid();
    }

    private static async Task<bool> WaitForCompletionAsync(
        HttpClient client, string token, Guid jobId,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var json = await SendAsync(client, HttpMethod.Get, $"/api/v1/ocr-jobs/{jobId}",
                token, null, latencies, onRequest, onFailure, expected: 200);
            string status = json.Root.GetProperty("status").GetString()!;
            if (status == "Completed")
            {
                // Whether a produced artifact exists. The no-op processor produces no
                // output (download would 404), while the native engine does.
                return json.Root.GetProperty("items")[0].TryGetProperty("outputVersionId", out JsonElement ov)
                       && ov.ValueKind == JsonValueKind.String
                       && !string.IsNullOrEmpty(ov.GetString());
            }
            if (status == "Failed")
                throw new InvalidOperationException($"Job {jobId} failed: {json.Root.GetProperty("errorMessage").GetString()}");
            await Task.Delay(50);
        }
        throw new TimeoutException($"Job {jobId} did not complete within 20s.");
    }

    private static async Task DownloadResultAsync(
        HttpClient client, string token, Guid jobId,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure)
    {
        // Discover the item id from the completed job so the download URL is exact.
        string itemId;
        using (var json = await SendAsync(client, HttpMethod.Get, $"/api/v1/ocr-jobs/{jobId}",
            token, null, latencies, onRequest, onFailure, expected: 200))
        {
            itemId = json.Root.GetProperty("items")[0].GetProperty("id").GetString()!;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/ocr-jobs/{jobId}/items/{itemId}/result");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await SendRawAsync(client, request, latencies, onRequest, onFailure, expected: 200);
    }

    // --- Core HTTP plumbing -------------------------------------------------

    private static async Task<JsonResult> SendAsync(
        HttpClient client, HttpMethod method, string path, string? token, object? body,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure, int expected)
    {
        string raw = await SendCoreAsync(client, BuildRequest(method, path, token, body),
            latencies, onRequest, onFailure, expected);
        return new JsonResult(raw);
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string path, string? token, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<string> SendRawAsync(
        HttpClient client, HttpRequestMessage request,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure, int expected)
        => await SendCoreAsync(client, request, latencies, onRequest, onFailure, expected);

    private static async Task<string> SendCoreAsync(
        HttpClient client, HttpRequestMessage request,
        BoxedConcurrentQueue latencies, Action onRequest, Action onFailure, int expected)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request);
            onRequest();
            sw.Stop();
            latencies.Add(sw.Elapsed);
            string body = await response.Content.ReadAsStringAsync();
            if ((int)response.StatusCode != expected)
            {
                onFailure();
                throw new InvalidOperationException(
                    $"Unexpected status {response.StatusCode} (wanted {expected}) for {request.Method} {request.RequestUri}. Body: {body}");
            }
            return body;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            onFailure();
            throw;
        }
    }

    // --- Reporting ----------------------------------------------------------

    private static int Report(
        TimeSpan elapsed, int users, int iterations, int totalRequests, int failures,
        BoxedConcurrentQueue latencies, int p95TargetMs)
    {
        long[] samples = latencies.ToArray();
        Array.Sort(samples);
        TimeSpan p50 = Percentile(samples, 50);
        TimeSpan p90 = Percentile(samples, 90);
        TimeSpan p95 = Percentile(samples, 95);
        TimeSpan p99 = Percentile(samples, 99);
        double reqsPerSec = elapsed.TotalSeconds > 0 ? totalRequests / elapsed.TotalSeconds : 0;

        Console.WriteLine();
        Console.WriteLine("===== PageForge load-test report =====");
        Console.WriteLine($"Virtual users          : {users} x iterations {iterations}");
        Console.WriteLine($"Elapsed                : {elapsed.TotalSeconds:F1} s");
        Console.WriteLine($"Total requests         : {totalRequests}  ({reqsPerSec:F1} req/s)");
        Console.WriteLine($"Failures               : {failures}  ({(totalRequests == 0 ? 0 : failures / (double)totalRequests * 100):F2}%)");
        Console.WriteLine($"Latency  p50           : {p50.TotalMilliseconds:F1} ms");
        Console.WriteLine($"Latency  p90           : {p90.TotalMilliseconds:F1} ms");
        Console.WriteLine($"Latency  p95           : {p95.TotalMilliseconds:F1} ms");
        Console.WriteLine($"Latency  p99           : {p99.TotalMilliseconds:F1} ms");
        Console.WriteLine($"Latency  max           : {Max(samples).TotalMilliseconds:F1} ms");
        Console.WriteLine();

        bool ok = failures == 0 && p95.TotalMilliseconds <= p95TargetMs;
        Console.WriteLine($"Targets: p95 <= {p95TargetMs} ms, 0 failures.");
        Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
        return ok ? 0 : 1;
    }

    private static TimeSpan Percentile(long[] sorted, double pct)
    {
        if (sorted.Length == 0) return TimeSpan.Zero;
        int idx = (int)Math.Ceiling(pct / 100.0 * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return TimeSpan.FromTicks(sorted[idx]);
    }

    private static TimeSpan Max(long[] sorted) => sorted.Length == 0 ? TimeSpan.Zero : TimeSpan.FromTicks(sorted[^1]);

    private static int GetInt(string[] args, string name, int defaultVal)
    {
        int i = Array.IndexOf(args, $"--{name}");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int v))
            return v;
        return defaultVal;
    }

    private sealed class JsonResult : IDisposable
    {
        private readonly JsonDocument _doc;
        public JsonResult(string raw) => _doc = JsonDocument.Parse(raw);
        public JsonElement Root => _doc.RootElement;
        public void Dispose() => _doc.Dispose();
    }

    private sealed class BoxedConcurrentQueue
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<long> _q = new();
        public void Add(TimeSpan t) => _q.Enqueue(t.Ticks);
        public long[] ToArray() => _q.ToArray();
    }
}