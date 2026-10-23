using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandProtectionChecks
{
    internal static void Run(string root)
    {
        foreach (var parentMove in new[] { false, true })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, parentMove ? "folder" : "file"),
                V2Settings.Default, TimeProvider.System);
            var pair = new PairDef("pair", "Pair A",
                new("local", SyncProviders.Local, null, "/local", "local"),
                new("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root"), [], [], true);
            store.SavePair(pair);
            store.EnableStorage();
            var local = new ItemState("Folder/file.txt", ItemKind.File, "local-object", null,
                new string('a', 64), 4, DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer);
            var remote = local with { ObjectId = "cloud-object", VersionId = "revision-1", Flags = ItemFlags.None };
            Publish(pair.A, Side.A, local);
            Publish(pair.B, Side.B, remote);
            var adopt = new SyncAction("adopt", 3, ActionKind.Adopt, Side.B, local.Path, null,
                local, remote, false, "linked");
            store.RecordIntent(pair.PairId, adopt);
            store.CommitResult(pair.PairId, 1, adopt,
                new(adopt.ActionId, Outcome.Done, local, remote, null, null));
            // Receipt mismatch/partial data protection follows the physical entity across either
            // a file rename or a containing-folder rename, never just the current display path.
            store.RequireRehash(pair.A.RootId, local, OwnedPointerProof.VerificationMessage);
            var moved = local with
            {
                Path = parentMove ? "Moved/file.txt" : "Folder/renamed.txt",
                Hash = null,
                Flags = ItemFlags.OwnedPointer | ItemFlags.NeedsRehash | ItemFlags.Unreadable
            };
            Publish(pair.A, Side.A, moved);
            Require(store.ByObject(pair.A.RootId, local.ObjectId)?.Path == moved.Path,
                "renamed needs-verification pointer remains discoverable by the same object id");
            var links = LinkExclusions.Of(store, pair, false);
            var scope = new RunScope(store, _ => true, _ => true, links).LoadFull(pair);
            Require(scope.A.Count == 0 && scope.B.Count == 0 && scope.Baselines.Count == 0,
                "protected pointer hides its whole established identity triple, including the old baseline path");
            var rows = Planner.Join(scope.Baselines, scope.A, scope.B,
                new Dictionary<string, DateTimeOffset?>(), new Dictionary<string, DateTimeOffset?>(),
                new(true, true, new HashSet<string>(), new HashSet<string>()), false);
            var plan = Planner.Decide(pair.PairId, 1, rows,
                new(TimeSpan.Zero, false, new("{name} [{side}]{ext}", "s", TimeSpan.Zero, "Local", "Dropbox"), 10));
            Require(!plan.Actions.Any(action => action.Target == Side.B || action.Kind == ActionKind.Forget),
                "renamed protected pointer cannot cause a remote Delete/Move at the old path or lose its baseline");
            Require(store.Baselines(pair.PairId).Single().A?.ObjectId == local.ObjectId,
                "the protected original baseline remains durable for retry");

            void Publish(RootDef endpoint, Side side, ItemState item)
            {
                var generation = store.BeginGeneration(endpoint.RootId, "snapshot");
                store.WriteSnapshot(new Snapshot(endpoint.RootId, side, generation, "cursor", [item], []));
                store.CompleteGeneration(generation, "cursor", true);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FoD identity protection: " + message);
    }
}
