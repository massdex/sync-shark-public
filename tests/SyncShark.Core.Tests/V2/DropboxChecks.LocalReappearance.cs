namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    // Real NTFS source + real executor/Dropbox writer; only the remote HTTP service is scripted.
    // No inventory refresh is allowed between freezing the absent-source plan and dispatch.
    private static async Task LocalSourceReappearanceChecksAsync()
    {
        foreach (var subset in new[] { false, true })
        foreach (var route in new[] { "bulk-admission", "bulk-dispatch", "per-item" })
            await LocalFolderReappearsAsync(subset, route);
        foreach (var queued in new[] { false, true })
            await LocalFileReappearsAsync(queued);
    }

    private static async Task LocalFolderReappearsAsync(bool subset, string route)
    {
        using var rig = await TreeRigAsync(route == "per-item" ? 4 : 52);
        var setup = await LocalWriterChecks.SetupAsync("folder-reappearance-" + Guid.NewGuid().ToString("N"));
        var local = setup.Writer;
        var parent = Path.GetDirectoryName(setup.Root)!;
        var statePath = Path.Combine(parent, "state");
        var parked = Path.Combine(parent, "parked");
        var victims = FolderDelete.LiveDescendants(rig.Store, RootId, "big")
            .Append(rig.Delete.ExpectedTarget!).ToArray();
        var originals = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        using (var store = SqliteStateStore.Open(statePath, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(new PairDef("p1", "Pair A", LocalChecks.RootOf(setup.Root, "ra"), Root(), [], [], true));
            var gen = store.BeginGeneration(RootId, "snapshot");
            store.WriteSnapshot(new(RootId, Side.B, gen, rig.Cursor, victims, []));
            store.CompleteGeneration(gen, rig.Cursor, true);
            foreach (var victim in victims)
            {
                if (victim.Kind == ItemKind.Folder) Directory.CreateDirectory(Path.Combine(setup.Root, victim.Path));
                else LocalChecks.Seed(setup.Root, victim.Path, "content " + Path.GetFileNameWithoutExtension(victim.Path)[1..]);
            }
            foreach (var victim in victims)
            {
                var original = (await local.ObserveAsync(victim.Path, default))!;
                originals.Add(victim.Path, original);
                var adopt = new SyncAction("adopt-" + victim.ObjectId, 12, ActionKind.Adopt, Side.B,
                    victim.Path, null, original, victim, false, "baseline-" + victim.ObjectId);
                store.CommitResult("p1", gen, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
            }
            // Moving outside the held root proves absence without relying on NTFS id reuse after deletion.
            Directory.Move(Path.Combine(setup.Root, "big"), parked);
            foreach (var original in originals.Values)
                Require(await local.IsSourceAbsentAsync(original, original.Path, default) == true,
                    "every planned folder victim is genuinely absent from A before reappearance");
            var delete = rig.Delete with { Rule = 23, ChoiceRemove = false,
                ObjectKey = "baseline-" + rig.Delete.ExpectedTarget!.ObjectId };
            var plan = rig.Plan() with { Actions = [delete] };
            var reappeared = false;
            void Reappear()
            {
                if (reappeared) return;
                reappeared = true;
                if (subset)
                {
                    // Keep the original folder and f0 identities, but not the other original files.
                    foreach (var original in originals.Values.Where(item => item.Kind == ItemKind.File && item.Path != "big/f0.txt"))
                        File.Move(Path.Combine(parked, original.Path[4..]), Path.Combine(parent, "removed-" + Path.GetFileName(original.Path)));
                    Directory.Move(parked, Path.Combine(setup.Root, "big"));
                }
                else
                {
                    LocalChecks.Seed(setup.Root, "big/f0.txt", "new child at an old path");
                    LocalChecks.Seed(setup.Root, "big/new.txt", "brand new child");
                }
            }
            if (route == "bulk-dispatch") rig.H.Wire.AfterContinueResponse = _ => Reappear();
            else Reappear();
            var results = new List<ActionResult>();
            var executor = new Executor(store, V2Settings.Default, ExecutorChecks.Admission(),
                isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            await foreach (var result in executor.ApplyAsync(plan, local, rig.B, default)) results.Add(result);
            Require(reappeared && rig.H.Wire.WholeFolderDeletes == 0,
                "Rule 23 reappearance blocks recursive remote deletion: " + route);
            foreach (var path in subset ? new[] { "big", "big/sub", "big/f0.txt" } : new[] { "big", "big/f0.txt" })
            {
                var current = (await local.ObserveAsync(path, default))!;
                Require((current.ObjectId == originals[path].ObjectId) == subset,
                    "fixture proves same-folder subset or new NTFS identity: " + path);
                Require(await rig.B.ObserveAsync(path, default) is not null,
                    "every reappeared planned path keeps its Dropbox survivor: " + path);
                var id = path == "big" ? delete.ActionId : delete.ActionId + ":" + path;
                Require(results.Single(result => result.ActionId == id) is
                    { Outcome: Outcome.Skipped, DeleteSourceUnconfirmed: true },
                    "reappeared folder/child has the guarded Skipped outcome: " + path);
                Require(await local.IsSourceAbsentAsync(originals[path], path, default) == false &&
                    (await local.IsAbsentBatchAsync([(originals[path], path)], default)).Single().Absent == false,
                    "both scalar and batched real local guards reject current path presence");
            }
            Require(await rig.B.ObserveAsync("big/f2.txt", default) is null &&
                results.Single(result => result.ActionId == delete.ActionId + ":big/f2.txt").Outcome == Outcome.Done,
                "truly absent old child still deletes through guarded per-item fallback");
            if (!subset) Require(File.ReadAllText(Path.Combine(setup.Root, "big/new.txt")) == "brand new child",
                "new unplanned local child remains untouched");
        }
        using var reopened = SqliteStateStore.Open(statePath, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        foreach (var path in subset ? new[] { "big", "big/sub", "big/f0.txt" } : new[] { "big", "big/f0.txt" })
            Require(reopened.SyncRetryAt("p1", path) is not null && reopened.Baselines("p1").Any(b => b.A.Path == path),
                "blocked folder and child retain baseline and durable replan debt after cold reopen");
    }

    private static async Task LocalFileReappearsAsync(bool queued)
    {
        using var h = new Harness();
        var setup = await LocalWriterChecks.SetupAsync("file-reappearance-" + Guid.NewGuid().ToString("N"));
        var parent = Path.GetDirectoryName(setup.Root)!;
        var statePath = Path.Combine(parent, "state");
        var settings = V2Settings.Default with { DeleteBatchSize = 2, DeleteBatchMaxAge = TimeSpan.FromSeconds(1) };
        var remote = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(parent, "spool"),
            isStateDamage: StorageFailureClassifier.IsStateDamage, settings);
        var target = new QueuedDeleteWriter(remote, 2);
        using (var store = SqliteStateStore.Open(statePath, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(new PairDef("p1", "Pair A", LocalChecks.RootOf(setup.Root, "ra"), Root(), [], [], true));
            var actions = new List<SyncAction>();
            ItemState? historical = null;
            foreach (var path in new[] { "returned.txt", "absent.txt" })
            {
                LocalChecks.Seed(setup.Root, path, "original");
                h.Wire.SeedFile(path, "original");
                var original = (await setup.Writer.ObserveAsync(path, default))!;
                var victim = (await remote.ObserveAsync(path, default))!;
                var adopt = new SyncAction("adopt-" + path, 12, ActionKind.Adopt, Side.B, path, null,
                    original, victim, false, "key-" + path);
                store.CommitResult("p1", 1, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
                File.Move(Path.Combine(setup.Root, path), Path.Combine(parent, "parked-" + path));
                Require(await setup.Writer.IsSourceAbsentAsync(original, path, default) == true,
                    "Rule 6 source is absent when plan is frozen");
                if (path == "returned.txt") historical = original;
                actions.Add(new("delete-" + path, 6, ActionKind.Delete, Side.B, path, null, null, victim, true, "key-" + path));
            }
            var plan = new Plan("p1", 1, actions, []);
            var binding = Binding().Normalize();
            var gate = DropboxDeleteJobGate.For(binding.ConnectionId, binding.RootNamespaceId);
            if (queued) { gate.Throttled(); gate.Throttled(); await gate.EnterAsync(); }
            else LocalChecks.Seed(setup.Root, "returned.txt", "replacement");
            var executor = new Executor(store, settings, ExecutorChecks.Admission(),
                isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            var running = CollectAsync();
            if (queued)
            {
                try
                {
                    await target.Queued.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Require(!h.Wire.Calls.Contains("/2/files/delete_batch"), "both deletes admitted but dispatch gate still holds");
                    LocalChecks.Seed(setup.Root, "returned.txt", "replacement");
                }
                finally { gate.Exit(false); }
            }
            var results = await running.WaitAsync(TimeSpan.FromSeconds(30));
            Require((await setup.Writer.ObserveAsync("returned.txt", default))!.ObjectId != historical!.ObjectId,
                "replacement file has a different real NTFS identity");
            Require(results.Single(r => r.ActionId == "delete-returned.txt") is
                { Outcome: Outcome.Skipped, DeleteSourceUnconfirmed: true } &&
                await remote.ObserveAsync("returned.txt", default) is not null,
                "Rule 6 replacement blocks Dropbox delete with guarded Skipped outcome");
            Require(results.Single(r => r.ActionId == "delete-absent.txt").Outcome == Outcome.Done &&
                await remote.ObserveAsync("absent.txt", default) is null && h.Wire.DeleteBatchSizes.Sum() == 1,
                "only the truly absent file is submitted and deleted");
            Require(File.ReadAllText(Path.Combine(setup.Root, "returned.txt")) == "replacement",
                "replacement local bytes remain intact");

            async Task<List<ActionResult>> CollectAsync()
            {
                var results = new List<ActionResult>();
                await foreach (var result in executor.ApplyAsync(plan, setup.Writer, target, default)) results.Add(result);
                return results;
            }
        }
        using var reopened = SqliteStateStore.Open(statePath, StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        Require(reopened.SyncRetryAt("p1", "returned.txt") is not null &&
            reopened.Baselines("p1").Any(b => b.ObjectKey == "key-returned.txt") && reopened.UnresolvedIntents("p1").Count == 0,
            "Rule 6 blocked file has durable replan debt and baseline but no submitted intent after cold reopen");
    }
}
