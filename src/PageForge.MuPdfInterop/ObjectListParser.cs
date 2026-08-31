// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Decodes the tab-separated line-per-object output the native shim writes for
/// <c>pf_list_objects</c>. Each line is:
/// <c>object_index \t tag(Image=1/Form=2) \t resource_name \t x0 \t y0 \t x1 \t
/// y1 \t stream_index \t span_start \t span_end</c>, with the bounds in PDF
/// points. The object's <see cref="PdfPageObject.Id"/> is its zero-based index,
/// which the engine hands back verbatim to <c>MoveResizeObjectAsync</c>.
/// Malformed lines are skipped so one bad record never hides the rest.
/// </summary>
internal static class ObjectListParser
{
    private const int TagImage = 1;

    public static IReadOnlyList<PdfPageObject> Parse(IEnumerable<string> lines)
    {
        var result = new List<PdfPageObject>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 7)
            {
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || index < 0)
            {
                continue;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tag))
            {
                continue;
            }

            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x0)
                || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double y0)
                || !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double x1)
                || !double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double y1))
            {
                continue;
            }

            PageObjectKind kind = tag == TagImage ? PageObjectKind.Image : PageObjectKind.Vector;
            string name = parts[2];
            result.Add(new PdfPageObject(
                kind,
                index.ToString(CultureInfo.InvariantCulture),
                new PdfRect(x0, y0, x1, y1),
                string.IsNullOrEmpty(name) ? null : name));
        }

        return result;
    }
}
