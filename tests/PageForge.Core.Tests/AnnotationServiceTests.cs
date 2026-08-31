// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-ANNOT unit tests for <see cref="AnnotationService"/>: convenience builders
/// must translate into correct engine calls, per-type validation must reject
/// incomplete specs, and flatten-on-export must flatten only the selected types
/// across only the matching pages, then persist the document. The engine seam
/// (fake) records these calls so no native dependency is needed.
/// </summary>
public sealed class AnnotationServiceTests
{
    private static readonly IReadOnlyList<PdfQuad> Quads = new[]
    {
        new PdfQuad(new PdfPoint(10, 10), new PdfPoint(50, 10), new PdfPoint(50, 20), new PdfPoint(10, 20)),
    };

    [Fact]
    public async Task List_returns_engine_annotations()
    {
        var engine = new FakePdfEngine(3);
        engine.AddStoredAnnotation(1, AnnotationType.Highlight, "note");

        var result = await AnnotationService.ListAsync(engine, 1);

        var annot = Assert.Single(result);
        Assert.Equal(AnnotationType.Highlight, annot.Type);
        Assert.Equal("note", annot.Contents);
    }

    [Fact]
    public async Task AddHighlight_forwards_quad_run_and_color()
    {
        var engine = new FakePdfEngine(1);

        await AnnotationService.AddHighlightAsync(engine, 0, Quads, (0.98, 0.9, 0.1));

        var annotations = await engine.ListAnnotationsAsync(0);
        var annot = Assert.Single(annotations);
        Assert.Equal(AnnotationType.Highlight, annot.Type);
        Assert.Equal(10, annot.X0);
        Assert.Equal(20, annot.Y1);
    }

    [Fact]
    public async Task AddTextNote_requires_contents()
    {
        var engine = new FakePdfEngine(1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            AnnotationService.AddTextNoteAsync(engine, 0, 10, 10, 50, 20, string.Empty).AsTask());
    }

    [Fact]
    public async Task AddInk_computes_bounding_rect()
    {
        var engine = new FakePdfEngine(1);

        await AnnotationService.AddInkAsync(engine, 0, new[] { new PdfPoint(5, 8), new PdfPoint(20, 3), new PdfPoint(9, 12) });

        var annotations = await engine.ListAnnotationsAsync(0);
        var annot = Assert.Single(annotations);
        Assert.Equal(AnnotationType.Ink, annot.Type);
        Assert.Equal(5, annot.X0);
        Assert.Equal(20, annot.X1);
        Assert.Equal(3, annot.Y0);
        Assert.Equal(12, annot.Y1);
    }

    [Fact]
    public async Task Add_highlight_without_quads_is_rejected()
    {
        var engine = new FakePdfEngine(1);
        var spec = new AnnotBuildSpec
        {
            Type = AnnotationType.Highlight,
            X0 = 0, Y0 = 0, X1 = 10, Y1 = 10,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => AnnotationService.AddAsync(engine, 0, spec).AsTask());
    }

    [Fact]
    public async Task Add_rejects_inverted_rectangle()
    {
        var engine = new FakePdfEngine(1);
        var spec = new AnnotBuildSpec
        {
            Type = AnnotationType.Square,
            X0 = 50, Y0 = 0, X1 = 10, Y1 = 10,
        };

        await Assert.ThrowsAsync<ArgumentException>(() => AnnotationService.AddAsync(engine, 0, spec).AsTask());
    }

    [Fact]
    public async Task Add_rejects_out_of_range_opacity()
    {
        var engine = new FakePdfEngine(1);
        var spec = new AnnotBuildSpec
        {
            Type = AnnotationType.Circle,
            X0 = 0, Y0 = 0, X1 = 10, Y1 = 10,
            Opacity = 1.5,
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AnnotationService.AddAsync(engine, 0, spec).AsTask());
    }

    [Fact]
    public async Task FlattenForExport_flattens_only_pages_with_matching_type()
    {
        var engine = new FakePdfEngine(3);
        engine.AddStoredAnnotation(0, AnnotationType.Highlight);
        engine.AddStoredAnnotation(1, AnnotationType.Text);
        engine.AddStoredAnnotation(2, AnnotationType.Highlight);

        await AnnotationService.FlattenForExportAsync(engine, 3, new HashSet<AnnotationType> { AnnotationType.Highlight }, "export.pdf");

        Assert.Equal(new[] { 0, 2 }, engine.FlattenedPages);
        Assert.Equal("export.pdf", engine.LastSavePath);
    }

    [Fact]
    public async Task FlattenForExport_leaves_other_pages_untouched()
    {
        var engine = new FakePdfEngine(3);
        engine.AddStoredAnnotation(0, AnnotationType.Text);

        await AnnotationService.FlattenForExportAsync(engine, 3, new HashSet<AnnotationType> { AnnotationType.Highlight }, "export.pdf");

        Assert.Empty(engine.FlattenedPages);
        Assert.Equal("export.pdf", engine.LastSavePath);
    }
}
