// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Globalization;
using PageForge.Core.Pdf;

namespace PageForge.MuPdfInterop;

/// <summary>
/// Decodes the tab-separated line-per-widget output the native shim writes for
/// <c>pf_list_widgets</c>. Each line is:
/// <c>widget_index \t type_num \t field_name \t x0 \t y0 \t x1 \t y1 \t value</c>,
/// with the bounds in PDF points. The widget's <see cref="PdfFormField.Id"/> is its
/// zero-based page index, which the engine hands back verbatim to
/// <c>SetFormFieldValueAsync</c>. type_num is the <c>enum pdf_widget_type</c> integer
/// (see <c>mupdf/pdf/form.h</c>): 0=Unknown 1=Button 2=Checkbox 3=Combobox 4=Listbox
/// 5=Radiobutton 6=Signature 7=Text. Malformed lines are skipped so one bad record
/// never hides the rest (tab/CR/LF inside name and value were already replaced with
/// spaces by the shim, so field splitting is safe).
/// </summary>
internal static class WidgetListParser
{
    private const int TypeButton = 1;
    private const int TypeCheckbox = 2;
    private const int TypeCombobox = 3;
    private const int TypeListbox = 4;
    private const int TypeRadiobutton = 5;
    private const int TypeSignature = 6;
    private const int TypeText = 7;

    public static IReadOnlyList<PdfFormField> Parse(IEnumerable<string> lines)
    {
        var result = new List<PdfFormField>();
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length < 8)
            {
                continue;
            }

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index)
                || index < 0)
            {
                continue;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeNum))
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

            result.Add(new PdfFormField(
                ToKind(typeNum),
                index.ToString(CultureInfo.InvariantCulture),
                parts[2],
                new PdfRect(x0, y0, x1, y1),
                parts[7]));
        }

        return result;
    }

    private static FormFieldKind ToKind(int typeNum) => typeNum switch
    {
        TypeText => FormFieldKind.Text,
        TypeCheckbox => FormFieldKind.Checkbox,
        TypeRadiobutton => FormFieldKind.Radio,
        TypeCombobox => FormFieldKind.Combo,
        TypeListbox => FormFieldKind.ListBox,
        TypeButton => FormFieldKind.Button,
        TypeSignature => FormFieldKind.Signature,
        _ => FormFieldKind.Text,
    };
}
