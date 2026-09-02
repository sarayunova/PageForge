// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.Editing;

/// <summary>
/// The undoable redaction apply (FR-SEC-02 apply step). Executing once snapshots
/// the whole open document to a scratch file, then applies every /Redact region
/// already marked on the page (deleting the covered content from the content
/// streams). Undo restores the pre-apply document from that snapshot; redo applies
/// the marks again on the restored document. Redaction is destructive — content is
/// genuinely removed, not painted over — so undo cannot use a stream receipt and
/// instead swaps the whole in-memory document back to the snapshot.
///
/// This command is <see cref="IDisposable"/>, and an <see cref="EditCommandStack"/>
/// disposes it when it is pruned (a newer edit clears the redo branch, or the
/// session is closed) so the scratch snapshot file does not leak.
/// </summary>
public sealed class ApplyRedactionsCommand : IEditCommand, IDisposable
{
    private readonly IPdfEngine _engine;
    private readonly int _pageIndex;
    private readonly RedactionOptions? _options;
    private string? _snapshotPath;
    private bool _hasExecuted;

    public ApplyRedactionsCommand(
        IPdfEngine engine, int pageIndex, RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        _engine = engine;
        _pageIndex = pageIndex;
        _options = options;
    }

    /// <summary>Stable label for the undo menu and journal records.</summary>
    public string Name => "Apply redactions";

    /// <summary>The page whose marked regions were applied (0-based).</summary>
    public int PageIndex => _pageIndex;

    /// <summary>The number of regions applied on the last execute, or 0 if not yet executed.</summary>
    public int AppliedCount { get; private set; }

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasExecuted)
        {
            // First apply: snapshot the pre-redaction document so undo can restore it.
            if (_snapshotPath is null)
            {
                _snapshotPath = Path.Combine(Path.GetTempPath(), $"pageforge-redact-snapshot-{Guid.NewGuid():N}.pdf");
            }

            await _engine.SaveAsAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);
            AppliedCount = await _engine
                .ApplyRedactionsAsync(_pageIndex, _options, cancellationToken).ConfigureAwait(false);
            _hasExecuted = true;
        }
        else
        {
            // Redo: the undo restored the pre-apply document (marks present), so
            // re-applying the same marks reproduces the identical post-apply state.
            AppliedCount = await _engine
                .ApplyRedactionsAsync(_pageIndex, _options, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask UndoAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasExecuted)
        {
            throw new InvalidOperationException($"Cannot undo {Name} before it has been executed.");
        }

        if (_snapshotPath is null || !File.Exists(_snapshotPath))
        {
            throw new InvalidOperationException(
                $"Cannot undo {Name}: the pre-apply snapshot is missing.");
        }

        // Swap the whole in-memory document back to the pre-apply state.
        await _engine.RestoreSnapshotAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_snapshotPath is not null)
        {
            try
            {
                File.Delete(_snapshotPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            _snapshotPath = null;
        }
    }
}
