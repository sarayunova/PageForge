// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PageForge.App.Wpf.Diagnostics;

/// <summary>
/// The application's logging seam.
///
/// Diagnostics used to go to <see cref="System.Diagnostics.Trace"/>, which in a
/// released WPF build writes nowhere anybody can read: there is no console, and
/// no listener is registered. A render that failed on a user's machine therefore
/// left no trail at all, which is precisely how the blank-viewer bug (FR-VIEW-01)
/// survived to a user. Logging goes to a file under the user's local app data so
/// a bug report can carry it.
///
/// This is deliberately a static holder rather than constructor injection. The
/// shell has no DI container yet; introducing one is Phase U4's job, and it would
/// have to thread a logger through every view and code-behind to reach the places
/// that actually fail. When the container arrives, <see cref="Factory"/> is the
/// single seam to repoint, and the call sites keep their <see cref="ILogger"/>.
/// </summary>
public static class AppLog
{
    private static ILoggerFactory? _factory;

    /// <summary>Where the log is written, for the about box and bug reports.</summary>
    public static string LogFilePath { get; private set; } = string.Empty;

    /// <summary>
    /// The factory backing every logger. Null until <see cref="Initialize"/> runs, so
    /// <see cref="For"/> hands back a no-op logger rather than throwing — a logging
    /// seam must never be the reason the app fails to start.
    /// </summary>
    public static ILoggerFactory Factory => _factory ?? NullLoggerFactory.Instance;

    /// <summary>
    /// Starts file logging. Safe to call more than once; the second call is ignored.
    /// Any failure here (a read-only profile, a locked file, a policy-restricted
    /// directory) leaves the app running with logging disabled rather than failing
    /// to start.
    /// </summary>
    /// <param name="minimumLevel">Below this, messages are dropped unwritten.</param>
    public static void Initialize(LogLevel minimumLevel = LogLevel.Information)
    {
        if (_factory != null)
        {
            return;
        }

        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PageForge",
                "logs");
            Directory.CreateDirectory(directory);
            LogFilePath = Path.Combine(directory, "pageforge.log");

            var provider = new FileLoggerProvider(LogFilePath, minimumLevel);
            _factory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(minimumLevel);
                builder.AddProvider(provider);
            });
        }
        catch (Exception)
        {
            // No log is a degraded app, not a broken one.
            _factory = null;
            LogFilePath = string.Empty;
        }
    }

    /// <summary>A logger named for <typeparamref name="T"/>.</summary>
    public static ILogger For<T>() => For(typeof(T));

    /// <summary>A logger named for a type. The non-generic form exists because
    /// static classes cannot be type arguments, and some of the shell's most
    /// failure-prone code (theme detection, for one) is static.</summary>
    public static ILogger For(Type type) => Factory.CreateLogger(type.FullName ?? type.Name);

    /// <summary>Flushes and releases the log file. Call on shutdown.</summary>
    public static void Shutdown()
    {
        _factory?.Dispose();
        _factory = null;
    }
}
