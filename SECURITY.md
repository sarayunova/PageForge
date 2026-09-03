# Security Policy

## Reporting a vulnerability

Please do **not** report security vulnerabilities through public GitHub
issues, pull requests, or discussions. Instead, report them privately to the
maintainers.

To responsibly disclose, contact the project maintainers or use GitHub's
private vulnerability reporting for this repository. Include:

- A description of the issue and the affected endpoint/component.
- Steps to reproduce, including any sample inputs.
- Impact assessment if known.
- Suggested remediation if you have one.

You will receive an acknowledgment within a reasonable timeframe and we will
coordinate disclosure once a fix is available. Please give us time to prepare a
fix before publicizing the issue.

## Scope

In scope:

- Remote code execution or data exfiltration via the hosted API
  (`services/PageForge.Api`).
- Malicious PDFs processed by the desktop viewer/editor (engine hardening
  against crafted documents).
- Authentication/authorization or billing integrity in hosted services.
- Secret/key or PII exposure.

Out of scope:

- The native MuPDF engine itself — upstream vulnerabilities should be reported
  to Artifex and are addressed by bumping the pinned version.
- Third-party libraries — see `THIRD-PARTY-NOTICES.md`; report upstream.

## Supported versions

Security fixes land on the latest `main` and the most recent release. We do not
provide backported patches for older releases.