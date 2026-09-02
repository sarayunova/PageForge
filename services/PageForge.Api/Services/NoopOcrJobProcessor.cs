// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Api.Data;

namespace PageForge.Api.Services;

/// <summary>
/// Default in-process OCR/conversion processor used when no native engine is
/// deployed. It completes each item deterministically (one page processed) so the
/// job lifecycle, usage metering and completion notification can be exercised
/// end-to-end. A production processor records the true page count and emits the
/// converted artifact; the shared contract is <see cref="IOcrJobProcessor"/>.
/// </summary>
public sealed class NoopOcrJobProcessor : IOcrJobProcessor
{
    public Task<OcrItemResult> ProcessAsync(
        OcrJob job, OcrJobItem item, DocumentVersion version, CancellationToken cancellationToken)
    {
        // The hosted slice has no shared engine wired to this instance; every item
        // is treated as a single successfully processed page so the surrounding
        // lifecycle is testable end-to-end. Swap in the engine-backed processor in prod.
        return Task.FromResult(new OcrItemResult(PagesProcessed: 1, ErrorMessage: null));
    }
}