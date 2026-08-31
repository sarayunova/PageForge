// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.Editing;

/// <summary>
/// The undoable move and/or resize of one image/vector object (FR-EDIT-04/05).
/// Executing once moves/resizes the object identified by
/// <paramref name="objectId"/> to <paramref name="newBounds"/> and stores the
/// <see cref="PdfTextEditReceipt"/> the engine returns; the stack replays redo as
/// an exact re-splice of the stored new operators and undo as the matching splice
/// of the old ones — geometry is never re-matched, so undo/redo of a transform is
/// faithful even when other edits have since shifted the page.
/// </summary>
public sealed class ObjectEditCommand : IEditCommand
{
    private readonly IPdfEngine _engine;
    private readonly int _pageIndex;
    private readonly string _objectId;
    private readonly PdfRect _newBounds;
    private bool _hasReceipt;
    private PdfTextEditReceipt? _receipt;

    public ObjectEditCommand(IPdfEngine engine, int pageIndex, string objectId, PdfRect newBounds)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(objectId);

        _engine = engine;
        _pageIndex = pageIndex;
        _objectId = objectId;
        _newBounds = newBounds;
    }

    /// <summary>Stable label for the undo menu and journal records.</summary>
    public string Name => "Move object";

    /// <summary>The page that was edited (0-based).</summary>
    public int PageIndex => _pageIndex;

    /// <summary>The object id this command transformed, stable within the document state it was created for.</summary>
    public string ObjectId => _objectId;

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_hasReceipt)
        {
            // Redo: re-apply the stored new geometry exact.
            await _engine.RevertTextEditAsync(_pageIndex, _receipt!, redo: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            PdfTextEditReceipt receipt = await _engine
                .MoveResizeObjectAsync(_pageIndex, _objectId, _newBounds, cancellationToken).ConfigureAwait(false);
            _receipt = receipt;
            _hasReceipt = true;
        }
    }

    public async ValueTask UndoAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasReceipt)
        {
            throw new InvalidOperationException($"Cannot undo {Name} before it has been executed.");
        }

        await _engine.RevertTextEditAsync(_pageIndex, _receipt!, redo: false, cancellationToken).ConfigureAwait(false);
    }
}
