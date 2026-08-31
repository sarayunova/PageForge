// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Editing;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-05 command-layer tests: the edit command stack must execute Do on
/// push, reverse on undo, replay on redo, clear the redo branch on a new edit,
/// stay unlimited in depth, reject re-entrant mutations, and never record a
/// command whose Do failed. Composite edits must run Do in order and Undo in
/// reverse. Pure C# — no engine, no native dependency.
/// </summary>
public sealed class EditCommandStackTests
{
    private sealed class Recorder
    {
        public int Value { get; set; }
    }

    private sealed class CounterCommand : IEditCommand
    {
        private readonly Recorder _target;
        private readonly int _oldValue;
        private readonly int _newValue;
        private readonly bool _failOnDo;

        public CounterCommand(Recorder target, int oldValue, int newValue, bool failOnDo = false)
        {
            _target = target;
            _oldValue = oldValue;
            _newValue = newValue;
            _failOnDo = failOnDo;
        }

        public string Name { get; } = "set counter";

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            if (_failOnDo)
            {
                throw new InvalidOperationException("simulated Do failure");
            }

            _target.Value = _newValue;
            return ValueTask.CompletedTask;
        }

        public ValueTask UndoAsync(CancellationToken cancellationToken = default)
        {
            _target.Value = _oldValue;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Push_executes_the_command_and_records_it()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();

        await stack.PushAsync(new CounterCommand(target, 0, 5));

        Assert.Equal(5, target.Value);
        Assert.True(stack.CanUndo);
        Assert.False(stack.CanRedo);
        Assert.Equal(1, stack.UndoDepth);
        Assert.Equal(0, stack.RedoDepth);
    }

    [Fact]
    public async Task StateChanged_fires_on_transitions()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();
        int transitions = 0;
        stack.StateChanged += (_, _) => transitions++;

        await stack.PushAsync(new CounterCommand(target, 0, 1));
        await stack.UndoAsync();
        await stack.RedoAsync();
        stack.Clear();

        Assert.Equal(4, transitions);
    }

    [Fact]
    public async Task Undo_restores_previous_state_and_enables_redo()
    {
        var target = new Recorder { Value = 0 };
        var stack = new EditCommandStack();
        await stack.PushAsync(new CounterCommand(target, 0, 3));
        await stack.PushAsync(new CounterCommand(target, 3, 7));

        IEditCommand? undone = await stack.UndoAsync();

        Assert.Equal(3, target.Value);
        Assert.Equal(1, stack.UndoDepth);
        Assert.Equal(1, stack.RedoDepth);
        Assert.NotNull(undone);
        Assert.Equal("set counter", undone.Name);
    }

    [Fact]
    public async Task Redo_reapplies_the_undone_edit()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();
        await stack.PushAsync(new CounterCommand(target, 0, 3));
        await stack.PushAsync(new CounterCommand(target, 3, 7));
        await stack.UndoAsync();

        IEditCommand? redone = await stack.RedoAsync();

        Assert.Equal(7, target.Value);
        Assert.Equal(2, stack.UndoDepth);
        Assert.Equal(0, stack.RedoDepth);
        Assert.NotNull(redone);
    }

    [Fact]
    public async Task New_edit_after_undo_clears_the_redo_branch()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();
        await stack.PushAsync(new CounterCommand(target, 0, 1));
        await stack.PushAsync(new CounterCommand(target, 1, 2));
        await stack.UndoAsync();

        await stack.PushAsync(new CounterCommand(target, 1, 5));

        Assert.False(stack.CanRedo);
        Assert.Equal(0, stack.RedoDepth);
        Assert.Equal(5, target.Value);
    }

    [Fact]
    public async Task Depth_is_unlimited_through_a_large_batch()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();
        const int Count = 1000;

        for (int i = 0; i < Count; i++)
        {
            await stack.PushAsync(new CounterCommand(target, i, i + 1));
        }

        Assert.Equal(Count, stack.UndoDepth);
        Assert.Equal(Count, target.Value);

        for (int i = 0; i < Count; i++)
        {
            await stack.UndoAsync();
        }

        Assert.Equal(0, target.Value);
        Assert.Equal(0, stack.UndoDepth);
        Assert.Equal(Count, stack.RedoDepth);

        for (int i = 0; i < Count; i++)
        {
            await stack.RedoAsync();
        }

        Assert.Equal(Count, target.Value);
    }

    [Fact]
    public async Task Failed_execution_is_not_recorded()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stack.PushAsync(new CounterCommand(target, 0, 99, failOnDo: true)).AsTask());

        Assert.Equal(0, stack.UndoDepth);
        Assert.Equal(0, target.Value);
        Assert.False(stack.CanUndo);
    }

    [Fact]
    public async Task Clear_resets_the_whole_history()
    {
        var target = new Recorder();
        var stack = new EditCommandStack();
        await stack.PushAsync(new CounterCommand(target, 0, 1));
        await stack.PushAsync(new CounterCommand(target, 1, 2));
        await stack.UndoAsync();

        stack.Clear();

        Assert.Equal(0, stack.UndoDepth);
        Assert.Equal(0, stack.RedoDepth);
        Assert.False(stack.CanUndo);
        Assert.False(stack.CanRedo);
    }

    [Fact]
    public async Task Undo_with_empty_stack_returns_null()
    {
        var stack = new EditCommandStack();
        Assert.Null(await stack.UndoAsync());
        Assert.Null(await stack.RedoAsync());
    }

    [Fact]
    public async Task Reentrant_push_from_inside_a_command_is_rejected()
    {
        var stack = new EditCommandStack();
        var target = new Recorder();

        var offending = new DelegateEditCommand(
            "offending",
            async ct => await stack.PushAsync(new CounterCommand(target, 0, 1)),
            _ => ValueTask.CompletedTask);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            stack.PushAsync(offending).AsTask());

        Assert.Equal(0, stack.UndoDepth);
    }

    [Fact]
    public async Task Delegate_command_runs_do_and_undo()
    {
        var calls = new List<string>();
        var cmd = new DelegateEditCommand("toggle", () => calls.Add("do"), () => calls.Add("undo"));
        var stack = new EditCommandStack();

        await stack.PushAsync(cmd);
        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(new[] { "do", "undo", "do" }, calls);
    }

    [Fact]
    public async Task Composite_executes_children_in_order_and_undoes_in_reverse()
    {
        var calls = new List<int>();
        var stack = new EditCommandStack();

        var composite = new CompositeEditCommand("macro", Enumerable.Range(1, 3).Select(i =>
            new DelegateEditCommand($"step {i}", () => calls.Add(i), () => calls.Add(-i))));

        await stack.PushAsync(composite);
        Assert.Equal(new[] { 1, 2, 3 }, calls);

        await stack.UndoAsync();
        Assert.Equal(new[] { 1, 2, 3, -3, -2, -1 }, calls);
        Assert.Equal(0, stack.UndoDepth);
        Assert.Equal(1, stack.RedoDepth);
    }

    [Fact]
    public void Composite_requires_at_least_one_child()
    {
        Assert.Throws<ArgumentException>(() => new CompositeEditCommand("empty", Array.Empty<IEditCommand>()));
        Assert.Throws<ArgumentNullException>(() => new CompositeEditCommand("empty", null!));
    }

    [Fact]
    public async Task Push_rejects_null_command()
    {
        var stack = new EditCommandStack();
        await Assert.ThrowsAsync<ArgumentNullException>(() => stack.PushAsync(null!).AsTask());
    }
}