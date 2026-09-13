// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Services;

/// <summary>
/// Batch OCR/conversion configuration (FR-BATCH-01). Holds the per-plan usage
/// quota used to meter batch jobs.
/// </summary>
public sealed class OcrOptions
{
    public const string SectionName = "Ocr";

    /// <summary>Maximum cumulative pages an account may submit on the Free plan (0 = unlimited).</summary>
    public long FreeMonthlyPageQuota { get; set; } = 50;

    /// <summary>Maximum cumulative pages an account may submit on the Pro plan (0 = unlimited).</summary>
    public long ProMonthlyPageQuota { get; set; } = 10_000;

    /// <summary>
    /// When true, <see cref="MuPdfOcrJobProcessor"/> (the real MuPDF+Tesseract
    /// engine) is registered as the job processor; otherwise the deterministic
    /// <see cref="NoopOcrJobProcessor"/> is used. Off by default so hosts without
    /// the native engine (e.g. the integration-test factory) keep a no-op path.
    /// </summary>
    public bool EnableNativeEngine { get; set; }

    /// <summary>
    /// When true (the default), <see cref="OcrJobWorker"/> sweeps items still marked
    /// Queued in the database at start-up and re-enqueues them. That is what lets a
    /// restarted host pick up work that was in flight when it went down, and it is
    /// correct for a deployed API, which owns its database.
    ///
    /// The integration-test harness turns it off, because there it is actively
    /// harmful: every test class builds its own host against one shared in-memory
    /// database, so a starting host would sweep up another host's queued job and
    /// complete it in its own scope - delivering the completion email to the wrong
    /// host's recording sender. The job would read Completed, the email would be
    /// nowhere, and the owning test would fail about one run in three.
    /// </summary>
    public bool SweepQueuedOnStart { get; set; } = true;
}