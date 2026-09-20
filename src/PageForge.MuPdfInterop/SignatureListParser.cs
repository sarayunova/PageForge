// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Decodes the tab-separated line-per-field output the native shim writes for
/// <c>pf_list_signatures</c>. Each line is:
/// <c>sig_index \t page \t name \t x0 \t y0 \t x1 \t y1 \t signed \t digest \t certificate \t signer</c>,
/// with bounds in PDF points, <c>signed</c> as 1 or 0, and the digest,
/// certificate and signer columns empty for an unsigned field. Tabs and line
/// breaks inside the text columns were already replaced with spaces by the
/// shim, so splitting on the tab is safe.
/// </summary>
/// <remarks>
/// A malformed line is skipped rather than throwing, matching
/// <see cref="WidgetListParser"/>: one unreadable record must not hide the
/// signatures that did decode. The trade is sharper here — a dropped row is a
/// signature the user is never told about — so the parser skips only rows it
/// cannot turn into a well-formed record at all, never rows whose
/// verification merely failed.
/// </remarks>
internal static class SignatureListParser
{
    private const int ColumnCount = 11;

    public static IReadOnlyList<PdfSignature> Parse(IEnumerable<string> lines)
    {
        var result = new List<PdfSignature>();
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

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || index < 0)
            {
                continue;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pageIndex)
                || pageIndex < 0)
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

            // Anything other than a literal "1" reads as unsigned. An
            // unparseable flag must not become "signed", which would present an
            // unverified field as a verified one.
            bool isSigned = string.Equals(parts[7], "1", StringComparison.Ordinal);

            result.Add(new PdfSignature(
                index,
                pageIndex,
                parts[2],
                new PdfRect(x0, y0, x1, y1),
                isSigned,
                parts[8],
                parts[9],
                parts[10]));
        }

        return result;
    }
}
