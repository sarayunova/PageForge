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
            // Nothing but the explicit Shutdown() below may end this run.
            //
            // The default is OnLastWindowClose, and RunThemeTokenProof creates a
            // probe Window and closes it - which counts as the last window closing
            // and starts tearing the application down. Everything after it was
            // then racing a dispatcher that was going away: the short synchronous
            // proofs finished in time and the first genuinely long asynchronous
            // one did not, stopping mid-await with a clean exit code 0 and no
            // error anywhere. That looked exactly like a hang in the engine call
            // it happened to stop on.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            await RunHeadlessProofAsync();
            await RunHeadlessViewerProofAsync();
            await RunHeadlessSlotRenderProofAsync();
            await RunHeadlessOrganizerProofAsync();
            await RunHeadlessAnnotationProofAsync();
            await RunHeadlessEditProofAsync();
            await RunHeadlessCorpusDogfoodProofAsync();
            RunThemeTokenProof();
            RunRedactGeometryProof();
            RunObjectGeometryProof();
            await RunFormGeometryProofAsync();
            await RunRecoveryBufferProofAsync();
            await RunSigningProofAsync();
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

    /// <summary>
    /// Proves the object-edit overlay's geometry: that a box lands where the PDF
    /// says, that it follows its bounds when a drag changes them, and that the
    /// eight selection handles sit on that box's corners and edge midpoints.
    ///
    /// The object overlay had no cover of any kind, which is how it reached this
    /// point with the surface blank, a mirrored drag axis and a bbox three orders
    /// of magnitude too large. The handles are included because they are the part
    /// with no equivalent on the other two surfaces: nothing else would notice
    /// them drifting away from the box they belong to.
    ///
    /// Numbers chosen so each is checkable by hand. A 200x400pt page at 144 DPI is
    /// 800px tall; an object at (10,300)-(60,350) is 50x50pt, so 100x100px at
    /// left 20, and its top is 800 - 350*2 = 100 measured down from the page top.
    /// The bounds are asymmetric in both axes, so a dropped flip, a doubled flip
    /// or a transposed pair all move the answer.
    /// </summary>
    private static void RunObjectGeometryProof()
    {
        try
        {
            const double dpi = 144.0;
            const double pageHeightPx = 400.0 * dpi / 72.0; // 800
            var obj = new PageForge.Core.Pdf.PdfPageObject(
                PageForge.Core.Pdf.PageObjectKind.Image,
                "0",
                new PageForge.Core.Pdf.PdfRect(10, 300, 60, 350));
            var box = new ViewModels.ObjectBoxViewModel(obj, dpi, pageHeightPx);

            if (!Check("Left", box.Left, 20) ||
                !Check("Top", box.Top, 100) ||
                !Check("Width", box.Width, 100) ||
                !Check("Height", box.Height, 100))
            {
                return;
            }

            // A drag moves the object 10pt UP the page. PDF Y grows up and screen Y
            // grows down, so Top must DECREASE by 20px. Getting this backwards is
            // precisely the defect that shipped in the mouse-drag handler, where a
            // comment stated the rule and the line below it did not apply it.
            box.Bounds = new PageForge.Core.Pdf.PdfRect(10, 310, 60, 360);
            if (!Check("Top after moving 10pt up the page", box.Top, 80))
            {
                return;
            }

            // The handles must track the box. Corners and edge midpoints of
            // (20,80) 100x100: the south-east handle is the far corner.
            if (!Check("SE handle X", box.Left + box.Width, 120) ||
                !Check("SE handle Y", box.Top + box.Height, 180) ||
                !Check("N handle X", box.Left + (box.Width / 2), 70))
            {
                return;
            }

            // Hit-testing must agree with what is drawn - the click target and the
            // rectangle are no longer two separately maintained rectangles, and
            // this is the assertion that says so.
            if (!box.Contains(70, 130))
            {
                Trace("object-geometry proof: the box does not contain its own centre (70,130).");
                FailProof();
                return;
            }

            if (box.Contains(70, 60))
            {
                Trace("object-geometry proof: the box contains a point above its top edge.");
                FailProof();
                return;
            }

            Trace("object-geometry proof: box maps to (20,100) 100x100 at 144 DPI, " +
                  "rises to top 80 when moved 10pt up, and its handles and hit-test follow it.");
        }
        catch (Exception exception)
        {
            Trace($"object-geometry proof failed: {exception}");
            FailProof(2);
        }

        static bool Check(string name, double actual, double expected)
        {
            if (Math.Abs(actual - expected) <= 0.001)
            {
                return true;
            }

            Trace($"object-geometry proof: {name} was {actual}, expected {expected}.");
            FailProof();
            return false;
        }
    }

    /// <summary>
    /// Proves the form-field overlay's geometry, and specifically that it does
    /// NOT flip Y.
    ///
    /// The form overlay is the one surface whose coordinates do not match the
    /// others. Redaction regions and object boxes arrive in PDF user space,
    /// bottom-left origin, and genuinely need flipping; form-field bounds arrive
    /// top-down and must not be. Getting that wrong drew every outline on the
    /// opposite half of the page, and it went unnoticed because the surface
    /// underneath was blank at the time.
    ///
    /// The obvious regression is someone tidying <see cref="ViewModels.FormFieldBoxViewModel"/>
    /// into <see cref="ViewModels.PageBoxGeometry"/> because the other two
    /// overlays use it. That would look like removing duplication and would
    /// reintroduce the bug, so this proof is written to fail loudly if it
    /// happens - it asserts the un-flipped value AND names what the flipped one
    /// would have been.
    ///
    /// Unlike the redaction and object proofs, this one runs against the real
    /// engine and the real fixture rather than a synthetic rectangle. The
    /// convention is the ENGINE's to define: a synthetic test would only encode
    /// what this code believes, and would still pass if the engine started
    /// reporting bounds bottom-up while the UI broke.
    /// </summary>
    private static async Task RunFormGeometryProofAsync()
    {
        try
        {
            string? corpusDir = FindCorpusDir();
            string fixture = Path.Combine(corpusDir ?? string.Empty, "form-application.pdf");
            if (!File.Exists(fixture))
            {
                Trace($"form-geometry proof: fixture not found at {fixture}.");
                FailProof(2);
                return;
            }

            const double dpi = 96.0;
            const double scale = dpi / 72.0;

            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(fixture);
            PdfPageRegion size = await engine.GetPageSizeAsync(0);
            IReadOnlyList<PageForge.Core.Pdf.PdfFormField> fields = await engine.ListFormFieldsAsync(0);

            if (fields.Count == 0)
            {
                // The fixture is the whole point of this proof; an empty list
                // means the form path is broken, not that there is nothing to
                // check. Silently passing here is how the old mode-panel test
                // passed against an unrendered page.
                Trace("form-geometry proof: the fixture reported no form fields at all.");
                FailProof();
                return;
            }

            double pageHeightPx = size.HeightPt * scale;

            foreach (PageForge.Core.Pdf.PdfFormField field in fields)
            {
                var box = new ViewModels.FormFieldBoxViewModel(field, dpi);

                double expectedTop = field.Bounds.Y0 * scale;
                double flippedTop = pageHeightPx - (field.Bounds.Y1 * scale);

                if (Math.Abs(box.Top - expectedTop) > 0.001)
                {
                    Trace($"form-geometry proof: '{field.Label}' Top was {box.Top:F2}, " +
                          $"expected {expectedTop:F2} (bounds are top-down, so Top is Y0 scaled). " +
                          $"The flipped value would be {flippedTop:F2} - if that is what it is, " +
                          "the overlay has been switched to PageBoxGeometry, which is for the " +
                          "bottom-up surfaces only.");
                    FailProof();
                    return;
                }

                if (Math.Abs(box.Left - (field.Bounds.X0 * scale)) > 0.001)
                {
                    Trace($"form-geometry proof: '{field.Label}' Left was {box.Left:F2}, " +
                          $"expected {field.Bounds.X0 * scale:F2}.");
                    FailProof();
                    return;
                }

                if (box.Width <= 0 || box.Height <= 0)
                {
                    Trace($"form-geometry proof: '{field.Label}' has no area " +
                          $"({box.Width:F2}x{box.Height:F2}); an outline with no size draws nothing.");
                    FailProof();
                    return;
                }

                // Every outline must land on the page. A flip applied to top-down
                // bounds does not always leave the page, so this is a weaker check
                // than the one above - but it catches a wrong scale, which that
                // one does not.
                if (box.Top < 0 || box.Top + box.Height > pageHeightPx + 1)
                {
                    Trace($"form-geometry proof: '{field.Label}' sits outside the page " +
                          $"(top {box.Top:F2}, height {box.Height:F2}, page {pageHeightPx:F2}px).");
                    FailProof();
                    return;
                }
            }

            string measured = string.Join(", ", fields.Select(f =>
                $"{f.Label}@{new ViewModels.FormFieldBoxViewModel(f, dpi).Top:F0}px"));
            Trace($"form-geometry proof: {fields.Count} field outline(s) read top-down, " +
                  $"un-flipped, on the page at {dpi} DPI - {measured}.");
        }
        catch (Exception exception)
        {
            Trace($"form-geometry proof failed: {exception}");
            FailProof(2);
        }
    }

    /// <summary>
    /// Proves the recovery buffer (TRD §6) does the three things it is trusted
    /// for, and does not do the one thing that would make it harmful.
    ///
    /// An edited document must be copied aside; a folder left by a dead instance
    /// must be found; and clearing must remove it. The fourth, and the reason the
    /// lock file exists at all: a folder belonging to a LIVE instance must not be
    /// offered as crash leftovers. Getting that wrong would let one window hand
    /// another window's in-progress work back as though the session had died -
    /// and it is invisible in any single-instance test, which is exactly the kind
    /// of gap this project keeps finding after the fact.
    /// </summary>
    /// <summary>
    /// FR-SEC-03 through the shell's own path: the view model signs a copy and
    /// then reads the verdict back. The fidelity suite proves the cryptography
    /// against the engine; what this proves is that the command a user presses
    /// reaches it — the gap that let the CHANGELOG claim signing for a release
    /// in which no one could sign anything.
    ///
    /// The certificate is generated here and thrown away. A self-signed one is
    /// the right instrument as well as the convenient one: it must verify as
    /// intact and as NOT trusted, and a verifier that cannot tell those apart
    /// fails this proof.
    /// </summary>
    private static async Task RunSigningProofAsync()
    {
        string? fixture = FindCorpusDir() is { } dir
            ? Path.Combine(dir, "contract-multipage.pdf")
            : null;
        if (fixture is null || !File.Exists(fixture))
        {
            Trace("signing proof: corpus fixture not found.");
            FailProof(2);
            return;
        }

        string certificate = Path.Combine(Path.GetTempPath(), $"pageforge-smoke-{Guid.NewGuid():N}.pfx");
        string signed = Path.Combine(Path.GetTempPath(), $"pageforge-smoke-signed-{Guid.NewGuid():N}.pdf");
        const string password = "smoke";

        try
        {
            using (var key = System.Security.Cryptography.RSA.Create(2048))
            {
                var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                    "CN=PageForge Smoke Signer, O=LiVi Software Company",
                    key,
                    System.Security.Cryptography.HashAlgorithmName.SHA256,
                    System.Security.Cryptography.RSASignaturePadding.Pkcs1);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                using System.Security.Cryptography.X509Certificates.X509Certificate2 cert =
                    request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));
                await File.WriteAllBytesAsync(
                    certificate,
                    cert.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, password));
            }

            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(fixture);

            await PdfSigningService.SignAsync(
                engine,
                0,
                new PdfSignatureRequest(
                    "SmokeSignature1",
                    Views.SignDialog.DefaultBounds,
                    certificate,
                    password,
                    Reason: "Smoke proof"),
                signed);

            if (!File.Exists(signed))
            {
                Trace("signing proof: the signed copy was not written.");
                FailProof();
                return;
            }

            await using MuPdfEngine verifier = MuPdfEngine.Create();
            await verifier.OpenAsync(signed);
            IReadOnlyList<PdfSignature> signatures = await PdfSigningService.ListAsync(verifier);

            if (signatures.Count != 1)
            {
                Trace($"signing proof: expected one signature in the signed copy, found {signatures.Count}.");
                FailProof();
                return;
            }

            PdfSignature signature = signatures[0];
            if (!signature.IsSigned || !signature.IsDigestIntact)
            {
                Trace($"signing proof: the just-signed document does not verify " +
                      $"(signed={signature.IsSigned}, digest='{signature.DigestStatus}'). " +
                      "Nothing changed after signing, so the digest covers the wrong bytes.");
                FailProof();
                return;
            }

            if (signature.IsTrusted)
            {
                Trace("signing proof: an untrusted self-signed certificate reported as trusted, " +
                      "so the chain is not being validated and no certificate would be rejected.");
                FailProof();
                return;
            }

            Trace("signing proof: the shell's signing path writes a signed copy, the signature " +
                  "verifies as intact, and its untrusted certificate is reported as untrusted.");
        }
        catch (Exception ex)
        {
            Trace($"signing proof: threw {ex.GetType().Name}: {ex.Message}");
            FailProof();
        }
        finally
        {
            TryDeleteProofFile(certificate);
            TryDeleteProofFile(signed);
        }
    }

    private static void TryDeleteProofFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static async Task RunRecoveryBufferProofAsync()
    {
        string? fixture = FindCorpusDir() is { } dir
            ? Path.Combine(dir, "contract-multipage.pdf")
            : null;
        if (fixture is null || !File.Exists(fixture))
        {
            Trace("recovery-buffer proof: corpus fixture not found.");
            FailProof(2);
            return;
        }

        string abandoned = Path.Combine(
            Diagnostics.RecoverySession.Root, "smoke-abandoned-" + Guid.NewGuid().ToString("N"));

        using var live = Diagnostics.RecoverySession.Start();
        try
        {
            if (!live.IsActive)
            {
                Trace("recovery-buffer proof: the session did not start.");
                FailProof();
                return;
            }

            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(fixture);

            // Unedited: nothing should be written, or an idle session would churn.
            await live.AutosaveAsync("probe", engine, fixture, "probe.pdf");
            // File.Exists rather than a ".lock" search pattern: Windows wildcard
            // matching on a leading-dot name is not dependable, and a lookup that
            // silently matches nothing here would fail far from its cause.
            string[] candidates = Directory.GetDirectories(Diagnostics.RecoverySession.Root)
                .Where(IsThisSession)
                .ToArray();
            if (candidates.Length != 1)
            {
                Trace($"recovery-buffer proof: expected exactly one folder locked by this " +
                      $"process, found {candidates.Length} under {Diagnostics.RecoverySession.Root}.");
                FailProof();
                return;
            }

            string liveFolder = candidates[0];
            if (Directory.GetFiles(liveFolder, "*.pdf").Length != 0)
            {
                Trace("recovery-buffer proof: an unedited document was autosaved.");
                FailProof();
                return;
            }

            bool probe = await engine.HasUnsavedChangesAsync();

            await engine.AddAnnotationAsync(
                0,
                new AnnotBuildSpec { Type = AnnotationType.Text, X0 = 72, Y0 = 700, X1 = 200, Y1 = 720 });
            await live.AutosaveAsync("probe", engine, fixture, "probe.pdf");

            if (!File.Exists(Path.Combine(liveFolder, "probe.pdf")) ||
                !File.Exists(Path.Combine(liveFolder, "probe.json")))
            {
                Trace("recovery-buffer proof: an edited document was not copied aside.");
                FailProof();
                return;
            }

            // The live folder must not look like crash leftovers to anyone else.
            if (Diagnostics.RecoverySession.FindRecoverable()
                .Any(d => d.RecoveredFile.StartsWith(liveFolder, StringComparison.OrdinalIgnoreCase)))
            {
                Trace("recovery-buffer proof: a LIVE session's documents were offered as " +
                      "recoverable. The lock is not doing its job, and a second window " +
                      "would hand back work that is still being edited.");
                FailProof();
                return;
            }

            // A folder with no held lock is what a crashed instance leaves.
            Directory.CreateDirectory(abandoned);
            File.Copy(Path.Combine(liveFolder, "probe.pdf"), Path.Combine(abandoned, "gone.pdf"));
            File.Copy(Path.Combine(liveFolder, "probe.json"), Path.Combine(abandoned, "gone.json"));

            if (!Diagnostics.RecoverySession.FindRecoverable()
                .Any(d => d.RecoveredFile.StartsWith(abandoned, StringComparison.OrdinalIgnoreCase)))
            {
                Trace("recovery-buffer proof: a crashed instance's document was NOT offered " +
                      "back, so the buffer would silently lose the work it exists to keep.");
                FailProof();
                return;
            }

            Diagnostics.RecoverySession.ClearAbandoned();
            if (Directory.Exists(abandoned))
            {
                Trace("recovery-buffer proof: clearing left the abandoned folder behind, so the " +
                      "same documents would be offered again after every future crash.");
                FailProof();
                return;
            }

            Trace("recovery-buffer proof: idle writes nothing, an edit is copied aside, a live " +
                  "session is not offered as leftovers, a dead one is, and clearing removes it.");
        }
        catch (Exception exception)
        {
            Trace($"recovery-buffer proof failed: {exception}");
            FailProof(2);
        }
        finally
        {
            try
            {
                if (Directory.Exists(abandoned))
                {
                    Directory.Delete(abandoned, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best effort; the proof's verdict is already recorded.
            }
        }

        // The folder this process holds open, told apart from any other live
        // instance's by trying to lock it - ours is the one we cannot take.
        static bool IsThisSession(string folder)
        {
            // The lock must EXIST before failing to open it means anything. A
            // missing file throws FileNotFoundException, which is an IOException,
            // so catching IOException alone reported every lock-less folder as
            // this process's own - including the abandoned one this proof creates.
            string lockPath = Path.Combine(folder, ".lock");
            if (!File.Exists(lockPath))
            {
                return false;
            }

            try
            {
                using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return false;
            }
            catch (IOException)
            {
                return true;
            }
        }
    }

    private static void Trace(string message) =>
        Diagnostics.AppLog.For(typeof(App)).LogInformation("{Message}", message);

    protected override void OnExit(ExitEventArgs e)
    {
        // Release the recovery buffer first. Reaching this line is precisely what
        // distinguishes a clean exit from a crash: the folder is removed here, so
        // anything still on disk at the next start is work the user did not get
        // to keep (TRD §6).
        if (MainWindow is MainWindow shell)
        {
            shell.DisposeRecovery();
        }

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