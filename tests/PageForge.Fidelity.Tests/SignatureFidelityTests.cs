// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// Digital signing and verification against the real shim (FR-SEC-03).
///
/// The native signer has existed and been compiled into the shipped DLL for
/// some time, but nothing managed could reach it: there was no P/Invoke and no
/// command. The CHANGELOG once claimed the feature as shipped while describing
/// only the C code. This suite is what makes the claim checkable — it goes
/// through the same engine surface the shell will call.
///
/// Every assertion is made in both directions, because the failure this
/// project keeps producing is a check that cannot fail. A verifier hard-coded
/// to report "OK" would pass a sign-then-verify test forever, so the tamper
/// case below edits a signed document and requires the verdict to change.
/// </summary>
public sealed class SignatureFidelityTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    /// <summary>
    /// Builds a throwaway self-signed PKCS#12 with a private key. Generated per
    /// run rather than committed: a checked-in certificate expires and one day
    /// fails the suite for a reason that has nothing to do with signing, and a
    /// private key in the repository is a private key in the repository even
    /// when it is only a test's.
    /// </summary>
    private static string WriteTestCertificate(string password)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=PageForge Fidelity Test Signer, O=LiVi Software Company",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddDays(30));

        string path = Path.Combine(AppContext.BaseDirectory, $"signer-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    [Fact]
    public async Task An_unsigned_document_reports_no_signatures()
    {
        await using MuPdfEngine engine = MuPdfEngine.Create();
        await engine.OpenAsync(Fixture("contract-multipage.pdf"));

        IReadOnlyList<PdfSignature> signatures = await engine.ListSignaturesAsync();

        Assert.Empty(signatures);
    }

    [Fact]
    public async Task Signing_then_saving_produces_a_signature_that_verifies()
    {
        const string password = "fidelity-test";
        string certificatePath = WriteTestCertificate(password);
        string signed = Path.Combine(AppContext.BaseDirectory, $"signed-{Guid.NewGuid():N}.pdf");

        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(Fixture("contract-multipage.pdf"));

                await engine.SignAsync(
                    0,
                    new PdfSignatureRequest(
                        "PageForgeSignature1",
                        new PdfRect(72, 640, 272, 700),
                        certificatePath,
                        password,
                        Reason: "Fidelity proof",
                        Location: "Test suite"));

                // The digest is computed over the saved byte range, so the
                // signature only becomes real here. An incremental save is used
                // because that is what the interface documents as the correct
                // save for a signed document.
                await engine.SaveIncrementalAsync(signed);
            }

            Assert.True(File.Exists(signed), "Signing produced no file.");

            await using MuPdfEngine verifier = MuPdfEngine.Create();
            await verifier.OpenAsync(signed);

            PdfSignature signature = Assert.Single(await verifier.ListSignaturesAsync());

            Assert.Equal("PageForgeSignature1", signature.FieldName);
            Assert.Equal(0, signature.PageIndex);
            Assert.True(
                signature.IsSigned,
                "The field was created but carries no signature, so the sign call built an " +
                "empty widget rather than signing it.");
            Assert.True(
                signature.IsDigestIntact,
                "The signature does not verify against the document it was just applied to " +
                $"(digest: '{signature.DigestStatus}'). Nothing about this document has " +
                "changed since signing, so a failure here means the digest is computed over " +
                "the wrong bytes.");
            Assert.Contains(
                "PageForge Fidelity Test Signer", signature.SignerName, StringComparison.OrdinalIgnoreCase);

            // The certificate is self-signed and deliberately not installed in
            // any trust store, so the chain must NOT validate. Asserting this
            // is what proves the certificate check is a real check: if
            // IsTrusted were true here, it would be true for anything.
            Assert.False(
                signature.IsTrusted,
                "An untrusted self-signed certificate reported as trusted (certificate: " +
                $"'{signature.CertificateStatus}'), so the chain is not actually being " +
                "validated and no certificate would ever be rejected.");
        }
        finally
        {
            TryDelete(certificatePath);
            TryDelete(signed);
        }
    }

    [Fact]
    public async Task Editing_a_signed_document_invalidates_its_signature()
    {
        const string password = "fidelity-test";
        string certificatePath = WriteTestCertificate(password);
        string signed = Path.Combine(AppContext.BaseDirectory, $"signed-{Guid.NewGuid():N}.pdf");
        string tampered = Path.Combine(AppContext.BaseDirectory, $"tampered-{Guid.NewGuid():N}.pdf");

        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(Fixture("contract-multipage.pdf"));
                await engine.SignAsync(
                    0,
                    new PdfSignatureRequest(
                        "PageForgeSignature1",
                        new PdfRect(72, 640, 272, 700),
                        certificatePath,
                        password));
                await engine.SaveIncrementalAsync(signed);
            }

            // Rewrite the signed file in full after adding content. A full save
            // renumbers objects, which is exactly the change a signature exists
            // to detect, and it is also the accident a shell would commit by
            // calling the ordinary save on a signed document.
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(signed);
                await engine.AddAnnotationAsync(
                    0,
                    new AnnotBuildSpec
                    {
                        Type = AnnotationType.Text,
                        X0 = 72,
                        Y0 = 500,
                        X1 = 200,
                        Y1 = 520,
                        Contents = "Added after signing",
                    });
                await engine.SaveAsAsync(tampered);
            }

            await using MuPdfEngine verifier = MuPdfEngine.Create();
            await verifier.OpenAsync(tampered);

            PdfSignature signature = Assert.Single(await verifier.ListSignaturesAsync());

            Assert.True(signature.IsSigned, "The signature field was lost entirely by the rewrite.");
            Assert.False(
                signature.IsDigestIntact,
                "A document changed after signing still verifies as intact. The verification " +
                "is therefore not reading the document's bytes, and it would report a forged " +
                "contract as genuine — the one failure this feature must not have.");
        }
        finally
        {
            TryDelete(certificatePath);
            TryDelete(signed);
            TryDelete(tampered);
        }
    }

    [Fact]
    public async Task A_wrong_certificate_password_fails_loudly_and_signs_nothing()
    {
        string certificatePath = WriteTestCertificate("the-right-one");
        string output = Path.Combine(AppContext.BaseDirectory, $"unsigned-{Guid.NewGuid():N}.pdf");

        try
        {
            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(Fixture("contract-multipage.pdf"));

            await Assert.ThrowsAnyAsync<Exception>(async () =>
                await engine.SignAsync(
                    0,
                    new PdfSignatureRequest(
                        "PageForgeSignature1",
                        new PdfRect(72, 640, 272, 700),
                        certificatePath,
                        "the-wrong-one")));

            // A failed sign must leave nothing signed behind. Half a signature
            // written to the user's file is worse than a refusal: it looks
            // signed.
            await engine.SaveIncrementalAsync(output);

            await using MuPdfEngine verifier = MuPdfEngine.Create();
            await verifier.OpenAsync(output);

            Assert.DoesNotContain(
                await verifier.ListSignaturesAsync(),
                s => s.IsSigned);
        }
        finally
        {
            TryDelete(certificatePath);
            TryDelete(output);
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
