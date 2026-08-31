// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.Editing;

/// <summary>
/// The undoable replacement of one image/vector object's interior (FR-EDIT-04/05).
/// Executing once replaces the object identified by <paramref name="objectId"/>
/// with <paramref name="replacement"/> and stores the
/// <see cref="PdfTextEditReceipt"/> the engine returns; the stack replays redo as
/// an exact re-application of the stored replacement and undo as the matching
/// splice back of the original content — nothing is re-matched, so undo/redo of a
/// replace is faithful even after other edits.
/// </summary>
public sealed class ReplaceObjectCommand : IEditCommand
{
    private readonly IPdfEngine _engine;
    private readonly int _pageIndex;
    private readonly string _objectId;
    private readonly PdfObjectReplacement _replacement;
    private bool _hasReceipt;
    private PdfTextEditReceipt? _receipt;

    public ReplaceObjectCommand(
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

        _engine = engine;
        _pageIndex = pageIndex;
        _objectId = objectId;
        _replacement = replacement;
    }

    /// <summary>Stable label for the undo menu and journal records.</summary>
    public string Name => "Replace object";

    /// <summary>The page that was edited (0-based).</summary>
    public int PageIndex => _pageIndex;

    /// <summary>The object id this command replaced, stable within the document state it was created for.</summary>
    public string ObjectId => _objectId;

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_hasReceipt)
        {
            // Redo: re-apply the stored replacement exact.
            await _engine.RevertTextEditAsync(_pageIndex, _receipt!, redo: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            PdfTextEditReceipt receipt = await _engine
                .ReplaceObjectAsync(_pageIndex, _objectId, _replacement, cancellationToken).ConfigureAwait(false);
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
