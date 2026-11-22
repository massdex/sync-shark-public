namespace Massdex.SyncShark.Engine.Tests.Contracts;

/// <summary>Inner roots and the drive-root bin are held out without hiding similarly named siblings.</summary>
public static class PathScopeChecks
{
    public static Task RunAsync()
    {
        const string root = @"T:\";
        var outer = new PairConfig("a", "A", root, "/A", "test", [], [], FirstRunPolicy.NewestWins, true, null);
        var inner = outer with { PairId = "b", LocalRoot = Path.Combine(root, "Inner"), DropboxRoot = "/A/RemoteInner" };
        var scope = new PathScope(outer, [outer, inner], volumeBin: Path.Combine(root, ResourceNames.RootBinFolder));
        Check.Require(scope.Excludes("inner/file", null, false) && scope.Excludes(null, "RemoteInner/file", false), "Nested roots");
        Check.Require(scope.Excludes("Recycle Bin/item", null, false) && !scope.Excludes("Recycle Bin Extra/item", null, false), "Volume bin boundary");
        Check.Require(!scope.Excludes(null,"Inner/file",false) && !scope.Excludes("RemoteInner/file",null,false),"Unrelated relative spelling crossed endpoints");
        Check.Require(!scope.Excludes(null,"Recycle Bin/item",false),"PC bin leaked into Dropbox matcher");
        Check.Require(scope.Excludes("Inner/file","Elsewhere/file",false),"Mapped PC boundary lost");
        var unrelated = new Change(Side.Dropbox,ChangeKind.Changed,"Inner/file",null,"db",null,false,false);
        Check.Require(!scope.ExcludesChange(unrelated,0,static (_,_,_)=>null),"Unbound change inferred a PC boundary from a Dropbox name");
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
        Check.Require(!await store.FinishRepairAsync(pair.PairId,Side.Dropbox,original.Revision,default,a),"Old comparison consumed reissued scope");
        var current=await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default);
        Check.Require(await store.FinishRepairAsync(pair.PairId,Side.Dropbox,current,default,a),"Matching completion refused");
        Check.Require((await store.PairAsync(pair.PairId,default))!.OwedScopes!.SequenceEqual([b]),"Wrong mapped footprint cleared");
        Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason=="requested","Scoped completion cleared whole-pair debt");
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
        Check.Require(!await store.FinishRepairAsync(pair.PairId,Side.Dropbox,old,default,completedRepairJson:"{}"),"Stale completion published first Repair");
        Check.Require(await store.FinishRepairAsync(pair.PairId,Side.Dropbox,current,default,completedRepairJson:"{}"),"Current completion refused");
        Check.Require(await store.RepairStateAsync(pair.PairId,default)==(true,"{}"),"Empty completed pair lost first-run state or summary");
        await store.SaveCursorAsync(pair.PairId,"feed",default);await store.PublishDeletedHistoryAsync(pair.PairId,"history",default);
        Check.Require(await store.CursorAsync(pair.PairId,default)=="feed" && await store.DeletedHistoryAsync(pair.PairId,default)==("history",false),"Feed and history continuations were collapsed");
    }
}
