// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// Tuning for a local OCR run (FR-OCR-01). All recognition happens on this
/// machine with the bundled Tesseract trained data; nothing ever leaves the
/// device. Passing <c>null</c> options to <c>OcrToPdfAsync</c> behaves exactly
/// like the defaults.
/// </summary>
public sealed record OcrOptions(
    /// <summary>
    /// Tesseract language code (e.g. "eng", "deu"). <c>null</c>/empty selects the
    /// bundled English model. The trained data must be present under the data
    /// directory; only "eng" ships with PageForge in this release.
    /// </summary>
    string? Language = null,
    /// <summary>
    /// Directory that contains "<c>&lt;language&gt;.traineddata</c>". When null,
    /// the engine searches, in order: the <c>PF_TESSDATA_DIR</c> environment
    /// variable, a <c>tessdata</c> folder next to the application binaries, and
    /// the native build output of a development checkout. The first candidate
    /// that exists is used, otherwise the call fails with an error listing every
    /// location searched.
    /// </summary>
    string? DataDirectory = null);

/// <summary>The outcome of an offline OCR run (FR-OCR-01).</summary>
public sealed record OcrResult(
    /// <summary>Number of pages written to the output searchable PDF.</summary>
    int PageCount,
    /// <summary>Absolute path of the new searchable-PDF file.</summary>
    string OutputPath,
    /// <summary>The Tesseract language effectively used for recognition.</summary>
    string Language,
    /// <summary>The data directory the recognizer loaded its model from.</summary>
    string DataDirectory);