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
            string replacedId = string.Empty;
            PdfRect originalBounds = default;
            byte[] beforePng = [];
            byte[] afterPng = [];
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
                    replacedId = target.Id;
                    originalBounds = target.Bounds;

                    // The replacement source used to be a render of the very page
                    // being edited, so a working replace and a replace that did
                    // nothing produced near-identical pages. It is now a flat
                    // magenta square: nothing on any corpus document looks like
                    // that, so "did the painted interior actually change" has an
                    // answer.
                    await File.WriteAllBytesAsync(replacementPng, SolidPng(64, 64, 0xFF, 0x00, 0xFF));

                    // What the page looks like before the swap, to compare against.
                    // Comparing rendered PNG bytes rather than decoding pixels keeps
                    // this suite free of an image-decoding dependency - which would
                    // need an AGPL licence check and a notices entry - and byte
                    // equality of renders is already this gate's currency.
                    beforePng = (await engine.RenderPageToPngAsync(page, 72)).PngBytes;

                    receipt = await engine.ReplaceObjectAsync(
                        page, target.Id, new PdfObjectReplacement(replacementPng, "png"));
                    Assert.NotNull(receipt);
                    Assert.NotEmpty(receipt.OldOperators);
                    Assert.NotEmpty(receipt.NewOperators);
                    Assert.NotEqual(receipt.OldOperators, receipt.NewOperators);

                    // The page must actually look different now. Nothing asserted
                    // this before: the gate checked that a receipt came back, that
                    // it differed from the original, and that the page still
                    // rendered - every one of which is equally true of a replace
                    // that quietly did nothing. That is the same assertion shape
                    // that let the sibling move/resize ship broken.
                    afterPng = (await engine.RenderPageToPngAsync(page, 72)).PngBytes;
                    Assert.False(
                        beforePng.AsSpan().SequenceEqual(afterPng),
                        $"{name} page {page} renders identically after replacing its object: " +
                        "the painted interior did not change.");

                    // FR-EDIT-04's contract is that only the interior is swapped, so
                    // the box must be exactly where it was.
                    PdfPageObject afterReplace = Assert.Single(
                        await engine.ListObjectsAsync(page), o => o.Id == target.Id);
                    PdfRectAssert.Equal(originalBounds, afterReplace.Bounds, $"{name} after replace");

                    replaced = true;
                }

                if (!replaced)
                {
                    // No image/vector object anywhere in this document: the list
                    // path (empty sequence, no crash) is all this document must prove.
                    return;
                }

                // (2) prove the engine splice round-trips exactly: undo then redo.
                // Proven by what the page LOOKS like, not just by the splice coming
                // back. The old image lives on in the document's resources after a
                // replace - only the name token before the Do is swapped - so an
                // undo that failed to swap it back would leave the magenta on the
                // page while every structural check still passed.
                await engine.RevertTextEditAsync(replacedPage, receipt!, redo: false);
                byte[] undonePng = (await engine.RenderPageToPngAsync(replacedPage, 72)).PngBytes;
                Assert.True(
                    beforePng.AsSpan().SequenceEqual(undonePng),
                    $"{name} page {replacedPage} did not render identically to the original after undo.");

                await engine.RevertTextEditAsync(replacedPage, receipt!, redo: true);
                byte[] redonePng = (await engine.RenderPageToPngAsync(replacedPage, 72)).PngBytes;
                Assert.True(
                    afterPng.AsSpan().SequenceEqual(redonePng),
                    $"{name} page {replacedPage} did not render identically to the replacement after redo.");

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

                // The box must have survived the save/reopen where it was.
                PdfRectAssert.Equal(
                    originalBounds,
                    Assert.Single(objects, o => o.Id == replacedId).Bounds,
                    $"{name} after save and reopen");

                RenderedPdfPage png = await reader.RenderPageToPngAsync(replacedPage, 72);
                Assert.True(png.PngBytes.Length > 100, $"{name} replaced-object page did not render.");

                // And the replacement must still be the thing being painted. A
                // reopened document renders through a fresh engine, so this is the
                // assertion that says the swap is in the FILE and not merely in the
                // session that performed it.
                Assert.True(
                    afterPng.AsSpan().SequenceEqual(png.PngBytes),
                    $"{name} page {replacedPage} does not render as the replacement after save and reopen.");
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

    /// <summary>
    /// Writes a solid-colour 8-bit RGB PNG.
    ///
    /// Hand-rolled rather than taken from a library: this suite targets net8.0 and
    /// has no imaging dependency, and adding one to draw a coloured square would
    /// mean an AGPL compatibility check and a THIRD-PARTY-NOTICES entry for a
    /// rectangle. The pieces needed are all in the BCL - ZLibStream for the IDAT
    /// payload, and a CRC-32 that PNG specifies directly.
    /// </summary>
    private static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
    {
        // Raw scanlines: each row is a filter byte (0 = None) then RGB triples.
        var raw = new byte[height * ((width * 3) + 1)];
        for (int y = 0, i = 0; y < height; y++)
        {
            raw[i++] = 0;
            for (int x = 0; x < width; x++)
            {
                raw[i++] = r;
                raw[i++] = g;
                raw[i++] = b;
            }
        }

        using var deflated = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(
                   deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        var ihdr = new byte[13];
        BigEndian(ihdr, 0, width);
        BigEndian(ihdr, 4, height);
        ihdr[8] = 8;  // bit depth
        ihdr[9] = 2;  // colour type 2 = truecolour RGB
        // [10] compression, [11] filter, [12] interlace all 0.

        using var png = new MemoryStream();
        png.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR", ihdr);
        WriteChunk(png, "IDAT", deflated.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();

        static void BigEndian(byte[] target, int offset, int value)
        {
            target[offset] = (byte)(value >> 24);
            target[offset + 1] = (byte)(value >> 16);
            target[offset + 2] = (byte)(value >> 8);
            target[offset + 3] = (byte)value;
        }

        static void WriteChunk(Stream target, string type, byte[] payload)
        {
            var length = new byte[4];
            BigEndian(length, 0, payload.Length);
            target.Write(length);

            // The CRC covers the type and the payload, but not the length.
            var typeAndData = new byte[4 + payload.Length];
            for (int i = 0; i < 4; i++)
            {
                typeAndData[i] = (byte)type[i];
            }

            payload.CopyTo(typeAndData, 4);
            target.Write(typeAndData);

            var crc = new byte[4];
            BigEndian(crc, 0, unchecked((int)Crc32(typeAndData)));
            target.Write(crc);
        }
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
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
