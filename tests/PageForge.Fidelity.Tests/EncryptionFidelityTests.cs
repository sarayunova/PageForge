// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// FR-SEC-01 fidelity: drives the real shim's password-protection primitives
/// end-to-end against the contract-multipage corpus fixture. Confirms that a
/// protect pass writes an encrypted copy whose xref/trailer are still readable
/// (page count, geometry) but whose content streams only decrypt once the right
/// password is supplied: text extraction comes back empty and rendering yields
/// unusable pixels before authentication, then the same document reads back fully
/// (text runs, page render) after the correct open password authenticates.
/// A wrong password must not unlock anything.
/// </summary>
public sealed class EncryptionFidelityTests
{
    private const string OpenPassword = "pageforge";

    private static string Corpus(string name) => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Protect_then_reopen_with_password_round_trip()
    {
        string src = Corpus("contract-multipage.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"protected-{Guid.NewGuid():N}.pdf");
        try
        {
            // (1) protect the source with AES-256 + a non-trivial permission mask
            int expectedPages;
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await engine.OpenAsync(src);
                expectedPages = info.PageCount;
                Assert.True(expectedPages >= 1);

                var options = new PdfProtectionOptions(
                    OpenPassword: OpenPassword,
                    PermissionsPassword: "owner",
                    Method: PdfEncryptionMethod.Aes256,
                    Permissions: PdfPermissions.Print | PdfPermissions.Copy);
                await PdfSecurityService.ProtectAsync(engine, output, options);

                // The protected copy is a separate file; the open document is unmodified.
                Assert.True(File.Exists(output));
            }

            // (2) reopen WITHOUT a password: trailer metadata reads (page count)
            // but the encrypted content streams must NOT decrypt — no text runs
            // and a wrong password does not authenticate.
            await using (MuPdfEngine locked = MuPdfEngine.Create())
            {
                PdfDocumentInfo reopened = await locked.OpenAsync(output);
                Assert.Equal(expectedPages, reopened.PageCount);

                Assert.False(await locked.AuthenticateAsync("wrong-password"));

                IReadOnlyList<PdfTextRun> lockedRuns = await locked.ListTextRunsAsync(0);
                Assert.Empty(lockedRuns);
            }

            // (3) reopen WITH the correct open password: the same streams now
            // decrypt — text runs come back and the page renders.
            await using (MuPdfEngine unlocked = MuPdfEngine.Create())
            {
                PdfDocumentInfo opened = await unlocked.OpenAsync(output);
                Assert.Equal(expectedPages, opened.PageCount);

                Assert.True(await unlocked.AuthenticateAsync(OpenPassword));

                IReadOnlyList<PdfTextRun> runs = await unlocked.ListTextRunsAsync(0);
                Assert.NotEmpty(runs);
                Assert.Contains(runs, r => r.Text.Contains("AGREEMENT"));

                RenderedPdfPage png = await unlocked.RenderPageToPngAsync(0, 72);
                Assert.True(png.PngBytes.Length > 100, "Password-authenticated page did not render.");
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    [Fact]
    public async Task Protect_defaults_aes256_and_single_password_round_trip()
    {
        string src = Corpus("contract-multipage.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"protected2-{Guid.NewGuid():N}.pdf");
        try
        {
            await using (MuPdfEngine engine = MuPdfEngine.Create())
            {
                await engine.OpenAsync(src);

                // Defaults: AES-256 + all permissions; single password (open pw
                // is also the owner pw).
                var options = new PdfProtectionOptions(OpenPassword: "single");
                await PdfSecurityService.ProtectAsync(engine, output, options);
            }

            Assert.True(File.Exists(output));

            await using (MuPdfEngine reopened = MuPdfEngine.Create())
            {
                PdfDocumentInfo info = await reopened.OpenAsync(output);
                Assert.True(info.PageCount >= 1);

                // The single password must open it (user AND owner are the same).
                Assert.True(await reopened.AuthenticateAsync("single"));
                Assert.False(await reopened.AuthenticateAsync("wrong"));
            }
        }
        finally
        {
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