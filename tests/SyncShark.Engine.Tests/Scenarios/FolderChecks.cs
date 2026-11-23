using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Models a deletion on one side with an independent surviving, changed, hidden or unreadable child on the other.</summary>
public static class FolderChecks
{
    internal static Func<(JournalPosition? Local,string? Dropbox),bool> RemovalCoverage(FakeSide pc,FakeSide cloud,Side victimSide,Entry victim,
        ChangePage local,ChangePage remote,CancellationToken ct,out Func<bool> verified,Entry? child=null)
    {
        var before=(local.ProjectedThrough,remote.Cursor);
        var target=victimSide==Side.Local?pc:cloud;
        var echo=new Change(victimSide,ChangeKind.Deleted,victim.Path,null,victim.Id,null,false,victimSide==Side.Local);
        var at=local.Position!;
        var journal=victimSide==Side.Local?ItemChecks.Journal([echo with {Usn=at.NextUsn,JournalId=at.JournalId}],at.NextUsn+1):local;
        var feed=victimSide==Side.Dropbox?new ChangePage([echo],"after-removal",null):remote;
        bool admitted=false,removed=false,proved=false;
        verified=()=>proved;
        target.AfterDelete=entry=>
        {
            Check.Require(admitted,"Selected removal ran without pre-send coverage");
            Check.Require(entry==victim,"Unexpected removal");
            removed=true;
            ItemChecks.Script(target,victimSide==Side.Local?journal:feed);
        };
        return fence=>
        {
            if(!removed) return admitted=fence==before;
            if(fence!=(journal.Position,feed.Cursor) || !target.History.Any(v=>v.Entry==victim)) return false;
            if(proved) return true;
            if(child!=null)
            {
                var parentReceipt=pc.ReadByIdAsync(victim.Id,ct).GetAwaiter().GetResult();
                var childReceipt=pc.ReadByIdAsync(child.Id,ct).GetAwaiter().GetResult();
                if(parentReceipt is not {InScope:false} a || a.Location.Entry!=victim || childReceipt is not {InScope:false} b || b.Location.Entry!=child) return false;
            }
            var stop=(fence.Dropbox,fence.Local);
            var localRead=pc.ReadChangesAsync(null,local.Position,stop,ct).GetAwaiter().GetResult();
            var cloudRead=cloud.ReadChangesAsync(remote.Cursor,null,stop,ct).GetAwaiter().GetResult();
            return proved=!localRead.ContinuityLost && !cloudRead.ContinuityLost && localRead.CaughtUp && cloudRead.CaughtUp &&
                localRead.Position==fence.Local && cloudRead.Cursor==fence.Dropbox &&
                (victimSide!=Side.Local || localRead.ProjectedThrough==fence.Local) &&
                localRead.Changes.SequenceEqual(victimSide==Side.Local?journal.Changes:[]) && cloudRead.Changes.SequenceEqual(victimSide==Side.Dropbox?feed.Changes:[]);
        };
    }

