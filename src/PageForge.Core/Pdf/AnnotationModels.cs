// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// The closed set of annotation kinds the FR-ANNOT-01 feature produces.
/// Each value maps 1:1 to a MuPDF annotation subtype.
/// </summary>
public enum AnnotationType
{
    /// <summary>Text styled as a (usually semi-transparent) background highlight.</summary>
    Highlight,

    /// <summary>A line drawn beneath the selected text.</summary>
    Underline,

    /// <summary>A line drawn through the selected text.</summary>
    StrikeOut,

    /// <summary>A free-hand stroke made of an ordered list of points.</summary>
    Ink,

    /// <summary>A text note/comment pinned at a location on the page.</summary>
    Text,

    /// <summary>A rectangle shape drawn on the page.</summary>
    Square,

    /// <summary>An ellipse/circle shape drawn on the page.</summary>
    Circle,

    /// <summary>A graphical stamp (uses a synthesized appearance).</summary>
    Stamp,
}

/// <summary>
/// A point in PDF page coordinates (origin bottom-left, units = PDF points) used
/// to describe annotation rectangles and ink strokes.
/// </summary>
public readonly record struct PdfPoint(double X, double Y);

/// <summary>
/// A quad (four corners, in reading order: lower-left, lower-right, upper-right,
/// upper-left) selecting a run of text for highlight/underline/strikethrough
/// annotations. A single annotation can cover multiple quads (e.g. a highlight
/// spanning several lines).
/// </summary>
public readonly record struct PdfQuad(PdfPoint LowerLeft, PdfPoint LowerRight, PdfPoint UpperRight, PdfPoint UpperLeft);

/// <summary>
/// A bound annotation on a page, as reported by
/// <see cref="IPdfEngine.ListAnnotationsAsync"/>. The rectangle (x0,y0,x1,y1) is
/// in PDF points with origin bottom-left.
/// </summary>
public sealed record PdfAnnotation(AnnotationType Type, double X0, double Y0, double X1, double Y1, string? Contents = null);

/// <summary>
/// The full description of an annotation to create via
/// <see cref="IPdfEngine.AddAnnotationAsync"/>. This is the Core-level contract;
/// the interop layer translates it into the native BOM-free spec-file grammar
/// (T/R/Q/I/O/P lines).
/// </summary>
public sealed record AnnotBuildSpec
{
    public required AnnotationType Type { get; init; }

    public required double X0 { get; init; }

    public required double Y0 { get; init; }

    public required double X1 { get; init; }

    public required double Y1 { get; init; }

    /// <summary>Human-readable contents (required for <see cref="AnnotationType.Text"/>).</summary>
    public string? Contents { get; init; }

    /// <summary>RGB color components in [0,1]. When null, MuPDF's default color is used.</summary>
    public (double R, double G, double B)? Color { get; init; }

    /// <summary>Opacity in [0,1]; null leaves the engine default.</summary>
    public double? Opacity { get; init; }

    /// <summary>The text regions covered (highlight/underline/strikethrough).</summary>
    public IReadOnlyList<PdfQuad>? Quads { get; init; }

    /// <summary>The stroke vertices (ink only).</summary>
    public IReadOnlyList<PdfPoint>? InkPoints { get; init; }
}
