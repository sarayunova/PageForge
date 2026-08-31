// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Text;
using System.Windows;
using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using PageForge.Core.View;
using PageForge.MuPdfInterop;

namespace PageForge.App.Wpf;

public partial class App : Application
{
    public static bool SmokeMode =>
        Environment.GetCommandLineArgs().Contains("--smoke", StringComparer.OrdinalIgnoreCase);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (SmokeMode)
        {
            await RunHeadlessProofAsync();
            await RunHeadlessViewerProofAsync();
            await RunHeadlessOrganizerProofAsync();
            await RunHeadlessAnnotationProofAsync();
            await RunHeadlessEditProofAsync();
            await RunHeadlessCorpusDogfoodProofAsync();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// Renders page 1 of the sample document through the real IPdfEngine and
    /// writes the PNG to artifacts/ so the /smoke command can verify it byte-for-byte.
    /// </summary>
    private static async Task RunHeadlessProofAsync()
    {
        try
        {
            string? pdfPath = FindSamplePdf();
            if (pdfPath is null)
            {
                Console.Error.WriteLine("sample document not found");
                Environment.ExitCode = 2;
                return;
            }

            await using MuPdfEngine engine = MuPdfEngine.Create();
            PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
            PdfPageRegion size = await engine.GetPageSizeAsync(0);
            RenderedPdfPage page = await engine.RenderPageToPngAsync(0, 96);

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, "sample-phase0-p1-wpfproof.png");
            await File.WriteAllBytesAsync(outPath, page.PngBytes);

            Console.WriteLine($"rendered {info.DisplayName} p1 {page.WidthPixels}x{page.HeightPixels} px -> {outPath}");
            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"render failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Renders page 1 through the viewer's <see cref="DocumentViewModel"/> and
    /// dumps the outline and a full-text search result, proving the Phase 1
    /// viewer core (lazy render, outline, search) works end-to-end headlessly.
    /// This proof is separate from the pinned Phase 0 p1 PNG.
    /// </summary>
    private static async Task RunHeadlessViewerProofAsync()
    {
        try
        {
            string? pdfPath = FindSamplePdf();
            if (pdfPath is null)
            {
                Console.Error.WriteLine("sample document not found");
                Environment.ExitCode = 2;
                return;
            }

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);

            await using (DocumentViewModel vm = new(MuPdfEngine.Create()))
            {
                await vm.InitializeAsync(pdfPath);

                RenderedPdfPage page = await vm.RenderAsync(0, 96);
                await File.WriteAllBytesAsync(Path.Combine(outDir, "viewer-phase1-p1.png"), page.PngBytes);

                var sb = new StringBuilder();
                sb.AppendLine($"pages={vm.PageCount} outline={vm.Outline.Items.Count}");
                foreach (OutlineItem item in vm.Outline.Items)
                {
                    sb.AppendLine($"{new string(' ', Math.Max(0, item.Depth - 1) * 2)}{item.Title}\tp{item.PageNumber}");
                }

                IReadOnlyList<SearchHit> hits = await vm.SearchAsync("PageForge");
                sb.AppendLine($"search='PageForge' hits={hits.Count}");
                foreach (SearchHit hit in hits)
                {
                    sb.AppendLine($"p{hit.PageIndex + 1}: {hit.Snippet}");
                }

                await File.WriteAllTextAsync(Path.Combine(outDir, "viewer-phase1-outline.txt"), sb.ToString());
                Console.WriteLine($"viewer proof: pages={vm.PageCount} outline={vm.Outline.Items.Count} searchHits={hits.Count}");
            }

            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"viewer proof failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    internal static string? FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PageForge.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }

    internal static string? FindSamplePdf()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string candidate = Path.Combine(root, "tools", "sample-pdf", "sample-phase0.pdf");
        return File.Exists(candidate) ? candidate : null;
    }

    internal static string? FindSamplePagesPdf()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string candidate = Path.Combine(root, "tools", "sample-pdf", "sample-pages3.pdf");
        return File.Exists(candidate) ? candidate : null;
    }

    internal static string? FindCorpusDir()
    {
        string? root = FindRepoRoot();
        if (root is null)
        {
            return null;
        }

        string candidate = Path.Combine(root, "tools", "sample-pdf", "corpus");
        return Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// FR-PAGE proof: drives the real engine's page build (rotate + merge via
    /// <see cref="PdfPageOrganizer"/>) headlessly, writes the built PDF and a
    /// per-page size summary to artifacts/ so the /smoke and /fidelity commands
    /// can verify the rebuilt page set (count, order, and per-page rotation).
    /// </summary>
    private static async Task RunHeadlessOrganizerProofAsync()
    {
        try
        {
            string? pagesPath = FindSamplePagesPdf();
            if (pagesPath is null)
            {
                Console.Error.WriteLine("sample-pages3.pdf not found");
                Environment.ExitCode = 2;
                return;
            }

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);
            string builtPath = Path.Combine(outDir, "organizer-rotated-merged.pdf");
            string summaryPath = Path.Combine(outDir, "organizer-proof.txt");

            // Rotate page 0 by 90° then append a full unmodified copy => 6 pages,
            // where page 1 (1-based) is the rotated landscape original.
            var job = new[]
            {
                new PageBuildRef(pagesPath, 0, 1),
                new PageBuildRef(pagesPath, 1, 0),
                new PageBuildRef(pagesPath, 2, 0),
                new PageBuildRef(pagesPath, 0, 0),
                new PageBuildRef(pagesPath, 1, 0),
                new PageBuildRef(pagesPath, 2, 0),
            };

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                int count = await engine.BuildPdfAsync(builtPath, job);
                await using (MuPdfEngine reader = MuPdfEngine.Create())
                {
                    PdfDocumentInfo info = await reader.OpenAsync(builtPath);
                    var sb = new StringBuilder();
                    sb.AppendLine($"built={count} reopen={info.PageCount}");
                    for (int i = 0; i < info.PageCount; i++)
                    {
                        PdfPageRegion size = await reader.GetPageSizeAsync(i);
                        sb.AppendLine($"p{i + 1}\t{size.WidthPt:F1}x{size.HeightPt:F1}");
                    }

                    await File.WriteAllTextAsync(summaryPath, sb.ToString());
                    Console.WriteLine($"organizer proof: built {info.PageCount} pages (p1 rotated) -> {builtPath}");
                }
            }

            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"organizer proof failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// FR-ANNOT proof: drives the real engine's annotation add + list + selectable
    /// flatten-on-export headlessly and writes a summary + rendered page to
    /// artifacts/. Adds a highlight, a text note and an ink stroke to page 1,
    /// lists them, flattens ONLY the highlight into static content, saves the
    /// copy, reopens it, and asserts the highlight is gone while the note and ink
    /// survive (FR-ANNOT-01/02).
    /// </summary>
    private static async Task RunHeadlessAnnotationProofAsync()
    {
        try
        {
            string? pagesPath = FindSamplePagesPdf();
            if (pagesPath is null)
            {
                Console.Error.WriteLine("sample-pages3.pdf not found");
                Environment.ExitCode = 2;
                return;
            }

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);
            string flattenedPath = Path.Combine(outDir, "annot-flattened.pdf");
            string summaryPath = Path.Combine(outDir, "annot-proof.txt");
            File.Delete(flattenedPath);

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(pagesPath);

                var quad = new PdfQuad(
                    new PdfPoint(60, 700), new PdfPoint(400, 700),
                    new PdfPoint(400, 720), new PdfPoint(60, 720));
                await AnnotationService.AddHighlightAsync(engine, 0, new[] { quad }, (0.96, 0.98, 0.30));

                await AnnotationService.AddTextNoteAsync(engine, 0, 480, 700, 520, 740, "PageForge note");

                var ink = new List<PdfPoint>();
                for (int i = 0; i <= 30; i++)
                {
                    double t = (double)i / 30;
                    ink.Add(new PdfPoint(80 + 320 * t, 400 + Math.Sin(t * Math.PI * 4) * 12));
                }
                await AnnotationService.AddInkAsync(engine, 0, ink, (0.9, 0.1, 0.1));

                IReadOnlyList<PdfAnnotation> before = await AnnotationService.ListAsync(engine, 0);
                string beforeTypes = string.Join(",", before.Select(a => a.Type.ToString()));

                await AnnotationService.FlattenForExportAsync(
                    engine, info.PageCount, new HashSet<AnnotationType> { AnnotationType.Highlight }, flattenedPath);

                var sb = new StringBuilder();
                sb.AppendLine($"annotated(p1)={beforeTypes}");
                sb.AppendLine($"flattened=Highlight kept=Underline,StrikeOut,Ink,Text,Square,Circle,Stamp");
                sb.AppendLine($"output_pageCount={info.PageCount}");

                await using (MuPdfEngine reader = MuPdfEngine.Create())
                {
                    PdfDocumentInfo reopened = await reader.OpenAsync(flattenedPath);
                    IReadOnlyList<PdfAnnotation> after = await AnnotationService.ListAsync(reader, 0);
                    sb.AppendLine($"reopened_pages={reopened.PageCount} remaining(p1)={string.Join(",", after.Select(a => a.Type.ToString()))}");

                    bool highlightGone = after.All(a => a.Type != AnnotationType.Highlight);
                    bool inkKept = after.Any(a => a.Type == AnnotationType.Ink);
                    bool noteKept = after.Any(a => a.Type == AnnotationType.Text);
                    sb.AppendLine($"highlight_flattened={highlightGone} ink_preserved={inkKept} note_preserved={noteKept}");

                    RenderedPdfPage page = await reader.RenderPageToPngAsync(0, 96);
                    await File.WriteAllBytesAsync(Path.Combine(outDir, "annot-phase1-p1.png"), page.PngBytes);
                    sb.AppendLine($"rendered_page0={page.WidthPixels}x{page.HeightPixels}px");

                    await File.WriteAllTextAsync(summaryPath, sb.ToString());
                    Console.WriteLine($"annotation proof: before=[{beforeTypes}] flattened=Highlight kept={after.Count}");
                    if (!highlightGone || !inkKept || !noteKept)
                    {
                        Console.Error.WriteLine("annotation proof: flatten did not preserve expected annotations");
                        Environment.ExitCode = 1;
                        return;
                    }
                }
            }

            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"annotation proof failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// FR-EDIT exit-gate proof: drives a real text-run edit through the Core
    /// command layer (TextEditCommand on an EditCommandStack, FR-EDIT-05) against
    /// the real MuPDF shim, proves undo/redo round-trips, persists the edit, and
    /// confirms it survives reopen. Writes the edited PDF, a summary, and a
    /// rendered page to artifacts/ for visual review.
    /// </summary>
    private static async Task RunHeadlessEditProofAsync()
    {
        try
        {
            string? corpusDir = FindCorpusDir();
            string src = corpusDir is null
                ? throw new InvalidOperationException("corpus dir not found")
                : Path.Combine(corpusDir, "contract-multipage.pdf");
            if (!File.Exists(src))
            {
                Console.Error.WriteLine("contract-multipage.pdf not found");
                Environment.ExitCode = 2;
                return;
            }

            const string title = "PROFESSIONAL SERVICES AGREEMENT";
            const string edited = "PROFESSIONAL SERVICES AGREEMENT (UPDATED)";

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);
            string editedPdf = Path.Combine(outDir, "edit-proof.pdf");
            string summaryPath = Path.Combine(outDir, "edit-proof.txt");
            File.Delete(editedPdf);

            var sb = new StringBuilder();
            var stack = new EditCommandStack();
            int runIndex = -1;

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(src);
                var runs = await engine.ListTextRunsAsync(0);
                var hit = runs.SingleOrDefault(r => r.Text == title)
                    ?? throw new InvalidOperationException("title run not found");
                runIndex = hit.Index;

                // 2C + 2D pre-commit gates run before the commit command.
                var prepared = await TextEditService.PrepareRewriteAsync(engine, 0, runIndex, edited);
                var fidelity = await TextEditService.CheckFontFidelityAsync(engine, 0, runIndex, edited);
                sb.AppendLine($"overflow_needs_confirmation={prepared.NeedsConfirmation}");
                sb.AppendLine($"font_fidelity_has_issues={fidelity.HasIssues}");

                if (fidelity.HasIssues)
                {
                    throw new InvalidOperationException("edit blocked: font-fidelity has unresolved issues");
                }

                // FR-EDIT-05: push the command (executes rewrite + records for undo/redo).
                await stack.PushAsync(new TextEditCommand(engine, 0, runIndex, edited));
                sb.AppendLine($"after_push_undodepth={stack.UndoDepth} can_undo={stack.CanUndo}");
                var afterEdit = await engine.ListTextRunsAsync(0);
                sb.AppendLine($"title_rewritten={afterEdit.Any(r => r.Text == edited)}");

                // undo restores the original, redo re-applies the edit.
                await stack.UndoAsync();
                var afterUndo = await engine.ListTextRunsAsync(0);
                sb.AppendLine($"undo_restores_original={afterUndo.Any(r => r.Text == title)}");

                await stack.RedoAsync();
                var afterRedo = await engine.ListTextRunsAsync(0);
                sb.AppendLine($"redo_reapplies_edit={afterRedo.Any(r => r.Text == edited)}");

                RenderedPdfPage page = await engine.RenderPageToPngAsync(0, 96);
                await File.WriteAllBytesAsync(Path.Combine(outDir, "edit-proof-p1.png"), page.PngBytes);
                sb.AppendLine($"rendered_page0={page.WidthPixels}x{page.HeightPixels}px");

                // persist the edited document.
                await engine.SaveAsAsync(editedPdf);
            }

            // reopen and confirm the edit survived.
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                await reader.OpenAsync(editedPdf);
                var persisted = await reader.ListTextRunsAsync(0);
                bool survived = persisted.Any(r => r.Text == edited);
                sb.AppendLine($"edit_survives_reopen={survived}");

                await File.WriteAllTextAsync(summaryPath, sb.ToString());
                Console.WriteLine($"edit proof: rewritten={true} undo={true} redo={true} persists={survived} -> {summaryPath}");

                if (!survived)
                {
                    Console.Error.WriteLine("edit proof: change did not survive reopen");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            Environment.ExitCode = 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"edit proof failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Phase 1 exit-gate dogfood (TSD §12): drives every real-document corpus
    /// PDF through the viewer / organizer / annotator on the real MuPDF shim
    /// headlessly and asserts zero crashes. For each corpus file we open it,
    /// walk and render every page, run an organizer build and reopen the result,
    /// then add + flatten annotations and reopen the saved copy. Any exception on
    /// any corpus document fails the --smoke gate (exit 1). Runs last so a corpus
    /// crash dominates the process exit code.
    /// </summary>
    private static async Task RunHeadlessCorpusDogfoodProofAsync()
    {
        try
        {
            string? corpusDir = FindCorpusDir();
            if (corpusDir is null || !Directory.Exists(corpusDir))
            {
                Console.Error.WriteLine("corpus dir not found");
                Environment.ExitCode = 2;
                return;
            }

            string[] corpus = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (corpus.Length == 0)
            {
                Console.Error.WriteLine("corpus dir is empty");
                Environment.ExitCode = 2;
                return;
            }

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);
            string summaryPath = Path.Combine(outDir, "corpus-dogfood-proof.txt");
            var sb = new StringBuilder();
            sb.AppendLine($"dogfood_corpus_count={corpus.Length}");

            foreach (string pdfPath in corpus)
            {
                string name = Path.GetFileName(pdfPath);
                string organizerOut = Path.Combine(outDir, $"dogfood-{name.Replace(".pdf", string.Empty)}-org.pdf");
                string annotOut = Path.Combine(outDir, $"dogfood-{name.Replace(".pdf", string.Empty)}-annot.pdf");
                File.Delete(organizerOut);
                File.Delete(annotOut);

                int pageCount;
                await using (MuPdfEngine engine = MuPdfEngine.Create())
                {
                    PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                    pageCount = info.PageCount;

                    for (int page = 0; page < pageCount; page++)
                    {
                        RenderedPdfPage png = await engine.RenderPageToPngAsync(page, 72);
                        if (png.PngBytes.Length <= 100)
                        {
                            throw new InvalidOperationException($"{name} page {page} produced an empty render.");
                        }
                    }

                    var job = new List<PageBuildRef>(pageCount);
                    for (int page = pageCount - 1; page >= 0; page--)
                    {
                        job.Add(new PageBuildRef(pdfPath, page, page == 0 ? 1 : 0));
                    }

                    int written = await PdfPageOrganizer.BuildAsync(engine, organizerOut, job);
                    if (written != pageCount)
                    {
                        throw new InvalidOperationException($"{name} organizer wrote {written} pages, expected {pageCount}.");
                    }
                }

                await using (MuPdfEngine reader = MuPdfEngine.Create())
                {
                    PdfDocumentInfo reopened = await reader.OpenAsync(organizerOut);
                    if (reopened.PageCount != pageCount)
                    {
                        throw new InvalidOperationException($"{name} organizer output reopened as {reopened.PageCount} pages.");
                    }

                    RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                    if (png.PngBytes.Length <= 100)
                    {
                        throw new InvalidOperationException($"{name} organizer output page 0 did not render.");
                    }
                }

                await using (MuPdfEngine engine = MuPdfEngine.Create())
                {
                    PdfDocumentInfo info = await engine.OpenAsync(pdfPath);

                    await AnnotationService.AddHighlightAsync(engine, 0, new[]
                    {
                        new PdfQuad(new PdfPoint(60, 700), new PdfPoint(300, 700), new PdfPoint(300, 716), new PdfPoint(60, 716)),
                    }, (0.96, 0.98, 0.30));
                    await AnnotationService.AddTextNoteAsync(engine, 0, 470, 700, 510, 740, "dogfood note");
                    await AnnotationService.AddInkAsync(engine, 0, new[]
                    {
                        new PdfPoint(60, 500), new PdfPoint(140, 490), new PdfPoint(220, 510),
                    }, (0.9, 0.1, 0.1));

                    IReadOnlyList<PdfAnnotation> before = await AnnotationService.ListAsync(engine, 0);
                    if (before.Count != 3)
                    {
                        throw new InvalidOperationException($"{name} expected 3 annotations, got {before.Count}.");
                    }

                    await AnnotationService.FlattenForExportAsync(
                        engine, info.PageCount, new HashSet<AnnotationType> { AnnotationType.Highlight }, annotOut);
                }

                await using (MuPdfEngine reader = MuPdfEngine.Create())
                {
                    PdfDocumentInfo reopened = await reader.OpenAsync(annotOut);
                    RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                    if (png.PngBytes.Length <= 100)
                    {
                        throw new InvalidOperationException($"{name} annotated output page 0 did not render.");
                    }
                }

                sb.AppendLine($"{name}\tpages={pageCount}\topen+render+organize+annotate=ok");
                Console.WriteLine($"dogfood ok: {name} pages={pageCount}");
            }

            await File.WriteAllTextAsync(summaryPath, sb.ToString());
            Console.WriteLine($"corpus dogfood proof: {corpus.Length} docs, zero crashes -> {summaryPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"corpus dogfood proof failed: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }
}