    public static async Task DeleteAsync(bool excluded=false, bool ignored=false, bool unreadable=false, bool edited=false, bool nested=false)
    {
        const string root="folder"; const string child="folder/child";
        var pair=ItemChecks.Pair() with { IgnoreRules=ignored?["FileName:child"]:[], Exclusions=excluded?[new(Side.Local,child,true)]:[] };
        var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        var inner=pair with { PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"folder","child"),DropboxRoot=pair.DropboxRoot+"/folder/child" };
        if(nested) await store.SavePairAsync(inner,default);
        var paths=new PathScope(pair,nested?[pair,inner]:[pair],DriveType.Fixed);var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        var a=ItemChecks.File(Side.Local,root,0,"parent-a",true);var b=ItemChecks.File(Side.Dropbox,root,0,"parent-b",true);
        var parent=SyncedEntries.From(a,b);await store.CommitAsync(pair.PairId,null,parent,null,null,default); pc.Seed(a,[]);
        var childA=ItemChecks.File(Side.Local,child,1,"child-a",nested);var childB=ItemChecks.File(Side.Dropbox,child,1,"child-b",nested);
        if(!ignored && !excluded && !nested) await store.CommitAsync(pair.PairId,null,SyncedEntries.From(childA,childB),null,null,default);
        var live=edited?ItemChecks.File(Side.Local,child,3,childA.Id):childA;pc.Seed(live,nested?[]:new[]{edited?(byte)3:(byte)1});
        if(excluded || nested)
        {
            var sibling=ItemChecks.File(Side.Local,"folder/free",2,"free-a");pc.Seed(sibling,[2]);
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(sibling,ItemChecks.File(Side.Dropbox,"folder/free",2,"free-b")),null,null,default);
        }
        if(unreadable) pc.ReadFailures[child]=new ItemFailedException(child,TextKey.ReadLocalFailed,null);
        if(edited)
        {
            // The parent owns the footprint before the local edit is read; recovery must consume the held child.
            var edit = new Change(Side.Local,ChangeKind.Changed,child,null,live.Id,live,true,false,10,JournalId:1);
            var gone = new Change(Side.Dropbox,ChangeKind.Deleted,root,null,b.Id,null,false,false);
            var deletion=await ItemChecks.ReadPrefixAsync(cloud,new([gone],"removed",null),default);
            Task? held = null;
            await queue.AdmitAsync(pair,deletion,async (_,ct)=>
            {
                var editedPage=await ItemChecks.ReadPrefixAsync(pc,ItemChecks.Journal([edit],11),ct);
                held = queue.AdmitAsync(pair,editedPage,(_,_)=>throw new InvalidOperationException("Recovered edit ran independently"),pc.SettleJournalAsync,ct);
                var coverage=RemovalCoverage(pc,cloud,Side.Local,a,editedPage,deletion,ct,out var verified,live);
                await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,Side.Dropbox,pc,cloud,store,queue.Held,ItemChecks.Population(pair,paths,pc,cloud,store),ItemChecks.Hop(pair,paths,queue,pc,cloud,store),ct,coversThrough:coverage);
                Check.Require(verified(),"Missing selected parent/child receipts or bounded recycle echo");
            },null,default);
            Check.Require(held!=null,"Parent never admitted the held child");
            await held!;
        }
        else
        {
            Task<RemovalResult> Apply(Func<(JournalPosition? Local,string? Dropbox),bool>? coverage,CancellationToken ct) =>
                new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,Side.Dropbox,pc,cloud,store,queue.Held,ItemChecks.Population(pair,paths,pc,cloud,store),ItemChecks.Hop(pair,paths,queue,pc,cloud,store),ct,coversThrough:coverage);
            if(excluded || nested || ignored)
            {
                var local=await ItemChecks.ReadPrefixAsync(pc,ItemChecks.Journal([],1),default);
                var gone=new Change(Side.Dropbox,ChangeKind.Deleted,root,null,b.Id,null,false,false);
                var remote=await ItemChecks.ReadPrefixAsync(cloud,new([gone],"before",null),default);
                await queue.AdmitAsync(pair,local,(_,_)=>throw new InvalidOperationException("Unexpected local input"),pc.SettleJournalAsync,default);
                var victim=ignored?a:pc.Items["folder/free"].Entry;
                var coverage=RemovalCoverage(pc,cloud,Side.Local,victim,local,remote,default,out var verified);
                await queue.AdmitAsync(pair,remote,async (_,ct)=>
                {
                    var result=await Apply(coverage,ct);
                    if(ignored) Check.Require(result.Outcome==RemovalOutcome.Removed,"Ignored folder removal was not settled");
                },null,default);
                Check.Require(verified(),"Missing bounded removal echo");
            }
            else await Apply(null,default);
        }
        if(ignored)
        {
            Check.Require(pc.Items.Count==0 && cloud.Items.Count==0 && pc.History.Any(x=>x.Entry.Path==child && x.Bytes.SequenceEqual(new byte[]{1})),"Ignored child did not accompany recycled parent");
            Check.Require(store.Records.Count==0 && store.OpenNotices.Count==0,"Spurious kept-folder notice or binding");
        }
        else if(edited)
        {
            Check.Require(pc.Items[child].Bytes.SequenceEqual(new byte[]{3}) && cloud.Items[child].Bytes.SequenceEqual(new byte[]{3}),"Latest attributable edit lost");
            Check.Require(store.Records.Values.Any(row=>row.LocalPath==child && row.DropboxPath==child) && store.OpenNotices.Count(n=>n.Cause==TextKey.InfoEditBeatsDelete)==1,"Lost linkage or edit notice");
        }
        else
        {
            Check.Require(pc.Items.Count==2 && pc.Items.ContainsKey(root) && pc.Items[child].Entry==live,"Protected survivor or folder root was removed");
            Check.Require(cloud.Items.Count==0 && pc.History.All(v=>v.Entry.Path=="folder/free"),"Removal receipt wrong: cloud not empty or an unrelated local entry was recycled");
            if(excluded || nested) Check.Require(pc.History.Single().Bytes.SequenceEqual(new byte[]{2}) && store.Records.Values.All(v=>v.LocalPath!="folder/free"),"Eligible sibling not recycled and retired");
            if(unreadable) Check.Require(store.OpenNotices.Count(n=>n.Kind==NoticeKind.Failure)==1 && store.Records.Count==2,"Unreadable source incorrectly settled");
            else Check.Require(store.OpenNotices.All(n=>n.Kind!=NoticeKind.Failure),"Ordinary protected survivor became a failure");
        }
    }
    public static async Task LinksAsync()
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var cloud=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        var paths=new PathScope(pair,[pair],DriveType.Fixed);var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,cloud,store);
        foreach(var path in new[]{"top-a/link","top-b/link"}) pc.Seed(ItemChecks.File(Side.Local,path,0,path,true) with { Skip=nameof(TextKey.SkipLink) },[]);
        await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,default);
        var notice=store.OpenNotices.Single();Check.Require(notice.Kind==NoticeKind.Info && notice.Cause==TextKey.InfoLinks && notice.Subject.Items.Count==2,"Link summary must contain both members");
        Check.Require(pc.Items.Count==2 && cloud.Items.Count==0 && store.Records.Count==0,"Link followed or copied");
        pc.Items.Clear();await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,pc,cloud,store,null,null,default);Check.Require(store.OpenNotices.Count==0 && cloud.Items.Count==0 && store.Records.Count==0,"Stale link membership");
    }
    /// <summary>A detached read creates no image; the first explicit add publishes the pair.</summary>
    public static Task FreshStartAsync() => FirstAddAsync(false);

    private static async Task FirstAddAsync(bool interrupt)
    {
        var scratch=Directory.CreateTempSubdirectory().FullName;
        var folder=Path.Combine(scratch,ResourceNames.EngineFolder);var path=Path.Combine(folder,ResourceNames.DatabaseFile);
        var stage=path+".20000101T000000Z.new";var boundary=new IOException("Fixture: closed stage, before publication");
        int creates=0;byte[]? abandoned=null;Host host=null!;
        try
        {
            var pair=ItemChecks.Pair();var local=new FakeSide(Side.Local){Root=(new(1,1,null),null,null,DriveType.Fixed)};var cloud=new FakeSide(Side.Dropbox){Root=(null,new("account","bound-namespace","home","root"),null,null)};
            async Task<IStore?> Open(bool create,CancellationToken ct)
            {
                if(!File.Exists(path) && !create) return null;
                if(create && !File.Exists(path))
                {
                    creates++;
                    if(interrupt && creates==1)
                    {
                        // Stop the first host at the create factory, after writing a closed stage but before the rename.
                        Directory.CreateDirectory(folder);
                        using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder {DataSource=stage,Pooling=false}.ToString()))
                        {
                            await db.OpenAsync(ct);db.CreateFunction<string,string>("pathkey",PathNames.Key,isDeterministic:true);
                            using var cmd=db.CreateCommand();cmd.CommandText=global::Massdex.SyncShark.Engine.Storage.Schema.Create;
                            await cmd.ExecuteNonQueryAsync(ct);
                            cmd.CommandText=$"PRAGMA user_version={(global::Massdex.SyncShark.Engine.Storage.Schema.Version)}; INSERT INTO journal_position VALUES('orphan',1,2)";await cmd.ExecuteNonQueryAsync(ct);
                        }
                        abandoned=File.ReadAllBytes(stage);throw boundary;
                    }
                    await new global::Massdex.SyncShark.Engine.Storage.StoreMove().CreateAsync(scratch,ct);
                }
                return new global::Massdex.SyncShark.Engine.Storage.Store(path,false,ItemChecks.Settings);
            }
            Host Start()=>new(Open,(_,_,_,_)=>(local,cloud),ItemChecks.Settings,null!,TimeProvider.System,_=>throw new InvalidOperationException(),(_,_)=>throw new InvalidOperationException());
            host=Start();
            await foreach(var unused in host.PairsAsync(default)) throw new InvalidDataException("Fresh read reported a pair");
            Check.Require(creates==0 && !File.Exists(path),"Read created a database");
            if(interrupt)
            {
                try { await host.AddPairAsync(pair,default);throw new InvalidDataException("First add did not report interruption"); }
                catch(IOException ex) when(ReferenceEquals(ex,boundary)) { }
                Check.Require(creates==1 && abandoned is {Length:>0} && !File.Exists(path),"No observable closed-stage/pre-publication boundary");
                host=Start();await foreach(var unused in host.PairsAsync(default)) throw new InvalidDataException("Restart adopted an abandoned stage");
                Check.Require(creates==1 && !File.Exists(path) && File.ReadAllBytes(stage).SequenceEqual(abandoned!),"Restart published or disturbed the earlier attempt");
            }
            await host.AddPairAsync(pair,default);
            Check.Require(creates==(interrupt?2:1) && File.Exists(path),"Explicit add did not publish exactly one fresh store");
            using var reopened=new global::Massdex.SyncShark.Engine.Storage.Store(path,true,ItemChecks.Settings);
            Check.Require((await reopened.PairAsync(pair.PairId,default))?.PairId==pair.PairId,"Published image lacks the new pair");
            if(interrupt) Check.Require(await reopened.PositionAsync("orphan",default)==null && File.ReadAllBytes(stage).SequenceEqual(abandoned!),"Retry reused or removed an earlier attempt’s stage");
        }
        finally { if(host is not null) await host.StopAsync(default);Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();Directory.Delete(scratch,true); }
    }

    private sealed record PairRun(PairConfig Pair, PathScope Paths, FakeSide Pc, FakeSide Db, InMemoryStateStore Store, WorkQueue Queue);

    private static async Task<PairRun> StartAsync(PairConfig? pair = null, bool baseline = false, IReadOnlyList<PairConfig>? allPairs = null, IReadOnlyList<PairConfig>? extraPairs = null)
    {
        pair ??= ItemChecks.Pair();
        var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        var store = new InMemoryStateStore();
        await store.SavePairAsync(pair, default);
        if (extraPairs != null) foreach (var extra in extraPairs) await store.SavePairAsync(extra, default);
        if (baseline) await store.FinishRepairAsync(pair.PairId, (0, await store.OweRepairAsync(pair.PairId, Side.Dropbox, "requested", default)), "{}", default);
        IReadOnlyList<PairConfig> pairList = allPairs ?? [pair, ..(extraPairs ?? [])];
        var paths = new PathScope(pair, pairList, DriveType.Fixed);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, db, store);
        return new(pair, paths, pc, db, store, queue);
    }

    private static Task RepairAsync(PairRun run, EngineSettings? settings = null) =>
        Repair.RunAsync(run.Pair, settings ?? ItemChecks.Settings, _ => { }, run.Paths, run.Queue, run.Pc, run.Db, run.Store, null, null, default);

    private static Entry Seed(FakeSide side, string path, byte value = 0, bool folder = false, string? skip = null)
    {
        var entry = ItemChecks.File(side.Side, path, value, $"{side.Side}:{path}", folder);
        if (skip != null) entry = entry with { Skip = skip };
        side.Items[entry.Path] = (entry, folder ? [] : new[] { value });
        return entry;
    }

    private static bool Is(FakeSide side, string path, bool folder, params byte[] bytes) =>
        side.Items.TryGetValue(path, out var at) && at.Entry.IsFolder == folder && at.Bytes.SequenceEqual(bytes);

    private static string Label(string path, Side source, DateTimeOffset modified, int ordinal) =>
        global::Massdex.SyncShark.Engine.Planner.ConflictName.For(path, source, modified, ItemChecks.Settings.ComputerName, ItemChecks.Settings, ordinal);

    /// <summary>PC file against a cloud folder: keep both, the folder side copies the file and the file side copies the folder with its child.</summary>
    public static async Task A04FileOnPcFolderOnCloudAsync()
    {
        var run = await StartAsync();
        var pcFile = Seed(run.Pc, "item", 1);
        var dbFolder = Seed(run.Db, "item", 0, folder: true);
        var dbChild = Seed(run.Db, "item/child", 2);
        await RepairAsync(run);
        var toCloud = Label("item", Side.Local, pcFile.Modified, 1);
        var toPc = Label("item", Side.Dropbox, dbFolder.Modified, 1);
        Check.Require(Is(run.Pc, "item", false, 1) && Is(run.Db, "item", true), "An original endpoint changed during keep-both");
        Check.Require(Is(run.Db, toCloud, false, 1) && Is(run.Pc, toPc, true) && Is(run.Pc, toPc + "/child", false, 2), "A labelled copy of the other kind is missing or changed");
        Check.Require(run.Store.Records.Count == 3, "Keep-both did not settle the two links and the child binding");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "item" && r.DropboxPath == toCloud) &&
            run.Store.Records.Values.Any(r => r.LocalPath == toPc && r.DropboxPath == "item") &&
            run.Store.Records.Values.Any(r => r.LocalPath == toPc + "/child" && r.DropboxPath == "item/child"), "Wrong links after keep-both");
        var notice = run.Store.OpenNotices.Single();
        var members = new HashSet<NoticeItem> {
            new(Side.Local, "item", pcFile.Id), new(Side.Dropbox, "item", dbFolder.Id),
            new(Side.Dropbox, toCloud, run.Db.Items[toCloud].Entry.Id), new(Side.Local, toPc, run.Pc.Items[toPc].Entry.Id) };
        Check.Require(notice.Kind == NoticeKind.Info && notice.Cause == TextKey.InfoKeptBoth && members.SetEquals(notice.Subject.Items), "Kept-both notice did not name the actual endpoints");
    }

    /// <summary>PC folder against a cloud file: the folder side copies the file and the file side copies the whole folder.</summary>
    public static async Task A04FolderOnPcFileOnCloudAsync()
    {
        var run = await StartAsync();
        var pcFolder = Seed(run.Pc, "item", 0, folder: true);
        var pcChild = Seed(run.Pc, "item/child", 3);
        var dbFile = Seed(run.Db, "item", 4);
        await RepairAsync(run);
        var toCloud = Label("item", Side.Local, pcFolder.Modified, 1);
        var toPc = Label("item", Side.Dropbox, dbFile.Modified, 1);
        Check.Require(Is(run.Pc, "item", true) && Is(run.Pc, "item/child", false, 3) && Is(run.Db, "item", false, 4), "An original endpoint changed during keep-both");
        Check.Require(Is(run.Pc, toPc, false, 4) && Is(run.Db, toCloud, true) && Is(run.Db, toCloud + "/child", false, 3), "A labelled copy of the other kind is missing or changed");
        Check.Require(run.Store.Records.Count == 3, "Keep-both did not settle the two links and the child binding");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "item" && r.DropboxPath == toCloud) &&
            run.Store.Records.Values.Any(r => r.LocalPath == toPc && r.DropboxPath == "item") &&
            run.Store.Records.Values.Any(r => r.LocalPath == "item/child" && r.DropboxPath == toCloud + "/child"), "Wrong links after keep-both");
        var notice = run.Store.OpenNotices.Single();
        var members = new HashSet<NoticeItem> {
            new(Side.Local, "item", pcFolder.Id), new(Side.Dropbox, "item", dbFile.Id),
            new(Side.Local, toPc, run.Pc.Items[toPc].Entry.Id), new(Side.Dropbox, toCloud, run.Db.Items[toCloud].Entry.Id) };
        Check.Require(notice.Kind == NoticeKind.Info && notice.Cause == TextKey.InfoKeptBoth && members.SetEquals(notice.Subject.Items), "Kept-both notice did not name the actual endpoints");
    }

    /// <summary>The folder copy's child is bound once, at the fresh label, with no record left at the abandoned spelling.</summary>
    public static async Task A04ChildBoundAtAbandonedLabelAsync()
    {
        var run = await StartAsync();
        Seed(run.Pc, "item", 1);
        var dbFolder = Seed(run.Db, "item", 0, folder: true);
        Seed(run.Db, "item/child", 2);
        await RepairAsync(run);
        var toPc = Label("item", Side.Dropbox, dbFolder.Modified, 1);
        var childRecords = run.Store.Records.Values.Where(r => r.DropboxPath == "item/child").ToArray();
        Check.Require(childRecords.Length == 1 && childRecords[0].LocalPath == toPc + "/child", "The child was not rebound to exactly one fresh label");
        Check.Require(run.Store.Records.Values.Count(r => PathNames.IsUnderKey(r.LocalKey, PathNames.Key(toPc))) == 1, "A second child record survived beside the fresh label");
        Check.Require(Is(run.Pc, toPc + "/child", false, 2), "The fresh label did not hold the copied child");
    }

    /// <summary>After the completed leg, the child's sole record names the fresh label on the receiving side.</summary>
    public static async Task A04FreshLabelCompletionRebindsChildAsync()
    {
        var run = await StartAsync();
        var pcFolder = Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/child", 3);
        Seed(run.Db, "item", 4);
        await RepairAsync(run);
        var toCloud = Label("item", Side.Local, pcFolder.Modified, 1);
        var childRecords = run.Store.Records.Values.Where(r => r.LocalPath == "item/child").ToArray();
        Check.Require(childRecords.Length == 1 && childRecords[0].DropboxPath == toCloud + "/child", "The child's sole record does not point to the fresh label");
        Check.Require(Is(run.Db, toCloud + "/child", false, 3), "The fresh label did not hold the copied child");
    }
    /// <summary>A cloud file replaced by a folder propagates the folder to the PC, whose file is displaced.</summary>
    public static async Task A08FileToFolderOnCloudAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 1);
        var oldDb = Seed(run.Db, "item", 1);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        var dbFolder = Seed(run.Db, "item", 0, folder: true) with { Id = "cloud-folder" };
        run.Db.Items["item"] = (dbFolder, []);
        Seed(run.Db, "item/child", 2);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "item", true) && Is(run.Pc, "item/child", false, 2), "PC did not adopt the replacement folder with its child");
        Check.Require(Is(run.Db, "item", true), "The cloud replacement folder changed");
        Check.Require(run.Pc.History.Any(v => v.Entry.Id == oldPc.Id && v.Bytes.SequenceEqual(new byte[] { 1 })), "The displaced PC file was not kept recoverable");
        Check.Require(run.Store.Records.Count == 2 && run.Store.Records.Values.Any(r => r.LocalPath == "item" && r.DropboxPath == "item" && r.IsFolder) &&
            run.Store.Records.Values.Any(r => r.LocalPath == "item/child" && r.DropboxPath == "item/child"), "Wrong links for the replacement folder");
        Check.Require(run.Store.OpenNotices.Count == 0, "A plain replacement raised a notice");
    }

    /// <summary>A PC folder replaced by a file propagates the file to Dropbox, whose folder and children go to history.</summary>
    public static async Task A08FolderToFileOnPcAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        var dbChild = Seed(run.Db, "item/child", 2);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        var pcFile = Seed(run.Pc, "item", 3) with { Id = "pc-file" };
        run.Pc.Items["item"] = (pcFile, [3]);
        await RepairAsync(run);
        Check.Require(Is(run.Db, "item", false, 3) && Is(run.Pc, "item", false, 3), "The replacement file did not reach both sides");
        Check.Require(run.Db.History.Any(v => v.Entry.Id == oldDb.Id) && run.Db.History.Any(v => v.Entry.Id == dbChild.Id), "The replaced cloud folder or its child was not kept recoverable");
        Check.Require(run.Store.Records.Count == 1 && run.Store.Records.Values.Single().LocalPath == "item" && run.Store.Records.Values.Single().DropboxPath == "item", "Wrong link after the folder-to-file replacement");
        Check.Require(run.Store.OpenNotices.Count == 0, "A plain replacement raised a notice");
    }

    /// <summary>A cloud folder replaced by a file propagates the file to the PC, whose folder and children go to the bin.</summary>
    public static async Task A08FolderToFileOnCloudAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var pcChild = Seed(run.Pc, "item/child", 2);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        var dbFile = Seed(run.Db, "item", 3) with { Id = "cloud-file" };
        run.Db.Items["item"] = (dbFile, [3]);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "item", false, 3) && Is(run.Db, "item", false, 3), "The replacement file did not reach both sides");
        Check.Require(run.Pc.History.Any(v => v.Entry.Id == oldPc.Id) && run.Pc.History.Any(v => v.Entry.Id == pcChild.Id), "The replaced PC folder or its child was not kept recoverable");
        Check.Require(run.Store.Records.Count == 1 && run.Store.Records.Values.Single().LocalPath == "item", "Wrong link after the folder-to-file replacement");
        Check.Require(run.Store.OpenNotices.Count == 0, "A plain replacement raised a notice");
    }

    /// <summary>The replaced folder's children are displaced with their folder, not removed independently on the other side.</summary>
    public static async Task A08ChildDeletesBeforeParentReplacementAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        var c1 = Seed(run.Pc, "item/c1", 1);
        var c2 = Seed(run.Pc, "item/c2", 2);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(c1, Seed(run.Db, "item/c1", 1)), null, null, default);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(c2, Seed(run.Db, "item/c2", 2)), null, null, default);
        run.Db.Items.Remove("item");
        run.Db.Items.Remove("item/c1");
        run.Db.Items.Remove("item/c2");
        run.Db.Seed(ItemChecks.File(Side.Dropbox, "item", 3, "cloud-file"), [3]);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "item", false, 3) && Is(run.Db, "item", false, 3), "The parent replacement did not settle on both sides");
        Check.Require(run.Pc.History.Any(v => v.Entry.Id == oldPc.Id) && run.Pc.History.Any(v => v.Entry.Id == c1.Id) && run.Pc.History.Any(v => v.Entry.Id == c2.Id),
            "The children were not displaced together with their folder");
        Check.Require(run.Store.Records.Count == 1, "A child binding survived the parent replacement");
        Check.Require(run.Store.OpenNotices.Count == 0, "The children's deletes raised an independent notice");
    }

    /// <summary>A stage failure before removal leaves the target folder and its contents exactly as they were.</summary>
    public static async Task A08StageFailureBeforeRemovalAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var pcChild = Seed(run.Pc, "item/child", 2);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        run.Db.Items["item"] = (ItemChecks.File(Side.Dropbox, "item", 3, "cloud-file"), [3]);
        run.Pc.AfterStage = () => throw new ItemFailedException("item", TextKey.StagePending, null);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "item", true) && Is(run.Pc, "item/child", false, 2), "The target folder was altered by a failed stage");
        Check.Require(run.Pc.History.Count == 0, "A failed stage removed the target folder");
        Check.Require(run.Store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "A failed stage raised no failure notice");
        Check.Require(run.Store.Records.Values.Single().IsFolder, "A failed stage changed the saved binding");
    }
    public static async Task A08SoleSmallUploadSourceUnreadableAsync()
    {
        var pair=ItemChecks.Pair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new InMemoryStateStore();
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var file=ItemChecks.File(Side.Local,"item",2,"pc");var folder=ItemChecks.File(Side.Dropbox,"item",0,"db",true);
        var queue=await ItemChecks.StartQueueAsync(pair,paths,pc,db,store);
        pc.Seed(file,[2]);db.Seed(folder,[]);pc.ContentFailures[file.Path]=new ItemFailedException(file.Path,TextKey.ReadLocalFailed,null);
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,file.Path,null,file.Id,file,true,false),pc,db,store,ItemChecks.Population(pair,paths,pc,db,store),ItemChecks.Hop(pair,paths,queue,pc,db,store),default); }
        catch (ItemFailedException e) when (e.Cause == TextKey.ReadLocalFailed) { }
        Check.Require(pc.Requests[nameof(ISide.OpenReadAsync)]+pc.Requests[nameof(ISide.ContentHashAsync)]>0 && db.Items[folder.Path].Entry==folder && db.Requests[nameof(ISide.DeleteAsync)]==0 && store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure),"Failed content read removed the target or went unreported");
    }
    /// <summary>A target folder holding protected or known-unreadable content is not replaced; a failure notice names it.</summary>
    private static async Task ReplacementRetainedAsync(Side folderSide, Func<PairConfig, PairConfig> configure, string? childSkip)
    {
        var pair = configure(ItemChecks.Pair());
        var run = await StartAsync(pair: pair, baseline: true);
        var folderAdapter = folderSide == Side.Local ? run.Pc : run.Db;
        var otherAdapter = folderSide == Side.Local ? run.Db : run.Pc;
        var folderEntry = Seed(folderAdapter, "item", 0, folder: true);
        var child = childSkip == null ? Seed(folderAdapter, "item/child", 1) : Seed(folderAdapter, "item/child", 1, skip: childSkip);
        var otherEntry = Seed(otherAdapter, "item", 0, folder: true);
        var localEntry = folderSide == Side.Local ? folderEntry : otherEntry;
        var dropboxEntry = folderSide == Side.Local ? otherEntry : folderEntry;
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(localEntry, dropboxEntry), null, null, default);
        // The other side becomes a file, flipping the kind against the folder record.
        otherAdapter.Items["item"] = (ItemChecks.File(otherAdapter.Side, "item", 9, "incoming-file"), [9]);
        await RepairAsync(run);
        Check.Require(folderAdapter.Items["item"].Entry.IsFolder && folderAdapter.Items.ContainsKey("item/child"), "The protected target folder or its child was replaced");
        Check.Require(folderAdapter.History.Count == 0, "Protected descendants were removed");
        Check.Require(run.Store.Records.Values.Single().IsFolder, "The folder binding was replaced");
        Check.Require(run.Store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "The retained replacement raised no failure notice");
    }

    public static Task A08ProtectedExclusionPcAsync() => ReplacementRetainedAsync(Side.Local,
        p => p with { Exclusions = [new(Side.Local, "item/child", true)] }, null);
    public static Task A08ProtectedExclusionCloudAsync() => ReplacementRetainedAsync(Side.Dropbox,
        p => p with { Exclusions = [new(Side.Dropbox, "item/child", true)] }, null);
    public static Task A08ProtectedLinkPcAsync() => ReplacementRetainedAsync(Side.Local, p => p, nameof(TextKey.SkipLink));
    public static Task A08ProtectedLinkCloudAsync() => ReplacementRetainedAsync(Side.Dropbox, p => p, nameof(TextKey.SkipLink));
    public static Task A08UnreadablePcChildAsync() => ReplacementRetainedAsync(Side.Local, p => p, nameof(TextKey.SkipUnreadable));
    public static Task A08CloudReplacementKnownUnreadableAsync() => ReplacementRetainedAsync(Side.Dropbox, p => p, nameof(TextKey.SkipUnreadable));

    /// <summary>A refused finish after the folder was removed is an ordinary failure with the folder in history.</summary>
    public static async Task A08FinishRefusedAfterFolderRemovalAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        run.Pc.Items["item"] = (ItemChecks.File(Side.Local, "item", 3, "pc-file"), [3]);
        run.Db.PublishFailures["item"] = new ItemFailedException("item", TextKey.PublishFailed, null);
        await RepairAsync(run);
        Check.Require(run.Db.History.Any(v => v.Entry.Id == oldDb.Id), "The removed cloud folder did not reach history");
        Check.Require(!run.Db.Items.ContainsKey("item"), "A refused finish still published the file");
        Check.Require(run.Pc.Items.TryGetValue("item", out var kept) && kept.Bytes.SequenceEqual(new byte[] { 3 }), "The pending incoming file left its own side");
        Check.Require(run.Store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "A refused finish raised no failure notice");
    }

    /// <summary>The retry publishes create-only at the path the refused finish had vacated.</summary>
    public static async Task A08RetryCreateOnlyAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        run.Pc.Items["item"] = (ItemChecks.File(Side.Local, "item", 3, "pc-file"), [3]);
        run.Db.Items.Remove("item");
        await RepairAsync(run);
        Check.Require(Is(run.Db, "item", false, 3) && Is(run.Pc, "item", false, 3), "The retry did not publish create-only at the vacated path");
        Check.Require(run.Store.Records.Count == 1 && !run.Store.Records.Values.Single().IsFolder, "The retry left a stale folder binding");
        Check.Require(run.Store.OpenNotices.Count(n => n.Kind == NoticeKind.Failure) == 0, "A successful create-only retry left a failure notice");
    }

    /// <summary>A recovered competing folder takes keep-both rather than the stale staged finish.</summary>
    public static async Task A08CompetingRecoveredFolderAsync()
    {
        var run = await StartAsync(baseline: true);
        var oldPc = Seed(run.Pc, "item", 0, folder: true);
        var oldDb = Seed(run.Db, "item", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(oldPc, oldDb), null, null, default);
        run.Pc.Items["item"] = (ItemChecks.File(Side.Local, "item", 3, "pc-file"), [3]);
        run.Db.AfterDelete = entry => { if (entry.Path == "item") run.Db.Seed(ItemChecks.File(Side.Dropbox, "item", 0, "recovered-folder", folder: true), []); };
        await RepairAsync(run);
        Check.Require(run.Db.Items.TryGetValue("item", out var dbAt) && dbAt.Entry.IsFolder, "The stale staged finish displaced the recovered folder");
        Check.Require(run.Pc.Items.TryGetValue("item", out var pcAt) && !pcAt.Entry.IsFolder && pcAt.Bytes.SequenceEqual(new byte[] { 3 }), "The PC file was overwritten instead of kept");
        Check.Require(run.Store.OpenNotices.Any(n => n.Kind == NoticeKind.Info && n.Cause == TextKey.InfoKeptBoth), "The competing folder did not take keep-both");
    }
    public static Task A14NestedRootSurvivorAsync() => DeleteAsync(nested: true);
    public static Task A14EditedLooseFileBesideExclusionAsync() => DeleteAsync(excluded: true, edited: true);
    public static Task A14FailedChildKeepsCustodyAsync() => DeleteAsync(unreadable: true);
    public static Task A14OverlappingEarlierEditBeforeDeleteAsync() => DeleteAsync(edited: true);
    public static Task A14MoveOutBeforeDeleteJournalAsync() => ReconciliationChecks.CompareAsync(true,
        [new("held/item", 1)], [new("item", 1)], [new("held/item", 1)], [new("held/item", 1)], 1, [], expectMove: true);
    public static Task A14MoveOutFirstSeenByRepairAsync() => ReconciliationChecks.CompareAsync(true,
        [new("held/item", 1)], [new("item", 1)], [new("held/item", 1)], [new("held/item", 1)], 1, [], expectMove: true);

    /// <summary>A peer child whose deleting-side path is a link is a survivor; the link-bearing folder is retained.</summary>
    public static async Task A14LinkOnDeletingSideAsync()
    {
        var pair = ItemChecks.Pair();
        var run = await StartAsync(pair: pair, baseline: true);
        var pcRoot = Seed(run.Pc, "p", 0, folder: true); var dbRoot = Seed(run.Db, "p", 0, folder: true);
        var saved = SyncedEntries.From(pcRoot, dbRoot);
        await run.Store.CommitAsync(pair.PairId, null, SyncedEntries.From(Seed(run.Pc, "p/child", 1), Seed(run.Db, "p/child", 1)), null, null, default);
        await run.Store.CommitAsync(pair.PairId, null, SyncedEntries.From(Seed(run.Pc, "p/link", 5), Seed(run.Db, "p/link", 0, skip: nameof(TextKey.SkipLink))), null, null, default);
        var result = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, run.Paths, dbRoot, saved, Side.Dropbox,
            ActionKind.DeleteDropbox, run.Pc, run.Db, run.Store, _ => [], ItemChecks.Population(pair, run.Paths, run.Pc, run.Db, run.Store),
            ItemChecks.Hop(pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), default, userSelected: true);
        Check.Require(run.Pc.Items.ContainsKey("p/link") && run.Pc.Items["p/link"].Bytes.SequenceEqual(new byte[] { 5 }),
            "The peer child held by a deleting-side link was removed");
        Check.Require(run.Db.Items.ContainsKey("p") && run.Db.Items.ContainsKey("p/link") && result.Outcome == RemovalOutcome.Retained,
            "A link-bearing folder was not retained with its necessary parent");
    }

    /// <summary>An oversized selected recursive delete is split into per-child deletes, not one recursive removal.</summary>
    public static async Task A14OversizeSelectedFolderSplitAsync()
    {
        var pair = ItemChecks.Pair();
        var run = await StartAsync(pair: pair, baseline: true);
        var pcRoot = Seed(run.Pc, "p", 0, folder: true); var dbRoot = Seed(run.Db, "p", 0, folder: true);
        var saved = SyncedEntries.From(pcRoot, dbRoot);
        Seed(run.Db, "p/a", 1); Seed(run.Db, "p/b", 2);
        run.Pc.Pages.Enqueue(new ChangePage([], null, new JournalPosition(1, 1)));
        run.Db.Pages.Enqueue(new ChangePage([], "c", null));
        var result = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, run.Paths, dbRoot, saved, Side.Dropbox,
            ActionKind.DeleteDropbox, run.Pc, run.Db, run.Store, _ => [], ItemChecks.Population(pair, run.Paths, run.Pc, run.Db, run.Store),
            ItemChecks.Hop(pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), default, splitSelected: true, coversThrough: _ => true);
        Check.Require(run.Db.Items.Count == 0 && result.Outcome == RemovalOutcome.Removed, "The selected oversized cloud folder was not split and removed");
        Check.Require(run.Db.Requests[nameof(ISide.DeleteAsync)] >= 3, "The oversized delete was not split into per-child deletes");
    }

    /// <summary>An earlier ancestor move is applied with its descendant before any unsent deletion.</summary>
    public static async Task A14EarlierAncestorMoveBeforeDeleteAsync()
    {
        var run = await StartAsync(baseline: true);
        var pcOld = Seed(run.Pc, "old", 0, folder: true); var dbOld = Seed(run.Db, "old", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pcOld, dbOld), null, null, default);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(Seed(run.Pc, "old/c", 1), Seed(run.Db, "old/c", 1)), null, null, default);
        await run.Pc.MoveAsync(pcOld, "new", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "new", true) && Is(run.Pc, "new/c", false, 1) && Is(run.Db, "new", true) && Is(run.Db, "new/c", false, 1),
            "The ancestor move did not survive with its child on both sides");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "new" && r.DropboxPath == "new") &&
            run.Store.Records.Values.Any(r => r.LocalPath == "new/c" && r.DropboxPath == "new/c"), "Bindings were not projected through the ancestor move");
    }
    /// <summary>A delete over a known-unreadable PC descendant waits with a failure notice; an independent folder still goes.</summary>
    public static async Task A16IndependentFolderContinuesAsync()
    {
        var pair = ItemChecks.Pair();
        var run = await StartAsync(pair: pair, baseline: true);
        var pcA = Seed(run.Pc, "a", 0, folder: true); var dbA = Seed(run.Db, "a", 0, folder: true);
        var savedA = SyncedEntries.From(pcA, dbA);
        await run.Store.CommitAsync(pair.PairId, null, savedA, null, null, default);
        Seed(run.Pc, "a/child", 1);
        run.Pc.ReadFailures["a/child"] = new ItemFailedException("a/child", TextKey.ReadLocalFailed, null);
        var pcB = Seed(run.Pc, "b", 0, folder: true); var dbB = Seed(run.Db, "b", 0, folder: true);
        var savedB = SyncedEntries.From(pcB, dbB);
        await run.Store.CommitAsync(pair.PairId, null, savedB, null, null, default);
        var retained = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, run.Paths, pcA, savedA, Side.Local,
            ActionKind.DeleteLocal, run.Pc, run.Db, run.Store, _ => [], ItemChecks.Population(pair, run.Paths, run.Pc, run.Db, run.Store),
            ItemChecks.Hop(pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), default, userSelected: true);
        Check.Require(retained.Outcome == RemovalOutcome.Retained && run.Pc.Items.ContainsKey("a") && run.Pc.Items.ContainsKey("a/child"),
            "The folder with a known-unreadable descendant was removed");
        Check.Require(run.Store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "The retained unreadable delete raised no failure notice");
        var removed = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, run.Paths, pcB, savedB, Side.Local,
            ActionKind.DeleteLocal, run.Pc, run.Db, run.Store, _ => [], ItemChecks.Population(pair, run.Paths, run.Pc, run.Db, run.Store),
            ItemChecks.Hop(pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), default, userSelected: true);
        Check.Require(removed.Outcome == RemovalOutcome.Removed && !run.Pc.Items.ContainsKey("b"), "An independent folder did not continue after the retained delete");
    }
    private static async Task<(Entry Folder, Entry Child)> FolderWithChildAsync(PairRun run, string path = "Old", byte value = 1)
    {
        var pf = Seed(run.Pc, path, 0, folder: true); var df = Seed(run.Db, path, 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pf, df), null, null, default);
        var pc = Seed(run.Pc, path + "/Child", value); var dc = Seed(run.Db, path + "/Child", value);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pc, dc), null, null, default);
        return (pf, pc);
    }

    /// <summary>A one-sided folder move with its child and exact spelling syncs the whole subtree.</summary>
    public static async Task A18OneSidedFolderMoveAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "New", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "New", true) && Is(run.Pc, "New/Child", false, 1) && Is(run.Db, "New", true) && Is(run.Db, "New/Child", false, 1),
            "The one-sided folder move did not sync with exact spelling and its child");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "New" && r.DropboxPath == "New") &&
            run.Store.Records.Values.Any(r => r.LocalPath == "New/Child" && r.DropboxPath == "New/Child"), "Bindings did not follow the move");
    }

    /// <summary>A child deleted on Dropbox while the PC moved its folder follows the child to its real endpoint.</summary>
    public static async Task A18TombstoneQueuedAtFirstHopAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "New", default);
        run.Db.Items.Remove("Old/Child");
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "New", true) && Is(run.Db, "New", true), "The folder move did not settle at the new spelling");
        Check.Require(!run.Pc.Items.ContainsKey("New/Child") && run.Store.Records.Values.All(r => r.LocalPath != "New/Child"), "The queued tombstone did not follow the child to its real endpoint");
    }

    /// <summary>A child whose tombstone is not yet read travels with the moved folder instead of being deleted.</summary>
    public static async Task A18TombstoneUnreadAtFirstHopAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "New", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "New/Child", false, 1) && Is(run.Db, "New/Child", false, 1), "The child did not follow the folder move to the new spelling");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "New/Child" && r.DropboxPath == "New/Child"), "The child binding did not follow the move");
    }

    /// <summary>A capitals-only folder rename with a queued child tombstone settles the exact spelling and the delete.</summary>
    public static async Task A18TombstoneQueuedAtCaseHopAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "OLD", default);
        run.Db.Items.Remove("Old/Child");
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "OLD", true) && Is(run.Db, "OLD", true), "The capitals rename did not settle the exact spelling on both sides");
        Check.Require(!run.Pc.Items.ContainsKey("OLD/Child") && run.Store.Records.Values.All(r => r.LocalPath != "OLD/Child"), "The queued tombstone did not follow the child through the capitals hop");
    }

    /// <summary>A capitals-only folder rename with an unread child keeps the child and the exact spelling.</summary>
    public static async Task A18TombstoneUnreadAtCaseHopAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "OLD", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "OLD/Child", false, 1) && Is(run.Db, "OLD/Child", false, 1), "The child did not follow the capitals rename");
    }

    /// <summary>A PC child edited while Dropbox deleted it takes the ordinary edit-beats-delete decision at the moved path.</summary>
    public static async Task A18PcChildAlsoEditedAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "New", default);
        var at = run.Pc.Items["New/Child"];
        run.Pc.Items["New/Child"] = (at.Entry with { Size = 2, Modified = at.Entry.Modified.AddSeconds(1) }, new byte[] { 7, 7 });
        run.Db.Items.Remove("Old/Child");
        await RepairAsync(run);
        Check.Require(run.Pc.Items.ContainsKey("New/Child") && run.Db.Items.ContainsKey("New/Child") &&
            run.Db.Items["New/Child"].Bytes.SequenceEqual(new byte[] { 7, 7 }), "The competing edit did not survive the one-sided delete");
        Check.Require(run.Store.OpenNotices.Any(n => n.Cause == TextKey.InfoEditBeatsDelete), "The competing edit raised no edit-beats-delete notice");
    }

    /// <summary>A stop after the parent move is recorded and before the child delete settles is resumed by the next Repair.</summary>
    public static async Task A18RestartAfterParentBindingBeforeChildSettlementAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "New", default);
        await RepairAsync(run);
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "New" && r.DropboxPath == "New"), "The parent move was not recorded before the restart");
        run.Db.Items.Remove("New/Child");
        await RepairAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("New/Child") && run.Store.Records.Values.All(r => r.LocalPath != "New/Child"), "The resumed Repair did not settle the held child delete");
    }

    /// <summary>A folder moved twice before the first cloud report settles only at the second destination.</summary>
    public static async Task A18SecondMoveBeforeFirstCloudReportAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "Mid", default);
        await run.Pc.MoveAsync(run.Pc.Items["Mid"].Entry, "New", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "New", true) && Is(run.Db, "New", true) && Is(run.Db, "New/Child", false, 1), "The second move did not settle at the final destination");
        Check.Require(!run.Db.Items.ContainsKey("Mid") && run.Store.Records.Values.All(r => r.LocalPath != "Mid"), "An intermediate move binding survived");
    }

    /// <summary>A hard-linked PC file under a moved folder moves with its in-folder name; the alias is untouched.</summary>
    public static async Task A18HardLinkedChildInPairAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, child) = await FolderWithChildAsync(run);
        run.Pc.Items["Other/x"] = (child with { Path = "Other/x" }, new byte[] { 1 });
        await run.Pc.MoveAsync(folder, "New", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "New/Child", false, 1) && Is(run.Db, "New/Child", false, 1), "The in-folder hard link did not move with its folder");
        Check.Require(run.Pc.Items.ContainsKey("Other/x"), "The alias under another in-pair path was consumed or removed by the move");
    }

    /// <summary>A hard-linked PC file whose alias is outside the pair neither consumes nor suppresses the inside report.</summary>
    public static async Task A18HardLinkedChildOutsidePairAsync()
    {
        var pair = ItemChecks.Pair() with { Exclusions = [new(Side.Local, "Other", true)] };
        var run = await StartAsync(pair: pair, baseline: true);
        var (folder, child) = await FolderWithChildAsync(run);
        run.Pc.Items["Other/x"] = (child with { Path = "Other/x" }, new byte[] { 1 });
        await run.Pc.MoveAsync(folder, "New", default);
        await RepairAsync(run);
        Check.Require(Is(run.Db, "New/Child", false, 1), "The out-of-pair alias suppressed the inside child report");
        Check.Require(run.Store.Records.Values.All(r => !PathNames.IsUnderKey(r.LocalKey, PathNames.Key("Other"))), "The out-of-pair alias was bound or followed");
        Check.Require(run.Pc.Items.ContainsKey("Other/x"), "The out-of-pair alias was touched by the move");
    }

    /// <summary>A one-sided delete follows the child to its real endpoint, never a new occupant at the old path.</summary>
    public static async Task A18OldNameReusedAsync()
    {
        var run = await StartAsync(baseline: true);
        var (folder, _) = await FolderWithChildAsync(run);
        await run.Pc.MoveAsync(folder, "New", default);
        run.Db.Items.Remove("Old/Child");
        run.Db.Seed(ItemChecks.File(Side.Dropbox, "Old/Child", 9, "new-occupant"), [9]);
        await RepairAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("New/Child"), "The tombstone did not follow the real child to the moved endpoint");
        Check.Require(run.Store.Records.Values.All(r => r.LocalPath != "New/Child"), "The deleted child's binding survived at the moved endpoint");
        Check.Require(run.Db.Items.TryGetValue("Old/Child", out var occupant) && occupant.Entry.Id == "new-occupant", "The new occupant at the old path was removed by the queued delete");
    }
    public static Task A23NestedClashingChildFoldersAsync() => throw new NotImplementedException();
    public static Task A23DifferentChildFilesKeepBothAsync() => throw new NotImplementedException();
    public static Task A23MoveOverRelocationCeilingAsync() => throw new NotImplementedException();
    public static Task A23NewChildBeforeLastListingAsync() => throw new NotImplementedException();
    public static Task A23NewChildAfterLastListingAsync() => throw new NotImplementedException();
    public static Task A23RemoveOnlyOriginalSourceAsync() => throw new NotImplementedException();
    public static Task A30JunctionAsync() => throw new NotImplementedException();
    public static Task A30SymbolicLinkAsync() => throw new NotImplementedException();
    public static Task A30LastMemberDisappearsAsync() => throw new NotImplementedException();
    public static Task A30MappedLinkProtectsRemoteMoveAsync() => throw new NotImplementedException();
    public static Task A36IgnoredFileOnlyAsync() => throw new NotImplementedException();
    public static Task A36IgnoredFolderWithContentAsync() => throw new NotImplementedException();
    public static Task A36OrdinaryFileInFolderAsync() => throw new NotImplementedException();
    public static Task A36ExcludedChildAsync() => throw new NotImplementedException();
    public static Task A36NestedRootChildAsync() => throw new NotImplementedException();
    public static Task A36LinkedChildAsync() => throw new NotImplementedException();
    public static Task A36NestedEmptyFoldersAsync() => throw new NotImplementedException();
    public static Task A36EmptyChildWithoutDeletionRecordAsync() => throw new NotImplementedException();
    public static Task A36InterleavedOrdinalSubtreesAsync() => throw new NotImplementedException();
    public static Task A46CrashStageWrittenAsync() => throw new NotImplementedException();
    public static Task A46CrashV16PutAsideAsync() => throw new NotImplementedException();
    public static Task A46CrashAfterSwitchAsync() => throw new NotImplementedException();
    public static Task A46SwitchFailedAsync() => throw new NotImplementedException();
    public static Task A46PointerRemovalFailedAsync() => throw new NotImplementedException();
    public static Task A46PreviousAttemptStageUntouchedAsync() => throw new NotImplementedException();
    public static Task A46PointerToFixedRootAsync() => throw new NotImplementedException();
    public static Task A46FixedPointerWithOnlyV16BackupAsync() => throw new NotImplementedException();
    public static Task A46TwoLinkNoticesFoldedAsync() => throw new NotImplementedException();
    public static Task A46MixedLinkAndNonlinkMembersAsync() => throw new NotImplementedException();
    public static Task A46UnchangedNoticesPreservedAsync() => throw new NotImplementedException();
    public static Task A46CrashDuringConnectionListCopyAsync() => throw new NotImplementedException();
    /// <summary>Restart after the first add stops before publication; neither restart nor retry may adopt its stage.</summary>
    public static Task A46CrashDuringFirstPairAddAsync() => FirstAddAsync(true);
    public static Task A46FreshReadCreatesNoStoreAsync() => FreshStartAsync();
    public static Task A46ClosedImageNoWalAsync() => throw new NotImplementedException();
    public static Task A46AmbiguousIndependentStoresAsync() => throw new NotImplementedException();
    public static Task A46InvalidSourceAsync() => throw new NotImplementedException();
    public static Task A46ExternalDanglingPointerAsync() => throw new NotImplementedException();
    public static Task A46BothListsAbsentNoRequiredAccountAsync() => throw new NotImplementedException();
    public static Task A46MissingRequiredConnectionAsync() => throw new NotImplementedException();
    public static Task A46DistinctConnectionListsAsync() => throw new NotImplementedException();
    public static Task A48SameNameRemovedTwiceAsync() => throw new NotImplementedException();
    public static Task A48TwoPairsOneVolumeAsync() => throw new NotImplementedException();
    public static Task A48DriveRootScopeAsync() => throw new NotImplementedException();
    public static Task A48RootBinJunctionRemoveRefusedAsync() => throw new NotImplementedException();
    public static Task A48RootBinJunctionTrimRefusedAsync() => throw new NotImplementedException();
    public static Task A48StartupTrimsOldTopLevelAsync() => throw new NotImplementedException();
    public static Task A48ExactRetentionBoundaryAsync() => throw new NotImplementedException();
    public static Task A48QuietJobBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48FailedPutBackBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48BinOnNativeBinVolumeNotTrimmedAsync() => throw new NotImplementedException();
    public static Task A48UnrelatedVolumeNotTrimmedAsync() => throw new NotImplementedException();
    public static Task A48LongDowntimeRecoverBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48UnavailableHistoryAsync() => throw new NotImplementedException();
    public static Task A48UnknownDriveUsesShellOrAbortAsync() => throw new NotImplementedException();
    public static Task A48StampBeforeNonreplacingMoveAsync() => throw new NotImplementedException();
    public static Task A50NewInnerRootEffectiveBeforeNextEntryAsync() => throw new NotImplementedException();
    public static Task A50ExclusionEffectiveBeforeNextEntryAsync() => throw new NotImplementedException();
    public static Task A50InnerPairRemovedQuietRangeAsync() => throw new NotImplementedException();
    public static Task A50SideAccountSpecificRootsAsync() => throw new NotImplementedException();
    public static Task A50MappedOtherEndpointProtectedAsync() => throw new NotImplementedException();
    public static Task A50WholeMoveProtectedAsync() => throw new NotImplementedException();
    public static Task A50PartialMoveProtectedAsync() => throw new NotImplementedException();
    public static Task A50ReplacementRetainsFolderAsync() => throw new NotImplementedException();
}
