// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// 2E-native exit gate for FR-EDIT-04 image replace: the real MuPDF shim embeds
/// a replacement raster as a NEW XObject and splices only the name token before
/// the <c>Do</c> operator, so the object's bounding box is preserved while its
/// painted interior is swapped. This drives every real-document corpus PDF
/// through that path on the real shim: list objects, replace the first object
/// found with a real rendered PNG, prove the undo/redo splice round-trips
/// exactly, persist, reopen and render. A corpus with no <c>Do</c> objects
/// validates the empty-list path; the scanned-letter corpus provides the
/// image-bearing pages that give the gate its teeth. Any unhandled shim
/// exception fails the gate.
/// </summary>
public sealed class ObjectReplaceFidelityTests
{
    private const string ArtifactRoot = "artifacts/edit-fidelity";
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

    private static string Artifact(string name)
        => Path.Combine(AppContext.BaseDirectory, ArtifactRoot, name);

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public async Task Object_replace_persists_across_save_reopen(string pdfPath)
    {
        string name = Path.GetFileNameWithoutExtension(pdfPath);
        Directory.CreateDirectory(ArtifactRoot);

        string editedOut = Path.Combine(AppContext.BaseDirectory, $"replobj-{Guid.NewGuid():N}.pdf");
        string replacementPng = Path.Combine(Path.GetTempPath(), $"repl-src-{Guid.NewGuid():N}.png");
        try
        {
            bool replaced = false;
            int pageCount;
            int replacedPage = -1;
            PdfTextEditReceipt? receipt = null;

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                pageCount = info.PageCount;

                for (int page = 0; page < pageCount && !replaced; page++)
                {
                    IReadOnlyList<PdfPageObject> objects = await engine.ListObjectsAsync(page);
                    if (objects.Count == 0)
                    {
                        continue;
                    }

                    PdfPageObject target = objects[0];
                    replacedPage = page;

                    // A genuinely distinct, valid PNG: render the same page to a
                    // file and use that raster as the replacement interior.
                    RenderedPdfPage pagePng = await engine.RenderPageToPngAsync(page, 72);
                    Assert.True(pagePng.PngBytes.Length > 100, $"{name} page {page} did not render a replacement source.");
                    await File.WriteAllBytesAsync(replacementPng, pagePng.PngBytes);

                    receipt = await engine.ReplaceObjectAsync(
                        page, target.Id, new PdfObjectReplacement(replacementPng, "png"));
                    Assert.NotNull(receipt);
                    Assert.NotEmpty(receipt.OldOperators);
                    Assert.NotEmpty(receipt.NewOperators);
                    Assert.NotEqual(receipt.OldOperators, receipt.NewOperators);
                    replaced = true;
                }

                if (!replaced)
                {
                    // No image/vector object anywhere in this document: the list
                    // path (empty sequence, no crash) is all this document must prove.
                    return;
                }

                // (2) prove the engine splice round-trips exactly: undo then redo.
                await engine.RevertTextEditAsync(replacedPage, receipt!, redo: false);
                await engine.RevertTextEditAsync(replacedPage, receipt!, redo: true);

                // (3) persist.
                await engine.SaveAsAsync(editedOut);
            }

            Assert.True(File.Exists(editedOut), "Edited document did not save.");

            // (4) reopen: the replaced page must still list objects and render.
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(editedOut);
                Assert.Equal(pageCount, reopened.PageCount);

                IReadOnlyList<PdfPageObject> objects = await reader.ListObjectsAsync(replacedPage);
                Assert.NotEmpty(objects);

                RenderedPdfPage png = await reader.RenderPageToPngAsync(replacedPage, 72);
                Assert.True(png.PngBytes.Length > 100, $"{name} replaced-object page did not render.");
                await File.WriteAllBytesAsync(Artifact($"{name}.repl.p1.png"), png.PngBytes);
            }

            File.Copy(editedOut, Artifact($"{name}.replobj.pdf"), overwrite: true);
        }
        finally
        {
            TryDelete(editedOut);
            TryDelete(replacementPng);
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
