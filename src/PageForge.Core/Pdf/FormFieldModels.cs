// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>The kind of an AcroForm field on a page (FR-FORM).</summary>
public enum FormFieldKind
{
    /// <summary>A single-line or multi-line text entry field.</summary>
    Text,

    /// <summary>A checkbox that toggles between checked and unchecked.</summary>
    Checkbox,

    /// <summary>A radio button in a mutually-exclusive group.</summary>
    Radio,

    /// <summary>A combo box (editable drop-down list).</summary>
    Combo,

    /// <summary>A non-editable list box.</summary>
    ListBox,

    /// <summary>A push button (not fillable).</summary>
    Button,

    /// <summary>A digital signature field (not fillable via text).</summary>
    Signature,
}

/// <summary>
/// A fillable AcroForm field on a page (FR-FORM-01): its <see cref="Kind"/>,
/// <see cref="Bounds"/> in PDF points, its <see cref="Name"/> (the field's /T
/// name, empty when unnamed), its current <see cref="Value"/>, and a stable
/// <see cref="Id"/> (the zero-based widget index on the page) that the engine
/// hands back verbatim to <c>SetFormFieldValueAsync</c>. The id is opaque to
/// Core: it is created by the engine's <c>ListFormFieldsAsync</c>.
/// </summary>
public sealed record PdfFormField(
    FormFieldKind Kind,
    string Id,
    string Name,
    PdfRect Bounds,
    string Value)
{
    /// <summary>A short, human-readable label for UI (e.g. "Name" or "Text 0").</summary>
    public string Label => string.IsNullOrEmpty(Name) ? $"{Kind} {Id}" : Name;
}

/// <summary>
/// Field /Ff flag bits for a text field being created (FR-FORM-02). Values map
/// to the PDF spec's field-flag bits (bit 1 = read-only, bit 2 = required,
/// bit 13 = multi-line, bit 25 = comb).
/// </summary>
public static class FormFieldFlags
{
    /// <summary>Bit 1 (1): the field is read-only.</summary>
    public const int ReadOnly = 1;

    /// <summary>Bit 2 (2): the field is required (must be filled).</summary>
    public const int Required = 1 << 1;

    /// <summary>Bit 13 (4096): the text field is multi-line.</summary>
    public const int Multiline = 1 << 12;

    /// <summary>Bit 25 (16777216): the text field is rendered as cells of width MaxLen.</summary>
    public const int Comb = 1 << 24;
}

/// <summary>Horizontal justification of a text field (FR-FORM-02).</summary>
public enum FormFieldJustification
{
    /// <summary>Left-justified.</summary>
    Left = 0,

    /// <summary>Centre-justified.</summary>
    Center = 1,

    /// <summary>Right-justified.</summary>
    Right = 2,
}

/// <summary>
/// A specification for creating a new AcroForm field on a page (FR-FORM-02).
/// Only <see cref="FormFieldKind.Text"/> fields are supported by the engine in
/// this slice; a non-text kind throws. <see cref="Bounds"/> is the widget
/// rectangle in PDF points. <see cref="Flags"/> is an OR of <see cref="FormFieldFlags"/>
/// bits. <see cref="MaxLength"/> limits the number of characters (0 = no limit);
/// <see cref="Quadding"/> sets justification; <see cref="BorderWidth"/> is the
/// visible border in points (1 by default).
/// </summary>
public sealed record FormFieldSpec(
    FormFieldKind Kind,
    string Name,
    PdfRect Bounds,
    int Flags = 0,
    int MaxLength = 0,
    FormFieldJustification Quadding = FormFieldJustification.Left,
    int BorderWidth = 1);

