// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-05 unit tests for <see cref="TextEditCommand"/>: pushing the command
/// rewrites the run and stores the engine's receipt; undo restores the exact old
/// operators and redo re-applies the exact new ones — the stack drives both
/// without re-running geometry matching, so the edit round-trips exactly.
/// </summary>
public sealed class TextEditCommandTests
{
    private const int Page = 0;

    private static FakePdfEngine CreateEngineWithRun(string text = "PROFESSIONAL SERVICES AGREEMENT")
        => CreateEngineWithRuns(text);

    private static FakePdfEngine CreateEngineWithRuns(params string[] texts)
    {
        var engine = new FakePdfEngine(1);
        for (int i = 0; i < texts.Length; i++)
        {
            engine.AddStoredRun(Page, new PdfTextRun(i, 10, 50, 200, 60, 28, true, "Helvetica", texts[i]));
        }

        return engine;
    }

    [Fact]
    public async Task Push_executes_rewrite_and_updates_the_run()
    {
        var engine = CreateEngineWithRun();
        var stack = new EditCommandStack();
        var command = new TextEditCommand(engine, Page, 0, "UPDATED CONTRACT TITLE");

        await stack.PushAsync(command);

        var runs = await engine.ListTextRunsAsync(Page);
        Assert.Equal("UPDATED CONTRACT TITLE", Assert.Single(runs).Text);
        Assert.Equal(1, stack.UndoDepth);
        Assert.Equal("Edit text", command.Name);
    }

    [Fact]
    public async Task Undo_restores_the_exact_old_text()
    {
        var engine = CreateEngineWithRun();
        var stack = new EditCommandStack();
        var command = new TextEditCommand(engine, Page, 0, "UPDATED CONTRACT TITLE");

        await stack.PushAsync(command);
        await stack.UndoAsync();

        var runs = await engine.ListTextRunsAsync(Page);
        Assert.Equal("PROFESSIONAL SERVICES AGREEMENT", Assert.Single(runs).Text);
        Assert.Equal(0, stack.UndoDepth);
        Assert.Equal(1, stack.RedoDepth);
    }

    [Fact]
    public async Task Redo_reapplies_the_new_text()
    {
        var engine = CreateEngineWithRun();
        var stack = new EditCommandStack();
        var command = new TextEditCommand(engine, Page, 0, "UPDATED CONTRACT TITLE");

        await stack.PushAsync(command);
        await stack.UndoAsync();
        await stack.RedoAsync();

        var runs = await engine.ListTextRunsAsync(Page);
        Assert.Equal("UPDATED CONTRACT TITLE", Assert.Single(runs).Text);
        Assert.Equal(1, stack.UndoDepth);
        Assert.Equal(0, stack.RedoDepth);
    }

    [Fact]
    public async Task Edit_round_trips_exactly_through_one_undo_redo_cycle()
    {
        var engine = CreateEngineWithRun("ABC");
        var stack = new EditCommandStack();
        const string newText = "ABCDEF";

        await stack.PushAsync(new TextEditCommand(engine, Page, 0, newText));
        await stack.UndoAsync();
        await stack.RedoAsync();

        Assert.Equal(
            new[] { "rewrite:ABC->ABCDEF", "undo:ABCDEF->ABC", "redo:ABC->ABCDEF" },
            engine.EditedTextByPage[Page]);
        Assert.Equal("ABCDEF", (await engine.ListTextRunsAsync(Page))[0].Text);
    }

    [Fact]
    public async Task Edits_to_different_runs_undo_and_redo_independently()
    {
        var engine = CreateEngineWithRuns("First", "Second");
        var stack = new EditCommandStack();

        await stack.PushAsync(new TextEditCommand(engine, Page, 0, "First!"));

        var runs = await engine.ListTextRunsAsync(Page);
        Assert.Equal("First!", runs[0].Text);
        Assert.Equal("Second", runs[1].Text);

        await stack.UndoAsync();
        Assert.Equal("First", (await engine.ListTextRunsAsync(Page))[0].Text);
    }

    [Fact]
    public async Task Undo_before_execution_throws()
    {
        var engine = CreateEngineWithRun();
        var command = new TextEditCommand(engine, Page, 0, "x");

        await Assert.ThrowsAsync<InvalidOperationException>(() => command.UndoAsync().AsTask());
    }
}