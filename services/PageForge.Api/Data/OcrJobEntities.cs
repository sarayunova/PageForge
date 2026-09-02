// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.ComponentModel.DataAnnotations;

namespace PageForge.Api.Data;

public enum OcrJobType
{
    Ocr = 0,
    Convert = 1
}

public enum OcrJobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>
/// What a <see cref="OcrJobItem"/> should be turned into.
/// </summary>
public enum OcrTargetFormat
{
    /// <summary>Searchable PDF (Tesseract hidden text layer) — the default OCR output.</summary>
    SearchablePdf = 0,
    Docx = 1,
    Xlsx = 2,
    Png = 3
}

/// <summary>
/// Progress of an individual <see cref="OcrJobItem"/> within its job.
/// </summary>
public enum OcrItemStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3
}

/// <summary>
/// A batch OCR / format-conversion request (FR-BATCH-01). A job covers one or
/// more document versions, each represented by an <see cref="OcrJobItem"/>.
/// The worker processes items off a queue and, on completion, notifies the owner
/// by email. Pages processed are counted against the account's usage quota.
/// </summary>
public sealed class OcrJob
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;

    public OcrJobType JobType { get; set; } = OcrJobType.Ocr;
    public OcrTargetFormat TargetFormat { get; set; } = OcrTargetFormat.SearchablePdf;

    /// <summary>Caller-supplied Idempotency-Key (nullable); unique per owner so retries don't double-submit.</summary>
    [MaxLength(128)]
    public string? IdempotencyKey { get; set; }

    public OcrJobStatus Status { get; set; } = OcrJobStatus.Queued;

    /// <summary>Aggregate pages processed across all items (usage metering).</summary>
    public int PagesProcessed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    public ICollection<OcrJobItem> Items { get; set; } = [];
}

/// <summary>
/// One document version to be OCR'd or converted as part of an <see cref="OcrJob"/>.
/// </summary>
public sealed class OcrJobItem
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OcrJobId { get; set; }
    public OcrJob OcrJob { get; set; } = null!;

    public Guid DocumentVersionId { get; set; }
    public DocumentVersion DocumentVersion { get; set; } = null!;

    /// <summary>
    /// New <see cref="DocumentVersion"/> holding the OCR'd/converted artifact this
    /// item produced (set on success). Used by the result download endpoint.
    /// </summary>
    public Guid? OutputVersionId { get; set; }
    public DocumentVersion? OutputVersion { get; set; }

    /// <summary>Generated file name of the produced artifact (e.g. "ocr-&lt;id&gt;.pdf").</summary>
    [MaxLength(255)]
    public string? OutputFileName { get; set; }

    /// <summary>Content type of the produced artifact (e.g. "application/pdf").</summary>
    [MaxLength(128)]
    public string? OutputContentType { get; set; }

    public OcrItemStatus Status { get; set; } = OcrItemStatus.Queued;

    /// <summary>Number of pages actually processed (used for usage metering).</summary>
    public int PagesProcessed { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Cumulative per-account usage meter for batch OCR/conversion (FR-BATCH-01
/// "usage-metered"). Incremented as job items complete; submission is denied when
/// the account exceeds its configured quota.
/// </summary>
public sealed class OcrUsage
{
    [Key]
    public Guid UserId { get; set; }

    /// <summary>Cumulative pages processed via batch OCR/conversion.</summary>
    public long PagesProcessed { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}