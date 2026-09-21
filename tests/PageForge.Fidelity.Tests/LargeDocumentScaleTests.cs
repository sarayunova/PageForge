// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// The scale FR-VIEW-01 names: 2,000 pages, loaded lazily, with any page
/// reachable at interactive speed.
///
/// The requirement had been carried as met on the strength of the lazy-loading
/// code existing. Nothing exercised it — the largest fixture in the corpus is
/// four pages — so "up to at least 2,000 pages" was an untested claim about a
/// number nobody had ever tried.
/// </summary>
/// <remarks>
/// Two fixtures, because one of them cannot answer the memory question. The
/// first repeats a corpus document's pages, which gives 2,000 page objects
/// over a handful of content streams — the right shape for page-table
/// handling, random access and open cost, and useless for memory, since a
/// document with four distinct streams looks lazy even to an engine that
/// keeps every stream it sees. The second is written here as raw PDF with
/// text on every page that appears nowhere else, so retention has somewhere
/// to show, and so a page can be asked to prove which page it is.
/// </remarks>
public sealed class LargeDocumentScaleTests
{
    private const int LargePageCount = 2000;

    /// <summary>
    /// Ceiling for opening the large document. TRD §6 asks for a 100-page text
    /// document in under 1.5s on reference hardware; the same figure is applied
    /// here to a document twenty times larger, because opening is supposed to
    /// be independent of page count. Observed on this dev machine: about 10ms.
    /// </summary>
    private const int OpenBudgetMs = 1500;

