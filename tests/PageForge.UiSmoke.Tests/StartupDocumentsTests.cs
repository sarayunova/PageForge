// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.App.Wpf;
using Xunit;

namespace PageForge.UiSmoke.Tests;

/// <summary>
/// The command line is how a registered PDF handler receives the document.
/// These cases are the ones that would make Open with open nothing, open the
/// theme name as a file, or open a PDF that was only the value of a flag.
/// </summary>
public sealed class StartupDocumentsTests
{
    [Theory]
    [InlineData("PageForge.App.Wpf.exe", "")]
    [InlineData("PageForge.App.Wpf.exe|--smoke", "")]
    [InlineData("PageForge.App.Wpf.exe|--theme|dark", "")]
    [InlineData("PageForge.App.Wpf.exe|--theme", "")]
    [InlineData("PageForge.App.Wpf.exe|--theme|dark|C:\\docs\\a.pdf", "C:\\docs\\a.pdf")]
    [InlineData("PageForge.App.Wpf.exe|--theme|C:\\not-a-theme.pdf", "")]
    [InlineData("PageForge.App.Wpf.exe|C:\\docs\\a.pdf|C:\\docs\\b.PDF", "C:\\docs\\a.pdf|C:\\docs\\b.PDF")]
    [InlineData("PageForge.App.Wpf.exe|notes.txt", "")]
    [InlineData("PageForge.App.Wpf.exe|--unknown|C:\\docs\\a.pdf", "C:\\docs\\a.pdf")]
    public void FromArgs_selects_pdf_documents_and_skips_flags(string rawArgs, string rawExpected)
    {
        string[] args = rawArgs.Split('|');
        string[] expected = rawExpected.Length == 0 ? [] : rawExpected.Split('|');

        IReadOnlyList<string> actual = StartupDocuments.FromArgs(args);

        Assert.Equal(expected, actual);
    }
}
