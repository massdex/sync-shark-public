namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Part A only: no cursor/scope-retention dependency; call the production execution lane directly.
    private static async Task DeferredEvidence_ReviewedFolderFreshAdmissionAsync()
    {
        foreach (var side in new[] { Side.A, Side.B })
        foreach (var held in new[] { false, true })
        {
            var fx = Build();
            using var deadline = new CancellationTokenSource(GateTimeout);
            var target = side == Side.A ? fx.A : fx.B;
            var folder = target.Seed("reviewed", "", ItemKind.Folder);
            var child = target.Seed("reviewed/child.txt", "original");
            fx.A.Seed("held.txt", "local");
            fx.Provider.AddInventory(fx.Pair.B, new DeferredInventory(fx.B) { Fail = true });
            var run = fx.Run();
            await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), deadline.Token);
            var action = new SyncAction("reviewed-delete", 32, ActionKind.Delete, side, folder.Path,
                null, null, folder, false, null)
            {
                FirstSyncChoice = true, ChoiceRemove = true,
                FrozenVictimSignature = FolderDelete.Signature([child]),
                FrozenRemovalSignature = FolderDelete.Signature([child])
            };
            target.Seed("reviewed/new.txt", "not disclosed");
            var plan = new Plan(fx.Pair.PairId, 1, [action], [])
                { HeldDeletions = held ? [new HeldDeletion("held.txt", Side.A)] : [] };
            var execute = typeof(SyncRun).GetMethod("ExecuteWithEvidenceAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)execute.Invoke(run, [plan, fx.Pair,
                new ProgressThrottle(null, TimeProvider.System, TimeSpan.Zero),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "A:held.txt", FolderDelete.VictimKey(side, folder.Path) },
                deadline.Token, false])!;
            Require(fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
                await target.ObserveAsync(folder.Path, default) is not null &&
                fx.Store.SyncRetryAt(fx.Pair.PairId, folder.Path)?.ReviewedActions?.Count == 1,
                "fresh admission holds changed reviewed folder before all effects, side=" + side + ", held=" + held);
        }
    }
}
