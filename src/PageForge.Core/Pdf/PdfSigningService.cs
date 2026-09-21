// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns a high-level FR-SEC-03 operation — sign the open
/// document and write the signed copy, or list what a document is already
/// signed with — into calls on the <see cref="IPdfEngine"/> seam, doing the
/// validation that does not depend on the native engine.
///
/// Two rules the shell would otherwise have to remember are enforced here.
/// The signed result is always a NEW file, so a failed signing run cannot
/// damage the document being signed. And the write is always incremental,
/// because a full rewrite renumbers objects and invalidates every signature
/// the document already carried — silently, since the file still opens.
/// </summary>
public static class PdfSigningService
{
    /// <summary>
    /// Where a signature goes when the user has not placed one: a 200x60 point
    /// box above the bottom-left margin. It is also the box placement starts
    /// from, so "I did not choose" and "I chose by dragging" produce the same
    /// kind of result rather than following two different conventions.
    /// </summary>
    public static readonly PdfRect DefaultBounds = new(72, 72, 272, 132);

    /// <summary>
    /// Signs the open document on <paramref name="pageIndex"/> and writes the
    /// signed copy to <paramref name="outputPath"/> (FR-SEC-03). The open
    /// document is left on disk untouched.
    ///
    /// Rejects an output path that already exists, a missing certificate file,
    /// an empty field name, a degenerate widget rectangle, and a field name the
    /// document is already using — the last because the native layer's refusal
    /// arrives as a generic signing failure that reads like a broken
    /// certificate.
    /// </summary>
    public static async ValueTask SignAsync(
        IPdfEngine engine,
        int pageIndex,
        PdfSignatureRequest request,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);

        if (string.IsNullOrWhiteSpace(request.FieldName))
        {
            throw new ArgumentException(
                "A signature field name is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CertificatePath))
        {
            throw new ArgumentException(
                "A signing certificate (PKCS#12 .pfx) is required.", nameof(request));
        }

        if (!File.Exists(request.CertificatePath))
        {
            throw new FileNotFoundException(
                "The signing certificate was not found.", request.CertificatePath);
        }

        if (request.Bounds.X1 <= request.Bounds.X0 || request.Bounds.Y1 <= request.Bounds.Y0)
        {
            throw new ArgumentException(
                "The signature must have a positive width and height.", nameof(request));
        }

        if (File.Exists(outputPath))
        {
            throw new IOException(
                $"The signed output '{outputPath}' already exists; choose a new path so nothing is overwritten.");
        }

        IReadOnlyList<PdfSignature> existing =
            await engine.ListSignaturesAsync(cancellationToken).ConfigureAwait(false);
        foreach (PdfSignature signature in existing)
        {
            if (string.Equals(signature.FieldName, request.FieldName, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"The document already has a signature field named '{request.FieldName}'; " +
                    "choose a different name.", nameof(request));
            }
        }

        await engine.SignAsync(pageIndex, request, cancellationToken).ConfigureAwait(false);

        // Incremental, always: a full rewrite would invalidate any signature the
        // document already carried, and the damage is invisible until someone
        // verifies it.
        await engine.SaveIncrementalAsync(
            Path.GetFullPath(outputPath), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Lists the signature fields of the open document with their verification
    /// verdicts (FR-SEC-03). Non-mutating.
    /// </summary>
    public static ValueTask<IReadOnlyList<PdfSignature>> ListAsync(
        IPdfEngine engine,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        return engine.ListSignaturesAsync(cancellationToken);
    }

    /// <summary>
    /// One line per signature, for a status bar or a listing dialog. Kept here
    /// rather than in the shell so the wording of a verdict is decided once:
    /// an intact digest with an untrusted certificate is a real and common
    /// outcome (a self-signed certificate), and it must read as neither
    /// "valid" nor "broken".
    /// </summary>
    public static string Describe(PdfSignature signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        if (!signature.IsSigned)
        {
            return "not signed yet";
        }

        string who = string.IsNullOrWhiteSpace(signature.SignerName)
            ? "unknown signer"
            : signature.SignerName;

        if (!signature.IsDigestIntact)
        {
            return $"INVALID — the document changed after signing ({signature.DigestStatus}); {who}";
        }

        return signature.IsTrusted
            ? $"valid, trusted certificate; {who}"
            : "unchanged since signing, but the certificate is not trusted " +
              $"({signature.CertificateStatus}); {who}";
    }
}
