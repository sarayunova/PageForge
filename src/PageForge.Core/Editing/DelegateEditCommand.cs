// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Editing;

/// <summary>
/// A lightweight <see cref="IEditCommand"/> backed by caller-supplied Do/Undo
/// closures. The workhorse for recording a mutation whose reversal is a simple
/// swap: snapshot the old value in the capture, provide the inverse callbacks,
/// and the stack handles push/undo/redo. Once a mutation needs engine
/// interaction (e.g. a content-stream rewrite), prefer a dedicated command type
/// over closures, but keep it implementing <see cref="IEditCommand"/>.
/// </summary>
public sealed class DelegateEditCommand : IEditCommand
{
    private readonly Func<CancellationToken, ValueTask> _execute;
    private readonly Func<CancellationToken, ValueTask> _undo;

    public DelegateEditCommand(
        string name,
        Func<CancellationToken, ValueTask> execute,
        Func<CancellationToken, ValueTask> undo)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Edit" : name.Trim();
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _undo = undo ?? throw new ArgumentNullException(nameof(undo));
    }

    public DelegateEditCommand(string name, Action execute, Action undo)
        : this(name, _ => { execute(); return ValueTask.CompletedTask; },
                     _ => { undo(); return ValueTask.CompletedTask; })
    {
    }

    /// <summary>Command label, used for undo menus and the journal.</summary>
    public string Name { get; }

    public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) =>
        _execute(cancellationToken);

    public ValueTask UndoAsync(CancellationToken cancellationToken = default) =>
        _undo(cancellationToken);
}