// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Pdf;

/// <summary>
/// One entry in the document outline (bookmarks). The document's outline is
/// returned as a flattened pre-order item list; <see cref="PdfOutline.Items"/>
/// carries an explicit <see cref="OutlineItem.Depth"/> so a tree (WinUI/WPF
/// TreeView) can be reconstructed purely from the flat sequence.
/// </summary>
public sealed record OutlineItem(
    string Title,
    int PageNumber,
    double X,
    double Y,
    int Depth);

/// <summary>
/// The document outline. Empty when the document defines none.
/// </summary>
public sealed record PdfOutline(IReadOnlyList<OutlineItem> Items)
{
    public static PdfOutline Empty { get; } = new(Array.Empty<OutlineItem>());

    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// The plain text of a single page, extracted in reading order. Used to back
/// full-text search (FR-VIEW-03). The engine owns extraction; consumers search
/// the returned text.
/// </summary>
public sealed record PageText(int PageIndex, string Text)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}
