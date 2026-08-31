// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Runtime.CompilerServices;

namespace PageForge.Core.Editing;

/// <summary>
/// The editing session's undo/redo backbone (FR-EDIT-05). Every mutation of a
/// document is pushed here as an <see cref="IEditCommand"/>; the stack executes
/// Do on push, reverses on undo, and replays on redo. Depth is unlimited —
/// nothing is evicted, so a session's full edit history stays reversible.
///
/// Semantics:
///  - <see cref="PushAsync"/> executes the command and records it. A command
///    that throws during execution is NOT recorded, and the redo branch is
///    cleared (a new edit invalidates all undone work).
///  - <see cref="UndoAsync"/>/<see cref="RedoAsync"/> pop one command and move
///    it to the other branch.
///  - <see cref="Clear"/> drops all history (used when closing a document).
///
/// Threading: not thread-safe. The document's owner worker serializes all
/// accesses (the same worker that serializes the engine). Re-entrant calls
/// (push/undo/redo from inside another command's Do) throw
/// <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class EditCommandStack
{
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();
    private bool _busy;

    /// <summary>Raised after the stack transitions (push / undo / redo / clear).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Number of recorded, currently-applied commands (undo depth).</summary>
    public int UndoDepth => _undo.Count;

    /// <summary>Number of commands currently undone and available to redo.</summary>
    public int RedoDepth => _redo.Count;

    /// <summary>True when at least one command can be undone and no operation is in flight.</summary>
    public bool CanUndo => _undo.Count > 0 && !_busy;

    /// <summary>True when at least one undone command can be redone and no operation is in flight.</summary>
    public bool CanRedo => _redo.Count > 0 && !_busy;

    /// <summary>
    /// Executes <paramref name="command"/> and records it as the newest edit,
    /// clearing the redo branch. If execution throws, nothing is recorded and
    /// the document is left in whatever state the command produced.
    /// </summary>
    /// <returns>The same command, for callers that want to reference it.</returns>
    public async ValueTask<IEditCommand> PushAsync(IEditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureNotBusy();

        _busy = true;
        try
        {
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _undo.Push(command);
            _redo.Clear();
            OnStateChanged();
            return command;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Reverts the most recent edit and moves it to the redo branch. Returns the
    /// command that was undone, or null when nothing is left to undo.
    /// </summary>
    public async ValueTask<IEditCommand?> UndoAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotBusy();
        if (_undo.Count == 0)
        {
            return null;
        }

        _busy = true;
        try
        {
            IEditCommand command = _undo.Pop();
            await command.UndoAsync(cancellationToken).ConfigureAwait(false);
            _redo.Push(command);
            OnStateChanged();
            return command;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>
    /// Re-applies the most recently undone edit and moves it back to the undo
    /// branch. Returns the command that was redone, or null when nothing is left
    /// to redo.
    /// </summary>
    public async ValueTask<IEditCommand?> RedoAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotBusy();
        if (_redo.Count == 0)
        {
            return null;
        }

        _busy = true;
        try
        {
            IEditCommand command = _redo.Pop();
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            _undo.Push(command);
            OnStateChanged();
            return command;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Drops all undo/redo history for the current document session.</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        OnStateChanged();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureNotBusy()
    {
        if (_busy)
        {
            throw new InvalidOperationException(
                "EditCommandStack is busy: push/undo/redo must not re-enter an in-flight operation.");
        }
    }

    private void OnStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}