using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Data.Sqlite;
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
        if(edited && !excluded && !nested && !ignored)
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
        else if(edited && !excluded && !nested && !ignored)
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
        IReadOnlyList<PairConfig> pairList;
        if (allPairs != null) pairList = allPairs;
        else { var list = new List<PairConfig> { pair }; if (extraPairs != null) list.AddRange(extraPairs); pairList = list; }
        var paths = new PathScope(pair, pairList, DriveType.Fixed);
        var queue = await ItemChecks.StartQueueAsync(pair, paths, pc, db, store);
        return new(pair, paths, pc, db, store, queue);
    }

    private static Task RepairAsync(PairRun run, EngineSettings? settings = null) =>
        Repair.RunAsync(run.Pair, settings ?? ItemChecks.Settings, _ => { }, run.Paths, run.Queue, run.Pc, run.Db, run.Store, null, null, default);

    /// <summary>Supply a full Repair the live proof interval a reader would: real boundary pages on both fakes plus an
    /// admitted fence, so its destructive legs may run instead of being withheld. ProofNow is internal and the test
    /// assembly has no InternalsVisibleTo, so the one seam is set here with real boundary facts, not fabricated ones.</summary>
    internal static void AdmitFullRepair(WorkQueue queue, FakeSide pc, FakeSide db)
    {
        var end = new JournalPosition(1, 42);
        ItemChecks.Script(pc, new ChangePage([], null, end) { ProjectedThrough = end });
        ItemChecks.Script(db, new ChangePage([], "complete", null));
        Func<(JournalPosition? Local, string? Dropbox), bool> fence = f => f.Local is { } at && at == end && f.Dropbox == "complete";
        Func<Func<(JournalPosition? Local, string? Dropbox), bool>?> proof = () => fence;
        var property = typeof(WorkQueue).GetProperty("ProofNow", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("WorkQueue.ProofNow seam missing");
        property.SetValue(queue, proof);
    }

    // Repair a full scan under an admitted-proven interval and a real cancellation token, so a withheld destructive
    // leg becomes a bounded result rather than a leaked follower wait.
    private static async Task RepairWithProofAsync(PairRun run, EngineSettings? settings = null)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        AdmitFullRepair(run.Queue, run.Pc, run.Db);
        await Repair.RunAsync(run.Pair, settings ?? ItemChecks.Settings, _ => { }, run.Paths, run.Queue, run.Pc, run.Db, run.Store, null, null, budget.Token);
    }

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
        await RepairWithProofAsync(run);
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
        await RepairWithProofAsync(run);
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
        await RepairWithProofAsync(run);
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
        await RepairWithProofAsync(run);
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
        if (childSkip == nameof(TextKey.SkipUnreadable))
        {
            // An unreadable inventory must surface the existing incomplete-inventory error, never finish as complete (Repair.cs:182).
            bool threw = false;
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            {
                try { await Repair.RunAsync(run.Pair, ItemChecks.Settings, _ => { }, run.Paths, run.Queue, run.Pc, run.Db, run.Store, null, null, budget.Token); }
                catch (InventoryIncompleteException) { threw = true; }
            }
            Check.Require(threw, "An unreadable inventory finished as complete");
        }
        else await RepairAsync(run);
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
        await RepairWithProofAsync(run);
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
        int recovered = 0;
        run.Db.AfterDelete = entry => { if (entry.Path == "item") { recovered++; run.Db.Seed(ItemChecks.File(Side.Dropbox, "item", 0, "recovered-folder", folder: true), []); } };
        await RepairWithProofAsync(run);
        Check.Require(recovered == 1, "The recovered-folder window was never reached");
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
        // A selected delete of the cloud folder is one-sided: the peer copy is already gone, so the
        // same-identity peer guard (FolderDelete.cs:92) does not retain before the split.
        run.Pc.Items.Remove("p");
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
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var ct = budget.Token;
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
            ItemChecks.Hop(pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), ct, userSelected: true);
        Check.Require(retained.Outcome == RemovalOutcome.Retained && run.Pc.Items.ContainsKey("a") && run.Pc.Items.ContainsKey("a/child"),
            "The folder with a known-unreadable descendant was removed");
        Check.Require(run.Store.OpenNotices.Any(n => n.Kind == NoticeKind.Failure), "The retained unreadable delete raised no failure notice");
        // The independent folder is removed bilaterally: its Dropbox peer is a real departure, so the scoped settlement
        // never treats a live identity as absent. Live-identity validation stays intact.
        run.Db.Items.Remove("b");
        var removed = await new FolderDelete().ApplyAsync(pair, ItemChecks.Settings, _ => { }, run.Paths, pcB, savedB, Side.Local,
            ActionKind.DeleteLocal, run.Pc, run.Db, run.Store, _ => [], ItemChecks.Population(pair, run.Paths, run.Pc, run.Db, run.Store),
            ItemChecks.Hop(pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), ct, userSelected: true);
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
        await RepairWithProofAsync(run);
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

    /// <summary>A capitals-only folder rename with a real child tombstone queued at the first case leg settles the
    /// exact casing, the delete at the projected endpoint and the temporary name without a mutation.</summary>
    public static Task A18TombstoneQueuedAtCaseHopAsync() => global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async ct =>
    {
        var run = await StartAsync(baseline: true);
        var (folder, child) = await FolderWithChildAsync(run);
        var dbChild = run.Db.Items["Old/Child"].Entry;
        await run.Pc.MoveAsync(folder, "OLD", ct);
        var renamed = run.Pc.Items["OLD"].Entry;
        // The provider reports a path-only delete for the old child name; the endpoint no longer holds it.
        run.Db.Items.Remove("Old/Child");
        var tombstone = new Change(Side.Dropbox, ChangeKind.Deleted, "Old/Child", null, dbChild.Id, null, false, false);
        var childPage = new ChangePage([tombstone], "tombstone", null);
        var paths = new PathScope(run.Pair, [run.Pair], DriveType.Fixed);
        // Real boundary facts for the tombstone's own removal receipt; no live proof is handed to the case hop.
        run.Pc.IdleBoundary = new ChangePage([], null, new(1, 42)) { ProjectedThrough = new(1, 42) };
        run.Db.IdleBoundary = new ChangePage([], "complete", null);
        var order = new List<string>();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? queued = null;
        async Task AdmitChild()
        {
            try
            {
                await run.Queue.AdmitAsync(run.Pair, childPage, async (change, token) =>
                {
                    order.Add("child");
                    await ItemSync.ApplyAsync(run.Pair, ItemChecks.Settings, _ => { }, paths, change, run.Pc, run.Db, run.Store,
                        ItemChecks.Population(run.Pair, paths, run.Pc, run.Db, run.Store), ItemChecks.Hop(run.Pair, paths, run.Queue, run.Pc, run.Db, run.Store),
                        token, run.Queue.Held, (_, _) => throw new WorkQueue.Unsettled(), coversThrough: _ => true);
                }, null, (cursor, token) => run.Store.SaveCursorAsync(run.Pair.PairId, cursor, token), null, ct);
                admitted.TrySetResult();
            }
            catch (Exception error) { admitted.TrySetException(error); }
        }
        // Admit the tombstone while the first case leg moves the folder; the queue keeps it behind the parent input.
        var firstLeg = true;
        run.Db.AfterMove = (_, _) =>
        {
            if (!firstLeg) return;
            firstLeg = false; queued = AdmitChild();
        };
        var parent = new Change(Side.Local, ChangeKind.Renamed, "OLD", "Old", folder.Id, renamed, true, false, 5, JournalId: 1);
        await run.Queue.AdmitAsync(run.Pair, ItemChecks.Journal([parent], 6), async (change, token) =>
        {
            order.Add("parent");
            await ItemSync.ApplyAsync(run.Pair, ItemChecks.Settings, _ => { }, paths, change, run.Pc, run.Db, run.Store,
                ItemChecks.Population(run.Pair, paths, run.Pc, run.Db, run.Store), ItemChecks.Hop(run.Pair, paths, run.Queue, run.Pc, run.Db, run.Store), token);
        }, run.Pc.SettleJournalAsync, ct);
        Check.Require(queued != null, "The first case leg never admitted the queued tombstone");
        await admitted.Task; await queued!;
        Check.Require(order.SequenceEqual(new[] { "parent", "child" }), "The queued tombstone ran before its parent case hop");
        Check.Require(await run.Store.CursorAsync(run.Pair.PairId, ct) == "tombstone", "The queued tombstone did not settle its cursor");
        Check.Require(run.Pc.Items.TryGetValue("OLD", out var pcFolder) && pcFolder.Entry.Path == "OLD" &&
            run.Db.Items.TryGetValue("OLD", out var dbFolder) && dbFolder.Entry.Path == "OLD", "The capitals rename did not settle the exact casing on both sides");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "OLD" && r.DropboxPath == "OLD"), "The parent binding did not follow the case hop");
        Check.Require(!run.Pc.Items.ContainsKey("OLD/Child") && run.Pc.History.Any(x => x.Entry.Id == child.Id), "The queued tombstone did not recycle the child at its real endpoint");
        Check.Require(run.Store.Records.Values.All(r => r.LocalPath != "OLD/Child" && r.DropboxPath != "OLD/Child"), "The deleted child binding survived the case hop");
        Check.Require(!run.Pc.Items.Keys.Concat(run.Db.Items.Keys).Any(p => p.Contains(ItemChecks.Settings.CaseRenameSuffix, StringComparison.Ordinal)), "A mutation landed at the case temporary name");
    });

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
        await RepairWithProofAsync(run);
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
        await RepairWithProofAsync(run);
        Check.Require(run.Db.Items.TryGetValue("New/Child", out var moved) && moved.Entry.Id == "new-occupant" && moved.Bytes.SequenceEqual(new byte[]{9}) && !run.Db.Items.ContainsKey("Old/Child"), "The occupant did not travel with the moved Dropbox folder");
        await ItemSync.ApplyAsync(run.Pair, ItemChecks.Settings, _ => { }, run.Paths, new Change(Side.Dropbox, ChangeKind.Changed, "New/Child", null, "new-occupant", moved.Entry, false, false), run.Pc, run.Db, run.Store,
            ItemChecks.Population(run.Pair, run.Paths, run.Pc, run.Db, run.Store), ItemChecks.Hop(run.Pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), default, run.Queue.Held, coversThrough: _ => true);
        Check.Require(run.Pc.Items.TryGetValue("New/Child", out var local) && local.Bytes.SequenceEqual(new byte[]{9}) && run.Store.Records.Values.Any(r => r.LocalPath == "New/Child" && r.DropboxId == "new-occupant"), "The moved occupant was not synced at its real endpoint");
    }
    private static Task<RemovalResult> MergeAsync(PairRun run, Entry source, Entry dest,
        Func<Entry, Entry, Entry?, SyncedEntry?, CancellationToken, Task<EffectOutcome>>? collide = null) =>
        new FolderMerge().ApplyAsync(run.Pair, ItemChecks.Settings, _ => { }, run.Paths, source, dest, Side.Local, run.Pc, run.Db, run.Store,
            _ => [], (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask, default, collide: collide,
            coversThrough: global::Massdex.SyncShark.Engine.Tests.Executor.FolderDeleteChecks.Admitted(run.Pc, run.Db));

    private static Task<(Entry Source, Entry Dest)> MergeFolderAsync(PairRun run, string source = "old", string dest = "new")
    {
        var src = Seed(run.Pc, source, 0, folder: true);
        var at = src with { Path = dest, Id = "dest" };
        run.Pc.Items[dest] = (at, []);
        return Task.FromResult((src, at));
    }

    /// <summary>A nested clashing child folder merges into the existing destination child without a notice.</summary>
    public static async Task A23NestedClashingChildFoldersAsync()
    {
        var run = await StartAsync(baseline: true);
        var source = Seed(run.Pc, "old", 0, folder: true);
        var dest = source with { Path = "new", Id = "dest" };
        run.Pc.Items["new"] = (dest, []);
        foreach (var path in new[] { "old/sub", "new/sub" })
        {
            var v = source with { Path = path, Id = path };
            run.Pc.Items[path] = (v, []);
            await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(v, v with { Id = "remote-" + path }), null, null, default);
        }
        var child = Seed(run.Pc, "old/free", 1); var peer = Seed(run.Db, "old/free", 1);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(child, peer), null, null, default);
        var result = await MergeAsync(run, source, dest);
        Check.Require(result.Outcome == RemovalOutcome.Removed && !run.Pc.Items.ContainsKey("old") && !run.Pc.Items.ContainsKey("old/sub"), "The source was not removed after the nested merge");
        Check.Require(run.Pc.Items.ContainsKey("new/sub") && run.Pc.Items.ContainsKey("new/free") && run.Pc.Items["new/free"].Entry.Id == child.Id, "The nested child folders did not merge or the free child did not move");
        Check.Require(run.Store.OpenNotices.Count == 0, "A plain folder merge raised a notice");
    }

    /// <summary>A clashing child file of different content is kept both; neither copy is lost.</summary>
    public static async Task A23DifferentChildFilesKeepBothAsync()
    {
        var run = await StartAsync(baseline: true);
        var (source, dest) = await MergeFolderAsync(run);
        var selected = Seed(run.Pc, "old/f", 1); var incumbent = Seed(run.Pc, "new/f", 2);
        int collisions = 0;
        var result = await MergeAsync(run, source, dest, (sel, inc, peer, saved, ct) =>
        {
            collisions++;
            Check.Require(sel.Id == selected.Id && inc.Id == incumbent.Id, "Wrong collision subjects for the clashing child file");
            return Task.FromResult(EffectOutcome.Refused);
        });
        Check.Require(collisions == 1, "The different child files were not sent to the collision owner");
        Check.Require(Is(run.Pc, "new/f", false, 2) && Is(run.Pc, "old/f", false, 1), "A clashing child file was lost instead of kept");
        Check.Require(run.Pc.Items.ContainsKey("new"), "The existing destination folder was removed");
    }

    public static Task A23MoveOverRelocationCeilingAsync() => throw new NotImplementedException();

    /// <summary>A child the last one-level listing finds keeps the source as merge work and lands at the destination.</summary>
    public static async Task A23NewChildBeforeLastListingAsync()
    {
        var run = await StartAsync(baseline: true);
        var (source, dest) = await MergeFolderAsync(run);
        var extra = Seed(run.Pc, "old/extra", 3);
        var result = await MergeAsync(run, source, dest);
        Check.Require(run.Pc.Items.ContainsKey("new/extra") && run.Pc.Items["new/extra"].Bytes.SequenceEqual(new byte[] { 3 }), "A newly found child was not carried into the destination");
        Check.Require(!run.Pc.Items.ContainsKey("old") && !run.Pc.Items.ContainsKey("old/extra"), "The source survived after its last listing was empty of work");
    }

    /// <summary>A child landing after the listing is recovered rather than lost with the source cleanup.</summary>
    public static async Task A23NewChildAfterLastListingAsync()
    {
        var run = await StartAsync(baseline: true);
        var (source, dest) = await MergeFolderAsync(run);
        Seed(run.Pc, "old/late", 4);
        var result = await MergeAsync(run, source, dest);
        var recovered = run.Pc.Items.ContainsKey("new/late") || run.Pc.Items.ContainsKey("old/late");
        Check.Require(recovered && run.Pc.Items.Values.Any(v => v.Bytes.SequenceEqual(new byte[] { 4 })), "A late child was lost with the source cleanup");
    }

    /// <summary>Only the original source folder is removed; other folders and the destination are kept.</summary>
    public static async Task A23RemoveOnlyOriginalSourceAsync()
    {
        var run = await StartAsync(baseline: true);
        var (source, dest) = await MergeFolderAsync(run);
        var other = Seed(run.Pc, "other", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(other, Seed(run.Db, "other", 0, folder: true)), null, null, default);
        Seed(run.Pc, "old/a", 1); Seed(run.Pc, "old/b", 2);
        var result = await MergeAsync(run, source, dest);
        Check.Require(!run.Pc.Items.ContainsKey("old") && run.Pc.Items.ContainsKey("new") && run.Pc.Items.ContainsKey("other"), "The merge removed a folder other than the original source");
        Check.Require(run.Pc.Items.ContainsKey("new/a") && run.Pc.Items.ContainsKey("new/b"), "The free children did not reach the destination");
    }
    private static async Task LinkNoticeAsync(string top)
    {
        var run = await StartAsync(baseline: true);
        Seed(run.Pc, top + "/link", 0, skip: nameof(TextKey.SkipLink));
        await RepairAsync(run);
        var notice = run.Store.OpenNotices.Single();
        Check.Require(notice.Kind == NoticeKind.Info && notice.Cause == TextKey.InfoLinks && notice.Subject.Items.Count == 1,
            "A link was not summarised in the pair-level links notice");
        Check.Require(run.Pc.Items.Count == 1 && run.Db.Items.Count == 0 && run.Store.Records.Count == 0, "A link was followed or copied to the other side");
    }

    public static Task A30JunctionAsync() => LinkNoticeAsync("top-a");
    public static Task A30SymbolicLinkAsync() => LinkNoticeAsync("top-b");

    /// <summary>The pair-level links notice counts the members and closes when the last one is gone.</summary>
    public static async Task A30LastMemberDisappearsAsync()
    {
        var run = await StartAsync(baseline: true);
        Seed(run.Pc, "top-a/link", 0, skip: nameof(TextKey.SkipLink));
        Seed(run.Pc, "top-b/link", 0, skip: nameof(TextKey.SkipLink));
        await RepairAsync(run);
        Check.Require(run.Store.OpenNotices.Single().Subject.Items.Count == 2, "The links notice did not count both members");
        run.Pc.Items.Remove("top-a/link");
        await RepairAsync(run);
        Check.Require(run.Store.OpenNotices.Single().Subject.Items.Count == 1, "The links notice did not shrink to the remaining member");
        run.Pc.Items.Remove("top-b/link");
        await RepairAsync(run);
        Check.Require(run.Store.OpenNotices.Count == 0, "The links notice did not close when the last link was gone");
    }

    /// <summary>A mapped link subtree is never carried into the destination by an enclosing move.</summary>
    public static async Task A30MappedLinkProtectsRemoteMoveAsync()
    {
        var run = await StartAsync(baseline: true);
        var source = Seed(run.Pc, "old", 0, folder: true);
        var dest = source with { Path = "new", Id = "dest" };
        run.Pc.Items["new"] = (dest, []);
        Seed(run.Pc, "old/link", 0, skip: nameof(TextKey.SkipLink));
        var result = await MergeAsync(run, source, dest);
        Check.Require(run.Pc.Items.ContainsKey("old/link") && !run.Pc.Items.ContainsKey("new/link"), "The enclosing move carried the mapped link subtree");
        Check.Require(run.Db.Items.Count == 0, "The link subtree was copied to the other endpoint");
        Check.Require(result.Outcome == RemovalOutcome.Retained, "The enclosing move removed its source despite the protected link");
    }
    private static async Task<PairRun> DeletedFolderRunAsync(PairConfig? pair = null, IReadOnlyList<PairConfig>? extra = null, params string[] deleted)
    {
        var run = await StartAsync(pair: pair, extraPairs: extra);
        foreach (var path in deleted) run.Db.DeletedHistory[path] = new DeletionEvidence.DeletedPath(path, path);
        return run;
    }

    private static void NoKeptFolderNotice(PairRun run) =>
        Check.Require(run.Store.OpenNotices.All(n => n.Cause is not (TextKey.InfoIgnoredFolderKept or TextKey.InfoSharedFolderKept)),
            "A kept-folder notice was raised for a recyclable first-run folder");

    /// <summary>A first-run PC folder holding only an ignored file is recycled whole, never created on Dropbox.</summary>
    public static async Task A36IgnoredFileOnlyAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair() with { IgnoreRules = ["FileName:draft.tmp"] }, null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/draft.tmp", 1);
        await RepairWithProofAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("item") && run.Pc.History.Any(v => v.Entry.Path == "item/draft.tmp"), "The ignored-only folder was not recycled with its ignored content");
        Check.Require(run.Db.Items.Count == 0 && run.Store.Records.Count == 0, "The ignored-only folder was created on Dropbox or bound");
        NoKeptFolderNotice(run);
    }

    /// <summary>A first-run PC folder holding only an ignored folder and its files is recycled whole.</summary>
    public static async Task A36IgnoredFolderWithContentAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair() with { IgnoreRules = ["FolderName:cache"] }, null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/cache", 0, folder: true);
        Seed(run.Pc, "item/cache/x.txt", 2);
        await RepairWithProofAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("item") && run.Pc.History.Any(v => v.Entry.Path == "item/cache/x.txt"), "The ignored folder and its content were not recycled together");
        Check.Require(run.Db.Items.Count == 0 && run.Store.Records.Count == 0, "The ignored folder was created on Dropbox");
        NoKeptFolderNotice(run);
    }

    /// <summary>A first-run PC folder holding an ordinary file is synced, not recycled.</summary>
    public static async Task A36OrdinaryFileInFolderAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair(), null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/keep.txt", 3);
        await RepairAsync(run);
        Check.Require(run.Db.Items.ContainsKey("item") && run.Db.Items.ContainsKey("item/keep.txt"), "The folder with an ordinary file was not synced to Dropbox");
        Check.Require(run.Pc.Items.ContainsKey("item") && run.Pc.History.Count == 0, "The synced PC folder was recycled");
    }

    /// <summary>A first-run PC folder holding an excluded child is synced (the child is not copied).</summary>
    public static async Task A36ExcludedChildAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair() with { Exclusions = [new(Side.Local, "item/held", true)] }, null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/held", 3);
        await RepairAsync(run);
        Check.Require(run.Db.Items.ContainsKey("item") && !run.Db.Items.ContainsKey("item/held"), "The folder with an excluded child was not synced or the excluded child was copied");
        Check.Require(run.Pc.Items.ContainsKey("item") && run.Pc.History.Count == 0, "The folder with an excluded child was recycled");
    }

    /// <summary>A first-run PC folder holding a nested pair's root is synced, the root held back.</summary>
    public static async Task A36NestedRootChildAsync()
    {
        var pair = ItemChecks.Pair();
        var inner = pair with { PairId = "inner", LocalRoot = Path.Combine(pair.LocalRoot, "item", "inner"), DropboxRoot = pair.DropboxRoot + "/item/inner" };
        var run = await DeletedFolderRunAsync(pair, [inner], "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/inner", 0, folder: true);
        await RepairAsync(run);
        Check.Require(run.Db.Items.ContainsKey("item") && !run.Db.Items.ContainsKey("item/inner"), "The nested root was copied or its folder was not synced");
        Check.Require(run.Pc.Items.ContainsKey("item") && run.Pc.History.Count == 0, "The folder holding a nested root was recycled");
    }

    /// <summary>A first-run PC folder holding a link is synced, the link held back.</summary>
    public static async Task A36LinkedChildAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair(), null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/link", 0, skip: nameof(TextKey.SkipLink));
        await RepairAsync(run);
        Check.Require(run.Db.Items.ContainsKey("item") && !run.Db.Items.ContainsKey("item/link"), "The folder with a link was not synced or the link was copied");
        Check.Require(run.Pc.Items.ContainsKey("item") && run.Pc.History.Count == 0, "The folder holding a link was recycled");
    }

    /// <summary>Nested empty folders are recycled with their parent, never created on Dropbox first.</summary>
    public static async Task A36NestedEmptyFoldersAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair(), null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/sub", 0, folder: true);
        await RepairWithProofAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("item") && run.Pc.History.Any(v => v.Entry.Path == "item/sub"), "The nested empty folders were not recycled together");
        Check.Require(run.Db.Items.Count == 0, "The empty folder was created on Dropbox first");
    }

    /// <summary>An empty subfolder with no deletion record of its own is still recycled with its parent.</summary>
    public static async Task A36EmptyChildWithoutDeletionRecordAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair(), null, "item");
        Seed(run.Pc, "item", 0, folder: true);
        Seed(run.Pc, "item/sub", 0, folder: true);
        await RepairWithProofAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("item") && !run.Pc.Items.ContainsKey("item/sub") && run.Db.Items.Count == 0, "An empty child without its own deletion record defeated the parent recycle");
    }

    /// <summary>Ordinal interval handling keeps `A` recycle from swallowing `A!`'s synced contents.</summary>
    public static async Task A36InterleavedOrdinalSubtreesAsync()
    {
        var run = await DeletedFolderRunAsync(ItemChecks.Pair() with { IgnoreRules = ["FileNamePrefix:~"] }, null, "A");
        Seed(run.Pc, "A", 0, folder: true);
        Seed(run.Pc, "A/~draft", 1);
        Seed(run.Pc, "A!", 0, folder: true);
        Seed(run.Pc, "A!/z", 2);
        await RepairWithProofAsync(run);
        Check.Require(!run.Pc.Items.ContainsKey("A") && run.Pc.History.Any(v => v.Entry.Path == "A/~draft"), "The empty-ish A folder was not recycled");
        Check.Require(run.Db.Items.ContainsKey("A!") && run.Db.Items.ContainsKey("A!/z") && run.Pc.Items.ContainsKey("A!"), "A!'s contents were swallowed by A's ordinal interval");
    }
    public static async Task A46CrashStageWrittenAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, SimpleFacts);
            var orphan = OrphanStage(target); var abandoned = await WriteClosedStageAsync(orphan);
            await NextStartAsync(root);
            Check.Require(ReleaseVersion(target) == SchemaVersion && ReleaseScalar(target, SimpleFactsQuery) == 1,
                "A crash after the stage was written lost the authoritative facts");
            Check.Require(File.Exists(orphan) && File.ReadAllBytes(orphan).SequenceEqual(abandoned) &&
                File.Exists(ReleaseBackup(root)) && ReleaseVersion(ReleaseBackup(root)) == 16,
                "The crashed stage image or its predecessor was consumed or rewritten");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46CrashV16PutAsideAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var backup = await WriteReleaseAsync(root, "state.v16.db", SimpleFacts);
            var target = ReleaseTarget(root); var orphan = OrphanStage(target); var abandoned = await WriteClosedStageAsync(orphan);
            await NextStartAsync(root);
            Check.Require(ReleaseVersion(target) == SchemaVersion && ReleaseScalar(target, SimpleFactsQuery) == 1 && File.Exists(backup) && ReleaseVersion(backup) == 16 && File.ReadAllBytes(orphan).SequenceEqual(abandoned),
                "A crash after putting v16 aside lost the backup, mispublished the target, or rewrote the stage image");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46CrashAfterSwitchAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var backup = await WriteReleaseAsync(root, "state.v16.db", SimpleFacts);
            await NextStartAsync(root);
            var target = ReleaseTarget(root); var pointer = Path.Combine(root, "product-root.txt");
            await File.WriteAllTextAsync(pointer, root);
            var warning = await NextStartAsync(root);
            Check.Require(warning == null && !File.Exists(pointer) && ReleaseVersion(target) == SchemaVersion && ReleaseScalar(target, SimpleFactsQuery) == 1 && File.Exists(backup) && ReleaseVersion(backup) == 16,
                "A crash after the switch before pointer removal changed the authoritative image");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46SwitchFailedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, SimpleFacts);
            var before = await File.ReadAllBytesAsync(target);
            bool threw = false, reached = false; FileStream? blocker = null;
            try
            {
                try
                {
                    await new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveWithOutcomeAsync(root, root, (_, _, _) =>
                    {
                        // Runs after the closed stage is built and before the switch. Deny the replacement here so the
                        // failure is provably at the publication boundary rather than an earlier open of the source.
                        reached = true;
                        blocker = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return null;
                    }, default);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { threw = true; }
                // The source bytes must be unchanged while the share-denying handle is still held.
                Check.Require(threw && reached && (await File.ReadAllBytesAsync(target)).SequenceEqual(before),
                    "A failed switch damaged the source image or never reached the publication boundary");
            }
            finally { blocker?.Dispose(); }
            // With the handle released, the source is still the untouched v16 image with no backup.
            Check.Require(ReleaseVersion(target) == 16 && ReleaseScalar(target, SimpleFactsQuery) == 1 && !File.Exists(ReleaseBackup(root)),
                "A failed switch published a migrated image or left a backup");
            await NextStartAsync(root);
            Check.Require(ReleaseVersion(target) == SchemaVersion && ReleaseScalar(target, SimpleFactsQuery) == 1 && File.Exists(ReleaseBackup(root)),
                "The retry after a released switch did not publish the migrated image");
        }
        finally { Directory.Delete(root, true); }
    }
    private static string StoreFileAt(string root) => Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);

    /// <summary>A pointer-cleanup failure after publication is a warning; pairs keep running and the next start retries.</summary>
    public static async Task A46PointerRemovalFailedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new global::Massdex.SyncShark.Engine.Storage.StoreMove();
            await move.CreateAsync(root, default);
            var path = StoreFileAt(root); var pointer = Path.Combine(root, "product-root.txt");
            await File.WriteAllTextAsync(pointer, root);
            string? warning;
            using (new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
                warning = await move.MoveWithOutcomeAsync(root, root, (_, _, ids) => { Check.Require(ids.Count == 0, "Empty store reported required connections"); return null; }, default);
            Check.Require(!string.IsNullOrEmpty(warning) && File.Exists(path) && File.Exists(pointer), "A pointer-cleanup failure hid the usable store");
            var retry = await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
            Check.Require(retry == null && !File.Exists(pointer) && File.Exists(path), "The next start did not retry the pointer removal");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A stage left by an earlier attempt is never adopted or disturbed by the next start.</summary>
    public static async Task A46PreviousAttemptStageUntouchedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new global::Massdex.SyncShark.Engine.Storage.StoreMove();
            await move.CreateAsync(root, default);
            var path = StoreFileAt(root); var stage = path + ".previous.new";
            await File.WriteAllTextAsync(stage, "abandoned stage");
            var warning = await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
            Check.Require(warning == null && File.Exists(path) && await File.ReadAllTextAsync(stage) == "abandoned stage", "The next start consumed or disturbed another attempt's stage");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A pointer to the fixed folder is one source, not an external lineage claim.</summary>
    public static async Task A46PointerToFixedRootAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new global::Massdex.SyncShark.Engine.Storage.StoreMove();
            await move.CreateAsync(root, default);
            var pointer = Path.Combine(root, "product-root.txt");
            await File.WriteAllTextAsync(pointer, root);
            var warning = await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
            Check.Require(warning == null && !File.Exists(pointer) && File.Exists(StoreFileAt(root)), "A pointer to the fixed folder was not treated as one source");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46FixedPointerWithOnlyV16BackupAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var backup = await WriteReleaseAsync(root, "state.v16.db", SimpleFacts);
            var pointer = Path.Combine(root, "product-root.txt"); await File.WriteAllTextAsync(pointer, root);
            var warning = await NextStartAsync(root);
            Check.Require(warning == null && !File.Exists(pointer) && ReleaseVersion(ReleaseTarget(root)) == SchemaVersion && ReleaseScalar(ReleaseTarget(root), SimpleFactsQuery) == 1 && File.Exists(backup) && ReleaseVersion(backup) == 16,
                "A fixed self-pointer with only a v16 backup was not migrated from that backup");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46TwoLinkNoticesFoldedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, TwoLinkFacts);
            await NextStartAsync(root);
            Check.Require(ReleaseScalar(target, "SELECT (SELECT count(*) FROM issue WHERE pair_id=1 AND kind=3 AND cause='InfoLinks')=1 AND (SELECT count(*) FROM issue_member m JOIN issue i USING(issue_id) WHERE m.member_id IN(1,4) AND i.cause='InfoLinks')=2 AND NOT EXISTS(SELECT 1 FROM issue WHERE issue_id IN(8,10))") == 1,
                "Two pure link notices were not folded into one InfoLinks notice");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46MixedLinkAndNonlinkMembersAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, LinkPairFacts);
            await NextStartAsync(root);
            Check.Require(ReleaseScalar(target, "SELECT (SELECT count(*) FROM issue WHERE issue_id=9 AND cause='SkipSpecial')=1 AND (SELECT count(*) FROM issue_member WHERE issue_id=9)=1 AND EXISTS(SELECT 1 FROM issue_member WHERE member_id=3 AND issue_id=9 AND skip_cause='SkipSpecial') AND EXISTS(SELECT 1 FROM issue_member m JOIN issue i USING(issue_id) WHERE m.member_id=2 AND i.cause='InfoLinks')") == 1,
                "A mixed link/nonlink notice did not keep its nonlink member and move the link member");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46UnchangedNoticesPreservedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, UnchangedFacts);
            await NextStartAsync(root);
            Check.Require(ReleaseScalar(target, "SELECT (SELECT count(*) FROM issue WHERE issue_id=30 AND kind=0 AND cause='FailChanged' AND path='keep' AND at_ticks=5 AND dismissed=1)=1 AND EXISTS(SELECT 1 FROM issue_member WHERE member_id=30 AND issue_id=30 AND side=0 AND skip_cause='SkipSpecial')") == 1,
                "Migration changed a notice unrelated to the link fold");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46CrashDuringConnectionListCopyAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, SimpleFacts);
            var orphan = OrphanStage(target); var abandoned = await WriteClosedStageAsync(orphan);
            bool threw = false;
            try { await new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveWithOutcomeAsync(root, root, (_, _, _) => throw new IOException("crash"), default); }
            catch (IOException) { threw = true; }
            Check.Require(threw && ReleaseVersion(target) == 16 && ReleaseScalar(target, SimpleFactsQuery) == 1 && File.ReadAllBytes(orphan).SequenceEqual(abandoned) && !File.Exists(ReleaseBackup(root)),
                "A connection-list failure published, damaged, or rewrote the source image or its stage");
            await NextStartAsync(root);
            Check.Require(ReleaseVersion(target) == SchemaVersion && ReleaseScalar(target, SimpleFactsQuery) == 1,
                "The retry after a connection-list failure did not publish");
        }
        finally { Directory.Delete(root, true); }
    }
    /// <summary>Restart after the first add stops before publication; neither restart nor retry may adopt its stage.</summary>
    public static Task A46CrashDuringFirstPairAddAsync() => FirstAddAsync(true);
    public static Task A46FreshReadCreatesNoStoreAsync() => FreshStartAsync();
    /// <summary>A freshly published image is closed: no WAL, shared-memory or rollback-journal side file remains.</summary>
    public static async Task A46ClosedImageNoWalAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            await new global::Massdex.SyncShark.Engine.Storage.StoreMove().CreateAsync(root, default);
            var path = StoreFileAt(root);
            Check.Require(File.Exists(path), "No store was created");
            Check.Require(!File.Exists(path + "-wal") && !File.Exists(path + "-shm") && !File.Exists(path + "-journal"), "The published image depends on a WAL side file");
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await db.OpenAsync();
            using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version";
            Check.Require(Convert.ToInt32(await cmd.ExecuteScalarAsync()) == global::Massdex.SyncShark.Engine.Storage.Schema.Version, "The published image has the wrong version");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Two independently occupied stores never prove provenance: the move refuses without overwriting either.</summary>
    public static async Task A46AmbiguousIndependentStoresAsync()
    {
        var first = Directory.CreateTempSubdirectory().FullName; var second = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new global::Massdex.SyncShark.Engine.Storage.StoreMove();
            await move.CreateAsync(first, default); await move.CreateAsync(second, default);
            var pointer = Path.Combine(second, "product-root.txt"); await File.WriteAllTextAsync(pointer, first);
            var firstBefore = await File.ReadAllBytesAsync(StoreFileAt(first)); var secondBefore = await File.ReadAllBytesAsync(StoreFileAt(second));
            bool refused = false;
            try { await move.MoveWithOutcomeAsync(second, second, (_, _, _) => null, default); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && File.ReadAllText(pointer) == first && (await File.ReadAllBytesAsync(StoreFileAt(first))).SequenceEqual(firstBefore) &&
                (await File.ReadAllBytesAsync(StoreFileAt(second))).SequenceEqual(secondBefore), "An ambiguous independent store was overwritten or consumed");
        }
        finally { Directory.Delete(first, true); Directory.Delete(second, true); }
    }

    /// <summary>A pointer to a directory that holds no release image is refused, leaving source and pointer intact.</summary>
    public static async Task A46InvalidSourceAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new global::Massdex.SyncShark.Engine.Storage.StoreMove();
            await move.CreateAsync(root, default);
            var other = Directory.CreateDirectory(Path.Combine(root, "elsewhere")).FullName;
            var pointer = Path.Combine(root, "product-root.txt"); await File.WriteAllTextAsync(pointer, other);
            var before = await File.ReadAllBytesAsync(StoreFileAt(root));
            bool refused = false;
            try { await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && File.Exists(pointer) && File.ReadAllText(pointer) == other && (await File.ReadAllBytesAsync(StoreFileAt(root))).SequenceEqual(before), "An invalid source was accepted or the store changed");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A dangling pointer to a missing predecessor is refused without creating or changing a store.</summary>
    public static async Task A46ExternalDanglingPointerAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var move = new global::Massdex.SyncShark.Engine.Storage.StoreMove();
            await move.CreateAsync(root, default);
            var missing = Path.Combine(root, "missing-predecessor");
            var pointer = Path.Combine(root, "product-root.txt"); await File.WriteAllTextAsync(pointer, missing);
            var before = await File.ReadAllBytesAsync(StoreFileAt(root));
            bool refused = false;
            try { await move.MoveWithOutcomeAsync(root, root, (_, _, _) => null, default); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && File.ReadAllText(pointer) == missing && (await File.ReadAllBytesAsync(StoreFileAt(root))).SequenceEqual(before), "A dangling external pointer was followed or changed the store");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Both lists absent with no required connections is valid empty input: no store is created.</summary>
    public static async Task A46BothListsAbsentNoRequiredAccountAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var warning = await new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveWithOutcomeAsync(root, root, (_, _, _) => null, default);
            Check.Require(warning == null && !File.Exists(StoreFileAt(root)), "Empty input created a store instead of leaving the first add to publish");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46MissingRequiredConnectionAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, MissingConnectionFacts);
            // The pair names a well-formed id that the (empty, canonical) connection list does not hold, so the same
            // Connections.Verify boundary production startup uses must refuse before publication.
            Directory.CreateDirectory(Path.Combine(root, "state"));
            await File.WriteAllTextAsync(Path.Combine(root, "state", "dropbox-connection.jsonl"), "[]\n");
            var connections = new global::Massdex.SyncShark.Engine.Dropbox.Connections();
            bool refused = false;
            try { await new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveWithOutcomeAsync(root, root, (source, product, ids) => connections.Verify(source, product, ids), default); }
            catch (InvalidDataException) { refused = true; }
            Check.Require(refused && ReleaseVersion(target) == 16 && !File.Exists(ReleaseBackup(root)) &&
                ReleaseScalar(target, "SELECT EXISTS(SELECT 1 FROM pair WHERE json_extract(def_json,'$.ConnectionId')='connection-absent')") == 1,
                "A valid required connection absent from the list was not refused before publication");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A46DistinctConnectionListsAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var target = await WriteReleaseAsync(root, ResourceNames.DatabaseFile, DistinctFacts);
            IReadOnlyList<string>? seen = null;
            await new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveWithOutcomeAsync(root, root, (_, _, ids) => { seen = ids; return null; }, default);
            Check.Require(seen != null && seen.Count == 2 && seen.OrderBy(x => x).SequenceEqual(new[] { "c1", "c2" }) && ReleaseVersion(target) == SchemaVersion && ReleaseScalar(target, "SELECT count(*) FROM pair WHERE json_extract(def_json,'$.ConnectionId') IN ('c1','c2')") == 2,
                "Distinct required connections were not both presented before publication");
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A48SameNameRemovedTwiceAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar; var bin = Path.Combine(root, ResourceNames.RootBinFolder);
                var full = Path.Combine(root, "x");
                File.WriteAllBytes(full, new byte[] { 1, 2 });
                var e1 = ManagedFile(full, "x");
                var r1 = await ArmedBin().RemoveAsync(volume, DriveType.Removable, e1, ManagedProofFile(e1, ManagedVolume(full)), default);
                File.WriteAllBytes(full, new byte[] { 3, 4 });
                var e2 = ManagedFile(full, "x");
                var r2 = await ArmedBin().RemoveAsync(volume, DriveType.Removable, e2, ManagedProofFile(e2, ManagedVolume(full)), default);
                Check.Require(r1.Outcome == RemovalOutcome.Removed && r2.Outcome == RemovalOutcome.Removed && !File.Exists(full),
                    "A repeated managed removal did not settle both versions");
                Check.Require(File.Exists(Path.Combine(bin, "x")) && File.ReadAllBytes(Path.Combine(bin, "x")).SequenceEqual(new byte[] { 1, 2 }) &&
                    File.Exists(Path.Combine(bin, "x (2)")) && File.ReadAllBytes(Path.Combine(bin, "x (2)")).SequenceEqual(new byte[] { 3, 4 }),
                    "Two receipts for one name did not preserve both versions");
            }
            finally { Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static Task A48TwoPairsOneVolumeAsync() => throw new NotImplementedException();
    public static async Task A48DriveRootScopeAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive(); var culture = CultureInfo.InvariantCulture;
        try
        {
            Subst(drive, root);
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar; var bin = Path.Combine(root, ResourceNames.RootBinFolder);
                var folder = Directory.CreateDirectory(Path.Combine(root, "top")).FullName;
                Directory.CreateDirectory(Path.Combine(folder, "nested")); File.WriteAllBytes(Path.Combine(folder, "nested", "child"), new byte[] { 7, 9 });
                var identity = Identity(folder);
                var entry = new Entry("top", true, 0, default, unchecked((long)identity.FileId).ToString(culture), "", "", null);
                var proof = new RemovalSelection(new RemovalProof(entry, identity.VolumeSerialNumber.ToString(culture), null), null);
                var result = await ArmedBin().RemoveAsync(volume, DriveType.Removable, entry, proof, default);
                Check.Require(result.Outcome == RemovalOutcome.Removed && !Directory.Exists(folder) && Directory.Exists(Path.Combine(bin, "top")) &&
                    File.ReadAllBytes(Path.Combine(bin, "top", "nested", "child")).SequenceEqual(new byte[] { 7, 9 }),
                    "A folder at the drive root scope was not recycled whole into the volume bin");
            }
            finally { Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A48RootBinJunctionRemoveRefusedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            string? link = null;
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar; var path = Path.Combine(root, "source"); File.WriteAllBytes(path, new byte[] { 1, 3 });
                var target = Directory.CreateDirectory(Path.Combine(root, "redirect")).FullName; File.WriteAllText(Path.Combine(target, "kept"), "original");
                link = Path.Combine(root, ResourceNames.RootBinFolder); Directory.CreateSymbolicLink(link, target);
                var entry = ManagedFile(path, "source");
                var result = await ArmedBin().RemoveAsync(volume, DriveType.Removable, entry, ManagedProofFile(entry, ManagedVolume(path)), default);
                Check.Require(result.Outcome == RemovalOutcome.NoEffect && result.Cause == TextKey.RecycleUnavailable &&
                    File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 3 }) &&
                    Directory.GetFileSystemEntries(target).Length == 1 && File.ReadAllText(Path.Combine(target, "kept")) == "original",
                    "A linked root bin did not refuse without changing the source or redirect target");
            }
            finally { if (link != null && Directory.Exists(link)) Directory.Delete(link); Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A48RootBinJunctionTrimRefusedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            string? link = null;
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar;
                var target = Directory.CreateDirectory(Path.Combine(root, "redirect")).FullName;
                var old = Path.Combine(target, "old"); File.WriteAllText(old, "kept"); File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-40));
                link = Path.Combine(root, ResourceNames.RootBinFolder); Directory.CreateSymbolicLink(link, target);
                await new global::Massdex.SyncShark.Engine.Local.RecycleBin().TrimAsync(volume, DriveType.Removable, DateTimeOffset.UtcNow, default);
                Check.Require(File.Exists(old) && File.ReadAllText(old) == "kept", "Trim entered a linked root bin instead of refusing");
            }
            finally { if (link != null && Directory.Exists(link)) Directory.Delete(link); Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A48StartupTrimsOldTopLevelAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar; var now = DateTimeOffset.UtcNow;
                var bin = Directory.CreateDirectory(Path.Combine(root, ResourceNames.RootBinFolder)).FullName;
                var old = Path.Combine(bin, "old"); File.WriteAllText(old, "gone"); File.SetCreationTimeUtc(old, now.UtcDateTime.AddDays(-40));
                var recent = Path.Combine(bin, "recent"); File.WriteAllText(recent, "kept"); File.SetCreationTimeUtc(recent, now.UtcDateTime.AddDays(-1));
                // Retention is decided by the top-level item's own creation; a fresh top-level folder holding an expired
                // descendant is kept whole (the trim never descends), so an old child cannot drag its parent out.
                var freshTop = Directory.CreateDirectory(Path.Combine(bin, "fresh-top")).FullName; Directory.SetCreationTimeUtc(freshTop, now.UtcDateTime.AddDays(-1));
                var oldDescendant = Path.Combine(freshTop, "old-descendant"); File.WriteAllText(oldDescendant, "kept"); File.SetCreationTimeUtc(oldDescendant, now.UtcDateTime.AddDays(-40));
                await new global::Massdex.SyncShark.Engine.Local.RecycleBin().TrimAsync(volume, DriveType.Removable, now, default);
                Check.Require(!File.Exists(old) && File.Exists(recent) && File.Exists(oldDescendant), "Startup trim removed an expired descendant or kept only some expired top-level items");
            }
            finally { Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A48ExactRetentionBoundaryAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar;
                // The approved retention contract is 30 days; the boundary cases are derived around it (±1s), an
                // independent expectation rather than a mirror of the product's clock.
                var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); var cutoff = now.UtcDateTime.AddDays(-30);
                var bin = Directory.CreateDirectory(Path.Combine(root, ResourceNames.RootBinFolder)).FullName;
                var edge = Path.Combine(bin, "edge"); File.WriteAllText(edge, "k"); File.SetCreationTimeUtc(edge, cutoff);
                var inside = Path.Combine(bin, "inside"); File.WriteAllText(inside, "k"); File.SetCreationTimeUtc(inside, cutoff.AddSeconds(1));
                var over = Path.Combine(bin, "over"); File.WriteAllText(over, "g"); File.SetCreationTimeUtc(over, cutoff.AddSeconds(-1));
                await new global::Massdex.SyncShark.Engine.Local.RecycleBin().TrimAsync(volume, DriveType.Removable, now, default);
                Check.Require(File.Exists(edge) && File.Exists(inside) && !File.Exists(over), "The retention boundary did not keep exactly-30-day items and remove older ones");
            }
            finally { Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static Task A48QuietJobBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48FailedPutBackBeforeTrimAsync() => throw new NotImplementedException();
    public static async Task A48BinOnNativeBinVolumeNotTrimmedAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar;
                var bin = Directory.CreateDirectory(Path.Combine(root, ResourceNames.RootBinFolder)).FullName;
                var old = Path.Combine(bin, "old"); File.WriteAllText(old, "kept"); File.SetCreationTimeUtc(old, DateTime.UtcNow.AddDays(-40));
                await new global::Massdex.SyncShark.Engine.Local.RecycleBin().TrimAsync(volume, DriveType.Fixed, DateTimeOffset.UtcNow, default);
                Check.Require(File.Exists(old), "A fixed (native-bin) volume's managed bin was trimmed");
            }
            finally { Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }
    public static async Task A48UnrelatedVolumeNotTrimmedAsync()
    {
        var rootA = Directory.CreateTempSubdirectory().FullName; var rootB = Directory.CreateTempSubdirectory().FullName; var driveA = FreeDrive();
        try
        {
            Subst(driveA, rootA);
            AssertFixtureVolume(driveA, rootA);
            var driveB = FreeDrive();
            try
            {
                Subst(driveB, rootB);
                try
                {
                    AssertFixtureVolume(driveB, rootB);
                    var a = Directory.CreateDirectory(Path.Combine(rootA, ResourceNames.RootBinFolder)).FullName;
                    var b = Directory.CreateDirectory(Path.Combine(rootB, ResourceNames.RootBinFolder)).FullName;
                    var oldA = Path.Combine(a, "old"); File.WriteAllText(oldA, "a"); File.SetCreationTimeUtc(oldA, DateTime.UtcNow.AddDays(-40));
                    var oldB = Path.Combine(b, "old"); File.WriteAllText(oldB, "b"); File.SetCreationTimeUtc(oldB, DateTime.UtcNow.AddDays(-40));
                    await new global::Massdex.SyncShark.Engine.Local.RecycleBin().TrimAsync(driveA + Path.DirectorySeparatorChar, DriveType.Removable, DateTimeOffset.UtcNow, default);
                    Check.Require(!File.Exists(oldA) && File.Exists(oldB), "Trim of one volume touched another volume's bin");
                }
                finally { Subst(driveB, "/D"); }
            }
            finally { Subst(driveA, "/D"); }
        }
        finally { Directory.Delete(rootA, true); Directory.Delete(rootB, true); }
    }
    public static Task A48LongDowntimeRecoverBeforeTrimAsync() => throw new NotImplementedException();
    public static Task A48UnavailableHistoryAsync() => throw new NotImplementedException();
    public static Task A48UnknownDriveUsesShellOrAbortAsync() => throw new NotImplementedException();
    public static async Task A48StampBeforeNonreplacingMoveAsync()
    {
        var root = Directory.CreateTempSubdirectory().FullName; var drive = FreeDrive();
        try
        {
            Subst(drive, root);
            try
            {
                AssertFixtureVolume(drive, root);
                var volume = drive + Path.DirectorySeparatorChar; var bin = Path.Combine(root, ResourceNames.RootBinFolder);
                var full = Path.Combine(root, "x"); File.WriteAllBytes(full, new byte[] { 5, 6 }); File.SetCreationTimeUtc(full, DateTime.UtcNow.AddDays(-40));
                var sourceWrite = File.GetLastWriteTimeUtc(full); var sourceId = Identity(full); var removalStart = DateTime.UtcNow;
                var entry = ManagedFile(full, "x");
                var result = await ArmedBin().RemoveAsync(volume, DriveType.Removable, entry, ManagedProofFile(entry, ManagedVolume(full)), default);
                Check.Require(result.Outcome == RemovalOutcome.Removed, "A managed removal of an old file did not settle");
                var binned = Path.Combine(bin, "x");
                // At the move boundary the binned object is the same identity with unchanged bytes and modification
                // time, and its creation is re-stamped to the removal time so the 30-day trim keeps it. Whether that
                // stamp lands before or after the rename is not distinguishable offline; that is the live step RS-31,
                // so only these boundary facts are claimed here.
                Check.Require(File.Exists(binned) && File.ReadAllBytes(binned).SequenceEqual(new byte[] { 5, 6 }) &&
                    Identity(binned) == sourceId && File.GetLastWriteTimeUtc(binned) == sourceWrite &&
                    File.GetCreationTimeUtc(binned) >= removalStart && File.GetCreationTimeUtc(binned) <= DateTime.UtcNow.AddMinutes(1),
                    "The moved object did not preserve identity, bytes and modification time with a fresh creation stamp");
                await new global::Massdex.SyncShark.Engine.Local.RecycleBin().TrimAsync(volume, DriveType.Removable, DateTimeOffset.UtcNow, default);
                Check.Require(File.Exists(binned), "A freshly binned version was trimmed: creation was not re-stamped at removal");
            }
            finally { Subst(drive, "/D"); }
        }
        finally { Directory.Delete(root, true); }
    }

    // ---- A46 materialized store-move preimages: rebuild the release image, run the next start, prove the image ----
    private static int SchemaVersion => global::Massdex.SyncShark.Engine.Storage.Schema.Version;
    private const string SimpleFacts = """
        INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c"}');
        INSERT INTO synced_entry(pair_id,local_path,is_folder,local_id,local_size,local_mtime,dropbox_id,dropbox_hash) VALUES(1,'item',0,101,1,10,'cloud',X'01');
        INSERT INTO journal_position VALUES('volume',3,17);
        """;
    private const string SimpleFactsQuery = "SELECT EXISTS(SELECT 1 FROM pair p JOIN synced_entry s ON p.id=s.pair_id JOIN journal_position j ON j.volume_key='volume' WHERE p.pair_id='p' AND s.local_id=101 AND s.dropbox_id='cloud' AND j.journal_id=3 AND j.next_usn=17)";
    private const string LinkPairFacts = """
        INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c"}');
        INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
          (8,1,4,'SkipLink','a','{"Items":[],"Incoming":null}',1,0),
          (9,1,4,'SkipLink','b','{"Items":[{"Side":0,"Path":"b","Id":"old-link"},{"Side":1,"Path":"b","Id":"still-skipped"}],"Incoming":null}',2,0);
        INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
          (1,8,1,0,pathkey('a'),'{"Version":1,"Value":{"Items":[],"Incoming":null}}','SkipLink'),
          (2,9,1,0,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"b","Id":"old-link"}],"Incoming":null}}','SkipLink'),
          (3,9,1,1,pathkey('b'),'{"Version":1,"Value":{"Items":[{"Side":"Dropbox","Path":"b","Id":"still-skipped"}],"Incoming":null}}','SkipSpecial');
        """;
    private const string TwoLinkFacts = """
        INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c"}');
        INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
          (8,1,4,'SkipLink','a','{"Items":[],"Incoming":null}',1,0),
          (10,1,4,'SkipLink','c','{"Items":[],"Incoming":null}',3,0);
        INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
          (1,8,1,0,pathkey('a'),'{"Version":1,"Value":{"Items":[],"Incoming":null}}','SkipLink'),
          (4,10,1,0,pathkey('c'),'{"Version":1,"Value":{"Items":[],"Incoming":null}}','SkipLink');
        """;
    private const string UnchangedFacts = """
        INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"c"}');
        INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES
          (30,1,0,'FailChanged','keep','{"Items":[{"Side":0,"Path":"keep","Id":"k"}],"Incoming":null}',5,1);
        INSERT INTO issue_member(member_id,issue_id,pair_id,side,path_key,subject_json,skip_cause) VALUES
          (30,30,1,0,pathkey('keep'),'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"keep","Id":"k"}],"Incoming":null}}','SkipSpecial');
        """;
    // A well-formed id the seeded connection list (the empty canonical []) does not hold.
    private const string MissingConnectionFacts = """INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p','{"ConnectionId":"connection-absent"}');""";
    private const string DistinctFacts = """
        INSERT INTO pair(id,pair_id,def_json) VALUES(1,'p1','{"ConnectionId":"c1"}'),(2,'p2','{"ConnectionId":"c2"}');
        """;

    // The release DDL is the real fixture in Scenarios/StoreMoveChecks.cs (the same file Storage/StoreMoveChecks borrows).
    // Read and parsed once per process, not once per seed.
    private static readonly Lazy<string> ReleaseDdl = new(() =>
    {
        var source = File.ReadAllText(Path.Combine(CheckArguments.Parse(Environment.GetCommandLineArgs()[1..]).Root, "tests", "SyncShark.Engine.Tests", "Scenarios", "StoreMoveChecks.cs"));
        var field = CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(n => n.Identifier.ValueText == "ReleaseSchema");
        return ((LiteralExpressionSyntax)field.Initializer!.Value).Token.ValueText;
    });
    private static Task<string> WriteReleaseAsync(string root, string fileName, string seed)
    {
        Directory.CreateDirectory(Path.Combine(root, ResourceNames.EngineFolder));
        var path = Path.Combine(root, ResourceNames.EngineFolder, fileName);
        using var db = ReleaseDb(path, SqliteOpenMode.ReadWriteCreate);
        using var cmd = db.CreateCommand(); cmd.CommandText = ReleaseDdl.Value + seed; cmd.ExecuteNonQuery();
        return Task.FromResult(path);
    }
    private static SqliteConnection ReleaseDb(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false }.ToString());
        db.Open();
        db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
        db.CreateFunction<string, int>("plandepth", PathNames.Depth, isDeterministic: true);
        db.CreateFunction<string, int>("localaccessretrycause", _ => 0, isDeterministic: true);
        return db;
    }
    private static string ReleaseTarget(string root) => Path.Combine(root, ResourceNames.EngineFolder, ResourceNames.DatabaseFile);
    private static string ReleaseBackup(string root) => Path.Combine(root, ResourceNames.EngineFolder, "state.v16.db");
    private static string OrphanStage(string target) => target + "." + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture) + "." + Guid.NewGuid().ToString("N") + ".new";
    /// <summary>A distinguishable, closed v17 image (no WAL side file) so an abandoned stage is a real store, not text;
    /// the marker journal row would surface if it were ever adopted.</summary>
    private static async Task<byte[]> WriteClosedStageAsync(string stage)
    {
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = stage, Pooling = false }.ToString()))
        {
            await db.OpenAsync();
            db.CreateFunction<string, string>("pathkey", PathNames.Key, isDeterministic: true);
            using var cmd = db.CreateCommand();
            cmd.CommandText = global::Massdex.SyncShark.Engine.Storage.Schema.Create;
            await cmd.ExecuteNonQueryAsync();
            cmd.CommandText = $"PRAGMA user_version={SchemaVersion}; INSERT INTO journal_position VALUES('stage-marker',11,13)";
            await cmd.ExecuteNonQueryAsync();
        }
        return await File.ReadAllBytesAsync(stage);
    }
    private static int ReleaseVersion(string path)
    {
        using var db = ReleaseDb(path, SqliteOpenMode.ReadOnly);
        using var cmd = db.CreateCommand(); cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    private static int ReleaseScalar(string path, string sql)
    {
        using var db = ReleaseDb(path, SqliteOpenMode.ReadOnly);
        using var cmd = db.CreateCommand(); cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
    private static Task<string?> NextStartAsync(string root, Func<string, string, IReadOnlyList<string>, Action?>? verify = null)
        => new global::Massdex.SyncShark.Engine.Storage.StoreMove().MoveWithOutcomeAsync(root, root, verify ?? ((_, _, _) => null), default);

    // ---- A48 removable-volume bin fixtures: drive the real shell-bin and trim paths on marker-safe SUBST roots ----
    // Ten candidate letters Q..Z, highest first, taking the first the OS reports free; the same range the
    // Local/RecycleBinChecks and Local/TrimContinues fixtures use, so a synthetic SUBST alias never shadows a
    // real in-use drive.
    private static string FreeDrive() => Enumerable.Range('Q', 10).Reverse().Select(c => ((char)c) + ":").First(d => !Directory.Exists(d + Path.DirectorySeparatorChar));
    private static void Subst(string drive, string argument)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "subst.exe")) { UseShellExecute = false };
        start.ArgumentList.Add(drive); start.ArgumentList.Add(argument);
        using var command = System.Diagnostics.Process.Start(start)!; command.WaitForExit();
        Check.Require(command.ExitCode == 0, "could not prepare or release synthetic volume");
    }
    /// <summary>The ownership proof from Local/TrimContinues: a removable-bin Remove/Trim may run only once the SUBST alias
    /// is shown to resolve to this fixture's own temporary root, so a stale or foreign mapping is never touched.</summary>
    private static void AssertFixtureVolume(string drive, string root)
    {
        var marker = Guid.NewGuid().ToString("N");
        File.WriteAllText(Path.Combine(root, marker), marker);
        Check.Require(File.ReadAllText(Path.Combine(drive + Path.DirectorySeparatorChar, marker)) == marker, "drive is not the fixture");
    }
    [System.Runtime.InteropServices.DllImport("kernel32", EntryPoint = "CreateFileW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenMetadata(string path, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [System.Runtime.InteropServices.DllImport("kernel32", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle h, int kind, [System.Runtime.InteropServices.Out] byte[] data, uint bytes);
    // Win32 values, by name from the Windows SDK: access FILE_READ_ATTRIBUTES=0x80; disposition OPEN_EXISTING=3;
    // flags FILE_FLAG_BACKUP_SEMANTICS(0x02000000)|FILE_FLAG_OPEN_REPARSE_POINT(0x00200000)|FILE_FLAG_SESSION_AWARE(0x00100000)
    // = 0x02300000; FILE_INFO_BY_HANDLE_CLASS.FileIdInfo=18 and sizeof(FILE_ID_INFO)=24. Mirrors Local/RecycleBinChecks.Identity.
    private static DirectoryIdentity Identity(string full)
    {
        using var handle = OpenMetadata(full, 0x80, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, 3, 0x02300000, IntPtr.Zero);
        var id = new byte[24]; Check.Require(!handle.IsInvalid && GetFileInformationByHandleEx(handle, 18, id, 24), "native fixture identity unavailable");
        return new(BitConverter.ToUInt64(id), BitConverter.ToUInt64(id, 8), BitConverter.ToUInt64(id, 16));
    }
    private static global::Massdex.SyncShark.Engine.Local.RecycleBin ArmedBin()
        => (global::Massdex.SyncShark.Engine.Local.RecycleBin)Activator.CreateInstance(typeof(global::Massdex.SyncShark.Engine.Local.RecycleBin),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, [(Func<Task<bool>>)(() => Task.FromResult(true)), null], null)!;
    private static Entry ManagedFile(string full, string rel)
    {
        var identity = Identity(full); var bytes = File.ReadAllBytes(full);
        using var hash = new ContentHash(); hash.Append(bytes);
        return new Entry(rel, false, bytes.Length, File.GetLastWriteTimeUtc(full), unchecked((long)identity.FileId).ToString(CultureInfo.InvariantCulture), "", hash.Finish(), null);
    }
    private static string ManagedVolume(string full) => Identity(full).VolumeSerialNumber.ToString(CultureInfo.InvariantCulture);
    private static RemovalSelection ManagedProofFile(Entry entry, string volumeId) => new(new RemovalProof(entry, volumeId, entry.ContentHash), null);
    private static PairConfig NestedPair(PairConfig outer, string innerRoot) =>
        outer with { PairId = "inner", LocalRoot = Path.Combine(outer.LocalRoot, innerRoot), DropboxRoot = outer.DropboxRoot + "/" + innerRoot };

    /// <summary>A new inner pair's root is skipped by the outer pair before its next entry and its records are kept.</summary>
    public static async Task A50NewInnerRootEffectiveBeforeNextEntryAsync()
    {
        var pair = ItemChecks.Pair(); var inner = NestedPair(pair, "nested");
        var run = await StartAsync(pair, baseline: true, extraPairs: [inner]);
        Seed(run.Pc, "nested/x", 1);
        await RepairAsync(run);
        Check.Require(run.Db.Items.Count == 0 && run.Store.Records.Count == 0, "The outer pair synced the new inner pair's subtree");
    }

    /// <summary>An added exclusion applies to the outer pair before its next entry.</summary>
    public static async Task A50ExclusionEffectiveBeforeNextEntryAsync()
    {
        var pair = ItemChecks.Pair() with { Exclusions = [new(Side.Local, "held", true)] };
        var run = await StartAsync(pair, baseline: true);
        Seed(run.Pc, "held/x", 1);
        await RepairAsync(run);
        Check.Require(run.Db.Items.Count == 0 && run.Store.Records.Count == 0, "The outer pair ignored the newly added exclusion");
    }

    /// <summary>Once the inner pair is removed, the outer pair compares the formerly quiet subtree at once.</summary>
    public static async Task A50InnerPairRemovedQuietRangeAsync()
    {
        var pair = ItemChecks.Pair();
        var run = await StartAsync(pair, baseline: true);
        Seed(run.Pc, "nested/x", 1);
        await RepairAsync(run);
        Check.Require(run.Db.Items.ContainsKey("nested/x") && run.Store.Records.Values.Any(r => r.LocalPath == "nested/x"), "The outer pair did not compare the removed inner pair's subtree");
    }

    /// <summary>Nested roots are derived for each side's own actual roots.</summary>
    public static Task A50SideAccountSpecificRootsAsync()
    {
        var pair = ItemChecks.Pair(); var inner = NestedPair(pair, "nested");
        var paths = new PathScope(pair, [pair, inner], DriveType.Fixed);
        Check.Require(paths.NestedRoots(Side.Local).SequenceEqual(new[] { "nested" }) && paths.NestedRoots(Side.Dropbox).SequenceEqual(new[] { "nested" }),
            "Nested roots were not derived separately for each side's actual roots");
        return Task.CompletedTask;
    }

    /// <summary>Both mapped endpoints of a nested root are protected from a whole or partial move: the covering
    /// path carries the root, and an endpoint inside the root is inside a protected range.</summary>
    public static Task A50MappedOtherEndpointProtectedAsync()
    {
        var pair = ItemChecks.Pair(); var inner = NestedPair(pair, "nested");
        var paths = new PathScope(pair, [pair, inner], DriveType.Fixed);
        // HoldsNestedRoot answers "is the nested root at or under this path" - the covering path a whole/partial move starts from.
        Check.Require(paths.HoldsNestedRoot(Side.Local, "nested") && paths.HoldsNestedRoot(Side.Dropbox, "nested"),
            "The covering path of a mapped nested root was not protected before a whole or partial move");
        // An endpoint inside the root is protected only by the overlap direction, on each side's own spelling.
        Check.Require(paths.OverlapsNestedRoot(Side.Local, "nested/child", PathNames.KeyFor(Side.Local, "nested/child")) &&
            paths.OverlapsNestedRoot(Side.Dropbox, "nested/child", PathNames.KeyFor(Side.Dropbox, "nested/child")),
            "An endpoint inside a mapped nested root was not protected before a move");
        return Task.CompletedTask;
    }

    /// <summary>A whole-folder move that carries another pair's root fails without being propagated.</summary>
    public static async Task A50WholeMoveProtectedAsync()
    {
        var pair = ItemChecks.Pair(); var inner = NestedPair(pair, "top/nested");
        var run = await StartAsync(pair, baseline: true, extraPairs: [inner]);
        var top = Seed(run.Pc, "top", 0, folder: true); var dbTop = Seed(run.Db, "top", 0, folder: true);
        await run.Store.CommitAsync(pair.PairId, null, SyncedEntries.From(top, dbTop), null, null, default);
        await run.Pc.MoveAsync(top, "moved", default);
        await RepairAsync(run);
        Check.Require(run.Store.OpenNotices.Any(n => n.Cause == TextKey.FailCarriesOtherPair), "The move carrying another pair's root raised no failure");
        Check.Require(!run.Db.Items.ContainsKey("moved") && run.Db.Items.ContainsKey("top"), "The carrying move was propagated to the other side");
    }

    /// <summary>A partial move that carries another pair's root is likewise refused.</summary>
    public static async Task A50PartialMoveProtectedAsync()
    {
        var pair = ItemChecks.Pair(); var inner = NestedPair(pair, "top/sub/nested");
        var run = await StartAsync(pair, baseline: true, extraPairs: [inner]);
        var top = Seed(run.Pc, "top", 0, folder: true); var sub = Seed(run.Pc, "top/sub", 0, folder: true);
        var dbTop = Seed(run.Db, "top", 0, folder: true); var dbSub = Seed(run.Db, "top/sub", 0, folder: true);
        await run.Store.CommitAsync(pair.PairId, null, SyncedEntries.From(top, dbTop), null, null, default);
        await run.Store.CommitAsync(pair.PairId, null, SyncedEntries.From(sub, dbSub), null, null, default);
        await run.Pc.MoveAsync(sub, "moved", default);
        await RepairAsync(run);
        Check.Require(run.Store.OpenNotices.Any(n => n.Cause == TextKey.FailCarriesOtherPair), "The partial move carrying another pair's root raised no failure");
        Check.Require(!run.Db.Items.ContainsKey("moved") && run.Db.Items.ContainsKey("top/sub"), "The partial carrying move was propagated");
    }

    /// <summary>A folder holding another pair's root is not replaced by a file; a retained-replacement failure is raised.</summary>
    public static async Task A50ReplacementRetainsFolderAsync()
    {
        var pair = ItemChecks.Pair(); var inner = NestedPair(pair, "top/nested");
        var run = await StartAsync(pair, baseline: true, extraPairs: [inner]);
        var top = Seed(run.Pc, "top", 0, folder: true); var dbTop = Seed(run.Db, "top", 0, folder: true);
        await run.Store.CommitAsync(pair.PairId, null, SyncedEntries.From(top, dbTop), null, null, default);
        run.Db.Items["top"] = (ItemChecks.File(Side.Dropbox, "top", 9, "incoming-file"), [9]);
        await RepairAsync(run);
        Check.Require(run.Pc.Items.ContainsKey("top") && run.Pc.Items["top"].Entry.IsFolder, "The folder holding another pair's root was replaced");
        Check.Require(run.Store.OpenNotices.Any(n => n.Cause == TextKey.FailReplacementRetained), "The retained replacement raised no failure notice");
    }

    // B-218 (open-bug audit 20261123): a Dropbox folder moves after the delete's identity check and
    // before the delete request. The queued tombstone still names the old path, but the selected
    // identity now lives at the new one, so no delete may land there and no version may be lost.
    public static async Task A13DropboxMoveLandsBeforeDeleteRequestAsync()
    {
        var run = await StartAsync(baseline: true);
        var pcFolder = Seed(run.Pc, "old", 0, folder: true);
        var dbFolder = Seed(run.Db, "old", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pcFolder, dbFolder), null, null, default);
        var pcChild = Seed(run.Pc, "old/child", 1);
        var dbChild = Seed(run.Db, "old/child", 1);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pcChild, dbChild), null, null, default);
        // The move lands after the read that queued the tombstone; the queued delete still names the old path.
        await run.Db.MoveAsync(dbFolder, "new", default);
        var tombstone = new Change(Side.Dropbox, ChangeKind.Deleted, "old", null, dbFolder.Id, null, false, false);
        await ItemSync.ApplyAsync(run.Pair, ItemChecks.Settings, _ => { }, run.Paths, tombstone, run.Pc, run.Db, run.Store,
            ItemChecks.Population(run.Pair, run.Paths, run.Pc, run.Db, run.Store),
            ItemChecks.Hop(run.Pair, run.Paths, run.Queue, run.Pc, run.Db, run.Store), default,
            heldInput: _ => [], coversThrough: _ => true);
        Check.Require(Is(run.Db, "new", true) && Is(run.Db, "new/child", false, 1), "The queued delete removed the moved Dropbox folder or its child");
        Check.Require(Is(run.Pc, "new", true) && Is(run.Pc, "new/child", false, 1), "The move did not sync to the PC");
        Check.Require(run.Db.Requests[nameof(ISide.DeleteAsync)] == 0, "The queued tombstone issued a Dropbox delete for the moved identity");
        Check.Require(run.Db.History.Count == 0 && run.Pc.History.Count == 0, "A version was lost to the mistaken delete");
        Check.Require(run.Store.OpenNotices.All(n => n.Cause != TextKey.InfoMovedDeleted), "The surviving move was reported as moved and deleted");
    }

    // L111 (open-bug audit 20261123): crossed parent mappings must not turn a PC relocation into a
    // false moved-and-deleted decision. The saved parent mapping owns the destination, identity is kept.
    public static async Task A18CrossedParentMappingsAsync()
    {
        var run = await StartAsync(baseline: true);
        var pcOne = Seed(run.Pc, "one", 0, folder: true); var dbTwo = Seed(run.Db, "two", 0, folder: true);
        var pcTwo = Seed(run.Pc, "two", 0, folder: true); var dbOne = Seed(run.Db, "one", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pcOne, dbTwo), null, null, default);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pcTwo, dbOne), null, null, default);
        var file = Seed(run.Pc, "one/x", 1); var peer = Seed(run.Db, "two/x", 1);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(file, peer), null, null, default);
        await run.Pc.MoveAsync(file, "two/x", default);
        await RepairAsync(run);
        Check.Require(Is(run.Pc, "two/x", false, 1), "The relocated PC file disappeared");
        Check.Require(Is(run.Db, "one/x", false, 1), "The move did not sync to the parent the crossed mapping names");
        Check.Require(!run.Pc.Items.ContainsKey("one/x") && !run.Db.Items.ContainsKey("two/x"), "A stale copy survived beside the mapped destination");
        Check.Require(run.Pc.History.Count == 0 && run.Db.History.Count == 0, "The crossed mapping lost a version");
        Check.Require(run.Store.Records.Values.Any(r => r.LocalPath == "two/x" && r.DropboxPath == "one/x"), "The saved binding did not follow the mapped destination");
        Check.Require(run.Store.OpenNotices.All(n => n.Cause is not (TextKey.InfoMovedDeleted or TextKey.InfoMovedTwice)),
            "The relocation took a moved-and-deleted or moved-twice decision");
    }

    // L121 (open-bug audit 20261123): a relocation under a mapped parent spelling while a Repair runs.
    // Both inputs reach one item; the final paths, contents and the single surviving version must agree.
    public static async Task A18RelocationMappedParentConcurrentRepairAsync()
    {
        var run = await StartAsync(baseline: true);
        var pcFolder = Seed(run.Pc, "Folder", 0, folder: true); var dbFolder = Seed(run.Db, "folder", 0, folder: true);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(pcFolder, dbFolder), null, null, default);
        var file = Seed(run.Pc, "Folder/x", 1); var peer = Seed(run.Db, "folder/x", 1);
        await run.Store.CommitAsync(run.Pair.PairId, null, SyncedEntries.From(file, peer), null, null, default);
        await run.Pc.MoveAsync(file, "Folder/relocated", default);
        var moved = run.Pc.Items["Folder/relocated"].Entry;
        var rename = new Change(Side.Local, ChangeKind.Renamed, "Folder/relocated", "Folder/x", moved.Id, moved, true, false, 5, JournalId: 1);
        await Task.WhenAll(
            RepairAsync(run),
            AutoSync.ApplyPageAsync(run.Pair, ItemChecks.Settings, _ => { }, run.Paths, run.Queue, Side.Local,
                ItemChecks.Journal([rename], 6), run.Pc, run.Db, run.Store, default));
        Check.Require(Is(run.Pc, "Folder/relocated", false, 1), "The relocated PC file disappeared");
        Check.Require(Is(run.Db, "folder/relocated", false, 1), "The move did not reach the mapped Dropbox spelling");
        Check.Require(!run.Pc.Items.ContainsKey("Folder/x") && !run.Db.Items.ContainsKey("folder/x"), "A duplicate or stale copy survived the concurrent run");
        Check.Require(run.Pc.History.Count == 0 && run.Db.History.Count == 0, "The relocation lost a version");
        Check.Require(run.Store.Records.Values.Count(r => r.LocalPath == "Folder/relocated" && r.DropboxPath == "folder/relocated") == 1,
            "The relocation did not settle exactly one binding at the mapped destination");
        Check.Require(run.Store.OpenNotices.Count == 0, "The concurrent Repair or page raised a notice");
    }
}
