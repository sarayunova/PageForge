// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Editing;

/// <summary>
/// A single undoable document mutation (FR-EDIT-05). Every edit in PageForge —
/// text-run rewrite, image/vector move, page-reorder build, annotation change —
/// is expressed as an <see cref="IEditCommand"/> with reversible Do/Undo
/// semantics, per TSD §3.1 and §6. Commands are handed to an
/// <see cref="EditCommandStack"/> which executes Do on push and pairs every
/// undo with the corresponding redo.
///
/// Implementations must be re-entrant: <see cref="ExecuteAsync"/> and
/// <see cref="UndoAsync"/> will be called once per direction per push/undo/redo,
/// and both must leave the document in the same observable state on repetition
/// (a command that is pushed executes Do; the matching undo restores the exact
/// post-command state of the document plus this command's own effect removed).
///
/// Threading: like the engine seam, commands are invoked by the document's
/// single owner worker — never from the UI thread, and never concurrently with
/// each other.
/// </summary>
public interface IEditCommand
{
    /// <summary>
    /// A short human/machine-readable label for the edit (e.g. "Edit text",
    /// "Move image", "Reorder pages"). Used for journal records, UI undo menus,
    /// and diagnostics. Should be stable across instances of the same operation.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Applies the mutation to the document. Called by the stack on push and on
    /// redo. Must throw if the mutation cannot be applied, in which case the
    /// stack does not record the command.
    /// </summary>
    ValueTask ExecuteAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverts the mutation applied by <see cref="ExecuteAsync"/>, restoring the
    /// document to its pre-command state. Called by the stack on undo.
    /// </summary>
    ValueTask UndoAsync(CancellationToken cancellationToken = default);
}