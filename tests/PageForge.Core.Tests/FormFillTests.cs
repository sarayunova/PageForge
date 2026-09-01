// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-FORM-01 Core expectations against the <see cref="FakePdfEngine"/> seam:
/// listing AcroForm fields, applying a value with the value round-tripping,
/// checkbox fill, rejection of non-fillable field kinds, and flattening. The
/// native path is covered end-to-end by the fidelity suite; these lock in the
/// Core-visible contract without a native dependency.
/// </summary>
public sealed class FormFillTests
{
    [Fact]
    public async Task ListFormFieldsAsync_returns_seeded_fields_in_order()
    {
        FakePdfEngine engine = new(1);
        await using (engine)
        {
            engine.AddStoredFormField(0, new PdfFormField(FormFieldKind.Text, "0", "FullName", new PdfRect(170, 650, 420, 672), ""));
            engine.AddStoredFormField(0, new PdfFormField(FormFieldKind.Checkbox, "1", "Consent", new PdfRect(170, 590, 190, 610), "Off"));

            IReadOnlyList<PdfFormField> fields = await engine.ListFormFieldsAsync(0);

            Assert.Equal(2, fields.Count);
            Assert.Equal("FullName", fields[0].Name);
            Assert.Equal("Consent", fields[1].Name);
            Assert.Equal("FullName", fields[0].Label);
        }
    }

    [Fact]
    public async Task ListFormFieldsAsync_returns_empty_when_none_seeded()
    {
        FakePdfEngine engine = new(2);
        await using (engine)
        {
            Assert.Empty(await engine.ListFormFieldsAsync(0));
            Assert.Empty(await engine.ListFormFieldsAsync(1));
        }
    }

    [Fact]
    public async Task SetFormFieldValueAsync_updates_the_field_value()
    {
        FakePdfEngine engine = new(1);
        await using (engine)
        {
            engine.AddStoredFormField(0, new PdfFormField(FormFieldKind.Text, "0", "FullName", new PdfRect(170, 650, 420, 672), ""));

            await engine.SetFormFieldValueAsync(0, "0", "Grace Hopper");

            Assert.Equal("Grace Hopper", engine.StoredFormFields(0)[0].Value);
            Assert.Contains("0:0:Grace Hopper", engine.FormValueSet);
        }
    }

    [Fact]
    public async Task SetFormFieldValueAsync_checks_a_checkbox()
    {
        FakePdfEngine engine = new(1);
        await using (engine)
        {
            engine.AddStoredFormField(0, new PdfFormField(FormFieldKind.Checkbox, "0", "Consent", new PdfRect(170, 590, 190, 610), "Off"));

            await engine.SetFormFieldValueAsync(0, "0", "Yes");

            Assert.Equal("Yes", engine.StoredFormFields(0)[0].Value);
        }
    }

    [Fact]
    public async Task SetFormFieldValueAsync_rejects_non_fillable_kinds()
    {
        FakePdfEngine engine = new(1);
        await using (engine)
        {
            engine.AddStoredFormField(0, new PdfFormField(FormFieldKind.Signature, "0", "Sig", new PdfRect(170, 400, 420, 460), ""));

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.SetFormFieldValueAsync(0, "0", "x").AsTask());
        }
    }

    [Fact]
    public async Task SetFormFieldValueAsync_rejects_unknown_field_id()
    {
        FakePdfEngine engine = new(1);
        await using (engine)
        {
            engine.AddStoredFormField(0, new PdfFormField(FormFieldKind.Text, "0", "Name", new PdfRect(0, 0, 100, 50), ""));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => engine.SetFormFieldValueAsync(0, "9", "x").AsTask());
        }
    }

    [Fact]
    public async Task FlattenFormAsync_marks_the_form_as_flattened()
    {
        FakePdfEngine engine = new(1);
        await using (engine)
        {
            await engine.FlattenFormAsync();

            Assert.True(engine.FormFlattened);
        }
    }
}
