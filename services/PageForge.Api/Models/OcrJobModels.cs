// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Api.Models;

public sealed class SubmitOcrJobRequest
{
    /// <summary>Document version ids to OCR or convert. All must belong to the caller.</summary>
    public required System.Collections.Generic.List<System.Guid> DocumentVersionIds { get; init; } = [];
    public string JobType { get; init; } = "ocr";
    public string TargetFormat { get; init; } = "searchablePdf";
}

public sealed class OcrJobResponse
{
    public required System.Guid Id { get; init; }
    public required string JobType { get; init; }
    public required string TargetFormat { get; init; }
    public required string Status { get; init; }
    public required int PagesProcessed { get; init; }
    public required System.DateTime CreatedAt { get; init; }
    public System.DateTime? CompletedAt { get; init; }
    public System.DateTime? FailedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public required System.Collections.Generic.IReadOnlyList<OcrJobItemResponse> Items { get; init; }
    public long UsagePages { get; set; }
    public long UsageQuota { get; set; }
}

public sealed class OcrJobItemResponse
{
    public required System.Guid Id { get; init; }
    public required System.Guid DocumentVersionId { get; init; }
    public System.Guid? OutputVersionId { get; init; }
    public string? OutputFileName { get; init; }
    public string? OutputContentType { get; init; }
    public required string Status { get; init; }
    public required int PagesProcessed { get; init; }
    public required System.DateTime CreatedAt { get; init; }
    public System.DateTime? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed class OcrJobListResponse
{
    public required System.Collections.Generic.IReadOnlyList<OcrJobResponse> Items { get; init; }
    public string? NextCursor { get; init; }
}