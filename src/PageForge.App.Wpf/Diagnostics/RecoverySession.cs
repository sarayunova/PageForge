// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PageForge.Core.Pdf;

namespace PageForge.App.Wpf.Diagnostics;

/// <summary>What was known about an autosaved document, for offering it back.</summary>
/// <param name="RecoveredFile">The autosaved copy on disk.</param>
/// <param name="SourcePath">Where the document came from, if it had a file.</param>
/// <param name="DisplayName">What the tab was called.</param>
/// <param name="SavedAtUtc">When the copy was last written.</param>
internal sealed record RecoverableDocument(
    string RecoveredFile,
    string? SourcePath,
    string DisplayName,
    DateTime SavedAtUtc);

/// <summary>
/// The autosave/recovery buffer TRD §6 requires: edits are copied aside while
/// the application runs, discarded when it closes cleanly, and offered back if
/// it did not.
///
/// Three things decide whether this is trustworthy, and each is handled
/// deliberately:
///
/// **A half-written recovery file is worse than none**, because it is offered to
/// the user as their work and then fails to open. Every autosave is written to a
/// temporary name and moved into place, so the visible file is either the
/// previous good copy or the new one.
///
/// **Recovery must never damage the session it protects.** Every operation here
/// is best-effort: failures are logged and swallowed. An autosave that threw into
/// the UI, or a disk-full condition that closed the application, would cost the
/// user the very edits this exists to keep.
///
/// **"Left over" has to mean "crashed", not "someone else is running".** Each
/// instance owns a folder and holds a lock file open inside it for its lifetime.
/// A folder whose lock can be taken exclusively belongs to a process that is
/// gone, so its contents are recoverable; a folder whose lock is held belongs to
/// a live instance and is left alone.
/// </summary>
internal sealed class RecoverySession : IDisposable
{
    private const string SidecarExtension = ".json";
    private const string LockFileName = ".lock";

    private readonly string _folder;
    private readonly FileStream? _lock;
    private bool _disposed;

    private RecoverySession(string folder, FileStream? heldLock)
    {
        _folder = folder;
        _lock = heldLock;
    }

    private static ILogger Log => AppLog.For(typeof(RecoverySession));

