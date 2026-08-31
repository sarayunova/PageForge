// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.Editing;

/// <summary>
/// The undoable text-run rewrite (FR-EDIT-01/05). Executing once rewrites a run
/// on a page and stores the <see cref="PdfTextEditReceipt"/> the engine returns;
/// the stack replays redo as an exact stream re-splice of the stored new
/// operators and undo as the matching splice of the old ones — geometry is never
/// re-matched, so undo/redo of a text edit is faithful even when other edits
/// have since shifted the page.
/// </summary>
public sealed class TextEditCommand : IEditCommand
{
    private readonly IPdfEngine _engine;
    private readonly int _pageIndex;
    private readonly int _runIndex;
    private readonly string _newText;
    private bool _hasReceipt;
    private PdfTextEditReceipt? _receipt;

    public TextEditCommand(IPdfEngine engine, int pageIndex, int runIndex, string newText)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        if (runIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runIndex));
        }

        ArgumentException.ThrowIfNullOrEmpty(newText);

        _engine = engine;
        _pageIndex = pageIndex;
        _runIndex = runIndex;
        _newText = newText;
    }

    /// <summary>Stable label for the undo menu and journal records.</summary>
    public string Name => "Edit text";

    /// <summary>The page that was edited (0-based).</summary>
    public int PageIndex => _pageIndex;

    /// <summary>The run index this command rewrote, stable within the document state it was created for.</summary>
    public int RunIndex => _runIndex;

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (_hasReceipt)
        {
            // Redo: re-apply the stored new operators exact.
            await _engine.RevertTextEditAsync(_pageIndex, _receipt!, redo: true, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            PdfTextEditReceipt receipt = await _engine
                .RewriteTextRunAsync(_pageIndex, _runIndex, _newText, cancellationToken).ConfigureAwait(false);
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