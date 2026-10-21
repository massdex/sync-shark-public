using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task PlanOnlyAsync()
    {
        var preview = Build();
        var ordinary = Build();
        foreach (var fx in new[] { preview, ordinary })
        {
            fx.A.Seed("edit.txt", "old");
            await FirstSyncAsync(fx.Run(), fx);
            fx.A.Seed("edit.txt", "new");
            fx.A.Seed("upload.txt", "upload");
            fx.B.Seed("download.txt", "download");
        }
        var before = preview.WriterA.Calls + preview.WriterB.Calls;
        var capture = new PlanCaptureExecutor(ordinary.Executor);
        var run = new SyncRun(ordinary.Store, capture, ordinary.Provider, Settings, TimeProvider.System);
        var previewCapture = new PlanCaptureExecutor(preview.Executor);
        var previewRun = new SyncRun(preview.Store, previewCapture, preview.Provider, Settings, TimeProvider.System);
        var plan = await previewRun.PreviewIncrementalAsync(preview.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(previewCapture.Actions.Count == 0, "plan-only never enters executor Apply, including metadata-only actions");
        await run.IncrementalAsync(ordinary.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.RunUnavailable is null && plan.Actions.Count >= 3, "ordinary plan-only includes edits and both copy directions");
        Require(preview.WriterA.Calls + preview.WriterB.Calls == before, "plan-only makes zero endpoint mutation calls");
        static string Key(SyncAction a) => $"{a.Kind}|{a.Target}|{a.Path}|{a.Rule}|{a.RecoveryFirst}|{a.Source?.Hash}|{a.ExpectedTarget?.Hash}";
        Require(plan.Actions.Select(Key).Order().SequenceEqual(capture.Actions.Select(Key).Order()),
            "plan-only exports exactly the ordinary run's initial execution plan");
        var denied = new PlanOnlyEndpoints(preview.Provider).Writer(preview.Pair.B);
        try { await denied.MkDirAsync("must-not-exist", CancellationToken.None); throw new Exception("write allowed"); }
        catch (InvalidOperationException) { }
        Require(preview.WriterA.Calls + preview.WriterB.Calls == before, "write-denying facade never delegates an effect");
        // A null route client proves this reconciliation-only reference needs no provider call.
        var cloud = new DropboxWriter(preview.Pair.B, Side.B, null!, string.Empty, Settings);
        var readCloud = new PlanOnlyEndpoints(new FakeEndpointProvider().AddWriter(preview.Pair.B, cloud));
        var victim = (await preview.B.ObserveAsync("download.txt", CancellationToken.None))!;
        var recovery = await readCloud.Writer(preview.Pair.B).PreserveAsync(victim, "pending", CancellationToken.None);
        Require(recovery.VersionId == victim.VersionId && recovery.Hash == victim.Hash && !readCloud.MutationRequested,
            "cloud reconciliation constructs revision recovery without a provider call");
        var pending = plan.Actions.First();
        foreach (var kind in new[] { "whole-folder-group", "whole-folder-member", SyncDirectDownloadCheckpoint.Kind })
            Require(PlanOnlyEndpoints.UnsupportedReconcile(pending with
                { ProviderEffect = new ProviderEffectCheckpoint(kind, "{}", DateTimeOffset.UtcNow) }),
                "effectful reconciliation is explicitly marked unsupported: " + kind);
        Require(!PlanOnlyEndpoints.UnsupportedReconcile(pending with
            { ProviderEffect = new ProviderEffectCheckpoint("delete-job", "{}", DateTimeOffset.UtcNow) }),
            "read-only delete-job verification remains supported");
        Console.WriteLine("PASS ordinary plan-only parity and zero endpoint writes");
    }

    private sealed class PlanCaptureExecutor(IExecutor inner) : IExecutor
    {
        internal IReadOnlyList<SyncAction> Actions { get; private set; } = [];
        public async IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b,
            [EnumeratorCancellation] CancellationToken ct)
        {
            Actions = plan.Actions.ToArray();
            await foreach (var result in inner.ApplyAsync(plan, a, b, ct)) yield return result;
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> actions, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(actions, gen, a, b, ct, progress);
    }
}
