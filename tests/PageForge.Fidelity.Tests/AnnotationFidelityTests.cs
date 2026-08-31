// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-ANNOT fidelity: exercises the real shim's add / list / flatten / save
/// primitives end-to-end against a deterministic fixture. Confirms that added
/// annotations come back from the list API with the expected types, that a
/// flatten-on-export of a single type removes exactly those annotations from the
/// saved document while leaving others intact, and that the flattened page still
/// renders.
/// </summary>
public sealed class AnnotationFidelityTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public async Task Add_flatten_and_save_round_trip()
    {
        string src = Fixture("sample-pages3.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"annot-{Guid.NewGuid():N}.pdf");
        try
        {
            await using (MuPdfEngine editor = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await editor.OpenAsync(src);
                Assert.True(info.PageCount >= 1);

                var quads = new[]
                {
                    new PdfQuad(new PdfPoint(72, 700), new PdfPoint(200, 700), new PdfPoint(200, 712), new PdfPoint(72, 712)),
                };
                await AnnotationService.AddHighlightAsync(editor, 0, quads, (0.98, 0.9, 0.1));
                await AnnotationService.AddInkAsync(editor, 0, new[]
                {
                    new PdfPoint(72, 650), new PdfPoint(120, 640), new PdfPoint(170, 655),
                }, (1.0, 0.0, 0.0));
                await AnnotationService.AddTextNoteAsync(editor, 0, 300, 300, 340, 330, "Review this block");

                IReadOnlyList<PdfAnnotation> before = await editor.ListAnnotationsAsync(0);
                Assert.Equal(3, before.Count);
                Assert.Contains(before, a => a.Type == AnnotationType.Highlight);
                Assert.Contains(before, a => a.Type == AnnotationType.Ink);
                Assert.Contains(before, a => a.Type == AnnotationType.Text && a.Contents == "Review this block");

                // Flatten only the highlight; the ink and text note must survive.
                await AnnotationService.FlattenForExportAsync(
                    editor, info.PageCount, new HashSet<AnnotationType> { AnnotationType.Highlight }, output);
            }

            Assert.True(File.Exists(output));

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(output);
                Assert.Equal(3, reopened.PageCount);

                IReadOnlyList<PdfAnnotation> after = await reader.ListAnnotationsAsync(0);
                Assert.DoesNotContain(after, a => a.Type == AnnotationType.Highlight);
                Assert.Contains(after, a => a.Type == AnnotationType.Ink);
                Assert.Contains(after, a => a.Type == AnnotationType.Text);

                RenderedPdfPage page = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(page.PngBytes.Length > 100, "Flattened page did not render.");
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
