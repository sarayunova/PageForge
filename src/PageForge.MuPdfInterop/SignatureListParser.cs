// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Decodes the tab-separated rows the shim writes for <c>pf_list_signatures</c>:
/// <c>sig_index \t page \t name \t x0 \t y0 \t x1 \t y1 \t signed \t digest \t
/// certificate \t signer</c>, with the rect in PDF points and <c>signed</c> as
/// 1 or 0. The last three columns are absent for an unsigned field.
///
/// Malformed rows are skipped rather than thrown on, so one unreadable field
/// cannot hide the rest of a document's signatures - the same rule
/// <see cref="ObjectListParser"/> follows.
/// </summary>
internal static class SignatureListParser
{
    public static IReadOnlyList<PdfSignatureField> Parse(IEnumerable<string> lines)
    {
        var result = new List<PdfSignatureField>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');

            // Eight columns is a complete unsigned row; a signed one carries
            // three more. Anything shorter cannot be read at all.
            if (parts.Length < 8)
            {
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index) ||
                !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page))
            {
                continue;
            }

            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x0) ||
                !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double y0) ||
                !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double x1) ||
                !double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out double y1))
            {
                continue;
            }

            result.Add(new PdfSignatureField(
                index,
                page,
                parts[2],
                new PdfRect(x0, y0, x1, y1),
                parts[7] == "1",
                parts.Length > 8 ? parts[8] : string.Empty,
                parts.Length > 9 ? parts[9] : string.Empty,
                parts.Length > 10 ? parts[10] : string.Empty));
        }

        return result;
    }
}
