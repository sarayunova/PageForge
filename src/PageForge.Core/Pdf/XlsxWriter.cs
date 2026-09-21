// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;

namespace PageForge.Core.Pdf;

/// <summary>
/// A minimal Office Open XML workbook writer (FR-OCR-02).
///
/// PageForge's other conversions are produced by MuPDF — searchable PDF by its
/// OCR band writer, DOCX by its document writer, PNG by its zip writer — but
/// MuPDF has no spreadsheet writer, which is why this requirement sat
/// unimplemented. A workbook of recognized text needs no cell formatting, no
/// formulas and no styles, so the format is written directly rather than
/// taking on a dependency an offline desktop application would then have to
/// ship and keep licensed.
/// </summary>
/// <remarks>
/// Strings are written inline (<c>t="inlineStr"</c>) instead of through a
/// shared-strings table. A shared table is how real spreadsheets avoid
/// repeating text, but it is a second part that has to agree with the sheet,
/// and disagreement between them produces a file that opens with the wrong
/// text in the wrong cells rather than one that fails to open. Recognized page
/// text repeats little, so the trade costs almost nothing.
/// </remarks>
public static class XlsxWriter
{
    /// <summary>One row's cells, in column order.</summary>
    public sealed record Row(IReadOnlyList<string> Cells);

    /// <summary>
    /// Writes a single-sheet workbook to <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="sheetName">
    /// The worksheet's name. Excel rejects a workbook whose sheet name is
    /// empty, longer than 31 characters or contains <c>: \ / ? * [ ]</c>, so
    /// the name is sanitized rather than passed through — a rejected workbook
    /// reports as "unreadable content", which says nothing about the cause.
    /// </param>
    public static void Write(string outputPath, string sheetName, IReadOnlyList<Row> rows)
    {
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentNullException.ThrowIfNull(rows);

        string safeSheet = SanitizeSheetName(sheetName);

        using var archive = new ZipArchive(
            File.Create(outputPath), ZipArchiveMode.Create, leaveOpen: false);

        WriteEntry(archive, "[Content_Types].xml", ContentTypes);
        WriteEntry(archive, "_rels/.rels", RootRelationships);
        WriteEntry(archive, "xl/workbook.xml", Workbook(safeSheet));
        WriteEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationships);
        WriteEntry(archive, "xl/worksheets/sheet1.xml", Sheet(rows));
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using Stream stream = entry.Open();

        // No BOM: each part is XML with an explicit declaration, and a
        // byte-order mark ahead of that declaration makes stricter readers
        // reject the part.
        using var writer = new StreamWriter(
            stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private const string ContentTypes =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
        </Types>
        """;

    private const string RootRelationships =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
        </Relationships>
        """;

    private const string WorkbookRelationships =
        """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
        </Relationships>
        """;

    private static string Workbook(string sheetName) =>
        $"""
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                  xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <sheets><sheet name="{Escape(sheetName)}" sheetId="1" r:id="rId1"/></sheets>
        </workbook>
        """;

    private static string Sheet(IReadOnlyList<Row> rows)
    {
        var sheet = new StringBuilder();
        sheet.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
        sheet.Append(
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        for (int r = 0; r < rows.Count; r++)
        {
            int rowNumber = r + 1;
            sheet.Append("<row r=\"").Append(rowNumber.ToString(CultureInfo.InvariantCulture)).Append("\">");

            IReadOnlyList<string> cells = rows[r].Cells;
            for (int c = 0; c < cells.Count; c++)
            {
                sheet.Append("<c r=\"").Append(ColumnName(c))
                     .Append(rowNumber.ToString(CultureInfo.InvariantCulture))
                     .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                     .Append(Escape(cells[c]))
                     .Append("</t></is></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        return sheet.ToString();
    }

    /// <summary>A, B, ... Z, AA, AB, ... for a zero-based column index.</summary>
    public static string ColumnName(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);

        var name = new StringBuilder();
        int value = index;
        do
        {
            name.Insert(0, (char)('A' + (value % 26)));
            value = (value / 26) - 1;
        }
        while (value >= 0);

        return name.ToString();
    }

    /// <summary>
    /// XML-escapes a cell value and drops the characters XML 1.0 cannot carry.
    ///
    /// Recognized text comes from an image, so it can contain control bytes the
    /// recognizer produced from noise. Writing one yields a workbook every
    /// reader refuses, which presents as "OCR export is broken" rather than
    /// "one page had a stray byte in it".
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var clean = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (XmlConvert.IsXmlChar(ch))
            {
                clean.Append(ch);
            }
        }

        return new System.Xml.Linq.XText(clean.ToString()).ToString();
    }

    /// <summary>Trims a sheet name down to what Excel accepts.</summary>
    public static string SanitizeSheetName(string? name)
    {
        string candidate = string.IsNullOrWhiteSpace(name) ? "Sheet1" : name.Trim();

        var clean = new StringBuilder(candidate.Length);
        foreach (char ch in candidate)
        {
            clean.Append(ch is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? ' ' : ch);
        }

        string result = clean.ToString().Trim();
        if (result.Length == 0)
        {
            result = "Sheet1";
        }

        return result.Length <= 31 ? result : result[..31];
    }
}
