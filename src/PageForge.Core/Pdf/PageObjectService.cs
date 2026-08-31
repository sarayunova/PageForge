// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Editing;

namespace PageForge.Core.Pdf;

/// <summary>
/// Pure helper that turns FR-EDIT-04 image/vector operations (list objects, move,
/// resize, aspect-preserving resize, replace) into calls on the
/// <see cref="IPdfEngine"/> seam and undoable commands, doing the validation that
/// does not depend on the native engine. Keeps the orchestration shared between
/// the WPF/WinUI shells and fully unit-testable against a fake engine.
/// </summary>
public static class PageObjectService
{
    /// <summary>
    /// Returns the image and vector objects of one page, each with its bounding
    /// box and a stable id for later FR-EDIT-04 transforms.
    /// </summary>
    public static async ValueTask<IReadOnlyList<PdfPageObject>> ListObjectsAsync(
        IPdfEngine engine,
        int pageIndex,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        return await engine.ListObjectsAsync(pageIndex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds an undoable <see cref="ObjectEditCommand"/> that moves and/or resizes
    /// the object <paramref name="objectId"/> to <paramref name="newBounds"/>. The
    /// caller pushes it onto an <see cref="EditCommandStack"/>.
    /// </summary>
    public static ObjectEditCommand MoveResizeAsync(
        IPdfEngine engine, int pageIndex, string objectId, PdfRect newBounds)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(objectId);
        return new ObjectEditCommand(engine, pageIndex, objectId, newBounds);
    }

    /// <summary>
    /// Resizes the object <paramref name="objectId"/> to <paramref name="width"/>
    /// while preserving its current aspect ratio (anchored at its bottom-left),
    /// then builds an undoable <see cref="ObjectEditCommand"/> for the result.
    /// </summary>
    public static ObjectEditCommand ResizeToWidthAsync(
        IPdfEngine engine, int pageIndex, PdfPageObject target, double width)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(target);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        PdfRect resized = PageObjectGeometry.ResizeToWidthAspect(target.Bounds, width);
        return new ObjectEditCommand(engine, pageIndex, target.Id, resized);
    }

    /// <summary>
    /// Builds an undoable <see cref="ObjectEditCommand"/> that moves the object
    /// <paramref name="objectId"/> by the deltas (preserving its size).
    /// </summary>
    public static ObjectEditCommand MoveByAsync(
        IPdfEngine engine, int pageIndex, PdfPageObject target, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(target);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        PdfRect moved = PageObjectGeometry.Translate(target.Bounds, dx, dy);
        return new ObjectEditCommand(engine, pageIndex, target.Id, moved);
    }

    /// <summary>
    /// Builds an undoable <see cref="ReplaceObjectCommand"/> that swaps the object
    /// <paramref name="objectId"/>'s interior for <paramref name="replacement"/>
    /// (preserving its bounds).
    /// </summary>
    public static ReplaceObjectCommand ReplaceAsync(
        IPdfEngine engine, int pageIndex, string objectId, PdfObjectReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(objectId);
        ArgumentNullException.ThrowIfNull(replacement);
        ArgumentException.ThrowIfNullOrEmpty(replacement.SourcePath);
        ArgumentException.ThrowIfNullOrEmpty(replacement.Format);
        return new ReplaceObjectCommand(engine, pageIndex, objectId, replacement);
    }
}
