// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-SEC-03 unit tests for <see cref="PdfSigningService"/>: the validation and
/// the choice of save that do not depend on the native engine. Cryptography is
/// proved against the real shim in the fidelity suite; what is asserted here is
/// the part a shell would otherwise have to get right on its own.
/// </summary>
public sealed class PdfSigningTests : IDisposable
{
    private readonly string _output = Path.Combine(
        Path.GetTempPath(), $"pageforge-signed-test-{Guid.NewGuid():N}.pdf");

    private readonly string _certificate = Path.Combine(
        Path.GetTempPath(), $"pageforge-signer-test-{Guid.NewGuid():N}.pfx");

    public PdfSigningTests()
    {
        // The service only checks that the certificate exists; loading it is the
        // engine's job, so a placeholder is enough and keeps the unit tests free
        // of key generation.
        File.WriteAllText(_certificate, "placeholder");
    }

    public void Dispose()
    {
        foreach (string path in new[] { _output, _certificate })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private PdfSignatureRequest Request(string fieldName = "Signature1")
        => new(fieldName, new PdfRect(72, 640, 272, 700), _certificate, "pw");

    [Fact]
    public async Task SignAsync_signs_the_page_and_saves_incrementally()
    {
        var engine = new FakePdfEngine(3);

        await PdfSigningService.SignAsync(engine, 1, Request(), _output);

        (int pageIndex, PdfSignatureRequest request) = Assert.Single(engine.Signings);
        Assert.Equal(1, pageIndex);
        Assert.Equal("Signature1", request.FieldName);

        // The save must be the incremental one. A full rewrite also produces a
        // file that opens perfectly, so nothing downstream would notice that it
        // had invalidated every signature already in the document.
        Assert.Equal(_output, engine.LastIncrementalSave);
        Assert.True(File.Exists(_output));
    }

    [Fact]
    public async Task SignAsync_refuses_to_overwrite_an_existing_file()
    {
        var engine = new FakePdfEngine(1);
        await File.WriteAllTextAsync(_output, "not mine to destroy");

        await Assert.ThrowsAsync<IOException>(async () =>
            await PdfSigningService.SignAsync(engine, 0, Request(), _output));

        Assert.Empty(engine.Signings);
        Assert.Equal("not mine to destroy", await File.ReadAllTextAsync(_output));
    }

    [Fact]
    public async Task SignAsync_rejects_a_missing_certificate_without_touching_the_document()
    {
        var engine = new FakePdfEngine(1);
        var request = new PdfSignatureRequest(
            "Signature1",
            new PdfRect(72, 640, 272, 700),
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.pfx"));

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await PdfSigningService.SignAsync(engine, 0, request, _output));

        Assert.Empty(engine.Signings);
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task SignAsync_rejects_a_field_name_the_document_already_uses()
    {
        var engine = new FakePdfEngine(1);
        engine.Signatures.Add(new PdfSignature(
            0, 0, "Signature1", new PdfRect(10, 10, 20, 20), true, "OK", "OK", "cn=Someone"));

        ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await PdfSigningService.SignAsync(engine, 0, Request("Signature1"), _output));

        Assert.Contains("already has a signature field", error.Message, StringComparison.Ordinal);
        Assert.Empty(engine.Signings);
    }

    [Fact]
    public async Task SignAsync_rejects_a_zero_area_signature()
    {
        var engine = new FakePdfEngine(1);
        var request = new PdfSignatureRequest(
            "Signature1", new PdfRect(72, 640, 72, 640), _certificate);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await PdfSigningService.SignAsync(engine, 0, request, _output));

        Assert.Empty(engine.Signings);
    }

    [Theory]
    [InlineData(true, "OK", "OK", "valid, trusted certificate")]
    [InlineData(true, "OK", "Self-signed certificate.", "not trusted")]
    [InlineData(true, "Signature invalidated by change to document.", "OK", "INVALID")]
    [InlineData(false, "", "", "not signed yet")]
    public void Describe_distinguishes_the_verdicts_that_mean_different_things(
        bool isSigned, string digest, string certificate, string expected)
    {
        // A tampered document and an untrusted certificate are different
        // problems with different answers, and "unchanged but untrusted" is the
        // normal outcome for a self-signed certificate. Collapsing any of these
        // into a single "invalid" would tell the user the wrong thing.
        string described = PdfSigningService.Describe(new PdfSignature(
            0, 0, "Signature1", new PdfRect(10, 10, 20, 20),
            isSigned, digest, certificate, "cn=Someone"));

        Assert.Contains(expected, described, StringComparison.Ordinal);
    }
}
