using Massdex.SyncShark.Engine.Executor;
namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Independent pairs share real locations; exact and subtree acquisition differ.</summary>
public static class ItemLocksChecks
{
    public static async Task RunAsync()
    {
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
        var parentAction=new global::Massdex.SyncShark.Engine.Planner.SyncAction(ActionKind.DeferFolder,row);
        var childRow=new Row("moved/child",pc with {Path="moved/child",IsFolder=false},db with {Path="old/child",IsFolder=false},null,[],null,RowContext.Neutral);
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
        await new ItemLocks().RunAsync(pair,parentAction,[],async ()=>
        {
            startOutsider.SetResult();
            Check.Require(!await attempted.Task,"Unrelated owner borrowed a recovery subtree");
            outsiderStop.Cancel(); await outsider;
            using var cts=new CancellationTokenSource(); var entered=false;
            var nested=new ItemLocks().RunAsync(pair,new(ActionKind.Upload,childRow),[],()=>{entered=true;return Task.FromResult(true);},cts.Token);
            if(!entered) cts.Cancel();
            try { await nested; } catch(OperationCanceledException) { }
            Check.Require(entered,"R2-2 parent recovery blocked on its own covered child");return true;
        },default);
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
}
