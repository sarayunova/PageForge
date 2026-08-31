// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// One editably visible run of text on a page (FR-EDIT-01), as reported by
/// <see cref="IPdfEngine.ListTextRunsAsync"/>. A run is a maximal span of
/// consecutive characters sharing the same font and size on the same line, so a
/// hit-test is: pick the run whose bounding box contains the click point. All
/// coordinates are in PDF points with origin bottom-left.
/// </summary>
public sealed record PdfTextRun(
    int Index,
    double X0,
    double Y0,
    double X1,
    double Y1,
    double FontSize,
    bool FontEmbedded,
    string FontName,
    string Text)
{
    /// <summary>True when the click point falls inside the run's bounding box.</summary>
    public bool Contains(double x, double y) => x >= X0 && x <= X1 && y >= Y0 && y <= Y1;
}

/// <summary>
/// The opaque undo/redo payload of one text-run rewrite (FR-EDIT-05). The
/// rewrite primitive replaces the run's text-showing operators in the page's
/// content stream with operands that paint the new text; this record pins the
/// content stream, byte offset and the old/new operator bytes so undo and redo
/// are exact stream splices and never re-run geometry matching.
/// </summary>
public sealed record PdfTextEditReceipt(
    int Version,
    int StreamIndex,
    int Offset,
    int OldLength,
    int NewLength,
    byte[] OldOperators,
    byte[] NewOperators);