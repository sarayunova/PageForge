// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Editing;
using PageForge.Core.Pdf;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-04 unit tests: pure transform geometry, the <see cref="PageObjectService"/>
/// helpers, and the undoable move/resize/replace commands driven by the
/// <see cref="EditCommandStack"/> — all against the fake engine, with no native
/// dependency.
/// </summary>
public sealed class PageObjectTests
{
    private const int Page = 0;

    private static readonly PdfRect ImageBounds = new(100, 100, 200, 140);

    private static FakePdfEngine CreateEngineWithImage()
    {
        var engine = new FakePdfEngine(1);
        engine.AddStoredObject(Page, new PdfPageObject(PageObjectKind.Image, "img1", ImageBounds, "logo.png"));
        return engine;
    }

    // --- Pure geometry -------------------------------------------------------

    [Fact]
    public void Translate_moves_the_rect_keeping_size()
    {
        var moved = PageObjectGeometry.Translate(ImageBounds, 15, -5);
        Assert.Equal(new PdfRect(115, 95, 215, 135), moved);
        Assert.Equal(ImageBounds.Width, moved.Width);
        Assert.Equal(ImageBounds.Height, moved.Height);
    }

    [Fact]
    public void ResizeFromBottomLeft_keeps_the_bottom_left_corner()
    {
        var resized = PageObjectGeometry.ResizeFromBottomLeft(ImageBounds, 60, 30);
        Assert.Equal(new PdfRect(100, 100, 160, 130), resized);
    }

