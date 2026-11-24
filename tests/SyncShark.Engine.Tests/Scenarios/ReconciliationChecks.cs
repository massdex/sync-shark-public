using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Exercises the whole pair comparison; the oracle checks bytes, exact spelling, bindings and notice causes independently.</summary>
public static class ReconciliationChecks
{
    private const string Original = "item";
    internal sealed record FileAt(string Path, byte Value, bool Folder = false, string? Id = null, DateTimeOffset? Modified = null, long? Size = null);
    private static void Seed(FakeSide side, IEnumerable<FileAt> entries)
    {
        foreach (var f in entries)
        {
            var id = f.Id ?? side.Side.ToString();
            var modified = f.Modified ?? DateTimeOffset.UnixEpoch.AddSeconds(f.Value);
            var revision = side.Side == Side.Dropbox ? $"rev-{f.Value}" : "";
            var hash = "";
            if (!f.Folder) { using var h = new ContentHash(); h.Append([f.Value]); hash = h.Finish(); }
            side.Seed(new Entry(f.Path, f.Folder, f.Size ?? (f.Folder ? 0 : 1), modified, id, revision, hash, null), f.Folder ? [] : [f.Value]);
        }
    }
    // Repair and any full scan need the real store (BeginScan/scan comparison); a fake scan is prohibited.
    private static async Task WithStoreAsync(PairConfig pair, Func<IStore,Task> run)
    {
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=image,Pooling=false}.ToString()))
            {
                await db.OpenAsync();
                db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
                using var schema=db.CreateCommand();
                schema.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create+$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)};";
                await schema.ExecuteNonQueryAsync();
            }
            using var store=new global::Massdex.SyncShark.Engine.Storage.Store(image,false,ItemChecks.Settings);
            await store.SavePairAsync(pair,default);
            await run(store);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); directory.Delete(true); }
    }
    internal static Task CompareAsync(bool baseline, FileAt[] pc, FileAt[] cloud, FileAt[] finalPc, FileAt[] finalCloud,
        int links, TextKey[] notices, FirstRunPolicy policy = FirstRunPolicy.NewestWins, string[]? ignores = null, Exclusion[]? exclusions = null, bool expectMove=false, bool baselineFolder=false, bool noDeletes=false, bool pastFirstSync=false, bool admit=false)
    {
        var pair = ItemChecks.Pair() with { FirstRun = policy, IgnoreRules = ignores ?? [], Exclusions = exclusions ?? [] };
        return global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        await WithStoreAsync(pair, async store =>
        {
            var local = new FakeSide(Side.Local); var remote = new FakeSide(Side.Dropbox); Seed(local,pc); Seed(remote,cloud);
            if (baseline || pastFirstSync) await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
            if (baseline) await store.CommitAsync(pair.PairId, null, SyncedEntries.From(
                ItemChecks.File(Side.Local, Original, baselineFolder?(byte)0:(byte)1, Side.Local.ToString(), baselineFolder), ItemChecks.File(Side.Dropbox, Original, baselineFolder?(byte)0:(byte)1, Side.Dropbox.ToString(), baselineFolder)),null,null,default);
            var before=new[]{local,remote}.SelectMany(side=>side.Items.Values.Select(v=>(Side:side,v.Entry,v.Bytes))).ToArray();
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var queue=await ItemChecks.StartQueueAsync(pair,paths,local,remote,store);
            if(admit) FolderChecks.AdmitFullRepair(queue,local,remote);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue, local, remote, store, null,null, deadline);
            // A displaced edition is proved by identity, revision and bytes; a counterpart move relocates it, so its path may differ.
            foreach(var item in before.Where(x=>!x.Side.Items.Values.Any(v=>v.Entry.Id==x.Entry.Id && v.Bytes.SequenceEqual(x.Bytes))))
                Check.Require(item.Side.History.Any(v=>v.Entry.Id==item.Entry.Id && v.Entry.Revision==item.Entry.Revision && v.Bytes.SequenceEqual(item.Bytes)),"Displaced version is not recoverable");
            if(expectMove)
            {
                // The destination is the expected output, not a fixed spelling; the moved identity must land there by move, never delete+create.
                var original=before.Single(x=>x.Side==remote);
                var landed=remote.Items.Values.Where(v=>v.Entry.Id==original.Entry.Id).Select(v=>v.Entry).FirstOrDefault();
                var destination=finalCloud.Length==1?finalCloud[0].Path:string.Empty;
                Check.Require(landed is {} now && now.Path==destination && remote.Requests[nameof(ISide.MoveAsync)]==1 && remote.Requests[nameof(ISide.DeleteAsync)]==0 && remote.Requests[nameof(ISide.PublishAsync)]==0,"Counterpart recreated instead of moved");
            }
            VerifySide(local,finalPc); VerifySide(remote,finalCloud);
            var records=new List<SyncedEntry>(); await foreach(var r in store.DescendantsAsync(pair.PairId,Side.Local,"",default)) records.Add(r);
            Check.Require(records.Count == links, "Unexpected binding count");
            foreach (var binding in records)
            {
                var a = local.Items[binding.LocalPath].Entry; var b = remote.Items[binding.DropboxPath].Entry;
                Check.Require(binding == SyncedEntries.From(a,b), "The saved paths, identities or versions do not match both live endpoints");
            }
            var open=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,default)) if(!n.Dismissed) open.Add(n);
            Check.Require(open.Select(n=>n.Cause).Order().SequenceEqual(notices.Order()), "Unexpected notices");
            if (noDeletes) Check.Require(local.Requests[nameof(ISide.DeleteAsync)] == 0 && remote.Requests[nameof(ISide.DeleteAsync)] == 0, "A new folder caused a delete");
        }));
    }
    private static void VerifySide(FakeSide side, FileAt[] expected)
    {
        Check.Require(side.Items.Count == expected.Length, $"Unexpected entries on {side.Side}");
        foreach (var f in expected)
        {
            Check.Require(side.Items.TryGetValue(f.Path,out var v) && v.Entry.Path == f.Path && v.Entry.IsFolder == f.Folder &&
                v.Bytes.SequenceEqual(f.Folder ? Array.Empty<byte>() : new byte[]{f.Value}), $"Wrong spelling, kind or bytes on {side.Side} at {f.Path}");
        }
    }
    public static Task MoveAndEditAsync() => CompareAsync(true, [new("renamed",2)], [new(Original,1)], [new("renamed",2)], [new("renamed",2)], 1, []);
    public static Task MoveAndDeleteAsync() => CompareAsync(true, [new("renamed",1)], [], [new("renamed",1)], [new("renamed",1)], 1, [TextKey.InfoMovedDeleted]);
    public static Task MoveAndEditOppositeAsync() => CompareAsync(true, [new("renamed",1)], [new(Original,2)], [new("renamed",1),new(Original,2)], [new("renamed",1),new(Original,2)], 2, [TextKey.InfoMovedEdited]);
    public static Task DifferentDestinationsAsync() => CompareAsync(true, [new("pc-place",1)], [new("cloud-place",1)], [new("pc-place",1),new("cloud-place",1)], [new("pc-place",1),new("cloud-place",1)], 2, [TextKey.InfoMovedTwice]);
    public static Task MoveOnlyAsync() => CompareAsync(true, [new("renamed",1)], [new(Original,1)], [new("renamed",1)], [new("renamed",1)], 1, [],expectMove:true);
    public static Task CapitalsAsync() => CompareAsync(true, [new("ITEM",1)], [new(Original,1)], [new("ITEM",1)], [new("ITEM",1)], 1, []);
    public static Task DifferentCapitalsAsync() => CompareAsync(true, [new("ITEM",1)], [new("Item",1)], [new("ITEM",1)], [new("Item",1)], 1, [TextKey.InfoCapitalsDiffer]);
    public static Task FolderCapitalsAsync() => CompareAsync(false, [new("Folder",0,true)], [new("folder",0,true)], [new("Folder",0,true)], [new("folder",0,true)], 1, []);
    public static Task FirstRunAsync(FirstRunPolicy policy, byte winner, bool admit = false) => CompareAsync(false, [new(Original,policy==FirstRunPolicy.DropboxWins?(byte)5:(byte)2)], [new(Original,4)], [new(Original,winner)], [new(Original,winner)], 1, [], policy, admit: admit);
    public static Task IgnoreAsync() => CompareAsync(false, [new("~$draft",1)], [], [new("~$draft",1)], [], 0, [], ignores:["FileNamePrefix:~$"]);
    public static Task ExcludeKeepAsync() => CompareAsync(false, [new("outside",1)], [], [new("outside",1)], [], 0, [], exclusions:[new(Side.Local,"outside",true)]);
    // A32: the Dropbox adapter classifies an untransferable name with SkipReservedName. Keep no Local copy and
    // retain that exact classification; the engine must not transfer or bind a reserved name.
    public static Task ForbiddenNameAsync()
    {
        var pair = ItemChecks.Pair();
        return global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        await WithStoreAsync(pair, async store =>
        {
            var local = new FakeSide(Side.Local); var remote = new FakeSide(Side.Dropbox);
            remote.Seed(new Entry(Original, false, 1L, DateTimeOffset.UnixEpoch.AddSeconds(1), Side.Dropbox.ToString(), "rev-1", Hash(1), nameof(TextKey.SkipReservedName)), [1]);
            var paths = new PathScope(pair, [pair], DriveType.Fixed);
            var queue = await ItemChecks.StartQueueAsync(pair, paths, local, remote, store);
            await Repair.RunAsync(pair, ItemChecks.Settings, _ => { }, paths, queue, local, remote, store, null, null, deadline);
            Check.Require(local.Items.Count == 0 && !local.Items.ContainsKey(Original), "A reserved Dropbox name was copied to Local");
            Check.Require(remote.Items.TryGetValue(Original, out var kept) && kept.Entry.Skip == nameof(TextKey.SkipReservedName) && kept.Bytes.SequenceEqual(new byte[] { 1 }), "The reserved-name classification or the cloud copy changed");
            var records = new List<SyncedEntry>(); await foreach (var r in store.DescendantsAsync(pair.PairId, Side.Local, "", default)) records.Add(r);
            Check.Require(records.Count == 0, "A reserved Dropbox name was bound");
        }));
    }
    public static Task KindReplacementAsync() => CompareAsync(true, [new(Original,0,true)], [new(Original,1)], [new(Original,0,true)], [new(Original,0,true)], 1, []);

    public static Task MoveOutsideAsync() => CompareAsync(true,[new("held/item",1)],[new(Original,1)], [new("held/item",1)], [], 0, [], exclusions:[new(Side.Local,"held",true)]);

    public static Task A02ChildlessFolderCreateLocalAsync() => CompareAsync(false,
        [new("folder",0,true,"L-folder")], [], [new("folder",0,true)], [new("folder",0,true)], 1, []);
    public static Task A02ChildlessFolderCreateCloudAsync() => CompareAsync(false,
        [], [new("folder",0,true,"D-folder")], [new("folder",0,true)], [new("folder",0,true)], 1, []);
    public static Task A02PopulatedFolderCreateLocalAsync() => CompareAsync(false,
        [new("folder",0,true,"L-folder"),new("folder/one",1,Id:"L-one"),new("folder/two",2,Id:"L-two")], [],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)], 3, []);
    public static Task A02PopulatedFolderCreateCloudAsync() => CompareAsync(false,
        [], [new("folder",0,true,"D-folder"),new("folder/one",1,Id:"D-one"),new("folder/two",2,Id:"D-two")],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)], 3, []);
    public static Task A02OutsideFolderMovedInWithoutChildReportsAsync() => CompareAsync(false,
        [new("moved",0,true,"L-moved"),new("moved/a",1,Id:"L-a"),new("moved/b",2,Id:"L-b")], [],
        [new("moved",0,true),new("moved/a",1),new("moved/b",2)],
        [new("moved",0,true),new("moved/a",1),new("moved/b",2)], 3, []);
    // The other side already has the folder the interrupted pass created (unrecorded, empty); the replay lists
    // it again, links the folder and creates its children, never a delete (PLAN A02, M4 add-only).
    public static Task A02RestartAfterCreateBeforeListingAsync() => CompareAsync(false,
        [new("folder",0,true,"L-folder"),new("folder/one",1,Id:"L-one"),new("folder/two",2,Id:"L-two")],
        [new("folder",0,true,"D-folder")],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)], 3, [], noDeletes:true);
    public static Task A02AddOnlyPopulationNeverDeletesAsync() => CompareAsync(false,
        [new("folder",0,true,"L-folder"),new("folder/one",1,Id:"L-one"),new("folder/two",2,Id:"L-two")], [],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)],
        [new("folder",0,true),new("folder/one",1),new("folder/two",2)], 3, [], noDeletes:true);
    public static Task A03EqualTrustedTimesAsync()
    {
        const string pc="Workstation";var settings=ItemChecks.Settings;var at=DateTimeOffset.UnixEpoch;
        var local=global::Massdex.SyncShark.Engine.Planner.ConflictName.For("item.txt",Side.Local,at,pc,settings,1);var remote=global::Massdex.SyncShark.Engine.Planner.ConflictName.For("item.txt",Side.Dropbox,at,pc,settings,1);
        Check.Require(local.Contains(pc,StringComparison.Ordinal) && !remote.Contains(pc,StringComparison.Ordinal) && remote.Contains(settings.ConflictLabelDropboxName,StringComparison.Ordinal),"Conflict label lost its source");
        return RecoveryChecks.KeptVersionsAsync(false);
    }
    // Past the first sync two different no-record files at the same path keep both (PLAN L226), whatever the times.
    public static Task A03UnequalTrustedTimesAsync() => CompareAsync(false,
        [new(Original,2)], [new(Original,3)],
        [new(Original,2), new(Label(Original,Side.Dropbox,At(3)),3)],
        [new(Original,3), new(Label(Original,Side.Local,At(2)),2)],
        2, [TextKey.InfoKeptBoth], pastFirstSync:true);
    // A03 four-endpoint kept-both fixture shared by the use-side, keep-both and rename resolutions. One kept-both notice
    // names both originals and each side's labelled copy; the two cross bindings are (pc, dbLabel) and (pcLabel, db).
    private sealed class KeptBoth : IDisposable
    {
        internal PairConfig Pair { get; } = ItemChecks.Pair();
        internal PathScope Scope { get; }
        internal InMemoryStateStore Store { get; } = new();
        internal FakeSide Local { get; } = new(Side.Local);
        internal FakeSide Remote { get; } = new(Side.Dropbox);
        internal WorkQueue Queue { get; } = new();
        internal Entry Pc { get; }
        internal Entry Db { get; }
        internal Entry DbLabel { get; }
        internal Entry PcLabel { get; }
        internal KeptBoth()
        {
            Scope = new PathScope(Pair, [Pair], DriveType.Fixed);
            Pc = ItemChecks.File(Side.Local, "item", 1, "pc");
            Db = ItemChecks.File(Side.Dropbox, "item", 2, "db");
            DbLabel = ItemChecks.File(Side.Dropbox, "pc-label", 1, "db-label");
            PcLabel = ItemChecks.File(Side.Local, "db-label", 2, "pc-label");
            Local.Seed(Pc, [1]); Remote.Seed(Db, [2]); Remote.Seed(DbLabel, [1]); Local.Seed(PcLabel, [2]);
        }
        internal async Task PrepareAsync()
        {
            await Store.CommitAsync(Pair.PairId, null, SyncedEntries.From(Pc, DbLabel), null, null, default);
            await Store.CommitAsync(Pair.PairId, null, SyncedEntries.From(PcLabel, Db), null, null, default);
            var items = new List<NoticeItem> { new(Side.Local, Pc.Path, Pc.Id), new(Side.Dropbox, Db.Path, Db.Id),
                new(Side.Dropbox, DbLabel.Path, DbLabel.Id), new(Side.Local, PcLabel.Path, PcLabel.Id) };
            await Store.SaveNoticeAsync(Pair.PairId, new(0, NoticeKind.Info, TextKey.InfoKeptBoth, "item", new(items, null), DateTimeOffset.UnixEpoch, false), default);
        }
        internal Task<EffectOutcome> ApplyAsync(ResolutionChoice choice, string? name = null) =>
            new Resolve().ApplyAsync(Pair, ItemChecks.Settings, _ => { }, Scope, Store.OpenNotices.Single(), choice, name,
                Local, Remote, Store, ItemChecks.Population(Pair, Scope, Local, Remote, Store),
                ItemChecks.Hop(Pair, Scope, Queue, Local, Remote, Store), default, Queue.Held);
        internal async Task<EffectOutcome> ResolveAsync(ResolutionChoice choice, string? name = null)
        {
            await PrepareAsync();
            return await ApplyAsync(choice, name);
        }
        public void Dispose() => Store.Dispose();
    }
    // Resolving by a side keeps the chosen bytes at both originals, retires both labelled copies, retains their exact
    // identity, revision and bytes, keeps one surviving binding and closes the notice.
    private static async Task ResolveKeptBothAsync(ResolutionChoice choice)
    {
        using var x = new KeptBoth();
        var outcome = await x.ResolveAsync(choice);
        var chosen = choice == ResolutionChoice.UseLocal ? (byte)1 : (byte)2;
        Check.Require(outcome == EffectOutcome.Applied, $"{choice} did not settle the kept-both notice");
        Check.Require(x.Local.Items.Count == 1 && x.Remote.Items.Count == 1 &&
            x.Local.Items["item"].Bytes.SequenceEqual(new[]{chosen}) && x.Remote.Items["item"].Bytes.SequenceEqual(new[]{chosen}),
            $"{choice} did not keep the chosen bytes at both originals");
        Check.Require(!x.Local.Items.ContainsKey("db-label") && !x.Remote.Items.ContainsKey("pc-label"),
            $"{choice} left a labelled copy in place");
        Check.Require(x.Local.History.Any(v => v.Entry == x.PcLabel && v.Bytes.SequenceEqual(new byte[]{2})) &&
            x.Remote.History.Any(v => v.Entry == x.DbLabel && v.Bytes.SequenceEqual(new byte[]{1})),
            $"{choice} did not retain the exact displaced labelled copies (identity, revision and bytes)");
        Check.Require(x.Store.Records.Values.Single() == SyncedEntries.From(x.Local.Items["item"].Entry, x.Remote.Items["item"].Entry),
            $"{choice} did not keep exactly one surviving binding");
        Check.Require(x.Store.OpenNotices.Count == 0, $"{choice} left the notice open");
    }
    public static Task A03ResolveUsePcAsync() => ResolveKeptBothAsync(ResolutionChoice.UseLocal);
    public static Task A03ResolveUseDropboxAsync() => ResolveKeptBothAsync(ResolutionChoice.UseDropbox);
    // Keep-both acknowledges the kept-both notice without moving or removing any of the four endpoints: the exact two
    // saved bindings and all four copies must be unchanged (Planner.Resolutions.For returns no actions for KeepBoth).
    public static async Task A03ResolveKeepBothAsync()
    {
        using var x = new KeptBoth();
        await x.PrepareAsync();
        var before = x.Store.Records.Values.ToHashSet();
        var outcome = await x.ApplyAsync(ResolutionChoice.KeepBoth);
        Check.Require(outcome == EffectOutcome.Applied && x.Store.OpenNotices.Count == 0, "Resolving keep-both did not acknowledge the notice");
        Check.Require(before.Count == 2 && x.Store.Records.Values.ToHashSet().SetEquals(before),
            "Resolving keep-both changed a saved binding");
        Check.Require(x.Local.Items.Count == 2 && x.Remote.Items.Count == 2 &&
            x.Local.Items["item"].Bytes.SequenceEqual(new byte[]{1}) && x.Local.Items["db-label"].Bytes.SequenceEqual(new byte[]{2}) &&
            x.Remote.Items["item"].Bytes.SequenceEqual(new byte[]{2}) && x.Remote.Items["pc-label"].Bytes.SequenceEqual(new byte[]{1}),
            "Resolving keep-both moved or removed an original or a labelled copy");
    }
    // A rename resolution moves the chosen side's labelled copy to the requested single-segment name with its bytes and
    // identity intact, leaving both originals and closing the notice (Planner.Resolutions.For -> Unlinked -> MoveUnlinked).
    public static Task A03ResolveRenamePcAsync() => RenameLabelAsync(ResolutionChoice.RenameLocal, Side.Local);
    public static Task A03ResolveRenameDropboxAsync() => RenameLabelAsync(ResolutionChoice.RenameDropbox, Side.Dropbox);
    private static async Task RenameLabelAsync(ResolutionChoice choice, Side labelSide)
    {
        using var x = new KeptBoth();
        await x.PrepareAsync();
        var outcome = await x.ApplyAsync(choice, "renamed");
        var label = labelSide == Side.Local ? x.PcLabel : x.DbLabel;
        var bytes = labelSide == Side.Local ? (byte)2 : (byte)1;
        var endpoint = labelSide == Side.Local ? x.Local : x.Remote;
        Check.Require(outcome == EffectOutcome.Applied && x.Store.OpenNotices.Count == 0, $"{choice} did not close the kept-both notice");
        Check.Require(endpoint.Items.TryGetValue("renamed", out var moved) && moved.Bytes.SequenceEqual(new[]{bytes}) && moved.Entry.Id == label.Id,
            $"{choice} did not rename the labelled copy to the requested name with its bytes and identity");
        Check.Require(!endpoint.Items.ContainsKey(label.Path) && x.Local.Items["item"].Bytes.SequenceEqual(new byte[]{1}) &&
            x.Remote.Items["item"].Bytes.SequenceEqual(new byte[]{2}), $"{choice} left the old label or moved an original");
        var change = new Change(labelSide, ChangeKind.Renamed, moved.Entry.Path, label.Path, label.Id, moved.Entry, false, false);
        await ItemSync.ApplyAsync(x.Pair, ItemChecks.Settings, _ => { }, x.Scope, change, x.Local, x.Remote, x.Store,
            ItemChecks.Population(x.Pair, x.Scope, x.Local, x.Remote, x.Store), ItemChecks.Hop(x.Pair, x.Scope, x.Queue, x.Local, x.Remote, x.Store), default);
        var peer = labelSide == Side.Local ? x.Remote : x.Local;
        var other = labelSide == Side.Local ? x.Db : x.Pc;
        Check.Require(peer.Items.TryGetValue("renamed", out var mate) && mate.Entry.Id == other.Id && mate.Bytes.SequenceEqual(new[] { bytes }) &&
            x.Store.Records.Values.Contains(SyncedEntries.From(x.Local.Items["renamed"].Entry, x.Remote.Items["renamed"].Entry)), "Resolved rename did not sync the linked peer");
        var untouched = labelSide == Side.Local ? x.Pc : x.Db;
        var untouchedMate = labelSide == Side.Local ? x.DbLabel : x.PcLabel;
        Check.Require(endpoint.Items[untouched.Path].Entry == untouched && peer.Items[untouchedMate.Path].Entry == untouchedMate &&
            endpoint.Items[untouched.Path].Bytes.SequenceEqual(peer.Items[untouchedMate.Path].Bytes), "Resolved rename disturbed the other pair of versions");
    }
    // One kept-both notice names all four endpoints: both originals and each side's labelled copy (PLAN A03).
    public static Task A03FourEndpointNoticeAsync()
    {
        var pair = ItemChecks.Pair();
        return global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        await WithStoreAsync(pair, async store =>
        {
            await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var local=new FakeSide(Side.Local); var remote=new FakeSide(Side.Dropbox);
            local.Seed(ItemChecks.File(Side.Local,"item",2,"pc"),[2]);
            remote.Seed(ItemChecks.File(Side.Dropbox,"item",3,"db"),[3]);
            var queue=await ItemChecks.StartQueueAsync(pair,paths,local,remote,store);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,remote,store,null,null,deadline);
            var open=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,default)) if(!n.Dismissed) open.Add(n);
            Check.Require(open.Count==1 && open.Single().Cause==TextKey.InfoKeptBoth,"Expected exactly one kept-both notice");
            Check.Require(open.Single().Subject.Items.Count==4,"Kept-both notice did not name all four endpoints");
        }));
    }
    public static async Task A05SameIdSizeMtimeWriteAfterPublicationAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new InMemoryStateStore();var local=new FakeSide(Side.Local);var remote=new FakeSide(Side.Dropbox);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,local,remote,store);
        var before=ItemChecks.File(Side.Local,"file",1,"pc");var db=ItemChecks.File(Side.Dropbox,"file",1,"db");
        var after=ItemChecks.File(Side.Local,"file",2,before.Id) with { Modified=before.Modified };
        local.Seed(after,[2]);remote.Seed(db,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(before,db),new(1,10),null,default);
        await store.SaveNoticeAsync(pair.PairId,new(1,NoticeKind.Failure,TextKey.FailChanged,before.Path,new([new(Side.Local,before.Path,before.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,after.Path,null,after.Id,after,true,false,11,JournalId:1),local,remote,store,ItemChecks.Population(pair,paths,local,remote,store),ItemChecks.Hop(pair,paths,queue,local,remote,store),default);
        Check.Require(remote.Items[after.Path].Bytes.SequenceEqual(new byte[]{2}) && remote.History.Any(v=>v.Entry==db),"Stale publication or stamp-only proof hid changed bytes");
    }
    public static Task A05SameWriteAtPublicationBoundaryAsync() => throw new NotImplementedException();
    private static DateTimeOffset At(byte v) => DateTimeOffset.UnixEpoch.AddSeconds(v);
    private static string Label(string path, Side side, DateTimeOffset modified) =>
        global::Massdex.SyncShark.Engine.Planner.ConflictName.For(path, side, modified, ItemChecks.Settings.ComputerName, ItemChecks.Settings, 1);
    public static Task A07DifferentFileEditsAsync() => CompareAsync(true,
        [new(Original,2)], [new(Original,3)],
        [new(Original,2), new(Label(Original,Side.Dropbox,At(3)),3)],
        [new(Original,3), new(Label(Original,Side.Local,At(2)),2)],
        2, [TextKey.InfoKeptBoth]);
    public static Task A07TwoKindFlipsToIdenticalFilesAsync() => CompareAsync(true,
        [new(Original,2)], [new(Original,2)], [new(Original,2)], [new(Original,2)], 1, [], baselineFolder:true);
    public static Task A07TwoKindFlipsToDifferentFilesAsync() => CompareAsync(true,
        [new(Original,2)], [new(Original,3)],
        [new(Original,2), new(Label(Original,Side.Dropbox,At(3)),3)],
        [new(Original,3), new(Label(Original,Side.Local,At(2)),2)],
        2, [TextKey.InfoKeptBoth], baselineFolder:true);
    // A20: a moved-twice notice names two saved links (A<->B and C<->D). Keeping one place retires the other on both
    // sides; keep-both acknowledges without moving anything. Each selected losing endpoint is guarded so a replaced
    // occupant is refused, never removed. Mirrors Executor/ResolveChecks ReviewedEndpoints' two-link fixture.
    private sealed class MovedTwice : IDisposable
    {
        internal PairConfig Pair { get; } = ItemChecks.Pair();
        internal PathScope Scope { get; }
        internal InMemoryStateStore Store { get; } = new();
        internal FakeSide Local { get; } = new(Side.Local);
        internal FakeSide Remote { get; } = new(Side.Dropbox);
        internal WorkQueue Queue { get; } = new();
        internal Entry A = null!, B = null!, C = null!, D = null!;
        internal MovedTwice() => Scope = new PathScope(Pair, [Pair], DriveType.Fixed);
        internal async Task PrepareAsync()
        {
            A = ItemChecks.File(Side.Local, "left", 1, "pc-a");
            B = ItemChecks.File(Side.Dropbox, "left", 1, "db-a");
            C = ItemChecks.File(Side.Local, "right", 2, "pc-b");
            D = ItemChecks.File(Side.Dropbox, "right", 2, "db-b");
            Local.Seed(A, [1]); Remote.Seed(B, [1]); Local.Seed(C, [2]); Remote.Seed(D, [2]);
            await Store.CommitAsync(Pair.PairId, null, SyncedEntries.From(A, B), null, null, default);
            await Store.CommitAsync(Pair.PairId, null, SyncedEntries.From(C, D), null, null, default);
            var items = new List<NoticeItem> { new(Side.Local, A.Path, A.Id), new(Side.Dropbox, D.Path, D.Id),
                new(Side.Local, C.Path, C.Id), new(Side.Dropbox, B.Path, B.Id) };
            await Store.SaveNoticeAsync(Pair.PairId, new(0, NoticeKind.Info, TextKey.InfoMovedTwice, "left", new(items, null), DateTimeOffset.UnixEpoch, false), default);
        }
        internal (Entry Entry, string Bytes)[] Endpoints() => new[] { Local, Remote }.SelectMany(side => side.Items.Values.OrderBy(v => v.Entry.Path, StringComparer.Ordinal)
            .Select(v => (v.Entry, Convert.ToHexString(v.Bytes)))).ToArray();
        internal Task<EffectOutcome> ApplyAsync(ResolutionChoice choice) =>
            new Resolve().ApplyAsync(Pair, ItemChecks.Settings, _ => { }, Scope, Store.OpenNotices.Single(), choice, null,
                Local, Remote, Store, ItemChecks.Population(Pair, Scope, Local, Remote, Store),
                ItemChecks.Hop(Pair, Scope, Queue, Local, Remote, Store), default, Queue.Held);
        public void Dispose() => Store.Dispose();
    }
    public static Task A20ResolveKeepPcPlaceAsync() => KeepPlaceAsync(ResolutionChoice.KeepLocal, "left");
    public static Task A20ResolveKeepDropboxPlaceAsync() => KeepPlaceAsync(ResolutionChoice.KeepDropbox, "right");
    private static async Task KeepPlaceAsync(ResolutionChoice choice, string keeper)
    {
        using var x = new MovedTwice();
        await x.PrepareAsync();
        var outcome = await x.ApplyAsync(choice);
        Check.Require(outcome == EffectOutcome.Applied && x.Store.OpenNotices.Count == 0, $"{choice} did not settle the moved-twice notice");
        Check.Require(x.Local.Items.Count == 1 && x.Remote.Items.Count == 1 && x.Local.Items.ContainsKey(keeper) && x.Remote.Items.ContainsKey(keeper),
            $"{choice} kept the wrong place or an extra endpoint");
        var survivor = choice == ResolutionChoice.KeepLocal ? SyncedEntries.From(x.A, x.B) : SyncedEntries.From(x.C, x.D);
        Check.Require(x.Store.Records.Values.Single() == survivor, $"{choice} kept the wrong surviving binding");
        var losing = keeper == "left" ? (x.C, x.D, (byte)2) : (x.A, x.B, (byte)1);
        Check.Require(x.Local.History.Any(v => v.Entry == losing.Item1 && v.Bytes.SequenceEqual(new[] { losing.Item3 })) &&
            x.Remote.History.Any(v => v.Entry == losing.Item2 && v.Bytes.SequenceEqual(new[] { losing.Item3 })), "Retired versions are not recoverable");
        var kept = keeper == "left" ? (byte)1 : (byte)2;
        Check.Require(x.Local.Items[keeper].Bytes.SequenceEqual(new[]{kept}) && x.Remote.Items[keeper].Bytes.SequenceEqual(new[]{kept}),
            $"{choice} kept the wrong bytes");
    }
    public static async Task A20ResolveKeepBothPlacesAsync()
    {
        using var x = new MovedTwice();
        await x.PrepareAsync();
        var endpoints = x.Endpoints();
        var before = x.Store.Records.Values.ToHashSet();
        var outcome = await x.ApplyAsync(ResolutionChoice.KeepBoth);
        Check.Require(outcome == EffectOutcome.Applied && x.Store.OpenNotices.Count == 0, "Keep-both did not acknowledge the moved-twice notice");
        Check.Require(endpoints.SequenceEqual(x.Endpoints()) && before.SetEquals(x.Store.Records.Values) && x.Local.Items.Count == 2 && x.Remote.Items.Count == 2 &&
            x.Local.Requests[nameof(ISide.DeleteAsync)] == 0 && x.Remote.Requests[nameof(ISide.DeleteAsync)] == 0,
            "Keep-both moved, removed or re-bound a moved-twice endpoint");
    }
    public static Task A20ChoiceSeesReplacedPcOccupantAsync() => ReplacedOccupantAsync(Side.Local);
    public static Task A20ChoiceSeesReplacedCloudOccupantAsync() => ReplacedOccupantAsync(Side.Dropbox);
    private static async Task ReplacedOccupantAsync(Side replaced)
    {
        using var x = new MovedTwice();
        await x.PrepareAsync();
        // Replace the losing endpoint the chosen resolution would delete with a different identity.
        ResolutionChoice choice; string path; FakeSide endpoint; Entry replacement;
        if (replaced == Side.Local) { choice = ResolutionChoice.KeepLocal; path = "right"; endpoint = x.Local; replacement = ItemChecks.File(Side.Local, path, 9, "replacement"); }
        else { choice = ResolutionChoice.KeepDropbox; path = "left"; endpoint = x.Remote; replacement = ItemChecks.File(Side.Dropbox, path, 9, "replacement"); }
        endpoint.Items[path] = (replacement, [9]);
        var endpoints = x.Endpoints(); var bindings = x.Store.Records.Values.ToHashSet(); var notice = x.Store.OpenNotices.Single();
        var stale = false;
        try { await x.ApplyAsync(choice); }
        catch (EngineRequestException e) when (e.Key == TextKey.ResolveSubjectStale) { stale = true; }
        Check.Require(stale, $"{choice} acted on a replaced endpoint instead of refusing it");
        Check.Require(endpoints.SequenceEqual(x.Endpoints()) && bindings.SetEquals(x.Store.Records.Values) && x.Store.OpenNotices.Single().Matches(notice),
            "A stale resolution removed the unrelated occupant, retired the notice or changed a binding");
    }
    public static Task A22MoveOntoDifferentFileAsync() => throw new NotImplementedException();
    public static async Task A22CreateOntoIdenticalFileAsync()
    {
        var pair = ItemChecks.Pair(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        using var store = new InMemoryStateStore();
        var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        var source = ItemChecks.File(Side.Local, Original, 1, "source");
        var competitor = ItemChecks.File(Side.Dropbox, Original, 1, "competitor");
        pc.Seed(source, [1]); cloud.AfterStage = () => cloud.Seed(competitor, [1]);
        await ItemSync.ApplyAsync(pair, ItemChecks.Settings, _ => { }, paths,
            new(Side.Local, ChangeKind.Changed, Original, null, source.Id, source, true, false), pc, cloud, store,
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default);
        Check.Require(cloud.Items[Original].Entry == competitor && cloud.Items[Original].Bytes.SequenceEqual(new byte[] { 1 }) &&
            cloud.History.Count == 0 && store.OpenNotices.Count == 0 && store.Records.Values.Single() == SyncedEntries.From(source, competitor),
            "Concurrent equal-content create did not retain and bind the exact competing occupant");
    }
    public static Task A22CreateOntoDifferentFileAsync() => throw new NotImplementedException();
    public static Task A22ObsoleteSourceRecoveryAsync() => throw new NotImplementedException();
    public static Task A24MovedInUnchangedCounterpartAsync() => CompareAsync(true,
        [new("held/item",1)],[new(Original,1)],[new("held/item",1)],[],0,[],exclusions:[new(Side.Local,"held",true)]);
    public static Task A24MovedInWithEditedCounterpartAsync() => throw new NotImplementedException();
    public static Task A24MovedOutWithOldRecordAsync() => throw new NotImplementedException();
    public static Task A24KeptCounterpartLeftAtExcludedOriginAsync() => throw new NotImplementedException();
    public static Task A26AutosyncIdenticalBytesAsync() => throw new NotImplementedException();
    public static Task A26AutosyncCounterpartEditedAsync() => throw new NotImplementedException();
    public static Task A26RepairSafeSaveAsync() => throw new NotImplementedException();
    public static Task A26RepairIdenticalBytesAsync() => throw new NotImplementedException();
    public static Task A26RepairCounterpartEditedAsync() => throw new NotImplementedException();
    // A capitals-only rename made on Dropbox propagates to the PC (PLAN A27).
    public static Task A27CloudOnlyCapitalRenameAsync() => CompareAsync(true,
        [new(Original,1)], [new("ITEM",1)],
        [new("ITEM",1)], [new("ITEM",1)], 1, []);
    // A case-only rename on Dropbox runs both physical hops in order, then settles the logical binding (mirrors
    // Executor/CaseRenameChecks).
    public static async Task A27BothPhysicalCaseHopsAsync()
    {
        var side = new FakeSide(Side.Dropbox);
        var folder = ItemChecks.File(Side.Dropbox, "old", 0, "folder", true);
        side.Seed(folder, []);
        var binding = SyncedEntries.From(folder with { Id = "pc" }, folder);
        var start = new Scope("OLD", folder.Path); var goal = new Scope("OLD", "OLD");
        var temporary = goal with { DropboxPath = PathNames.CaseRenamePath(goal.DropboxPath, ItemChecks.Settings.CaseRenameSuffix) };
        var hops = new List<(Scope Before, Scope After)>();
        var result = await new CaseRename().MoveAsync(side, folder, goal.DropboxPath, ItemChecks.Settings.CaseRenameSuffix, binding, start,
            (saved, before, after, _) =>
            {
                Check.Require(saved == binding && side.Items[after!.DropboxPath].Entry.Path == after!.DropboxPath,
                    "Handoff lost binding or ran before physical movement");
                hops.Add((before, after!)); return Task.CompletedTask;
            }, default);
        Check.Require(result.Final?.Id == folder.Id && hops.SequenceEqual(new[] { (start, temporary), (temporary, goal) }),
            "Both physical folder ranges must be compared in order");
    }
    // The second physical hop is refused: the confirmed temporary source is retained and the goal is never reached.
    public static async Task A27UnavailableRemainingHopAsync()
    {
        var side = new FakeSide(Side.Dropbox);
        var folder = ItemChecks.File(Side.Dropbox, "old", 0, "folder", true);
        side.Seed(folder, []);
        var binding = SyncedEntries.From(folder with { Id = "pc" }, folder);
        var start = new Scope("OLD", folder.Path); var goal = new Scope("OLD", "OLD");
        var temporary = goal with { DropboxPath = PathNames.CaseRenamePath(goal.DropboxPath, ItemChecks.Settings.CaseRenameSuffix) };
        var blocked = await new CaseRename().MoveAsync(side, folder, goal.DropboxPath, ItemChecks.Settings.CaseRenameSuffix, binding, start, (_, _, _, _) =>
        {
            side.MoveOutcomes.Enqueue(new(RemovalOutcome.NoEffect, null, TextKey.FailChanged)); return Task.CompletedTask;
        }, default);
        Check.Require(blocked.Outcome == RemovalOutcome.NoEffect && blocked.Final?.Id == folder.Id && blocked.Final.Path == temporary.DropboxPath,
            "Second leg refusal lost the confirmed temporary source");
        Check.Require(side.Items.Count == 1 && side.Items[temporary.DropboxPath].Entry == blocked.Final &&
            !side.Items.ContainsKey(goal.DropboxPath) && !side.Items.ContainsKey(folder.Path),
            "Receipt did not match the actual physical temporary entry");
    }
    // A case-only rename is one logical binding move over two physical hops. Driving it through Effects (the executor
    // that persists bindings) proves the store never commits the temporary spelling and the surviving binding uses the
    // requested spelling (PLAN A27).
    public static async Task A27NoTemporaryBindingAsync()
    {
        var pair = ItemChecks.Pair();
        var pc = new FakeSide(Side.Local); var side = new FakeSide(Side.Dropbox);
        var pcFolder = ItemChecks.File(Side.Local, "item", 0, "pc-folder", true);
        var folder = ItemChecks.File(Side.Dropbox, "item", 0, "folder", true);
        var pcChild = ItemChecks.File(Side.Local, "item/child", 1, "pc-child");
        var dbChild = ItemChecks.File(Side.Dropbox, "item/child", 1, "db-child");
        pc.Seed(pcFolder, []); side.Seed(folder, []); pc.Seed(pcChild, [1]); side.Seed(dbChild, [1]);
        using var store = new InMemoryStateStore();
        var saved = SyncedEntries.From(pcFolder, folder);
        var savedChild = SyncedEntries.From(pcChild, dbChild);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        await store.CommitAsync(pair.PairId, null, savedChild, null, null, default);
        var commits = new List<SyncedEntry?>();
        var projections = new List<global::Massdex.SyncShark.Engine.Scope>();
        var recording = Executor.FolderDeleteChecks.Observe.Wrap<IStore>(store, (op, args) =>
        {
            if (op == nameof(IStore.CommitAsync)) commits.Add((SyncedEntry?)args[2]);
            if (op == nameof(IStore.ProjectSubtreeAsync)) projections.Add((Scope)args[2]!);
            return null;
        });
        var row = new Row(saved.LocalKey, pcFolder, folder, saved, [], null, RowContext.Neutral);
        var outcome = await new Effects().ApplyAsync(pair, ItemChecks.Settings, _ => { }, new PathScope(pair, [pair], DriveType.Fixed),
            new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.MoveDropbox, row, Target: "ITEM"),
            pc, side, recording, (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default);
        var temporary = PathNames.CaseRenamePath("ITEM", ItemChecks.Settings.CaseRenameSuffix);
        Check.Require(outcome == EffectOutcome.Applied, "A bound case-only rename did not apply");
        Check.Require(side.Items.TryGetValue("ITEM", out var landed) && landed.Entry.IsFolder && side.Items.TryGetValue("ITEM/child", out var movedChild) && movedChild.Bytes.SequenceEqual(new byte[]{1}),
            "The physical folder or its bound descendant did not follow the requested spelling");
        var final = store.Records.Values.Single(r => r.LocalPath == "item" && r.DropboxPath == "ITEM");
        Check.Require(final.DropboxId == folder.Id, "The persisted root binding did not use the requested spelling and identity");
        var finalChild = store.Records.Values.Single(r => r.LocalPath == "item/child");
        Check.Require(finalChild == (savedChild with { DropboxPath = "ITEM/child", DropboxKey = PathNames.DropboxKey("ITEM/child") }), "The persisted descendant binding was not projected onto the requested spelling");
        Check.Require(commits.All(c => c is null || !Temporary(c.LocalPath) && !Temporary(c.DropboxPath)), "A temporary spelling was persisted as a binding");
        Check.Require(commits.Any(c => c is { } done && done.DropboxPath == "ITEM"), "No store binding write named the final spelling");
        Check.Require(projections.All(p => !Temporary(p.LocalPath) && !Temporary(p.DropboxPath)),
            "A subtree projection used the temporary case-rename spelling");
        Check.Require(projections.Any(p => p.DropboxPath == "ITEM"), "No subtree projection named the requested final spelling");
        bool Temporary(string path) => PathNames.IsSelfOrUnder(path, temporary);
    }
    // A28 keep: a keep exclusion applies with no content change on either side; both copies and records remain.
    public static Task A28ExcludeKeepPcAsync() => ExcludeKeepAsync(Side.Local);
    public static Task A28ExcludeKeepCloudAsync() => ExcludeKeepAsync(Side.Dropbox);
    private static async Task ExcludeKeepAsync(Side side)
    {
        var exclusion = new Exclusion(side, Original, Keep: true);
        var pair = ItemChecks.Pair();
        pair = pair with { Roots = new(pair.LocalRoot, pair.DropboxRoot, pair.ConnectionId, new(1, 2, null), new("account", "root", "home", "folder")) };
        using var store = new InMemoryStateStore(); await store.SavePairAsync(pair, default);
        var pc = new FakeSide(Side.Local) { Root = (pair.Roots!.Local, null, null, DriveType.Fixed) };
        var db = new FakeSide(Side.Dropbox) { Root = (null, pair.Roots.Dropbox, null, null) };
        var a = ItemChecks.File(Side.Local, Original, 1, "pc-file"); var b = ItemChecks.File(Side.Dropbox, Original, 1, "db-file");
        pc.Seed(a, [1]); db.Seed(b, [1]); var saved = SyncedEntries.From(a, b);
        await store.CommitAsync(pair.PairId, null, saved, null, null, default);
        Func<PathScope>? live = null;
        var host = new Host((_, _) => Task.FromResult<IStore?>(store), (_, _, current, _) => { live = current; return (pc, db); },
            ItemChecks.Settings, new global::Massdex.SyncShark.Engine.Tests.Run.HostChecks.MemoryLogs(), TimeProvider.System, _ => Task.FromResult(""), (_, _) => Task.CompletedTask);
        try
        {
            var outcome = await host.SetExclusionAsync(pair.PairId, exclusion, false, true, default);
            var edited = (await store.PairAsync(pair.PairId, default))!;
            Check.Require(outcome.Outcome == ExclusionOutcome.Applied && edited.Exclusions.SequenceEqual(new[] { exclusion }) &&
                pc.Items[Original].Entry == a && db.Items[Original].Entry == b && pc.Items[Original].Bytes.SequenceEqual(new byte[] { 1 }) &&
                db.Items[Original].Bytes.SequenceEqual(new byte[] { 1 }) && store.Records.Values.Single() == saved, "Keep did not publish the rule and preserve both linked versions");
            var incoming = ItemChecks.File(side, Original, 2, side == Side.Local ? a.Id : b.Id);
            var endpoint = side == Side.Local ? pc : db; var other = side == Side.Local ? db : pc;
            endpoint.Items[Original] = (incoming, [2]);
            var paths = live!();
            await ItemSync.ApplyAsync(edited, ItemChecks.Settings, _ => { }, paths, new(side, ChangeKind.Changed, Original, null, incoming.Id, incoming, true, false),
                pc, db, store, ItemChecks.Population(edited,paths,pc,db,store), (_, _, _, _) => Task.CompletedTask, default);
            Check.Require(other.Items[Original].Bytes.SequenceEqual(new byte[] { 1 }) && store.Records.Values.Single() == saved, "Keep allowed an excluded change to sync");
        }
        finally { await host.StopAsync(default); }
    }
    public static Task A28RecreateInKeepScopeAsync() => throw new NotImplementedException();
    public static async Task A28PreviewWithoutConfirmationAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();await store.SavePairAsync(pair,default);var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        db.Seed(ItemChecks.File(Side.Dropbox,"victim",1,"db"),[1]);var host=new Host((_,_)=>Task.FromResult<IStore?>(store),(_,_,_,_)=>(pc,db),ItemChecks.Settings,null!,TimeProvider.System,_=>throw new InvalidOperationException(),(_,_)=>throw new InvalidOperationException());
        var savedRules=(await store.PairAsync(pair.PairId,default))!.Exclusions.ToArray();
        var original=db.Items["victim"];var originalBytes=original.Bytes.ToArray();
        using var output=new StringWriter();using var error=new StringWriter();
        var code=await new global::Massdex.SyncShark.Engine.Edge.Commands().ExecuteAsync(["pairs","exclude","--pair",pair.PairId,"--side","dropbox","--rel","victim"],host,output,error,default);
        var expected=Text.Get(TextKey.ResultExcludePreview,(TextArg.Path,"victim"),(TextArg.Side,"Dropbox"),
            (TextArg.CopyFiles,"1"),(TextArg.CopyBytes,"1"),(TextArg.RemoveFiles,"1"),(TextArg.RemoveBytes,"1"),
            (TextArg.Place,Text.Get(TextKey.RecoveryDropboxHistory)));
        var saved=await store.PairAsync(pair.PairId,default);
        Check.Require(code==0 && error.ToString().Length==0 && output.ToString().TrimEnd()==expected &&
            saved!=null && saved.Exclusions.SequenceEqual(savedRules) && pc.Items.Count==0 && db.Items.Count==1 &&
            db.Items.TryGetValue("victim",out var after) && after.Entry==original.Entry && after.Bytes.SequenceEqual(originalBytes) &&
            pc.Requests[nameof(ISide.DeleteAsync)]==0 && db.Requests[nameof(ISide.DeleteAsync)]==0,
            "Destructive preview did not explain recovery or removed the target");
    }
    public static Task A28ConfirmedRemovalPcAsync() => throw new NotImplementedException();
    public static Task A28ConfirmedRemovalCloudAsync() => throw new NotImplementedException();
    public static Task A28OnlyKeptSideEditedAsync() => throw new NotImplementedException();
    public static Task A28BothEditedKeepBothOnKeptSideAsync() => throw new NotImplementedException();
    public static Task A28NewOnRemovedSideAsync() => throw new NotImplementedException();
    public static Task A28ChangedOnRemovedSideAsync() => throw new NotImplementedException();
    public static Task A28SkippedChildAsync() => throw new NotImplementedException();
    public static Task A28OtherExclusionAsync() => throw new NotImplementedException();
    public static Task A28NestedRootAsync() => throw new NotImplementedException();
    public static Task A28CopyFailureAsync() => throw new NotImplementedException();
    public static Task A28FileFolderSwapAsync() => throw new NotImplementedException();
    public static Task A28RecreatedIdenticalAsync() => throw new NotImplementedException();
    public static Task A28RecreatedDifferentAsync() => throw new NotImplementedException();
    public static Task A28ListExclusionsAsync() => throw new NotImplementedException();
    public static Task A28IncludeQuietScopeAsync() => throw new NotImplementedException();
    public static Task A28CrashAfterRuleBeforeCopyAsync() => throw new NotImplementedException();
    public static Task A28CrashAfterRemovalBeforeSettlementAsync() => throw new NotImplementedException();
    public static Task A28CrashAfterIncludeAsync() => throw new NotImplementedException();
    public static Task A28SameScopeReissuedBeforeClearAsync() => throw new NotImplementedException();
    public static Task A28TwoDistinctOwedScopesAsync() => throw new NotImplementedException();
    public static Task A28PartialCompareKeepsDebtAsync() => throw new NotImplementedException();
    public static Task A29DotTildePrefixAsync() => CompareAsync(false,
        [new("~$draft",1)],[],[new("~$draft",1)],[],0,[],ignores:["FileNamePrefix:~$"]);
    public static Task A29FileExtensionAsync() => CompareAsync(false,
        [new("report.tmp",1)],[],[new("report.tmp",1)],[],0,[],ignores:["FileExtension:.tmp"]);
    public static Task A29FileNameAsync() => CompareAsync(false,
        [new("secret",1)],[],[new("secret",1)],[],0,[],ignores:["FileName:secret"]);
    public static Task A29FolderNameAsync() => CompareAsync(false,
        [new("cache",0,true,"L-c"),new("cache/x",1,Id:"L-x")],[],[new("cache",0,true),new("cache/x",1)],[],0,[],ignores:["FolderName:cache"]);
    public static Task A29UnignoreQuietScopeAsync() => throw new NotImplementedException();
    public static Task A29OtherMatchingRuleRemainsAsync() => throw new NotImplementedException();
    public static Task A29IgnoredChildTravelsWithFolderAsync() => throw new NotImplementedException();
    public static Task A32ValidRenameClosesNoticeAsync() => throw new NotImplementedException();
    // A33 (ordering pinned by Run/WorkQueueChecks.RunAsync): a departure must run before the arrival that reuses its
    // path, and an earlier prerequisite before both. Each case holds a change behind a gate and requires the exact
    // receipt order, so a missing departure cannot pass as "nothing before the arrival" (myIndex test).
    private static (WorkQueue Queue, PairConfig Pair, Change Arrival, Change Prerequisite, Change Departure) A33()
    {
        var pair = ItemChecks.Pair();
        var version = ItemChecks.File(Side.Dropbox, "remote/item", 1, "child");
        var first = new Change(Side.Dropbox, ChangeKind.Changed, "remote/item", null, version.Id, version, false, false);
        return (new WorkQueue(), pair,
            first with { Kind = ChangeKind.Renamed, OldPath = "a", Path = "b" },
            first with { Path = "b/child" },
            first with { Kind = ChangeKind.Renamed, OldPath = "b", Path = "c" });
    }
    private static bool Taken(List<string> applied, string path) { lock (applied) return applied.Contains(path); }
    public static Task A33DepartureBeforeArrivalAsync() => A33TwoMoveAsync("The departure did not run before the arrival that reuses its path");
    public static Task A33NoReciprocalWaitAsync() => A33TwoMoveAsync("Departure and arrival waited on each other instead of settling");
    // Both named two-move registrations share this single gated check, so they cannot drift apart; the earlier-prerequisite
    // variant stays separate below.
    private static Task A33TwoMoveAsync(string verdict) =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        {
            var (queue, pair, arrival, _, departure) = A33();
            var applied = new List<string>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = queue.AdmitAsync(pair, new([arrival, departure], "moved", null),
                async (change, ct) => { if (change.Path == "c") await gate.Task.WaitAsync(ct); lock (applied) applied.Add(change.Path!); }, null, deadline);
            Check.Require(!page.IsCompleted && !Taken(applied, "b"), "The arrival ran while the departure it reuses a path from was unsettled (A33)");
            gate.SetResult();
            await page;
            Check.Require(applied.SequenceEqual(new[] { "c", "b" }), verdict + " (A33)");
        });
    public static Task A33EarlierPrerequisiteBeforeDepartureAsync() =>
        global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        {
            var (queue, pair, arrival, prerequisite, departure) = A33();
            var applied = new List<string>();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var page = queue.AdmitAsync(pair, new([arrival, prerequisite, departure], "moved", null),
                async (change, ct) => { if (change.Path == "b/child") await gate.Task.WaitAsync(ct); lock (applied) applied.Add(change.Path!); }, null, deadline);
            Check.Require(!page.IsCompleted && !Taken(applied, "c") && !Taken(applied, "b"), "The prerequisite did not hold its departure and arrival (A33)");
            gate.SetResult();
            await page;
            Check.Require(applied.SequenceEqual(new[] { "b/child", "c", "b" }), "An earlier prerequisite did not precede its departure and arrival (A33)");
        });
    public static Task A34SameSizeSameSecondAsync()
    {
        var t=DateTimeOffset.UnixEpoch.AddSeconds(100);
        return CompareAsync(false,[new(Original,2,Modified:t,Size:1)],[new(Original,3,Modified:t,Size:1)],
            [new(Original,2)],[new(Original,3)],1,[]);
    }
    public static Task A34DifferentSizeSameSecondAsync()
    {
        var t=DateTimeOffset.UnixEpoch.AddSeconds(100);
        return CompareAsync(false,[new(Original,2,Modified:t,Size:1)],[new(Original,3,Modified:t,Size:2)],
            [new(Original,2),new(Label(Original,Side.Dropbox,t),3)],
            [new(Original,3),new(Label(Original,Side.Local,t),2)],2,[TextKey.InfoKeptBoth]);
    }
    public static Task A34UntrustedTimeAsync()
    {
        var now=DateTimeOffset.UnixEpoch.AddSeconds(100); var future=DateTimeOffset.UnixEpoch.AddDays(36500);
        return CompareAsync(false,[new(Original,2,Modified:future,Size:1)],[new(Original,3,Modified:now,Size:1)],
            [new(Original,2),new(Label(Original,Side.Dropbox,now),3)],
            [new(Original,3),new(Label(Original,Side.Local,future),2)],2,[TextKey.InfoKeptBoth]);
    }
    // A file against a folder under newest-wins is kept both (PLAN A34, BC3): the PC keeps its file, Dropbox its
    // folder, and each side gets the other version at a labelled copy.
    public static Task A34FileFolderNewestAsync() => CompareAsync(false,
        [new(Original,2)], [new(Original,4,true)],
        [new(Original,2), new(Label(Original,Side.Dropbox,At(4)),0,true)],
        [new(Original,0,true), new(Label(Original,Side.Local,At(2)),2)],
        2, [TextKey.InfoKeptBoth]);
    public static Task A34FileFolderPcWinsAsync() => CompareAsync(false,
        [new(Original,2)],[new(Original,0,true)],[new(Original,2)],[new(Original,2)],1,[],FirstRunPolicy.LocalWins, admit: true);
    public static Task A34FileFolderDropboxWinsAsync() => CompareAsync(false,
        [new(Original,2)],[new(Original,0,true)],[new(Original,0,true)],[new(Original,0,true)],1,[],FirstRunPolicy.DropboxWins, admit: true);
    private static string Hash(byte v){using var h=new ContentHash();h.Append([v]);return h.Finish();}
    // A PC-only file on first run with a Dropbox deleted entry that cannot confirm equal-content-later-delete:
    // it is uploaded with one edit-beats-delete notice, never recycled.
    private static Task FirstRunCannotRecycleAsync(DeletionEvidence evidence,(TimeSpan Offset,TimeSpan Uncertainty)? clock)
    {
        var pair=ItemChecks.Pair();
        return global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        await WithStoreAsync(pair,async store =>
        {
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var local=new FakeSide(Side.Local); var remote=new FakeSide(Side.Dropbox);
            var queue=await ItemChecks.StartQueueAsync(pair,paths,local,remote,store);
            const string path="item";
            var entry=ItemChecks.File(Side.Local,path,1,"pc"); local.Seed(entry,[1]);
            remote.DeletedHistory[path]=evidence; remote.ClockOffset=clock;
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,remote,store,null,null,deadline);
            Check.Require(local.Items.TryGetValue(path,out var pcv) && pcv.Bytes.SequenceEqual(new byte[]{1}),"PC file lost on unconfirmed evidence");
            Check.Require(remote.Items.TryGetValue(path,out var dbv) && dbv.Bytes.SequenceEqual(new byte[]{1}),"File was not uploaded");
            Check.Require(local.Requests[nameof(ISide.DeleteAsync)]==0 && remote.Requests[nameof(ISide.DeleteAsync)]==0,"Unconfirmed evidence recycled a file");
            var open=new List<Notice>(); await foreach(var n in store.NoticesAsync(pair.PairId,default)) if(!n.Dismissed) open.Add(n);
            Check.Require(open.Count==1 && open.Single().Cause==TextKey.InfoEditBeatsDelete,"Missing edit-beats-delete notice");
            var records=new List<SyncedEntry>(); await foreach(var r in store.DescendantsAsync(pair.PairId,Side.Local,"",default)) records.Add(r);
            Check.Require(records.Count==1,"Upload not linked");
        }));
    }
    public static Task A35DifferentDeletedHashAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",DateTimeOffset.UnixEpoch.AddDays(2),null,Hash(9),null),(TimeSpan.Zero,TimeSpan.Zero));
    public static Task A35EarlierDeleteAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",DateTimeOffset.UnixEpoch.AddSeconds(-100),null,Hash(1),null),(TimeSpan.Zero,TimeSpan.Zero));
    public static Task A35MissingTimeAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",null,TextKey.DeletionTimeMissing,Hash(1),null),(TimeSpan.Zero,TimeSpan.Zero));
    public static Task A35MalformedTimeAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",null,TextKey.DeletionTimeMalformed,Hash(1),null),(TimeSpan.Zero,TimeSpan.Zero));
    public static Task A35MissingHashAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",DateTimeOffset.UnixEpoch.AddDays(2),null,null,TextKey.DeletionHashMissing),(TimeSpan.Zero,TimeSpan.Zero));
    public static Task A35MalformedHashAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",DateTimeOffset.UnixEpoch.AddDays(2),null,null,TextKey.DeletionHashMalformed),(TimeSpan.Zero,TimeSpan.Zero));
    public static Task A35InsideOffsetUncertaintyAsync() => FirstRunCannotRecycleAsync(
        new DeletionEvidence.DeletedFile("item","dropbox","deleted","r1",DateTimeOffset.UnixEpoch.AddDays(2),null,Hash(1),null),(TimeSpan.Zero,TimeSpan.FromDays(5)));
    public static Task A35LiveAtTargetAsync() => CompareAsync(false,[new(Original,2)],[new(Original,2)],[new(Original,2)],[new(Original,2)],1,[]);
    public static Task A35LiveElsewhereAsync() => throw new NotImplementedException();
    public static Task A35RetryMatchingHistoricalDeleteAsync() => throw new NotImplementedException();
    public static Task A35RetryChangedHistoricalEvidenceAsync() => throw new NotImplementedException();
    // L47 / A50: two pairs with an identical local root both map the same PC folder. Neither `pairs add`
    // (Host.AddPairAsync:404 has no overlap refusal) nor PathScope isolates equal roots (PathNames
    // .RelativeUnderLocalRoot:318-325 needs a strict sub-path), so both pairs co-own the folder. This asserts
    // the sole-version safety invariant (A50 "protect both mapped endpoints"): a version removed by one
    // pair is never left nowhere - it stays live or is retained in the recycle bin - and the pair does not
    // overwrite its endpoint for an unchanged counterpart.
    public static Task A50IdenticalRootsPreserveSoleCopyAsync()
    {
        var p1 = ItemChecks.Pair() with { PairId = "p1", LocalRoot = @"T:\A", DropboxRoot = "/X" };
        var p2 = ItemChecks.Pair() with { PairId = "p2", LocalRoot = @"T:\A", DropboxRoot = "/Y" };
        return global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        await WithStoreAsync(p1, async store =>
        {
            await store.FinishRepairAsync(p1.PairId,(0,await store.OweRepairAsync(p1.PairId,Side.Dropbox,"requested",default)),"{}",default);
            var local = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
            var pc = ItemChecks.File(Side.Local, @"T:\A\doc", 1, "pc-doc");
            var db = ItemChecks.File(Side.Dropbox, "/X/doc", 1, "db-doc");
            local.Seed(pc, [1]);
            await store.CommitAsync(p1.PairId, null, SyncedEntries.From(pc, db), null, null, default);
            var paths = new PathScope(p1, [p1, p2], DriveType.Fixed);
            var queue = await ItemChecks.StartQueueAsync(p1, paths, local, cloud, store);
            await Repair.RunAsync(p1, ItemChecks.Settings, _=>{}, paths, queue, local, cloud, store, null, null, deadline);
            var live = local.Items.TryGetValue(pc.Path, out var kept) && kept.Bytes.SequenceEqual(new byte[]{1});
            var binned = local.History.Any(h => h.Entry.Path == pc.Path && h.Bytes.SequenceEqual(new byte[]{1}));
            Check.Require(live || binned, "A version shared by two identical-root pairs ended up nowhere");
            Check.Require(cloud.Requests[nameof(ISide.PublishAsync)]==0 && cloud.Requests[nameof(ISide.MoveAsync)]==0,
                "The pair overwrote an endpoint for an unchanged counterpart");
        }));
    }
}
