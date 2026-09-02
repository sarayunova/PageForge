// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using PageForge.Api.Data;

namespace PageForge.Api.Services;

/// <summary>
/// In-memory job queue + worker pool for batch OCR/conversion (FR-BATCH-01).
/// Submissions are enqueued on a bounded <see cref="Channel{T}"/> and consumed by a
/// fixed set of workers that run each item through <see cref="IOcrJobProcessor"/> and
/// report completion/usage back through <see cref="OcrJobsService"/>. Being an
/// in-process queue, jobs pending in the DB are picked up on the next host start.
/// </summary>
public sealed class OcrJobWorker : BackgroundService
{
    private readonly Channel<(Guid JobId, Guid ItemId)> _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOcrJobProcessor _processor;

    // Serializes the item-completion write across every worker instance in this
    // process. Tests spin up multiple WebApplicationFactory hosts (each with its own
    // worker) against a shared in-memory store, so per-instance locks do not suffice;
    // a static lock makes completion updates atomic process-wide and race-free.
    private static readonly SemaphoreSlim _completionLock = new(1, 1);

    public OcrJobWorker(IServiceScopeFactory scopeFactory, IOcrJobProcessor processor)
    {
        _scopeFactory = scopeFactory;
        _processor = processor;
        _queue = Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Enqueue a job item for processing. No-op when the worker is stopping.</summary>
    public bool TryEnqueue(Guid jobId, Guid itemId) => _queue.Writer.TryWrite((jobId, itemId));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SweepQueuedItemsAsync(stoppingToken);
        await RunWorkerAsync(stoppingToken);
    }

    private async Task SweepQueuedItemsAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<OcrJobItem> queued = await db.OcrJobItems
            .AsNoTracking()
            .Where(i => i.Status == OcrItemStatus.Queued)
            .Take(500)
            .ToListAsync(ct);
        foreach (OcrJobItem item in queued)
        {
            TryEnqueue(item.OcrJobId, item.Id);
        }
    }

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        await foreach (var (jobId, itemId) in _queue.Reader.ReadAllAsync(ct))
        {
            await ProcessItemAsync(jobId, itemId, ct);
        }
    }

    private async Task ProcessItemAsync(Guid jobId, Guid itemId, CancellationToken ct)
    {
        await _completionLock.WaitAsync(ct);
        try
        {
            await ProcessItemCoreAsync(jobId, itemId, ct);
        }
        finally
        {
            _completionLock.Release();
        }
    }

    private async Task ProcessItemCoreAsync(Guid jobId, Guid itemId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<OcrJobsService>();

        OcrJobItem? item = await db.OcrJobItems.FindAsync([itemId], ct);
        if (item is null)
            return;

        OcrJob? job = await db.OcrJobs.FindAsync([jobId], ct);
        if (job is null)
            return;

        DocumentVersion? version = await db.DocumentVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == item.DocumentVersionId, ct);
        if (version is null)
        {
            await service.CompleteItemAsync(jobId, itemId, 0, "The document version no longer exists.", null, ct);
            return;
        }

        OcrItemResult result;
        try
        {
            result = await _processor.ProcessAsync(job, item, version, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            result = new OcrItemResult(0, ex.Message);
        }

        await service.CompleteItemAsync(
            jobId, itemId, result.PagesProcessed, result.ErrorMessage, result.Output, ct);
    }
}