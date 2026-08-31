// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Decodes the tab-separated line-per-annotation output the native shim writes
/// for <c>pf_list_annotations</c>. Each line is:
/// <c>typeNumber \t typeName \t x0 \t y0 \t x1 \t y1 \t contents</c>.
/// The leading number is the MuPDF <c>pdf_annot_type</c> enum value for those
/// kinds the Core API exposes; native-only kinds (Link, Popup, Widget, ...) are
/// skipped because the Core contract does not model them.
/// </summary>
internal static class AnnotationListParser
{
    public static IReadOnlyList<PdfAnnotation> Parse(IEnumerable<string> lines)
    {
        var result = new List<PdfAnnotation>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 6)
            {
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeCode))
            {
                continue;
            }

            AnnotationType? type = Map(typeCode);
            if (type is null)
            {
                continue;
            }

            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x0)
                || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double y0)
                || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double x1)
                || !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double y1))
            {
                continue;
            }

            string? contents = parts.Length >= 7 && parts[6].Length > 0 ? parts[6] : null;
            result.Add(new PdfAnnotation(type.Value, x0, y0, x1, y1, contents));
        }

        return result;
    }

    private static AnnotationType? Map(int code) => code switch
    {
        0 => AnnotationType.Text,      // PDF_ANNOT_TEXT
        4 => AnnotationType.Square,    // PDF_ANNOT_SQUARE
        5 => AnnotationType.Circle,    // PDF_ANNOT_CIRCLE
        8 => AnnotationType.Highlight, // PDF_ANNOT_HIGHLIGHT
        9 => AnnotationType.Underline, // PDF_ANNOT_UNDERLINE
        11 => AnnotationType.StrikeOut,// PDF_ANNOT_STRIKE_OUT
        13 => AnnotationType.Stamp,    // PDF_ANNOT_STAMP
        15 => AnnotationType.Ink,      // PDF_ANNOT_INK
        _ => null,
    };
}
