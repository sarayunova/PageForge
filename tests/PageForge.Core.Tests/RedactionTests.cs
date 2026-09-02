// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-SEC-02 unit tests: the <see cref="RedactionService"/> helpers and the
/// undoable <see cref="ApplyRedactionsCommand"/> driven by an
/// <see cref="EditCommandStack"/> — all against the fake engine, with no native
/// dependency. The destructive apply is isolated with the snapshot-on-execute /
/// restore-on-undo pattern, and the command is <see cref="IDisposable"/> so the
/// stack prunes its scratch file when the redo branch is cleared or the session
/// closes.
/// </summary>
public sealed class RedactionTests
{
    private const int Page = 0;

    private static readonly PdfRect Region = new(100, 200, 350, 240);

    // --- Marking -------------------------------------------------------------

    [Fact]
    public async Task MarkRegion_calls_the_engine_with_normalized_bounds()
    {
        var engine = new FakePdfEngine(1);
        await RedactionService.MarkRegionAsync(engine, Page, new PdfRect(350, 240, 100, 200));

        string recorded = Assert.Single(engine.RedactionsMarked);
        Assert.Equal("0:100:200:350:240", recorded);
        Assert.Equal(Region, Assert.Single(engine.StoredRedactions(Page)));
    }

    [Fact]
    public async Task MarkRegion_rejects_a_degenerate_region()
    {
        var engine = new FakePdfEngine(1);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RedactionService.MarkRegionAsync(engine, Page, new PdfRect(100, 200, 100, 240)).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            RedactionService.MarkRegionAsync(engine, Page, new PdfRect(100, 200, 350, 200)).AsTask());
    }

    [Fact]
    public async Task MarkRegion_rejects_a_negative_page()
    {
        var engine = new FakePdfEngine(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RedactionService.MarkRegionAsync(engine, -1, Region).AsTask());
    }

    // --- Apply (direct engine route) -----------------------------------------

    [Fact]
    public async Task Apply_removes_stored_marks_and_reports_the_count()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRedaction(Page, Region);
        engine.AddStoredRedaction(Page, new PdfRect(400, 200, 500, 240));

        int applied = await RedactionService.ApplyRedactionsAsync(engine, Page);

        Assert.Equal(2, applied);
        Assert.Empty(engine.StoredRedactions(Page));
        Assert.Equal(Page, Assert.Single(engine.RedactedPages));
    }

    [Fact]
    public async Task Apply_on_a_page_with_no_marks_returns_zero()
    {
        var engine = new FakePdfEngine(1);
        int applied = await RedactionService.ApplyRedactionsAsync(engine, Page);
        Assert.Equal(0, applied);
    }

    [Fact]
    public async Task Apply_forwarding_non_default_options()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRedaction(Page, Region);
        var options = new RedactionOptions(
            BlackBox: false,
            ImageMethod: RedactionImageMethod.BlackoutPixels,
            LineArtMethod: RedactionLineArtMethod.RemoveIfTouched,
            TextMethod: RedactionTextMethod.RemoveInvisibleOnly);

        await RedactionService.ApplyRedactionsAsync(engine, Page, options);

        Assert.Equal(options, engine.LastRedactionOptions);
    }

    // --- ApplyRedactionsCommand + EditCommandStack ---------------------------

    [Fact]
    public async Task Apply_command_applies_then_undo_restores_then_redo_reapplies()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRedaction(Page, Region);
        var stack = new EditCommandStack();

        var command = RedactionService.ApplyAsync(engine, Page);
        await stack.PushAsync(command);

        Assert.Empty(engine.StoredRedactions(Page));
        Assert.Equal(1, stack.UndoDepth);
        Assert.Equal("Apply redactions", command.Name);

        await stack.UndoAsync();
        Assert.Equal(Region, Assert.Single(engine.StoredRedactions(Page)), TestRectComparer.Instance);
        Assert.Single(engine.RestoredSnapshots);

        await stack.RedoAsync();
        Assert.Empty(engine.StoredRedactions(Page));
    }

    [Fact]
    public async Task Apply_command_reports_the_applied_count()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRedaction(Page, Region);
        engine.AddStoredRedaction(Page, new PdfRect(400, 200, 500, 240));

        var command = RedactionService.ApplyAsync(engine, Page);
        await new EditCommandStack().PushAsync(command);

        Assert.Equal(2, command.AppliedCount);
    }

    [Fact]
    public async Task Undo_before_execution_throws()
    {
        var engine = new FakePdfEngine(1);
        var command = RedactionService.ApplyAsync(engine, Page);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.UndoAsync().AsTask());
    }

    // --- IDisposable / snapshot pruning --------------------------------------

    [Fact]
    public async Task Command_dispose_deletes_the_snapshot_file()
    {
        var engine = new FakePdfEngine(1);
        var command = RedactionService.ApplyAsync(engine, Page);
        await command.ExecuteAsync();

        string? snapshot = engine.LastSavePath;
        Assert.NotNull(snapshot);
        Assert.True(File.Exists(snapshot), "The pre-apply snapshot should exist on disk.");

        command.Dispose();
        Assert.False(File.Exists(snapshot), "Disposing the command must delete its snapshot.");
    }

    [Fact]
    public async Task Stack_pruning_the_redo_branch_disposes_the_command()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredRedaction(Page, Region);
        var stack = new EditCommandStack();

        var apply = RedactionService.ApplyAsync(engine, Page);
        await stack.PushAsync(apply);
        string? snapshot = engine.LastSavePath;
        Assert.NotNull(snapshot);
        Assert.True(File.Exists(snapshot));

        await stack.UndoAsync(); // apply -> redo branch

        // A new edit prunes the redo branch: the apply command must be disposed,
        // deleting its snapshot scratch file.
        await stack.PushAsync(new DelegateEditCommand("no-op", () => { }, () => { }));
        Assert.False(File.Exists(snapshot));
    }

    private sealed class TestRectComparer : IEqualityComparer<PdfRect>
    {
        public static readonly TestRectComparer Instance = new();

        public bool Equals(PdfRect x, PdfRect y)
            => x.X0 == y.X0 && x.Y0 == y.Y0 && x.X1 == y.X1 && x.Y1 == y.Y1;

        public int GetHashCode(PdfRect obj) => HashCode.Combine(obj.X0, obj.Y0, obj.X1, obj.Y1);
    }
}