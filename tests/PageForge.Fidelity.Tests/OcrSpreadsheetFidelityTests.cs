// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO.Compression;
using System.Xml.Linq;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// Spreadsheet export of recognized text (FR-OCR-02), against the real engine
/// and the real recognizer.
///
/// The unit tests prove the workbook is well formed. This proves it contains
/// what was on the page: a writer that produces a perfect empty workbook
/// passes every structural check ever written.
/// </summary>
/// <summary>
/// Recognition runs are serialized against each other. Tesseract is
/// initialized per run over the same bundled trained data, and two classes
/// recognizing at once is enough to make one of them fail - which presented
/// as an unrelated OCR test breaking the moment this one was added.
/// </summary>
[CollectionDefinition(OcrSerial.Name, DisableParallelization = true)]
public sealed class OcrSerial
{
    public const string Name = "ocr";
}

[Collection(OcrSerial.Name)]
public sealed class OcrSpreadsheetFidelityTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Recognized_text_reaches_the_spreadsheet()
    {
        string output = Path.Combine(AppContext.BaseDirectory, $"ocr-sheet-{Guid.NewGuid():N}.xlsx");

        try
        {
            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(Fixture("scan-letters.pdf"));

            OcrResult result = await engine.OcrToSpreadsheetAsync(output, options: null);

            Assert.True(File.Exists(output), "The spreadsheet was not written.");
            Assert.Equal(2, result.PageCount);

            string[] cells = ReadCells(output);

            Assert.Equal("Page", cells[0]);
            Assert.Equal("Line", cells[1]);
            Assert.Equal("Text", cells[2]);

            // The words the OCR fidelity suite already pins for this fixture.
            // Asserting on the text is what separates "a workbook was
            // produced" from "the scan came through".
            string body = string.Join(" ", cells.Skip(3));
            Assert.Contains("SCANNED", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("PAGE", body, StringComparison.OrdinalIgnoreCase);

            // Both pages have to be represented, not only the first: a loop
            // that stops after one page still fills a workbook with plausible
            // text.
            string[] pages = PageColumn(output);
            Assert.Contains("1", pages);
            Assert.Contains("2", pages);
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task A_spreadsheet_of_a_text_document_carries_its_text()
    {
        // Not every document handed to this is a scan. A born-digital page has
        // text already, and the export must carry it rather than depend on the
        // recognizer finding something in a page that holds no image.
        string output = Path.Combine(AppContext.BaseDirectory, $"ocr-sheet-{Guid.NewGuid():N}.xlsx");

        try
        {
            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(Fixture("contract-multipage.pdf"));

            OcrResult result = await engine.OcrToSpreadsheetAsync(output, options: null);

            Assert.Equal(4, result.PageCount);

            string body = string.Join(" ", ReadCells(output).Skip(3));
            Assert.False(
                string.IsNullOrWhiteSpace(body),
                "The workbook has a header and nothing else, so no page text reached it.");
        }
        finally
        {
            TryDelete(output);
        }
    }

    private static string[] ReadCells(string workbook)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using ZipArchive archive = ZipFile.OpenRead(workbook);
        ZipArchiveEntry sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidOperationException("The workbook has no worksheet part.");
        using Stream stream = sheet.Open();
        return XDocument.Load(stream).Descendants(ns + "t").Select(t => t.Value).ToArray();
    }

    /// <summary>The first cell of every row after the header — the page numbers.</summary>
    private static string[] PageColumn(string workbook)
    {
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using ZipArchive archive = ZipFile.OpenRead(workbook);
        ZipArchiveEntry sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidOperationException("The workbook has no worksheet part.");
        using Stream stream = sheet.Open();

        return XDocument.Load(stream)
            .Descendants(ns + "row")
            .Skip(1)
            .Select(r => r.Descendants(ns + "t").FirstOrDefault()?.Value ?? string.Empty)
            .ToArray();
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
