// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.
//
// PageForge FR-SEC-03 digital-signature crypto backend.
//
// MuPDF's `pdf_pkcs7_signer` / `pdf_pkcs7_verifier` are pluggable vtables; the
// stock build only ships a stub (`pkcs7-openssl.c` without HAVE_LIBCRYPTO
// throws "No OpenSSL support"). This module implements those two vtables over
// the Windows crypto APIs that ship with the OS (crypt32 / bcrypt):
//
//   - signer:   loads a PKCS#12 (.pfx/.p12) file via PFXImportCertStore, and
//               produces a detached PKCS#7/CMS SignedData with CryptSignMessage.
//   - verifier: checks the detached signature digest with
//               CryptVerifyMessageSignature, validates the signer certificate
//               chain with CertGetCertificateChain, and extracts the signatory
//               distinguished name from the CMS SignedData.
//
// No third-party dependency is introduced; cryptography runs fully offline via
// the Windows cryptographic service provider (CAPI) or CNG.
//
// The two exported constructors below are called from mupdf_shim.c inside its
// fz_try blocks; both fz_throw on failure so the caller reports through
// pf_last_error. Drop the objects with pdf_drop_signer/pdf_drop_verifier.

#include "mupdf/fitz.h"
#include "mupdf/pdf.h"
#include "mupdf_shim.h"

#include <windows.h>
#include <wincrypt.h>

#include <stdlib.h>
#include <string.h>

#if defined(_MSC_VER)
#pragma comment(lib, "crypt32.lib")
#endif

/* ------------------------------------------------------------------ */
/* Signer                                                              */
/* ------------------------------------------------------------------ */

typedef struct pf_capi_signer
{
	pdf_pkcs7_signer base;
	int refs;
	HCERTSTORE pfx_store;   /* PFXImportCertStore result, owned */
	PCCERT_CONTEXT cert;   /* the leaf certificate WITH its private key */
} pf_capi_signer;

static pdf_pkcs7_signer *
pf_capi_keep_signer(fz_context *ctx, pdf_pkcs7_signer *signer)
{
	pf_capi_signer *os = (pf_capi_signer *)signer;
	return (pdf_pkcs7_signer *)fz_keep_imp(ctx, os, &os->refs);
}

static void
pf_capi_drop_signer(fz_context *ctx, pdf_pkcs7_signer *signer)
{
	pf_capi_signer *os = (pf_capi_signer *)signer;
	if (fz_drop_imp(ctx, os, &os->refs))
	{
		if (os->cert != NULL)
			CertFreeCertificateContext(os->cert);
		if (os->pfx_store != NULL)
			CertCloseStore(os->pfx_store, 0);
		fz_free(ctx, os);
	}
}

/* UTF-8 <-> wide conversions, matching the shim's "everything crosses the
 * boundary as UTF-8" convention. */
static void
pf_wchar_from_utf8(const char *utf8, wchar_t *out, size_t out_chars)
{
	int n;
	if (utf8 == NULL)
		utf8 = "";
	if (out == NULL || out_chars == 0)
		return;
	out[0] = L'\0';
	n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, NULL, 0);
	if (n <= 0 || (size_t)n < out_chars)
		return;
	MultiByteToWideChar(CP_UTF8, 0, utf8, -1, out, n);
}

static wchar_t *
pf_capi_wchar_from_utf8(fz_context *ctx, const char *utf8)
{
	int n = MultiByteToWideChar(CP_UTF8, 0, utf8 != NULL ? utf8 : "", -1, NULL, 0);
	wchar_t *w;
	if (n <= 0)
		fz_throw(ctx, FZ_ERROR_LIBRARY, "signature: invalid UTF-8 (0x%lx)",
		         (unsigned long)GetLastError());
	w = fz_malloc(ctx, (size_t)n * sizeof(wchar_t));
	if (MultiByteToWideChar(CP_UTF8, 0, utf8 != NULL ? utf8 : "", -1, w, n) == 0)
	{
		fz_free(ctx, w);
		fz_throw(ctx, FZ_ERROR_LIBRARY, "signature: UTF-8 conversion failed (0x%lx)",
		         (unsigned long)GetLastError());
	}
	return w;
}

