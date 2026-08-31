// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns high-level FR-ANNOT operations (add annotation of a
/// given kind, list annotations, flatten selected types on export) into calls on
/// the <see cref="IPdfEngine"/> seam, performing the validation that does not
/// depend on the native engine. Keeping the orchestration here makes it shared
/// between the WPF/WinUI shells and fully unit-testable against a fake engine.
/// </summary>
public static class AnnotationService
{
    /// <summary>Returns the annotations on one page, in document order.</summary>
    public static ValueTask<IReadOnlyList<PdfAnnotation>> ListAsync(
        IPdfEngine engine,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.ListAnnotationsAsync(pageIndex, cancellationToken);
    }

    /// <summary>Adds an annotation exactly as described by the spec.</summary>
    public static async ValueTask AddAsync(
        IPdfEngine engine,
        int pageIndex,
        AnnotBuildSpec annotation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(annotation);
        ValidateSpec(annotation);
        await engine.AddAnnotationAsync(pageIndex, annotation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Shortcut for a text highlight across one or more quads.</summary>
    public static ValueTask AddHighlightAsync(
        IPdfEngine engine,
        int pageIndex,
        IReadOnlyList<PdfQuad> quads,
        (double R, double G, double B)? color = null,
        CancellationToken cancellationToken = default)
        => AddAsync(engine, pageIndex, new AnnotBuildSpec
        {
            Type = AnnotationType.Highlight,
            X0 = MinX(quads), Y0 = MinY(quads), X1 = MaxX(quads), Y1 = MaxY(quads),
            Quads = quads,
            Color = color,
        }, cancellationToken);

    /// <summary>Shortcut for a text note pinned at the given rectangle.</summary>
    public static ValueTask AddTextNoteAsync(
        IPdfEngine engine,
        int pageIndex,
        double x0, double y0, double x1, double y1,
        string contents,
        CancellationToken cancellationToken = default)
        => AddAsync(engine, pageIndex, new AnnotBuildSpec
        {
            Type = AnnotationType.Text,
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
            Contents = contents,
        }, cancellationToken);

    /// <summary>Shortcut for an ink stroke through the given vertices.</summary>
    public static ValueTask AddInkAsync(
        IPdfEngine engine,
        int pageIndex,
        IReadOnlyList<PdfPoint> points,
        (double R, double G, double B)? color = null,
        CancellationToken cancellationToken = default)
    {
        double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
        foreach (PdfPoint p in points)
        {
            x0 = Math.Min(x0, p.X); x1 = Math.Max(x1, p.X);
            y0 = Math.Min(y0, p.Y); y1 = Math.Max(y1, p.Y);
        }

        return AddAsync(engine, pageIndex, new AnnotBuildSpec
        {
            Type = AnnotationType.Ink,
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
            InkPoints = points,
            Color = color,
        }, cancellationToken);
    }

    /// <summary>
    /// Flatten-on-export (FR-ANNOT-02): writes <paramref name="outputPath"/> with
    /// every page whose annotations include at least one of
    /// <paramref name="typesToFlatten"/> having that annotation baked into static
    /// page content. Annotations of other types are preserved. Pages with no
    /// matching annotation are copied through unchanged; the document is
    /// otherwise untouched.
    /// </summary>
    public static async ValueTask FlattenForExportAsync(
        IPdfEngine engine,
        int pageCount,
        IReadOnlySet<AnnotationType> typesToFlatten,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(typesToFlatten);
        ArgumentNullException.ThrowIfNull(outputPath);
        if (pageCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        for (int page = 0; page < pageCount; page++)
        {
            IReadOnlyList<PdfAnnotation> annotations = await engine
                .ListAnnotationsAsync(page, cancellationToken).ConfigureAwait(false);

            bool anyMatch = false;
            foreach (PdfAnnotation a in annotations)
            {
                if (typesToFlatten.Contains(a.Type))
                {
                    anyMatch = true;
                    break;
                }
            }

            if (anyMatch)
            {
                await engine.FlattenAnnotationsAsync(page, typesToFlatten, cancellationToken).ConfigureAwait(false);
            }
        }

        await engine.SaveAsAsync(outputPath, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateSpec(AnnotBuildSpec spec)
    {
        if (spec.X1 < spec.X0 || spec.Y1 < spec.Y0)
        {
            throw new ArgumentException("The annotation rectangle must be normalized (x1 >= x0 and y1 >= y0).", nameof(spec));
        }

        if (spec.Type is AnnotationType.Highlight or AnnotationType.Underline or AnnotationType.StrikeOut
            && (spec.Quads is null || spec.Quads.Count == 0))
        {
            throw new ArgumentException(
                $"{spec.Type} annotations require at least one quad selecting the text run.", nameof(spec));
        }

        if (spec.Type == AnnotationType.Ink && (spec.InkPoints is null || spec.InkPoints.Count == 0))
        {
            throw new ArgumentException("Ink annotations require at least one stroke vertex.", nameof(spec));
        }

        if (spec.Type == AnnotationType.Text && string.IsNullOrEmpty(spec.Contents))
        {
            throw new ArgumentException("Text annotations require contents.", nameof(spec));
        }

        if (spec.Color is { } c && (c.R < 0 || c.R > 1 || c.G < 0 || c.G > 1 || c.B < 0 || c.B > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "Color components must be in [0,1].");
        }

        if (spec.Opacity is { } o && (o < 0 || o > 1))
        {
            throw new ArgumentOutOfRangeException(nameof(spec), "Opacity must be in [0,1].");
        }
    }

    private static double MinX(IReadOnlyList<PdfQuad> quads) => quads.Min(q => Math.Min(q.LowerLeft.X, Math.Min(q.LowerRight.X, Math.Min(q.UpperLeft.X, q.UpperRight.X))));

    private static double MaxX(IReadOnlyList<PdfQuad> quads) => quads.Max(q => Math.Max(q.LowerLeft.X, Math.Max(q.LowerRight.X, Math.Max(q.UpperLeft.X, q.UpperRight.X))));

    private static double MinY(IReadOnlyList<PdfQuad> quads) => quads.Min(q => Math.Min(q.LowerLeft.Y, Math.Min(q.LowerRight.Y, Math.Min(q.UpperLeft.Y, q.UpperRight.Y))));

    private static double MaxY(IReadOnlyList<PdfQuad> quads) => quads.Max(q => Math.Max(q.LowerLeft.Y, Math.Max(q.LowerRight.Y, Math.Max(q.UpperLeft.Y, q.UpperRight.Y))));
}
