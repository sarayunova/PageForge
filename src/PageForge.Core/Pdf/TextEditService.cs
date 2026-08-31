// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns high-level FR-EDIT-01/06 operations (hit-test a click
/// against the page's text runs, rewrite a run) into calls on the
/// <see cref="IPdfEngine"/> seam, doing the validation that does not depend on
/// the native engine. Keeping the orchestration shared between the WPF/WinUI
/// shells and fully unit-testable against a fake engine.
/// </summary>
public static class TextEditService
{
    /// <summary>
    /// Returns the run whose bounding box contains the click point, or null when
    /// the click is not on editable text. Picks the most specific run when boxes
    /// overlap (the one with the smallest area).
    /// </summary>
    public static async ValueTask<PdfTextRun?> HitTestAsync(
        IPdfEngine engine,
        int pageIndex,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(pageIndex, cancellationToken).ConfigureAwait(false);

        PdfTextRun? best = null;
        double bestArea = double.MaxValue;
        foreach (PdfTextRun run in runs)
        {
            if (!run.Contains(x, y))
            {
                continue;
            }

            double area = Math.Max(0, (run.X1 - run.X0)) * Math.Max(0, (run.Y1 - run.Y0));
            if (area < bestArea)
            {
                best = run;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// Rewrites the text of one run on a page, returning the undo/redo receipt.
    /// The caller (usually the editing command layer via an
    /// <see cref="Editing.TextEditCommand"/>) keeps the receipt for undo/redo.
    /// Callers performing user-facing edits should first route through
    /// <see cref="PrepareRewriteAsync"/> so a box-overflow collision surfaces the
    /// FR-EDIT-02 warning before this commit runs.
    /// </summary>
    public static ValueTask<PdfTextEditReceipt> RewriteRunAsync(
        IPdfEngine engine,
        int pageIndex,
        int runIndex,
        string newText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (runIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(newText);
        return engine.RewriteTextRunAsync(pageIndex, runIndex, newText, cancellationToken);
    }

    /// <summary>
    /// FR-EDIT-02 confirmation gateway. Evaluates whether rewriting run
    /// <paramref name="runIndex"/> to <paramref name="newText"/> would grow its
    /// box past the original by more than the configured threshold and, if so,
    /// whether the grown box would collide with any sibling run on the page.
    ///
    /// The return value is a <see cref="PreparedTextEdit"/>: when its
    /// <see cref="PreparedTextEdit.NeedsConfirmation"/> is true the caller must
    /// surface the warning and obtain explicit confirmation before calling
    /// <see cref="RewriteRunAsync"/> (never commit silently). Pure overflow
    /// without a collision grows the box cleanly and is safe to commit. The
    /// estimate here is advisory — the engine computes the exact box on commit.
    /// </summary>
    public static async ValueTask<PreparedTextEdit> PrepareRewriteAsync(
        IPdfEngine engine,
        int pageIndex,
        int runIndex,
        string newText,
        OverflowOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (runIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(newText);

        IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(pageIndex, cancellationToken).ConfigureAwait(false);
        if (runIndex >= runs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex), $"Run index {runIndex} is out of range ({runs.Count} runs).");
        }

        PdfTextRun target = runs[runIndex];

        // Siblings are every other run on the page; the edited run itself is excluded.
        var siblings = new List<PdfTextRun>(runs.Count - 1);
        for (int i = 0; i < runs.Count; i++)
        {
            if (i != runIndex)
            {
                siblings.Add(runs[i]);
            }
        }

        PdfRect estimated = TextOverflowDetector.EstimatedGrownBox(target, newText);
        TextEditOverflowResult analysis = TextOverflowDetector.Analyze(target, estimated, siblings, options);
        return new PreparedTextEdit(runIndex, newText, analysis);
    }

    /// <summary>
    /// FR-EDIT-03 surfacing hook. Checks whether rewriting run
    /// <paramref name="runIndex"/> to <paramref name="newText"/> would introduce
    /// characters the run's font cannot render faithfully (font not embedded,
    /// missing glyph, or outside the family's character set) and resolves bundled
    /// substitutions for them. The caller surfaces <c>result.HasIssues</c> inline
    /// and in the properties panel before commit; the engine's rewrite remains the
    /// authoritative hard gate on encodability.
    /// </summary>
    public static async ValueTask<FontFidelityResult> CheckFontFidelityAsync(
        IPdfEngine engine,
        int pageIndex,
        int runIndex,
        string newText,
        FontFallbackTable? table = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (runIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(newText);

        IReadOnlyList<PdfTextRun> runs = await engine.ListTextRunsAsync(pageIndex, cancellationToken).ConfigureAwait(false);
        if (runIndex >= runs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex), $"Run index {runIndex} is out of range ({runs.Count} runs).");
        }

        PdfTextRun target = runs[runIndex];
        return FontFidelityAnalyzer.Analyze(target, newText, table);
    }
}