static char *
pf_capi_utf8_from_wchar(const wchar_t *w)
{
	int n;
	char *s;
	if (w == NULL)
		return NULL;
	n = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
	if (n <= 0)
		return NULL;
	s = (char *)malloc((size_t)n);
	if (s == NULL)
		return NULL;
	WideCharToMultiByte(CP_UTF8, 0, w, -1, s, n, NULL, NULL);
	return s;
}

/* Build a MuPDF pdf_pkcs7_distinguished_name from a certificate. Field strings
 * are fz-allocated so pdf_signature_drop_distinguished_name (fz_free) is the
 * matching destructor. */
static pdf_pkcs7_distinguished_name *
pf_capi_dn_from_cert(fz_context *ctx, PCCERT_CONTEXT cert)
{
	const char *oids[] =
	{
		szOID_COMMON_NAME,
		szOID_ORGANIZATION_NAME,
		szOID_ORGANIZATIONAL_UNIT_NAME,
		szOID_RSA_emailAddr,
		szOID_COUNTRY_NAME,
	};
	int which;
	pdf_pkcs7_distinguished_name *dn = NULL;

	fz_var(dn);

	fz_try(ctx)
	{
		WCHAR wname[1024];
		char *utf8;

		dn = fz_malloc_struct(ctx, pdf_pkcs7_distinguished_name);

		for (which = 0; which < 5; ++which)
		{
			wname[0] = L'\0';
			/* CERT_NAME_ATTR_TYPE, not CERT_NAME_RDN_TYPE. pvTypePara is an
			 * OID string for the former and a DWORD* of flags for the latter,
			 * so asking for RDN_TYPE while handing it an OID made the function
			 * dereference a string pointer as a DWORD*. The const-qualifier
			 * warning on this line was the visible edge of that mismatch. */
			CertGetNameStringW(cert, CERT_NAME_ATTR_TYPE, 0, (void *)oids[which],
			                   wname, (DWORD)(sizeof(wname) / sizeof(wname[0])));
			utf8 = pf_capi_utf8_from_wchar(wname);
			if (utf8 == NULL)
				continue;
			switch (which)
			{
			case 0: dn->cn = fz_strdup(ctx, utf8); break;
			case 1: dn->o = fz_strdup(ctx, utf8); break;
			case 2: dn->ou = fz_strdup(ctx, utf8); break;
			case 3: dn->email = fz_strdup(ctx, utf8); break;
			case 4: dn->c = fz_strdup(ctx, utf8); break;
			}
			free(utf8);
		}
	}
	fz_catch(ctx)
	{
		if (dn != NULL)
		{
			pdf_signature_drop_distinguished_name(ctx, dn);
			dn = NULL;
		}
		fz_rethrow(ctx);
	}

	return dn;
}

static pdf_pkcs7_distinguished_name *
pf_capi_signer_name(fz_context *ctx, pdf_pkcs7_signer *signer)
{
	pf_capi_signer *os = (pf_capi_signer *)signer;
	return pf_capi_dn_from_cert(ctx, os->cert);
}

/* Produce a detached PKCS#7/CMS SignedData over the (possibly empty)
 * fz_buffer `buf`. Uses SHA-1 to match the classic mutool/Adobe PKCS#7
 * signature profile. Returns the CMS blob length; when `digest` is non-NULL
 * the blob is written into it (digest_len = placeholder capacity).
 *
 * The size of a detached CMS is independent of the signed content, so the same
 * call with a NULL buffer sizes /Contents (Mirrors MuPDF's openssl helper).
 *
 * Note on the SDK: as of Windows SDK 10.0.26100 the legacy hCryptProv and
 * dwKeySpec members of CRYPT_SIGN_MESSAGE_PARA are gone — the certificate
 * itself (pSigningCert) selects the signer's private key via the OS key
 * store. cMsgCert = 1 embeds the certificate in the message so that external
 * verifiers that only look inside the CMS can find it. */
