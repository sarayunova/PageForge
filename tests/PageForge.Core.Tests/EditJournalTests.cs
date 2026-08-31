// Copyright (c) 2026 LiVi Software Company
// SPDX-License-Identifier: AGPL-3.0-only
// This file is part of PageForge. See LICENSE for the full license text.

using System.Text;
using PageForge.Core.Editing;
using Xunit;

namespace PageForge.Core.Tests;

/// <summary>
/// FR-EDIT-05 journal tests: the crash-recovery journal must append Do/Undo
/// records with a contiguous sequence, rebuild the timeline on replay, mark
/// reversed edits undone, trim a torn trailing record left by a crash mid-append,
/// reject middle-file corruption outright, and continue sequencing after a
/// replay. Round-trips use a payload codec over a small in-memory recorder kind.
/// </summary>
public sealed class EditJournalTests
{
    private sealed class Recorder
    {
        public int Value { get; set; }
    }

    private sealed class RecordingEditCommand : IEditCommand
    {
        private readonly Recorder _target;

        public RecordingEditCommand(Recorder target, int oldValue, int newValue, string name = "set counter")
        {
            _target = target;
            OldValue = oldValue;
            NewValue = newValue;
            Name = name;
        }

        public string Name { get; }

        public int OldValue { get; }

        public int NewValue { get; }

        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
        {
            _target.Value = NewValue;
            return ValueTask.CompletedTask;
        }

        public ValueTask UndoAsync(CancellationToken cancellationToken = default)
        {
            _target.Value = OldValue;
            return ValueTask.CompletedTask;
        }
    }

    private static string TempPath() => Path.Combine(AppContext.BaseDirectory, $"pf-edit-journal-{Guid.NewGuid():N}.txt");

    private static (Func<IEditCommand, byte[]?> Encode, Func<string, byte[]?, IEditCommand?> Decode) Codec(Recorder recorder)
    {
        byte[]? Encode(IEditCommand command) => command is RecordingEditCommand r
            ? Encoding.UTF8.GetBytes($"{r.OldValue}|{r.NewValue}")
            : null;

        IEditCommand? Decode(string name, byte[]? payload)
        {
            if (payload is null)
            {
                return null;
            }

            string[] parts = Encoding.UTF8.GetString(payload).Split('|');
            return new RecordingEditCommand(recorder, int.Parse(parts[0]), int.Parse(parts[1]), name);
        }

        return (Encode, Decode);
    }

