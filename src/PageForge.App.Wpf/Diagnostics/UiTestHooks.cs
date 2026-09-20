// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using Microsoft.Extensions.Logging;

namespace PageForge.App.Wpf.Diagnostics;

/// <summary>
/// The one affordance the UI suite needs from the app: somewhere to put a file
/// without a save dialog standing in the way.
///
/// This exists because a native common file dialog cannot be driven reliably
/// across Windows images. The UiSmoke suite drives one through Win32 messages,
/// which works on a developer machine and silently cancels on the hosted CI
/// runner - the dialog closes, nothing is written, and the app correctly reports
/// nothing, because from its point of view the user pressed Cancel. The result
/// was that reorder-and-save, a real product path, could not be covered by CI at
/// all (issue #6).
///
/// The trade is deliberate and narrow. What gets tested is the behaviour that
/// matters - reorder the pages, write the file, reopen it - and what stops being
/// tested is the file dialog itself, which is Windows' code rather than this
/// project's. Automating the chrome was never the point; it was only ever the
/// means of reaching the part that is.
///
/// Safety properties, because a hook in shipping code has to earn its place:
///
///   - Inert unless the environment variable is set, which no user session sets.
///   - Read once at startup, so nothing can change a running app's behaviour by
///     altering the environment underneath it.
///   - Consumed on first use, so it redirects exactly one save and cannot
///     silently swallow every subsequent one.
///   - It supplies a destination only. Nothing about what is written, or whether
///     the write succeeds, changes - those are the paths under test and they are
///     untouched.
/// </summary>
internal static class UiTestHooks
{
    /// <summary>Environment variable naming the file the next save should write
    /// to instead of asking. Set by the UI smoke suite when it launches the app.</summary>
    public const string SaveTargetVariable = "PAGEFORGE_UITEST_SAVE_PATH";

    // Captured at type initialisation: the value the process STARTED with.
    private static string? _saveTarget = Environment.GetEnvironmentVariable(SaveTargetVariable);

    /// <summary>
    /// The scripted save destination, if there is one, clearing it as it goes so
    /// only the next save is affected. Returns null in every normal run, and the
    /// caller then shows the dialog exactly as before.
    /// </summary>
    public static string? TakeSaveTarget()
    {
        string? target = _saveTarget;
        if (target is not null)
        {
            _saveTarget = null;
            AppLog.For(typeof(UiTestHooks)).LogInformation(
                "UI test hook: saving to {Target} without showing a dialog. " +
                "This happens only when {Variable} is set.",
                target,
                SaveTargetVariable);
        }

        return target;
    }
}
