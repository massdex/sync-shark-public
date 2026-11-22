namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Inner roots and the drive-root bin are held out without hiding similarly named siblings.</summary>
public static class PathScopeChecks
{
    public static Task RunAsync()
    {
        const string root = @"T:\";
        var outer = new PairConfig("a", "A", root, "/A", "test", [], [], FirstRunPolicy.NewestWins, true, null);
        var inner = outer with { PairId = "b", LocalRoot = Path.Combine(root, "Inner"), DropboxRoot = "/A/RemoteInner" };
        var scope = new PathScope(outer, [outer, inner], DriveType.Removable);
        Check.Require(scope.Excludes("inner/file", null, false) && scope.Excludes(null, "RemoteInner/file", false), "Nested roots");
        Check.Require(scope.Excludes("Recycle Bin/item", null, false) && !scope.Excludes("Recycle Bin Extra/item", null, false), "Volume bin boundary");
        Check.Require(!scope.Excludes(null,"Inner/file",false) && !scope.Excludes("RemoteInner/file",null,false),"Unrelated relative spelling crossed endpoints");
        Check.Require(!scope.Excludes(null,"Recycle Bin/item",false),"PC bin leaked into Dropbox matcher");
        Check.Require(scope.Excludes("Inner/file","Elsewhere/file",false),"Mapped PC boundary lost");
        var incoming = new Change(Side.Dropbox,ChangeKind.Changed,"Inner/file",null,"db",null,false,false);
        Check.Require(scope.ExcludesChange(incoming,0,static (_,_,_)=>null),"Unbound change entered PC nested root");
        Check.Require(scope.ExcludesChange(incoming with { Path="Recycle Bin/item" },0,static (_,_,_)=>null),"Unbound change entered the PC bin");
        var parent = new SyncedEntry("INNER","Inner","CLOUD","Cloud",true,"local",0,0,"cloud","");
        Check.Require(scope.ExcludesChange(incoming with { Path="Cloud/new" },parent,static (p,side,key)=>side==Side.Dropbox && key==p.DropboxKey?p:null),"Mapped child entered nested root");
        var saved = parent with { LocalKey="OUTSIDE/FILE",LocalPath="Outside/file",DropboxKey="INNER/FILE",DropboxPath="Inner/file" };
        Check.Require(!scope.ExcludesChange(incoming,saved,static (p,side,key)=>side==Side.Dropbox && key==p.DropboxKey?p:null),"Coincidental spelling excluded a safe saved endpoint");
        Check.Require(!new PathScope(outer,[outer],DriveType.Fixed).ExcludesChange(incoming with { Path="Recycle Bin/item" },0,static (_,_,_)=>null),"Native-bin volume treated as removable");
        Check.Require(!scope.Excludes("innermost/file", null, false), "Unrelated sibling");
        return Task.CompletedTask;
    }
    /// <summary>A stale completion, an unrelated scope and an uncompleted full-pair Repair all keep their independent debt.</summary>
    public static async Task MatchingRevisionAsync()
    {
        using var store=new TestDoubles.InMemoryStateStore();
        var pair=Scenarios.ItemChecks.Pair(); var a=new Scope("Held","Mapped");var b=new Scope("Other","Other");
        await store.SavePairAsync(pair,default,[(pair.PairId,a),(pair.PairId,a),(pair.PairId,b)]);
        var original=await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default);
        var edited=(await store.PairAsync(pair.PairId,default))!;
        Check.Require(edited.OwedScopes!.Count==2,"Scope set is not distinct");
        await store.SavePairAsync(edited,default,[(pair.PairId,a)]);
        Check.Require(!await store.FinishRepairAsync(pair.PairId,a,original.Revision,default),"Old comparison consumed reissued scope");
        var current=await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default);
        Check.Require(await store.FinishRepairAsync(pair.PairId,a,current,default),"Matching completion refused");
        Check.Require((await store.PairAsync(pair.PairId,default))!.OwedScopes!.SequenceEqual([b]),"Wrong mapped footprint cleared");
        Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason=="requested","Scoped completion cleared whole-pair debt");
        var inner=pair with { PairId="inner" };await store.SavePairAsync(inner,default);
        var ordinary=new Notice(1,NoticeKind.Failure,TextKey.PublishFailed,"file",new([new(Side.Local,"file","id")],Side.Local),DateTimeOffset.UnixEpoch,false);
        var putBack=ordinary with { Id=2,Cause=TextKey.FailPutBack,Path="edit",Subject=new([new(Side.Local,"edit","edit-id")],Side.Local) };
        await store.SaveNoticeAsync(inner.PairId,ordinary,default);await store.SaveNoticeAsync(inner.PairId,putBack with { Ignored=true },default);
        Check.Require(await store.RemovePairAsync(inner.PairId,default,[(pair.PairId,a)])==TextKey.FailPutBack && !(await store.PairAsync(pair.PairId,default))!.OwedScopes!.Contains(a),"Removal released an outstanding put-back or published its scope");
        await store.CloseNoticeAsync(inner.PairId,putBack,default);
        var job=new AcceptedJob(inner.PairId,"route","token",[]);await store.SaveJobAsync(job,default);
        Check.Require(await store.RemovePairAsync(inner.PairId,default)==TextKey.PairJobOutstanding,"Accepted job lost its removal reason");await store.SettleJobAsync(job,default);
        var note=new RemovalSelection(new(Scenarios.ItemChecks.File(Side.Local,"folder",0,"id",true),"volume",null),null);await store.ArmAsync(inner.PairId,note,null,default);
        Check.Require(await store.RemovePairAsync(inner.PairId,default)==TextKey.PairPcNoteOutstanding,"Armed PC note lost its removal reason");await store.DisarmAsync(inner.PairId,note,null,default);
        Check.Require(await store.RemovePairAsync(inner.PairId,default,[(pair.PairId,a)]) is null && (await store.PairAsync(pair.PairId,default))!.OwedScopes!.Contains(a),"Ordinary failure blocked removal or its returned scope");
    }
    public static Task FolderProofAsync()
    {
        var folder=Scenarios.ItemChecks.File(Side.Local,"victim",0,"folder-id",true);
        var note=new RemovalSelection(new(folder,"volume",null),null);
        Check.Require(note.Proof.Expected==folder && note.Proof.ContentHash==null,"Folder selection fabricated file proof");
        var stub=folder with { IsFolder=false, Skip=nameof(TextKey.SkipOnlineOnly) };
        Check.Require(new RemovalProof(stub,"volume",null).Expected==stub,"Dehydrated metadata proof rejected");
        try { _=new StageProof("stage","volume","stage-id",1,"bad"); throw new InvalidDataException("Malformed stage proof accepted"); } catch(ArgumentException) { }
        try { _=new RemovalProof(folder with { IsFolder=false },"volume",null); }
        catch(ArgumentException) { return Task.CompletedTask; }
        throw new InvalidDataException("File victim accepted without content proof");
    }
    /// <summary>Empty pair state survives completing its first full Repair, without promoting partial/scoped work.</summary>
    public static async Task CompletedRepairFactsAsync()
    {
        using IStore store=new TestDoubles.InMemoryStateStore();var pair=Scenarios.ItemChecks.Pair();await store.SavePairAsync(pair,default);
        Check.Require(!(await store.RepairStateAsync(pair.PairId,default)).FirstRepairDone,"New pair was already repaired");
        var old=await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default);
        var current=await store.OweRepairAsync(pair.PairId,Side.Dropbox,"gap",default);
        Check.Require(!await store.FinishRepairAsync(pair.PairId,(0,old),"{}",default),"Stale completion published first Repair");
        Check.Require(await store.FinishRepairAsync(pair.PairId,(0,current),"{}",default),"Current completion refused");
        Check.Require(await store.RepairStateAsync(pair.PairId,default)==(true,"{}"),"Empty completed pair lost first-run state or summary");
        await store.SaveCursorAsync(pair.PairId,"feed",default);var scan=ResourceNames.NewScanToken();
        static async IAsyncEnumerable<Listed> Empty() { await Task.CompletedTask;yield break; }
        await store.AddScanAsync(pair.PairId,scan,Side.Dropbox,Empty(),()=>"history",default);
        await store.PublishDeletedHistoryAsync(pair.PairId,scan,default);
        Check.Require(await store.CursorAsync(pair.PairId,default)=="feed" && await store.DeletedHistoryAsync(pair.PairId,default)==("history",false),"Feed and history continuations were collapsed");
    }
    public static async Task AcquisitionIsolationAsync()
    {
        using var fake=new TestDoubles.InMemoryStateStore();IStore store=fake;var pair=Scenarios.ItemChecks.Pair();
        var first=ResourceNames.NewScanToken();var second=ResourceNames.NewScanToken();
        static async IAsyncEnumerable<Listed> Listing(string path) { await Task.CompletedTask;yield return new Listed(path,Scenarios.ItemChecks.File(Side.Local,path,1,path)); }
        await store.AddScanAsync(pair.PairId,first,Side.Local,Listing("first"),null,default);
        await store.AddScanAsync(pair.PairId,second,Side.Local,Listing("second"),null,default);
        var discovery=ResourceNames.NewScanToken();var key=PathNames.DropboxKey("unbound");
        static async IAsyncEnumerable<Listed> Tombstone() { await Task.CompletedTask;yield return new Listed("unbound",null); }
        await store.AddScanAsync(pair.PairId,discovery,Side.Dropbox,Tombstone(),()=>"terminal",default);
        try { await store.PublishDeletedHistoryAsync(pair.PairId,first,default); throw new InvalidDataException("Ordinary scan published history"); } catch(InvalidOperationException) { }
        await store.ClearScanAsync(pair.PairId,first,default);
        Check.Require(await store.DeletedNameAsync(pair.PairId,discovery,key,default) && !await store.DeletedNameAsync(pair.PairId,second,key,default),"Another scan cleared or read discovery names");
        await store.PublishDeletedHistoryAsync(pair.PairId,discovery,default);
        Check.Require(await store.DeletedNameAsync(pair.PairId,null,key,default) && (await store.DeletedHistoryAsync(pair.PairId,default)).Cursor=="terminal","History names or acquisition cursor lost");
        var rows=new List<Row>();await foreach(var row in store.CompareAsync(pair.PairId,second,null,(_,_) => (true,false),default)) rows.Add(row.Row);
        Check.Require(rows.Count==1 && rows[0].Local?.Path=="second","Clearing one acquisition changed another acquisition");
        var pc=Scenarios.ItemChecks.File(Side.Local,"old",1,"pc-id");var db=Scenarios.ItemChecks.File(Side.Dropbox,"original",1,"db-id");
        var boundary=new JournalPosition(3,17);var binding=SyncedEntries.From(pc,db) with { LocalPublished=boundary };
        await store.CommitAsync(pair.PairId,null,binding with { LocalPublished=null },boundary,null,default);
        Check.Require((await store.FindAsync(pair.PairId,Side.Local,binding.LocalKey,default))?.LocalPublished==boundary,"Binding lost its publication boundary");
        static async IAsyncEnumerable<Listed> Moved(Entry entry) { await Task.CompletedTask; yield return new Listed(entry.Path,entry); }
        await store.AddScanAsync(pair.PairId,second,Side.Local,Moved(pc with {Path="z/file"}),null,default);
        await store.AddScanAsync(pair.PairId,second,Side.Dropbox,Moved(db with {Path="a/file"}),null,default);
        foreach(var path in new[]{"AliasA","AliasB","LetterCase"})
        {
            var alias=pc with { Path=path, Id=path=="LetterCase"?"case-id":"shared-id" };
            await store.CommitAsync(pair.PairId,null,SyncedEntries.From(alias,db with {Path=path,Id=path}),null,null,default);
            await store.AddScanAsync(pair.PairId,second,Side.Local,Moved(alias with {Path=path=="LetterCase"?"lettercase":path}),null,default);
        }
        var moves=new List<RenameCandidate>();await foreach(var move in store.RenamesAsync(pair.PairId,second,null,default))moves.Add(move);
        Check.Require(moves.Count==3 && moves.Count(m=>m.Moved.Path=="lettercase" && m.Synced.LocalPath=="LetterCase")==1 && moves.Where(m=>m.Synced==binding).Select(m=>m.Side).SequenceEqual(new[]{Side.Local,Side.Dropbox}),"Rename projection lost or split a binding’s two sides");
        var movedPc=pc with { Path="renamed" };var relink=SyncedEntries.Relink(binding,movedPc,db);
        await store.CommitAsync(pair.PairId,binding,relink,null,null,default);
        Check.Require((await store.FindAsync(pair.PairId,Side.Local,relink.LocalKey,default))?.LocalPublished==boundary,"Committed same-file move lost its publication boundary");
        var replaced=SyncedEntries.Relink(relink,movedPc with { Id="replacement" },db);
        await store.CommitAsync(pair.PairId,relink,replaced,null,null,default);
        Check.Require(await store.FindAsync(pair.PairId,Side.Local,replaced.LocalKey,default) is { LocalPublished:null,LocalId:"replacement" },"Replacement inherited an old publication boundary");
        await store.CommitAsync(pair.PairId,null,null,null,spentDeletedKey:key,ct:default);
        Check.Require(!await store.DeletedNameAsync(pair.PairId,discovery,key,default),"Discovery returned a spent name");
        await store.ClearScanAsync(pair.PairId,discovery,default);
        Check.Require(!await store.DeletedNameAsync(pair.PairId,null,key,default),"Unbound first-run history was not spent");
    }
    public static async Task SourceResetAsync()
    {
        using IStore store=new TestDoubles.InMemoryStateStore();var a=Scenarios.ItemChecks.Pair();var b=a with {PairId="other"};
        await store.SavePairAsync(a,default);await store.SavePairAsync(b,default);var boundary=new JournalPosition(2,9);
        await store.ResetJournalAsync("volume",boundary,[a.PairId,b.PairId],"gap",default);
        Check.Require(await store.PositionAsync("volume",default)==boundary && await store.ReadRepairAsync(a.PairId,Side.Local,default)==(1L,"gap") && await store.ReadRepairAsync(b.PairId,Side.Local,default)==(1L,"gap"),"Volume reset lost a member obligation");
        try { await store.ResetJournalAsync("volume",new(3,1),[a.PairId,"unknown"],"new gap",default);throw new InvalidDataException("Reset accepted an unknown pair"); } catch(InvalidDataException error) when(error.Message=="Unknown volume member") { }
        Check.Require(await store.PositionAsync("volume",default)==boundary && (await store.ReadRepairAsync(a.PairId,Side.Local,default)).Revision==1,"Failed reset partially committed");
        await store.ResetCursorAsync(a.PairId,"new-base","reset",default);
        Check.Require(await store.CursorAsync(a.PairId,default)=="new-base" && await store.ReadRepairAsync(a.PairId,Side.Dropbox,default)==(1L,"reset") && (await store.ReadRepairAsync(b.PairId,Side.Dropbox,default)).Revision==0,"Dropbox reset reached wrong pair");
    }
}