static int
pf_capi_make_signature(fz_context *ctx, pf_capi_signer *os, fz_buffer *buf,
                       unsigned char *digest, size_t digest_len)
{
	unsigned char *data = NULL;
	size_t data_len = 0;
	CRYPT_SIGN_MESSAGE_PARA para;
	const BYTE *rgpb[1];
	DWORD rgcb[1];
	DWORD cb_p7 = 0;
	int res = 0;

	memset(&para, 0, sizeof(para));

	if (buf != NULL)
		data_len = fz_buffer_storage(ctx, buf, &data);

	para.cbSize = sizeof(para);
	para.dwMsgEncodingType = X509_ASN_ENCODING | PKCS_7_ASN_ENCODING;
	para.pSigningCert = os->cert;
	para.HashAlgorithm.pszObjId = (LPSTR)szOID_OIWSEC_sha1;
	para.pvHashAuxInfo = NULL;
	para.cMsgCert = 1;
	para.rgpMsgCert = &os->cert;
	para.dwFlags = CRYPT_MESSAGE_SILENT_KEYSET_FLAG;

	rgpb[0] = data != NULL ? (const BYTE *)data : (const BYTE *)"";
	rgcb[0] = (DWORD)data_len;

	/* Size pass (CryptSignMessage reports the needed buffer). */
	if (!CryptSignMessage(&para, TRUE, 1, rgpb, rgcb, NULL, &cb_p7))
		fz_throw(ctx, FZ_ERROR_LIBRARY, "signature: CryptSignMessage sizing failed (0x%lx)",
		         (unsigned long)GetLastError());

	if (digest != NULL)
	{
		if ((size_t)cb_p7 > digest_len)
			fz_throw(ctx, FZ_ERROR_LIBRARY,
			         "signature: CMS (%lu bytes) exceeds /Contents placeholder (%lu bytes)",
			         (unsigned long)cb_p7, (unsigned long)digest_len);
		if (!CryptSignMessage(&para, TRUE, 1, rgpb, rgcb, digest, &cb_p7))
			fz_throw(ctx, FZ_ERROR_LIBRARY, "signature: CryptSignMessage failed (0x%lx)",
			         (unsigned long)GetLastError());
	}

	res = (int)cb_p7;
	return res;
}

static int
pf_capi_signer_create_digest(fz_context *ctx, pdf_pkcs7_signer *signer,
                             fz_stream *in, unsigned char *digest, size_t digest_len)
{
	pf_capi_signer *os = (pf_capi_signer *)signer;
	fz_buffer *buf = NULL;
	int res = 0;

	fz_var(buf);

	fz_try(ctx)
	{
		if (in != NULL)
			buf = fz_read_all(ctx, in, 0);
		res = pf_capi_make_signature(ctx, os, buf, digest, digest_len);
	}
	fz_always(ctx)
	{
		fz_drop_buffer(ctx, buf);
	}
	fz_catch(ctx)
	{
		fz_rethrow(ctx);
	}

	return res;
}

static size_t
pf_capi_max_digest_size(fz_context *ctx, pdf_pkcs7_signer *signer)
{
	return (size_t)pf_capi_signer_create_digest(ctx, signer, NULL, NULL, 0);
}

/* Load a PKCS#12 signer. `pfx`/`pfx_len` is the raw file content;
 * `password_utf8` unlocks it (NULL/empty means no password). The caller picks
 * the first certificate that carries a private key. Throws on failure. */
