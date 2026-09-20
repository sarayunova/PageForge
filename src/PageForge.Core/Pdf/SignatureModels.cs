// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// What to sign a document with, and where the visible signature goes
/// (FR-SEC-03).
/// </summary>
/// <param name="FieldName">Name of the signature field to create.</param>
/// <param name="Rect">The widget's rectangle in PDF points.</param>
/// <param name="Pkcs12Path">A PKCS#12/PFX file holding the signing certificate
/// AND its private key. A certificate without a key cannot sign.</param>
/// <param name="Pkcs12Password">Password for the PKCS#12, when it has one.</param>
/// <param name="Reason">Optional "reason for signing" recorded in the signature.</param>
/// <param name="Location">Optional signing location recorded in the signature.</param>
public sealed record PdfSignatureSpec(
    string FieldName,
    PdfRect Rect,
    string Pkcs12Path,
    string? Pkcs12Password = null,
    string? Reason = null,
    string? Location = null);

/// <summary>
/// One signature field found in a document, with what verification made of it.
///
/// <paramref name="DigestStatus"/> and <paramref name="CertificateStatus"/> are
/// two separate questions and are deliberately kept apart. The digest answers
/// "has the signed content been altered"; the certificate answers "is the signer
/// trusted by this machine". A self-signed certificate yields an intact digest
/// and an untrusted chain, which is a meaningful and common state - collapsing
/// the two into one "valid" flag would report such a document as simply invalid.
/// </summary>
/// <param name="Index">Position among the document's signature fields.</param>
/// <param name="PageIndex">0-based page the widget sits on.</param>
/// <param name="FieldName">The field's name.</param>
/// <param name="Rect">The widget's rectangle in PDF points.</param>
/// <param name="IsSigned">False for a signature field that is present but empty.</param>
/// <param name="DigestStatus">MuPDF's description of the digest check; "OK" when
/// the signed bytes are unaltered. Empty for an unsigned field.</param>
/// <param name="CertificateStatus">MuPDF's description of the certificate check;
/// "OK" when the chain is trusted. Empty for an unsigned field.</param>
/// <param name="Signer">The signer's distinguished name, when known.</param>
public sealed record PdfSignatureField(
    int Index,
    int PageIndex,
    string FieldName,
    PdfRect Rect,
    bool IsSigned,
    string DigestStatus,
    string CertificateStatus,
    string Signer)
{
    /// <summary>The signed bytes are unaltered. Says nothing about whether the
    /// signer is trusted - see <see cref="CertificateIsTrusted"/>.</summary>
    public bool DigestIsIntact =>
        IsSigned && DigestStatus.Equals("OK", StringComparison.Ordinal);

    /// <summary>The signer's certificate chains to a root this machine trusts.
    /// False for a self-signed certificate even when the document itself is
    /// untouched.</summary>
    public bool CertificateIsTrusted =>
        IsSigned && CertificateStatus.Equals("OK", StringComparison.Ordinal);
}