    [Fact]
    public async Task Append_do_records_replay_to_applied_commands()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 0, 1));
                await journal.AppendDoAsync(new RecordingEditCommand(target, 1, 7));
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();

                Assert.Equal(2, result.Records.Count);
                Assert.Equal(2, result.Commands.Count);
                Assert.True(result.FullyRestorable);
                Assert.False(result.TrailingDataTrimmed);
                Assert.Equal(3, result.NextSequence);
                Assert.Equal(new long[] { 1, 2 }, result.Records.Select(r => r.Sequence).ToArray());
                Assert.All(result.Records, r => Assert.False(r.Undone));

                foreach (IEditCommand? command in result.Commands)
                {
                    await command!.ExecuteAsync();
                }

                Assert.Equal(7, target.Value);
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Undo_records_flag_the_referenced_edit_as_reversed()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 0, 1));
                await journal.AppendDoAsync(new RecordingEditCommand(target, 1, 2));
                await journal.AppendUndoAsync(undoesSequence: 1);
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();

                Assert.Equal(2, result.Records.Count);
                Assert.True(result.Records[0].Undone, "the oldest edit was undone on the timeline");
                Assert.False(result.Records[1].Undone);
                Assert.Equal(4, result.NextSequence);
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Inside_one_snapshot_apply_and_undo_replay_applied_only()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 0, 1));
                await journal.AppendUndoAsync(1);
                await journal.AppendDoAsync(new RecordingEditCommand(target, 1, 5));
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();

                var applied = result.Records.Where(r => !r.Undone).ToArray();
                Assert.Single(applied);
                Assert.Equal(3L, applied[0].Sequence);
                Assert.False(applied[0].Undone);
                Assert.True(result.FullyRestorable);

                await result.Commands[1]!.ExecuteAsync();
                Assert.Equal(5, target.Value);
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Torn_trailing_record_is_trimmed_and_sequencing_continues()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 0, 1));
            }

            // Simulate a crash mid-append: partial final record without terminating newline.
            await File.AppendAllTextAsync(path, "D\t2\tset counter\t!!!");

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();

                Assert.True(result.TrailingDataTrimmed);
                Assert.Single(result.Records);
                Assert.Equal(2, result.NextSequence);

                await journal.AppendDoAsync(new RecordingEditCommand(target, 1, 9));
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();
                Assert.Equal(new long[] { 1, 2 }, result.Records.Select(r => r.Sequence).ToArray());
                Assert.Equal("set counter", result.Records[1].Name);
                Assert.False(result.TrailingDataTrimmed);

                await result.Commands[0]!.ExecuteAsync();
                await result.Commands[1]!.ExecuteAsync();
                Assert.Equal(9, target.Value);
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Middle_file_corruption_is_fatal()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 0, 1));
            }

            // Inject a corrupt record strictly between two valid ones.
            await File.AppendAllTextAsync(path, "NOT A RECORD\nD\t2\tset counter\tAQ==");

            using (var journal = new EditJournal(path, encode, decode))
            {
                Assert.Throws<InvalidOperationException>(() => journal.Replay());
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Undo_referencing_an_unknown_sequence_is_fatal()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendUndoAsync(undoesSequence: 7);
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                Assert.Throws<InvalidOperationException>(() => journal.Replay());
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Payloadless_commands_are_recorded_but_not_restorable()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new DelegateEditCommand(
                    "closure edit",
                    () => { },
                    () => { }));
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();

                var record = Assert.Single(result.Records);
                Assert.Equal("closure edit", record.Name);
                Assert.Null(record.Payload);
                Assert.Null(result.Commands[0]);
                Assert.False(result.FullyRestorable);
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public void Fresh_journal_replays_empty_with_next_sequence_one()
    {
        string path = TempPath();
        var (encode, decode) = Codec(new Recorder());

        try
        {
            using var journal = new EditJournal(path, encode, decode);
            EditJournalReplayResult result = journal.Replay();

            Assert.Empty(result.Records);
            Assert.Empty(result.Commands);
            Assert.False(result.TrailingDataTrimmed);
            Assert.Equal(1, result.NextSequence);
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public void Replay_on_a_file_without_the_magic_line_throws()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "this is not a journal\n");

            using var journal = new EditJournal(path, _ => null, (_, _) => null);
            Assert.Throws<InvalidOperationException>(() => journal.Replay());
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Sequence_resumes_after_replay_without_collision()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 0, 1));
                Assert.Equal(1L, journal.HighestSequence);
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                _ = journal.Replay();

                await journal.AppendDoAsync(new RecordingEditCommand(target, 1, 3));
                Assert.Equal(2L, journal.HighestSequence);
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();
                Assert.Equal(new long[] { 1, 2 }, result.Records.Select(r => r.Sequence).ToArray());
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task Jobs_hold_payload_bytes_across_the_boundary()
    {
        string path = TempPath();
        var target = new Recorder();
        var (encode, decode) = Codec(target);

        try
        {
            using (var journal = new EditJournal(path, encode, decode))
            {
                await journal.AppendDoAsync(new RecordingEditCommand(target, 41, 42, "fix answer"));
            }

            using (var journal = new EditJournal(path, encode, decode))
            {
                EditJournalReplayResult result = journal.Replay();
                Assert.Equal("fix answer", result.Records[0].Name);
                var rebuilt = Assert.IsType<RecordingEditCommand>(result.Commands[0]);
                Assert.Equal(41, rebuilt.OldValue);
                Assert.Equal(42, rebuilt.NewValue);
            }
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    private static void DeleteQuietly(string path)
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