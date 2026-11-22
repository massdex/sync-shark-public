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