pdf_pkcs7_signer *
pf_capi_signer_new(fz_context *ctx, const unsigned char *pfx, size_t pfx_len,
                   const char *password_utf8)
{
	wchar_t *password_w = NULL;
	HCERTSTORE store = NULL;
	PCCERT_CONTEXT cursor = NULL;
	PCCERT_CONTEXT leaf = NULL;
	pf_capi_signer *os = NULL;

	fz_var(password_w);
	fz_var(store);
	fz_var(cursor);
	fz_var(leaf);
	fz_var(os);

	fz_try(ctx)
	{
		password_w = pf_capi_wchar_from_utf8(ctx, password_utf8);

		{
			CRYPT_DATA_BLOB pfx_blob;
			pfx_blob.pbData = (BYTE *)pfx;
			pfx_blob.cbData = (DWORD)pfx_len;
			store = PFXImportCertStore(&pfx_blob, password_w, CRYPT_EXPORTABLE);
		}
		if (store == NULL)
			fz_throw(ctx, FZ_ERROR_LIBRARY,
			         "signature: cannot open the PKCS#12 file (0x%lx). Wrong password?",
			         (unsigned long)GetLastError());

		while ((cursor = CertEnumCertificatesInStore(store, cursor)) != NULL)
		{
			DWORD prop_size = 0;
			if (CertGetCertificateContextProperty(cursor, CERT_KEY_PROV_INFO_PROP_ID,
			                                      NULL, &prop_size) ||
			    CertGetCertificateContextProperty(cursor, CERT_NCRYPT_KEY_HANDLE_PROP_ID,
			                                      NULL, &prop_size))
			{
				leaf = CertDuplicateCertificateContext(cursor);
				break;
			}
		}
		if (leaf == NULL)
			fz_throw(ctx, FZ_ERROR_LIBRARY,
			         "signature: the PKCS#12 file contains no certificate with a private key");

		os = fz_malloc_struct(ctx, pf_capi_signer);
		os->base.keep = pf_capi_keep_signer;
		os->base.drop = pf_capi_drop_signer;
		os->base.get_signing_name = pf_capi_signer_name;
		os->base.max_digest_size = pf_capi_max_digest_size;
		os->base.create_digest = pf_capi_signer_create_digest;
		os->refs = 1;
		os->pfx_store = store;
		os->cert = leaf;

		store = NULL; /* ownership moved into the signer */
		leaf = NULL;

		fz_free(ctx, password_w);
		password_w = NULL;
	}
	fz_catch(ctx)
	{
		if (password_w != NULL)
			fz_free(ctx, password_w);
		if (store != NULL)
			CertCloseStore(store, 0);
		if (leaf != NULL)
			CertFreeCertificateContext(leaf);
		fz_rethrow(ctx);
	}

	return (pdf_pkcs7_signer *)os;
}

/* ------------------------------------------------------------------ */
/* Verifier                                                            */
/* ------------------------------------------------------------------ */

typedef struct pf_capi_verifier
{
	pdf_pkcs7_verifier base;
	int refs;
	PCCERT_CONTEXT cached_signer; /* signer cert found by check_digest (owned) */
} pf_capi_verifier;

static void
pf_capi_verifier_drop(fz_context *ctx, pdf_pkcs7_verifier *verifier)
{
	pf_capi_verifier *ov = (pf_capi_verifier *)verifier;
	if (fz_drop_imp(ctx, ov, &ov->refs))
	{
		if (ov->cached_signer != NULL)
			CertFreeCertificateContext(ov->cached_signer);
		fz_free(ctx, ov);
	}
}

/* Locate the signer's leaf certificate inside a detached PKCS#7/CMS blob by
 * matching issuer+serial (the PDF signature profile embeds the signer cert).
 * Returns a duplicated certificate context (caller frees) or NULL.
 *
 * Uses the message-store building block: CERT_STORE_PROV_MSG exposes the
 * certificates embedded in the CMS, and CMSG_SIGNER_CERT_INFO_PARAM yields the
 * signer identity (issuer + serial number as a CERT_INFO). */
