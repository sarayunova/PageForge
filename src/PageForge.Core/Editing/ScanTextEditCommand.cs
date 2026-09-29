// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using PageForge.Core.Pdf;

namespace PageForge.Core.Editing;

/// <summary>
/// The undoable cover-and-replace edit of scanned text. Executing snapshots the
/// whole open document, removes the OCR text under the old words (so a search no
/// longer finds them), then paints the cover and the new text. The scan image is
/// never touched. Undo restores the snapshot, exactly as
/// <see cref="ApplyRedactionsCommand"/> does, because removing text cannot be
/// undone by a stream receipt.
///
/// Disposable for the same reason as the redaction command: the stack disposes a
/// pruned command so the scratch snapshot does not leak.
/// </summary>
public sealed class ScanTextEditCommand : IEditCommand, IDisposable
{
    // Take the old OCR words out of the text layer, and nothing else: no black bar,
    // and the scan image and any line art are left alone.
    private static readonly RedactionOptions TextOnly = new(
        BlackBox: false,
        ImageMethod: RedactionImageMethod.None,
        LineArtMethod: RedactionLineArtMethod.None,
        TextMethod: RedactionTextMethod.Remove);

    private readonly IPdfEngine _engine;
    private readonly int _pageIndex;
    private readonly ScanTextReplacement _replacement;
    private string? _snapshotPath;
    private bool _hasExecuted;

    public ScanTextEditCommand(IPdfEngine engine, int pageIndex, ScanTextReplacement replacement)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(replacement);
        if (pageIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        }

        _engine = engine;
        _pageIndex = pageIndex;
        _replacement = replacement;
    }

    public string Name => "Edit scanned text";

    public int PageIndex => _pageIndex;

    /// <summary>The rectangle actually covered (it grows when the new text is wider).</summary>
    public PdfRect? CoveredBox { get; private set; }

    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (!_hasExecuted)
        {
            _snapshotPath ??= Path.Combine(Path.GetTempPath(), $"pageforge-scantext-snapshot-{Guid.NewGuid():N}.pdf");
            await _engine.SaveAsAsync(_snapshotPath, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await _engine.AddRedactionAsync(_pageIndex, _replacement.ClearRegion, cancellationToken).ConfigureAwait(false);
            await _engine.ApplyRedactionsAsync(_pageIndex, TextOnly, cancellationToken).ConfigureAwait(false);
            CoveredBox = await _engine
                .CoverAndReplaceTextAsync(_pageIndex, _replacement, cancellationToken).ConfigureAwait(false);
            _hasExecuted = true;
        }
        catch
        {
            // Half an edit (text removed, nothing drawn) would leave a hole in the
            // page. Put the document back before reporting the failure.
            if (_snapshotPath is not null && File.Exists(_snapshotPath))
            {
                await _engine.RestoreSnapshotAsync(_snapshotPath, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
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
            throw new InvalidOperationException($"Cannot undo {Name}: the pre-edit snapshot is missing.");
        }

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
