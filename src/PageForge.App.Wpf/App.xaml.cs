// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.Logging;
using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using PageForge.Core.View;
using PageForge.MuPdfInterop;
using PageForge.App.Wpf.Views;

namespace PageForge.App.Wpf;

public partial class App : Application
{
    public static bool SmokeMode =>
        Environment.GetCommandLineArgs().Contains("--smoke", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The theme to start in, from <c>--theme light|dark|system</c>; defaults to
    /// following the OS.
    ///
    /// This exists so the light palette can be inspected without changing the
    /// machine's Windows setting. Reviewing it used to mean editing the OS
    /// personalisation registry and putting it back afterwards, which is invasive
    /// and easy to forget, so in practice light theme went unexercised - which is
    /// how nine hardcoded dark-theme colours survived in the form and redaction
    /// panels until someone thought to look.
    /// </summary>
    public static Themes.AppTheme RequestedTheme
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.FindIndex(
                args, a => a.Equals("--theme", StringComparison.OrdinalIgnoreCase));

            string? value = index >= 0 && index + 1 < args.Length ? args[index + 1] : null;

            return value?.ToLowerInvariant() switch
            {
                "light" => Themes.AppTheme.Light,
                "dark" => Themes.AppTheme.Dark,
                _ => Themes.AppTheme.System,
            };
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Before anything that can fail, so a startup failure has a trail.
        Diagnostics.AppLog.Initialize();
        Diagnostics.AppLog.For(typeof(App)).LogInformation(
            "PageForge starting. Version {Version}, smoke mode {SmokeMode}.",
            typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown",
            SmokeMode);

        // Before any window is shown, so nothing renders with the wrong palette.
        // Skipped in smoke mode: the headless proofs draw no chrome, and following
        // the OS theme there would make the run depend on the machine's settings.
        if (!SmokeMode)
        {
            Themes.ThemeManager.Initialize(RequestedTheme);
        }

        if (SmokeMode)
        {
            await RunHeadlessProofAsync();
            await RunHeadlessViewerProofAsync();
            await RunHeadlessSlotRenderProofAsync();
            await RunHeadlessOrganizerProofAsync();
            await RunHeadlessAnnotationProofAsync();
            await RunHeadlessEditProofAsync();
            await RunHeadlessCorpusDogfoodProofAsync();
            RunThemeTokenProof();
            RunRedactGeometryProof();
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// Proves that chrome built in code follows the theme (see
    /// <see cref="Views.ThemedElements"/>).
    ///
    /// This is asserted rather than eyeballed because eyeballing it is what failed:
    /// nine hardcoded dark-theme colours survived in the form and redaction panels
    /// through a whole phase that was meant to remove them, and screenshots did not
    /// catch them - the sample document has no form fields, so the code paths that
    /// build those panels barely run, and the empty-state labels that do render sit
    /// behind the page surface.
    ///
    /// A literal brush would pass a "is it the right colour in dark theme" check and
    /// still be wrong in light. So the assertion is the one that actually matters:
    /// the same element must resolve to DIFFERENT brushes under the two themes, and
    /// to exactly the token's brush in each. A SetResourceReference that failed to
    /// resolve falls back to the property default and would be identical in both.
    /// </summary>
    private static void RunThemeTokenProof()
    {
        try
        {
            // The probe has to sit in a real tree. A resource reference resolves by
            // walking up the logical tree to the application's resources, so a
            // TextBlock with no parent falls back to the property default (black)
            // and would report a false failure - the panels this is standing in for
            // add their labels to a StackPanel inside a window.
            var probe = new System.Windows.Controls.TextBlock()
                .Themed(System.Windows.Controls.TextBlock.ForegroundProperty, "ContentMutedBrush");
            var host = new Window { Content = probe };

            Themes.ThemeManager.Apply(Themes.AppTheme.Light);
            var light = probe.Foreground as System.Windows.Media.SolidColorBrush;

            Themes.ThemeManager.Apply(Themes.AppTheme.Dark);
            var dark = probe.Foreground as System.Windows.Media.SolidColorBrush;

            if (light is null || dark is null)
            {
                Trace("theme-token proof: ContentMutedBrush did not resolve to a brush.");
                FailProof();
                return;
            }

            if (light.Color == dark.Color)
            {
                Trace($"theme-token proof: the brush did not follow the theme " +
                      $"(light and dark both {light.Color}).");
                FailProof();
                return;
            }

            Trace($"theme-token proof: ContentMutedBrush light={light.Color} dark={dark.Color}");
            host.Close();
        }
        catch (Exception exception)
        {
            Trace($"theme-token proof failed: {exception}");
            FailProof(2);
        }
    }

    /// <summary>
    /// Proves the PDF-to-screen conversion behind the redaction overlay.
    ///
    /// This is the part of U4 part two most likely to break and least likely to be
    /// noticed: a wrong Y flip puts the box on the other half of the page, which a
    /// passing test suite would never mention because nothing else asserts where a
    /// region lands. It was confirmed once by screenshot; a screenshot does not run
    /// in CI.
    ///
    /// A 200x400pt page with a region from (10,300) to (60,350), rendered at 144 DPI
    /// (2x), so every expected number is checkable by hand: scale 2, left 20, width
    /// 100, height 100, and a top of 800 - 350*2 = 100 measured down from the top.
    /// </summary>
    private static void RunRedactGeometryProof()
    {
        try
        {
            const double dpi = 144.0;
            const double pageHeightPx = 400.0 * dpi / 72.0; // 800
            var region = new ViewModels.RedactRegionViewModel(
                new PageForge.Core.Pdf.PdfRect(10, 300, 60, 350), dpi, pageHeightPx);

            (string name, double actual, double expected)[] checks =
            [
                ("Left", region.Left, 20),
                ("Top", region.Top, 100),
                ("Width", region.Width, 100),
                ("Height", region.Height, 100),
            ];

            foreach ((string name, double actual, double expected) in checks)
            {
                if (Math.Abs(actual - expected) > 0.001)
                {
                    Trace($"redact-geometry proof: {name} was {actual}, expected {expected}.");
                    FailProof();
                    return;
                }
            }

            Trace("redact-geometry proof: region maps to (20,100) 100x100 at 144 DPI.");
        }
        catch (Exception exception)
        {
            Trace($"redact-geometry proof failed: {exception}");
            FailProof(2);
        }
    }

    private static void Trace(string message) =>
        Diagnostics.AppLog.For(typeof(App)).LogInformation("{Message}", message);

    protected override void OnExit(ExitEventArgs e)
    {
        // Flush and release the log file before the process goes away.
        Diagnostics.AppLog.Shutdown();
        base.OnExit(e);
    }

    /// <summary>
    /// Records a failed proof without ever clearing a failure recorded earlier.
    ///
    /// Each proof used to assign <see cref="Environment.ExitCode"/> directly, including
    /// assigning 0 on success. A later passing proof therefore overwrote an earlier
    /// failure and --smoke could exit 0 with proofs broken. The corpus dogfood proof
    /// carried a comment saying it ran last so a crash would "dominate the process exit
    /// code" - a workaround for this masking rather than a fix for it.
    ///
    /// The first failure now wins and sticks. Success assigns nothing, because the
    /// default exit code is already 0.
    /// </summary>
    /// <param name="code">2 for a precondition/setup failure, 1 for a failed proof.</param>
    private static void FailProof(int code = 1)
    {
        if (Environment.ExitCode == 0)
        {
            Environment.ExitCode = code;
        }
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
                FailProof(2);
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
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"render failed: {ex.Message}");
            FailProof();
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
                FailProof(2);
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

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"viewer proof failed: {ex.Message}");
            FailProof();
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
    /// FR-VIEW-01 regression proof for the two defects that made an opened PDF show
    /// nothing on screen:
    ///
    /// 1. The RenderOnLoad behavior matched the realized element's DataContext against
    ///    PageImageViewModel, but the page and thumbnail templates bind through a
    ///    PageSlotViewModel. The match never succeeded, RenderAsync was never called,
    ///    and every page stayed blank â€” silently, because it was a bare `if`. This
    ///    proof asserts the resolution for both surfaces (page and thumbnail).
    ///
    /// 2. A zoom change retargets each slot's DPI, but nothing re-rendered slots whose
    ///    containers were already realized (Loaded does not fire twice). This proof
    ///    asserts the DPI change marks the cache stale while keeping the old bitmap
    ///    visible, and that re-rendering yields a genuinely higher-resolution bitmap.
    /// </summary>
    private static async Task RunHeadlessSlotRenderProofAsync()
    {
        try
        {
            string? pdfPath = FindSamplePdf();
            if (pdfPath is null)
            {
                Console.Error.WriteLine("sample document not found");
                FailProof(2);
                return;
            }

            var sb = new StringBuilder();
            await using (DocumentViewModel core = new(MuPdfEngine.Create()))
            {
                var tab = new ViewModels.DocumentTabViewModel(core);
                await tab.InitializeAsync(pdfPath);

                if (tab.Pages.Count == 0)
                {
                    Console.Error.WriteLine("slot render proof failed: no page slots built");
                    FailProof();
                    return;
                }

                ViewModels.PageSlotViewModel slot = tab.Pages[0];

                // (1) The behavior must resolve a PageSlotViewModel DataContext to the
                // right image for each surface. Returning null here is the original
                // blank-viewer bug.
                var pageImage = new System.Windows.Controls.Image { DataContext = slot };
                pageImage.SetBinding(
                    System.Windows.Controls.Image.SourceProperty,
                    new System.Windows.Data.Binding("Image.Bitmap"));

                var thumbImage = new System.Windows.Controls.Image { DataContext = slot };
                thumbImage.SetBinding(
                    System.Windows.Controls.Image.SourceProperty,
                    new System.Windows.Data.Binding("Thumbnail.Bitmap"));

                bool pageResolves = ReferenceEquals(ViewModels.PageImageBehavior.ResolveImage(pageImage), slot.Image);
                bool thumbResolves = ReferenceEquals(ViewModels.PageImageBehavior.ResolveImage(thumbImage), slot.Thumbnail);

                sb.AppendLine($"resolve page slot -> Image: {pageResolves}");
                sb.AppendLine($"resolve thumb slot -> Thumbnail: {thumbResolves}");

                // (2) Render at 1x, then zoom and prove the stale cache is both kept
                // and replaced by a higher-resolution render.
                await slot.Image.RenderAsync();
                bool rendered = slot.Image.Bitmap is not null;
                int baseWidth = slot.Image.Bitmap?.PixelWidth ?? 0;
                sb.AppendLine($"render @{slot.Image.RenderDpi:0} dpi -> loaded={rendered} width={baseWidth}px");

                System.Windows.Media.Imaging.BitmapSource? before = slot.Image.Bitmap;
                slot.Image.RenderDpi *= 2.0;

                bool staleKeepsPixels = slot.Image.IsStale && ReferenceEquals(slot.Image.Bitmap, before);
                sb.AppendLine($"after zoom: stale={slot.Image.IsStale} keptOldBitmap={ReferenceEquals(slot.Image.Bitmap, before)}");

                await tab.RenderSlotsAsync(new[] { slot });
                int zoomedWidth = slot.Image.Bitmap?.PixelWidth ?? 0;
                bool rerendered = zoomedWidth > baseWidth && !slot.Image.IsStale;
                sb.AppendLine($"re-render @{slot.Image.RenderDpi:0} dpi -> width={zoomedWidth}px stale={slot.Image.IsStale}");

                if (!pageResolves || !thumbResolves || !rendered || !staleKeepsPixels || !rerendered)
                {
                    Console.Error.WriteLine(
                        "slot render proof failed: " +
                        $"pageResolves={pageResolves} thumbResolves={thumbResolves} rendered={rendered} " +
                        $"staleKeepsPixels={staleKeepsPixels} rerendered={rerendered}");
                    FailProof();
                }

                string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
                Directory.CreateDirectory(outDir);
                string outPath = Path.Combine(outDir, "slot-render-proof.txt");
                await File.WriteAllTextAsync(outPath, sb.ToString());

                Console.WriteLine(
                    $"slot render proof: resolve={pageResolves && thumbResolves} " +
                    $"render={baseWidth}px zoom={zoomedWidth}px -> {outPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"slot render proof failed: {ex.Message}");
            FailProof();
        }
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
                FailProof(2);
                return;
            }

            string outDir = Path.GetFullPath(Path.Combine(FindRepoRoot() ?? string.Empty, "artifacts"));
            Directory.CreateDirectory(outDir);
            string builtPath = Path.Combine(outDir, "organizer-rotated-merged.pdf");
            string summaryPath = Path.Combine(outDir, "organizer-proof.txt");

            // Rotate page 0 by 90Â° then append a full unmodified copy => 6 pages,
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

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"organizer proof failed: {ex.Message}");
            FailProof();
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
                FailProof(2);
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
                        FailProof();
                        return;
                    }
                }
            }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"annotation proof failed: {ex.Message}");
            FailProof();
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
                FailProof(2);
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
                    FailProof();
                    return;
                }
            }

        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"edit proof failed: {ex.Message}");
            FailProof();
        }
    }

    /// <summary>
    /// Phase 1 exit-gate dogfood (TSD Â§12): drives every real-document corpus
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
                FailProof(2);
                return;
            }

            string[] corpus = Directory.GetFiles(corpusDir, "*.pdf", SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.Ordinal).ToArray();
            if (corpus.Length == 0)
            {
                Console.Error.WriteLine("corpus dir is empty");
                FailProof(2);
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
            FailProof();
        }
    }
}