namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task QuarantineDeferredRehashAsync(string root)
    {
        var folder = Path.Combine(root, "held-write-debt");
        var files = Path.Combine(folder, "files");
        Directory.CreateDirectory(files);
        var path = Path.Combine(files, "held.txt");
        await File.WriteAllTextAsync(path, "aaa");
        using var store = SqliteStateStore.Open(Path.Combine(folder, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var local = LocalChecks.RootOf(files, "ra");
        store.SavePair(fx.Pair with { A = local });
        var inventory = new LocalInventory(local, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System);
        var gen = store.BeginGeneration("ra", "seed");
        store.WriteSnapshot(await inventory.FullAsync(gen, _ => true, new Progress<string>(), default));
        var item = store.Live("ra").Single();
        store.HoldQuarantine("ra", item);
        var mtime = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "bbb");
        File.SetLastWriteTimeUtc(path, mtime);
        inventory.GapIo = new VerificationTestIo((_, _, _) => throw new InvalidOperationException("held bytes must not be opened"));
        var identity = LocalItems.ParseObjectId(local.RootObjectId);
        using (var lease = PilotDirectoryLease.Open(files, new(identity.Volume, identity.FileId)))
        {
            var delta = new Delta("ra", Side.A, gen, "", "", [new Change(ChangeKind.Upsert, item.ObjectId, item.Path, item, null)], false, []);
            store.WriteDelta(await inventory.RehashAsync(lease, delta, new HashSet<string> { item.ObjectId }, default));
        }
        await inventory.VerifyIndexingPassAsync(default);
        await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
        var held = store.Live("ra").Single();
        Require(held.Hash is null && held.Flags.HasFlag(ItemFlags.NeedsRehash) && held.Flags.HasFlag(ItemFlags.Quarantined),
            "metadata-preserving held write records debt without reading bytes");
        Require(store.ClearQuarantine("ra", item), "release held write identity");
        inventory.GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => false);
        await inventory.VerifyIndexingPassAsync(default);
        await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
        var verified = store.Live("ra").Single();
        Require(verified.Hash == DropboxContentHash.Compute(System.Text.Encoding.UTF8.GetBytes("bbb")) &&
            !verified.Flags.HasFlag(ItemFlags.NeedsRehash), "release verifies changed bytes before trusting old hash");
    }

    // Real local identity and production rehash/store paths; only the hash scanner's 225/226 result is injected.
    private static async Task QuarantineRehashHoldAsync(string root, bool verifyPending)
    {
        if (!verifyPending) await QuarantineDeferredRehashAsync(root);
        var owned = Path.Combine(root, verifyPending ? "verify-quarantine" : "rehash-quarantine");
        var localPath = Path.Combine(owned, "files");
        Directory.CreateDirectory(localPath);
        await File.WriteAllTextAsync(Path.Combine(localPath, "held.txt"), "preserve these bytes");
        using var store = SqliteStateStore.Open(Path.Combine(owned, "state"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var local = LocalChecks.RootOf(localPath, "ra");
        store.SavePair(fx.Pair with { A = local });
        var inventory = new LocalInventory(local, Side.A, store, new HashSet<string>(), Settings, TimeProvider.System);
        var gen = store.BeginGeneration("ra", "rehash-fixture");
        store.WriteSnapshot(await inventory.FullAsync(gen, _ => true, new Progress<string>(), default));
        var item = store.Live("ra", ["held.txt"]).Single();
        store.RequireRehash("ra", item, "journal write");
        var calls = 0;
        inventory.GapIo = new VerificationTestIo((_, _, _) =>
        {
            calls++;
            return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Unverifiable,
                Reason: "injected native quarantine", QuarantineCode: verifyPending ? 226 : 225));
        });
        async Task Attempt()
        {
            if (verifyPending)
            {
                await inventory.VerifyIndexingPassAsync(default);
                await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
            }
            else
            {
                var identity = LocalItems.ParseObjectId(local.RootObjectId);
                using var lease = PilotDirectoryLease.Open(localPath, new(identity.Volume, identity.FileId));
                var delta = new Delta("ra", Side.A, gen, "cursor", "cursor",
                    [new Change(ChangeKind.Upsert, item.ObjectId, item.Path, item, null)], false, []);
                await inventory.RehashAsync(lease, delta, new HashSet<string> { item.ObjectId }, default);
                await inventory.VerifyIndexingPassAsync(default);
            }
        }
        await Attempt();
        Require(store.Live("ra", [item.Path]).Single().Flags.HasFlag(ItemFlags.Quarantined) &&
            store.OpenNotices("p1").Any(n => n.Path == item.Path && n.Quarantine is not null),
            (verifyPending ? "verify pending" : "journal rehash") + " native quarantine persists held row and notice");
        await Attempt();
        await inventory.VerifyIndexingPassAsync(default);
        await inventory.VerifyPendingAsync(_ => true, _ => { }, default);
        Require(calls == 1 && File.Exists(Path.Combine(localPath, item.Path)),
            (verifyPending ? "verify pending" : "journal rehash") + " quarantine never loops through automatic hash retries and preserves file");
    }
}
