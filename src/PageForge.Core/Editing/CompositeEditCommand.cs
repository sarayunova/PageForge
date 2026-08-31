// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.Core.Editing;

/// <summary>
/// Groups several edits into one undoable unit ("macro"). Pushing the composite
/// executes its children in order and records the composite as a single stack
/// entry, so one undo reverts the whole batch, with child undos applied in
/// reverse order (last child first). Used for operations that legitimately span
/// several mutations (e.g. move image: update content stream, then update page
/// bounds) and for turning a multi-step UI gesture into one undo step.
/// </summary>
public sealed class CompositeEditCommand : IEditCommand
{
    private readonly IReadOnlyList<IEditCommand> _children;

    public CompositeEditCommand(string name, IEnumerable<IEditCommand> children)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(children);

        _children = children as IReadOnlyList<IEditCommand> ?? children.ToArray();
        if (_children.Count == 0)
        {
            throw new ArgumentException("A composite edit requires at least one child command.", nameof(children));
        }

        if (_children.Any(c => c is null))
        {
            throw new ArgumentException("A composite edit cannot contain null child commands.", nameof(children));
        }

        Name = name.Trim();
    }

    /// <summary>Command label; the caller supplies it (child names are not concatenated).</summary>
    public string Name { get; }

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        foreach (IEditCommand child in _children)
        {
            await child.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask UndoAsync(CancellationToken cancellationToken = default)
    {
        for (int i = _children.Count - 1; i >= 0; i--)
        {
            await _children[i].UndoAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}