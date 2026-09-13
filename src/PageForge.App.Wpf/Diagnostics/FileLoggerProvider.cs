// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PageForge.App.Wpf.Diagnostics;

/// <summary>
/// Appends log lines to a single file, rolling once when it gets large.
///
/// Hand-rolled rather than taken from Serilog or NLog on purpose: PageForge is
/// AGPLv3 and every dependency has to be licence-checked and recorded in
/// THIRD-PARTY-NOTICES.md, which is a poor trade for roughly eighty lines of
/// append-with-a-lock. The logging abstraction itself is the part worth taking
/// from Microsoft, because it is what lets the sink be swapped later without
/// touching a call site.
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    /// <summary>Roll at 4 MB. Large enough to hold a session's render failures,
    /// small enough to attach to a bug report.</summary>
    private const long MaxBytes = 4L * 1024 * 1024;

    /// <summary>Serializes writes across every logger this provider hands out;
    /// renders log from worker threads while the UI logs from the dispatcher.</summary>
    private readonly object _gate = new();

    private readonly string _path;
    private readonly LogLevel _minimumLevel;
    private bool _disabled;

    internal FileLoggerProvider(string path, LogLevel minimumLevel)
    {
        _path = path;
        _minimumLevel = minimumLevel;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        // Each write opens, appends and closes, so there is no handle to release.
        // Disposing only stops further writes.
        lock (_gate)
        {
            _disabled = true;
        }
    }

    private bool IsEnabled(LogLevel level) => !_disabled && level >= _minimumLevel && level != LogLevel.None;

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                Roll();

                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [").Append(Abbreviate(level)).Append("] ")
                    .Append(category).Append(": ").Append(message);

                if (exception != null)
                {
                    line.AppendLine().Append(exception);
                }

                File.AppendAllText(_path, line.AppendLine().ToString(), Encoding.UTF8);
            }
            catch (Exception)
            {
                // A log that cannot be written must not take the app with it, and
                // must not retry on every subsequent call either — a full disk or a
                // revoked permission will not fix itself during this session.
                _disabled = true;
            }
        }
    }

    /// <summary>Keeps one previous log alongside the current one, so a crash's
    /// context is not immediately overwritten by the next session.</summary>
    private void Roll()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < MaxBytes)
        {
            return;
        }

        string previous = _path + ".1";
        File.Delete(previous);
        File.Move(_path, previous);
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _category;

        internal FileLogger(FileLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        // Scopes are not written; nothing in the shell nests logging context yet.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(logLevel, _category, formatter(state, exception), exception);
        }
    }
}
