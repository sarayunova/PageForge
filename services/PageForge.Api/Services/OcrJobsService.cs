// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PageForge.Api.Data;
using PageForge.Api.Services.Email;

namespace PageForge.Api.Services;

/// <summary>
/// Batch OCR / conversion job lifecycle (FR-BATCH-01): submit a multi-document job
/// (idempotent, usage-metered), read it back, and complete items as the queue worker
/// processes them. On full completion it notifies the owner by email.
/// </summary>
public sealed class OcrJobsService
{
    private readonly AppDbContext _db;
    private readonly IEmailSender _email;
    private readonly OcrOptions _options;
    private readonly OcrJobWorker _worker;

    public OcrJobsService(AppDbContext db, IEmailSender email, IOptions<OcrOptions> options, OcrJobWorker worker)
    {
        _db = db;
        _email = email;
        _options = options.Value;
        _worker = worker;
    }

    public async Task<OcrJob> SubmitAsync(
        Guid ownerId, List<Guid> versionIds, OcrJobType type, OcrTargetFormat format,
        string? idempotencyKey, CancellationToken ct)
    {
        if (versionIds.Count == 0)
            throw new InvalidOperationException("A job needs at least one document version.");

        if (idempotencyKey is not null)
        {
            OcrJob? existing = await _db.OcrJobs
                .Where(j => j.OwnerId == ownerId && j.IdempotencyKey == idempotencyKey)
                .SingleOrDefaultAsync(ct);
            if (existing is not null)
                return existing;
        }

        List<DocumentVersion> versions = await _db.DocumentVersions
            .AsNoTracking()
            .Where(v => v.Document.OwnerId == ownerId && versionIds.Contains(v.Id))
            .ToListAsync(ct);

        if (versions.Count != versionIds.Count)
            throw new InvalidOperationException("One or more document versions were not found or are not owned by you.");

        await EnforceQuotaAsync(ownerId, type, ct);

        var job = new OcrJob
        {
            OwnerId = ownerId,
            JobType = type,
            TargetFormat = format,
            IdempotencyKey = idempotencyKey
        };
        foreach (Guid vid in versionIds)
        {
            job.Items.Add(new OcrJobItem { DocumentVersionId = vid });
        }

        _db.OcrJobs.Add(job);
        await _db.SaveChangesAsync(ct);

        foreach (OcrJobItem item in job.Items)
        {
            _worker.TryEnqueue(job.Id, item.Id);
        }

        return job;
    }

