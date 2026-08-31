// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Decodes the tab-separated line-per-run output the native shim writes for
/// <c>pf_list_text_runs</c>. Each line is:
/// <c>run_idx \t x0 \t y0 \t x1 \t y1 \t font_size \t font_embedded \t font_name \t text</c>.
/// The font name and text are last because both may contain spaces (the shim
/// replaces their tabs/CR/LF with spaces). Malformed lines are skipped so one
/// bad run never hides the rest of the page's text.
/// </summary>
internal static class TextRunListParser
{
    private const int ColumnCount = 9;
    private const int IndexColumn = 0;
    private const int TextColumn = 8;

    public static IReadOnlyList<PdfTextRun> Parse(IEnumerable<string> lines)
    {
        var result = new List<PdfTextRun>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < ColumnCount)
            {
                continue;
            }

            if (!int.TryParse(parts[IndexColumn], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || index < 0)
            {
                continue;
            }

            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double x0)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double y0)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x1)
                || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double y1)
                || !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double fontSize)
                || !double.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out double embedded))
            {
                continue;
            }

            result.Add(new PdfTextRun(
                index,
                x0, y0, x1, y1,
                fontSize,
                embedded != 0,
                parts[7],
                parts.Length > TextColumn ? parts[TextColumn] : string.Empty));
        }

        return result;
    }
}