    /// <summary>
    /// Per-page render ceiling. TRD §6 asks for 150ms during scroll on
    /// reference hardware; 600ms here matches the existing render-performance
    /// gate, which is deliberately four times the target so a CI runner of
    /// unknown speed cannot make it flaky. Passing is a regression tripwire,
    /// not evidence that the 150ms target is met. Observed here: 32-102ms.
    /// </summary>
    private const int RenderBudgetMs = 600;

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    /// <summary>
    /// Builds the large fixture by repeating a corpus document's pages. Built
    /// per run rather than committed: two thousand pages of checked-in binary
    /// to prove a number is a poor trade, and the build is itself part of what
    /// is being demonstrated.
    /// </summary>
    private static async Task<string> BuildLargeDocumentAsync()
    {
        string source = Fixture("contract-multipage.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"scale-{Guid.NewGuid():N}.pdf");

        await using MuPdfEngine engine = MuPdfEngine.Create();
        PdfDocumentInfo info = await engine.OpenAsync(source);

        var pages = new List<PageBuildRef>(LargePageCount);
        for (int i = 0; i < LargePageCount; i++)
        {
            pages.Add(new PageBuildRef(source, i % info.PageCount));
        }

        int built = await PdfPageOrganizer.BuildAsync(engine, output, pages);
        Assert.Equal(LargePageCount, built);
        return output;
    }

    [Fact]
    public async Task A_two_thousand_page_document_opens_lazily_and_renders_any_page()
    {
        string large = await BuildLargeDocumentAsync();

        try
        {
            // Timed on a fresh engine, so the build above cannot warm anything
            // that belongs inside the measurement.
            var opening = Stopwatch.StartNew();
            await using MuPdfEngine engine = MuPdfEngine.Create();
            PdfDocumentInfo info = await engine.OpenAsync(large);
            opening.Stop();

            Assert.Equal(LargePageCount, info.PageCount);

            // The lazy-loading half of FR-VIEW-01. Reading and laying out two
            // thousand pages up front would take far longer than this, so a
            // pass means the open did not touch them.
            Assert.True(
                opening.ElapsedMilliseconds < OpenBudgetMs,
                $"Opening a {LargePageCount}-page document took {opening.ElapsedMilliseconds}ms, " +
                $"over the {OpenBudgetMs}ms ceiling. FR-VIEW-01 requires pages to load lazily, " +
                "so open time should not scale with the page count.");

            // First, middle and last. The last matters most: a page tree walked
            // linearly on every access still renders the right thing, and only
            // shows itself as the document gets longer.
            foreach (int index in new[] { 0, LargePageCount / 2, LargePageCount - 1 })
            {
                var render = Stopwatch.StartNew();
                RenderedPdfPage page = await engine.RenderPageToPngAsync(index, 96f);
                render.Stop();

                Assert.True(
                    page.PngBytes.Length > 0 && page.WidthPixels > 0 && page.HeightPixels > 0,
                    $"Page {index} of {LargePageCount} rendered nothing.");

                Assert.True(
                    render.ElapsedMilliseconds < RenderBudgetMs,
                    $"Page {index} of {LargePageCount} took {render.ElapsedMilliseconds}ms to " +
                    $"render, over the {RenderBudgetMs}ms tripwire. If only the later pages are " +
                    "slow, page access is walking the document rather than seeking into it.");
            }
        }
        finally
        {
            TryDelete(large);
        }
    }

    [Fact]
    public async Task Opening_a_large_document_costs_about_what_a_small_one_costs()
    {
        string large = await BuildLargeDocumentAsync();

        try
        {
            long small = await TimeOpenAsync(Fixture("contract-multipage.pdf"));
            long big = await TimeOpenAsync(large);

            // Five hundred times the pages. Eager loading would show up as a
            // proportional cost; this asserts the two stay within the same
            // order, with enough slack that a slow or contended machine cannot
            // fail it on noise alone.
            Assert.True(
                big <= small + OpenBudgetMs,
                $"Opening {LargePageCount} pages took {big}ms against {small}ms for four pages. " +
                "The gap suggests work proportional to the page count at open time, which is " +
                "exactly what FR-VIEW-01's lazy loading exists to avoid.");
        }
        finally
        {
            TryDelete(large);
        }
    }

    [Fact]
    public async Task Two_thousand_pages_of_distinct_content_stay_lazy_and_addressable()
    {
        // The repeated-page fixture above cannot answer this: 2,000 pages over
        // four content streams would look lazy even if the engine held every
        // stream it had ever seen. Here each page carries its own text, so
        // memory has somewhere to go if pages are being retained.
        string large = BuildDistinctPageDocument();

        try
        {
            using Process self = Process.GetCurrentProcess();
            long workingSetBefore = self.WorkingSet64;

            await using MuPdfEngine engine = MuPdfEngine.Create();
            PdfDocumentInfo info = await engine.OpenAsync(large);
            Assert.Equal(LargePageCount, info.PageCount);

            // Random access has to return the page that was asked for. A page
            // tree read with an off-by-one, or a cache keyed wrongly, still
            // returns a valid page and renders it perfectly; the only way to
            // catch that is for every page to say which one it is.
            foreach (int index in new[] { 0, 1, LargePageCount / 2, LargePageCount - 1 })
            {
                PageText text = await engine.GetPageTextAsync(index);
                Assert.Contains($"UNIQUE-MARKER-{index}-END", text.Text, StringComparison.Ordinal);
            }

            // Render a spread of pages, then check the process has not grown as
            // though it kept them. Rendering is where a page's content is
            // actually read, so retention would show here if anywhere.
            for (int index = 0; index < LargePageCount; index += LargePageCount / 40)
            {
                await engine.RenderPageToPngAsync(index, 96f);
            }

            self.Refresh();
            long grewMiB = (self.WorkingSet64 - workingSetBefore) / (1024 * 1024);

            // Observed on this dev machine: 9 MiB. The ceiling is an order of
            // magnitude above that, because it is here to catch growth
            // proportional to the document rather than to police allocation —
            // retaining two thousand pages would be far past it either way.
            Assert.True(
                grewMiB < 128,
                $"Opening and sampling a {LargePageCount}-page document of distinct pages grew " +
                $"the process by {grewMiB} MiB. FR-VIEW-01 requires that the whole document is " +
                "never loaded into memory at once.");
        }
        finally
        {
            TryDelete(large);
        }
    }

    /// <summary>
    /// Writes a PDF whose every page carries text nothing else does.
    ///
    /// Built as raw PDF rather than assembled from the corpus, because the
    /// point is content that CANNOT be shared between pages: the corpus has
    /// four pages, and any document built by repeating them shares their
    /// streams however many pages it ends up with. The syntax is the same
    /// hand-written form <c>tools/generate-corpus.ps1</c> uses for the
    /// fixtures themselves.
    /// </summary>
    private static string BuildDistinctPageDocument()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"scale-distinct-{Guid.NewGuid():N}.pdf");

        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new StreamWriter(file, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            // Object 0 is the free head of the cross-reference table and has no
            // offset of its own, so the list starts with a placeholder.
            var offsets = new List<long> { 0 };

            void StartObject(int number)
            {
                writer.Flush();
                offsets.Add(file.Position);
                writer.Write($"{number} 0 obj\n");
            }

            writer.Write("%PDF-1.4\n");

            // 1 catalog, 2 page tree, 3 font, then a page and a content stream
            // per page: page i is object 4 + 2i and its stream 5 + 2i.
            StartObject(1);
            writer.Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

            StartObject(2);
            writer.Write($"<< /Type /Pages /Count {LargePageCount} /Kids [");
            for (int i = 0; i < LargePageCount; i++)
            {
                writer.Write($"{4 + (2 * i)} 0 R ");
            }

            writer.Write("] >>\nendobj\n");

            StartObject(3);
            writer.Write("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

            for (int i = 0; i < LargePageCount; i++)
            {
                int pageObject = 4 + (2 * i);
                int streamObject = pageObject + 1;

                StartObject(pageObject);
                writer.Write(
                    "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
                    $"/Resources << /Font << /F1 3 0 R >> >> /Contents {streamObject} 0 R >>\nendobj\n");

                string body =
                    $"BT /F1 18 Tf 72 700 Td (UNIQUE-MARKER-{i}-END page {i + 1} of {LargePageCount}) Tj ET";

                StartObject(streamObject);
                writer.Write($"<< /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");
            }

            writer.Flush();
            long xref = file.Position;
            int objectCount = 4 + (2 * LargePageCount);

            writer.Write($"xref\n0 {objectCount}\n0000000000 65535 f \n");
            for (int i = 1; i < objectCount; i++)
            {
                writer.Write($"{offsets[i]:D10} 00000 n \n");
            }

            writer.Write($"trailer\n<< /Size {objectCount} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        }

        return path;
    }

    private static async Task<long> TimeOpenAsync(string path)
    {
        var sw = Stopwatch.StartNew();
        await using MuPdfEngine engine = MuPdfEngine.Create();
        await engine.OpenAsync(path);
        sw.Stop();
        return sw.ElapsedMilliseconds;
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
