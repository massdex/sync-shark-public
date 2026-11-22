using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks notice resolution against the reviewed subject.</summary>
public static class ResolveChecks
{
    /// <summary>Verifies the Resolve contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var settings = Scenarios.ItemChecks.Settings;
        var resolve = new Resolve();
        static Notice Make(NoticeKind kind, TextKey cause, string path, string id) =>
            new(0, kind, cause, path, new IssueSubject([new NoticeItem(Side.Local, path, id)], Side.Local),
                DateTimeOffset.UnixEpoch, false);
        static TestDoubles.FakeSide Side0(Side side) => new(side);

        using var store = new TestDoubles.InMemoryStateStore();
        await store.SaveNoticeAsync(pair.PairId, Make(NoticeKind.Info, TextKey.InfoKeptBoth, "item", "id"), default);
        var stored = store.OpenNotices.Single();
        var local = Side0(Side.Local); var remote = Side0(Side.Dropbox);
        var stale = false;
        try
        {
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored with { Path = "other" },
                ResolutionChoice.Dismiss, null, local, remote, store, null!, null!, default);
        }
        catch (EngineRequestException e) when (e.Key == TextKey.ResolveStale) { stale = true; }
        Check.Require(stale && store.OpenNotices.Single() == stored, "A stale resolution reported success");
        foreach (var choice in new[]{ResolutionChoice.Ignore, ResolutionChoice.Unignore, ResolutionChoice.Retry})
        {
            // These choices are handled before executor dispatch: even an identical store write is wrong.
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored with { Ignored = choice == ResolutionChoice.Unignore },
                choice, null, local, remote, null!, null!, null!, default);
        }
    }

    /// <summary>Only the choices permitted for the reviewed kind may close it.</summary>
    public static async Task DismissAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        foreach (var kind in new[]{NoticeKind.Failure, NoticeKind.RootIdentity, NoticeKind.DriveChange,
            NoticeKind.PutBack, NoticeKind.Info, NoticeKind.Skipped, NoticeKind.Tie, NoticeKind.NameClash})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var notice = new Notice(0, kind, TextKey.FailChanged, "item", new([], null), DateTimeOffset.UnixEpoch, false);
            await store.SaveNoticeAsync(pair.PairId, notice, default);
            var allowed = kind is NoticeKind.Info or NoticeKind.Skipped or NoticeKind.Tie or NoticeKind.NameClash;
            var rejected = false;
            try
            {
                var outcome = await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, null!, store.OpenNotices.Single(),
                    ResolutionChoice.Dismiss, null, null!, null!, store, null!, null!, default);
                Check.Require(allowed && outcome == EffectOutcome.Applied, $"Dismiss returned {outcome} for {kind}");
            }
            catch (EngineRequestException e) when (!allowed && e.Key == TextKey.ResolveBadChoice) { rejected = true; }
            Check.Require(rejected == !allowed && (store.OpenNotices.Count == 0) == allowed, $"Wrong Dismiss outcome for {kind}");
        }
    }

    /// <summary>One- and two-label choices use only real links; a resumed folder replacement must not select another occupant.</summary>
    public static async Task ReviewedEndpointsAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var local = new TestDoubles.FakeSide(Side.Local);
            var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var original = new IssueSubject([new(Side.Local, "item", "pc"), new(Side.Dropbox, "item", "db")], null);
            await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.Tie, TextKey.TieEqualTimes, "item", original,
                DateTimeOffset.UnixEpoch, false), default);
            var reviewed = store.OpenNotices.Single();
            remote.ReadFailures["item"] = new ItemFailedException("item", TextKey.ReadDropboxFailed, null);
            for (var i = 0; i < 2; i++)
            {
                var outcome = await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, scope,
                    reviewed, ResolutionChoice.UseLocal, null, local, remote, store, null!, null!, default);
                var failure = store.OpenNotices.Single(n => n.Kind == NoticeKind.Failure);
                Check.Require(outcome == EffectOutcome.Refused && failure.Cause == TextKey.ReadDropboxFailed &&
                    failure.Subject.Incoming == Side.Dropbox && failure.Subject.HasSameItems(original) &&
                    store.OpenNotices.Contains(reviewed), "Read failure changed the reviewed choice or lost its subject");
            }
        }
        foreach (var twoLabels in new[]{false, true})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local);
            var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var pc = Scenarios.ItemChecks.File(Side.Local, "p", 1, "pc");
            var db = Scenarios.ItemChecks.File(Side.Dropbox, "p", 2, "db");
            var dbLabel = Scenarios.ItemChecks.File(Side.Dropbox, "pc-label", 1, "db-label");
            local.Seed(pc, [1]); remote.Seed(db, [2]); remote.Seed(dbLabel, [1]);
            var items = new List<NoticeItem> { new(Side.Local, pc.Path, pc.Id), new(Side.Dropbox, db.Path, db.Id),
                new(Side.Dropbox, dbLabel.Path, dbLabel.Id) };
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(pc, dbLabel), null, null, default);
            if (twoLabels)
            {
                var pcLabel = Scenarios.ItemChecks.File(Side.Local, "db-label", 2, "pc-label");
                local.Seed(pcLabel, [2]); items.Add(new(Side.Local, pcLabel.Path, pcLabel.Id));
                await store.CommitAsync(pair.PairId, null, SyncedEntries.From(pcLabel, db), null, null, default);
            }
            await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.Info, TextKey.InfoKeptBoth, "p",
                new(items, null), DateTimeOffset.UnixEpoch, false), default);
            await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, scope, store.OpenNotices.Single(),
                ResolutionChoice.UseLocal, null, local, remote, store, null!, null!, default);
            Check.Require(local.Items.Count == 1 && remote.Items.Count == 1 &&
                local.Items["p"].Bytes.SequenceEqual(new byte[]{1}) && remote.Items["p"].Bytes.SequenceEqual(new byte[]{1}) &&
                store.OpenNotices.Count == 0, "Resolution did not keep only the selected originals");
        }
        foreach (var replacement in new[]{false, true})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local);
            var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var incoming = Scenarios.ItemChecks.File(Side.Local, "after", 0, "folder", true);
            var peer = Scenarios.ItemChecks.File(Side.Dropbox, "before", 0, "peer", true);
            local.Seed(incoming, []); remote.Seed(peer, []);
            await store.CommitAsync(pair.PairId, null, SyncedEntries.From(incoming with { Path = "before" }, peer),
                null, null, default);
            if (replacement) remote.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "after", 3, "replacement"), [3]);
            await store.SaveNoticeAsync(pair.PairId, new(0, NoticeKind.NameClash, TextKey.ClashNameTaken, "after",
                new([new(Side.Local, "after", "folder"), new(Side.Dropbox, "after", "removed"),
                    new(Side.Dropbox, "before", "peer")], Side.Local), DateTimeOffset.UnixEpoch, false), default);
            try
            {
                await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, scope,
                    store.OpenNotices.Single(), ResolutionChoice.ReplaceExisting, null, local, remote, store, null!, null!, default);
            }
            catch (EngineRequestException e) when (replacement && e.Key == TextKey.ResolveSubjectStale) { }
            Check.Require(store.OpenNotices.Count == (replacement ? 1 : 0) && remote.Mutations.Count == 0 &&
                (!replacement || remote.Items["after"].Entry.Id == "replacement"), "Resume acted on a new occupant or failed to close");
        }
    }
}
