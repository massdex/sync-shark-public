using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Composes the real execution owners for focused contract checks.</summary>
internal static class SyncJobs
{
    internal static async Task RunBatchAsync(ExecutorContext context, SyncPorts ports,
        WorkSelection? selection, string scope, CancellationToken ct, Side side = Side.Local,
        IReadOnlySet<long>? retryIssues = null, bool settlesFirstRun = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "batch-fixture-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var log = Massdex.SyncShark.Engine.Run.RunLog.Open(directory, "pair", "batch", _ => { }, 256);
            await Massdex.SyncShark.Engine.Run.FolderBatch.RunAsync(context, ports, context.Dropbox, log, selection, selection?.Side ?? side, scope, retryIssues, settlesFirstRun,
                _ => Task.CompletedTask, new Massdex.SyncShark.Engine.Run.ScanOwnership(), () => true, ct);
        }
        finally { Directory.Delete(directory, true); }
    }

    internal static async Task<FolderRemovalResult?> RemoveFolderAsync(this FolderSync folders,
        Row row, Side side, CancellationToken ct)
    {
        var empty = new HashSet<string>();
        var id = await folders.PrepareFolderRemovalAsync(row, side, empty, empty, ct);
        if (id is null) return null;
        var absent = await folders.RunPreparedFolderRemovalsAsync([id], ct);
        return await folders.FinishFolderRemovalAsync(id, null, ct, absent: absent);
    }

    internal static (ItemSync Items, FirstRunFolders FirstRunFolders, AncestorFence Ancestors, Recovery Recovery, ExclusionRunner Exclusions, KeepBothRunner KeepBoth, ActionExecutor Actions) Create(
        ExecutorContext context, SyncPorts ports)
    {
        var parents = new Parents(context);
        var transfers = new Transfers(context);
        var keepBoth = new KeepBothRunner(context, transfers);
        var actions = new ActionExecutor(context, transfers, parents, keepBoth);
        var deletes = new FolderDeleteRunner(context, actions, ports.Recovery);
        var fence = new AncestorFence(context);
        var items = new ItemSync(context, parents, actions, keepBoth, deletes, ports, fence);
        return (items, new FirstRunFolders(context, parents, actions, items), fence,
            new Recovery(context, ports.Recovery, deletes),
            new ExclusionRunner(context, parents, actions, keepBoth, items), keepBoth, actions);
    }
}
