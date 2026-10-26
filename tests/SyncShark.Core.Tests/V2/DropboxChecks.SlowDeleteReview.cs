using System.Text.Json;

namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task RunSlowDeleteReviewChecksAsync(string selected)
    {
        if (selected is "all" or "review" or "compact") await CompactRecoveryWindowAsync();
        if (selected is "all" or "review" or "crash") await RestoreCrashCheckpointsAsync();
        if (selected is "all" or "review" or "slot") await AdmissionWaitIsNamedAsync();
        if (selected is "all" or "review" or "ramp") await RefusalsAllowAdmissionRecoveryAsync();
        if (selected is "all" or "review" or "wirethrottle") await WireThrottleLowersAdmissionAsync();
        if (selected is "all" or "review" or "tally") KindProgressCountsPlannedActions();
        if (selected is "all" or "review" or "reason") await ExhaustedThrottleDefersAsync();
    }

    private static async Task CompactRecoveryWindowAsync()
    {
        using var rig = await TreeRigAsync(1000);
        var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
        var victims = tree.Victims.Append(tree.Folder).ToArray();
        var changed = victims.First(v => v.Kind == ItemKind.File);
        var missing = victims.Last(v => v.Kind == ItemKind.File);
        var before = new[] { new DropboxChange(changed.Path, DropboxItems.ToEntry(changed with { VersionId = "changed" })) };
        var after = victims.Where(v => v.Path != missing.Path).Select(v => new DropboxChange(v.Path, null)).ToList();
        after.Add(new DropboxChange("big/unexpected.txt", null));
        var compact = DropboxWriter.CompactFolderWindow([tree], before, after);
        Require(compact.Exceptions.Select(c => c.Path).ToHashSet().SetEquals([changed.Path, "big/unexpected.txt"]),
            "compact window keeps changed/unexpected paths, not ordinary victim tombstones");
        Require(compact.Unobserved.SequenceEqual([missing.Path]), "missing evidence never becomes an implicit tombstone");
        string durable = "";
        var outcomes = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, state) =>
        { durable = state; return Task.CompletedTask; }, CancellationToken.None);
        Require(outcomes.Single().Status == FolderTreeStatus.Completed, "ordinary large deletion completes");
        using var json = JsonDocument.Parse(durable);
        Require(json.RootElement.GetProperty("After").GetArrayLength() == 0 &&
            json.RootElement.GetProperty("Unobserved").GetArrayLength() == 0,
            "durable large deletion window contains no ordinary victim records");
    }

    private static async Task RestoreCrashCheckpointsAsync()
    {
        foreach (var settleWasDurable in new[] { false, true })
        foreach (var userRemoved in new[] { false, true })
        {
            using var rig = await TreeRigAsync(60);
            rig.H.Wire.SeedFile("big/late-a.txt", "a");
            rig.H.Wire.SeedFile("big/late-b.txt", "b");
            var tree = new FolderTree(rig.Delete.ExpectedTarget!, FolderDelete.LiveDescendants(rig.Store, RootId, "big")) { RevalidateDelete = () => Task.FromResult(true) };
            string durable = "";
            var crashed = false;
            var initial = await rig.B.DeleteFolderTreesAsync(rig.Cursor, [tree], (_, state) =>
            {
                using var json = JsonDocument.Parse(state);
                var settled = json.RootElement.GetProperty("Restored");
                if (!crashed && settled.ValueKind == JsonValueKind.Array && settled.GetArrayLength() == 1)
                {
                    crashed = true;
                    if (settleWasDurable) durable = state;
                    throw new IOException("simulated crash immediately after first restore");
                }
                durable = state;
                return Task.CompletedTask;
            }, CancellationToken.None);
            Require(crashed && initial.Single().Status == FolderTreeStatus.Uncertain && rig.H.Wire.Restores.Count == 1,
                "each restore is checkpointed before the next effect, and failed checkpoint stops the folder");
            var first = rig.H.Wire.Restores.Single();
            if (userRemoved) rig.H.Wire.MoveOutsideRoot(first, "/Elsewhere/removed.txt");
            rig.H.Wire.FailFeedAfterFolderDelete = true;
            var resumed = await rig.B.ReconcileFolderTreesAsync(rig.Cursor, [tree], durable, state =>
            { durable = state; return Task.CompletedTask; }, CancellationToken.None);
            // , E8 restore clarification (owner-decisions.md:134): absence after
            // Started is neither success nor replay authority; an unverified restore stays Uncertain.
            var expectedStatus = userRemoved && !settleWasDurable
                ? FolderTreeStatus.Uncertain : FolderTreeStatus.Completed;
            Require(resumed.Single().Status == expectedStatus && rig.H.Wire.Restores.Count == 2 &&
                rig.H.Wire.Restores.Count(p => p == first) == 1,
                "resume restores only the unopened second candidate, never the completed first effect");
            if (userRemoved) Require(await rig.B.ObserveAsync(first, CancellationToken.None) is null,
                "resume never resurrects a restored file subsequently removed by the user");
            if (userRemoved && !settleWasDurable) Require(resumed.Single().Unrestored.Any(p => p.Contains(first)),
                "ambiguous absent recovery is explicitly reported for inspection");
        }
    }

    private static async Task AdmissionWaitIsNamedAsync()
    {
        var warning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var operations = RunOperations.Start(TimeProvider.System, V2Settings.Default with
        {
            OperationSweepInterval = TimeSpan.FromMilliseconds(10),
            SlowGateWaitThreshold = TimeSpan.FromMilliseconds(20)
        }, text => { if (text.Contains("delete-job admission")) warning.TrySetResult(text); });
        var gate = DropboxDeleteJobGate.For(Guid.NewGuid().ToString(), "namespace");
        await gate.EnterAsync(); await gate.EnterAsync();
        var blocked = gate.EnterAsync();
        try { await warning.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { gate.Exit(false); await blocked; gate.Exit(false); gate.Exit(false); }
    }

    private static async Task AssertAdmissionAsync(DropboxDeleteJobGate gate, int limit)
    {
        var acquired = 0;
        try
        {
            for (; acquired < limit; acquired++) await gate.EnterAsync().WaitAsync(TimeSpan.FromSeconds(2));
            var blocked = gate.EnterAsync();
            Require(!blocked.IsCompleted, $"namespace admission is bounded to {limit}");
            gate.Exit(false); acquired--;
            await blocked.WaitAsync(TimeSpan.FromSeconds(2)); acquired++;
        }
        finally { while (acquired-- > 0) gate.Exit(false); }
    }

    private static async Task RefusalsAllowAdmissionRecoveryAsync()
    {
        var binding = Binding() with { ConnectionId = Guid.NewGuid().ToString() };
        using var h = new Harness(binding);
        var writer = h.Writer();
        for (var i = 0; i < 4; i++)
        {
            var path = $"normal-refusal-{i}.txt";
            h.Wire.SeedFile(path, "original");
            var item = (await writer.ObserveAsync(path, CancellationToken.None))!;
            h.Wire.EditFile(path, "new user version");
            await ExpectAsync<ExpectedTargetMismatchException>(() => AuthorizedFixtureDeleteAsync(writer, DeleteAction(path, item), CancellationToken.None));
        }
        await AssertAdmissionAsync(DropboxDeleteJobGate.For(binding.ConnectionId!, binding.RootNamespaceId), 3);
    }

    private static async Task WireThrottleLowersAdmissionAsync()
    {
        var binding = Binding() with { ConnectionId = Guid.NewGuid().ToString() };
        using var h = new Harness(binding);
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { DeleteBatchSize = 1, DropboxWriteOperationRetryCount = 1 });
        h.Wire.SeedFile("throttled.txt", "kept");
        h.Wire.DeleteWriteRefusals["throttled.txt"] = 2;
        var item = (await writer.ObserveAsync("throttled.txt", CancellationToken.None))!;
        await ExpectAsync<ProviderWriteThrottledException>(() => AuthorizedFixtureDeleteAsync(writer, DeleteAction(item.Path, item), CancellationToken.None));
        Require(h.Wire.DeleteBatchSizes.SequenceEqual([1, 1]) && await writer.ObserveAsync(item.Path, CancellationToken.None) is not null,
            "exhausted throttle is distinct from revision mismatch and retains its victim after one retry");
        await AssertAdmissionAsync(DropboxDeleteJobGate.For(binding.ConnectionId!, binding.RootNamespaceId), 1);
    }

    private static async Task ExhaustedThrottleDefersAsync()
    {
        using var rig = await TreeRigAsync(2);
        var item = (await rig.B.ObserveAsync("big/f0.txt", CancellationToken.None))!;
        rig.H.Wire.DeleteWriteRefusals[item.Path] = 2;
        var action = DeleteAction(item.Path, item);
        // Rule 6 needs a trusted, now-absent source before it can reach the scripted throttle.
        rig.A.DropboxHashes = true;
        var source = rig.A.Seed(item.Path, "content 0");
        var adopt = action with { ActionId = "adopt-throttle", Kind = ActionKind.Adopt, Source = source };
        rig.Store.CommitResult("p1", 1, adopt, new ActionResult(adopt.ActionId, Outcome.Done, source, item, null, null));
        await rig.A.DeleteAsync(action with { Target = Side.A, ExpectedTarget = source }, default);
        Require(rig.Store.Baselines("p1").Count == 1, "throttle fixture has a confirmed source identity baseline");
        var results = await rig.ApplyAsync(new Plan("p1", 1, [action], []));
        Require(results.Single().Outcome == Outcome.Skipped &&
            results.Single().Reason!.Contains("throttled", StringComparison.OrdinalIgnoreCase) &&
            results.Single().Reason!.Contains("later run", StringComparison.OrdinalIgnoreCase) &&
            await rig.B.ObserveAsync(item.Path, CancellationToken.None) is not null,
            "exhausted write throttle is skipped for a later run, not failed or mislabeled a revision refusal");
    }

    private static void KindProgressCountsPlannedActions()
    {
        var tally = new SyncRun.Tally();
        var pair = new PairDef("p", "p", Root(), Root(), [], [], true);
        var delete = DeleteAction("d", new ItemState("d", ItemKind.File, "id:d", "r", null, 1, DateTimeOffset.UnixEpoch, ItemFlags.None));
        var copy = delete with { ActionId = "copy", Kind = ActionKind.CopyNew };
        var hold = delete with { ActionId = "hold", Kind = ActionKind.Hold };
        tally.Plan([delete, copy, hold, delete]);
        var result = new ActionResult(delete.ActionId, Outcome.Done, null, null, null, null);
        tally.Add(pair, delete, result); tally.Add(pair, delete, result);
        tally.Add(pair, null, result with { ActionId = "expanded-child" });
        var deferred = delete with { ActionId = "deferred" };
        tally.Plan([deferred]);
        tally.Add(pair, deferred, result with { ActionId = deferred.ActionId, Outcome = Outcome.Skipped });
        Require(tally.KindProgress().SequenceEqual(new[] { new ActionKindProgress(ActionKind.CopyNew, 0, 1),
            new ActionKindProgress(ActionKind.Delete, 2, 2) }.OrderBy(p => p.Kind)),
            "kind counts deduplicate planned actions, omit Hold and expanded children, and include deferred actions");
    }
}
