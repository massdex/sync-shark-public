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

    /// <summary>A click uses current content and preserves the displaced version without reader coverage.</summary>
    public static async Task RemovalProofAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        foreach (var choice in new[]{ResolutionChoice.UseLocal, ResolutionChoice.UseDropbox})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local); var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var pc = Scenarios.ItemChecks.File(Side.Local, "item", 1, "pc");
            var db = Scenarios.ItemChecks.File(Side.Dropbox, "item", 2, "db");
            local.Seed(pc, [1]); remote.Seed(db, [2]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(pc, db),null,null,default);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Tie,TextKey.TieEqualTimes,"item",
                new([new(Side.Local,pc.Path,pc.Id),new(Side.Dropbox,db.Path,db.Id)],null),DateTimeOffset.UnixEpoch,false),default);
            var reviewed = store.OpenNotices.Single();
            var source = choice == ResolutionChoice.UseLocal ? local : remote;
            var target = choice == ResolutionChoice.UseLocal ? remote : local;
            source.Items["item"] = (Scenarios.ItemChecks.File(source.Side, "item", 3, source.Items["item"].Entry.Id), [3]);
            var displaced = Scenarios.ItemChecks.File(target.Side, "item", 4, target.Items["item"].Entry.Id);
            target.Items["item"] = (displaced, [4]);
            var outcome = await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { },
                new PathScope(pair,[pair],DriveType.Fixed), reviewed, choice, null, local,remote,store,null!,null!,default);
            Check.Require(outcome == EffectOutcome.Applied && store.OpenNotices.Count == 0 &&
                local.Items["item"].Bytes.SequenceEqual(new byte[]{3}) && remote.Items["item"].Bytes.SequenceEqual(new byte[]{3}) &&
                target.History.Any(v => v.Entry == displaced && v.Bytes.SequenceEqual(new byte[]{4})) &&
                store.Records.Values.Single() == SyncedEntries.From(local.Items["item"].Entry,remote.Items["item"].Entry),
                $"{choice} did not settle the current versions and preserve the displaced content without reader proof");
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

    /// <summary>A read failure is reported without planning or executing a choice.</summary>
    public static async Task ReadFailureAsync()
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
            var reports = new List<RunEvent>();
            for (var i = 0; i < 2; i++)
            {
                var outcome = await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, reports.Add, scope,
                    reviewed, ResolutionChoice.UseLocal, null, local, remote, store, null!, null!, default);
                var failure = store.OpenNotices.Single(n => n.Kind == NoticeKind.Failure);
                Check.Require(outcome == EffectOutcome.Refused && failure.Cause == TextKey.ReadDropboxFailed &&
                    failure.Subject.Incoming == Side.Dropbox && failure.Subject.Items.SequenceEqual([new NoticeItem(Side.Dropbox, "item", "db")]) &&
                    reports.Count(e => e.Kind == RunEventKind.Failed) == i + 1 &&
                    store.OpenNotices.Contains(reviewed), "Read failure changed the reviewed choice or lost its subject");
            }
        }
    }

    /// <summary>Source and destination failures at one spelling retain the identity on the side that failed.</summary>
    public static async Task EffectFailureAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            var local = new TestDoubles.FakeSide(Side.Local); var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var pc = Scenarios.ItemChecks.File(Side.Local,"p",1,"source");
            var db = Scenarios.ItemChecks.File(Side.Dropbox,"p",0,"incumbent",true);
            local.Seed(pc,[1]); remote.Seed(db,[]);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Tie,TextKey.TieKindBoth,"p",
                new([new(Side.Local,"p",pc.Id),new(Side.Dropbox,"p",db.Id)],null),DateTimeOffset.UnixEpoch,false),default);
            var reviewed = store.OpenNotices.Single();
            remote.AfterStage = () => local.ReadFailures[pc.Path] = new ChangedUnderneathException(pc.Path);
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
            var outcome = await new Resolve().ApplyAsync(pair,Scenarios.ItemChecks.Settings,_=>{},scope,reviewed,
                ResolutionChoice.UseLocal,null,local,remote,store,Scenarios.ItemChecks.Population(pair,scope,local,remote,store),
                Scenarios.ItemChecks.Hop(pair,scope,queue,local,remote,store),default,queue.Held);
            var failure = store.OpenNotices.Single(n=>n.Kind==NoticeKind.Failure);
            Check.Require(outcome == EffectOutcome.Refused && failure.Cause == TextKey.FailChanged && failure.Subject.Incoming == Side.Local &&
                failure.Subject.Items.SequenceEqual([new NoticeItem(Side.Local,"p",pc.Id)]) && store.OpenNotices.Contains(reviewed) &&
                local.Requests[nameof(ISide.OpenReadAsync)] == 1 && remote.Requests[nameof(ISide.StageAsync)] == 1 &&
                remote.Requests[nameof(ISide.DeleteAsync)] == 0 && remote.Items["p"].Entry == db,"Bare source change during file-over-folder replacement blamed the destination");
        }
        foreach (var sourceFailed in new[]{true, false})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local);
            var remote = new TestDoubles.FakeSide(Side.Dropbox);
            local.Seed(Scenarios.ItemChecks.File(Side.Local, "p", 1, "pc"), [1]);
            remote.Seed(Scenarios.ItemChecks.File(Side.Dropbox, "p", 2, "db"), [2]);
            var members = new[]{new NoticeItem(Side.Local,"p","pc"), new NoticeItem(Side.Dropbox,"p","db")};
            var original = new Notice(0, NoticeKind.Tie, TextKey.TieEqualTimes, "p", new(members,null), DateTimeOffset.UnixEpoch, false);
            await store.SaveNoticeAsync(pair.PairId, original, default);
            var reviewed = store.OpenNotices.Single();
            var failed = sourceFailed ? members[0] : members[1];
            if (sourceFailed) local.ContentFailures["p"] = new ItemFailedException("p", TextKey.ReadLocalFailed, null);
            else remote.PublishFailures["p"] = new PathOccupiedException("p", Side.Dropbox);
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
            var outcome = await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, scope, reviewed,
                ResolutionChoice.UseLocal, null, local, remote, store, Scenarios.ItemChecks.Population(pair,scope,local,remote,store),
                Scenarios.ItemChecks.Hop(pair,scope,queue,local,remote,store),default,queue.Held);
            var failure = store.OpenNotices.Single(n => n.Kind == NoticeKind.Failure);
            Check.Require(outcome == EffectOutcome.Refused && failure.Subject.Incoming == failed.Side &&
                failure.Subject.Items.SequenceEqual([failed]) && store.OpenNotices.Contains(reviewed) &&
                remote.Items["p"].Bytes.SequenceEqual(new byte[]{2}), "An effect failure retained the wrong side or retired the choice");
        }
    }

    /// <summary>One- and two-label choices use only real links; a resumed folder replacement must not select another occupant.</summary>
    public static async Task ReviewedEndpointsAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var scope = new PathScope(pair, [pair], DriveType.Fixed);
        // Both endpoints of the losing link were reviewed together. The first deletion retires that link.
        foreach (var keep in new[]{ResolutionChoice.KeepLocal, ResolutionChoice.KeepDropbox})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local); var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
            var a = Scenarios.ItemChecks.File(Side.Local,"left",1,"pc-a");
            var b = Scenarios.ItemChecks.File(Side.Dropbox,"left",1,"db-a");
            var c = Scenarios.ItemChecks.File(Side.Local,"right",2,"pc-b");
            var d = Scenarios.ItemChecks.File(Side.Dropbox,"right",2,"db-b");
            local.Seed(a,[1]); remote.Seed(b,[1]); local.Seed(c,[2]); remote.Seed(d,[2]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(c,d),null,null,default);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Info,TextKey.InfoMovedTwice,"left",
                new([new(Side.Local,a.Path,a.Id),new(Side.Dropbox,d.Path,d.Id),new(Side.Local,c.Path,c.Id),new(Side.Dropbox,b.Path,b.Id)],null),
                DateTimeOffset.UnixEpoch,false),default);
            EffectOutcome outcome;
            try
            {
                outcome = await new Resolve().ApplyAsync(pair,Scenarios.ItemChecks.Settings,_=>{},scope,store.OpenNotices.Single(),
                    keep,null,local,remote,store,Scenarios.ItemChecks.Population(pair,scope,local,remote,store),Scenarios.ItemChecks.Hop(pair,scope,queue,local,remote,store),default,queue.Held);
            }
            catch (EngineRequestException error) when (error.Key == TextKey.ResolveSubjectStale)
            {
                throw new InvalidDataException("Moved-twice resolution stopped after deleting only one endpoint", error);
            }
            var survivor = keep == ResolutionChoice.KeepLocal ? a.Path : d.Path;
            Check.Require(outcome == EffectOutcome.Applied && local.Items.Keys.Single() == survivor && remote.Items.Keys.Single() == survivor &&
                local.History.Single().Entry.Id == (keep == ResolutionChoice.KeepLocal ? c.Id : a.Id) &&
                remote.History.Single().Entry.Id == (keep == ResolutionChoice.KeepLocal ? d.Id : b.Id) &&
                store.Records.Values.Single() == (keep == ResolutionChoice.KeepLocal ? SyncedEntries.From(a,b) : SyncedEntries.From(c,d)) &&
                store.OpenNotices.Count == 0,"Moved-twice resolution stopped after deleting only one endpoint");
        }
        foreach (var intervene in new[]{false,true})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local); var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
            var a = Scenarios.ItemChecks.File(Side.Local,"p",0,"pc",true);
            var b = Scenarios.ItemChecks.File(Side.Dropbox,"p",0,"incumbent",true);
            local.Seed(a,[]); remote.Seed(b,[]);
            // Give the selected Dropbox delete a nonblank finite window (BoundaryAsync peeks the last page without consuming it; one page suffices) and an exact terminal selected-folder receipt, so real FolderDelete settles Removed instead of Unknown. No AutoSync coverage is supplied.
            remote.Pages.Enqueue(new ChangePage([], "cursor", null)); var receipt = DeleteReceipt.On(remote);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.NameClash,TextKey.ClashNameTaken,"p",
                new([new(Side.Local,"p",a.Id),new(Side.Dropbox,"p",b.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
            var reviewed = store.OpenNotices.Single();
            void Between(RunEvent e)
            {
                if (intervene && e.Action == ActionKind.DeleteDropbox)
                    remote.Seed(Scenarios.ItemChecks.File(Side.Dropbox,"p",3,"replacement"),[3]);
            }
            try
            {
                var done = await new Resolve().ApplyAsync(pair,Scenarios.ItemChecks.Settings,Between,scope,reviewed,ResolutionChoice.ReplaceExisting,
                    null,local,receipt,store,Scenarios.ItemChecks.Population(pair,scope,local,remote,store),Scenarios.ItemChecks.Hop(pair,scope,queue,local,remote,store),default,queue.Held);
                Check.Require(!intervene && done == EffectOutcome.Applied,"Unbound folder replacement accepted an intervening occupant");
            }
            catch (EngineRequestException e) when (intervene && e.Key == TextKey.ResolveSubjectStale) { }
            Check.Require(local.Items["p"].Entry == a && (intervene ? remote.Items["p"].Entry.Id == "replacement" && store.OpenNotices.Contains(reviewed) :
                remote.Items["p"].Entry.IsFolder && remote.Items["p"].Entry.Id != b.Id && store.OpenNotices.Count == 0),
                "Unbound folder replacement failed to honor its first leg");
        }
        foreach (var twoLabels in new[]{false, true})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local);
            var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
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
                ResolutionChoice.UseLocal, null, local, remote, store, Scenarios.ItemChecks.Population(pair,scope,local,remote,store), Scenarios.ItemChecks.Hop(pair,scope,queue,local,remote,store), default,queue.Held);
            Check.Require(local.Items.Count == 1 && remote.Items.Count == 1 &&
                local.Items["p"].Bytes.SequenceEqual(new byte[]{1}) && remote.Items["p"].Bytes.SequenceEqual(new byte[]{1}) &&
                store.OpenNotices.Count == 0, "Resolution did not keep only the selected originals");
        }
        foreach (var replacement in new[]{false, true})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var local = new TestDoubles.FakeSide(Side.Local);
            var remote = new TestDoubles.FakeSide(Side.Dropbox);
            var queue = new Massdex.SyncShark.Engine.Run.WorkQueue();
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
                    store.OpenNotices.Single(), ResolutionChoice.ReplaceExisting, null, local, remote, store, Scenarios.ItemChecks.Population(pair,scope,local,remote,store), Scenarios.ItemChecks.Hop(pair,scope,queue,local,remote,store), default,queue.Held);
            }
            catch (EngineRequestException e) when (replacement && e.Key == TextKey.ResolveSubjectStale) { }
            Check.Require(store.OpenNotices.Count == (replacement ? 1 : 0) && remote.Mutations.Count == 0 &&
                (!replacement || remote.Items["after"].Entry.Id == "replacement"), "Resume acted on a new occupant or failed to close");
        }
    }

    /// <summary>Adds the exact terminal selected-folder receipt to the fake Dropbox delete; this fixture has no dispatcher.</summary>
    public class DeleteReceipt : System.Reflection.DispatchProxy
    {
        private ISide target = null!;
        internal static ISide On(ISide target)
        {
            var side = Create<ISide, DeleteReceipt>();
            ((DeleteReceipt)(object)side).target = target;
            return side;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ISide.DeleteAsync) && args is { Length: 4 })
                return Delete((Entry)args[0]!, (RemovalSelection?)args[1], (ActionKind)args[2]!, (CancellationToken)args[3]!);
            try { return method.Invoke(target, args); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
        }
        private async Task<RemovalResult> Delete(Entry expected, RemovalSelection? selection, ActionKind origin, CancellationToken ct)
        {
            var sent = await target.DeleteAsync(expected, selection, origin, ct);
            return sent with { Reply = new JobReply.Terminal([(expected, null, null)]) };
        }
    }
}
