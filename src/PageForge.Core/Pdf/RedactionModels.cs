// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>How image objects overlapping a redaction region are treated (FR-SEC-02).</summary>
public enum RedactionImageMethod
{
    /// <summary>Do not remove images (can leak content covered by a redaction bar). Not recommended.</summary>
    None = 0,

    /// <summary>Remove the whole image object when it intrudes into a redaction region (secure default).</summary>
    Remove = 1,

    /// <summary>Paint over the redacted pixels inside the image (may still leak edges).</summary>
    BlackoutPixels = 2,

    /// <summary>Remove images only when they are not fully clipped by the redaction (less aggressive).</summary>
    RemoveUnlessInvisible = 3,
}

/// <summary>How vector line-art paths overlapping a redaction region are treated (FR-SEC-02).</summary>
public enum RedactionLineArtMethod
{
    /// <summary>Do not remove line art (can leak). Not recommended.</summary>
    None = 0,

    /// <summary>Remove paths entirely covered by a redaction region (secure default).</summary>
    RemoveIfCovered = 1,

    /// <summary>Remove paths merely touched by a redaction region (most aggressive).</summary>
    RemoveIfTouched = 2,
}

/// <summary>How text overlapping a redaction region is treated (FR-SEC-02).</summary>
public enum RedactionTextMethod
{
    /// <summary>Remove overlapping text from the content stream (secure default).</summary>
    Remove = 0,

    /// <summary>Keep all text (deliberately leaks; only for illustration). Not recommended.</summary>
    None = 1,

    /// <summary>Remove only those text runs already made invisible by clipping.</summary>
    RemoveInvisibleOnly = 2,
}

/// <summary>
/// Tuning knobs for a redaction apply (FR-SEC-02). The defaults are the secure
/// choices — text removed, whole images removed, line-art removed if fully
/// covered, and a black bar painted over each emptied region — so that content
/// in a redaction region is genuinely deleted rather than merely obscured.
/// Passing <c>null</c> options to <c>ApplyRedactionsAsync</c> behaves exactly
/// like the defaults.
/// </summary>
public sealed record RedactionOptions(
    bool BlackBox = true,
    RedactionImageMethod ImageMethod = RedactionImageMethod.Remove,
    RedactionLineArtMethod LineArtMethod = RedactionLineArtMethod.RemoveIfCovered,
    RedactionTextMethod TextMethod = RedactionTextMethod.Remove);