static PCCERT_CONTEXT
pf_cms_signer_cert(const unsigned char *sig, size_t sig_len)
{
	CRYPT_DATA_BLOB sig_blob;
	HCERTSTORE hMsgStore = NULL;
	HCRYPTMSG hMsg = NULL;
	PCERT_INFO signer_info = NULL;
	DWORD cb_info = 0;
	PCCERT_CONTEXT signer = NULL;

	sig_blob.pbData = (BYTE *)sig;
	sig_blob.cbData = (DWORD)sig_len;

	hMsgStore = CertOpenStore(CERT_STORE_PROV_MSG,
	                          X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
	                          0, 0, &sig_blob);
	if (hMsgStore == NULL)
		return NULL;

	/* CryptMsgOpenToDecode RETURNS the handle; it has no out-parameter. This
	 * passed &hMsg as the sixth argument (pStreamInfo) and threw the return
	 * value away, so CryptMsgUpdate was then called on an uninitialised handle
	 * and took the process down. Same story as the signing side: the compiler
	 * warned about both argument types on this line, and nothing had ever
	 * called the verifier, so nobody saw it fail. */
	hMsg = CryptMsgOpenToDecode(X509_ASN_ENCODING | PKCS_7_ASN_ENCODING, 0, 0,
	                           0, NULL, NULL);
	if (hMsg == NULL ||
	    !CryptMsgUpdate(hMsg, (const BYTE *)sig, (DWORD)sig_len, TRUE))
		goto cleanup;

	/* CMSG_SIGNER_CERT_INFO_PARAM returns a CERT_INFO whose Issuer and
	 * SerialNumber blobs point at bytes appended after the struct, so the size
	 * is larger than sizeof(CERT_INFO) and the result cannot go into a fixed
	 * one on the stack. This used a stack CERT_INFO and guarded the call with
	 * "cb_info == sizeof(signer_info)", which is never true - so the signer was
	 * never found and this returned NULL to every caller.
	 *
	 * The crash was the line below it: CERT_FIND_SUBJECT_CERT takes a
	 * CERT_INFO*, and it was handed &signer_info.Issuer, a CERT_NAME_BLOB.
	 * Reading a blob as a CERT_INFO is how verification took the process down.
	 * Matching on issuer+serial is what CERT_FIND_SUBJECT_CERT already does,
	 * so the hand-rolled serial comparison goes with it. */
	if (!CryptMsgGetParam(hMsg, CMSG_SIGNER_CERT_INFO_PARAM, 0, NULL, &cb_info) ||
	    cb_info == 0)
		goto cleanup;

	signer_info = (PCERT_INFO)malloc(cb_info);
	if (signer_info == NULL)
		goto cleanup;

	if (CryptMsgGetParam(hMsg, CMSG_SIGNER_CERT_INFO_PARAM, 0, signer_info, &cb_info))
	{
		PCCERT_CONTEXT match = CertFindCertificateInStore(
			hMsgStore, X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
			0, CERT_FIND_SUBJECT_CERT, (const void *)signer_info, NULL);
		if (match != NULL)
		{
			signer = CertDuplicateCertificateContext(match);
			CertFreeCertificateContext(match);
		}
	}

cleanup:
	free(signer_info);
	if (hMsg != NULL)
		CryptMsgClose(hMsg);
	CertCloseStore(hMsgStore, 0);
	return signer;
}

/* CryptVerifyMessageSignature invokes this to resolve the signer certificate.
 * We look inside the CMS (hMsgCertStore of embedded certificates) by matching
 * issuer + serial number, and cache the result on the verifier so the later
 * certificate/chain checks and the signatory name reuse it. The returned
 * context is handed to the OS, which frees it. */
static PCCERT_CONTEXT WINAPI
pf_capi_get_signer_callback(PVOID pvGetArg, DWORD dwCertEncodingType,
                            PCERT_INFO pSignerId, HCERTSTORE hMsgCertStore)
{
	pf_capi_verifier *ov = (pf_capi_verifier *)pvGetArg;
	PCCERT_CONTEXT match;

	(void)dwCertEncodingType;
	if (pSignerId == NULL || hMsgCertStore == NULL)
		return NULL;

	match = CertFindCertificateInStore(hMsgCertStore,
	                                   X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
	                                   0, CERT_FIND_SUBJECT_CERT,
	                                   (const void *)&pSignerId->Issuer, NULL);
	if (match == NULL)
		return NULL;

	if (match->pCertInfo == NULL ||
	    match->pCertInfo->SerialNumber.cbData != pSignerId->SerialNumber.cbData ||
	    memcmp(match->pCertInfo->SerialNumber.pbData, pSignerId->SerialNumber.pbData,
	           match->pCertInfo->SerialNumber.cbData) != 0)
	{
		CertFreeCertificateContext(match);
		return NULL;
	}

	if (ov->cached_signer == NULL)
		ov->cached_signer = CertDuplicateCertificateContext(match);
	return match;
}

