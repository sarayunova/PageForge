// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;

namespace PageForge.Core.Editing;

/// <summary>
/// One committed edit in the journal's timeline, in the order it happened.
/// An undo marker does not become its own entry — it flips
/// <see cref="Undone"/> on the <see cref="EditJournalRecord"/> it references, so
/// consumers replay only the records that are currently applied.
/// </summary>
public sealed record EditJournalRecord(long Sequence, string Name, bool Undone, byte[]? Payload);

/// <summary>
/// Result of <see cref="EditJournal.Replay"/>. <see cref="Commands"/> mirrors
/// <see cref="Records"/> entry-for-entry: each index is the caller-supplied
/// factory's reconstruction of that record's edit, or null when the edit is not
/// restorable (empty payload, or the factory returned null).
/// </summary>
public sealed record EditJournalReplayResult(
    IReadOnlyList<EditJournalRecord> Records,
    IReadOnlyList<IEditCommand?> Commands,
    long NextSequence,
    bool TrailingDataTrimmed)
{
    /// <summary>Whether every currently-applied record could be reconstructed for replay.</summary>
    public bool FullyRestorable =>
        Records.Select((record, i) => !record.Undone && Commands[i] is null).All(x => !x);
}

/// <summary>
/// Append-only crash-recovery journal of an editing session (FR-EDIT-05, TSD
/// §3.1). Every committed edit is appended as a Do record; every undo appends an
/// undo marker referencing the record it reversed. On the next launch,
/// <see cref="Replay"/> rebuilds the session's timeline and the caller can
/// re-apply the non-undone edits to reconstruct the document's post-crash state.
///
/// File format (UTF-8, one record per line, Tab-separated fields):
///   PF-EDJ 1                                  magic + version (first line)
///   D<TAB><seq><TAB><name><TAB><payloadB64>    edit applied; seq is monotonic
///   U<TAB><seq><TAB><undoesSeq>                an edit was undone
///
/// Crash safety: the journal tolerates a torn <em>trailing</em> record (the
/// process died mid-append) — <see cref="Replay"/> truncates it and reports
/// <see cref="EditJournalReplayResult.TrailingDataTrimmed"/>. A malformed or
/// non-contiguous record strictly in the middle of the file is fatal (throws),
/// because that implies real corruption, not a crash.
///
/// Payloads are opaque bytes provided by an encode/decode delegate pair so the
/// journal stays command-agnostic; a command that cannot be serialized (e.g. a
/// closure-only <see cref="DelegateEditCommand"/>) writes an empty payload and
/// is journal-recorded but cannot be reconstructed on replay.
///
/// Threading: like other Core editing types, owned by the document worker and
/// not thread-safe.
/// </summary>
public sealed class EditJournal : IDisposable
{
    private const string Magic = "PF-EDJ 1";

    private readonly FileStream _stream;
    private readonly Func<IEditCommand, byte[]?> _encode;
    private readonly Func<string, byte[]?, IEditCommand?> _decode;
    private long _sequence;
    private bool _disposed;

    /// <summary>
    /// Opens (or creates) the journal at <paramref name="path"/>.
    /// <paramref name="encode"/> writes a command's payload bytes (return null
    /// to record only its name); <paramref name="decode"/> restores a command
    /// from a name + payload during <see cref="Replay"/> (return null when not
    /// restorable).
    /// </summary>
    public EditJournal(
        string path,
        Func<IEditCommand, byte[]?> encode,
        Func<string, byte[]?, IEditCommand?> decode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _encode = encode ?? throw new ArgumentNullException(nameof(encode));
        _decode = decode ?? throw new ArgumentNullException(nameof(decode));

        _stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        if (_stream.Length == 0)
        {
            byte[] header = Encoding.UTF8.GetBytes(Magic + "\n");
            _stream.Write(header, 0, header.Length);
            _stream.Flush();
        }
    }

    /// <summary>The highest sequence number written so far.</summary>
    public long HighestSequence => _sequence;

    /// <summary>
    /// Appends a Do record for <paramref name="command"/> (committed edit). The
    /// sequence counter advances so replay sees a contiguous timeline.
    /// </summary>
    public async ValueTask AppendDoAsync(IEditCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureOpen();

        byte[]? payload = _encode(command);
        string name = Sanitize(command.Name);
        string payloadB64 = payload is null or { Length: 0 } ? string.Empty : Convert.ToBase64String(payload);
        long seq = ++_sequence;
        await WriteLineAsync($"D\t{seq}\t{name}\t{payloadB64}", cancellationToken);
    }

    /// <summary>
    /// Appends an undo marker reversing the edit recorded with the given
    /// <paramref name="undoesSequence"/>. Typically called after the stack has
    /// undone the referenced command.
    /// </summary>
    public async ValueTask AppendUndoAsync(long undoesSequence, CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        long seq = ++_sequence;
        await WriteLineAsync($"U\t{seq}\t{undoesSequence}", cancellationToken);
    }

