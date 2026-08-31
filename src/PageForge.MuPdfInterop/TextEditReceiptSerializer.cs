// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Reads and writes the text-rewrite receipt the native shim exchanges with the
/// command layer. On disk it is UTF-8 TSV:
/// <c>PF-TRW 1</c>, <c>R \t stream_index \t offset \t old_len \t new_len</c>,
/// <c>O \t &lt;base64 old operators&gt;</c> and <c>N \t &lt;base64 new
/// operators&gt;</c>. The receipt is opaque to the session journal (its payload
/// is the text being edited); it is handed back to the engine verbatim to undo
/// or redo the rewrite by exact stream splice.
/// </summary>
internal static class TextEditReceiptSerializer
{
    private const string Header = "PF-TRW";
    private const char FieldSeparator = '\t';

    public static PdfTextEditReceipt Parse(IReadOnlyList<string> lines)
    {
        if (lines.Count < 4)
        {
            throw new FormatException($"Text rewrite receipt has {lines.Count} lines; expected at least 4.");
        }

        string[] header = lines[0].Split(FieldSeparator);
        if (header.Length != 2 || header[0] != Header || !int.TryParse(header[1], out int version))
        {
            throw new FormatException("Text rewrite receipt has an invalid header.");
        }

        string[] r = lines[1].Split(FieldSeparator);
        if (r.Length != 5 || r[0] != "R"
            || !int.TryParse(r[1], out int streamIndex)
            || !int.TryParse(r[2], out int offset)
            || !int.TryParse(r[3], out int oldLength)
            || !int.TryParse(r[4], out int newLength))
        {
            throw new FormatException("Text rewrite receipt has an invalid R record.");
        }

        byte[] oldOperators = DecodeField(lines, "O");
        byte[] newOperators = DecodeField(lines, "N");
        return new PdfTextEditReceipt(version, streamIndex, offset, oldLength, newLength, oldOperators, newOperators);
    }

    public static string ToTsv(PdfTextEditReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var sb = new StringBuilder();
        sb.Append(Header).Append(FieldSeparator).Append(receipt.Version).Append('\n');
        sb.Append('R').Append(FieldSeparator).Append(receipt.StreamIndex).Append(FieldSeparator)
          .Append(receipt.Offset).Append(FieldSeparator)
          .Append(receipt.OldLength).Append(FieldSeparator).Append(receipt.NewLength).Append('\n');
        sb.Append("O\t").Append(Convert.ToBase64String(receipt.OldOperators)).Append('\n');
        sb.Append("N\t").Append(Convert.ToBase64String(receipt.NewOperators)).Append('\n');
        return sb.ToString();
    }

    private static byte[] DecodeField(IReadOnlyList<string> lines, string key)
    {
        foreach (string line in lines)
        {
            if (line.StartsWith(key + FieldSeparator, StringComparison.Ordinal))
            {
                try
                {
                    return Convert.FromBase64String(line[(key.Length + 1)..]);
                }
                catch (FormatException ex)
                {
                    throw new FormatException($"Text rewrite receipt has invalid base64 in its {key} record.", ex);
                }
            }
        }

        throw new FormatException($"Text rewrite receipt has no {key} record.");
    }
}