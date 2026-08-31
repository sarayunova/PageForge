// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>The kind of a page object that FR-EDIT-04 can move, resize, or replace.</summary>
public enum PageObjectKind
{
    /// <summary>An embedded raster image placed on the page.</summary>
    Image,

    /// <summary>A vector drawing (path, shape, or other vector content) placed on the page.</summary>
    Vector,
}

/// <summary>
/// One image or vector object on a page that FR-EDIT-04 can transform — a
/// <see cref="Kind"/>, its current <see cref="Bounds"/> in PDF points, and a
/// stable <see cref="Id"/> that uniquely identifies the object's content-stream
/// invocation so the engine can find and rewrite it. The id is opaque to Core:
/// it is created by the engine's <c>ListObjectsAsync</c> and handed back verbatim
/// to <c>MoveResizeObjectAsync</c>.
/// </summary>
public sealed record PdfPageObject(
    PageObjectKind Kind,
    string Id,
    PdfRect Bounds,
    string? Name = null)
{
    /// <summary>A short, human-readable label for UI (e.g. "Image 3").</summary>
    public string Label => $"{Kind} {Id}";
}

/// <summary>
/// The replacement content for a FR-EDIT-04 replace: bytes of a new embedded
/// object (<paramref name="SourcePath"/>) and its raster/vector
/// <paramref name="Format"/> (e.g. "png", "jpeg", "svg", "pdf"). The target
/// object's bounding box is preserved — only its interior is swapped.
/// </summary>
public sealed record PdfObjectReplacement(string SourcePath, string Format);

