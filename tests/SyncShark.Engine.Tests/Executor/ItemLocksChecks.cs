using Massdex.SyncShark.Engine.Executor;
namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Independent pairs share real locations; exact and subtree acquisition differ.</summary>
public static class ItemLocksChecks
{
    public static async Task RunAsync()
    {
        await Run.AutoSyncChecks.WithinDeadline(CancelConflictAsync);
        var pair=Scenarios.ItemChecks.Pair();
        var pc=Scenarios.ItemChecks.File(Side.Local,"moved",0,"pc",true);
        var db=Scenarios.ItemChecks.File(Side.Dropbox,"old",0,"cloud",true);
        var row=new Row("moved",pc,db,null,[],null,RowContext.Neutral);
        using (await new ItemLocks().AcquireAsync(pair,new(ActionKind.MoveDropbox,row,Target:"mapped"),[],default))
        {
            var localChild=PathNames.Key(Path.Combine(pair.LocalRoot,"mapped","child").Replace(Path.DirectorySeparatorChar,'/'));
            using var cancelProbe=new CancellationTokenSource();
            var probe=new ItemLocks().AcquireAsync([(Side.Local,localChild,false)],cancelProbe.Token);
            if(!probe.IsCompletedSuccessfully){ cancelProbe.Cancel(); try { using var ignored=await probe; } catch(OperationCanceledException) { } throw new InvalidDataException("MF3 Dropbox move locked an unrelated PC subtree"); }
            using var free=await probe;
        }
        foreach(var written in new[]{Side.Local,Side.Dropbox})
        foreach(var folder in new[]{false,true})
        {
            var clash=row with {Local=pc with {IsFolder=written==Side.Local?folder:!folder},Dropbox=db with {IsFolder=written==Side.Dropbox?folder:!folder}};
            using var removal=await new ItemLocks().AcquireAsync(pair,new(global::Massdex.SyncShark.Engine.Planner.SyncAction.DeleteOn(written),clash),[],default);
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            {
                using var stop=new CancellationTokenSource();
                var probe=new ItemLocks().AcquireAsync([(side,ItemLocks.Physical(pair,side,PathNames.Child(clash.EntryOn(side)!.Path,"child")),false)],stop.Token);
                bool free=probe.IsCompletedSuccessfully;if(!free) stop.Cancel();
                try {using var grant=await probe;} catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
                Check.Require(free==!(side==written && folder),"Delete must lock descendants only under the selected folder victim, never under a selected file or its kept peer");
            }
        }
        foreach(var receiver in new[]{Side.Local,Side.Dropbox})
        foreach(var folder in new[]{false,true})
        {
            var clash=row with {Local=pc with {IsFolder=receiver==Side.Dropbox?folder:!folder},Dropbox=db with {IsFolder=receiver==Side.Local?folder:!folder}};
            using var label=await new ItemLocks().AcquireAsync(pair,new(ActionKind.KeepBoth,clash,Target:"label",Side:receiver),[],default);
            using var stop=new CancellationTokenSource();
            var childGrant=new ItemLocks().AcquireAsync([(receiver,ItemLocks.Physical(pair,receiver,"label/child"),false)],stop.Token);
            bool free=childGrant.IsCompletedSuccessfully;if(!free) stop.Cancel();
            try {using var acquired=await childGrant;} catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
            Check.Require(free!=folder,"Receiving label must block descendants only for a source folder: "+receiver);
        }
        foreach(var written in new[]{Side.Local,Side.Dropbox})
        foreach(var kinds in new[]{(false,false),(true,false),(false,true),(true,true)})
        {
            var moving=row with {Local=pc with {IsFolder=kinds.Item1},Dropbox=db with {IsFolder=kinds.Item1}};
            var owner=SyncedEntries.From(pc with {Path="local-target",IsFolder=kinds.Item2},db with {Path="cloud-target",IsFolder=kinds.Item2});
            var move=new global::Massdex.SyncShark.Engine.Planner.SyncAction(written==Side.Local?ActionKind.MoveLocal:ActionKind.MoveDropbox,moving,Target:owner.PathOn(written));
            using var grant=await new ItemLocks().AcquireAsync(pair,move,[],default,incumbent:owner);
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            foreach(var path in new[]{moving.EntryOn(side)!.Path,owner.PathOn(side)})
            foreach(var descendant in new[]{false,true})
            {
                using var stop=new CancellationTokenSource();
                var key=ItemLocks.Physical(pair,side,descendant?PathNames.Child(path,"child"):path);
                var probe=new ItemLocks().AcquireAsync([(side,key,false)],stop.Token);
                bool free=probe.IsCompletedSuccessfully;if(!free) stop.Cancel();
                try {using var acquired=await probe;} catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
                Check.Require(free==(descendant && !kinds.Item1 && !kinds.Item2),"Bound move must grant both source and incumbent paths on both sides, recursively only for folders");
            }
        }
        var saved=SyncedEntries.From(pc with {Path="before"},db with {Path="before"});
        var relink=row with {Synced=saved};
        using(var record=await new ItemLocks().AcquireAsync(pair,new(ActionKind.Record,relink),[],default))
        {
            using var stop=new CancellationTokenSource();
            var contender=new ItemLocks().AcquireAsync([(Side.Local,ItemLocks.Physical(pair,Side.Local,"before/child"),false)],stop.Token);
            if(contender.IsCompletedSuccessfully){ using var unexpected=await contender; throw new InvalidDataException("R3-5 recursive folder rebind left the old child range unlocked"); }
            stop.Cancel(); try { using var unexpected=await contender; } catch(OperationCanceledException) { }
        }
        var settings=Scenarios.ItemChecks.Settings;
        var caseTarget=db.Path.ToUpperInvariant();
        using(var hop=await new ItemLocks().AcquireAsync(pair,new(ActionKind.MoveDropbox,row,Target:caseTarget),[],default,settings.CaseRenameSuffix))
        {
            using var stop=new CancellationTokenSource();
            var physical=PathNames.CaseRenamePath(caseTarget,settings.CaseRenameSuffix);
            var contender=new ItemLocks().AcquireAsync([(Side.Dropbox,ItemLocks.Physical(pair,Side.Dropbox,PathNames.Child(physical,"other")),false)],stop.Token);
            if(contender.IsCompletedSuccessfully){using var wrong=await contender;throw new InvalidDataException("Unrelated child entered the physical case hop");}
            stop.Cancel();try {using var wrong=await contender;} catch(OperationCanceledException) { }
        }
        var parentAction=new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.DeferFolder,row);
        var childRow=new Row("moved/child",pc with {Path="moved/child",IsFolder=false},db with {Path="old/child",IsFolder=false},null,[],null,RowContext.Neutral);
        foreach(var explicitFootprint in new[]{true,false})
        {
            using var outsiderStop=new CancellationTokenSource();
            var startOutsider=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempted=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task Outsider()
            {
                await startOutsider.Task;
                var acquire=new ItemLocks().AcquireAsync(pair,new(ActionKind.Upload,childRow),[],outsiderStop.Token);
                attempted.SetResult(acquire.IsCompletedSuccessfully);
                try { using var grant=await acquire; } catch(OperationCanceledException) when(outsiderStop.IsCancellationRequested) { }
            }
            var outsider=Outsider();
            async Task<bool> VerifyOwner()
            {
                startOutsider.SetResult();
                Check.Require(!await attempted.Task,"Unrelated owner borrowed a recovery subtree");
                outsiderStop.Cancel(); await outsider;
                using var cts=new CancellationTokenSource(); var entered=false;
                var nested=new ItemLocks().RunAsync(pair,new(ActionKind.Upload,childRow),[],()=>{entered=true;return Task.FromResult(true);},cts.Token);
                if(!entered) cts.Cancel();
                try { await nested; } catch(OperationCanceledException) { }
                Check.Require(entered,explicitFootprint?"Explicit footprint blocked its own covered child":"R2-2 parent recovery blocked on its own covered child");return true;
            }
            if(explicitFootprint) await new ItemLocks().RunAsync([(Side.Local,ItemLocks.Physical(pair,Side.Local,pc.Path),true),(Side.Dropbox,ItemLocks.Physical(pair,Side.Dropbox,db.Path),true)],VerifyOwner,default);
            else await new ItemLocks().RunAsync(pair,parentAction,[],VerifyOwner,default);
        }
        var a = new ItemLocks(); var b = new ItemLocks();
        using var root = await a.AcquireAsync([(Side.Local,"root",false)],default);
        using var child = await b.AcquireAsync([(Side.Local,"root/child",false)],default);
        using var otherSide = await b.AcquireAsync([(Side.Dropbox,"root",true)],default);
        var tree = b.AcquireAsync([(Side.Local,"root",true)],default);
        Check.Require(!tree.IsCompleted,"Recursive lock missed an exact endpoint");
        root.Dispose(); Check.Require(!tree.IsCompleted,"Recursive lock missed a held descendant");
        child.Dispose(); using var held = await tree;
        using var cancel = new CancellationTokenSource();
        var blocked = a.AcquireAsync([(Side.Local,"root/child",false),(Side.Local,"other",false)],cancel.Token);
        using var independent = await b.AcquireAsync([(Side.Local,"other",false)],default);
        cancel.Cancel(); try { using var wrong = await blocked; throw new InvalidDataException("Cancelled child acquired the subtree"); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private static async Task CancelConflictAsync(CancellationToken ct)
    {
        var locks=new ItemLocks();Task<bool> Done()=>Task.FromResult(true);
        using(var silent=await locks.AcquireAsync([(Side.Local,"cancel-before-root",false)],ct))
        using(var stop=CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var blocked=locks.RunAsync([(Side.Local,"cancel-before-root",false)],Done,stop.Token,serial:true);
            Check.Require(!blocked.IsCompleted,"Expected a blocked first conflict grant");stop.Cancel();
            try {await blocked;throw new InvalidDataException("Cancelled conflict entered");}catch(OperationCanceledException) when(stop.IsCancellationRequested) { }
        }
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(ct);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serial=locks.RunAsync([(Side.Dropbox,"serial",false)],async ()=>{await resume.Task.WaitAsync(ct);return true;},ct,serial:true);
        var waiter=locks.RunAsync([(Side.Local,"suspended",false)],()=>locks.RunAsync([],Done,cancel.Token,serial:true),cancel.Token);
        using(var borrowed=await locks.AcquireAsync([(Side.Local,"suspended",false)],ct))
        {
            resume.SetResult();await serial;cancel.Cancel();
            try {await waiter;throw new InvalidDataException("Cancelled suspended conflict entered");}catch(OperationCanceledException) when(cancel.IsCancellationRequested) { }
            using var probe=CancellationTokenSource.CreateLinkedTokenSource(ct);
            var duplicate=locks.AcquireAsync([(Side.Local,"suspended",false)],probe.Token);
            bool blocked=!duplicate.IsCompleted;probe.Cancel();try {using var grant=await duplicate;}catch(OperationCanceledException) when(probe.IsCancellationRequested) { }
            Check.Require(blocked,"Cancelled suspension removed another operation's key");
        }
        Check.Require(await locks.RunAsync([(Side.Local,"suspended",false)],Done,ct,serial:true),"Conflict admission leaked a permit");
    }
}
