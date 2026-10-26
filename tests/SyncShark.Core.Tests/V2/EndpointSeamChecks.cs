namespace Massdex.SyncShark.V2;

internal static class EndpointSeamChecks
{
    internal static async Task RunAsync()
    {
        var fallback = new FallbackProvider();
        await new PlanOnlyEndpoints(fallback).PrepareAsync(null!, default);
        LocalChecks.Require(fallback.Prepared == 1, "nonproduction plan-only defaults to normal preparation");
        var specialized = new PlanProvider();
        await new PlanOnlyEndpoints(specialized).PrepareAsync(null!, default);
        LocalChecks.Require(specialized.PlanPrepared == 1 && specialized.Prepared == 0,
            "plan-only preparation dispatches through the interface without concrete adapter casts");
        IFolderTreeDeleter unsupported = new UnsupportedFolder();
        string? message = null;
        try { unsupported.AuthorizeRestoreAgain("state", "Parent"); }
        catch (NotSupportedException error) { message = error.Message; }
        LocalChecks.Require(message == "This endpoint cannot authorize a folder restore.", "unsupported folder authorization is explicit");
    }

    private class FallbackProvider : IEndpointProvider
    {
        internal int Prepared;
        public IInventorySource Inventory(RootDef root) => throw new NotSupportedException();
        public IEndpointWriter Writer(RootDef root) => throw new NotSupportedException();
        public Task PrepareAsync(PairDef pair, CancellationToken ct) { Prepared++; return Task.CompletedTask; }
    }

    private sealed class PlanProvider : FallbackProvider, IEndpointProvider
    {
        internal int PlanPrepared;
        public Task PreparePlanOnlyAsync(PairDef pair, CancellationToken ct) { PlanPrepared++; return Task.CompletedTask; }
    }

    private sealed class UnsupportedFolder : IFolderTreeDeleter
    {
        public Task<IReadOnlyList<FolderTreeOutcome>> DeleteFolderTreesAsync(string scanCursor, IReadOnlyList<FolderTree> trees,
            Func<IReadOnlyList<FolderTree>, string, Task> beforeSubmit, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<FolderTreeOutcome>> ReconcileFolderTreesAsync(string scanCursor,
            IReadOnlyList<FolderTree> trees, string recoveryState, Func<string, Task> checkpoint, CancellationToken ct) => throw new NotSupportedException();
    }
}
