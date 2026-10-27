namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Real planner/executor, no changed feed on the retry: notices are the only durable retry source.
    private static async Task Incremental_FreeSpacePendingRetriedAsync()
    {
        foreach (var reason in new[] { FreeSpaceRefusal.InsufficientReasonKey,
                     FreeSpaceRefusal.UnknownSpaceReasonKey, FreeSpaceRefusal.UnknownReserveReasonKey })
        {
            var fx = Build();
            var clock = new CompletionClock();
            SyncRun RetryRun() => new(fx.Store, fx.Executor, fx.Provider, Settings, clock);
            fx.B.Seed("delete.txt", "old");
            await FirstSyncAsync(fx.Run(), fx);
            var cloud = await fx.B.ObserveAsync("delete.txt", CancellationToken.None);
            await fx.B.DeleteAsync(new SyncAction("remote-delete", 6, ActionKind.Delete, Side.B,
                "delete.txt", null, null, cloud, false, "delete-key"), CancellationToken.None);
            fx.B.Seed("copy.txt", "new");
            fx.B.Seed("later.txt", "later");
            var attempts = new List<string>();
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "copy.txt", "delete.txt", "later.txt" };
            var writer = new RecordingWriter(fx.A)
            {
                FreeSpaceAdmissionFor = action =>
                {
                    attempts.Add(action.Path);
                    return blocked.Contains(action.Path)
                        ? new FreeSpaceAdmission(null, new FreeSpaceRefusal("Pending space", reason, @"C:\", 10))
                        : FreeSpaceAdmission.Granted;
                },
            };
            fx.Provider.AddWriter(fx.Pair.A, writer);
            var progress = new RecordingProgress();
            var report = await RetryRun().IncrementalAsync(fx.Pair.PairId, progress, CancellationToken.None);
            var summary = report.FreeSpacePending.Single();
            Require(summary.Count == 3 && summary.RequiredBytes == 30 && summary.Members.Count == 3,
                "report groups one volume without losing per-path members");
            Require(progress.Reports.Count(p => p.Message == summary.Detail) == 1,
                "run output prints one pending summary per volume");
            Require(fx.Store.OpenNotices(fx.Pair.PairId).Count(n => n.ReasonKey == reason) == 3,
                "all refused paths have durable notices: " + reason);
            Require(fx.Store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
                "free-space refusals leave no replayable intent");

            // A new orchestrator with the already-advanced cursors must retry every notice, not only changed paths.
            attempts.Clear();
            blocked.Remove("copy.txt");
            blocked.Remove("delete.txt");
            clock.Now += TimeSpan.FromMinutes(6);
            await RetryRun().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(new[] { "copy.txt", "delete.txt", "later.txt" }.All(attempts.Contains),
                "an empty delta retries download, recovery delete and still-blocked path: " + reason);
            Require(await fx.A.ObserveAsync("copy.txt", CancellationToken.None) is not null &&
                    await fx.A.ObserveAsync("delete.txt", CancellationToken.None) is null,
                "space restored: download and recovery delete finish without a full scan");
            var pending = fx.Store.OpenNotices(fx.Pair.PairId).Where(n => n.ReasonKey == reason).ToArray();
            Require(pending.Length == 1 && pending[0].Path == "later.txt",
                "partial success closes only resolved notices");
            blocked.Clear();
            clock.Now += TimeSpan.FromMinutes(6);
            await RetryRun().IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(fx.Store.OpenNotices(fx.Pair.PairId).All(n => n.ReasonKey != reason),
                "the final pending notice closes on a later successful run");
        }
        Console.WriteLine("PASS Incremental_FreeSpacePendingRetried");
    }
}
