// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;

namespace PageForge.Core.Pdf;

/// <summary>
/// Parses the MuPDF shim's flat outline file format into <see cref="PdfOutline"/>.
///
/// The shim writes one Tab-separated record per outline item in pre-order:
///     depth<TAB>page_1based<TAB>x_pt<TAB>y_pt<TAB>title
/// A defensive max-4 split keeps a title that (pathologically) contained a tab
/// from corrupting the fixed fields. Lines are UTF-8 with CR/LF stripped by the
/// caller line split.
/// </summary>
public static class PdfOutlineParser
{
    public static PdfOutline Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var items = new List<OutlineItem>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t', 5);
            if (parts.Length < 5)
            {
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int depth)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
                || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
            {
                continue;
            }

            items.Add(new OutlineItem(parts[4], page, x, y, depth));
        }

        return new PdfOutline(items);
    }
}
