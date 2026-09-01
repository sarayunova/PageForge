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
