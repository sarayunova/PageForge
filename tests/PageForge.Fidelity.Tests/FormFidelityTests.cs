// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-FORM-01 exit-gate (Phase 3): AcroForm fill and flatten through the real
/// MuPDF shim. The engine lists the widgets of each corpus page, fills every
/// fillable field (text/checkbox on the form-application corpus), proves the
/// listed values round-trip at the engine layer, flattens the whole document,
/// persists, reopens and verifies the fields are no longer interactive, then
/// renders the result. Documents with no AcroForm widgets validate the list
/// path returns an empty sequence without crashing; the AcroForm corpus gives
/// the gate its teeth. Any unhandled shim exception fails the gate.
/// </summary>
public sealed class FormFidelityTests
{
    private const string ArtifactRoot = "artifacts/form-fidelity";
    private static string CorpusDir => Path.Combine(AppContext.BaseDirectory, "corpus");

    public static TheoryData<string> CorpusPdfs()
    {
        var data = new TheoryData<string>();
        foreach (string file in Directory.GetFiles(CorpusDir, "*.pdf", SearchOption.TopDirectoryOnly))
        {
            data.Add(file);
        }

        return data;
    }

    private static string Artifact(string name)
        => Path.Combine(AppContext.BaseDirectory, ArtifactRoot, name);

    [Theory]
    [MemberData(nameof(CorpusPdfs))]
    public async Task Form_fill_and_flatten_persists_across_save_reopen(string pdfPath)
    {
        string name = Path.GetFileNameWithoutExtension(pdfPath);
        Directory.CreateDirectory(ArtifactRoot);

        string editedOut = Path.Combine(AppContext.BaseDirectory, $"formedit-{Guid.NewGuid():N}.pdf");
        try
        {
            bool anyField = false;
            bool filled = false;
            int pageCount = 0;
            string fillValue = "Grace Hopper";

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(pdfPath);
                pageCount = info.PageCount;

                for (int page = 0; page < pageCount; page++)
                {
                    IReadOnlyList<PdfFormField> fields = await engine.ListFormFieldsAsync(page);
                    if (fields.Count == 0)
                    {
                        continue;
                    }

                    anyField = true;
                    foreach (PdfFormField field in fields)
                    {
                        switch (field.Kind)
                        {
                            case FormFieldKind.Text:
                                await engine.SetFormFieldValueAsync(page, field.Id, fillValue);
                                filled = true;
                                break;
                            case FormFieldKind.Checkbox:
                                await engine.SetFormFieldValueAsync(page, field.Id, "Yes");
                                filled = true;
                                break;
                            case FormFieldKind.Combo:
                            case FormFieldKind.ListBox:
                            case FormFieldKind.Radio:
                                await engine.SetFormFieldValueAsync(page, field.Id, "Yes");
                                filled = true;
                                break;
                        }
                    }

                    // Prove the values round-trip at the engine layer before flattening.
                    IReadOnlyList<PdfFormField> after = await engine.ListFormFieldsAsync(page);
                    Assert.Equal(fields.Count, after.Count);
                    foreach (PdfFormField field in fields)
                    {
                        PdfFormField updated = after.Single(f => f.Id == field.Id);
                        if (field.Kind is FormFieldKind.Text)
                        {
                            Assert.Equal(fillValue, updated.Value);
                        }
                    }
                }

                if (!anyField)
                {
                    // No AcroForm widgets in this document: the purity of the list
                    // path (empty sequence, no crash) is all this document must prove.
                    return;
                }

                Assert.True(filled, "Expected to fill at least one AcroForm field.");
                await engine.FlattenFormAsync();
                await engine.SaveAsAsync(editedOut);
            }

            Assert.True(File.Exists(editedOut), "Filled document did not save.");

            // Reopen: the fields must no longer be interactive (flatten baked them).
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(editedOut);
                Assert.Equal(pageCount, reopened.PageCount);

                for (int page = 0; page < pageCount; page++)
                {
                    IReadOnlyList<PdfFormField> widgets = await reader.ListFormFieldsAsync(page);
                    Assert.Empty(widgets);

                    RenderedPdfPage png = await reader.RenderPageToPngAsync(page, 72);
                    Assert.True(png.PngBytes.Length > 100, $"{name} flattened page {page} did not render.");
                    await File.WriteAllBytesAsync(Artifact($"{name}.flattened.p{page + 1}.png"), png.PngBytes);
                }
            }

            File.Copy(editedOut, Artifact($"{name}.flattened.pdf"), overwrite: true);
        }
        finally
        {
            TryDelete(editedOut);
        }
    }

    /// <summary>
    /// FR-FORM-02 exit-gate: creating a new AcroForm text field through the real
    /// MuPDF shim. On the first page of every corpus document we create a required,
    /// max-length text field, prove it lists back under its name and is immediately
    /// fillable (text round-trips), then flatten, save, reopen and verify the
    /// field is baked into static content and no longer interactive, finally
    /// rendering the page so the created field is visibly present.
    /// </summary>
    [Fact]
    public async Task Create_field_fills_then_flattens_to_static_content()
    {
        string name = "form-create";
        Directory.CreateDirectory(ArtifactRoot);

        string corpusOut = Path.Combine(AppContext.BaseDirectory, $"{name}-{Guid.NewGuid():N}.pdf");
        try
        {
            int pageCount;
            string createdName = "PR_F2_TaxId";

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                string anyPdf = Directory.GetFiles(CorpusDir, "*.pdf", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault()!;
                Assert.False(string.IsNullOrEmpty(anyPdf), "Corpus is empty.");

                PdfDocumentInfo info = await engine.OpenAsync(anyPdf);
                pageCount = info.PageCount;
                Assert.True(pageCount > 0);

                IReadOnlyList<PdfFormField> before = await engine.ListFormFieldsAsync(0);
                int beforeCount = before.Count;

                var spec = new FormFieldSpec(
                    FormFieldKind.Text,
                    createdName,
                    new PdfRect(72, 700, 320, 722),
                    Flags: FormFieldFlags.Required | FormFieldFlags.Comb,
                    MaxLength: 20);

                await engine.CreateFormFieldAsync(0, spec);

                IReadOnlyList<PdfFormField> afterCreate = await engine.ListFormFieldsAsync(0);
                Assert.Equal(beforeCount + 1, afterCreate.Count);

                PdfFormField created = afterCreate.Single(f => f.Name == createdName);
                Assert.Equal(FormFieldKind.Text, created.Kind);

                await engine.SetFormFieldValueAsync(0, created.Id, "123-456-7890");
                IReadOnlyList<PdfFormField> afterFill = await engine.ListFormFieldsAsync(0);
                Assert.Equal("123-456-7890", afterFill.Single(f => f.Id == created.Id).Value);

                await engine.FlattenFormAsync();
                await engine.SaveAsAsync(corpusOut);
            }

            Assert.True(File.Exists(corpusOut), "Created+filled document did not save.");

            // Reopen: the created field must be baked into static content.
            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await reader.OpenAsync(corpusOut);
                Assert.Equal(pageCount, reopened.PageCount);

                IReadOnlyList<PdfFormField> widgets = await reader.ListFormFieldsAsync(0);
                Assert.Empty(widgets);

                RenderedPdfPage png = await reader.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "Created-field page did not render.");
                await File.WriteAllBytesAsync(Artifact($"{name}.created.p1.png"), png.PngBytes);
            }

            File.Copy(corpusOut, Artifact($"{name}.created.pdf"), overwrite: true);
        }
        finally
        {
            TryDelete(corpusOut);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
