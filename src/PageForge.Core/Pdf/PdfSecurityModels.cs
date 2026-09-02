// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Document encryption algorithm for password protected PDFs (FR-SEC-01). The
/// values mirror the <c>PDF_ENCRYPT_*</c> algorithm codes of the MuPDF C API so
/// they cross the shim unchanged. <see cref="Aes256"/> is the recommended choice
/// (RFC 9506 / PDF 2.0 strongest R6 scheme); the RC4 choices exist for
/// compatibility with legacy viewers and should be avoided when possible.
/// </summary>
public enum PdfEncryptionMethod
{
    /// <summary>RC4 with a 40-bit key — legacy only, cryptographically obsolete.</summary>
    Rc4_40 = 2,

    /// <summary>RC4 with a 128-bit key — legacy only.</summary>
    Rc4_128 = 3,

    /// <summary>AES-128 (PDF 1.6 R4 scheme).</summary>
    Aes128 = 4,

    /// <summary>AES-256 (PDF 2.0 R6 scheme). Recommended default.</summary>
    Aes256 = 5,
}

/// <summary>
/// Permissions attached to the owner password of an encrypted PDF (FR-SEC-01).
/// A reader authenticated with the open (user) password is limited to the
/// union of these rights; the owner password bypasses them all. The values are
/// the ISO 32000 permission bits as used by MuPDF (<c>PDF_PERM_*</c>) so they
/// cross the shim unchanged. Note that in PDF 2.0 <see cref="Accessibility"/>
/// is always granted regardless of this flag.
/// </summary>
[Flags]
public enum PdfPermissions
{
    /// <summary>Nothing allowed.</summary>
    None = 0,

    /// <summary>Print at low fidelity (R5/R6 viewers: only with PrintHighQuality disabled).</summary>
    Print = 1 << 2,

    /// <summary>Modify the document contents other than annotations/form fill.</summary>
    Modify = 1 << 3,

    /// <summary>Copy or extract text and graphics.</summary>
    Copy = 1 << 4,

    /// <summary>Add or modify annotations and fill forms.</summary>
    Annotate = 1 << 5,

    /// <summary>Fill in form fields.</summary>
    Form = 1 << 8,

    /// <summary>Extract text for accessibility (always granted in PDF 2.0).</summary>
    Accessibility = 1 << 9,

    /// <summary>Assemble the document (insert/delete/rotate pages).</summary>
    Assemble = 1 << 10,

    /// <summary>Print at high fidelity.</summary>
    PrintHighQuality = 1 << 11,

    /// <summary>Every permission bit the encryption supports is allowed.</summary>
    All = Print | Modify | Copy | Annotate | Form | Accessibility | Assemble | PrintHighQuality,
}

/// <summary>
/// Tuning for an FR-SEC-01 protect (password protection) run. The open (user)
/// password lets anyone open the document with the permissions granted here;
/// the permissions (owner) password restores full control. When only
/// <see cref="OpenPassword"/> is given, the owner password defaults to the same
/// value (single-password semantics: one password both opens and unlocks all
/// rights). Passing <c>null</c> options to <c>SaveEncryptedAsync</c> behaves
/// exactly like AES-256 with all permissions allowed.
/// </summary>
public sealed record PdfProtectionOptions(
    /// <summary>
    /// Open (user) password. May be empty; the document then opens without a
    /// password under the granted permissions and only the owner password
    /// restricts changes. Must not exceed 127 UTF-8 bytes. At least one of the
    /// two passwords must be non-empty.
    /// </summary>
    string? OpenPassword = null,

    /// <summary>
    /// Permissions (owner) password. May be empty: defaults to
    /// <see cref="OpenPassword"/> (equal passwords). Must not exceed 127 UTF-8
    /// bytes when set.
    /// </summary>
    string? PermissionsPassword = null,

    /// <summary>Encryption algorithm; defaults to the recommended <see cref="PdfEncryptionMethod.Aes256"/>.</summary>
    PdfEncryptionMethod Method = PdfEncryptionMethod.Aes256,

    /// <summary>Permissions granted in the file; defaults to <see cref="PdfPermissions.All"/>.</summary>
    PdfPermissions Permissions = PdfPermissions.All)
{
    /// <summary>
    /// Resolves the effective open/owner password pair per the single-password
    /// semantics: an empty owner password becomes the open password. The result
    /// is what crosses to the native layer. Both values are normalized (never
    /// null) and never exceed 127 UTF-8 bytes.
    /// </summary>
    public (string OpenPassword, string PermissionsPassword) ResolveEffectivePasswords()
    {
        string open = OpenPassword ?? string.Empty;
        string owner = PermissionsPassword ?? string.Empty;
        if (owner.Length == 0)
        {
            owner = open;
        }

        return (open, owner);
    }
}