    /// <summary>Root of every instance's recovery folder.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PageForge",
        "recovery");

    /// <summary>
    /// Claims a folder for this process. Returns an inert session if the folder
    /// cannot be created or locked - the application must still run when its
    /// recovery buffer cannot.
    /// </summary>
    public static RecoverySession Start()
    {
        try
        {
            string folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);

            // Held open for the lifetime of the process. Its becoming lockable is
            // what later tells another instance that this one is gone.
            var held = new FileStream(
                Path.Combine(folder, LockFileName),
                FileMode.Create,
                FileAccess.ReadWrite,
                FileShare.None);

            Log.LogInformation("Recovery buffer active at {Folder}.", folder);
            return new RecoverySession(folder, held);
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Could not start the recovery buffer; edits will not be autosaved.");
            return new RecoverySession(string.Empty, null);
        }
    }

    /// <summary>Whether this session is actually recording anything.</summary>
    public bool IsActive => _lock is not null && _folder.Length > 0;

    /// <summary>
    /// Copies a document aside if it has unsaved edits. Does nothing for an
    /// unedited document, so an idle session writes nothing.
    /// </summary>
    public async Task AutosaveAsync(
        string key,
        IPdfEngine engine,
        string? sourcePath,
        string displayName,
        CancellationToken ct = default)
    {
        if (!IsActive)
        {
            return;
        }

        try
        {
            if (!await engine.HasUnsavedChangesAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            string target = Path.Combine(_folder, key + ".pdf");
            string staging = target + ".tmp";

            await engine.SaveAsAsync(staging, ct).ConfigureAwait(false);
            File.Move(staging, target, overwrite: true);

            string sidecar = Path.Combine(_folder, key + SidecarExtension);
            await File.WriteAllTextAsync(
                sidecar,
                JsonSerializer.Serialize(new SidecarData(sourcePath, displayName, DateTime.UtcNow)),
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Swallowed on purpose - see the class remarks. A recovery buffer that
            // can take the session down with it is a liability.
            Log.LogError(ex, "Autosave failed for {DisplayName}.", displayName);
        }
    }

    /// <summary>Forgets a document, for when the user closes it deliberately.</summary>
    public void Discard(string key)
    {
        if (!IsActive)
        {
            return;
        }

        TryDelete(Path.Combine(_folder, key + ".pdf"));
        TryDelete(Path.Combine(_folder, key + SidecarExtension));
        TryDelete(Path.Combine(_folder, key + ".pdf.tmp"));
    }

    /// <summary>
    /// Documents left behind by instances that are no longer running.
    ///
    /// Skips folders whose lock is still held, which belong to a live instance.
    /// </summary>
    public static IReadOnlyList<RecoverableDocument> FindRecoverable()
    {
        var found = new List<RecoverableDocument>();
        try
        {
            if (!Directory.Exists(Root))
            {
                return found;
            }

            foreach (string folder in Directory.GetDirectories(Root))
            {
                if (!IsAbandoned(folder))
                {
                    continue;
                }

                foreach (string sidecar in Directory.GetFiles(folder, "*" + SidecarExtension))
                {
                    string pdf = Path.ChangeExtension(sidecar, ".pdf");
                    if (!File.Exists(pdf))
                    {
                        continue;
                    }

                    SidecarData? data = null;
                    try
                    {
                        data = JsonSerializer.Deserialize<SidecarData>(File.ReadAllText(sidecar));
                    }
                    catch (JsonException ex)
                    {
                        // A damaged sidecar must not hide the PDF beside it: the
                        // file is the user's work, the metadata only describes it.
                        Log.LogWarning(ex, "Unreadable recovery sidecar {Sidecar}.", sidecar);
                    }

                    found.Add(new RecoverableDocument(
                        pdf,
                        data?.SourcePath,
                        data?.DisplayName ?? Path.GetFileNameWithoutExtension(pdf),
                        data?.SavedAtUtc ?? File.GetLastWriteTimeUtc(pdf)));
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Could not scan for recoverable documents.");
        }

        return found;
    }

    /// <summary>Removes every abandoned folder, once the user has decided what to
    /// do with what was in them.</summary>
    public static void ClearAbandoned()
    {
        try
        {
            if (!Directory.Exists(Root))
            {
                return;
            }

            foreach (string folder in Directory.GetDirectories(Root))
            {
                if (!IsAbandoned(folder))
                {
                    continue;
                }

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch (IOException ex)
                {
                    Log.LogWarning(ex, "Could not remove recovery folder {Folder}.", folder);
                }
            }
        }
        catch (Exception ex)
        {
            Log.LogError(ex, "Could not clear abandoned recovery folders.");
        }
    }

    /// <summary>
    /// Whether a folder belongs to a process that is no longer running.
    ///
    /// A folder with no lock file counts as abandoned: it predates this scheme or
    /// its lock was removed, and either way nothing is holding it.
    /// </summary>
    private static bool IsAbandoned(string folder)
    {
        string lockPath = Path.Combine(folder, LockFileName);
        if (!File.Exists(lockPath))
        {
            return true;
        }

        try
        {
            using var _ = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // Still held: another instance is alive and this is its working state.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            Log.LogWarning(ex, "Could not delete {Path}.", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.LogWarning(ex, "Could not delete {Path}.", path);
        }
    }

    /// <summary>
    /// Releases the lock and removes this instance's folder.
    ///
    /// This running is what makes a leftover folder mean "crashed". It is called
    /// from the application's exit path; if the process dies before reaching it,
    /// the folder survives and is exactly what the next start offers back.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _lock?.Dispose();
            if (_folder.Length > 0 && Directory.Exists(_folder))
            {
                Directory.Delete(_folder, recursive: true);
            }
        }
        catch (Exception ex)
        {
            Log.LogWarning(ex, "Could not clean up the recovery folder on exit.");
        }
    }

    private sealed record SidecarData(string? SourcePath, string DisplayName, DateTime SavedAtUtc);
}
