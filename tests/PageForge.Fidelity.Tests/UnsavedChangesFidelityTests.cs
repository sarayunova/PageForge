// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;
using PageForge.MuPdfInterop;
using Xunit;

namespace PageForge.Fidelity.Tests;

/// <summary>
/// The unsaved-changes flag, against the real shim.
///
/// TRD §6 requires that the application not lose unsaved edits on a crash. That
/// needs something able to answer "is there anything to lose", and nothing in
/// the codebase could: there was no dirty tracking of any kind.
///
/// This is the test that the answer is a real one. It has to be, because a flag
/// that is always false would satisfy every structural check while quietly
/// making a recovery buffer never fire - the exact failure shape this project
/// keeps producing. So both directions are asserted: clean after opening, dirty
/// after an edit.
/// </summary>
public sealed class UnsavedChangesFidelityTests
{
    private static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "corpus", name);

    [Fact]
    public async Task Unsaved_changes_flag_follows_the_document()
    {
        string source = Fixture("contract-multipage.pdf");
        string output = Path.Combine(AppContext.BaseDirectory, $"dirty-{Guid.NewGuid():N}.pdf");

        try
        {
            await using MuPdfEngine engine = MuPdfEngine.Create();
            await engine.OpenAsync(source);

            Assert.False(
                await engine.HasUnsavedChangesAsync(),
                "A freshly opened document reports unsaved changes. If this were always " +
                "true, an autosave built on it would write continuously.");

            // Any mutation will do; an annotation is the cheapest that certainly
            // changes the document.
            await engine.AddAnnotationAsync(
                0,
                new AnnotBuildSpec
                {
                    Type = AnnotationType.Text,
                    X0 = 72,
                    Y0 = 700,
                    X1 = 200,
                    Y1 = 720,
                });

            Assert.True(
                await engine.HasUnsavedChangesAsync(),
                "The document reports no unsaved changes after being edited. A recovery " +
                "buffer reading this flag would never fire and the edit would be lost on " +
                "a crash, which is the requirement this exists for.");

            await engine.SaveAsAsync(output);
            Assert.True(File.Exists(output));
        }
        finally
        {
            TryDelete(output);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