    public async Task<(OcrJob? Job, long Usage, long Quota)> GetForOwnerAsync(Guid ownerId, Guid jobId, CancellationToken ct)
    {
        OcrJob? job = await _db.OcrJobs
            .AsNoTracking()
            .Include(j => j.Items)
                .ThenInclude(i => i.DocumentVersion)
            .Where(j => j.Id == jobId && j.OwnerId == ownerId)
            .SingleOrDefaultAsync(ct);
        if (job is null)
            return (null, 0, 0);

        OcrUsage? usage = await _db.OcrUsages.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == ownerId, ct);
        return (job, usage?.PagesProcessed ?? 0, QuotaFor(ownerId, await GetPlanAsync(ownerId, ct)));
    }

    public async Task<IReadOnlyList<OcrJob>> ListForOwnerAsync(Guid ownerId, int limit, CancellationToken ct)
    {
        return await _db.OcrJobs
            .AsNoTracking()
            .Include(j => j.Items)
            .Where(j => j.OwnerId == ownerId)
            .OrderByDescending(j => j.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Marks a job item (and the job) complete/failed and accrues usage. Called by
    /// the queue worker after <see cref="IOcrJobProcessor.ProcessAsync"/>. Returns
    /// the job and whether the whole job just finished.
    /// </summary>
    public async Task<(OcrJob Job, bool Finished)> CompleteItemAsync(
        Guid jobId, Guid itemId, int pagesProcessed, string? error, CancellationToken ct)
    {
        OcrJobItem item = await _db.OcrJobItems.FindAsync([itemId], ct)
            ?? throw new KeyNotFoundException("Job item not found.");

        // Idempotency: a worker from another host may already have finalized this
        // item against the shared store; do not double-accrue usage or re-notify.
        if (item.Status is OcrItemStatus.Completed or OcrItemStatus.Failed)
        {
            OcrJob existingJob = await _db.OcrJobs.FindAsync([jobId], ct)
                ?? throw new KeyNotFoundException("Job not found.");
            return (existingJob, existingJob.Status is OcrJobStatus.Completed or OcrJobStatus.Failed);
        }

        item.Status = error is null ? OcrItemStatus.Completed : OcrItemStatus.Failed;
        item.PagesProcessed = pagesProcessed;
        item.CompletedAt = DateTime.UtcNow;
        item.ErrorMessage = error;

        OcrJob job = await _db.OcrJobs.FindAsync([jobId], ct)
            ?? throw new KeyNotFoundException("Job not found.");

        var allItems = await _db.OcrJobItems.Where(i => i.OcrJobId == jobId).ToListAsync(ct);
        job.PagesProcessed = allItems.Sum(i => i.PagesProcessed);
        bool anyFailed = allItems.Any(i => i.Status == OcrItemStatus.Failed);
        bool allDone = allItems.Count > 0 && allItems.All(i => i.Status != OcrItemStatus.Queued && i.Status != OcrItemStatus.Running);

        bool finished = false;
        if (allDone)
        {
            finished = true;
            if (anyFailed && job.PagesProcessed == 0)
            {
                job.Status = OcrJobStatus.Failed;
                job.FailedAt = DateTime.UtcNow;
                job.ErrorMessage = allItems.First(i => i.Status == OcrItemStatus.Failed).ErrorMessage;
                await _email.SendAsync(await BuildFailureEmailAsync(job, allItems, ct), ct);
            }
            else
            {
                job.Status = OcrJobStatus.Completed;
                job.CompletedAt = DateTime.UtcNow;
                await _email.SendAsync(await BuildCompletionEmailAsync(job, ct), ct);
            }
        }

        if (pagesProcessed > 0)
        {
            await AccrueUsageAsync(job.OwnerId, pagesProcessed, ct);
        }

        await _db.SaveChangesAsync(ct);
        return (job, finished);
    }

    private async Task EnforceQuotaAsync(Guid ownerId, OcrJobType type, CancellationToken ct)
    {
        OcrUsage? usage = await _db.OcrUsages.SingleOrDefaultAsync(u => u.UserId == ownerId, ct);
        long used = usage?.PagesProcessed ?? 0;
        long quota = QuotaFor(ownerId, await GetPlanAsync(ownerId, ct));
        if (quota > 0 && used >= quota)
            throw new InvalidOperationException($"Batch OCR page quota exceeded ({used}/{quota}).");
    }

    private async Task AccrueUsageAsync(Guid ownerId, long pages, CancellationToken ct)
    {
        OcrUsage? usage = await _db.OcrUsages.SingleOrDefaultAsync(u => u.UserId == ownerId, ct);
        if (usage is null)
        {
            _db.OcrUsages.Add(new OcrUsage { UserId = ownerId, PagesProcessed = pages });
        }
        else
        {
            usage.PagesProcessed += pages;
            usage.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task<string> GetPlanAsync(Guid ownerId, CancellationToken ct)
    {
        string? plan = await _db.Subscriptions
            .AsNoTracking()
            .Where(s => s.UserId == ownerId && s.Status == "active")
            .Select(s => s.PlanId)
            .SingleOrDefaultAsync(ct);
        return string.IsNullOrEmpty(plan) ? BillingPlans.Free : plan;
    }

    private long QuotaFor(Guid ownerId, string plan)
    {
        bool isPro = string.Equals(plan, BillingPlans.Pro, StringComparison.OrdinalIgnoreCase);
        return isPro ? _options.ProMonthlyPageQuota : _options.FreeMonthlyPageQuota;
    }

    private async Task<EmailMessage> BuildCompletionEmailAsync(OcrJob job, CancellationToken ct)
    {
        string? email = await _db.Users.Where(u => u.Id == job.OwnerId).Select(u => u.Email).SingleOrDefaultAsync(ct);
        return new EmailMessage
        {
            To = email ?? string.Empty,
            Subject = "Your batch OCR job is complete",
            PlainTextBody = $"Your {job.JobType} job {job.Id} processed {job.PagesProcessed} pages to {job.TargetFormat}."
        };
    }

    private async Task<EmailMessage> BuildFailureEmailAsync(OcrJob job, List<OcrJobItem> items, CancellationToken ct)
    {
        string? email = await _db.Users.Where(u => u.Id == job.OwnerId).Select(u => u.Email).SingleOrDefaultAsync(ct);
        string error = items.FirstOrDefault(i => i.Status == OcrItemStatus.Failed)?.ErrorMessage ?? "Unknown error";
        return new EmailMessage
        {
            To = email ?? string.Empty,
            Subject = "Your batch OCR job failed",
            PlainTextBody = $"Your {job.JobType} job {job.Id} failed: {error}"
        };
    }
}