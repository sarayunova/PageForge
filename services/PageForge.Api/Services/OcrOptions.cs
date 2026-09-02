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
}