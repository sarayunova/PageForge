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
/// FR-SEC-03 local signing, against the real shim.
///
/// The native signer had existed and been compiled into the shipped DLL for
/// some time with no managed binding and no caller, so none of it had ever run
/// from this side. That is why the assertions are about what the signature IS
/// rather than that the call returned: a signing path producing a field nobody
/// can verify is the failure worth catching.
///
/// The certificate is generated in-process. Committing a PKCS#12 with a private
/// key would be a credential in source control, and one that expires and quietly
/// takes the suite with it.
/// </summary>
public sealed class SignatureFidelityTests
{
    private const string CertPassword = "pageforge-test";

    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Signing_produces_a_signature_that_verifies()
    {
        string source = Fixture("contract-multipage.pdf");
        string pfx = Path.Combine(AppContext.BaseDirectory, $"sign-{Guid.NewGuid():N}.pfx");
        string signed = Path.Combine(AppContext.BaseDirectory, $"signed-{Guid.NewGuid():N}.pdf");

        try
        {
            await File.WriteAllBytesAsync(pfx, CreateSelfSignedPkcs12());

            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(source);

                Assert.Empty(await engine.ListSignaturesAsync());

                await engine.SignAsync(
                    0,
                    new PdfSignatureSpec(
                        "PageForgeTestSignature",
                        new PdfRect(72, 600, 272, 660),
                        pfx,
                        CertPassword,
                        Reason: "fidelity proof",
                        Location: "CI"));

                // Incremental, not a full rewrite: a signature covers a byte range
                // of the file, so renumbering the file breaks it. SaveAsAsync here
                // would produce a document whose signature reports as invalidated,
                // which is exactly the mistake this API shape exists to prevent.
                await engine.SaveIncrementalAsync(signed);
            }

            Assert.True(File.Exists(signed), "The signed document was not written.");

            await using (MuPdfEngine reader = MuPdfEngine.Create())
            {
                await reader.OpenAsync(signed);

                PdfSignatureField field = Assert.Single(await reader.ListSignaturesAsync());
                Assert.Equal("PageForgeTestSignature", field.FieldName);
                Assert.True(field.IsSigned, "The field was created but carries no signature.");

                // The digest is the assertion that matters: it says the signed
                // bytes are intact. The certificate chain is reported separately
                // and deliberately NOT asserted as trusted - this certificate is
                // self-signed and trusted by nothing, so requiring a trusted chain
                // would assert something about the machine's certificate stores
                // rather than about PageForge.
                Assert.True(
                    field.DigestIsIntact,
                    $"The signature's digest did not verify: '{field.DigestStatus}'. The document " +
                    "was signed and then written in a way that altered the bytes it signed.");

                Assert.False(string.IsNullOrEmpty(field.Signer), "No signer was reported.");
            }
        }
        finally
        {
            TryDelete(pfx);
            TryDelete(signed);
        }
    }

    /// <summary>A throwaway signing certificate with its private key.</summary>
    private static byte[] CreateSelfSignedPkcs12()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=PageForge Fidelity Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));

        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        return certificate.Export(X509ContentType.Pkcs12, CertPassword);
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
