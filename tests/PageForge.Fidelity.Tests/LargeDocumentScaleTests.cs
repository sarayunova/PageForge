// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
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
/// What this does NOT establish: the fixture is built by repeating a corpus
/// document's pages, so it has 2,000 page objects over a handful of distinct
/// content streams. That is the right shape for testing page-table handling,
/// random access and lazy loading, and the wrong shape for testing memory use
/// with 2,000 pages of DISTINCT content, which carries far more data. Read a
/// pass as "the page count is handled", not as "any 2,000-page document is
/// fine".
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
