// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO.Compression;
using System.Xml.Linq;
using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-OCR-02 unit tests for <see cref="XlsxWriter"/>.
///
/// A spreadsheet writer fails in a particular way: the file is produced, has a
/// plausible size, and is then refused by every reader as "unreadable
/// content" — or worse, opens with the text in the wrong cells. Asserting that
/// a file appeared would catch neither, so these read the workbook back apart
/// and check the parts a reader actually consumes.
/// </summary>
public sealed class XlsxWriterTests : IDisposable
{
    private readonly string _output = Path.Combine(
        Path.GetTempPath(), $"pageforge-xlsx-test-{Guid.NewGuid():N}.xlsx");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_output))
            {
                File.Delete(_output);
            }
        }
        catch (IOException)
        {
        }
    }

    private static XlsxWriter.Row Row(params string[] cells) => new(cells);

    private static XDocument PartOf(string path, string entryName)
    {
        using ZipArchive archive = ZipFile.OpenRead(path);
        ZipArchiveEntry entry = archive.GetEntry(entryName)
            ?? throw new InvalidOperationException($"The workbook has no '{entryName}' part.");
        using Stream stream = entry.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void A_workbook_carries_every_part_a_reader_needs()
    {
        XlsxWriter.Write(_output, "Recognized text", new[] { Row("Page", "Text") });

        using ZipArchive archive = ZipFile.OpenRead(_output);
        string[] entries = archive.Entries.Select(e => e.FullName).ToArray();

        // Any one of these missing produces a file that opens as corrupt, and
        // the reader never says which part it was looking for.
        Assert.Contains("[Content_Types].xml", entries);
        Assert.Contains("_rels/.rels", entries);
        Assert.Contains("xl/workbook.xml", entries);
        Assert.Contains("xl/_rels/workbook.xml.rels", entries);
        Assert.Contains("xl/worksheets/sheet1.xml", entries);
    }

    [Fact]
    public void Cells_land_in_the_rows_and_columns_they_were_given()
    {
        XlsxWriter.Write(_output, "Sheet", new[]
        {
            Row("Page", "Line", "Text"),
            Row("1", "1", "SCANNED PAGE"),
        });

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement[] rows = PartOf(_output, "xl/worksheets/sheet1.xml")
            .Descendants(ns + "row").ToArray();

        Assert.Equal(2, rows.Length);
        Assert.Equal("1", rows[0].Attribute("r")!.Value);
        Assert.Equal("2", rows[1].Attribute("r")!.Value);

        // The cell reference is what a reader uses to place a value, so a row
        // whose cells are numbered wrong opens with the text shifted rather
        // than failing — the kind of wrong nobody spots in a diff.
        string[] references = rows[1].Descendants(ns + "c")
            .Select(c => c.Attribute("r")!.Value).ToArray();
        Assert.Equal(new[] { "A2", "B2", "C2" }, references);

        Assert.Equal("SCANNED PAGE", rows[1].Descendants(ns + "t").Last().Value);
    }

    [Fact]
    public void Text_that_would_break_the_xml_is_carried_safely()
    {
        // Ampersands and angle brackets are ordinary in recognized text, and
        // pasting them straight into the sheet produces a workbook no reader
        // will open.
        XlsxWriter.Write(_output, "Sheet", new[] { Row("Tom & Jerry <b>", "a \"quote\"") });

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string[] values = PartOf(_output, "xl/worksheets/sheet1.xml")
            .Descendants(ns + "t").Select(t => t.Value).ToArray();

        Assert.Equal(new[] { "Tom & Jerry <b>", "a \"quote\"" }, values);
    }

    [Fact]
    public void A_control_character_from_the_recognizer_does_not_poison_the_file()
    {
        // OCR reads noise as well as letters, and XML 1.0 cannot carry most
        // control characters at all. One of them in one cell would otherwise
        // cost the entire export.
        XlsxWriter.Write(_output, "Sheet", new[] { Row("good\u0000text\u0008here") });

        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string value = PartOf(_output, "xl/worksheets/sheet1.xml")
            .Descendants(ns + "t").Single().Value;

        Assert.Equal("goodtexthere", value);
    }

    [Theory]
    [InlineData("Recognized text", "Recognized text")]
    [InlineData("", "Sheet1")]
    [InlineData("   ", "Sheet1")]
    [InlineData("bad:name/with\\chars?[]*", "bad name with chars")]
    [InlineData(
        "a name that is far longer than the thirty-one characters Excel allows",
        "a name that is far longer than ")]
    public void Sheet_names_are_trimmed_to_what_a_reader_accepts(string given, string expected)
    {
        // Excel refuses the whole workbook over the sheet name, reporting only
        // "unreadable content" — a failure that says nothing about the name
        // having been the cause.
        Assert.Equal(expected, XlsxWriter.SanitizeSheetName(given));
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(27, "AB")]
    [InlineData(51, "AZ")]
    [InlineData(52, "BA")]
    public void Column_names_continue_past_the_alphabet(int index, string expected)
        => Assert.Equal(expected, XlsxWriter.ColumnName(index));
}