    /// <summary>
    /// Rebuilds the session timeline from the journal file, truncating any torn
    /// trailing record (a crash mid-append). Returns the ordered records with
    /// their undone/applied state, the reconstructed commands (via the decode
    /// delegate), and whether trailing data was trimmed. Throws on middle-file
    /// corruption (see class doc). The journal remains open for further appends.
    /// </summary>
    public EditJournalReplayResult Replay()
    {
        EnsureOpen();
        _stream.Flush();
        _stream.Position = 0;

        byte[] data = new byte[_stream.Length];
        int length = _stream.Read(data, 0, (int)_stream.Length);

        {
            var raw = new List<RawEntry>();
            long expected = 0;
            long lastCommittedEnd = 0;
            bool trimmed = false;
            bool sawMagic = false;

            int lineStart = 0;
            int i = 0;
            while (i <= length)
            {
                if (i < length && data[i] != (byte)'\n')
                {
                    i++;
                    continue;
                }

                // Line content = [lineStart, i), the trailing \r stripped.
                int contentEnd = i;
                if (contentEnd > lineStart && data[contentEnd - 1] == (byte)'\r')
                {
                    contentEnd--;
                }

                bool hasNewline = i < length;
                int next = i + 1;
                i = next;

                string line = Encoding.UTF8.GetString(data, lineStart, contentEnd - lineStart);
                lineStart = next;

                if (!sawMagic)
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    if (!line.Equals(Magic, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"EditJournal: missing or wrong magic line; expected '{Magic}'.");
                    }

                    sawMagic = true;
                    lastCommittedEnd = hasNewline ? next : length;
                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                bool isFinalLine = next >= length;
                RawEntry? entry = TryParseLine(line, expected);
                if (entry is not null)
                {
                    expected = entry.Sequence;
                    raw.Add(entry);
                    lastCommittedEnd = hasNewline ? next : length;
                    continue;
                }

                if (isFinalLine)
                {
                    trimmed = true;
                    break;
                }

                throw new InvalidOperationException(
                    "EditJournal: corrupt record in the middle of the file; a malformed record there " +
                    "means real corruption, not a crash.");
            }

            if (trimmed)
            {
                _stream.SetLength(Math.Min(lastCommittedEnd, length));
                _stream.Position = _stream.Length;
            }

            EditJournalReplayResult result = Reconcile(raw, trimmed);
            _sequence = result.NextSequence - 1;
            return result;
        }
    }

    private static RawEntry? TryParseLine(string line, long expectedSequence)
    {
        string[] fields = line.Split('\t');
        if (fields.Length < 2 || (fields[0] != "D" && fields[0] != "U"))
        {
            return null;
        }

        if (!long.TryParse(fields[1], out long seq) || seq != expectedSequence + 1)
        {
            return null;
        }

        if (fields[0] == "D")
        {
            if (fields.Length < 4)
            {
                return null;
            }

            byte[]? payload = null;
            if (fields[3].Length > 0)
            {
                try
                {
                    payload = Convert.FromBase64String(fields[3]);
                }
                catch (FormatException)
                {
                    return null;
                }
            }

            return new RawEntry(seq, IsUndo: false, Sanitize(fields[2]), payload, UndoesSequence: 0);
        }

        return fields.Length >= 3 && long.TryParse(fields[2], out long undoes)
            ? new RawEntry(seq, IsUndo: true, string.Empty, null, undoes)
            : null;
    }

    private EditJournalReplayResult Reconcile(List<RawEntry> raw, bool trailingDataTrimmed)
    {
        var undone = new HashSet<long>();
        var committed = new HashSet<long>();
        var timeline = new List<EditJournalRecord>(raw.Count);

        foreach (RawEntry entry in raw)
        {
            if (entry.IsUndo)
            {
                if (!committed.Contains(entry.UndoesSequence))
                {
                    throw new InvalidOperationException(
                        $"EditJournal: undo record references sequence {entry.UndoesSequence}, which was never " +
                        "committed as an edit.");
                }

                undone.Add(entry.UndoesSequence);
                continue;
            }

            committed.Add(entry.Sequence);
            timeline.Add(new EditJournalRecord(entry.Sequence, entry.Name, Undone: false, entry.Payload));
        }

        for (int i = 0; i < timeline.Count; i++)
        {
            if (undone.Contains(timeline[i].Sequence))
            {
                timeline[i] = new EditJournalRecord(
                    timeline[i].Sequence, timeline[i].Name, Undone: true, timeline[i].Payload);
            }
        }

        timeline.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

        var commands = new IEditCommand?[timeline.Count];
        for (int i = 0; i < timeline.Count; i++)
        {
            commands[i] = _decode(timeline[i].Name, timeline[i].Payload);
        }

        long next = raw.Count == 0 ? 0 : raw[^1].Sequence;
        return new EditJournalReplayResult(timeline, commands, next + 1, trailingDataTrimmed);
    }

    private async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _stream.Dispose();
        }

        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private static string Sanitize(string name)
    {
        StringBuilder sb = new(name.Length);
        foreach (char ch in name ?? string.Empty)
        {
            sb.Append(ch is '\t' or '\r' or '\n' ? ' ' : ch);
        }

        return sb.ToString();
    }

    private sealed record RawEntry(
        long Sequence, bool IsUndo, string Name, byte[]? Payload, long UndoesSequence);
}