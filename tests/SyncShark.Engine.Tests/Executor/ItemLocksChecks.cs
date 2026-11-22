using Massdex.SyncShark.Engine.Executor;
namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Independent pairs share real locations; exact and subtree acquisition differ.</summary>
public static class ItemLocksChecks
{
    public static async Task RunAsync()
    {
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
