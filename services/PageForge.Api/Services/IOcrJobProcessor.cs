// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Api.Data;

namespace PageForge.Api.Services;

/// <summary>Outcome of running one OCR/conversion item.</summary>
public sealed record OcrItemResult(
    int PagesProcessed,
    string? ErrorMessage);

/// <summary>
/// Seam for the server-side OCR/conversion engine (FR-BATCH-01). The default
/// implementation performs a best-effort synchronous pass so the job lifecycle,
/// usage metering and completion notifications can be exercised end-to-end; a
/// production deployment supplies a real processor backed by the MuPDF/Tesseract
/// engine shared with the desktop client.
/// </summary>
public interface IOcrJobProcessor
{
    /// <summary>
    /// Process a single job item whose source document version has been fetched.
    /// Returns the count of pages processed (or an error) and whether it should be
    /// treated as completed or failed.
    /// </summary>
    Task<OcrItemResult> ProcessAsync(
        OcrJob job,
        OcrJobItem item,
        DocumentVersion version,
        CancellationToken cancellationToken);
}