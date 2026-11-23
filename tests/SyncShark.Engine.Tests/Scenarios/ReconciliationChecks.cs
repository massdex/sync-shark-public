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
        int links, TextKey[] notices, FirstRunPolicy policy = FirstRunPolicy.NewestWins, string[]? ignores = null, Exclusion[]? exclusions = null, bool expectMove=false, bool baselineFolder=false, bool noDeletes=false)
    {
        var pair = ItemChecks.Pair() with { FirstRun = policy, IgnoreRules = ignores ?? [], Exclusions = exclusions ?? [] };
        return global::Massdex.SyncShark.Engine.Tests.Run.AutoSyncChecks.WithinDeadline(async deadline =>
        await WithStoreAsync(pair, async store =>
        {
            var local = new FakeSide(Side.Local); var remote = new FakeSide(Side.Dropbox); Seed(local,pc); Seed(remote,cloud);
            if (baseline) await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
            if (baseline) await store.CommitAsync(pair.PairId, null, SyncedEntries.From(
                ItemChecks.File(Side.Local, Original, baselineFolder?(byte)0:(byte)1, Side.Local.ToString(), baselineFolder), ItemChecks.File(Side.Dropbox, Original, baselineFolder?(byte)0:(byte)1, Side.Dropbox.ToString(), baselineFolder)),null,null,default);
            var before=new[]{local,remote}.SelectMany(side=>side.Items.Values.Select(v=>(Side:side,v.Entry,v.Bytes))).ToArray();
            var paths=new PathScope(pair,[pair],DriveType.Fixed);
            var queue=await ItemChecks.StartQueueAsync(pair,paths,local,remote,store);
            await Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue, local, remote, store, null,null, deadline);
            foreach(var item in before.Where(x=>!x.Side.Items.Values.Any(v=>v.Entry.Id==x.Entry.Id && v.Bytes.SequenceEqual(x.Bytes))))
                Check.Require(item.Side.History.Any(v=>v.Entry==item.Entry && v.Bytes.SequenceEqual(item.Bytes)),"Displaced version is not recoverable");
            if(expectMove) Check.Require(remote.Items["renamed"].Entry.Id==before.Single(x=>x.Side==remote).Entry.Id && remote.Requests[nameof(ISide.MoveAsync)]==1 && remote.Requests[nameof(ISide.DeleteAsync)]==0 && remote.Requests[nameof(ISide.PublishAsync)]==0,"Counterpart recreated instead of moved");
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
    public static Task FirstRunAsync(FirstRunPolicy policy, byte winner) => CompareAsync(false, [new(Original,policy==FirstRunPolicy.DropboxWins?(byte)5:(byte)2)], [new(Original,4)], [new(Original,winner)], [new(Original,winner)], 1, [], policy);
    public static Task IgnoreAsync() => CompareAsync(false, [new("~$draft",1)], [], [new("~$draft",1)], [], 0, [], ignores:["FileNamePrefix:~$"]);
    public static Task ExcludeKeepAsync() => CompareAsync(false, [new("outside",1)], [], [new("outside",1)], [], 0, [], exclusions:[new(Side.Local,"outside",true)]);
    public static Task ForbiddenNameAsync() => CompareAsync(false, [], [new("CON",1)], [], [new("CON",1)], 0, [TextKey.SkipReservedName]);
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
    public static Task A02RestartAfterCreateBeforeListingAsync() => throw new NotImplementedException();
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
    public static Task A03UnequalTrustedTimesAsync() => throw new NotImplementedException();
    public static Task A03ResolveUsePcAsync() => throw new NotImplementedException();
    public static Task A03ResolveUseDropboxAsync() => throw new NotImplementedException();
    public static Task A03ResolveKeepBothAsync() => throw new NotImplementedException();
    public static Task A03ResolveRenamePcAsync() => throw new NotImplementedException();
    public static Task A03ResolveRenameDropboxAsync() => throw new NotImplementedException();
    public static Task A03FourEndpointNoticeAsync() => throw new NotImplementedException();
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
    public static Task A20ResolveKeepPcPlaceAsync() => throw new NotImplementedException();
    public static Task A20ResolveKeepDropboxPlaceAsync() => throw new NotImplementedException();
    public static Task A20ResolveKeepBothPlacesAsync() => throw new NotImplementedException();
    public static Task A20ChoiceSeesReplacedPcOccupantAsync() => throw new NotImplementedException();
    public static Task A20ChoiceSeesReplacedCloudOccupantAsync() => throw new NotImplementedException();
    public static Task A22MoveOntoDifferentFileAsync() => throw new NotImplementedException();
    public static Task A22CreateOntoIdenticalFileAsync() => throw new NotImplementedException();
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
    public static Task A27CloudOnlyCapitalRenameAsync() => throw new NotImplementedException();
    public static Task A27BothPhysicalCaseHopsAsync() => throw new NotImplementedException();
    public static Task A27UnavailableRemainingHopAsync() => throw new NotImplementedException();
    public static Task A27NoTemporaryBindingAsync() => throw new NotImplementedException();
    public static Task A28ExcludeKeepPcAsync() => throw new NotImplementedException();
    public static Task A28ExcludeKeepCloudAsync() => throw new NotImplementedException();
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
    public static Task A33DepartureBeforeArrivalAsync() => throw new NotImplementedException();
    public static Task A33EarlierPrerequisiteBeforeDepartureAsync() => throw new NotImplementedException();
    public static Task A33NoReciprocalWaitAsync() => throw new NotImplementedException();
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
    public static Task A34FileFolderNewestAsync() => throw new NotImplementedException();
    public static Task A34FileFolderPcWinsAsync() => CompareAsync(false,
        [new(Original,2)],[new(Original,0,true)],[new(Original,2)],[new(Original,2)],1,[],FirstRunPolicy.LocalWins);
    public static Task A34FileFolderDropboxWinsAsync() => CompareAsync(false,
        [new(Original,2)],[new(Original,0,true)],[new(Original,0,true)],[new(Original,0,true)],1,[],FirstRunPolicy.DropboxWins);
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
}
