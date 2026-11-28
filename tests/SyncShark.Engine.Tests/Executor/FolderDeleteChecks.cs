using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks evidence custody across a folder removal.</summary>
public static class FolderDeleteChecks
{
    /// <summary>A failed publication keeps the newest reported revision available to Retry.</summary>
    public static async Task RunAsync()
    {
        var failures=new List<string>();var missing=new List<string>();
        foreach(var test in new Func<Task>[]{()=>SelectedCloudWindowAsync(["latest"]),()=>SelectedCloudWindowAsync(["unverified"]),()=>SelectedCloudWindowAsync(["moved"]),()=>SelectedCloudWindowAsync(["missing"]),SelectedChildrenAsync,DefiniteNoEffectAsync,SameIdRetryAsync,MovedOutEvidenceAsync,LocatedFileRetryAsync,ExcludedPhysicalAsync,IgnoredPhysicalAsync,NestedPhysicalAsync,AncestorAsync,()=>OutsideFolderAsync(true),()=>OutsideFolderAsync(false),RemovalHopLeavesLiveDescendant,ArmedSiblingsAsync,ImmediateBoundaryAsync,SupersededRevisionAsync,RecoveryAsync,PartialAsync,OutsideAsync,TombstoneAsync,ForeignTombstoneAsync,ExcludedAsync,UnreadableAsync,CostAsync,NamespaceAsync,MappedAsync,FolderRetryAsync,ReceiptOrderAsync,LateMergeAsync,CrashAfterFirstAsync,LostReplyAsync,NewerAsync,KeptTreeAsync,CoveredAsync,ConflictAsync,ReceiptCoverageAsync,ConflictingLiveAsync,SubstituteAsync,StrandedAsync,SubFolderAsync,QuietAsync,RecycleBeforeReaderSeesItAsync,RootProofAsync,UnreadableVetoAsync,RecycleBeforeReaderSplitAsync,RemovalCommitCloseAsync,NoTokenZeroWorkAsync})
        {
            try {await test();}
            catch(NotImplementedException) {missing.Add(test.Method.Name);}
            catch(Exception e) {failures.Add(test.Method.Name+": "+e.Message);}
        }
        Check.Require(failures.Count==0,string.Join("; ",failures));
        if(missing.Count!=0) throw new NotImplementedException(string.Join("; ",missing));
    }
    /// <summary>The reader has not read the folder recycle's own records yet: the selected folder's settlement waits (Pending), with no Failure and the binding kept.</summary>
    internal static async Task RecycleBeforeReaderSeesItAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var root=ItemChecks.File(Side.Local,"p",0,"root",true);var child=ItemChecks.File(Side.Local,"p/c",1,"c");pc.Seed(root,[]);pc.Seed(child,[1]);
        var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(child,child with {Id="gone-c"}),null,null,default);
        var admitted=Admitted(pc,db);
        // Pre-send the reader is current; post-send it has not yet read the recycle's own records.
        bool Covers((JournalPosition? Local,string? Dropbox) fence)=>admitted(fence) && !pc.History.Any(v=>v.Entry.Id==root.Id);
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Covers);
        Check.Require(result.Outcome==RemovalOutcome.Pending && result.Cause==null && store.OpenNotices.Count==0 && store.Records.Values.Contains(saved) && pc.History.Any(v=>v.Entry.Id==root.Id),
            "A folder recycle not yet read by the reader raised a Failure or settled instead of waiting");
    }

    /// <summary>Split variant: a child whose post-send check is behind keeps Retained, the next independent child is still attempted, and the first sibling's cause is the folder's.</summary>
    internal static async Task RecycleBeforeReaderSplitAsync()
    {
        foreach(var refusedFirst in new[]{false,true})
        {
            var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Local,"p/kept",true)]};using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var root=ItemChecks.File(Side.Local,"p",0,"root",true);var a=ItemChecks.File(Side.Local,"p/a",1,"a");var b=ItemChecks.File(Side.Local,"p/b",2,"b");var kept=ItemChecks.File(Side.Local,"p/kept",3,"kept");
            pc.Seed(root,[]);pc.Seed(a,[1]);pc.Seed(b,[2]);pc.Seed(kept,[3]);
            var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            if(refusedFirst) pc.RecycleRefusals[a.Path]=new ItemFailedException(a.Path,TextKey.FailChanged,null);
            // a refused: a's send never reaches a check, so b's post-send check is the behind one; otherwise a's is.
            var admitted=Admitted(pc,db);int checks=0;int behind=refusedFirst?3:2;
            bool Covers((JournalPosition? Local,string? Dropbox) fence)=>admitted(fence) && ++checks!=behind;
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Covers);
            // "Split variant: two independent children, the first child's post-send check behind -> the second child is still attempted this turn; an earlier sibling refusal's cause is the folder's cause."
            Check.Require(result.Outcome==RemovalOutcome.Retained && result.Cause==(refusedFirst?TextKey.FailChanged:TextKey.RetryRecoveryRequired) && pc.Mutations.Contains(b.Id) && pc.History.Any(v=>v.Entry.Id==b.Id && v.Bytes.SequenceEqual(new byte[]{2})),
                $"A split child's behind check stopped its sibling or replaced the first cause (refusedFirst={refusedFirst}): {result.Outcome}/{result.Cause}");
            // No version ends up nowhere: each child is either in place or in the bin, the kept child and the folder binding stay.
            Check.Require(pc.Items[root.Path].Entry==root && pc.Items[kept.Path].Bytes.SequenceEqual(new byte[]{3}) && store.Records.Values.Contains(saved) && (refusedFirst?pc.Items[a.Path].Bytes.SequenceEqual(new byte[]{1}):pc.History.Any(v=>v.Entry.Id==a.Id && v.Bytes.SequenceEqual(new byte[]{1}))),
                $"A split removal lost a child version or its folder binding (refusedFirst={refusedFirst})");
        }
    }

    /// <summary>The removal commit closes its own exact earlier Failure only when no held input arrived before the close, also while the commit waits on the store.</summary>
    internal static async Task RemovalCommitCloseAsync()
    {
        foreach(var arm in new[]{"none","queued","admitted"})
        {
            var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var root=ItemChecks.File(Side.Local,"p",0,"root",true);var child=ItemChecks.File(Side.Local,"p/c",1,"c");pc.Seed(root,[]);pc.Seed(child,[1]);
            var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,root.Path,new([new(Side.Local,root.Path,root.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
            var held=new List<Change>();var late=new Change(Side.Local,ChangeKind.Changed,"p/late",null,"late",ItemChecks.File(Side.Local,"p/late",2,"late"),true,false);
            // (c2') input admitted while the commit waits on the store gate: added inside the commit call, before the guard runs.
            var gated=Observe.Wrap<IStore>(store,(op,args)=>{if(arm=="admitted" && op==nameof(IStore.CommitAsync) && args[7]!=null) held.Add(late);return null;});
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,gated,_=>held.ToArray(),(_,_,_,_)=>Task.CompletedTask,
                (_,_,_,_)=>{if(arm=="queued") held.Add(late);return Task.CompletedTask;},default,coversThrough:Admitted(pc,db)); // (c) queued after the recovered cohort, before the close
            // "removal child (FolderRemoval:302): (c) held input queued before the close -> open; (c2') ... -> binding committed, open; (e) none -> closed."
            Check.Require(result.Outcome==RemovalOutcome.Removed && !store.Records.Values.Contains(saved) && store.OpenNotices.Count==(arm=="none"?0:1),$"Removal commit close ({arm}): {result.Outcome}, open={store.OpenNotices.Count}");
            Check.Require(pc.History.Any(v=>v.Entry.Id==root.Id) && pc.History.Any(v=>v.Entry.Id==child.Id && v.Bytes.SequenceEqual(new byte[]{1})),$"Removal commit close ({arm}) lost a recycled version");
        }
    }

    /// <summary>The removal commit's close costs the same at two tree sizes for the same sparse notices, and nothing without them.</summary>
    internal static async Task NoTokenZeroWorkAsync()
    {
        var work=new Dictionary<(bool Noticed,int Size),(int Lookups,int Closings,int Tokens,int Open)>();
        foreach(var noticed in new[]{false,true}) foreach(var size in new[]{1,3})
        {
            var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);
            for(int i=0;i<size;i++){var c=ItemChecks.File(Side.Local,$"p/c{i}",(byte)(i+1),"c"+i);pc.Seed(c,[(byte)(i+1)]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(c,c with {Id="gone-c"+i}),null,null,default);}
            var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            if(noticed) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailChanged,root.Path,new([new(Side.Local,root.Path,root.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
            int lookups=0,closings=0,tokens=0;
            var counted=Observe.Wrap<IStore>(store,(op,args)=>
            {
                if(op is nameof(IStore.NoticesUnderAsync) or nameof(IStore.NoticesAtAsync)) lookups++;
                if(op==nameof(IStore.CommitAsync) && args[7] is ValueTuple<IReadOnlyList<Notice>,Func<bool>> closing) {closings++;tokens+=closing.Item1.Count;}
                return null;
            });
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,counted,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
            Check.Require(result.Outcome==RemovalOutcome.Removed && pc.History.Count(v=>!v.Entry.IsFolder)==size,$"Counted removal did not recycle every child (size {size})");
            work[(noticed,size)]=(lookups,closings,tokens,store.OpenNotices.Count);
        }
        // "NoTokenZeroWork: counted at FolderRemoval:302 only. Two total sizes, same sparse notice set -> identical lookups and closes; an empty `tokens` passes closing:null."
        Check.Require(work[(false,1)].Closings==0 && work[(false,3)].Closings==0,"A removal with no closable notice passed a close to its commit");
        Check.Require(work[(true,1)]==work[(true,3)] && work[(false,1)].Lookups==work[(false,3)].Lookups && work[(true,1)] is {Closings:1,Tokens:1,Open:0},
            "Removal close work grew with the tree: "+string.Join(",",work.Select(x=>$"{x.Key}={x.Value}")));
    }

    /// <summary>A Dropbox-absence folder effect acts only under the proved saved Dropbox root; the proof does not grow with the tree.</summary>
    internal static async Task RootProofAsync()
    {
        var counts=new List<int>();
        foreach(var arm in new[]{"matching-1","matching-3","replaced","both-absent","no-saved-root"})
        {
            var pair=arm=="no-saved-root"?ItemChecks.Pair() with {Roots=null}:BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            db.Root=(null,arm=="replaced"||arm=="both-absent"?new("account","other-namespace","home","other-root"):pair.Roots?.Dropbox,null,null);
            var names=arm=="matching-3"?new[]{"P","P/Q","P/Q/f"}:new[]{"P"};
            foreach(var name in names)
            {
                bool folder=name!="P/Q/f";var left=ItemChecks.File(Side.Local,name,folder?(byte)0:(byte)1,"pc-"+name,folder);var right=ItemChecks.File(Side.Dropbox,name,folder?(byte)0:(byte)1,"db-"+name,folder);
                if(arm!="both-absent") pc.Seed(left,folder?[]:[1]);
                await store.CommitAsync(pair.PairId,null,SyncedEntries.From(left,right),null,null,default);
            }
            var binding=store.Records.Values.Single(r=>r.LocalPath=="P");
            int roots=0,compares=0;var commits=store.Requests[nameof(IStore.CommitAsync)];
            var cloud=Observe.On(db,(op,_)=>{if(op==nameof(ISide.RootAsync))roots++;return null;});
            var volume=pair.Roots?.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)??"Local";
            var local=Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync) && pc.Items.Values.FirstOrDefault(v=>v.Entry.Id==(string?)args[0]).Entry is {} live?Task.FromResult<(IdentityLocation,bool)?>((new(volume,live.Path,live),true)):null);
            Exception? refused=null;
            try { await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),binding,Side.Dropbox,local,cloud,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compares++;return Task.CompletedTask;},default,coversThrough:Admitted(pc,db)); }
            catch(ChangedUnderneathException e) { refused=e; }
            if(arm.StartsWith("matching"))
            {
                Check.Require(refused==null && !pc.Items.ContainsKey("P") && pc.History.Any(v=>v.Entry.Id=="pc-P") && roots>=1,$"A departure under the matching root did not recycle the PC folder ({arm})");
                counts.Add(roots);
            }
            else Check.Require(refused is ChangedUnderneathException {Path:var at} && at==pair.DropboxRoot && store.Records.Values.Contains(binding) && compares==0 && store.Requests[nameof(IStore.CommitAsync)]==commits && (arm=="both-absent" || pc.Items.ContainsKey("P")),
                $"An absence-authorized folder effect acted without the proved saved Dropbox root ({arm})");
        }
        Check.Require(counts.Distinct().Count()==1,"The Dropbox root proof grew with the folder tree: "+string.Join(",",counts));
    }

    /// <summary>An open read notice vetoes an automatic removal only while its failed PC name, under the removal's PC path, may still be unreadable.</summary>
    internal static async Task UnreadableVetoAsync()
    {
        var failures=new List<string>();
        foreach(var arm in new[]{"stale","invalid-hash","hash-throws","folder","skip","read-error","store-fault","relabel","selected-dropbox"})
        {
            var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            bool dropboxSide=arm is "stale" or "relabel" or "selected-dropbox";
            var left=ItemChecks.File(Side.Local,"X",0,"pcX",true);var right=ItemChecks.File(Side.Dropbox,"X",0,"dbX",true);db.Seed(right,[]);
            if(!dropboxSide || arm=="selected-dropbox") pc.Seed(left,[]);
            var saved=SyncedEntries.From(left,right);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            var failed=arm switch {"folder"=>"X/d","skip"=>"X/f",_=>"X/x"};
            if(arm!="stale" && arm!="relabel") pc.Seed(arm switch {"folder"=>ItemChecks.File(Side.Local,"X/d",0,"pc-d",true),"skip"=>ItemChecks.File(Side.Local,"X/f",1,"pc-f") with {Skip=nameof(TextKey.SkipLink)},_=>ItemChecks.File(Side.Local,"X/x",1,"pc-x")},arm=="folder"?[]:[1]);
            if(arm=="folder") pc.Seed(ItemChecks.File(Side.Local,"X/d/f",1,"pc-df"),[1]);
            if(arm is "hash-throws" or "selected-dropbox") pc.ContentFailures["X/x"]=new ItemFailedException("X/x",TextKey.ReadLocalFailed,null);
            if(arm=="read-error") pc.ReadFailures["X/x"]=new ItemFailedException("X/x",TextKey.ReadLocalFailed,null);
            if(arm=="relabel") db.Seed(ItemChecks.File(Side.Dropbox,"X/u",1,"db-u") with {Skip=nameof(TextKey.SkipUnreadable)},[1]);
            else await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.ReadLocalFailed,failed,new([new(Side.Local,failed,"pc-"+failed[2..])],Side.Local),DateTimeOffset.UnixEpoch,false),default);
            int reads=0,hashes=0,lists=0;
            var local=Observe.On(pc,(op,args)=>
            {
                if(op==nameof(ISide.ReadOneAsync) && Equals(args[0],failed)) { reads++; if(arm=="store-fault") throw new InvalidOperationException("store fault"); }
                if(op is nameof(ISide.ContentHashAsync) or nameof(ISide.OpenReadAsync)) { hashes++; if(arm=="invalid-hash") return Task.FromResult(string.Empty); }
                if(op==nameof(ISide.ReadAllAsync)) lists++;
                return null;
            });
            Task<RemovalResult> Remove(bool userSelected)=>dropboxSide
                ? new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),right,saved,Side.Dropbox,ActionKind.DeleteDropbox,local,DeleteSide(db),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db),userSelected:userSelected)
                : new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),left,saved,Side.Local,ActionKind.DeleteLocal,local,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
            RemovalResult? result=null;Exception? fault=null;
            try { result=await Remove(arm is "relabel" or "selected-dropbox"); } catch(InvalidOperationException e) { fault=e; }
            bool kept=dropboxSide?db.Items.ContainsKey("X"):pc.Items.ContainsKey("X") && pc.History.Count==0;
            bool ok=arm switch
            {
                "stale" => !kept && reads==1 && hashes==0,
                "invalid-hash" or "hash-throws" or "read-error" => kept && result is {Outcome:RemovalOutcome.Retained,Cause:TextKey.ReadLocalFailed} && pc.Items.ContainsKey("X/x"),
                "folder" => kept && result is {Outcome:RemovalOutcome.Retained} && lists==0,
                "skip" => kept && result is {Outcome:RemovalOutcome.Retained} && hashes==0,
                "store-fault" => kept && fault!=null,
                "relabel" => kept && store.OpenNotices.Any(n=>n is {Cause:TextKey.ReadDropboxFailed,Path:"X/u"}),
                "selected-dropbox" => !kept && reads==0 && hashes==0,
                _ => false
            };
            if(arm=="relabel" && ok)
            {
                // An automatic Dropbox-side removal over the open Dropbox read notice keeps the folder and reads nothing on the PC; without it the folder goes.
                db.Items.Remove("X/u");var before=pc.Requests[nameof(ISide.ReadOneAsync)];
                ok=await Remove(false) is {Outcome:RemovalOutcome.Retained} && db.Items.ContainsKey("X") && pc.Requests[nameof(ISide.ReadOneAsync)]==before;
                await store.CloseNoticeAsync(pair.PairId,store.OpenNotices.Single(n=>n.Cause==TextKey.ReadDropboxFailed),default);
                ok&=await Remove(false) is {Outcome:RemovalOutcome.Removed} && !db.Items.ContainsKey("X");
            }
            if(!ok) failures.Add($"{arm}: {result?.Outcome}/{result?.Cause} reads={reads} hashes={hashes} lists={lists} fault={fault?.Message}");
        }
        Check.Require(failures.Count==0,"Unreadable veto: "+string.Join("; ",failures));
    }
    internal static async Task SelectedCloudWindowAsync(string[] modes)
    {
        var failures=new List<string>();
        foreach(var mode in modes)
        {
            var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            bool relocated=mode is "moved" or "remap" or "close" or "newer-slot";
            var root=ItemChecks.File(Side.Dropbox,"p",0,"tree",true);var late=ItemChecks.File(Side.Dropbox,relocated?"q/c":"p/c",2,"late");db.Seed(root,[]);
            var edit=new Change(Side.Dropbox,ChangeKind.Changed,late.Path,null,late.Id,late,false,false);var tomb=new Change(Side.Dropbox,ChangeKind.Deleted,late.Path,null,null,null,false,false);
            db.DeletedHistory[late.Path]=new DeletionEvidence.DeletedFile(late.Path,pair.Roots!.Dropbox.RootNamespaceId,late.Id,late.Revision,DateTimeOffset.UnixEpoch,null,late.ContentHash,null);
            db.Recoverable[(late.Id,late.Revision,late.ContentHash)]=new Recovery.File(late,new MemoryStream([2]));
            var first=ItemChecks.File(Side.Dropbox,late.Path,1,late.Id);var other=ItemChecks.File(Side.Dropbox,"p/d",3,"unverified");
            var before=DateTimeOffset.UtcNow;var boundary=before;int pages=0,compares=0; var oldSide=mode=="other-side"?Side.Local:Side.Dropbox; var oldId=mode=="different-id"?"previous":late.Id;
            async Task<RemovalResult> Send(){if(mode is "earlier" or "legacy" or "different-id" or "other-side") await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,late.Path,new([new(oldSide,late.Path,oldId)],oldSide){DropboxVersion=mode!="legacy" && oldSide==Side.Dropbox?new(pair.Roots!.Dropbox.RootNamespaceId,PathNames.RootedDropboxPath(pair.DropboxRoot,late.Path),first with {Id=oldId}):null},DateTimeOffset.UnixEpoch,false),default);var target=root;if(relocated){target=(await db.MoveAsync(root,"q",default)).Final!;db.Seed(late,[2]);}var sent=await db.DeleteAsync(target,null,default);return sent with {Reply=mode=="missing"?null:new JobReply.Terminal([(target,null,null)])};}
            var side=Observe.On(History(db),(op,args)=>op==nameof(ISide.DeleteAsync)?Send():op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((db.Mutations.Count==0?"before":"after",null)):op==nameof(ISide.ReadChangesAsync) && args.Length==4?Read((string?)args[0]):null);
            Task<ChangePage> Read(string? from){if(pages==0) boundary=DateTimeOffset.UtcNow;pages++;return Task.FromResult(from=="before"?new ChangePage(mode is "missing" or "moved"?[edit]:[edit with {Observed=first}],"middle",null):from=="middle"?new ChangePage(mode=="unverified"?[edit,edit with {Path=other.Path,Id=other.Id,Observed=other},tomb]:[edit,tomb],"after",null){CaughtUp=true}:throw new InvalidDataException("Foreign cursor in selected window"));}
            var saved=SyncedEntries.From(root with {Id="pc-tree"},root);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            if(mode=="unverified") db.Seed(other with {Path="outside/d"},[3]);
            if(mode is "restart" or "earlier" or "legacy" or "different-id" or "other-side") pc.PublishFailures[late.Path]=new IOException();
            var ancestor=SyncedEntries.From(root with {Path="mapped",Id="remote-parent"},root with {Path="q"});
            if(mode=="remap") await store.CommitAsync(pair.PairId,null,ancestor,null,null,default);
            var different=late with {Id="previous-slot"};
            var previous=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,late.Path,new([new(Side.Dropbox,late.Path,different.Id)],Side.Dropbox){DropboxVersion=new(pair.Roots!.Dropbox.RootNamespaceId,PathNames.RootedDropboxPath(pair.DropboxRoot,late.Path),different)},DateTimeOffset.UnixEpoch,false);
            if(mode=="close") await store.SaveNoticeAsync(pair.PairId,previous,default);
            async IAsyncEnumerable<Notice> Slot(){var snapshot=store.OpenNotices.Where(n=>n.Path==late.Path).ToArray();try{foreach(var n in snapshot) yield return n;}finally{
                if(mode=="close" && snapshot.FirstOrDefault()?.Subject.DropboxVersion?.Entry==different) await store.CloseNoticeAsync(pair.PairId,snapshot[0],default);
            }}
            int maps=0;bool remapped=false;
            var observedStore=Observe.Wrap<IStore>(store,(op,args)=>{
                if(mode=="remap" && !remapped && (op==nameof(IStore.SaveNoticeAsync) || op==nameof(IStore.FindAsync) && (string?)args[2]==PathNames.DropboxKey(late.Path) && ++maps==2)){
                    remapped=true;
                    store.CommitAsync(pair.PairId,ancestor,ancestor with {LocalPath="changed",LocalKey=PathNames.Key("changed")},null,null,default).GetAwaiter().GetResult();
                }
                if(op==nameof(IStore.NoticesAtAsync) && (string?)args[1]==PathNames.DropboxKey(late.Path)){
                    if(mode=="newer-slot" && store.OpenNotices.Count==0){var later=boundary.AddTicks(1);store.SaveNoticeAsync(pair.PairId,previous with {At=later},default).GetAwaiter().GetResult();while(DateTimeOffset.UtcNow<later) Thread.SpinWait(1);}
                    if(mode=="close") return Slot();
                }
                if(mode=="newer-slot" && op==nameof(IStore.SaveNoticeAsync) && args[1] is Notice n && store.OpenNotices.Any(prior=>prior.Path==n.Path && prior.At>n.At)) return Task.FromResult<Notice?>(null);
                return null;
            });
            Task<RemovalResult> Apply()=>new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Dropbox,ActionKind.DeleteDropbox,pc,side,observedStore,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compares++;return Task.CompletedTask;},default,userSelected:true);
            var result=await Apply();
            if(mode is "remap" or "close" or "newer-slot"){
                Check.Require((mode!="remap" || remapped) && result.Outcome==RemovalOutcome.Unknown && compares==0 && pc.Items.Count==0 && !db.Items.ContainsKey(late.Path) && store.OpenNotices.Any(n=>n.Subject.DropboxVersion?.Entry==(mode=="newer-slot"?different:late)),mode+": relocated custody lost or superseded");continue;
            }
            if(mode is "restart" or "earlier" or "legacy" or "different-id" or "other-side" or "moved")
            {
                if(mode=="moved" && (result.Outcome!=RemovalOutcome.Unknown || compares!=0 || pc.Items.ContainsKey(late.Path) || db.Items.ContainsKey(late.Path))) throw new InvalidDataException("moved: publication or settlement outside selected grant");
                var held=store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack && n.Path==late.Path);
                if(held.Subject.DropboxVersion?.Entry!=late || held.At<before || held.At>boundary){failures.Add(mode+": exact child was not saved at identification time");continue;}
                var reopened=System.Text.Json.JsonSerializer.Deserialize<Notice>(System.Text.Json.JsonSerializer.Serialize(held))!;
                pc.PublishFailures.Clear();db.DeletedHistory[late.Path]=new DeletionEvidence.DeletedFile(late.Path,pair.Roots!.Dropbox.RootNamespaceId,"another-id","later",DateTimeOffset.UnixEpoch,null,first.ContentHash,null);
                db.Recoverable[(late.Id,late.Revision,late.ContentHash)]=new Recovery.File(late,new MemoryStream([2]));
                if(!await HeldRecovery.RetryHeldAsync(pair,new(late.Path,late.Path),reopened,pc,side,store,_=>[edit with {Observed=first}],(_,_,_,_)=>Task.CompletedTask,default) || !pc.Items.TryGetValue(late.Path,out var a) || !a.Bytes.SequenceEqual(new byte[]{2}) || !db.Items.TryGetValue(late.Path,out var b) || !b.Bytes.SequenceEqual(new byte[]{2}) || store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==late.Path)) failures.Add(mode+": Restart failed to publish exact R2 independently of the later history head");
            }
            else if(mode is "latest" or "unverified")
            {
                if(pages!=2 || !pc.Items.TryGetValue(late.Path,out var a) || !a.Bytes.SequenceEqual(new byte[]{2}) || !db.Items.TryGetValue(late.Path,out var b) || !b.Bytes.SequenceEqual(new byte[]{2})) failures.Add(mode+": last identifiable child did not reach both sides");
                if(mode=="latest" && (result.Outcome!=RemovalOutcome.Removed || db.Requests[nameof(ISide.DeletedAsync)]!=0)) failures.Add("latest: selected removal read history");
                if(mode=="unverified" && (result.Outcome==RemovalOutcome.Removed || compares!=0 || !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==other.Path))) failures.Add("unverified: uncertain child lost put-back custody");
            }
            else if(result.Outcome!=RemovalOutcome.Unknown || compares!=0) failures.Add(mode+": uncertain terminal receipt settled the selected tree");
        }
        Check.Require(failures.Count==0,string.Join("; ",failures));
    }
    private static async Task SelectedChildrenAsync()
    {
        foreach(var mode in new[]{"late","stale","automatic"})
        {
            var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var root=ItemChecks.File(Side.Local,"p",0,"root",true);var child=ItemChecks.File(Side.Local,"p/c",1,"child");var changed=ItemChecks.File(Side.Local,child.Path,2,child.Id);pc.Seed(root,[]);pc.Seed(child,[1]);int lists=0;
            async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;yield return new(changed.Path,changed);}
            async Task<RemovalResult> Send(){pc.Items[child.Path]=(changed,[2]);var result=await pc.DeleteAsync(root,null,default);return result with {RecycledVictim=new(FakeSide.Volume,"bin",root),Recycled=Receipt()};}
            var side=Observe.On(pc,(op,args)=>{if(op==nameof(ISide.ReadAllAsync) && ++lists==2 && mode=="stale")pc.Items[child.Path]=(changed,[2]);return op==nameof(ISide.DeleteAsync)?Send():null;});
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,userSelected:mode!="automatic");
            if(mode=="late")Check.Require(result.Outcome==RemovalOutcome.Removed && pc.History.Any(x=>x.Entry==changed && x.Bytes.SequenceEqual(new byte[]{2})) && store.OpenNotices.Any(n=>n.Cause==TextKey.FailRecycledLate && n.Subject.Items.Any(i=>i.Id==changed.Id && i.Path==changed.Path)),"Selected recycle did not report the late child retained in its receipt");
            else Check.Require(result.Outcome!=RemovalOutcome.Removed && pc.Mutations.Count==0 && pc.Items[child.Path].Bytes.SequenceEqual(new byte[]{mode=="stale"?(byte)2:(byte)1}),"Stale child or unproved automatic folder was removed");
        }
    }
    private static async Task AncestorAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var cloud=new FakeSide(Side.Dropbox);
        var parent=ItemChecks.File(Side.Dropbox,"p",0,"parent",true);var child=parent with {Path="p/c",Id="child"};
        var a=SyncedEntries.From(parent with {Id="pc-p"},parent);var b=SyncedEntries.From(child with {Id="pc-c"},child);await store.CommitAsync(pair.PairId,null,a,null,null,default);await store.CommitAsync(pair.PairId,null,b,null,null,default);
        var source=Observe.On(cloud,(op,args)=>op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==parent.Id?Task.FromResult<(IdentityLocation,bool)?>((new("ns",parent.Path,parent with {Skip=nameof(TextKey.SkipUnreadable)}),true)):null);
        var paths=new PathScope(pair,[pair],DriveType.Fixed);Check.Require(await FolderDelete.HighestMissingAsync(pair,paths,b,source,store,default)==b && await FolderDelete.HighestMissingAsync(pair,paths,b,cloud,store,default)==a && cloud.Mutations.Count==0,"An in-scope ancestor widened the selected footprint");
    }
    private static Task RemovalHopLeavesLiveDescendant()=>OutsideFolderAsync(true,true);
    private static async Task OutsideFolderAsync(bool recycled,bool liveChild=false)
    {
        var pair=BoundPair();var paths=new PathScope(pair,[pair],DriveType.Fixed);using var store=new InMemoryStateStore();
        var pc=Rooted(new FakeSide(Side.Local),pair);var db=Rooted(new FakeSide(Side.Dropbox),pair);var outside=new FakeSide(Side.Local);
        await store.SavePairAsync(pair,default);await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
        var root=ItemChecks.File(Side.Local,"tree",0,"root",true);var folder=ItemChecks.File(Side.Local,"tree/sub",0,"sub",true);var file=ItemChecks.File(Side.Local,"tree/sub/data",1,"data");
        var peer=root with {Id="remote-root"};var saved=SyncedEntries.From(folder,folder with {Id="remote-sub"});
        foreach(var entry in new[]{root,folder,file})
        {
            var remote=entry with {Id="remote-"+entry.Id};byte[] bytes=entry.IsFolder?[]:[1];
            pc.Seed(entry,bytes);db.Seed(remote,bytes);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(entry,remote),null,null,default);
        }
        Check.Require(await FolderDelete.HighestMissingAsync(pair,paths,saved,pc,store,default)==null,"An in-scope folder was selected for removal");
        if(liveChild) await pc.MoveAsync(file,"kept/data",default);
        if(recycled) await pc.DeleteAsync(root,null,default);
        else foreach(var item in pc.Items.Values.ToArray()){outside.Seed(item.Entry with {Path="outside/"+item.Entry.Path},item.Bytes);pc.Items.Remove(item.Entry.Path);}
        var local=recycled?(ISide)pc:Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync) && outside.Items.Values.FirstOrDefault(v=>v.Entry.Id==(string?)args[0]).Entry is {} moved
            ?Task.FromResult<(IdentityLocation,bool)?>((new(FakeSide.Volume,moved.Path,moved),false)):null);
        Check.Require(await FolderDelete.HighestMissingAsync(pair,paths,saved,local,store,default)==SyncedEntries.From(root,peer),"Out-of-scope folder did not select its missing ancestor");
        var queue=await ItemChecks.StartQueueAsync(pair,paths,local,db,store);
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,saved,Side.Local,local,db,store,queue.Held,ItemChecks.Population(pair,paths,local,db,store),ItemChecks.Hop(pair,paths,queue,local,db,store),default,coversThrough:Admitted(pc,db));
        if(liveChild)
        {
            Check.Require(result.Outcome==RemovalOutcome.Removed && store.Records.Values.Single().LocalId==file.Id && pc.Items.ContainsKey("kept/data"),"A removal hop dropped or aborted on a live moved descendant");
            return;
        }
        var failures=new List<string>();
        void Require(bool met,string message){if(!met) failures.Add(message);}
        Require(result.Outcome==RemovalOutcome.Removed && db.Items.Count==0,"Dropbox folder removal did not finish");
        Require(store.Records.Count==0,"Folder removal left bindings");
        Require(!queue.Held(new(root.Path,peer.Path)).Any(),"Folder removal left held input");
        Require(db.History.Any(v=>v.Entry==peer) && db.History.Any(v=>v.Entry.Id=="remote-data" && v.Bytes.SequenceEqual(new byte[]{1})) && (recycled?pc.History.Any(v=>v.Entry==file && v.Bytes.SequenceEqual(new byte[]{1})):outside.Items["outside/"+file.Path].Bytes.SequenceEqual(new byte[]{1})),"Outside folder removal lost a retained version");
        if(recycled)
        {
            pc.Seed(file,[1]); // the original ID is restored, unchanged, before any fresh sweep
            await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,new Change(Side.Local,ChangeKind.Changed,file.Path,null,file.Id,file,true,false),local,db,store,
                ItemChecks.Population(pair,paths,local,db,store),ItemChecks.Hop(pair,paths,queue,local,db,store),default,queue.Held);
            Require(pc.Items.ContainsKey(file.Path) && db.Items.TryGetValue(file.Path,out var up) && up.Bytes.SequenceEqual(new byte[]{1}),"A restored unchanged child was not uploaded after its folder removal");
        }
        Check.Require(failures.Count==0,(recycled?"Binned":"Moved out")+": "+string.Join("; ",failures)+$" (outcome={result.Outcome}, cloud={db.Items.Count}, bindings={string.Join(",",store.Records.Values.Select(r=>r.LocalPath))})");
    }
    private static Task ExcludedPhysicalAsync()=>PhysicalFolderAsync(excluded:true);
    private static Task IgnoredPhysicalAsync()=>PhysicalFolderAsync(ignored:true);
    private static Task NestedPhysicalAsync()=>PhysicalFolderAsync(nested:true);
    private static async Task PhysicalFolderAsync(bool excluded=false,bool ignored=false,bool nested=false)
    {
        const string root="p", childPath="p/child";
        var pair=ItemChecks.Pair() with {Exclusions=excluded?[new(Side.Local,childPath,true)]:[],IgnoreRules=ignored?["FileName:child"]:[]};
        var inner=pair with {PairId="inner",LocalRoot=Path.Combine(pair.LocalRoot,"p","child"),DropboxRoot=pair.DropboxRoot+"/p/child",Roots=null};
        var paths=new PathScope(pair,nested?[pair,inner]:[pair],DriveType.Fixed);string name=excluded?"Excluded":ignored?"Ignored":"Nested";
        foreach(bool available in new[]{false,true})
        {
            using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
            var parent=ItemChecks.File(Side.Local,root,0,"parent",true);var saved=SyncedEntries.From(parent,parent with {Id="remote"});pc.Seed(parent,[]);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
            var child=ItemChecks.File(Side.Local,childPath,1,"child",nested);pc.Seed(child,nested?[]:[1]);
            if(nested)await store.CommitAsync(pair.PairId,null,SyncedEntries.From(child,child with {Id="remote-child"}),null,null,default);
            if(!ignored)pc.Seed(ItemChecks.File(Side.Local,"p/free",2,"free"),[2]);
            var mark=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,mark));db.Pages.Enqueue(new([],"end",null));
            var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,parent,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>Task.CompletedTask,default,coversThrough:available?f=>f.Local==mark && f.Dropbox=="end":null);
            if(!available){Check.Require(result.Outcome==RemovalOutcome.Retained && pc.Mutations.Count==0 && pc.History.Count==0 && pc.Items.ContainsKey(root) && pc.Items.ContainsKey(childPath),name+" tree mutated before acquisition owner was available");continue;}
            Check.Require(db.Items.Count==0 && store.OpenNotices.Count==0,name+" physical removal touched peer or raised an unexpected notice");
            if(ignored)Check.Require(result.Outcome==RemovalOutcome.Removed && pc.Items.Count==0 && pc.History.Any(x=>x.Entry.Id==child.Id && x.Bytes.SequenceEqual(new byte[]{1})),"Ignored bytes did not accompany the recycled folder");
            else Check.Require(result.Outcome==RemovalOutcome.Retained && pc.Items.Count==2 && pc.Items[root].Entry==parent && pc.Items[childPath].Entry==child && !pc.Items.ContainsKey("p/free") && pc.History.Single() is var moved && moved.Entry.Id=="free" && moved.Bytes.SequenceEqual(new byte[]{2}),name+" boundary lost the survivor or failed to recycle only the free sibling");
        }
    }
    private static async Task ArmedSiblingsAsync()
    {
        var pair=ItemChecks.Pair() with {Exclusions=[new(Side.Local,"p/kept",true)]};using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);foreach(var name in new[]{"a","b","kept"})pc.Seed(ItemChecks.File(Side.Local,"p/"+name,1,name),[1]);
        async Task<RemovalResult> Remove(object?[] args){var victim=(RemovalSelection)args[1]!; Check.Require(await store.ArmedAsync(pair.PairId,default)==null,"Second sibling blocked by first successful recycle note");await store.ArmAsync(pair.PairId,victim,null,default);return await pc.DeleteAsync((Entry)args[0]!,victim,default);}
        var side=Observe.On(pc,(op,args)=>op==nameof(ISide.DeleteAsync)?Remove(args):null);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(pc.Mutations.SequenceEqual(new[]{"a","b"}) && pc.Items.ContainsKey("p/kept") && await store.ArmedAsync(pair.PairId,default)==null,"Successful sibling removal did not settle its exact note");
    }
    private static async Task ImmediateBoundaryAsync()
    {
        foreach(bool missingLocal in new[]{true,false}){
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Dropbox,"p",0,"folder",true);db.Seed(root,[]);var saved=SyncedEntries.From(root with {Id="pc"},root);await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var mark=new JournalPosition(1,42);if(!missingLocal)pc.Pages.Enqueue(new([],null,mark));
        var v=ItemChecks.File(Side.Dropbox,"p/late",1,"child");db.Seed(v,[1]);bool ack=false,compared=false;
        var edit=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>ack=true};var cloud=Observe.On(DeleteSide(db),(op,_)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((db.Mutations.Count==0?"before":"after",null)):null);
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Dropbox,ActionKind.DeleteDropbox,pc,cloud,store,_=>[edit],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compared=true;return Task.CompletedTask;},default,coversThrough:f=>f.Dropbox=="before");
        Check.Require(!ack && !compared && result.Outcome==(missingLocal?RemovalOutcome.Retained:RemovalOutcome.Pending) && pc.Items.Count==0,"Immediate removal settled before reading through its post-effect boundary");
        Check.Require(missingLocal?db.Mutations.Count==0 && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{1}):db.Mutations.SequenceEqual(new[]{root.Id}) && !db.Items.ContainsKey(root.Path),"Removal did not require bilateral pre-send evidence");}
    }
    internal static Func<(JournalPosition? Local,string? Dropbox),bool> Admitted(FakeSide pc,FakeSide db)
    {
        var end=new JournalPosition(1,42);pc.Pages.Enqueue(new([],null,end));db.Pages.Enqueue(new([],"complete",null));return fence=>fence.Local==end && fence.Dropbox=="complete";
    }
    internal static ISide DeleteSide(FakeSide cloud)
    {
        async Task<RemovalResult> Remove(Entry entry,CancellationToken ct)=>(await cloud.DeleteAsync(entry,null,ct)) with {Reply=new JobReply.Terminal([(entry,null,null)])};
        return Observe.On(cloud,(name,args)=>name==nameof(ISide.DeleteAsync) && args.Length==4?Remove((Entry)args[0]!, (CancellationToken)args[3]!):null);
    }
    // An endpoint whose top folder still has the bound pair's saved identity, as Repair and removal cleanups require.
    internal static FakeSide Rooted(FakeSide side,PairConfig pair) { side.Root=side.Side==Side.Local ? (pair.Roots!.Local,null,null,DriveType.Fixed) : (null,pair.Roots!.Dropbox,null,null); return side; }
    internal static PairConfig BoundPair() => ItemChecks.Pair();
    internal static async Task SupersededRevisionAsync()
    {
        var pair=BoundPair(); using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var a=ItemChecks.File(Side.Local,"parent",0,"a",true); var b=ItemChecks.File(Side.Dropbox,a.Path,0,"b",true);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(a,b),null,null,default);
        var first=ItemChecks.File(Side.Dropbox,"parent/child",1,"child"); var last=ItemChecks.File(Side.Dropbox,first.Path,2,first.Id); var newer=ItemChecks.File(Side.Dropbox,first.Path,3,first.Id);
        db.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        var ns=pair.Roots!.Dropbox.RootNamespaceId;
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailLocalInUse,last.Path,new([new(Side.Dropbox,last.Path,last.Id)],Side.Dropbox){DropboxVersion=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,last.Path),last)},DateTimeOffset.UtcNow,false),default);
        var notice=store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack);
        int firstAcks=0,lastAcks=0,newerAcks=0,marked=0,firstRelease=0;
        Change Report(Entry v,Action ack)=>new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){MarkHeld=()=>{marked++;return ()=>{if(firstRelease==0) firstRelease=marked;ack();};}};
        Change[] held=[Report(first,()=>firstAcks++),Report(last,()=>lastAcks++),Report(newer,()=>newerAcks++),Report(last,()=>{})];
        bool retried=await HeldRecovery.RetryHeldAsync(pair,new(last.Path,last.Path),notice,pc,History(db),store,_=>held,(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(retried && firstRelease==held.Length-1 && lastAcks==1 && firstAcks==1 && newerAcks==0 && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Put-back Retry left a superseded revision of its restored item held");
    }

    internal static Task RecoveryAsync()=>RecoveryAsync(false);
    internal static async Task RecoveryAsync(bool olderSnapshot)
    {
        var pair=BoundPair(); var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store=new InMemoryStateStore(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox);
        var a=ItemChecks.File(Side.Local,"parent",0,"a",true); var b=ItemChecks.File(Side.Dropbox,a.Path,0,"b",true);
        var binding=SyncedEntries.From(a,b); await store.CommitAsync(pair.PairId,null,binding,null,null,default); db.Seed(b,[]);
        var first=ItemChecks.File(Side.Dropbox,"parent/child",1,"child"); var last=ItemChecks.File(Side.Dropbox,first.Path,2,first.Id);
        db.Seed(last,[2]); db.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        var full=Observe.On(pc,(op,_)=>op==nameof(ISide.StageAsync)?throw new IOException():null);
        bool consumedLast=false; var prior=new Change(Side.Dropbox,ChangeKind.Changed,first.Path,null,first.Id,first,false,false);
        bool consumedTomb=false;var reports=new[]{prior,prior with {Kind=ChangeKind.Deleted,Id=null,Observed=null,ConsumeHeld=()=>consumedTomb=true},prior with {Observed=last,ConsumeHeld=()=>consumedLast=true}};
        var successor=last with {Id="newer-id"};var pin=new IdentityLocation(pair.Roots!.Dropbox.RootNamespaceId,PathNames.RootedDropboxPath(pair.DropboxRoot,last.Path),successor);
        IEnumerable<Change> Capture(Scope _){if(olderSnapshot){
            var next=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,last.Path,new([new(Side.Dropbox,last.Path,successor.Id)],Side.Dropbox){DropboxVersion=pin},DateTimeOffset.UtcNow.AddTicks(1),false);
            store.SaveNoticeAsync(pair.PairId,next,default).GetAwaiter().GetResult();
            while(DateTimeOffset.UtcNow<next.At) Thread.SpinWait(1);
        }return reports;}
        var arbitration=Observe.Wrap<IStore>(store,(op,args)=>olderSnapshot && op==nameof(IStore.SaveNoticeAsync) && args[1] is Notice incoming && store.OpenNotices.Any(n=>n.Path==incoming.Path && n.At>incoming.At)?Task.FromResult<Notice?>(null):null);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},paths,b,binding,Side.Dropbox,ActionKind.DeleteDropbox,full,DeleteSide(db),arbitration,Capture,(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default,coversThrough:Admitted(pc,db));
        if(olderSnapshot){Check.Require(!consumedLast && store.OpenNotices.Single().Subject.DropboxVersion==pin,"Late held snapshot displaced newer custody");return;}
        Check.Require(!consumedLast && store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==last.Path),"Failed put-back lost its newest held revision or its Retry notice");
        var notice=store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack); pc.PublishFailures.Clear();
        db.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        Check.Require(notice.Subject.DropboxVersion?.Entry==last && notice.At!=DateTimeOffset.UnixEpoch,"Held child failure did not retain the exact verified Dropbox revision");
        db.DeletedHistory[last.Path]=new DeletionEvidence.DeletedFile(last.Path,pair.Roots!.Dropbox.RootNamespaceId,last.Id,"later",DateTimeOffset.UnixEpoch,null,first.ContentHash,null);
        var tombBeforeRetry=consumedTomb; int parentAcks=0,lateTombAcks=0;
        var parentDelete=new Change(Side.Local,ChangeKind.Deleted,a.Path,null,a.Id,null,false,true){ConsumeHeld=()=>parentAcks++};
        var range=new Scope(a.Path,b.Path);
        bool retried=await HeldRecovery.RetryHeldAsync(pair,new(last.Path,last.Path),notice,pc,History(db),store,s=>s==range?[parentDelete,..reports]:[..reports,prior with {Kind=ChangeKind.Deleted,Id=null,Observed=null,ConsumeHeld=()=>lateTombAcks++}],(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(parentAcks==0 && lateTombAcks==0 && db.Mutations.Count(id=>id==b.Id)==1,"Child retry acknowledged an original parent whose binding was not settled");
        var bound=await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(last.Path),default);
        Check.Require(bound!=null && bound.LocalId==pc.Items[last.Path].Entry.Id && bound.DropboxId==db.Items[last.Path].Entry.Id && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Missing exact recovered binding or still-open notice");
        Check.Require(retried && consumedLast && consumedTomb==tombBeforeRetry && pc.Items[last.Path].Bytes.SequenceEqual(new byte[]{2}) && db.Items[last.Path].Bytes.SequenceEqual(new byte[]{2}),"Retry consumed the child without restoring the last reported bytes on both sides");
    }

    internal static async Task MappedRetryAsync(string mode)
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var parent=ItemChecks.File(Side.Dropbox,"cloud",0,"folder",true);var localParent=parent with {Path="mapped",Id="local-folder"};
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(localParent,parent),null,null,default);
        var v=ItemChecks.File(Side.Dropbox,"cloud/c",2,"child",mode=="folder-grant");var ns=pair.Roots!.Dropbox.RootNamespaceId;
        var subject=new IssueSubject([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox);
        if(mode is not ("legacy-mapped" or "legacy-failure" or "folder-grant")) subject=subject with {DropboxVersion=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,v.Path),v)};
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,subject,DateTimeOffset.UnixEpoch,false),default);
        var notice=store.OpenNotices.Single();var r3=ItemChecks.File(Side.Dropbox,v.Path,3,v.Id) with {Modified=v.Modified};
        var edit=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);int echoAcks=0;var tomb=edit with {Kind=ChangeKind.Deleted,Id=null,Observed=null,ConsumeHeld=()=>echoAcks++};
        db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([2]));
        db.DeletedHistory[v.Path]=new DeletionEvidence.DeletedFile(v.Path,ns,v.Id,r3.Revision,DateTimeOffset.UnixEpoch,null,r3.ContentHash,null);
        if(mode=="moved-away") db.Seed(v with {Path="elsewhere/c"},[2]);
        bool attempted=false;var source=Observe.On(History(db),(op,args)=>{
            if(mode=="legacy-failure" && op==nameof(ISide.RecoverAsync)){
                attempted=args[0] is IdentityLocation loc && loc.Domain==ns && loc.NativePath==PathNames.RootedDropboxPath(pair.DropboxRoot,v.Path) && loc.Entry==v && args[1] is Entry requested && requested==v;
                Check.Require(store.OpenNotices.Single().Subject.DropboxVersion?.Entry==v && store.OpenNotices.Single().At==notice.At,"Legacy recovery failed before its exact checkpoint");throw new IOException();}
            return null;
        });
        if(mode=="stale-echo") r3=ItemChecks.File(Side.Dropbox,v.Path,1,v.Id);
        if(mode=="stale-echo") db.DeletedHistory[v.Path]=new DeletionEvidence.DeletedFile(v.Path,ns,v.Id,v.Revision,DateTimeOffset.UnixEpoch,null,v.ContentHash,null);
        bool entered=false;IEnumerable<Change> Held(Scope _) {entered=true;return mode is "backdated" or "stale-echo"?[edit with {Observed=r3},tomb,tomb,tomb,edit]:[edit];} // CONTRACT 4.2: "append the pinned report after the echoes, for both modes"
        var newer=notice with {At=DateTimeOffset.UnixEpoch.AddTicks(1),Subject=subject with {DropboxVersion=new(ns,PathNames.RootedDropboxPath(pair.DropboxRoot,v.Path),r3)}};
        bool populated=false;
        var targetStore=Observe.Wrap<IStore>(store,(op,args)=>{
            if(mode!="stage-race" || op!=nameof(IStore.SaveNoticeAsync) || args[1] is not Notice n || !n.Subject.Items.Any(i=>i.Side==Side.Local)) return null;
            store.SaveNoticeAsync(pair.PairId,newer,default).GetAwaiter().GetResult();
            return newer.At>n.At?Task.FromResult<Notice?>(null):null;
        });
        Task<bool> Retry()=>HeldRecovery.RetryHeldAsync(pair,new(v.Path,v.Path),notice,pc,source,targetStore,Held,async (_,_,_,_)=>{
            if(mode=="folder-grant"){
                var child=ItemChecks.File(Side.Local,"mapped/c/child",1,"local-child");using var cancel=new CancellationTokenSource();
                var nested=new ItemLocks().RunAsync(pair,new(ActionKind.DeleteLocal,Row.ForOneSide(Side.Local,child.Path,child,null,[],null,RowContext.Neutral)),[],()=>Task.FromResult(true),cancel.Token);
                if(!nested.IsCompleted){cancel.Cancel();try{await nested;}catch(OperationCanceledException){} throw new InvalidDataException("Folder population waited on its own child grant");}await nested;
            }populated=true;},default);
        if(mode=="stage-race"){Check.Require(!await Retry() && store.OpenNotices.Single().Subject.DropboxVersion==newer.Subject.DropboxVersion && pc.Requests[nameof(ISide.PublishAsync)]==0,"Stage checkpoint displaced newer identification");return;}
        if(mode=="folder-grant"){
            db.Seed(v,[]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.Folder(v,db.ReadAllAsync(v.Path,new(false,true),default));
            var child=ItemChecks.File(Side.Local,"mapped/c/child",1,"local-child");var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder=new ItemLocks().RunAsync(pair,new(ActionKind.DeleteLocal,Row.ForOneSide(Side.Local,child.Path,child,null,[],null,RowContext.Neutral)),[],()=>release.Task,default);
            Task<bool> pending;try{pending=Retry();Check.Require(!populated && !pending.IsCompleted,"Folder populated under a held receiving child");}finally{release.SetResult(true);await holder;}
            Check.Require(await pending && populated,"Folder did not populate after child release");return;
        }
        if(mode=="backdated"){Check.Require(!await Retry() && pc.Items.Count==0 && db.Items.Count==0 && store.OpenNotices.Contains(notice) && db.Requests[nameof(ISide.DeletedAsync)]==1,"Backdated independent deletion was reversed");Check.Require(echoAcks==0,"A history-corroborated tombstone was acknowledged as an echo");return;} // "backdated ... requires echoAcks==0"
        if(mode=="moved-away"){Check.Require(!await Retry() && pc.Items.Count==0 && db.Items.Count==1 && db.Items.ContainsKey("elsewhere/c") && store.OpenNotices.Any(n=>n.Subject.DropboxVersion==subject.DropboxVersion),"Live moved ID was published again");return;}
        if(mode=="legacy-failure"){Check.Require(!await Retry() && attempted && store.OpenNotices.Single().Subject.DropboxVersion?.Entry==v && store.OpenNotices.Single().At==notice.At,"Legacy recovery failed before its exact checkpoint");return;}
        Task<bool> retry;
        if(mode=="lease")
        {
            var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder=new ItemLocks().RunAsync(pair,new(ActionKind.DeleteLocal,Row.ForOneSide(Side.Local,localParent.Path,localParent,null,[],null,RowContext.Neutral)),[],()=>release.Task,default);
            try {retry=Retry();Check.Require(!retry.IsCompleted && !entered,"Manual retry entered recovery while selected removal held its physical grant");}
            finally {release.SetResult(true);await holder;}
        }
        else retry=Retry();
        Check.Require(await retry && pc.Items.TryGetValue("mapped/c",out var a) && a.Bytes.SequenceEqual(new byte[]{2}) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{2}),"Mapped held edit did not recover after grant release");
        if(mode=="stale-echo") Check.Require(db.Requests[nameof(ISide.DeletedAsync)]==1,"Repeated stale echoes read history more than once");
        if(mode=="stale-echo") Check.Require(echoAcks==3,"Superseded path-only echoes were not each acknowledged: "+echoAcks); // "stale-echo requires echoAcks==3 (positive)"
    }
    private static Task PartialAsync()=>Replay(0);
    private static Task OutsideAsync()=>Replay(1);
    private static Task TombstoneAsync()=>Replay(2);
    private static Task ForeignTombstoneAsync()=>Replay(3);
    private static Task ExcludedAsync()=>Replay(4);
    private static async Task Replay(int scenario)
    {
        var pair=BoundPair(); var pc=new FakeSide(Side.Local); var db=new FakeSide(Side.Dropbox); using var store=new InMemoryStateStore();
        const string path="p/item"; var version=ItemChecks.File(Side.Local,path,2,"old"); pc.History.Add((version,[2]));
        if(scenario==4) pair=pair with {Exclusions=[new(Side.Local,"p",false)]};
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,path,new([new(Side.Local,path,version.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        Notice current()=>store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack);
        bool released=false,foreign=false;var edit=new Change(Side.Local,ChangeKind.Changed,path,null,version.Id,version,true,false){ConsumeHeld=()=>released=true};
        Change[] evidence=scenario==2?[edit with {Kind=ChangeKind.Deleted,Observed=null}]:scenario==3?[edit,edit with {Kind=ChangeKind.Deleted,Observed=null,Id="different",ConsumeHeld=()=>foreign=true}]:[edit];
        var scope=scenario==1?new Scope("else","else"):new Scope("p","p");
        Task<bool> ReplayNotice()=>HeldRecovery.RetryHeldAsync(pair,scope,current(),pc,db,store,_=>evidence,(_,_,_,_)=>throw new InvalidOperationException(),default);
        if(scenario==0){db.PublishFailures[path]=new ItemFailedException(path,TextKey.FailLocalInUse,null);Check.Require(!await ReplayNotice() && pc.Items.ContainsKey(path) && !released,"First restored leg or original evidence lost");db.PublishFailures.Clear();}
        var result=await ReplayNotice();
        if(scenario==1) Check.Require(!result && !released && current().Path==path,"Outside scope incorrectly settled");
        else Check.Require(result && released && !foreign && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack) && (scenario==2 || db.Items[path].Bytes.SequenceEqual(new byte[]{2})) && (scenario!=4 || !pc.Items.ContainsKey(path)),"Wrong replay result, notice custody, or affected endpoint");
    }
    private static Task UnreadableAsync()=>Removal(true);
    private static Task CostAsync()=>Removal(false);
    private static async Task Removal(bool partial)
    {
        var pair=ItemChecks.Pair(); if(partial) pair=pair with {Exclusions=[new(Side.Local,"p/kept",true)]};
        var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);
        foreach(var name in new[]{"a","b","c","d"}){var v=ItemChecks.File(Side.Local,"p/"+name,1,name);pc.Seed(v,[1]);if(!partial) await store.CommitAsync(pair.PairId,null,SyncedEntries.From(v,v with {Id="remote-"+name}),null,null,default);}
        if(partial)pc.ReadFailures["p/d"]=new ItemFailedException("p/d",TextKey.ReadLocalFailed,null);
        var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var removal=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(partial?pc.History.Count==0:removal.Outcome==RemovalOutcome.Removed && pc.Items.Count==0 && pc.History.Count(x=>x.Bytes.SequenceEqual(new byte[]{1}))==4 && pc.Requests[nameof(ISide.ReadOneAsync)]<4 && db.Requests[nameof(ISide.ReadOneAsync)]<4,"Sibling removed before unreadable result, or recursive removal probed every saved child");
    }

    public class Observe : System.Reflection.DispatchProxy
    {
        private object target=null!;private Func<string,object?[],object?> intercept=null!;
        internal static ISide On(ISide target,Func<string,object?[],object?> intercept)
        {
            return Wrap(target,intercept);
        }
        internal static T Wrap<T>(T target,Func<string,object?[],object?> intercept) where T:class
        {
            var side=Create<T,Observe>();var proxy=(Observe)(object)side;proxy.target=target;proxy.intercept=intercept;return side;
        }
        protected override object? Invoke(System.Reflection.MethodInfo? method,object?[]? args)
        {
            var result=intercept(method!.Name,args??[]);if(result!=null)return result;
            try{return method.Invoke(target,args);}catch(System.Reflection.TargetInvocationException ex) when(ex.InnerException!=null){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();throw;}
        }
    }
    private static async Task NamespaceAsync()
    {
        var pair=BoundPair();var db=new FakeSide(Side.Dropbox);var local=new FakeSide(Side.Local);using var store=new InMemoryStateStore();var v=ItemChecks.File(Side.Dropbox,"p/item",1,"rev-id");
        var remote=Observe.On(db,(op,args)=>op==nameof(ISide.RecoverAsync)?Task.FromResult<Recovery?>(((IdentityLocation)args[0]!).Domain==pair.Roots!.Dropbox.RootNamespaceId?new Recovery.File(v,new MemoryStream([1])):null):null);
        var report=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        bool ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(),local,remote,store,_=>[report],(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(ok && local.Items[v.Path].Bytes.SequenceEqual(new byte[]{1}) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{1}),"Historical recovery used connection instead of bound namespace");
    }
    private static async Task MappedAsync()
    {
        var pair=BoundPair() with {Exclusions=[new(Side.Local,"different/kept",true)]};var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var selected=ItemChecks.File(Side.Dropbox,"p",0,"root",true);var child=ItemChecks.File(Side.Dropbox,"p/item",1,"db-child");var other=ItemChecks.File(Side.Local,"different/kept",1,"pc-child");db.Seed(selected,[]);db.Seed(child,[1]);
        await store.CommitAsync(pair.PairId,null,SyncedEntries.From(other,child),null,null,default);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selected,null,Side.Dropbox,ActionKind.DeleteDropbox,pc,DeleteSide(db),store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(db.Items.ContainsKey(child.Path) && db.History.Count==0,"Mapped excluded child was removed");
    }
    private static async Task FolderRetryAsync()
    {
        var pair=BoundPair();var db=new FakeSide(Side.Dropbox);var pc=new FakeSide(Side.Local);using var store=new InMemoryStateStore();var v=ItemChecks.File(Side.Dropbox,"p/folder",0,"folder",true);db.Seed(v,[]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.Folder(v,db.ReadAllAsync(v.Path,new(false,true),default));
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        var reviewed=store.OpenNotices.Single();var next=reviewed with {Subject=new([new(Side.Dropbox,v.Path,"successor")],Side.Dropbox)};
        var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        var ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),reviewed,pc,db,store,_=>[c],(_,_,_,ct)=>store.SaveNoticeAsync(pair.PairId,next,ct),default);
        Check.Require(!ok && store.OpenNotices.Single().Matches(next),"Folder Retry reported success while reviewed notice was superseded");
    }

    private static Task ReceiptOrderAsync()=>ReceiptAsync(false);
    private static Task LateMergeAsync()=>ReceiptAsync(true);
    private static async Task ReceiptAsync(bool unbound)
    {
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"p",0,"root",true);var first=ItemChecks.File(Side.Local,"p/child",1,"selected");var last=ItemChecks.File(Side.Local,first.Path,2,first.Id);pc.Seed(folder,[]);
        if(!unbound){pc.Seed(first,[1]);await store.CommitAsync(pair.PairId,null,SyncedEntries.From(first,first with {Id="peer"}),null,null,default);}
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;yield return new(last.Path,last);}
        async Task<RemovalResult> Remove(){pc.Items[last.Path]=(last,[2]);var result=await pc.DeleteAsync(folder,null,default);return result with {Recycled=Receipt()};}
        pc.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        var local=Observe.On(pc,(op,a)=>op==nameof(ISide.DeleteAsync)?Remove():op==nameof(ISide.RecoverAsync) && a[1] is Entry {ContentHash:""} candidate && candidate.Id==last.Id?Task.FromResult<Recovery?>(new Recovery.File(last,new MemoryStream([2]))):!unbound && op==nameof(ISide.ReadByIdAsync) && (string?)a[0]==last.Id && pc.History.Count!=0?Task.FromResult<(IdentityLocation,bool)?>( (new(FakeSide.Volume,"bin",last with {ContentHash=""}),false)):null);bool ack=false;
        var stale=new Change(Side.Local,ChangeKind.Changed,first.Path,null,first.Id,first,true,false){ConsumeHeld=()=>ack=true};
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,null,Side.Local,unbound?ActionKind.MoveLocal:ActionKind.DeleteLocal,local,db,store,_=>unbound?[]:[stale],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require((ack||unbound) && pc.Items.TryGetValue(last.Path,out var localCopy) && localCopy.Bytes.SequenceEqual(new byte[]{2}) && db.Items.TryGetValue(last.Path,out var remoteCopy) && remoteCopy.Bytes.SequenceEqual(new byte[]{2}),"Verified newer recycled edit or unbound late merge child was lost");
    }

    private static async Task CrashAfterFirstAsync()
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var folder=ItemChecks.File(Side.Dropbox,"p",0,"parent",true);db.Seed(folder,[]);
        var v=ItemChecks.File(Side.Dropbox,"p/item",3,"original");db.Seed(v,[3]);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
        var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);var stop=new OperationCanceledException();
        async Task<RemovalResult> Crash(object?[] a){await pc.PublishAsync((IStaged)a[0]!,null,null,default);throw stop;}
        var broken=Observe.On(pc,(op,args)=>op==nameof(ISide.PublishAsync)?Crash(args):null);
        try{await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,null,Side.Dropbox,ActionKind.DeleteDropbox,broken,DeleteSide(db),store,_=>[c],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));}catch(OperationCanceledException ex) when(ex==stop){}
        var notice=store.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.PutBack);Check.Require(notice!=null && notice.Subject.Items.Any(m=>m.Side==Side.Local && m.Id==pc.Items[v.Path].Entry.Id),"Restart has no attributable first publication identity");
        Check.Require(await HeldRecovery.RetryHeldAsync(pair,new("p","p"),notice!,pc,History(db),store,_=>[c],(_,_,_,_)=>Task.CompletedTask,default) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{3}) && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack),"Restart rejected the already-published first leg");
    }

    // The fake stores one stream; a provider opens a fresh stream on each exact recovery.
    private static ISide History(FakeSide db)=>Observe.On(db,(op,args)=>op==nameof(ISide.RecoverAsync) && args[0] is IdentityLocation loc && args[1] is Entry v && db.Recoverable.GetValueOrDefault((loc.Entry.Id,v.Revision,v.ContentHash)) is Recovery.File {Bytes:MemoryStream bytes} file?Task.FromResult<Recovery?>(file with {Bytes=new MemoryStream(bytes.ToArray(),false)}):null);

    private sealed class FailingRead : MemoryStream
    {
        public override Task CopyToAsync(Stream destination,int bufferSize,CancellationToken ct)=>Task.FromException(new IOException());
    }
    private static Task LostReplyAsync()=>Safety(false);
    private static async Task NewerAsync()
    {
        await Safety(true);
        foreach(bool pinned in new[]{true,false}) foreach(bool live in new[]{false,true})
        foreach(char l in "ESDF") foreach(char d in "ESDF") foreach(bool applied in new[]{false,true})
            await RetryCell(pinned,live,l,d,applied);
        foreach(bool pinned in new[]{true,false}) foreach(bool persistent in new[]{true,false})
        foreach(string fault in new[]{nameof(ISide.ReadOneAsync),nameof(ISide.ContentHashAsync),"DropboxRead"})
            await RetryCell(pinned,false,'S','E',false,fault,persistent);
        foreach(bool pinned in new[]{true,false}) {await RetryCell(pinned,false,'S','E',false,"Stream");await RetryCell(pinned,false,'S','E',false,"HashStream",true);await RetryCell(pinned,false,'E','E',false,"LocalCreate",true);}
    }
    private static async Task RetryCell(bool pinned,bool live,char l,char d,bool applied,string? fault=null,bool persistent=false)
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var v=ItemChecks.File(Side.Dropbox,"p/item",3,"old");var exact=new IdentityLocation(pair.Roots!.Dropbox.RootNamespaceId,PathNames.RootedDropboxPath(pair.DropboxRoot,v.Path),v);
        byte lb=(byte)(l=='D'?5:3),dbb=(byte)(d=='D'?7:3);var incumbent=ItemChecks.File(Side.Local,v.Path,lb,"local");
        if(l!='E') pc.Seed(incumbent,[lb]);if(d!='E') db.Seed(ItemChecks.File(Side.Dropbox,v.Path,dbb,"other"),[dbb]);
        if(live) db.Seed(v with {Path="elsewhere"},[3]);
        var subject=new IssueSubject([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox){DropboxVersion=pinned?exact:null};
        if(fault!=null && l!='E') subject=subject with {Items=[..subject.Items,new(Side.Local,v.Path,incumbent.Id)]};
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,subject,DateTimeOffset.UnixEpoch,false),default);
        var held=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);var path=new Scope(v.Path,v.Path);
        int failures=0;bool Fail(string op)=>fault==op && (++failures==1 || persistent);
        int opens=0;
        var local=Observe.On(pc,(op,_)=>fault=="HashStream" && op==nameof(ISide.ContentHashAsync)?HashStream():fault=="Stream" && op==nameof(ISide.OpenReadAsync)?Open():fault=="LocalCreate" && op==nameof(ISide.StageAsync)?throw new ItemFailedException(v.Path,TextKey.FailLocalNoSpace,null):l=='F' && op==nameof(ISide.ReadOneAsync) || Fail(op)?throw new IOException():null);
        Task<Stream> Open(){opens++;return Task.FromResult<Stream>(new FailingRead());}
        async Task<string> HashStream(){await using var stream=await Open();await stream.CopyToAsync(Stream.Null);return v.ContentHash;}
        bool pinnedAtCreate=false;
        var cloud=Observe.On(History(db),(op,args)=>{if(op==nameof(ISide.PublishAsync)) pinnedAtCreate=args[1]==null && args[2]==null && store.OpenNotices.Any(n=>n.Subject.DropboxVersion==exact && n.At==DateTimeOffset.UnixEpoch);return op==nameof(ISide.ReadOneAsync) && (d=='F' || Fail("DropboxRead"))?throw new IOException():null;});
        for(int attempt=0;attempt<(fault==null || fault=="Stream"?1:2);attempt++)
        {
            int calls=0;bool correct=false;db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
            var ok=await HeldRecovery.RetryHeldAsync(pair,path,store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack),local,cloud,store,_=>pinned || attempt!=0?[]:[held],(_,_,_,_)=>Task.CompletedTask,default,
                (scope,side,entry,_)=>{calls++;bool both=pc.Items.TryGetValue(v.Path,out var a) && db.Items.ContainsKey(v.Path);
                    correct=both && scope==path && side==Side.Dropbox && entry==v && a.Entry==incumbent && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{3});
                    return Task.FromResult(both && applied && (a.Entry.ContentHash==v.ContentHash || db.Items[v.Path].Entry.ContentHash==v.ContentHash)?EffectOutcome.Applied:EffectOutcome.Pending);});
            if(fault is "Stream" or "HashStream" or "LocalCreate")
            {
                bool unchanged=pc.Items.TryGetValue(v.Path,out var existing)?l=='S' && existing.Entry==incumbent && existing.Bytes.SequenceEqual(new[]{lb}):l=='E';
                bool pending=!ok && store.Records.Count==0 && store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Subject.DropboxVersion==exact && n.At==DateTimeOffset.UnixEpoch && n.Cause==(fault=="LocalCreate"?TextKey.FailLocalNoSpace:TextKey.FailChanged));
                Check.Require(pinnedAtCreate && unchanged && db.Items.TryGetValue(v.Path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{3}) && db.Requests[nameof(ISide.PublishAsync)]==1 && db.Mutations.Count==0 && pc.Mutations.Count==0 && calls==0 && (fault=="Stream"?opens==0 && ok:pending && (fault!="HashStream" || opens>0)),"Independent Dropbox create did not preserve the pinned bytes and Local outcome");
            }
            bool blocked=l=='F' || d=='F' || fault!=null && fault!="Stream" && (attempt==0 || persistent);
            bool free=!live && d=='E' && !(fault=="DropboxRead" && blocked);bool createLocal=fault!="LocalCreate" && !live && l=='E' && d is 'E' or 'S';
            bool handoff=!blocked && fault==null && (d=='D' || d=='S' && l!='E' && (!live || l=='D') || free && l is 'S' or 'D');
            bool settled=!blocked && (createLocal || fault!=null || handoff && applied && (d!='D' || l=='S'));
            bool bytes=pc.Items.TryGetValue(v.Path,out var p)==(l!='E' || createLocal) && (p.Entry==null || p.Bytes.SequenceEqual(new[]{lb})) && db.Items.TryGetValue(v.Path,out var q)==(d!='E' || free) && (q.Entry==null || q.Bytes.SequenceEqual(new[]{dbb}));
            if(pinned && !live && l=='D' && d=='E') Check.Require(bytes,"Pinned recovery failed to preserve Local and restore exact Dropbox bytes");
            if(!live && l=='F' && d=='E') Check.Require(bytes && !ok,"Unreadable Local stranded exact Dropbox recovery");
            if(!live && l=='D' && d=='E') Check.Require(calls==1 && correct,"Recovered live pair did not reach its exact conflict handoff once");
            Check.Require(bytes && ok==settled && calls==(handoff?1:0) && (settled?!store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack):store.OpenNotices.Any(n=>n.Subject.DropboxVersion==exact && n.At==DateTimeOffset.UnixEpoch)), $"Retry matrix {pinned}/{live}/{l}/{d}/{applied}/{fault}/{attempt}");
        }
    }
    private static async Task Safety(bool newer)
    {
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var v=ItemChecks.File(Side.Dropbox,"p/item",3,"old");
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,v.Path,new([new(Side.Dropbox,v.Path,v.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);db.Recoverable[(v.Id,v.Revision,v.ContentHash)]=new Recovery.File(v,new MemoryStream([3]));
        var change=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false);
        Task<bool> Retry(ISide cloud)=>HeldRecovery.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack),pc,cloud,store,_=>[change],(_,_,_,_)=>Task.CompletedTask,default);
        if(newer){pc.Seed(ItemChecks.File(Side.Local,v.Path,5,"new"),[5]);Check.Require(!await Retry(db) && pc.Items[v.Path].Bytes.SequenceEqual(new byte[]{5}) && db.Items.TryGetValue(v.Path,out var kept) && kept.Bytes.SequenceEqual(new byte[]{3}) && store.Records.Count==0,"Recovery did not preserve both versions outside history");return;}
        async Task<RemovalResult> Lost(object?[] a){await db.PublishAsync((IStaged)a[0]!,null,null,default);throw new OperationCanceledException();}
        try{await Retry(Observe.On(History(db),(op,args)=>op==nameof(ISide.PublishAsync)?Lost(args):null));}catch(OperationCanceledException){}
        Check.Require(await Retry(db) && pc.Items[v.Path].Bytes.SequenceEqual(new byte[]{3}) && db.Items[v.Path].Bytes.SequenceEqual(new byte[]{3}) && store.Records.Values.Single().DropboxId==db.Items[v.Path].Entry.Id && db.Requests[nameof(ISide.PublishAsync)]==1,"Unknown Dropbox publication stranded or resent an equal version");
    }
    private static async Task KeptTreeAsync()
    {
        var pair=BoundPair() with {Exclusions=[new(Side.Local,"p",false)]};using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"old",true);var child=ItemChecks.File(Side.Local,"p/quiet/file",4,"child");var empty=ItemChecks.File(Side.Local,"p/empty",0,"empty",true);
        pc.History.Add((root,[]));pc.History.Add((child,[4]));async IAsyncEnumerable<Listed> Contents(){await Task.CompletedTask;yield return new(empty.Path,empty);yield return new(child.Path,child);}
        pc.Recoverable[(root.Id,root.Revision,root.ContentHash)]=new Recovery.Folder(root,Contents());var c=new Change(Side.Local,ChangeKind.Changed,root.Path,null,root.Id,root,false,false);await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,root.Path,new([new(Side.Local,root.Path,root.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        bool delegated=false;
        var ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(),pc,db,store,_=>[c],(range,side,verified,ct)=>{delegated=range==new Scope("p","p") && side==Side.Local && verified.Root==root;return Task.CompletedTask;},default);
        Check.Require(ok && delegated && pc.Items.Count==0 && db.Items.Count==0,"Kept-only recovery wrote outside the add-only population owner");
    }

    private static async Task ConflictAsync()
    {
        foreach(bool afterSend in new[]{false,true})
        foreach(var outcome in new EffectOutcome?[]{EffectOutcome.Applied,EffectOutcome.Refused,EffectOutcome.Pending,null}){
            if(afterSend && outcome!=EffectOutcome.Pending) continue;
            var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Dropbox,"p",0,"root",true);var v=ItemChecks.File(Side.Dropbox,"p/f",2,"child");db.Seed(root,[]);db.Seed(v,[2]);bool called=false,ack=false;bool applied=outcome==EffectOutcome.Applied;
            var c=new Change(Side.Dropbox,ChangeKind.Changed,v.Path,null,v.Id,v,false,false){ConsumeHeld=()=>ack=true};
            var res=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Dropbox,ActionKind.ReplaceKind,pc,DeleteSide(db),store,_=>afterSend && db.Mutations.Count==0?[]:[c],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,resolveConflict:(scope,side,version,ct)=>{if(afterSend) Check.Require(store.OpenNotices.SingleOrDefault()?.Subject.DropboxVersion==new IdentityLocation(pair.Roots!.Dropbox.RootNamespaceId,PathNames.RootedDropboxPath(pair.DropboxRoot,v.Path),v),"Post-delete replacement reached resolution without its exact pin");else Check.Require(store.OpenNotices.Count==0,"Live replacement wrote custody before ordinary resolution");called=scope.DropboxPath==v.Path && side==Side.Dropbox && version==v && db.Items.ContainsKey(v.Path)!=afterSend;if(outcome==null)throw new ItemFailedException(v.Path,TextKey.FailChanged,null);return Task.FromResult(outcome.Value);},coversThrough:Admitted(pc,db));
            if(afterSend){Check.Require(called && !ack && pc.Items.Count==0 && !db.Items.ContainsKey(v.Path) && store.OpenNotices.Single().Subject.DropboxVersion?.Entry==v,"Pending replacement lost its exact post-delete revision");continue;}
            Check.Require(applied || store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Subject.Items.Any(i=>i.Id==v.Id && i.Path==v.Path)),"Unsettled replacement handoff left no exact child PutBack");
            Check.Require(called && ack==applied && (res.Outcome!=RemovalOutcome.Removed && db.Items.ContainsKey(v.Path) && db.History.Count==0) && pc.Items.Count==0,"Replacement discarded conflict outcome or restored over the replacement");}
    }
    private static async Task CoveredAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var row=new Row(saved.LocalKey,root,null,saved,[],null,RowContext.Neutral with {CompleteInventory=true,ProtectedContents=false,Destination="p"});
        async IAsyncEnumerable<Listed> Large(){await Task.CompletedTask;for(int i=0;i<960000;i++)yield return new("p/"+i,root with {Path="p/"+i,IsFolder=false});}
        var source=Observe.On(pc,(op,args)=>op==nameof(ISide.ReadAllAsync)?throw new InvalidOperationException("Reopened covered 960000-item listing: "+Large().GetType().Name):null);
        var res=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,source,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,covered:row,coversThrough:Admitted(pc,db));
        Check.Require(res.Outcome==RemovalOutcome.Removed,"Covered folder did not settle");
    }

    private static async Task ReceiptCoverageAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var saved=SyncedEntries.From(root,root with {Id="gone"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var covered=new Row(saved.LocalKey,root,null,saved,[],null,RowContext.Neutral with {CompleteInventory=true,ProtectedContents=false,Destination="p"});
        int visits=0;bool removed=false,proved=false;var mark=new JournalPosition(7,42);
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;for(int i=0;i<960000;i++){visits++;if(visits>1)throw new InvalidOperationException("Repeated the covered receipt walk");yield return new("p/old",null);}}
        async Task<RemovalResult> Remove(object?[] args){var result=await pc.DeleteAsync(root,(RemovalSelection?)args[1],default);removed=true;return result with {Recycled=Receipt()};}
        var a=Observe.On(pc,(op,args)=>op==nameof(ISide.DeleteAsync)?Remove(args):op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((null,mark)):null);
        var b=Observe.On(db,(op,args)=>op==nameof(ISide.BoundaryAsync)?Task.FromResult<(string?,JournalPosition?)>((removed?"after":"before",null)):null);
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,saved,Side.Local,ActionKind.DeleteLocal,a,b,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,covered:covered,coversThrough:fence=>removed?(proved=fence.Local==mark && fence.Dropbox=="after"):fence.Local==mark && fence.Dropbox=="before");
        Check.Require(proved && visits==0 && result.Outcome==RemovalOutcome.Removed,"Used stale coverage or enumerated proven unchanged receipt");
    }
    private static async Task ConflictingLiveAsync()
    {
        foreach(bool occupied in new[]{true,false}){
        var pair=BoundPair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var original=ItemChecks.File(Side.Dropbox,"p/f",1,"old");if(occupied)pc.Seed(ItemChecks.File(Side.Local,original.Path,2,"pc"),[2]);db.Seed(ItemChecks.File(Side.Dropbox,original.Path,3,"db"),[3]);db.Recoverable[(original.Id,original.Revision,original.ContentHash)]=new Recovery.File(original,new MemoryStream([1]));
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.FailChanged,original.Path,new([new(Side.Dropbox,original.Path,original.Id)],Side.Dropbox),DateTimeOffset.UnixEpoch,false),default);
        bool called=false,ack=false;var c=new Change(Side.Dropbox,ChangeKind.Changed,original.Path,null,original.Id,original,false,false){ConsumeHeld=()=>ack=true};
        var expected=new IdentityLocation(pair.Roots!.Dropbox.RootNamespaceId,PathNames.RootedDropboxPath(pair.DropboxRoot,original.Path),original);
        bool HasPin(Notice n)=>n.Subject.DropboxVersion==expected && n.At==DateTimeOffset.UnixEpoch;
        bool? pinnedAtProbe=null;
        object? FirstProbe(string op,object?[] args){if(op==nameof(ISide.ReadOneAsync) && (string?)args[0]==original.Path) pinnedAtProbe??=HasPin(store.OpenNotices.Single());return null;}
        Task<EffectOutcome> Pending(Scope scope,Side side,Entry v,CancellationToken ct){called=v==original && side==Side.Dropbox && scope.LocalPath==original.Path;return Task.FromResult(EffectOutcome.Pending);}
        var ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(),Observe.On(pc,FirstProbe),Observe.On(db,FirstProbe),store,_=>[c],(_,_,_,_)=>Task.CompletedTask,default,Pending);
        var pin=store.OpenNotices.Single();
        Check.Require(HasPin(pin),"Pending legacy conflict lost its exact revision or original timestamp");
        Check.Require(pinnedAtProbe==true,"First competition probe preceded durable exact revision or original timestamp");
        Check.Require(called && !ok && !ack && (occupied?pc.Items[original.Path].Bytes.SequenceEqual(new byte[]{2}):pc.Items.Count==0) && db.Items[original.Path].Bytes.SequenceEqual(new byte[]{3}) && pin.Kind==NoticeKind.PutBack,"Both occupied paths bypassed ordinary conflict recovery or discharged pending source");
        if(occupied) continue;
        var reopened=System.Text.Json.JsonSerializer.Deserialize<Notice>(System.Text.Json.JsonSerializer.Serialize(pin))!;
        called=false;
        ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),reopened,pc,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,default,Pending);
        Check.Require(called && !ok && pc.Items.Count==0 && db.Items[original.Path].Bytes.SequenceEqual(new byte[]{3}) && HasPin(store.OpenNotices.Single()),"Restarted pending conflict bypassed resolution or published into empty Local");}
    }

    /// <summary>Exercise whole-item restore against native identity and bytes; all paths belong to this temporary fixture.</summary>
    public static async Task WholeSubstituteAsync()
    {
        var home=Directory.CreateTempSubdirectory();var drive=Enumerable.Range('Q',10).Reverse().Select(n=>((char)n)+":").First(d=>!Directory.Exists(d+Path.DirectorySeparatorChar));bool mounted=false;
        async Task Mount(string target){var start=new System.Diagnostics.ProcessStartInfo("subst.exe"){UseShellExecute=false};start.ArgumentList.Add(drive);start.ArgumentList.Add(target);using var process=System.Diagnostics.Process.Start(start)!;await process.WaitForExitAsync();Check.Require(process.ExitCode==0,"Temporary test volume mapping failed");}
        try
        {
            await Mount(home.FullName);mounted=true;var volumeRoot=drive+Path.DirectorySeparatorChar;var root=Directory.CreateDirectory(Path.Combine(volumeRoot,"root")).FullName;var bin=Directory.CreateDirectory(Path.Combine(volumeRoot,ResourceNames.RootBinFolder)).FullName;
            var (volume,driveType)=DriveIdentity.Read(root);var pair=BoundPair();pair=pair with {LocalRoot=root,Roots=pair.Roots! with {Local=volume,LocalRoot=root}};
            using var store=new InMemoryStateStore();var local=LocalSide.Factory()(pair,()=>new(pair,[pair],driveType),ItemChecks.Settings,store);var cloud=new FakeSide(Side.Dropbox);
            foreach(bool folder in new[]{true,false})
            {
                var path=folder?"tree":"item";var original=Path.Combine(root,path);var receipt=Path.Combine(bin,path);
                Entry? prior=null;
                if(folder){Directory.CreateDirectory(original);await File.WriteAllBytesAsync(Path.Combine(original,"quiet"),[7]);}
                else{await File.WriteAllBytesAsync(original,[5]);prior=(await local.ReadOneAsync(path,default))!;prior=prior with {ContentHash=await local.ContentHashAsync(prior,default)};await File.WriteAllBytesAsync(original,[7]);}
                var version=(await local.ReadOneAsync(path,default))!;if(!folder)version=version with {ContentHash=await local.ContentHashAsync(version,default)};
                if(folder) Directory.Move(original,receipt);else File.Move(original,receipt);
                var selected=SyncedEntries.From(prior??(version with {Id="selected"}),(prior??version) with {Id="peer"});await store.CommitAsync(pair.PairId,null,selected,null,null,default);
                if(prior!=null)await store.ArmAsync(pair.PairId,new(new(prior,volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),prior.ContentHash),selected),null,default);
                if(!folder)await File.WriteAllBytesAsync(original,[9]);
                var locator=new IdentityLocation(volume.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),receipt,version);
                // Native ID lookup can spell this SUBST receipt through the underlying volume.
                async Task<(IdentityLocation Location,bool InScope)?> Locate(string id,CancellationToken token)
                {
                    var found=await local.ReadByIdAsync(id,token);
                    if(found is not {InScope:false} hit || hit.Location.Domain!=locator.Domain || hit.Location.Entry.Id!=version.Id) return found;
                    var physical=Path.Combine(home.FullName,ResourceNames.RootBinFolder,path);
                    Check.Require(PathNames.Same(hit.Location.NativePath,physical) || PathNames.Same(hit.Location.NativePath,receipt),"Native lookup did not locate the selected bin item");
                    return (hit.Location with {NativePath=receipt},false);
                }
                var endpoint=Observe.On(local,(op,args)=>op==nameof(ISide.ReadByIdAsync)?Locate((string)args[0]!, (CancellationToken)args[1]!):null);
                bool restored=await HeldRecovery.PutBackSubstituteAsync(pair,new(path,path),locator,endpoint,cloud,store,(_,_,_,_)=>throw new InvalidOperationException("Wrong population owner"),default);
                if(!folder)
                {
                    var reviewed=store.OpenNotices.SingleOrDefault(n=>n.Kind==NoticeKind.PutBack && n.Path==path);
                    Check.Require(!restored && reviewed!=null && reviewed.Cause==TextKey.ClashNameTaken && !reviewed.Ignored && !reviewed.Dismissed && reviewed.Subject.Incoming==Side.Local && reviewed.Subject.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,path,version.Id)}) && global::Massdex.SyncShark.Engine.Planner.Resolutions.Choices(reviewed).Contains(ResolutionChoice.Retry) && global::Massdex.SyncShark.Engine.Planner.Resolutions.Choices(reviewed).Contains(ResolutionChoice.Ignore),"Occupied bin item did not retain its exact Retry/Ignore notice");
                    Check.Require(File.ReadAllBytes(receipt).SequenceEqual(new byte[]{7}) && File.ReadAllBytes(original).SequenceEqual(new byte[]{9}),"Occupied name changed or binned version released");
                    File.Move(original,Path.Combine(home.FullName,"successor"));restored=await HeldRecovery.RetryHeldAsync(pair,new(path,path),reviewed!,endpoint,cloud,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),default);
                    Check.Require(!File.Exists(receipt),"Exact native Retry left the version in the bin");
                    Check.Require(restored,"Exact native Retry did not report restoration");
                    Check.Require(await store.NoticeAsync(pair.PairId,reviewed!.Id,default)==null && !store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Path==path),"Exact native Retry left its notice open");
                    Check.Require(await store.ArmedAsync(pair.PairId,default)==null,"Exact native Retry left its selection armed");
                }
                Check.Require(restored && (await local.ReadOneAsync(path,default))?.Id==version.Id && File.ReadAllBytes(folder?Path.Combine(original,"quiet"):original).SequenceEqual(new byte[]{7}) && !File.Exists(receipt) && !Directory.Exists(receipt) && cloud.Items.Count==0 && await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(path),default)==selected,"Whole-item restore lost identity, contents or selected binding");
            }
        }
        finally {if(mounted)await Mount("/D");home.Delete(true);}
    }
    private static async Task MovedOutEvidenceAsync()
    {
        foreach(bool affectedOnly in new[]{false,true}){
        var pair=ItemChecks.Pair();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);using var store=new InMemoryStateStore();
        var folder=ItemChecks.File(Side.Local,"old",0,"root",true);pc.Seed(folder,[]);var saved=SyncedEntries.From(folder,folder with {Id="peer"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        bool ack=false,compared=false;var move=new Change(Side.Local,ChangeKind.Renamed,"elsewhere",affectedOnly?null:"old/child","child",null,false,false){AffectedPath=affectedOnly?"old/child":null,ConsumeHeld=()=>ack=true};
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),folder,saved,Side.Local,ActionKind.DeleteLocal,pc,db,store,_=>[move],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>{compared=true;return Task.CompletedTask;},default,coversThrough:Admitted(pc,db));
        Check.Require(result.Outcome==RemovalOutcome.Retained && !ack && !compared && store.Records.Values.Single()==saved,"Held move out of the removal scope was treated as settled");}
    }
    private static async Task DefiniteNoEffectAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"old",0,"a",true);pc.Seed(root,[]);
        async Task<RemovalResult> Refuse(object?[] args){await store.ArmAsync(pair.PairId,(RemovalSelection)args[1]!,null,default);return new(RemovalOutcome.NoEffect,root,TextKey.FailChanged);}
        var side=Observe.On(pc,(op,args)=>op==nameof(ISide.DeleteAsync)?Refuse(args):null);
        await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(await store.ArmedAsync(pair.PairId,default)==null && pc.Items[root.Path].Entry==root,"Definite non-removal left the selected note armed");
        var next=new RemovalSelection(new(root with {Path="next",Id="next"},FakeSide.Volume,null),null);await store.ArmAsync(pair.PairId,next,null,default);
    }
    private static async Task SameIdRetryAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var before=ItemChecks.File(Side.Local,"p",1,"selected");var after=ItemChecks.File(Side.Local,"p",2,before.Id);var saved=SyncedEntries.From(before,before with {Id="peer"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        await store.ArmAsync(pair.PairId,new(new(before,FakeSide.Volume,before.ContentHash),saved),null,default);var receipt=new IdentityLocation(FakeSide.Volume,"bin",after);pc.Seed(before with {Id="occupant"},[1]);
        var side=Observe.On(pc,(op,args)=>op==nameof(ISide.ReadByIdAsync)?Task.FromResult<(IdentityLocation,bool)?>((receipt,false)):op==nameof(ISide.RestoreRecycledAsync)?Restore():null);
        Task<RemovalResult> Restore(){pc.Seed(after,[2]);return Task.FromResult(new RemovalResult(RemovalOutcome.Published,after,null));}
        Check.Require(!await HeldRecovery.PutBackSubstituteAsync(pair,new("p","p"),receipt,side,db,store,(_,_,_,_)=>Task.CompletedTask,default),"Occupied substitute name was overwritten");pc.Items.Clear();var notice=store.OpenNotices.Single();
        var ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),notice,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,default);
        Check.Require(ok && pc.Items["p"].Bytes.SequenceEqual(new byte[]{2}) && db.Items.Count==0 && store.Records.Values.Single()==saved && store.OpenNotices.Count==0 && await store.ArmedAsync(pair.PairId,default)==null,"Same-ID changed substitute retry did not settle its exact notice and original note");
        await store.ArmAsync(pair.PairId,new(new(before with {Id="next",Path="next"},FakeSide.Volume,before.ContentHash),null),null,default);
    }
    private static async Task LocatedFileRetryAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);
        var file=ItemChecks.File(Side.Local,"p",7,"substitute");var selected=SyncedEntries.From(file with {Id="selected"},file with {Id="peer"});await store.CommitAsync(pair.PairId,null,selected,null,null,default);
        var locator=new IdentityLocation(FakeSide.Volume,"receipt",file);var report=new Change(Side.Local,ChangeKind.Changed,file.Path,null,file.Id,file,true,false);
        await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.PutBack,TextKey.ClashNameTaken,file.Path,new([new(Side.Local,file.Path,file.Id)],Side.Local),DateTimeOffset.UnixEpoch,false),default);
        using var proof=new MemoryStream([7]);int reads=0,restores=0;
        var local=Observe.On(pc,(op,args)=>{
            if(op==nameof(ISide.ReadByIdAsync) && Equals(args[0],file.Id))return Task.FromResult<(IdentityLocation,bool)?>((locator,false));
            if(op==nameof(ISide.RecoverAsync)){reads++;return Task.FromResult<Recovery?>(new Recovery.File(file,proof));}
            if(op==nameof(ISide.RestoreRecycledAsync)){Check.Require(!proof.CanRead,"Located-file Retry still held the proof stream at rename");Check.Require(Equals(args[0],locator) && Equals(args[1],file),"Rename did not receive the verified version");restores++;pc.Seed(file,[7]);return Task.FromResult(new RemovalResult(RemovalOutcome.Published,file,null));}return null;});
        var ok=await HeldRecovery.RetryHeldAsync(pair,new(file.Path,file.Path),store.OpenNotices.Single(),local,db,store,_=>[report],(_,_,_,_)=>throw new InvalidOperationException(),default);
        Check.Require(ok && reads==1 && restores==1 && pc.Items[file.Path].Bytes.SequenceEqual(new byte[]{7}) && store.OpenNotices.Count==0 && store.Records.Values.Single()==selected,"Located-file Retry failed to release proof before its single exact restore");
    }
    private static Task SubstituteAsync()=>Substitute(false);
    private static Task StrandedAsync()=>Substitute(true);
    private static Task SubFolderAsync()=>Substitute(false,true);
    private static async Task Substitute(bool occupied,bool folder=false)
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var selected=ItemChecks.File(Side.Local,"p",0,"original",true);pc.Seed(selected,[]);var saved=SyncedEntries.From(selected,selected with {Id="peer"});await store.CommitAsync(pair.PairId,null,saved,null,null,default);
        var sub=ItemChecks.File(Side.Local,"p",7,"substitute",folder);var receipt=new IdentityLocation(FakeSide.Volume,"native-receipt",sub);bool seen=false;
        var side=Observe.On(pc,(op,args)=>{
            if(op==nameof(ISide.DeleteAsync)){pc.Items.Clear();if(occupied)pc.Seed(selected,[]);return Task.FromResult(new RemovalResult(RemovalOutcome.Retained,null,TextKey.RecoveryWrongRestored){RecycledVictim=receipt});}
            if(op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==selected.Id && pc.Items.ContainsKey("p"))return Task.FromResult<(IdentityLocation,bool)?>((new(FakeSide.Volume,"p",selected),true));
            if(op==nameof(ISide.ReadByIdAsync) && (string?)args[0]==sub.Id)return Task.FromResult<(IdentityLocation,bool)?>((receipt,false));
            if(op==nameof(ISide.RestoreRecycledAsync)){seen=Equals(args[0],receipt) && Equals(args[1],sub) && (string?)args[2]==sub.Path && !pc.Items.ContainsKey(sub.Path);if(!seen)throw new InvalidOperationException("Invalid physical restore request");pc.Seed(sub,folder?[]:[7]);return Task.FromResult(new RemovalResult(RemovalOutcome.Published,sub,null));}return null;});
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),selected,saved,Side.Local,ActionKind.ReplaceKind,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(result.Outcome!=RemovalOutcome.Removed && (occupied?pc.Items["p"].Entry==selected && store.OpenNotices.Any(n=>n.Kind==NoticeKind.PutBack && n.Subject.Items.Any(i=>i.Id==sub.Id)):seen && pc.Items.TryGetValue("p",out var at) && (folder?at.Entry.IsFolder:at.Bytes.SequenceEqual(new byte[]{7})) && db.Items.Count==0) && store.Records.Values.Single()==saved,folder?"Identified substitute folder was not restored at the free original name":"Verified substitute receipt was lost or the original binding was retired");
        if(occupied){pc.Items.Clear();var ok=await HeldRecovery.RetryHeldAsync(pair,new("p","p"),store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack),side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,default);Check.Require(ok && pc.Items["p"].Bytes.SequenceEqual(new byte[]{7}) && db.Items.Count==0 && store.Records.Values.Single()==saved,"Exact substitute Retry lost its bin subject without a queued change");}
    }
    private static async Task QuietAsync()
    {
        var pair=ItemChecks.Pair();using var store=new InMemoryStateStore();var pc=new FakeSide(Side.Local);var db=new FakeSide(Side.Dropbox);var root=ItemChecks.File(Side.Local,"p",0,"root",true);pc.Seed(root,[]);var child=ItemChecks.File(Side.Local,"p/child",3,"child");pc.Seed(child,[3]);pc.ReadFailures[child.Path]=new ItemFailedException(child.Path,TextKey.ReadLocalFailed,null);int pre=0,post=0;
        async IAsyncEnumerable<Listed> Receipt(){await Task.CompletedTask;post++;throw new InvalidOperationException("Read all recycled content");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
        async Task<RemovalResult> Remove(object?[] a)=>(await pc.DeleteAsync(root,(RemovalSelection?)a[1],default)) with {Recycled=Receipt()};
        var side=Observe.On(pc,(op,args)=>{if(op==nameof(ISide.ReadAllAsync))pre++;return op==nameof(ISide.DeleteAsync)?Remove(args):null;});
        var result=await new FolderDelete().ApplyAsync(pair,ItemChecks.Settings,_=>{},new(pair,[pair],DriveType.Fixed),root,null,Side.Local,ActionKind.DeleteLocal,side,db,store,_=>[],(_,_,_,_)=>Task.CompletedTask,(_,_,_,_)=>Task.CompletedTask,default,coversThrough:Admitted(pc,db));
        Check.Require(pre==1 && post==0 && result.Outcome==RemovalOutcome.Retained && pc.History.Count==0 && pc.Items[child.Path].Bytes.SequenceEqual(new byte[]{3}),"Unbound unreadable child recycled without eligibility proof");
    }
}
