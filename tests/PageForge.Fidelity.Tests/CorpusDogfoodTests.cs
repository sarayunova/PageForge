// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// Phase 1 exit-gate dogfood (TSD §12): every real-document corpus PDF is
/// driven through the full viewer / organizer / annotator pipeline on the real
/// MuPDF shim with zero crashes. For each corpus file we (1) open it, (2) walk
/// every page rendering it, (3) run an organizer build that reorders/rotates
/// pages and reopen the result, and (4) add + flatten annotations and reopen the
/// saved file. Any unhandled exception from the shim on any corpus document
/// fails this suite and blocks the Phase 1 exit gate.
/// </summary>
public sealed class CorpusDogfoodTests
{
    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static TheoryData<string> CorpusPdfs()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(CorpusDir, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            data.Add(file);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public async Task Full_pipeline_dogfood_with_zero_crashes(string pdfPath)
    {
        string name = Path.GetFileName(pdfPath);
        string organizerOut = Path.Combine(AppContext.BaseDirectory, $"dogfood-organizer-{Guid.NewGuid():N}.pdf");
        string annotOut = Path.Combine(AppContext.BaseDirectory, $"dogfood-annot-{Guid.NewGuid():N}.pdf");
        try
        {
            int pageCount;
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                // (1) open
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                pageCount = info.PageCount;
                Assert.True(pageCount >= 1);

                // (2) walk every page and render it
                for (int page = 0; page < pageCount; page++)
                {
                    RenderedPdfPage png = await engine.RenderPageToPngAsync(page, 72);
                    Assert.True(png.PngBytes.Length > 100, $"page {page} of {name} did not render.");
                }

                // (3) organizer: rotate last page 90° and reorder [last, ..., first]
                var job = new List<PageBuildRef>(pageCount);
                for (int page = pageCount - 1; page >= 0; page--)
                {
                    job.Add(new PageBuildRef(pdfPath, page, page == 0 ? 1 : 0));
                }

                int written = await PdfPageOrganizer.BuildAsync(engine, organizerOut, job);
                Assert.Equal(pageCount, written);
            }

            // (4) the organizer result must reopen and render
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(organizerOut);
                Assert.Equal(pageCount, reopened.PageCount);
                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "Organizer output page 0 did not render.");
            }

            // (5) annotate the original: add highlight + ink + text note, flatten on save, reopen
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);

                await AnnotationService.AddHighlightAsync(engine, 0, new[]
                {
                    new PdfQuad(new PdfPoint(72, 700), new PdfPoint(220, 700), new PdfPoint(220, 712), new PdfPoint(72, 712)),
                });
                await AnnotationService.AddInkAsync(engine, 0, new[]
                {
                    new PdfPoint(72, 650), new PdfPoint(120, 640), new PdfPoint(170, 655),
                }, (1.0, 0.0, 0.0));
                await AnnotationService.AddTextNoteAsync(engine, 0, 300, 300, 340, 330, "Dogfood review note");

                IReadOnlyList<PdfAnnotation> listed = await AnnotationService.ListAsync(engine, 0);
                Assert.Equal(3, listed.Count);

                await AnnotationService.FlattenForExportAsync(
                    engine, info.PageCount, new HashSet<AnnotationType> { AnnotationType.Highlight }, annotOut);
            }

            Assert.True(File.Exists(annotOut), "Annotation export did not write a file.");
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(annotOut);
                Assert.Equal(pageCount, reopened.PageCount);
                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "Annotated output page 0 did not render.");
            }
        }
        finally
        {
            TryDelete(organizerOut);
            TryDelete(annotOut);
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
