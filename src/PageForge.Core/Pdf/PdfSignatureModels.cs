// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// What to sign and with which credential for an FR-SEC-03 signing run. The
/// signature produced is a standard PDF <c>/Sig</c> field with
/// <c>/SubFilter adbe.pkcs7.detached</c>; the PKCS#7 blob is built by the
/// Windows crypto backend entirely offline, so signing never contacts a
/// service.
/// </summary>
public sealed record PdfSignatureRequest(
    /// <summary>
    /// AcroForm field name for the new signature widget. Required.
    /// </summary>
    string FieldName,

    /// <summary>
    /// Where the signature widget sits on the page, in PDF points with the
    /// origin at the bottom-left. Required.
    /// </summary>
    PdfRect Bounds,

    /// <summary>
    /// Path to the signer's PKCS#12 (<c>.pfx</c>) file. Required, and it must
    /// contain a certificate with a private key — that leaf is what signs.
    /// </summary>
    string CertificatePath,

    /// <summary>Password protecting the PKCS#12 file; <c>null</c> when it has none.</summary>
    string? CertificatePassword = null,

    /// <summary>Optional human-readable reason recorded in the signature dictionary.</summary>
    string? Reason = null,

    /// <summary>Optional location recorded in the signature dictionary.</summary>
    string? Location = null);

/// <summary>
/// One AcroForm signature field found in a document, with the result of
/// verifying it (FR-SEC-03). Verification runs offline against the operating
/// system's trust stores, so no certificate bundle ships with PageForge — and
/// a certificate the machine does not trust reports as untrusted rather than
/// failing the whole listing.
/// </summary>
/// <remarks>
/// An unsigned field is still listed: a document can carry an empty signature
/// widget waiting to be signed, and hiding it would make "no signatures" and
/// "one blank signature field" look identical.
/// </remarks>
public sealed record PdfSignature(
    /// <summary>Zero-based index over every signature field in the document, in page order.</summary>
    int Index,

    /// <summary>Zero-based index of the page the widget sits on.</summary>
    int PageIndex,

    /// <summary>The AcroForm field name.</summary>
    string FieldName,

    /// <summary>Widget bounds in PDF points.</summary>
    PdfRect Bounds,

    /// <summary>Whether the field actually carries a signature, as opposed to being an empty widget.</summary>
    bool IsSigned,

    /// <summary>
    /// MuPDF's description of the digest check ("OK" when the signed bytes are
    /// unchanged), empty for an unsigned field. Kept as the library's own text
    /// rather than mapped to an enum, so a status this build has never seen is
    /// still reported verbatim instead of collapsing into "unknown".
    /// </summary>
    string DigestStatus,

    /// <summary>
    /// MuPDF's description of the certificate check ("OK" when the chain
    /// validates against the OS trust stores), empty for an unsigned field.
    /// A self-signed certificate reports as such: valid digest, untrusted
    /// issuer.
    /// </summary>
    string CertificateStatus,

    /// <summary>
    /// Formatted distinguished name of the signer ("cn=..., o=..."), empty when
    /// the signer could not be determined.
    /// </summary>
    string SignerName)
{
    /// <summary>
    /// The text both status fields carry when the check passed. It is MuPDF's
    /// own <c>pdf_signature_error_description</c> wording for "no error".
    /// </summary>
    public const string StatusOk = "OK";

    /// <summary>
    /// Whether the document bytes this signature covers are unchanged since it
    /// was applied — the question "has this been tampered with?". It says
    /// nothing about whether the signer is trusted, which is
    /// <see cref="IsTrusted"/>. The two are deliberately separate: a
    /// self-signed certificate detects tampering perfectly well while proving
    /// nothing about who signed.
    /// </summary>
    public bool IsDigestIntact => IsSigned && string.Equals(DigestStatus, StatusOk, StringComparison.Ordinal);

    /// <summary>
    /// Whether the signer's certificate chain validates against the operating
    /// system trust stores.
    /// </summary>
    public bool IsTrusted => IsSigned && string.Equals(CertificateStatus, StatusOk, StringComparison.Ordinal);
}
