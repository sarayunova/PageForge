// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Windows.Storage.Streams;

namespace PageForge.App;

/// <summary>
/// Phase 0 launch window: renders page 1 of the bundled sample document through
/// the real IPdfEngine (MuPDF shim) and shows it in an Image. No business logic
/// lives here; this only proves the App -> Interop -> Core -> shim -> MuPDF path.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherQueue _dispatcher;

    public MainWindow()
    {
        InitializeComponent();
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _ = LoadSampleAsync();
    }

    private async Task LoadSampleAsync()
    {
        string? pdfPath = FindSamplePdf();
        if (pdfPath is null)
        {
            StatusText.Text = "sample document not found (run tools/generate-sample-pdf.ps1).";
            return;
        }

        try
        {
            var render = await Task.Run(async () =>
            {
                await using MuPdfEngine engine = MuPdfEngine.Create();
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                PdfPageRegion size = await engine.GetPageSizeAsync(0);
                RenderedPdfPage page = await engine.RenderPageToPngAsync(0, 96);
                return (Page: page, Size: size, Info: info);
            });

            if (render.Info.PageCount == 0)
            {
                StatusText.Text = "document has no pages.";
                return;
            }

            var bitmap = new BitmapImage();
            _dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    using var stream = new InMemoryRandomAccessStream();
                    await stream.WriteAsync(render.Page.PngBytes.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                    PreviewImage.Source = bitmap;
                    StatusText.Text = $"{render.Info.DisplayName} ({render.Info.PageCount} pages)";
                    SizeText.Text = $"rendered {render.Page.WidthPixels}x{render.Page.HeightPixels} px at 96 DPI";
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"render failed: {ex.Message}";
                }
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"render failed: {ex.Message}";
        }
    }

    /// <summary>Opens the public source repository, satisfying the AGPL §13 source-availability
    /// obligation for the desktop client (TSD §7). Reads the same PAGEFORGE_REPO_URL used by
    /// the hosted /source endpoint so the two stay in sync.</summary>
    private async void ViewSource_Click(object sender, RoutedEventArgs e)
    {
        string repoUrl = Environment.GetEnvironmentVariable("PAGEFORGE_REPO_URL")
            ?? "https://github.com/pageforge/pageforge";
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(repoUrl));
        }
        catch (Exception ex)
        {
            StatusText.Text = $"could not open source repository: {ex.Message}";
        }
    }

    private static string? FindSamplePdf()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PageForge.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            return null;
        }

        string candidate = Path.Combine(dir, "tools", "sample-pdf", "sample-phase0.pdf");
        return File.Exists(candidate) ? candidate : null;
    }
}