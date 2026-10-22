// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PageForge.Api.Data;
using PageForge.Api.Services.Email;

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
    private readonly OcrOptions _options;
    private readonly IEmailSender _email;
    private readonly ILogger<OcrJobWorker> _logger;

    // Serializes the item-completion write across every worker instance in this
    // process. Tests spin up multiple WebApplicationFactory hosts (each with its own
    // worker) against a shared in-memory store, so per-instance locks do not suffice;
    // a static lock makes completion updates atomic process-wide and race-free.
    private static readonly SemaphoreSlim _completionLock = new(1, 1);

    public OcrJobWorker(
        IServiceScopeFactory scopeFactory,
        IOcrJobProcessor processor,
        IOptions<OcrOptions> options,
        IEmailSender email,
        ILogger<OcrJobWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _processor = processor;
        _options = options.Value;
        _email = email;
        _logger = logger;
        _queue = Channel.CreateBounded<(Guid, Guid)>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Enqueue a job item for processing. No-op when the worker is stopping.</summary>
    public bool TryEnqueue(Guid jobId, Guid itemId) => _queue.Writer.TryWrite((jobId, itemId));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.SweepQueuedOnStart)
        {
            await SweepQueuedItemsAsync(stoppingToken);
        }

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
            try
            {
                await ProcessItemAsync(jobId, itemId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad item (a failed save, a vanished row) must not end the worker:
                // an escaping exception stops this BackgroundService and with it every
                // later job. Log it and carry on with the next item.
                _logger.LogError(ex, "OCR job {JobId} item {ItemId} could not be finalised.", jobId, itemId);
            }
        }
    }

    private async Task ProcessItemAsync(Guid jobId, Guid itemId, CancellationToken ct)
    {
        EmailMessage? notification;
        await _completionLock.WaitAsync(ct);
        try
        {
            notification = await ProcessItemCoreAsync(jobId, itemId, ct);
        }
        finally
        {
            _completionLock.Release();
        }

        // Sent after the completion is saved and the lock is released: a slow or failing
        // mail server must neither undo the job's outcome nor hold up other workers.
        if (notification is not null)
        {
            try
            {
                await _email.SendAsync(notification, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "OCR job {JobId} finished but its notification email was not sent.", jobId);
            }
        }
    }

    private async Task<EmailMessage?> ProcessItemCoreAsync(Guid jobId, Guid itemId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<OcrJobsService>();

        OcrJobItem? item = await db.OcrJobItems.FindAsync([itemId], ct);
        if (item is null)
            return null;

        OcrJob? job = await db.OcrJobs.FindAsync([jobId], ct);
        if (job is null)
            return null;

        DocumentVersion? version = await db.DocumentVersions.AsNoTracking()
            .SingleOrDefaultAsync(v => v.Id == item.DocumentVersionId, ct);
        if (version is null)
        {
            return (await service.CompleteItemAsync(
                jobId, itemId, 0, "The document version no longer exists.", null, ct)).Notification;
        }

        OcrItemResult result;
        try
        {
            result = await _processor.ProcessAsync(job, item, version, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            result = new OcrItemResult(0, ex.Message);
        }

        return (await service.CompleteItemAsync(
            jobId, itemId, result.PagesProcessed, result.ErrorMessage, result.Output, ct)).Notification;
    }
}