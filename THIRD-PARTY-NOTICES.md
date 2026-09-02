# THIRD-PARTY NOTICES

PageForge is AGPLv3.0 (see `LICENSE`). This file documents the third-party components bundled,
linked, or used as build/verification tooling, and the license terms that govern their use.

## Bundled / linked components

### MuPDF — rendering and editing engine
- Version: 1.28.3
- Source: Artifex Software, from the official tarball
  `https://github.com/ArtifexSoftware/mupdf-downloads/releases/download/1.28.3/mupdf-1.28.3-source.tar.gz`
- License: GNU Affero General Public License v3.0 (AGPL-3.0)
- Copyright: Artifex Software, Inc.
- Usage: MuPDF static libraries are linked into `pageforge_mupdf.dll` — the PageForge shim
  under `native/PageForge.MuPdfShim`. `mutool` binaries built from the same tree are used for
  structural verification and as the fidelity reference renderer.

AGPL notice: MuPDF and PageForge are both AGPLv3. Modifications to MuPDF made for the PageForge
build (see `native/build-mupdf.ps1`) are limited to build-system configuration and are
distributed in source form via this repository. The built native output is excluded from the
repository (`.gitignore`) and is not redistributed as a binary-only artifact.

### Tesseract trained data (FR-OCR-01 offline OCR)
- Component: `eng.traineddata` from the `tessdata_fast` repository, pinned and staged to
  `tools/tessdata/` by `native/build-mupdf.ps1` (sha256 `7D4322BD2A7749724879683FC3912CB542F19906C83BCC1A52132556427170B2`).
- Source: `https://github.com/tesseract-ocr/tessdata_fast`
- License: Apache License 2.0
- Copyright: Google Inc. and the Tesseract OCR contributors
- Usage: embedded next to the application binaries so the bundled Tesseract inside
  `pageforge_mupdf.dll` can initialize the English recognition model fully offline; installed
  alongside the native build output. Not distributed as a product artifact separate from the app.

## Build / verification tooling (used at build or test time, not distributed)

### qpdf — structural PDF validation (dev tool)
- Version: 12.4.1
- License: Apache License 2.0
- Copyright: Jay Berkenbilt and other qpdf contributors
- Usage: `qpdf --check` in the `/structcheck` opencode command and CI.

### .NET 8 SDK / NuGet packages
- Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio: MIT
- Microsoft.WindowsAppSDK, Microsoft.Windows.SDK.BuildTools: MIT
- Used exclusively to build and test PageForge; none are distributed with the product.

## License texts
- AGPL-3.0: https://www.gnu.org/licenses/agpl-3.0.html (also in `LICENSE`)
- Apache-2.0: https://www.apache.org/licenses/LICENSE-2.0

_This file is part of PageForge. SPDX-License-Identifier: AGPL-3.0-only._