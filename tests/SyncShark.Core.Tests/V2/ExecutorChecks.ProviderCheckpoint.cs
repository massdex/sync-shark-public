namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task ProviderCheckpoint_ReconcileKeepsPending()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("pending", "tiny", versionId: "v1");
        var action = Act(ActionKind.CopyNew, Side.B, "pending", source: source) with
        {
            ProviderEffect = new ProviderEffectCheckpoint("upload-commit", "closed-session")
        };
        store.RecordIntent("p1", action);
        var results = await executor.ReconcileAsync([action], 1, a, b, CancellationToken.None);
        Require(results.Single().Outcome == Outcome.Uncertain,
            "absent path does not disprove an unknown upload batch commit");
        Require(store.UnresolvedIntents("p1").Single().ProviderEffect == action.ProviderEffect,
            "the unresolved intent retains its session checkpoint");

        var expired = action with
        {
            ProviderEffect = new ProviderEffectCheckpoint("upload-commit", "closed-session", DateTimeOffset.UtcNow.AddDays(-1))
        };
        store.RecordIntent("p1", expired);
        results = await executor.ReconcileAsync([expired], 1, a, b, CancellationToken.None);
        Require(results.Single().Outcome == Outcome.Skipped,
            "after grace an absent create may be conditionally replanned");

        var rejected = action with { ProviderEffect = new ProviderEffectCheckpoint("upload-rejected", "closed-session") };
        store.RecordIntent("p1", rejected);
        results = await executor.ReconcileAsync([rejected], 1, a, b, CancellationToken.None);
        Require(results.Single().Outcome == Outcome.Skipped && store.UnresolvedIntents("p1").Count == 0,
            "a definite rejection permits replanning without replaying an unknown effect");

        b.Seed("pending", "different", versionId: "other");
        store.RecordIntent("p1", expired);
        results = await executor.ReconcileAsync([expired], 1, a, b, CancellationToken.None);
        Require(results.Single().Outcome == Outcome.Skipped && results.Single().Reason == EffectResolver.Superseded,
            "after grace different content resolves superseded without replay");
        await DeleteJobObservationAsync();
        await DeleteJobEscalatesWhenStuckAsync();
        await DeleteJobStuckNoticeClosesWhenResolvedAsync();
        await MkDirJobCompletesWithFolderStateAsync();
        await MkDirSubmitWaitsForGraceAsync();
        await MkDirJobCheckFailureStaysUnresolvedAsync();
        await MkDirJobEscalatesWhenStuckAsync();
        await MkDirOccupiedPathWaitsForGraceAsync();
    }

    // Astra re-check of 0320328: a file at the path is not a definitive refusal of an unknown create. The effect is
    // retained inside its grace and only resolves superseded after it, so an occupied path is never replanned early.
    private static async Task MkDirOccupiedPathWaitsForGraceAsync()
    {
        foreach (var kind in new[] { "mkdir-submit", "mkdir-job" })
        {
            var (store, a, b, executor) = Rig();
            var sourceFolder = a.Seed("occupied", "", ItemKind.Folder);
            var occupant = b.Seed("occupied", "a file", versionId: "v1");
            var job = kind == "mkdir-job" ? "{\"jobId\":\"mkdir-21\",\"entryIndex\":0,\"entryCount\":1}" : "submission";
            // An unavailable job check leaves the outcome unknown (never a rejection), so the grace policy decides.
            var unknown = new PendingJobWriter(b, fail: true);
            var action = Act(ActionKind.MkDir, Side.B, "occupied", source: sourceFolder, key: "k-occupied") with
            {
                ProviderEffect = new ProviderEffectCheckpoint(kind, job, DateTimeOffset.UtcNow)
            };
            store.RecordIntent("p1", action);
            var result = (await executor.ReconcileAsync([action], 1, a, unknown, CancellationToken.None)).Single();
            Require(result.Outcome == Outcome.Uncertain,
                $"an unknown {kind} create whose path is occupied stays unresolved inside its grace: " + result.Reason);
            Require(store.UnresolvedIntents("p1").Count == 1, $"the occupied {kind} create keeps its intent unresolved");

            var expired = action with
            {
                ProviderEffect = new ProviderEffectCheckpoint(kind, job, DateTimeOffset.UtcNow.AddDays(-1))
            };
            store.RecordIntent("p1", expired);
            var resolved = (await executor.ReconcileAsync([expired], 1, a, unknown, CancellationToken.None)).Single();
            Require(resolved.Outcome == Outcome.Skipped && resolved.Reason == EffectResolver.Superseded,
                $"after grace the occupied {kind} create resolves superseded without replay: " + resolved.Reason);
            Require(store.UnresolvedIntents("p1").Count == 0, $"the superseded {kind} create closes its intent");
            Require((await b.ObserveAsync("occupied", CancellationToken.None))!.ObjectId == occupant.ObjectId,
                $"the occupant at the {kind} path is never touched");
        }
    }

    // Astra review of 04c4d75: a proven folder-create job must still read the folder's own state back. CommitDone
    // needs a new target state for a Done MkDir, so a null-state Done would fail every reconciliation.
    private static async Task MkDirJobCompletesWithFolderStateAsync()
    {
        var (store, a, b, executor) = Rig();
        var sourceFolder = a.Seed("created", "", ItemKind.Folder);
        var folder = b.Seed("created", "", ItemKind.Folder);
        const string Job = "{\"jobId\":\"mkdir-9\",\"entryIndex\":0,\"entryCount\":1}";
        var action = Act(ActionKind.MkDir, Side.B, "created", source: sourceFolder, key: "k-created") with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-job", Job, DateTimeOffset.UtcNow)
        };
        store.RecordIntent("p1", action);
        var result = (await executor.ReconcileAsync([action], 1, a, new ResolvedJobWriter(b, true),
            CancellationToken.None)).Single();
        Require(result.Outcome == Outcome.Done, "a completed create job reconciles Done: " + result.Reason);
        Require(result.NewB is { Kind: ItemKind.Folder } created && created.ObjectId == folder.ObjectId,
            "the Done create carries the folder's own read-back state");
        Require(store.Baselines("p1", ["k-created"]).Single().B.ObjectId == folder.ObjectId,
            "the folder state reaches the baseline a Done create commits");
        Require(store.UnresolvedIntents("p1").Count == 0, "the resolved create closes its intent");

        // A job that reports success but whose folder cannot be read back stays unresolved, never Done without state.
        var otherSource = a.Seed("unreadable", "", ItemKind.Folder);
        var other = Act(ActionKind.MkDir, Side.B, "unreadable", source: otherSource, key: "k-unreadable") with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-job", Job, DateTimeOffset.UtcNow)
        };
        store.RecordIntent("p1", other);
        var unproven = (await executor.ReconcileAsync([other], 1, a, new ResolvedJobWriter(b, true, unreadable: true),
            CancellationToken.None)).Single();
        Require(unproven.Outcome == Outcome.Uncertain,
            "a proven job whose folder cannot be read back stays unresolved: " + unproven.Reason);
    }

    // A submit with no job result yet is held by the same grace policy as an unknown upload or delete commit.
    private static async Task MkDirSubmitWaitsForGraceAsync()
    {
        var (store, a, b, executor) = Rig();
        var action = Act(ActionKind.MkDir, Side.B, "later") with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-submit", "submission", DateTimeOffset.UtcNow)
        };
        store.RecordIntent("p1", action);
        var result = (await executor.ReconcileAsync([action], 1, a, new ResolvedJobWriter(b, null),
            CancellationToken.None)).Single();
        Require(result.Outcome == Outcome.Uncertain,
            "a submitted create with no job result stays unresolved inside its grace: " + result.Reason);
        Require(store.UnresolvedIntents("p1").Count == 1, "its intent stays unresolved for the next run");

        var expired = action with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-submit", "submission", DateTimeOffset.UtcNow.AddDays(-1))
        };
        store.RecordIntent("p1", expired);
        var replayed = (await executor.ReconcileAsync([expired], 1, a, new ResolvedJobWriter(b, null),
            CancellationToken.None)).Single();
        Require(replayed.Outcome == Outcome.Skipped && replayed.Reason == EffectResolver.NotApplied,
            "after grace an absent create may be replanned as not applied: " + replayed.Reason);
    }

    // A job check that cannot be performed is never read as a rejection; only a definitive refusal permits a replan.
    private static async Task MkDirJobCheckFailureStaysUnresolvedAsync()
    {
        var (store, a, b, executor) = Rig();
        const string Job = "{\"jobId\":\"mkdir-11\",\"entryIndex\":0,\"entryCount\":1}";
        var action = Act(ActionKind.MkDir, Side.B, "later") with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-job", Job, DateTimeOffset.UtcNow)
        };
        store.RecordIntent("p1", action);
        var result = (await executor.ReconcileAsync([action], 1, a, new PendingJobWriter(b, fail: true),
            CancellationToken.None)).Single();
        Require(result.Outcome == Outcome.Uncertain,
            "a create job that cannot be checked stays unresolved, never a rejection: " + result.Reason);
        Require(store.UnresolvedIntents("p1").Count == 1, "an uncheckable job keeps its intent unresolved");

        store.RecordIntent("p1", action);
        var rejected = (await executor.ReconcileAsync([action], 1, a, new ResolvedJobWriter(b, false),
            CancellationToken.None)).Single();
        Require(rejected.Outcome == Outcome.Skipped && rejected.Reason == EffectResolver.NotApplied,
            "a definitive job rejection permits an immediate replan: " + rejected.Reason);
        Require(store.UnresolvedIntents("p1").Count == 0, "a refused create closes its intent");
    }

    // A create job that stays unresolved past the escalation window raises the same stored notice a delete job does.
    private static async Task MkDirJobEscalatesWhenStuckAsync()
    {
        var (store, a, b, executor) = Rig();
        const string Job = "{\"jobId\":\"mkdir-77\",\"entryIndex\":0,\"entryCount\":1}";
        var pending = new PendingJobWriter(b, fail: false);
        var fresh = Act(ActionKind.MkDir, Side.B, "stuckdir") with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-job", Job, DateTimeOffset.UtcNow)
        };
        store.RecordIntent("p1", fresh);
        var result = (await executor.ReconcileAsync([fresh], 1, a, pending, CancellationToken.None)).Single();
        Require(result.Outcome == Outcome.Uncertain && store.OpenNotices("p1").Count == 0,
            "a create job inside the escalation window raises no notice");

        var stuck = fresh with
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-job", Job, DateTimeOffset.UtcNow.AddHours(-7))
        };
        store.RecordIntent("p1", stuck);
        result = (await executor.ReconcileAsync([stuck], 1, a, pending, CancellationToken.None)).Single();
        var notice = store.OpenNotices("p1").Single();
        Require(result.Outcome == Outcome.Uncertain && notice.ReasonKey == "provider-job-stuck" &&
            notice.Path == "stuckdir" && notice.Detail.Contains("mkdir-77"),
            "a stuck create job stays Uncertain and names its path and job: " + notice.Detail);
    }

    // Astra review of 4c00da0: a delete job that stays unresolved leaves its intent Uncertain forever. Past the
    // escalation window reconcile raises one stored notice naming the path and job id; the effect stays Uncertain and
    // is never replayed or released.
    private static async Task DeleteJobEscalatesWhenStuckAsync()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("victim", "old", versionId: "v1");
        const string Job = "{\"jobId\":\"job-77\",\"entryIndex\":0,\"entryCount\":1}";
        var pending = new PendingJobWriter(b, fail: false);

        var fresh = Act(ActionKind.Delete, Side.B, "victim", expected: victim) with
        {
            ProviderEffect = new ProviderEffectCheckpoint("delete-job", Job, DateTimeOffset.UtcNow)
        };
        store.RecordIntent("p1", fresh);
        var result = (await executor.ReconcileAsync([fresh], 1, a, pending, CancellationToken.None)).Single();
        Require(result.Outcome == Outcome.Uncertain && store.OpenNotices("p1").Count == 0,
            "a pending job inside the escalation window raises no notice");

        var stuck = fresh with
        {
            ProviderEffect = new ProviderEffectCheckpoint("delete-job", Job, DateTimeOffset.UtcNow.AddHours(-7))
        };
        store.RecordIntent("p1", stuck);
        result = (await executor.ReconcileAsync([stuck], 1, a, pending, CancellationToken.None)).Single();
        var notice = store.OpenNotices("p1").Single();
        Require(result.Outcome == Outcome.Uncertain && notice.ReasonKey == "provider-job-stuck" &&
            notice.Path == "victim" && notice.Detail.Contains("job-77"),
            "a delete job stuck past the window stays Uncertain and names its path and job: " + notice.Detail);

        await executor.ReconcileAsync([stuck], 1, a, pending, CancellationToken.None);
        Require(store.OpenNotices("p1").Count == 1 && store.UnresolvedIntents("p1").Count == 1,
            "the stuck job is raised once and its effect stays unresolved (never replayed)");
    }

    // Follow-up to 4c00da0: once a stuck delete finally resolves (Done, Failed or Skipped), reconcile closes its open
    // notice. A completed delete removes its baseline, so its path drops out of the rows the generic closure
    // evaluates; without this the notice would stay open forever.
    private static async Task DeleteJobStuckNoticeClosesWhenResolvedAsync()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("victim", "old", versionId: "v1");
        const string Job = "{\"jobId\":\"job-88\",\"entryIndex\":0,\"entryCount\":1}";
        var stuck = Act(ActionKind.Delete, Side.B, "victim", expected: victim) with
        {
            ProviderEffect = new ProviderEffectCheckpoint("delete-job", Job, DateTimeOffset.UtcNow.AddHours(-7))
        };
        store.RecordIntent("p1", stuck);
        var escalated = (await executor.ReconcileAsync([stuck], 1, a, new PendingJobWriter(b, fail: false),
            CancellationToken.None)).Single();
        Require(escalated.Outcome == Outcome.Uncertain && store.OpenNotices("p1").Single().ReasonKey == "provider-job-stuck",
            "an escalated stuck delete job raises exactly one open notice");

        // The job resolves: the victim is gone, so reconcile proves the delete Done and must close the notice.
        await b.DeleteAsync(Act(ActionKind.Delete, Side.B, "victim", expected: victim), CancellationToken.None);
        var resolved = (await executor.ReconcileAsync([stuck], 1, a, new PendingJobWriter(b, fail: false),
            CancellationToken.None)).Single();
        Require(resolved.Outcome == Outcome.Done, "an absent target resolves the stuck delete Done: " + resolved.Reason);
        Require(store.OpenNotices("p1").Count == 0, "resolving the delete closes its open stuck-job notice");
        Require(store.UnresolvedIntents("p1").Count == 0, "the resolved intent is no longer unresolved");
    }

    private static async Task DeleteJobObservationAsync()
    {
        var (_, a, b, _) = Rig();
        var victim = b.Seed("victim", "old", versionId: "v1");
        var expired = new ProviderEffectCheckpoint("delete-job", "job", DateTimeOffset.UtcNow.AddDays(-1));
        var action = Act(ActionKind.Delete, Side.B, "victim", expected: victim) with { ProviderEffect = expired };
        // Dropbox definitively reports the job still in progress (delete_batch/check): it may still run, so grace
        // must never expire it into a replan while the target survives, else a restored copy could then be deleted.
        var result = await EffectResolver.ResolveAsync(action, new PendingJobWriter(b, fail: false), a, default);
        Require(result.Outcome == Outcome.Uncertain && result.Reason!.Contains("pending"),
            "a job Dropbox reports in progress stays Unverified past grace, never replanned while it may run: " +
            result.Reason);
        // An unknown/expired delete job lacks terminal proof even after grace.
        result = await EffectResolver.ResolveAsync(action, new PendingJobWriter(b, fail: true), a, default);
        Require(result.Outcome == Outcome.Uncertain,
            "an unavailable or expired delete job stays protected until a terminal result is known");
        result = await EffectResolver.ResolveAsync(action, new PendingJobWriter(b, fail: false, outage: true), a, default);
        Require(result.Outcome == Outcome.Uncertain && result.RetryAfterSeconds == 30,
            "transient job lookup failure never expires the job into replay after grace");
        // An absent target is not terminal proof: a pending delete could still remove a restored copy.
        result = await EffectResolver.ResolveAsync(action with { Path = "absent" },
            new PendingJobWriter(b, fail: false), a, default);
        Require(result.Outcome == Outcome.Uncertain, "absent target remains protected while the delete job is pending");
    }

    // A reconciler double whose job check is definitive: true (applied), false (refused) or null (still running),
    // optionally refusing the follow-up read-back the way an unavailable provider read would.
    private sealed class ResolvedJobWriter(IEndpointWriter inner, bool? applied, bool unreadable = false)
        : IEndpointWriter, IProviderEffectReconciler
    {
        public Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct) => Task.FromResult(applied);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => unreadable
            ? Task.FromException<ItemState?>(new IOException("read-back unavailable")) : inner.ObserveAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string id, CancellationToken ct) => inner.PreserveAsync(victim, id, ct);
        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new InvalidOperationException();
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => throw new InvalidOperationException();
    }

    private sealed class PendingJobWriter(IEndpointWriter inner, bool fail, bool outage = false) : IEndpointWriter, IProviderEffectReconciler
    {
        public Task<bool?> ResolveProviderEffectAsync(SyncAction action, ProviderJobCheckCache? jobChecks, CancellationToken ct) => outage
            ? Task.FromException<bool?>(new HttpRequestException("offline")) : fail
                ? Task.FromException<bool?>(new IOException("expired job")) : Task.FromResult<bool?>(null);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string id, CancellationToken ct) => inner.PreserveAsync(victim, id, ct);
        public Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => throw new InvalidOperationException();
        public Task DeleteAsync(SyncAction a, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new InvalidOperationException();
        public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => throw new InvalidOperationException();
    }
}
