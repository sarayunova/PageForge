// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;
using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-SEC-01 unit tests: the <see cref="PdfSecurityService"/> entry point
/// drives the engine seam to write a freshly encrypted copy of the open
/// document and to verify passwords against it. Password validation and
/// single-password resolution are pure and asserted here; all engine calls run
/// against the fake with no native dependency.
/// </summary>
public sealed class PdfSecurityTests : IDisposable
{
    private readonly string _output = Path.Combine(
        Path.GetTempPath(), $"pageforge-encrypt-test-{Guid.NewGuid():N}.pdf");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_output))
            {
                File.Delete(_output);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ProtectAsync_writes_an_encrypted_copy_and_forwards_options()
    {
        var engine = new FakePdfEngine(2);
        var options = new PdfProtectionOptions(
            OpenPassword: "open",
            PermissionsPassword: "owner",
            Method: PdfEncryptionMethod.Aes128,
            Permissions: PdfPermissions.Print | PdfPermissions.Copy);

        await PdfSecurityService.ProtectAsync(engine, _output, options);

        Assert.True(File.Exists(_output), "The engine should leave an encrypted copy on disk.");
        Assert.Equal(Path.GetFullPath(_output), engine.LastEncrypt?.OutputPath);
        Assert.Equal(options, engine.LastEncrypt?.Options);
        Assert.Equal(_output, Assert.Single(engine.EncryptedOutputs));
    }

    [Fact]
    public async Task ProtectAsync_uses_aes256_all_permissions_by_default()
    {
        var engine = new FakePdfEngine(1);
        var options = new PdfProtectionOptions(OpenPassword: "open");

        await PdfSecurityService.ProtectAsync(engine, _output, options);

        PdfProtectionOptions? forwarded = engine.LastEncrypt?.Options;
        Assert.NotNull(forwarded);
        Assert.Equal(PdfEncryptionMethod.Aes256, forwarded!.Method);
        Assert.Equal(PdfPermissions.All, forwarded.Permissions);
    }

    [Fact]
    public async Task ProtectAsync_requires_at_least_one_password()
    {
        var engine = new FakePdfEngine(1);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PdfSecurityService.ProtectAsync(engine, _output, new PdfProtectionOptions()).AsTask());
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task ProtectAsync_rejects_an_owner_password_over_the_pdf_limit()
    {
        var engine = new FakePdfEngine(1);
        string tooLong = new string('\u00e9', 65); // 65 * 2 UTF-8 bytes = 130 > 127

        await Assert.ThrowsAsync<ArgumentException>(() =>
            PdfSecurityService.ProtectAsync(
                engine, _output, new PdfProtectionOptions(OpenPassword: "ok", PermissionsPassword: tooLong))
                .AsTask());
        Assert.False(File.Exists(_output));
    }

    [Fact]
    public async Task ProtectAsync_rejects_an_existing_output_path()
    {
        var engine = new FakePdfEngine(1);
        await File.WriteAllTextAsync(_output, "existing");

        await Assert.ThrowsAsync<IOException>(() =>
            PdfSecurityService.ProtectAsync(
                engine, _output, new PdfProtectionOptions(OpenPassword: "open")).AsTask());
    }

    [Fact]
    public async Task ProtectAsync_propagates_engine_failures()
    {
        var engine = new FakePdfEngine(1);
        engine.OnEncrypt = _ => throw new InvalidOperationException("encrypt engine failed");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PdfSecurityService.ProtectAsync(
                engine, _output, new PdfProtectionOptions(OpenPassword: "open")).AsTask());
        Assert.False(File.Exists(_output), "No artifact should remain after a failure.");
    }

    [Fact]
    public async Task AuthenticateAsync_forwards_to_the_engine()
    {
        var engine = new FakePdfEngine(1);

        Assert.True(await PdfSecurityService.AuthenticateAsync(engine, "anything"));

        engine.OnAuthenticate = () => false;
        Assert.False(await PdfSecurityService.AuthenticateAsync(engine, "wrong"));
    }

    [Theory]
    [InlineData("p\ud83d\ude00w", "p\ud83d\ude00w")]  // only open password given -> owner = open (multi-byte passthrough)
    [InlineData("open", "open")]                      // only open password given -> owner = open
    public void Empty_owner_password_falls_back_to_the_open_password(string open, string expectedOwner)
    {
        var options = new PdfProtectionOptions(OpenPassword: open);
        (string resolvedOpen, string resolvedOwner) = options.ResolveEffectivePasswords();

        Assert.Equal(open, resolvedOpen);
        Assert.Equal(expectedOwner, resolvedOwner);
    }

    [Fact]
    public void Distinct_owner_password_passes_through()
    {
        var options = new PdfProtectionOptions(OpenPassword: "open", PermissionsPassword: "owner");
        (string open, string owner) = options.ResolveEffectivePasswords();

        Assert.Equal("open", open);
        Assert.Equal("owner", owner);
    }

    [Fact]
    public void Owner_only_document_opens_freely_but_restricts_to_owner()
    {
        var options = new PdfProtectionOptions(OpenPassword: "", PermissionsPassword: "owner");
        (string open, string owner) = options.ResolveEffectivePasswords();

        Assert.Equal(string.Empty, open);
        Assert.Equal("owner", owner);
    }

    [Fact]
    public void PdfPermission_values_match_the_pdf_specification_bits()
    {
        Assert.Equal(1 << 2, (int)PdfPermissions.Print);
        Assert.Equal(1 << 3, (int)PdfPermissions.Modify);
        Assert.Equal(1 << 4, (int)PdfPermissions.Copy);
        Assert.Equal(1 << 5, (int)PdfPermissions.Annotate);
        Assert.Equal(1 << 8, (int)PdfPermissions.Form);
        Assert.Equal(1 << 9, (int)PdfPermissions.Accessibility);
        Assert.Equal(1 << 10, (int)PdfPermissions.Assemble);
        Assert.Equal(1 << 11, (int)PdfPermissions.PrintHighQuality);
        Assert.Equal(0xF3C, (int)PdfPermissions.All);
    }

    [Fact]
    public void Password_validation_is_utf8_bytes_not_characters()
    {
        string hundredEAcute = new string('\u00e9', 100); // 200 UTF-8 bytes
        Assert.Equal(200, Encoding.UTF8.GetByteCount(hundredEAcute));
        Assert.True(Encoding.UTF8.GetByteCount(hundredEAcute) > PdfSecurityService.MaxPasswordUtf8Bytes);
    }
}