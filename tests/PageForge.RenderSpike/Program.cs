// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Diagnostics;
using PageForge.MuPdfInterop;

namespace PageForge.RenderSpike;

/// <summary>
/// Artifact-producing render harness used by the /rendercheck opencode command
/// (see PageForge_Artifact_Verification_Playbook.md, layer 1).
///
/// Usage:
///   dotnet run --project tests/PageForge.RenderSpike -- <pdf> [--page N] [--dpi N] [--out DIR] [--mutool-path mutool.exe]
///
/// Renders the requested pages through the real IPdfEngine (shim P/Invoke) and,
/// when a mutool executable is supplied, writes a mutool reference PNG alongside
/// so the agent can diff them by eye.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            string pdfPath = args.Length > 0 ? args[0] : string.Empty;
            if (string.IsNullOrEmpty(pdfPath) || !File.Exists(pdfPath))
            {
                Console.Error.WriteLine("usage: PageForge.RenderSpike <pdf> [--page N] [--dpi N] [--out DIR] [--mutool-path <exe>]");
                return 2;
            }

            int page = -1;
            float dpi = 96.0f;
            string outDir = Path.Combine(FindRepoRoot(), "artifacts");
            string? mutoolPath = null;

            for (int i = 1; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--page":
                        page = int.Parse(args[++i]);
                        break;
                    case "--dpi":
                        dpi = float.Parse(args[++i]);
                        break;
                    case "--out":
                        outDir = args[++i];
                        break;
                    case "--mutool-path":
                        mutoolPath = args[++i];
                        break;
                    default:
                        Console.Error.WriteLine($"unknown argument: {args[i]}");
                        return 2;
                }
            }

            Directory.CreateDirectory(outDir);

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PageForge.Core.Pdf.PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                Console.WriteLine($"document: {info.DisplayName} | pages: {info.PageCount}");

                Console.WriteLine($"document: {info.DisplayName} | pages: {info.PageCount}");

                int[] pages = page >= 0 ? new[] { Math.Min(page, info.PageCount - 1) } : Enumerable.Range(0, info.PageCount).ToArray();
                foreach (int pageIndex in pages)
                {
                    var size = await engine.GetPageSizeAsync(pageIndex);
                    string outPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(pdfPath)}-p{pageIndex + 1}-spike.png");
                    var rendered = await engine.RenderPageToPngAsync(pageIndex, dpi);
                    await File.WriteAllBytesAsync(outPath, rendered.PngBytes);
                    await File.WriteAllBytesAsync(outPath, rendered.PngBytes);
                    Console.WriteLine($"  page {pageIndex + 1}: {size.WidthPt:F1} x {size.HeightPt:F1} pt -> {outPath} ({rendered.WidthPixels}x{rendered.HeightPixels} px)");

                    if (mutoolPath is not null)
                    {
                        string refPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(pdfPath)}-p{pageIndex + 1}-mutool.png");
                        RunMutoolDraw(mutoolPath, pdfPath, pageIndex, dpi, refPath);
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"RenderSpike failed: {ex.Message}");
            return 1;
        }
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "PageForge.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? Directory.GetCurrentDirectory();
    }

    private static void RunMutoolDraw(string mutoolPath, string pdfPath, int pageIndex, float dpi, string outPath)
    {
        string resolutionArg = $"{dpi:0}"; // mutool -r is in DPI
        ProcessStartInfo psi = new()
        {
            FileName = mutoolPath,
            ArgumentList = { "draw", "-r", resolutionArg, $"-o", outPath, pdfPath, $"{pageIndex + 1}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            throw new InvalidOperationException("Failed to start mutool.");
        }

        process.WaitForExit(30_000);
        if (process.ExitCode != 0)
        {
            string err = process.StandardError.ReadToEnd();
            Console.Error.WriteLine($"mutool draw failed (exit {process.ExitCode}): {err}");
        }
        else
        {
            Console.WriteLine($"  mutool reference: {outPath}");
        }
    }
}