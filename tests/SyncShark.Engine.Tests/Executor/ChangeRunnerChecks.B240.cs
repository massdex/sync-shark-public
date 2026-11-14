using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void TemporaryRetryTime() => TemporaryRetryTimeAsync().GetAwaiter().GetResult();

    private static async Task TemporaryRetryTimeAsync()
    {
        foreach (var real in new[] { false, true })
        {
            await RetryTimeWithinRunAsync(real);
            await DependentInheritsOwnWaitAsync(real);
            await HeldKeyNotExtendedAsync(real);
            await WaitTravelsWithTurnAsync(real);
            await PerProbeEligibilityAsync(real);
            await SkippedAdmissionStaysTemporaryAsync(real);
        }
        RecordRules();
    }

    private static WorkSelection SeedRetryRename(Fixture fixture, Action moved)
    {
        var old = LocalEntry(OldPath, LocalId);
        var remote = RemoteEntry(OldPath, DropboxId);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(old, remote)] });
        SourceFixture.AcquireJournal(fixture.Inner, fixture.Pair);
        fixture.Local.Seed(old, [1]);
        var renamed = remote with { Path = Item };
        fixture.Dropbox.Seed(renamed, [1]);
        var selected = SeedRemote(fixture, renamed,
            new ChangeNotice(Side.Dropbox, ChangeKind.Renamed, Item, OldPath, DropboxId, null, false, false));
        SeedRetryFile(fixture, "Other.txt");
        if (fixture.DatabasePath is not null) RepairDone(fixture);
        ClearObligations(fixture);
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            MoveFault = path =>
            {
                moved();
                return new ItemFailedException(path, TextKey.ReadLocalFailed, null)
                    { Disposition = FailureDisposition.TemporaryNoEffect };
            }
        };
        return selected;
    }

    private static WorkSelection SeedRetryFile(Fixture fixture, string path)
    {
        var entry = RemoteEntry(path, "remote-" + path);
        fixture.Dropbox.Seed(entry, [1]);
        return SeedRemote(fixture, entry);
    }

    private static async Task RetryTimeWithinRunAsync(bool real)
    {
        using var fixture = new Fixture(realStore: real);
        var moves = 0;
        var selected = SeedRetryRename(fixture, () => moves++);
        var pending = new HashSet<long> { 1, 2 };
        var attempts = new TemporaryAttempts { WaitFor = (n, _) => n, Pending = pending.Contains };
        var ends = new List<int>();
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.EndWorkPass)) return;
            ends.Add(moves);
            if (ends.Count <= 3) SeedRetryFile(fixture, "churn-" + ends.Count + ".txt");
            if (ends.Count == 2) pending.Remove(1);
        };
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], attempts: attempts).SyncWorkAsync(default);
        Check.Require(ends.SequenceEqual([1, 1, 2, 2, 2]) && attempts.Reported == 2 &&
            fixture.Inner.Issues(PairId).Count == 0 && PendingAt(fixture, Side.Dropbox, Item).Any(),
            "a due temporary rename runs again in the same opportunity, then its next wait holds; real=" + real);
        foreach (var path in new[] { "Other.txt", "churn-1.txt", "churn-2.txt", "churn-3.txt" })
            Check.Require(await fixture.Local.ReadOneAsync(path, default) is not null, "unrelated work retires: " + path);
    }

    private static async Task DependentInheritsOwnWaitAsync(bool real)
    {
        foreach (var ancestorFirst in new[] { false, true })
        {
            using var fixture = new Fixture(realStore: real);
            ClearObligations(fixture);
            fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
            var child = LocalEntry(Plain, LocalId);
            fixture.Local.Seed(child, [1]);
            fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
            WorkSelection selected;
            if (ancestorFirst)
            {
                selected = SeedRemote(fixture, FolderEntry(Folder, DropboxId));
                Selected(fixture, Plain, child);
            }
            else
            {
                selected = Selected(fixture, Plain, child);
                SeedRemote(fixture, FolderEntry(Folder, DropboxId));
            }
            SeedRetryFile(fixture, "Other.txt");
            fixture.Dropbox.FailNext(Folder, new ItemFailedException(Folder, TextKey.DropboxRefused, null)
                { Disposition = FailureDisposition.TemporaryNoEffect });
            var pending = new HashSet<long> { 1 };
            var attempts = new TemporaryAttempts { WaitFor = (_, _) => 1, Pending = pending.Contains };
            var ends = 0;
            var firstSynced = 0;
            fixture.Hooks.BeforeCall = method =>
            {
                if (method.Name != nameof(IStateStore.EndWorkPass)) return;
                ends++;
                if (firstSynced == 0 && fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Plain)) is not null)
                    firstSynced = ends;
                if (ends == 1) SeedRetryFile(fixture, "churn-1.txt");
                if (ends == 2) pending.Clear();
            };
            var events = new List<RunEvent>();
            await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), events, attempts: attempts).SyncWorkAsync(default);
            Check.Require(ends == 4 && firstSynced == 3 && await SyncedAsync(fixture, Plain) &&
                fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key(Folder)) is not null &&
                attempts.Reported == 1 && fixture.Inner.Issues(PairId).Count == 0 &&
                events.All(item => item.Error is not LiveAdmissionStaleException),
                "the dependent inherits the folder's finite wait in either traversal order; real=" + real +
                "; ancestorFirst=" + ancestorFirst + "; passes=" + ends + "; firstSynced=" + firstSynced);
        }
    }

    private static async Task HeldKeyNotExtendedAsync(bool real)
    {
        using var fixture = new Fixture(realStore: real);
        var moves = 0;
        var selected = SeedRetryRename(fixture, () => moves++);
        var attempts = new TemporaryAttempts();
        attempts.Held.Add((Side.Dropbox, selected.Key));
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.EndWorkPass)) attempts.Held.Clear();
        };
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], attempts: attempts).SyncWorkAsync(default);
        Check.Require(moves == 1 && attempts.Reported == 1 && fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 2,
            "a host-only held key is not re-recorded for the run; real=" + real);
    }

    private static async Task WaitTravelsWithTurnAsync(bool real)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var fixture = new Fixture(realStore: real);
        var selected = SeedRetryFile(fixture, "A.txt");
        SeedRetryFile(fixture, "B.txt");
        SeedRetryFile(fixture, "Other.txt");
        ClearObligations(fixture);
        foreach (var path in new[] { "A.txt", "B.txt" })
            fixture.Dropbox.FailNext(path, new ItemFailedException(path, TextKey.DropboxRefused, null)
                { Disposition = FailureDisposition.TemporaryNoEffect });
        var aReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keyA = PathNames.DropboxKey("A.txt");
        var keyB = PathNames.DropboxKey("B.txt");
        var attempts = new TemporaryAttempts
        {
            WaitFor = (_, path) =>
            {
                (path == "A.txt" ? aReported : bReported).TrySetResult();
                return path == "A.txt" ? 1 : 2;
            },
            Pending = wait => wait == 2,
            Executing = (opportunity, after) => opportunity is WorkSelection work
                ? !after && work.Key == keyB ? aReported.Task.WaitAsync(guard.Token)
                    : after && work.Key == keyA ? bReported.Task.WaitAsync(guard.Token) : Task.CompletedTask
                : Task.CompletedTask
        };
        var boundary = new Boundary(_ => new Lease())
            { Capture = (selection, _, _) => Task.FromResult(Coverage(selection)) };
        var context = fixture.Context(selected, new FakeAdmission { Level = 2 }) with { ItemAttempts = attempts };
        await new Syncer(context, new SyncPorts(new RecoveryBoundary(), boundary,
            RefusingExecutionBoundary.Instance, 1)).SyncWorkAsync(guard.Token).WaitAsync(guard.Token);
        Check.Require(attempts.Order.IndexOf("report:A.txt") < attempts.Order.IndexOf("report:B.txt") &&
            attempts.Order.IndexOf("report:B.txt") < attempts.Order.IndexOf("executed:" + keyA) &&
            attempts.Reported == 2 && fixture.Inner.Issues(PairId).Count == 0 &&
            fixture.Hooks.Count(nameof(IStateStore.EndWorkPass)) == 3 &&
            fixture.Hooks.BegunSelections.Count(work => work.Key == keyA) == 2 &&
            fixture.Hooks.BegunSelections.Count(work => work.Key == keyB) == 1 &&
            await fixture.Local.ReadOneAsync("A.txt", default) is not null &&
            await fixture.Local.ReadOneAsync("B.txt", default) is null && PendingAt(fixture, Side.Dropbox, "B.txt").Any(),
            "each concurrent temporary result carries its own wait, not the latest report's; real=" + real);
    }

    private static async Task PerProbeEligibilityAsync(bool real)
    {
        using var fixture = new Fixture(realStore: real);
        var selected = SeedRetryFile(fixture, "X.txt");
        SeedRetryFile(fixture, "A.txt");
        SeedRetryFile(fixture, "Other.txt");
        ClearObligations(fixture);
        fixture.Dropbox.FailNext("A.txt", new ItemFailedException("A.txt", TextKey.DropboxRefused, null)
            { Disposition = FailureDisposition.TemporaryNoEffect });
        var pending = new HashSet<long> { 1 };
        var attempts = new TemporaryAttempts { WaitFor = (_, _) => 1, Pending = pending.Contains };
        attempts.Held.Add((Side.Dropbox, selected.Key));
        fixture.LocalView = new ProxySide(fixture.Local)
        {
            BeforeStage = (path, _) =>
            {
                if (path == "X.txt") pending.Clear();
                return Task.CompletedTask;
            }
        };
        var ends = 0;
        var syncedInSecond = false;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name != nameof(IStateStore.EndWorkPass)) return;
            if (++ends == 1) attempts.Held.Clear();
            if (ends == 2) syncedInSecond = fixture.Inner.SyncedByKey(PairId, Side.Local, PathNames.Key("A.txt")) is not null;
        };
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), [], attempts: attempts).SyncWorkAsync(default);
        Check.Require(syncedInSecond && ends == 3, "eligibility is fresh at each probe within the pass; real=" + real);
    }

    private static async Task SkippedAdmissionStaysTemporaryAsync(bool real)
    {
        using var fixture = new Fixture(realStore: real);
        ClearObligations(fixture);
        fixture.Local.Seed(FolderEntry(Folder, FolderId), null);
        var child = LocalEntry(Plain, LocalId);
        fixture.Local.Seed(child, [1]);
        fixture.Dropbox.Seed(FolderEntry(Folder, DropboxId), null);
        var selected = Selected(fixture, Plain, child);
        var ancestor = SeedRemote(fixture, FolderEntry(Folder, DropboxId));
        var attempts = new TemporaryAttempts();
        var held = false;
        fixture.Hooks.BeforeCall = method =>
        {
            if (method.Name == nameof(IStateStore.BeginSelection) && !held)
            {
                held = true;
                attempts.Held.Add((ancestor.Side, ancestor.Key));
            }
            if (method.Name == nameof(IStateStore.EndSelection)) attempts.Held.Clear();
        };
        var events = new List<RunEvent>();
        await LiveSyncer(fixture, selected, new Boundary(_ => new Lease()), events, attempts: attempts).SyncWorkAsync(default);
        Check.Require(held && fixture.Hooks.BegunSelections.Any(work => work.Key == ancestor.Key) &&
            events.All(item => item.Error is not LiveAdmissionStaleException) && fixture.Inner.Issues(PairId).Count == 0 &&
            PendingAt(fixture, Side.Local, Plain).Any(),
            "a wait ending after admission's skip retains its dependent without a false refusal; real=" + real);
    }

    private static void RecordRules()
    {
        using var fixture = new Fixture();
        var selected = SeedRetryFile(fixture, "A.txt");
        var old = selected with { Obligation = "old" };
        var newer = selected with { Obligation = "newer" };
        var pending = new HashSet<long> { 1 };
        var attempts = new TemporaryAttempts { Pending = pending.Contains };
        var records = new TemporaryRecords(fixture.Context(selected) with { ItemAttempts = attempts });
        records.Record(old, 1);
        attempts.Held.Add((old.Side, old.Key));
        Check.Require(records.Skips(old, out var until) && until == 1 && records.Skips(newer, out until) && until == 1,
            "the exact record and another responsibility inherit the key's holding wait");
        pending.Clear();
        Check.Require(records.Skips(old, out until) && until == long.MaxValue &&
            records.Skips(newer, out until) && until == long.MaxValue, "an unattributed host hold lasts for the run");
        attempts.Held.Clear();
        Check.Require(!records.Skips(old, out _) && !records.Skips(newer, out _), "due records no longer hold");
        foreach (var finiteFirst in new[] { false, true })
        {
            var work = selected with { Key = finiteFirst ? "finite-first" : "finite-last" };
            records.Record(work, finiteFirst ? 7 : null);
            records.Record(work, finiteFirst ? null : 7);
            Check.Require(records.Skips(work, out until) && until == long.MaxValue, "run-long containment is never shortened");
        }
        var latest = selected with { Key = "latest" };
        pending.UnionWith([1, 2]);
        records.Record(latest, 1);
        records.Record(latest, 2);
        Check.Require(records.Skips(latest, out until) && until == 2, "the larger wait identity is retained");
    }
}
