// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

namespace PageForge.App.Wpf;

/// <summary>
/// Documents named on the command line.
///
/// Explorer's Open with, a double-clicked .pdf, and the installer's file
/// association all start the program as <c>PageForge.App.Wpf.exe "path.pdf"</c>.
/// The association is worthless if that path is ignored, so startup opens
/// exactly these and does not also open the development sample.
/// </summary>
internal static class StartupDocuments
{
    /// <summary>
    /// The .pdf paths in <paramref name="args"/>, skipping the process path and
    /// the flags the shell already understands.
    /// </summary>
    /// <remarks>
    /// <c>--theme</c> consumes the following token, which is a theme name.
    /// A token that starts with <c>-</c> is a flag, not a document. Anything
    /// else is a document only when it names a PDF, so an unknown flag's value
    /// is not reported as a failed open.
    /// </remarks>
    public static IReadOnlyList<string> FromArgs(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var documents = new List<string>();
        for (int i = 1; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg.Equals("--smoke", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (arg.Equals("--theme", StringComparison.OrdinalIgnoreCase))
            {
                // The for-loop's own increment then steps past the theme name.
                i++;
                continue;
            }

            if (arg.StartsWith('-'))
            {
                continue;
            }

            if (arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                documents.Add(arg);
            }
        }

        return documents;
    }
}
