// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns a high-level FR-SEC-01 operation (password-protect the
/// open document by writing an encrypted copy, or verify a password against the
/// open document) into a call on the <see cref="IPdfEngine"/> seam, doing the
/// validation that does not depend on the native engine: mutations all happen on
/// the copy, never the open document.
/// </summary>
public static class PdfSecurityService
{
    /// <summary>
    /// The PDF password field is a 128-byte (UTF-8) string; MuPDF's write
    /// options truncate at that. Anything longer is invalid.
    /// </summary>
    public const int MaxPasswordUtf8Bytes = 127;

    /// <summary>
    /// Writes a freshly encrypted copy of the open document to
    /// <paramref name="outputPath"/> (FR-SEC-01). The output must not already
    /// exist (a protection pass never overwrites anything, including the open
    /// document itself, which the engine rejects too when paths compare equal).
    /// At least one password must be supplied and each password must fit the PDF
    /// string limit (127 UTF-8 bytes).
    /// </summary>
    public static ValueTask ProtectAsync(
        IPdfEngine engine,
        string outputPath,
        PdfProtectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);

        if (File.Exists(outputPath))
        {
            throw new IOException(
                $"The protected output '{outputPath}' already exists; choose a new path so nothing is overwritten.");
        }

        PdfProtectionOptions effective = options ?? new PdfProtectionOptions();
        (string open, string owner) = effective.ResolveEffectivePasswords();

        if (open.Length == 0 && owner.Length == 0)
        {
            throw new ArgumentException(
                "At least one password is required to protect a document.", nameof(options));
        }

        ValidatePassword(open, nameof(options));
        ValidatePassword(owner, nameof(options));

        return engine.SaveEncryptedAsync(
            Path.GetFullPath(outputPath),
            effective,
            cancellationToken);
    }

    /// <summary>
    /// Reports whether <paramref name="password"/> opens the open document
    /// (FR-SEC-01). Convenience for shells verifying a password before showing
    /// it as the unlocking secret; the engine ignores the value for documents
    /// that need no password (they open freely).
    /// </summary>
    public static ValueTask<bool> AuthenticateAsync(
        IPdfEngine engine,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.AuthenticateAsync(password, cancellationToken);
    }

    private static void ValidatePassword(string value, string paramName)
    {
        if (Encoding.UTF8.GetByteCount(value) > MaxPasswordUtf8Bytes)
        {
            throw new ArgumentException(
                "A PDF password must fit the 127-byte (UTF-8) string field; the supplied " +
                $"password is {Encoding.UTF8.GetByteCount(value)} bytes.", paramName);
        }
    }
}