    [Fact]
    public void ResizeFromBottomLeft_rejects_negative_dimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PageObjectGeometry.ResizeFromBottomLeft(ImageBounds, -1, 10));
    }

    [Fact]
    public void ScaleFromCenter_keeps_the_center()
    {
        var scaled = PageObjectGeometry.ScaleFromCenter(ImageBounds, 2);
        Assert.Equal(new PdfRect(50, 80, 250, 160), scaled);
        Assert.Equal(150, (scaled.X0 + scaled.X1) / 2);
        Assert.Equal(120, (scaled.Y0 + scaled.Y1) / 2);
    }

    [Fact]
    public void ResizeToWidthAspect_preserves_aspect_ratio()
    {
        var resized = PageObjectGeometry.ResizeToWidthAspect(ImageBounds, 200);
        Assert.Equal(200, resized.Width);
        Assert.Equal(80, resized.Height, 3);
        Assert.Equal(ImageBounds.Width / ImageBounds.Height, resized.Width / resized.Height, 3);
    }

    [Fact]
    public void KeepsAspectRatio_detects_warp()
    {
        Assert.True(PageObjectGeometry.KeepsAspectRatio(ImageBounds, new PdfRect(0, 0, 50, 20)));
        Assert.False(PageObjectGeometry.KeepsAspectRatio(ImageBounds, new PdfRect(0, 0, 50, 50)));
    }

    // --- List objects --------------------------------------------------------

    [Fact]
    public async Task ListObjects_returns_seeded_objects_with_kind_and_bounds()
    {
        var engine = CreateEngineWithImage();
        IReadOnlyList<PdfPageObject> objects = await PageObjectService.ListObjectsAsync(engine, Page);
        PdfPageObject obj = Assert.Single(objects);
        Assert.Equal(PageObjectKind.Image, obj.Kind);
        Assert.Equal("img1", obj.Id);
        Assert.Equal(ImageBounds, obj.Bounds);
        Assert.Equal("Image img1", obj.Label);
    }

    [Fact]
    public async Task ListObjects_empty_page_returns_empty()
    {
        var engine = new FakePdfEngine(1);
        IReadOnlyList<PdfPageObject> objects = await PageObjectService.ListObjectsAsync(engine, Page);
        Assert.Empty(objects);
    }

    // --- Move / resize command ----------------------------------------------

    [Fact]
    public async Task Move_command_updates_bounds_via_the_stack()
    {
        var engine = CreateEngineWithImage();
        var stack = new EditCommandStack();
        PdfRect newBounds = PageObjectGeometry.Translate(ImageBounds, 20, 30);
        var command = PageObjectService.MoveResizeAsync(engine, Page, "img1", newBounds);

        await stack.PushAsync(command);

        PdfPageObject obj = Assert.Single(await engine.ListObjectsAsync(Page));
        Assert.Equal(newBounds, obj.Bounds);
        Assert.Equal(1, stack.UndoDepth);
        Assert.Equal("Move object", command.Name);
    }

    [Fact]
    public async Task Move_undo_restores_original_bounds_and_redo_reapplies()
    {
        var engine = CreateEngineWithImage();
        var stack = new EditCommandStack();
        PdfRect newBounds = PageObjectGeometry.Translate(ImageBounds, 20, 30);

        await stack.PushAsync(PageObjectService.MoveResizeAsync(engine, Page, "img1", newBounds));
        await stack.UndoAsync();
        Assert.Equal(ImageBounds, Assert.Single(await engine.ListObjectsAsync(Page)).Bounds);

        await stack.RedoAsync();
        Assert.Equal(newBounds, Assert.Single(await engine.ListObjectsAsync(Page)).Bounds);
    }

    [Fact]
    public async Task Aspect_resize_command_produces_a_tighter_rect()
    {
        var engine = CreateEngineWithImage();
        var stack = new EditCommandStack();
        PdfPageObject target = Assert.Single(await engine.ListObjectsAsync(Page));

        await stack.PushAsync(PageObjectService.ResizeToWidthAsync(engine, Page, target, 50));

        PdfPageObject moved = Assert.Single(await engine.ListObjectsAsync(Page));
        Assert.Equal(50, moved.Bounds.Width, 3);
        Assert.Equal(20, moved.Bounds.Height, 3);
    }

    [Fact]
    public async Task MoveBy_command_preserves_size()
    {
        var engine = CreateEngineWithImage();
        var stack = new EditCommandStack();
        PdfPageObject target = Assert.Single(await engine.ListObjectsAsync(Page));

        await stack.PushAsync(PageObjectService.MoveByAsync(engine, Page, target, 10, -10));

        PdfPageObject moved = Assert.Single(await engine.ListObjectsAsync(Page));
        Assert.Equal(new PdfRect(110, 90, 210, 130), moved.Bounds);
    }

    // --- Replace command -----------------------------------------------------

    [Fact]
    public async Task Replace_command_swaps_the_interior_and_keeps_bounds()
    {
        var engine = CreateEngineWithImage();
        var stack = new EditCommandStack();
        var command = PageObjectService.ReplaceAsync(
            engine, Page, "img1", new PdfObjectReplacement(@"C:\assets\new.png", "png"));

        await stack.PushAsync(command);

        PdfPageObject obj = Assert.Single(await engine.ListObjectsAsync(Page));
        Assert.Equal(@"C:\assets\new.png", obj.Name);
        Assert.Equal(ImageBounds, obj.Bounds);
        Assert.Equal("Replace object", command.Name);
    }

    [Fact]
    public async Task Replace_undo_restores_original_and_redo_reapplies()
    {
        var engine = CreateEngineWithImage();
        var stack = new EditCommandStack();
        var replacement = new PdfObjectReplacement(@"C:\assets\new.png", "png");

        await stack.PushAsync(PageObjectService.ReplaceAsync(engine, Page, "img1", replacement));
        await stack.UndoAsync();
        Assert.Equal("logo.png", Assert.Single(await engine.ListObjectsAsync(Page)).Name);

        await stack.RedoAsync();
        Assert.Equal(@"C:\assets\new.png", Assert.Single(await engine.ListObjectsAsync(Page)).Name);
    }

    // --- Service / engine validation ----------------------------------------

    [Fact]
    public void MoveResize_requires_a_non_empty_object_id()
    {
        var engine = CreateEngineWithImage();
        Assert.Throws<ArgumentException>(() => PageObjectService.MoveResizeAsync(engine, Page, "", ImageBounds));
    }

    [Fact]
    public void Replace_rejects_a_null_or_empty_replacement()
    {
        var engine = CreateEngineWithImage();
        Assert.Throws<ArgumentNullException>(() => PageObjectService.ReplaceAsync(engine, Page, "img1", null!));
        Assert.Throws<ArgumentException>(() => PageObjectService.ReplaceAsync(
            engine, Page, "img1", new PdfObjectReplacement("", "png")));
    }

    [Fact]
    public async Task Undo_before_execution_throws()
    {
        var engine = CreateEngineWithImage();
        var command = new ObjectEditCommand(engine, Page, "img1", ImageBounds);
        await Assert.ThrowsAsync<InvalidOperationException>(() => command.UndoAsync().AsTask());
    }

    [Fact]
    public async Task Move_by_unknown_object_id_throws()
    {
        var engine = CreateEngineWithImage();
        var objectEdit = new ObjectEditCommand(engine, Page, "missing", ImageBounds);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => objectEdit.ExecuteAsync().AsTask());
    }
}
