using System.Globalization;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    // B-137: AutoSync reports its queue size. One snapshot line per pass, taken outside the progress lock before any
    // row runs, names both sides' pending rows; it is printed once even when the pass then completes several rows.
    // The ordinary rows stage keeps the opening, progress and ending it had before and still carries no total.
    private static async Task PassTotalAsync()
    {
        foreach (var realStore in new[] { false, true })
        {
            var name = realStore ? " real store" : " in-memory store";
            using var fixture = new Fixture(realStore);
            ClearObligations(fixture);
            SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
            var paths = new[] { Item, Item + "-2", Item + "-3" };
            WorkSelection? selected = null;
            for (var index = 0; index < paths.Length; index++)
            {
                var local = LocalEntry(paths[index], LocalId + "-" + index);
                var remote = RemoteEntry(paths[index], DropboxId + "-" + index);
                fixture.Local.Seed(local, [1]);
                fixture.Dropbox.Seed(remote, [1]);
                fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
                var work = SeedRemote(fixture, remote,
                    new ChangeNotice(Side.Dropbox, ChangeKind.Changed, paths[index], null, remote.Id, remote, false, false));
                selected ??= work;
            }
            var expected = fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark());
            Check.Require(expected == (0, paths.Length),
                "B-137 every pending row is in the queue before the pass" + name + ": " + expected);
            var events = new List<RunEvent>();
            await LiveSyncer(fixture, selected!, new Boundary(_ => new Lease()), events).SyncWorkAsync(default);
            var line = Text.Get(TextKey.AutoSyncQueueAtStart,
                (TextArg.Count, paths.Length.ToString(CultureInfo.InvariantCulture)),
                (TextArg.Local, "0"), (TextArg.Dropbox, paths.Length.ToString(CultureInfo.InvariantCulture)));
            var snapshots = events.Where(item => item.What == nameof(TextKey.AutoSyncQueueAtStart)).ToList();
            Check.Require(snapshots.Count == 1 && snapshots[0].Detail == line &&
                    snapshots[0].Outcome == nameof(RunEvent.InProgress),
                "B-137 the pass prints one queue snapshot line naming both sides" + name + ": " +
                string.Join("|", snapshots.Select(item => item.Detail)));
            Check.Require(fixture.Hooks.Count(nameof(IStateStore.CountWork)) == 1,
                "B-137 the queue is counted once per pass" + name);
            var rows = events.Where(item => item.StageKey == TextKey.StageRows).ToList();
            Check.Require(rows.Count(item => !item.Ended && !item.InProgress) == 1 &&
                    rows.Count(item => item.Ended) == 1 && rows[^1].Ended && rows[^1].Count >= 1 &&
                    rows.All(item => item.Total is null),
                "B-137 the rows stage keeps one opening, its progress and its completion count, with no total" + name);
            Check.Require(fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
                "B-137 completed rows leave the queue" + name);
            if (realStore) QueueLogRoundTrip(name, snapshots[0], line);
        }
    }

    // F3: the snapshot must be a record the canonical reader accepts with or without a live subscriber, so it is
    // written and read back through the same RunLog and HumanLog the CLI uses, not just inspected in memory.
    private static void QueueLogRoundTrip(string name, RunEvent snapshot, string line)
    {
        var folder = Directory.CreateTempSubdirectory(nameof(QueueLogRoundTrip));
        try
        {
            var live = new List<string>();
            var log = RunLog.Open(folder.FullName, "queue", "queue", live.Add, LogBufferCharacters);
            var path = log.Path;
            log.Event(snapshot);
            log.Dispose();
            Check.Require(live.Count(message => message.Contains(line, StringComparison.Ordinal)) == 1,
                "B-137 the live subscriber receives the queue snapshot sentence exactly once" + name + ": " +
                string.Join("|", live));
            var saved = File.ReadAllLines(path);
            Check.Require(saved.Length == 1 && RunLog.TryDecodeRecord(saved[0], out var record) &&
                    record.What == nameof(TextKey.AutoSyncQueueAtStart) &&
                    record.Outcome == nameof(RunEvent.InProgress) && record.Detail == line,
                "B-137 the saved queue snapshot is a canonical record" + name);
            var block = new HumanLog(PairId, "queue", path, TextKey.HumanTitleRun, false, RunLog.HumanWidth)
                .AcceptFileLine(1, saved[0]);
            Check.Require(block is not null && block.Contains(line, StringComparison.Ordinal) &&
                    !block.Contains("Malformed", StringComparison.OrdinalIgnoreCase),
                "B-137 the saved queue snapshot reads back as its one sentence" + name);
        }
        finally { folder.Delete(recursive: true); }
    }
}
