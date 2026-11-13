using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

public static partial class ChangeRunnerChecks
{
    public static void PathOnlyLiveReads() => PathOnlyLiveReadsAsync().GetAwaiter().GetResult();

    private static async Task PathOnlyLiveReadsAsync()
    {
        using var fixture = new Fixture(realStore: true);
        ClearObligations(fixture);
        var local = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Local.Seed(local, [1]);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(local, remote)] });
        var notice = new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, null, local, false, false);
        var first = Seed(fixture, notice);
        var begun = fixture.Inner.BeginSelection(first)!;
        Check.Require(fixture.Inner.ReadWork(begun).Single().Before?.LocalId == LocalId,
            "B210-LiveReads setup: the real saved binding has a LocalId");
        fixture.Inner.EndSelection(begun);
        WorkSelection newest = first;
        for (var page = 2; page <= 11; page++) newest = Seed(fixture, notice, page);
        var reads = 0;
        fixture.DropboxView = new HookedSide(fixture.Dropbox, beforeRead: _ => reads++);
        await LiveSyncer(fixture, newest, new Boundary(_ => new Lease()), []).SyncWorkAsync(default);
        Check.Require(reads == 1 && fixture.Inner.CountWork(PairId, Generation, fixture.Inner.ReadWorkWatermark()) == (0, 0),
            "B210-LiveReads: eleven persisted path-only rows cause one metadata attempt and drain; reads=" + reads);
    }
}
