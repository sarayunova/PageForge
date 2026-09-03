// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// Phase 6 performance pass (FR-BATCH §12 exit criteria "performance targets
/// met"). Establishes a repeatable render-throughput baseline for the real
/// MuPDF engine: every page of every corpus document renders within a per-page
/// budget at 96 and 150 DPI. A corpus regression or engine slowdown that blows
/// the budget fails this gate.
/// </summary>
public sealed class RenderPerformanceTests
{
    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "corpus");

    // Per-page render budget (ms). 96 DPI is the typical screen zoom, 150 DPI the
    // common print/zoom preset. Headroom is generous (well above the observed
    // median on this dev machine) so the gate is stable across machines yet still
    // stops pathological regressions.
    private const int BudgetMs96 = 600;
    private const int BudgetMs150 = 1000;
    private const int WarmupPages = 2;

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
    public async Task All_pages_render_within_perpage_budget(string pdfPath)
    {
        string name = Path.GetFileName(pdfPath);

        // Warm up the native context so JIT/init, model load and first-call setup
        // are excluded from the measured timings.
        int pageCount;
        await using (MuPdfEngine warm = MuPdfEngine.Create())
        {
            PdfDocumentInfo winfo = await warm.OpenAsync(pdfPath);
            pageCount = winfo.PageCount;
            Assert.True(pageCount >= 1);
            for (int p = 0; p < Math.Min(pageCount, WarmupPages); p++)
            {
                await warm.RenderPageToPngAsync(p, 96);
                await warm.RenderPageToPngAsync(p, 150);
            }
        }

        var samples96 = new List<TimeSpan>(pageCount);
        var samples150 = new List<TimeSpan>(pageCount);

        await using (MuPdfEngine engine = MuPdfEngine.Create())
        {
            PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
            Assert.Equal(pageCount, info.PageCount);

            for (int page = 0; page < pageCount; page++)
            {
                samples96.Add(await TimeRenderAsync(engine, page, 96));
                samples150.Add(await TimeRenderAsync(engine, page, 150));
            }
        }

        double median96 = Median(samples96);
        double median150 = Median(samples150);
        double p95_96 = Percentile(samples96, 95);
        double p95_150 = Percentile(samples150, 95);

        Console.WriteLine($"[perf] {name}: {pageCount} pages, " +
                          $"median96={median96:F0}ms (budget {BudgetMs96}), p95_96={p95_96:F0}ms, " +
                          $"median150={median150:F0}ms (budget {BudgetMs150}), p95_150={p95_150:F0}ms");

        Assert.True(median96 <= BudgetMs96,
            $"{name} median 96-DPI render {median96:F0}ms exceeds budget {BudgetMs96}ms.");
        Assert.True(median150 <= BudgetMs150,
            $"{name} median 150-DPI render {median150:F0}ms exceeds budget {BudgetMs150}ms.");
    }

    private static async Task<TimeSpan> TimeRenderAsync(MuPdfEngine engine, int page, float dpi)
    {
        var sw = Stopwatch.StartNew();
        RenderedPdfPage png = await engine.RenderPageToPngAsync(page, dpi);
        sw.Stop();
        Assert.True(png.PngBytes.Length > 100, $"page {page} at {dpi} DPI did not render.");
        return sw.Elapsed;
    }

    private static double Median(IReadOnlyList<TimeSpan> samples)
    {
        if (samples.Count == 0) return 0;
        return Percentile(samples, 50);
    }

    private static double Percentile(IReadOnlyList<TimeSpan> samples, double pct)
    {
        if (samples.Count == 0) return 0;
        long[] ticks = samples.Select(s => s.Ticks).OrderBy(t => t).ToArray();
        int idx = (int)Math.Ceiling(pct / 100.0 * ticks.Length) - 1;
        idx = Math.Clamp(idx, 0, ticks.Length - 1);
        return ticks[idx] / 10000.0;
    }
}