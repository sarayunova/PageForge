// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns high-level FR-SEC-02 redaction operations (mark a
/// region, apply every marked region) into calls on the <see cref="IPdfEngine"/>
/// seam, doing the validation that does not depend on the native engine. The
/// destructive apply is routed through an <see cref="Editing.IEditCommand"/> by
/// the shell so it can be undone; this service supplies the mark and the
/// non-command apply entry point.
/// </summary>
public static class RedactionService
{
    /// <summary>
    /// Marks the region <paramref name="bounds"/> on a 0-based page as a redaction
    /// to be applied later (FR-SEC-02 mark step, non-destructive). The rectangle
    /// must be non-degenerate (x1 &gt; x0 and y1 &gt; y0).
    /// </summary>
    public static async ValueTask MarkRegionAsync(
        IPdfEngine engine,
        int pageIndex,
        PdfRect bounds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        PdfRect normalized = Normalize(bounds);
        if (normalized.X1 <= normalized.X0 || normalized.Y1 <= normalized.Y0)
        {
            throw new ArgumentException(
                "A redaction region must be non-degenerate (a positive width and height).", nameof(bounds));
        }

        await engine.AddRedactionAsync(pageIndex, normalized, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Applies every redaction region already marked on the page (FR-SEC-02 apply
    /// step). The caller is responsible for undo isolation: shells should wrap this
    /// in an <see cref="Editing.ApplyRedactionsCommand"/> so a pre-apply snapshot
    /// is restored on undo. Returns the number of regions applied.
    /// </summary>
    public static ValueTask<int> ApplyRedactionsAsync(
        IPdfEngine engine,
        int pageIndex,
        RedactionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return engine.ApplyRedactionsAsync(pageIndex, options, cancellationToken);
    }

    /// <summary>
    /// Returns an undoable apply command for every redaction region marked on the
    /// page (FR-SEC-02 + FR-EDIT-05). Push it on an
    /// <see cref="Editing.EditCommandStack"/>: executing snapshots the pre-apply
    /// document to a scratch file and applies the redactions; undo restores that
    /// snapshot; the stack disposes the command (and the snapshot) when pruned.
    /// This is the entry point shells should use for user-facing applies.
    /// </summary>
    public static Editing.ApplyRedactionsCommand ApplyAsync(
        IPdfEngine engine,
        int pageIndex,
        RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return new Editing.ApplyRedactionsCommand(engine, pageIndex, options);
    }

    /// <summary>Returns a copy of <paramref name="rect"/> with x0&lt;=x1 and y0&lt;=y1.</summary>
    internal static PdfRect Normalize(PdfRect rect)
        => new(
            Math.Min(rect.X0, rect.X1),
            Math.Min(rect.Y0, rect.Y1),
            Math.Max(rect.X0, rect.X1),
            Math.Max(rect.Y0, rect.Y1));
}
