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

### WPF-UI — Fluent control styles for the desktop shell
- Version: 4.3.0 (with its `WPF-UI.Abstractions` dependency)
- Source: `https://github.com/lepoco/wpfui`
- License: MIT
- Copyright: Leszek Pomianowski and WPF UI Contributors
- Usage: supplies the Windows 11 / Fluent control templates and theme dictionaries
  merged by `src/PageForge.App.Wpf/App.xaml`, and the light/dark application theme
  that `Themes/ThemeManager.cs` keeps in step with PageForge's own design tokens.
  Unlike the entries in the build-tooling section below, this assembly **is
  distributed** with the desktop application.

### Microsoft.Extensions.Logging — the desktop shell's logging abstraction
- Version: 8.0.1 (with its transitive `Microsoft.Extensions.Logging.Abstractions`,
  `Microsoft.Extensions.DependencyInjection`(`.Abstractions`), `Microsoft.Extensions.Options`
  and `Microsoft.Extensions.Primitives` dependencies)
- Source: `https://github.com/dotnet/runtime`
- License: MIT
- Copyright: .NET Foundation and Contributors
- Usage: supplies `ILogger` and the logger factory behind
  `src/PageForge.App.Wpf/Diagnostics/AppLog.cs`, replacing `System.Diagnostics.Trace`
  so a failure on a user's machine leaves a readable trail. Only the abstraction and
  factory are taken; the sink itself (`Diagnostics/FileLoggerProvider.cs`) is
  PageForge code, so no logging framework is distributed. These assemblies **are
  distributed** with the desktop application.

### CommunityToolkit.Mvvm — MVVM primitives for the desktop shell
- Version: 8.4.0
- Source: `https://github.com/CommunityToolkit/dotnet`
- License: MIT
- Copyright: .NET Foundation and Contributors
- Usage: supplies the `ObservableObject` base and `RelayCommand` behind the shell's
  view models (`src/PageForge.App.Wpf/ViewModels`), replacing a hand-rolled
  `INotifyPropertyChanged` base so the shell's `Click`-handler code-behind can be
  converted to commands and bindings. These assemblies **are distributed** with the
  desktop application.

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
- MIT: https://opensource.org/license/mit

_This file is part of PageForge. SPDX-License-Identifier: AGPL-3.0-only._