static pdf_signature_error
pf_capi_verifier_check_digest(fz_context *ctx, pdf_pkcs7_verifier *verifier,
                              fz_stream *in, unsigned char *signature, size_t signature_len)
{
	pdf_signature_error result = PDF_SIGNATURE_ERROR_DIGEST_FAILURE;
	fz_buffer *content = NULL;
	unsigned char *data = NULL;
	size_t data_len = 0;
	DWORD decoded_len = 0;
	CRYPT_VERIFY_MESSAGE_PARA para;
	HCRYPTMSG hMsg = NULL;
	PCCERT_CONTEXT signer = NULL;
	pf_capi_verifier *ov = (pf_capi_verifier *)verifier;

	fz_var(content);

	if (in != NULL)
	{
		fz_try(ctx)
		{
			content = fz_read_all(ctx, in, 0);
			data_len = fz_buffer_storage(ctx, content, &data);
		}
		fz_catch(ctx)
		{
			fz_drop_buffer(ctx, content);
			fz_rethrow(ctx);
		}
	}

	/* A PDF signature is DETACHED (/SubFilter adbe.pkcs7.detached): the signed
	 * bytes live in the file, not inside the PKCS#7 blob.
	 *
	 * CryptVerifyMessageSignature cannot verify that - it is for opaque messages
	 * that carry their own content, and its fifth and sixth arguments are an
	 * OUTPUT buffer for the decoded content and its size. This passed the
	 * document's bytes as that output buffer with a size of zero, so it was
	 * simultaneously asking the wrong question and offering a zero-length
	 * destination that the API could have written through.
	 *
	 * The detached flow is: open with CMSG_DETACHED_FLAG, update once with the
	 * blob and once with the content it is supposed to cover, then ask
	 * CryptMsgControl to check the signature against the signer's certificate. */
	(void)para;
	(void)decoded_len;

	hMsg = CryptMsgOpenToDecode(X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
	                            CMSG_DETACHED_FLAG, 0, 0, NULL, NULL);
	if (hMsg == NULL)
		goto cleanup;

	if (!CryptMsgUpdate(hMsg, (const BYTE *)signature, (DWORD)signature_len, TRUE))
		goto cleanup;

	/* The detached content, as a single final update. */
	if (!CryptMsgUpdate(hMsg,
	                    data != NULL ? (const BYTE *)data : (const BYTE *)"",
	                    (DWORD)data_len, TRUE))
		goto cleanup;

	signer = pf_cms_signer_cert(signature, signature_len);
	if (signer == NULL)
		goto cleanup;

	if (CryptMsgControl(hMsg, 0, CMSG_CTRL_VERIFY_SIGNATURE, signer->pCertInfo))
	{
		result = PDF_SIGNATURE_ERROR_OKAY;

		/* Kept for the certificate check that follows, which would otherwise
		 * have to decode the blob a second time. */
		if (ov->cached_signer != NULL)
			CertFreeCertificateContext(ov->cached_signer);
		ov->cached_signer = CertDuplicateCertificateContext(signer);
	}

cleanup:
	if (signer != NULL)
		CertFreeCertificateContext(signer);
	if (hMsg != NULL)
		CryptMsgClose(hMsg);
	fz_drop_buffer(ctx, content);
	return result;
}

