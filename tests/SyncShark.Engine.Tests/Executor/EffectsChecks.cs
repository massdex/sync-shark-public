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
        Notice? delivered=null; int calls=0; bool alreadySaved=false;
        var debt=IssueSubject.FirstRunDeletionKind.UnscannedSubtree;
        var planned=new SyncAction(ActionKind.Notice,row with {Context=row.Context with {FirstRunDeletion=debt}},Side:Side.Local,NoticeKind:NoticeKind.Failure,Cause:TextKey.FailChanged);
        var refused=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),planned,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failedNotice:n=>{calls++;delivered=n;alreadySaved=store.OpenNotices.Count==1;});
        var savedNotice=store.OpenNotices.Single();
        Check.Require(refused==EffectOutcome.Refused && calls==1 && alreadySaved && delivered!=null && savedNotice.Matches(delivered with {Id=savedNotice.Id}) && delivered.Subject.FirstRunDeletion==debt && delivered.Subject.Items.Single()==new NoticeItem(Side.Local,a.Path,a.Id) && store.Records.Count==0,"Planned failure did not deliver the saved envelope before refusal");
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
        var noFence=ItemChecks.File(Side.Local,"no-fence",7,"victim"); pc.Seed(noFence,[7]);
        var missing=new SyncAction(ActionKind.DeleteLocal,new(noFence.Path,noFence,null,null,[],noFence.ContentHash,RowContext.Neutral));
        var pending=await Apply(missing);
        Check.Require(pending==EffectOutcome.Pending && pc.Items[noFence.Path].Entry==noFence,"Missing acquisition ownership allowed a PC recycle");
        var click=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),missing,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,userSelected:true);
        Check.Require(click==EffectOutcome.Applied && !pc.Items.ContainsKey(noFence.Path) && pc.History.Any(x=>x.Entry==noFence && x.Bytes.SequenceEqual(new byte[]{7})),"Reviewed file removal waited for a reader or lost its recycled version");
        pc.Seed(noFence,[7]);
        pc.Pages.Enqueue(new([],null,new(1,2))); db.Pages.Enqueue(new([],"after",null));
        bool queried=false;
        pending=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),missing,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,_=>{queried=true;return [];},_=>false);
        Check.Require(pending==EffectOutcome.Pending && !queried && !pc.Items.ContainsKey(noFence.Path),"An uncovered post-recycle snapshot counted as acquired input");
    }

    /// <summary>Replay a changed file under relocated parents, preserving the independent occupant of the old name.</summary>
    public static async Task ChangedChildTargetAsync()
    {
        var pair=ItemChecks.Pair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        var oldPc=ItemChecks.File(Side.Local,"before/child",1,"pc"); var oldDb=ItemChecks.File(Side.Dropbox,"prior/child",1,"cloud");
        var saved=SyncedEntries.From(oldPc,oldDb); await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var edited=ItemChecks.File(Side.Local,"after/child",9,oldPc.Id); var receiving=oldDb with {Path="final/child"};
        var successor=ItemChecks.File(Side.Dropbox,oldDb.Path,5,"successor"); pc.Seed(edited,[9]); db.Seed(receiving,[1]); db.Seed(successor,[5]);
        var row=new Row(saved.LocalKey,edited,receiving,saved,[],edited.ContentHash,RowContext.Neutral with {CompleteInventory=true});
        const string wrong="Changed-child replay used the old saved path instead of the verified receiving endpoint";
        var paths=new PathScope(pair,[pair],DriveType.Fixed); bool held=false;
        try { await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row with {Context=RowContext.Neutral},[],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,failed:(_,_)=>held=true); }
        catch(InventoryIncompleteException) when(held) { }
        Check.Require(held && store.Records.Values.Single()==saved && db.Items[receiving.Path].Bytes.SequenceEqual(new byte[]{1}),"Unsettled child was published before its parent projection");
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,row,[],pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(db.Items.TryGetValue(receiving.Path,out var current) && current.Entry.Id==receiving.Id && current.Bytes.SequenceEqual(new byte[]{9}) &&
            db.Items.TryGetValue(oldDb.Path,out var kept) && kept.Entry==successor && kept.Bytes.SequenceEqual(new byte[]{5}) &&
            !db.Mutations.Contains(successor.Id) && store.Records.Values.SingleOrDefault()==SyncedEntries.From(edited,current.Entry),wrong);
    }

    public static async Task ForeignDestinationAsync()
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair();
        var folder=ItemChecks.File(Side.Dropbox,"source",0,"moving",true); db.Seed(folder,[]);
        var peer=ItemChecks.File(Side.Local,"vacant",0,"peer",true);pc.Seed(peer,[]);
        var old=SyncedEntries.From(peer with {Path=folder.Path},folder);await store.CommitAsync(pair.PairId,null,old,null,null,default);
        var move=new SyncAction(ActionKind.MoveDropbox,new(old.LocalKey,peer,folder,old,[],null,RowContext.Neutral),Target:peer.Path);
        Task<EffectOutcome> Apply(SyncAction action)=>new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),action,pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        var stale=ItemChecks.File(Side.Dropbox,"vacant",0,"absent-folder",true);
        var former=SyncedEntries.From(stale with {Path="another",Id="owner"},stale);
        await store.CommitAsync(pair.PairId,null,former,null,null,default);
        var mutations=db.Mutations.Count;
        try { Check.Require(await Apply(move with {Target=stale.Path})==EffectOutcome.Pending,"Vacating a foreign key must require fresh comparison"); }
        finally { Check.Require(!db.Items.ContainsKey(stale.Path) && db.Mutations.Count==mutations,"Folder move wrote the vacant physical target before its saved owner was decided"); }
        Check.Require(!store.Records.Values.Contains(former),"Planner did not vacate the proven-gone owner before retry");
        foreach(var written in new[]{Side.Dropbox,Side.Local})
        foreach(var scenario in new[]{"equal","different","edited","uncovered","refused","cancelled","late"})
        {
            var dir=Directory.CreateTempSubdirectory();
            try
            {
                using var links=await Massdex.SyncShark.Engine.Storage.Store.OpenAsync(dir.FullName,true,ItemChecks.Settings,default) ?? throw new InvalidDataException();
                await links.SavePairAsync(pair,default);
                var left=new FakeSide(Side.Local);var right=new FakeSide(Side.Dropbox);
                left.Pages.Enqueue(new([],null,new(1,2)));right.Pages.Enqueue(new([],"after",null));
                byte occupantByte=scenario is "different" or "edited"?(byte)8:(byte)7;
                var incoming=PathNames.Other(written); var to=written==Side.Local?left:right; var from=written==Side.Local?right:left;
                var a=ItemChecks.File(written,"old",7,"arriving-selected");var d=ItemChecks.File(incoming,"final",7,"arriving-moved");
                var b=ItemChecks.File(written,d.Path,occupantByte,"incumbent");var c=ItemChecks.File(incoming,"separate",occupantByte,"partner");
                var arrival=SyncedEntries.From(written,a,d with {Path=a.Path});var owner=SyncedEntries.From(written,b,c);
                byte content=scenario=="edited"?(byte)9:(byte)7;
                if(scenario=="edited") d=ItemChecks.File(incoming,d.Path,content,d.Id);
                to.Seed(a,[7]);from.Seed(d,[content]);to.Seed(b,[occupantByte]);from.Seed(c,[occupantByte]);
                await links.CommitAsync(pair.PairId,null,arrival,null,null,default);await links.CommitAsync(pair.PairId,null,owner,null,null,default);
                var pcArrival=written==Side.Local?a:d;var dbArrival=written==Side.Dropbox?a:d;
                var action=new SyncAction(written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,new(arrival.LocalKey,pcArrival,dbArrival,arrival,[],null,RowContext.Neutral),Target:b.Path);
                using var cancel=new CancellationTokenSource();
                if(scenario=="refused") right.RecycleRefusals[dbArrival.Path]=new ItemFailedException(dbArrival.Path,TextKey.FailChanged,null);
                bool Covers((JournalPosition? Local,string? Dropbox) fence)
                {
                    if(scenario=="cancelled") cancel.Cancel();
                    if(scenario=="late") right.Items[dbArrival.Path]=(ItemChecks.File(Side.Dropbox,dbArrival.Path,9,dbArrival.Id),[9]);
                    return scenario!="uncovered" && fence.Local==new JournalPosition(1,2) && fence.Dropbox=="after";
                }
                const string wrong="Bound collision did not settle the arrival independently of the incumbent pair";
                EffectOutcome outcome;
                try { outcome=await new Effects().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),action,left,right,links,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),cancel.Token,_=>[],Covers,grantedIncumbent:owner); }
                catch(OperationCanceledException) when(cancel.IsCancellationRequested) {outcome=EffectOutcome.Pending;}
                catch(ItemFailedException e) when(scenario=="refused" && e.Path==dbArrival.Path && e.Cause==TextKey.FailChanged) {outcome=EffectOutcome.Refused;}
                catch(ItemFailedException e) when(e.Cause==TextKey.ClashNameTaken && e.Path==b.Path) {throw new InvalidDataException(wrong,e);}
                var records=new List<SyncedEntry>(); await foreach(var link in links.DescendantsAsync(pair.PairId,Side.Local,"",default)) records.Add(link);
                var notices=new List<Notice>(); await foreach(var notice in links.NoticesAsync(pair.PairId,default)) notices.Add(notice);
                if(scenario=="refused")
                    Check.Require(records.Contains(arrival) && left.History.Any(h=>h.Entry==pcArrival) && right.Items[dbArrival.Path].Entry==dbArrival,"Arrival binding retired after PC recycle before cloud removal");
                if(scenario=="edited")
                    Check.Require(outcome==EffectOutcome.Applied && !from.Items.ContainsKey(d.Path) && !to.Items.ContainsKey(a.Path) && records.Contains(owner),"Edited arrival published without settling its move");
                if (scenario is not ("equal" or "different" or "edited"))
                {
                    Check.Require(outcome==(scenario=="refused"?EffectOutcome.Refused:EffectOutcome.Pending) && records.Contains(arrival) && records.Contains(owner) && right.Items[dbArrival.Path].Bytes.SequenceEqual(new byte[]{scenario=="late"?(byte)9:(byte)7}),"Unsettled recycle lost the arriving binding or deleted its cloud peer");
                    continue;
                }
                Check.Require(outcome==EffectOutcome.Applied && records.Contains(owner) && !records.Contains(arrival) &&
                    from.Items[c.Path].Entry==c && to.Items[b.Path].Entry==b && !from.Mutations.Contains(c.Id) && !to.Mutations.Contains(b.Id) && !from.Items.ContainsKey(d.Path) && !to.Items.ContainsKey(a.Path),wrong);
                if(occupantByte==7) Check.Require(records.Count==1 && left.History.Any(h=>h.Entry==pcArrival && h.Bytes.SequenceEqual(new byte[]{7})) && right.History.Any(h=>h.Entry==dbArrival && h.Bytes.SequenceEqual(new byte[]{7})) && notices.Count==0,wrong);
                else
                {
                    var label=ConflictName.For(b.Path,incoming,d.Modified,ItemChecks.Settings.ComputerName,ItemChecks.Settings,1);
                    Check.Require(left.Items.TryGetValue(label,out var l) && right.Items.TryGetValue(label,out var r) && (scenario=="edited" && written==Side.Local || l.Entry.Id==pcArrival.Id) && r.Entry.Id==dbArrival.Id && l.Bytes.SequenceEqual(new byte[]{content}) && r.Bytes.SequenceEqual(new byte[]{content}) && records.Count==2 && records.Contains(SyncedEntries.From(l.Entry,r.Entry)) && notices.SingleOrDefault() is {} notice && notice.Cause==TextKey.InfoKeptBothRetained && !Resolutions.Choices(notice).Any(choice=>choice is ResolutionChoice.UseLocal or ResolutionChoice.UseDropbox or ResolutionChoice.RenameLocal or ResolutionChoice.RenameDropbox),wrong);
                }
            }
            finally {dir.Delete(recursive:true);}
        }
    }

    public static async Task RecycleEvidenceAsync()
    {
        var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore(); var pair=ItemChecks.Pair(); var effects=new Effects();
        pc.Pages.Enqueue(new([],null,new(1,2))); db.Pages.Enqueue(new([],"after",null));
        var obsolete=ItemChecks.File(Side.Local,"dir/old",7,"obsolete"); var incumbent=ItemChecks.File(Side.Local,"dir/new",7,"incumbent");
        pc.Seed(obsolete,[7]); pc.Seed(incumbent,[7]); var peerFile=ItemChecks.File(Side.Dropbox,"dir/new",7,"keeper"); db.Seed(peerFile,[7]);
        var oldPeer=peerFile with {Path="dir/former"}; var oldLink=SyncedEntries.From(obsolete,oldPeer);
        await store.CommitAsync(pair.PairId,null,oldLink,null,null,default);
        var successor=ItemChecks.File(Side.Dropbox,oldPeer.Path,5,"successor"); db.Seed(successor,[5]);
        var selected=new RemovalSelection(new(obsolete,Side.Local.ToString(),obsolete.ContentHash),oldLink);
        await store.ArmAsync(pair.PairId,selected,null,default);
        Scope? observed=null;
        var settled=await effects.CollisionAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),Side.Local,obsolete,incumbent,peerFile,oldLink,pc,db,store,scope=>{observed=scope;return [];},(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,f=>f.Local==new JournalPosition(1,2) && f.Dropbox=="after");
        Check.Require(observed==new Scope(obsolete.Path,peerFile.Path) && settled==EffectOutcome.Applied &&
            store.Records.Values.SingleOrDefault()==SyncedEntries.From(incumbent,peerFile) && await store.ArmedAsync(pair.PairId,default)==null &&
            !pc.Items.ContainsKey(obsolete.Path) && pc.History.Any(h=>h.Entry==obsolete && h.Bytes.SequenceEqual(new byte[]{7})) &&
            pc.Items[incumbent.Path].Entry==incumbent && db.Items[peerFile.Path].Entry==peerFile && db.Items[oldPeer.Path].Entry==successor &&
            db.Items[oldPeer.Path].Bytes.SequenceEqual(new byte[]{5}) && !db.Mutations.Contains(successor.Id),"Mapped cleanup did not settle the live link or touched the obsolete-name occupant");
        await store.ArmAsync(pair.PairId,new(new(incumbent,Side.Local.ToString(),incumbent.ContentHash),null),null,default);
        Check.Require(await store.ArmedAsync(pair.PairId,default) is {} next && next.Victim.Proof.Expected.Id==incumbent.Id,"Next independent removal could not arm");
        await store.DisarmAsync(pair.PairId,new(new(incumbent,Side.Local.ToString(),incumbent.ContentHash),null),null,default);
        var victim=ItemChecks.File(Side.Local,"late",7,"late-id"); pc.Seed(victim,[7]);
        var prior=SyncedEntries.From(victim,victim with {Id="cloud-late"}); await store.CommitAsync(pair.PairId,null,prior,null,null,default);
        var expectedNote=new RemovalSelection(new(victim,Side.Local.ToString(),victim.ContentHash),prior);
        await store.ArmAsync(pair.PairId,expectedNote,null,default);
        var newer=ItemChecks.File(Side.Local,victim.Path,8,victim.Id);bool consumed=false;bool read=false;
        var actualReport=new Change(Side.Local,ChangeKind.Changed,victim.Path,null,victim.Id,newer,true,false){ConsumeHeld=()=>consumed=true};
        var outcome=await effects.ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),new(ActionKind.DeleteLocal,new(prior.LocalKey,victim,null,prior,[],null,RowContext.Neutral)),pc,db,store,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,scope=>
        {
            read=scope==new Scope(victim.Path,prior.DropboxPath) && !pc.Items.ContainsKey(victim.Path);
            Check.Require(store.Records.GetValueOrDefault((pair.PairId,prior.LocalKey))==prior &&
                store.ArmedAsync(pair.PairId,default).GetAwaiter().GetResult() is {} note && note.Victim==expectedNote && note.Stage==null,"Recovery received the edit without the selected old link and its armed note");
            pc.History.RemoveAll(h=>h.Entry.Id==victim.Id); pc.History.Add((newer,[8]));
            return [actualReport];
        },f=>f.Local==new JournalPosition(1,2) && f.Dropbox=="after");
        Check.Require(read && consumed && outcome==EffectOutcome.Pending && pc.Items.TryGetValue(victim.Path,out var restored) && restored.Bytes.SequenceEqual(new byte[]{8}) && db.Items.TryGetValue(victim.Path,out var other) && other.Bytes.SequenceEqual(new byte[]{8}) &&
            store.Records.GetValueOrDefault((pair.PairId,prior.LocalKey))==SyncedEntries.From(restored.Entry,other.Entry) && await store.ArmedAsync(pair.PairId,default)==null,"Identified late edit was not delivered to recovery before retiring the binding");
    }
}
