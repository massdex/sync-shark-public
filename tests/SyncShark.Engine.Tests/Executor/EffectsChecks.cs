using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Planner;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Check bytes, binding and the distinction between a pending, refused and completed action.</summary>
public static class EffectsChecks
{
    public static async Task RunAsync()
    {
        var pc = new FakeSide(Side.Local); var db = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); var pair = ItemChecks.Pair();
        var a = ItemChecks.File(Side.Local,"item",7,"pc"); pc.Seed(a,[7]);
        var row = new Row(a.Path,a,null,null,[],a.ContentHash,RowContext.Neutral);
        var effects = new Effects();
        Task<EffectOutcome> Apply(SyncAction action) => effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),action,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(await Apply(new(ActionKind.None,row,NoneReason.Unfinished)) == EffectOutcome.Pending && store.Records.Count == 0,"Unfinished input must not settle");
        Check.Require(await Apply(new(ActionKind.Notice,row,NoticeKind:NoticeKind.Failure,Cause:TextKey.FailChanged)) == EffectOutcome.Refused && store.OpenNotices.Count == 1 && store.Records.Count == 0,"Notice-only refusal must not settle");
        Check.Require(await Apply(new(ActionKind.Upload,row)) == EffectOutcome.Applied && db.Items[a.Path].Bytes.SequenceEqual(new byte[]{7}) && store.Records.Values.Single() == SyncedEntries.From(a,db.Items[a.Path].Entry),"Upload must publish before settlement");
        Check.Require(await Apply(new(ActionKind.Upload,row))==EffectOutcome.Applied && db.Items.Count==1 && db.History.Count==0,"Create-only collision with identical bytes must adopt, not overwrite");
        // Only the source metadata re-read fails; opening its bytes and staging them on Dropbox must succeed.
        var source=ItemChecks.File(Side.Local,"p",5,"pc-source"); var victim=ItemChecks.File(Side.Dropbox,"p",0,"db-folder",true);
        pc.Seed(source,[5]); db.Seed(victim,[]);
        var bare=new ChangedUnderneathException("p"); pc.ReadFailures.Add("p",bare);
        var replace=new SyncAction(ActionKind.ReplaceKind,new("p",source,victim,null,[],source.ContentHash,RowContext.Neutral),Side:Side.Dropbox);
        var stages=db.Requests[nameof(ISide.StageAsync)];
        try { await Apply(replace); throw new InvalidDataException("Source re-read failure was ignored"); }
        catch(ChangedUnderneathException ex)
        {
            Check.Require(ex.Path==source.Path && ex.FailedSide==Side.Local,"PC source re-read during Dropbox folder replacement lost its endpoint");
        }
        Check.Require(db.Requests[nameof(ISide.StageAsync)]==stages+1 && db.Items[victim.Path].Entry==victim && db.Requests[nameof(ISide.DeleteAsync)]==0,"Source re-read refusal changed the selected folder or did not reach staging");
        pc.ReadFailures.Remove("p");
        // Identical relative path, opposite selection: the actual reader, not a fixed PC tag, wins.
        var remoteSource=source with {Id="db-source",Revision="r5"}; var pcFolder=victim with {Id="pc-folder"};
        var otherPc=new FakeSide(Side.Local); var otherDb=new FakeSide(Side.Dropbox);
        otherPc.Seed(pcFolder,[]); otherDb.Seed(remoteSource,[5]);
        otherDb.ReadFailures["p"]=new ChangedUnderneathException("p");
        try
        {
            await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.ReplaceKind,new("p",pcFolder,remoteSource,null,[],null,RowContext.Neutral),Side:Side.Local),otherPc,otherDb,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
            throw new InvalidDataException("Dropbox source re-read failure was ignored");
        }
        catch(ChangedUnderneathException ex) { Check.Require(ex.Path==remoteSource.Path && ex.FailedSide==Side.Dropbox,"Dropbox source re-read was attributed to the PC"); }
        Check.Require(otherPc.Requests[nameof(ISide.StageAsync)]==1 && otherPc.Items["p"].Entry==pcFolder && otherPc.Requests[nameof(ISide.DeleteAsync)]==0,"Dropbox failure removed the selected PC folder");
        var folder = ItemChecks.File(Side.Dropbox,"other",0,"cloud-folder",true); db.Seed(folder,[]);
        var peer = ItemChecks.File(Side.Local,"OTHER",0,"pc-folder",true); pc.Seed(peer,[]);
        var old = SyncedEntries.From(peer with {Path="other"},folder);
        await store.CommitAsync(pair.PairId,null,old,null,null,default);
        db.MoveOutcomes.Enqueue(new(RemovalOutcome.Unknown,null,TextKey.FailChanged));
        var move = new SyncAction(ActionKind.MoveDropbox,new(old.LocalKey,peer,folder,old,[],null,RowContext.Neutral),Target:peer.Path);
        try { await Apply(move); throw new InvalidDataException("Unknown move lost the selected identity and goal"); }
        catch (ItemFailedException ex) when (ex.Disposition==FailureDisposition.UnknownEffect && ex.Move?.ExpectedId==folder.Id && ex.Move.Destination==peer.Path && ex.Cause==TextKey.FailChanged) { }
        Check.Require(store.Records[(pair.PairId,old.LocalKey)]==old && db.Items[folder.Path].Entry==folder,"Unconfirmed move projected the old binding");
        db.MoveOutcomes.Enqueue(new(RemovalOutcome.NoEffect,null,TextKey.FailChanged));
        try { await Apply(move); throw new InvalidDataException("Definite move refusal was accepted"); }
        catch (ItemFailedException ex) { Check.Require(ex.Cause==TextKey.FailChanged && ex.Disposition==FailureDisposition.Permanent && ex.Move==null,"Definite Dropbox refusal was stored as an uncertain move"); }
        var unreadable = peer with {Skip=nameof(TextKey.SkipUnreadable)};
        pc.Items[peer.Path] = (unreadable,[]);
        try
        {
            await Effects.DecideMoveAsync(pair,ItemChecks.Settings,new(pair,[pair],DriveType.Fixed),[new(Side.Local,peer,old)],pc,db,store,false,default);
            throw new InvalidDataException("Unreadable move observation became an ordinary hold");
        }
        catch (ItemFailedException ex) when (ex.Path==peer.Path && ex.Cause==TextKey.ReadLocalFailed) { }
        // A bin-only file is not a live conflict row. BC5 must restore/checkpoint it or retain its PutBack notice.
        var held=ItemChecks.File(Side.Local,"lost",11,"binned"); pc.History.Add((held,[11]));
        var recovered=await effects.RecoverConflictAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new("lost","lost"),Side.Local,held,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(recovered==EffectOutcome.Pending && !pc.Items.ContainsKey("lost") && !db.Items.ContainsKey("lost"),"Ordinary conflict independently staged a bin-only last copy");
        var obsolete=ItemChecks.File(Side.Local,"dir/old",7,"obsolete"); var incumbent=ItemChecks.File(Side.Local,"dir/new",7,"incumbent");
        pc.Seed(obsolete,[7]); pc.Seed(incumbent,[7]); var peerFile=ItemChecks.File(Side.Dropbox,"dir/new",7,"keeper"); db.Seed(peerFile,[7]);
        var selected=new RemovalSelection(new(obsolete,Side.Local.ToString(),obsolete.ContentHash),null);
        await store.ArmAsync(pair.PairId,selected,null,default);
        var settled=await effects.CollisionAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),Side.Local,obsolete,incumbent,peerFile,null,pc,db,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(settled==EffectOutcome.Applied && await store.ArmedAsync(pair.PairId,default)==null,"Successful PC collision cleanup stranded its armed note");
        await store.ArmAsync(pair.PairId,new(new(incumbent,Side.Local.ToString(),incumbent.ContentHash),null),null,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default) is {} next && next.Victim.Proof.Expected.Id==incumbent.Id,"Next independent removal could not arm");
        var stale=ItemChecks.File(Side.Dropbox,"vacant",0,"absent-folder",true);
        var former=SyncedEntries.From(stale with {Path="another",Id="owner"},stale);
        await store.CommitAsync(pair.PairId,null,former,null,null,default);
        var mutations=db.Mutations.Count;
        try { await Apply(move with {Target=stale.Path}); }
        finally { Check.Require(!db.Items.ContainsKey(stale.Path) && db.Mutations.Count==mutations,"Folder move wrote the vacant physical target before its saved owner was decided"); }
        if (store.Records.Values.Contains(former)) await store.CommitAsync(pair.PairId,former,null,null,null,default);
        var destination=ItemChecks.File(Side.Dropbox,"occupied",0,"destination",true); db.Seed(destination,[]);
        var owner=SyncedEntries.From(destination with {Path="another",Id="owner"},destination);
        await store.CommitAsync(pair.PairId,null,owner,null,null,default);
        try { await Apply(move with {Target=destination.Path}); throw new InvalidDataException("Foreign folder destination reached recursive merge"); }
        catch(ItemFailedException error) when(error.Path==destination.Path && error.Cause==TextKey.ClashNameTaken) { }
        Check.Require(db.Items[destination.Path].Entry==destination && db.Items[folder.Path].Entry==folder && store.Records.Values.Contains(owner),"Folder incumbent or its binding changed on refusal");
        var late=ItemChecks.File(Side.Local,"late",7,"late-id"); pc.Seed(late,[7]);
        var prior=SyncedEntries.From(late,late with {Id="cloud-late"});
        await store.CommitAsync(pair.PairId,null,prior,null,null,default); bool consulted=false;
        try
        {
            await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.DeleteLocal,new(prior.LocalKey,late,null,prior,[],null,RowContext.Neutral)),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,scope=>
            { consulted=scope.LocalPath==late.Path && !pc.Items.ContainsKey(late.Path); throw new IOException("Held evidence unreadable"); });
        }
        catch(IOException ex) when(ex.Message=="Held evidence unreadable") { }
        Check.Require(consulted && store.Records.Values.Contains(prior),"Successful single-file recycle retired the binding before reading the held evidence");
    }
}