static pdf_signature_error
pf_capi_verifier_check_certificate(fz_context *ctx, pdf_pkcs7_verifier *verifier,
                                   unsigned char *signature, size_t signature_len)
{
	PCCERT_CONTEXT signer_cert = NULL;
	CERT_CHAIN_PARA chain_para;
	CERT_CHAIN_POLICY_PARA policy_para;
	CERT_CHAIN_POLICY_STATUS policy_status;
	PCCERT_CHAIN_CONTEXT chain = NULL;
	pdf_signature_error result = PDF_SIGNATURE_ERROR_NO_CERTIFICATE;
	pf_capi_verifier *ov = (pf_capi_verifier *)verifier;

	signer_cert = ov->cached_signer != NULL
		? CertDuplicateCertificateContext(ov->cached_signer)
		: pf_cms_signer_cert(signature, signature_len);
	if (signer_cert == NULL)
		return PDF_SIGNATURE_ERROR_NO_CERTIFICATE;

	memset(&chain_para, 0, sizeof(chain_para));
	chain_para.cbSize = sizeof(chain_para);

	if (!CertGetCertificateChain(NULL, signer_cert, NULL, NULL, &chain_para, 0,
	                             NULL, &chain))
	{
		CertFreeCertificateContext(signer_cert);
		return PDF_SIGNATURE_ERROR_NOT_TRUSTED;
	}

	memset(&policy_para, 0, sizeof(policy_para));
	policy_para.cbSize = sizeof(policy_para);
	memset(&policy_status, 0, sizeof(policy_status));
	policy_status.cbSize = sizeof(policy_status);

	if (CertVerifyCertificateChainPolicy(CERT_CHAIN_POLICY_BASE, chain,
	                                     &policy_para, &policy_status))
	{
		result = PDF_SIGNATURE_ERROR_OKAY;
	}
	else
	{
		/* Diagnose why the chain did not validate, preferring an informative
		 * "self-signed" verdict over a generic "not trusted". */
		PCERT_SIMPLE_CHAIN sc = chain->rgpChain[0];
		DWORD root_idx = sc->cElement > 0 ? sc->cElement - 1 : 0;
		PCCERT_CONTEXT root = sc->rgpElement[root_idx]->pCertContext;
		int root_self_signed = 0;
		int mid_self_signed = 0;
		DWORD e;

		if (root != NULL && root->pCertInfo != NULL &&
		    root->pCertInfo->Subject.cbData == root->pCertInfo->Issuer.cbData &&
		    memcmp(root->pCertInfo->Subject.pbData, root->pCertInfo->Issuer.pbData,
		           root->pCertInfo->Subject.cbData) == 0)
			root_self_signed = 1;

		for (e = 0; e < sc->cElement && !mid_self_signed; ++e)
		{
			PCCERT_CONTEXT elem = sc->rgpElement[e]->pCertContext;
			if (elem != NULL && elem->pCertInfo != NULL &&
			    elem->pCertInfo->Subject.cbData == elem->pCertInfo->Issuer.cbData &&
			    memcmp(elem->pCertInfo->Subject.pbData, elem->pCertInfo->Issuer.pbData,
			           elem->pCertInfo->Subject.cbData) == 0)
				mid_self_signed = 1;
		}
		if (mid_self_signed && !root_self_signed)
			result = PDF_SIGNATURE_ERROR_SELF_SIGNED_IN_CHAIN;
		else if (root_self_signed)
			result = PDF_SIGNATURE_ERROR_SELF_SIGNED;
		else
			result = PDF_SIGNATURE_ERROR_NOT_TRUSTED;
	}

	CertFreeCertificateChain(chain);
	CertFreeCertificateContext(signer_cert);
	return result;
}

static pdf_pkcs7_distinguished_name *
pf_capi_verifier_get_signatory(fz_context *ctx, pdf_pkcs7_verifier *verifier,
                               unsigned char *signature, size_t signature_len)
{
	PCCERT_CONTEXT signer_cert;
	pdf_pkcs7_distinguished_name *dn = NULL;

	(void)verifier;

	signer_cert = pf_cms_signer_cert(signature, signature_len);
	if (signer_cert == NULL)
		return NULL;

	fz_var(dn);
	fz_try(ctx)
	{
		dn = pf_capi_dn_from_cert(ctx, signer_cert);
	}
	fz_always(ctx)
	{
		CertFreeCertificateContext(signer_cert);
	}
	fz_catch(ctx)
	{
		fz_rethrow(ctx);
	}

	return dn;
}

/* Single reusable-verifier constructor used by pf_list_signatures. */
pdf_pkcs7_verifier *
pf_capi_verifier_new(fz_context *ctx)
{
	pf_capi_verifier *ov = fz_malloc_struct(ctx, pf_capi_verifier);
	ov->base.drop = pf_capi_verifier_drop;
	ov->base.check_digest = pf_capi_verifier_check_digest;
	ov->base.check_certificate = pf_capi_verifier_check_certificate;
	ov->base.get_signatory = pf_capi_verifier_get_signatory;
	ov->refs = 1;
	return (pdf_pkcs7_verifier *)ov;
}