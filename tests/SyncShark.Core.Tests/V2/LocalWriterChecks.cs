namespace Massdex.SyncShark.V2;

/// <summary>The local writer checks: conditional puts, moves, deletes, mkdir and preserve.</summary>
internal static class LocalWriterChecks
{
    public static async Task RunAsync()
    {
        await PutCreateAndParentsAsync().ConfigureAwait(false);
        await RecoveryUnavailableCreateOnlyAsync().ConfigureAwait(false);
        await PutCreateOccupiedMismatchAsync().ConfigureAwait(false);
        await PutUpdateMatchesAsync().ConfigureAwait(false);
        await PutUpdateStaleAsync().ConfigureAwait(false);
        await PutWrongBytesFailedAsync().ConfigureAwait(false);
        await PreserveThenPutAsync().ConfigureAwait(false);
        await PreserveTwiceIdempotentAsync().ConfigureAwait(false);
        await PreserveCorruptedRecoveryAsync().ConfigureAwait(false);
        await PreserveThenDeleteNoOpAsync().ConfigureAwait(false);
        await ConcurrentFirstRecoveryDeletesAsync().ConfigureAwait(false);
        await PlantedRecoveryRootRefusedAsync().ConfigureAwait(false);
        await DeleteStaleMismatchAsync().ConfigureAwait(false);
        await HeldDeleteChangedHashAsync().ConfigureAwait(false);
        await DeleteNonEmptyFolderMismatchAsync().ConfigureAwait(false);
        await DeleteEmptyFolderOkAsync().ConfigureAwait(false);
        await MoveFileAsync().ConfigureAwait(false);
        await MoveFolderAsync().ConfigureAwait(false);
        await MoveOccupiedMismatchAsync().ConfigureAwait(false);
        await MkDirAsync().ConfigureAwait(false);
        await MkDirOverFileMismatchAsync().ConfigureAwait(false);
        await LocalWriterReadChecks.RunAsync().ConfigureAwait(false);
        await LocalIgnoredChildrenChecks.RunAsync().ConfigureAwait(false);
        await LocalIgnoredChildrenChecks.SyncRunCloudFolderDeletionAsync().ConfigureAwait(false);
    }

    private static async Task RecoveryUnavailableCreateOnlyAsync()
    {
        var root = LocalChecks.FreshRoot("recovery-unavailable");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "missing-recovery");
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "root-local"), Side.A,
            LocalChecks.PairId, LocalChecks.InstallationId, recovery, default, recoveryUnavailable: "removable drive missing");
        var bytes = LocalChecks.Bytes("safe copy");
        var action = new SyncAction("recovery-create", 1, ActionKind.CopyNew, Side.A, "new.txt", null, Source(bytes), null, false, null);
        var admitted = await writer.AdmitFreeSpaceAsync(action, default);
        using var admission = admitted.Reservation;
        LocalChecks.Require(admitted.Refusal is null, "create-only space admission does not depend on missing Recovery volume");
        var victim = await writer.PutAsync(action, new MemoryStream(bytes), default);
        LocalChecks.Require(File.ReadAllText(Path.Combine(root, "new.txt")) == "safe copy" && !Directory.Exists(recovery),
            "degraded local writer copies without probing or allocating missing Recovery");
        await ExpectAsync<IOException>(() => writer.DeleteAsync(new SyncAction("blocked-delete", 6, ActionKind.Delete,
            Side.A, "new.txt", null, null, victim, true, null), default));
        LocalChecks.Require(File.Exists(Path.Combine(root, "new.txt")), "degraded writer independently refuses destructive effects");
    }

    private static async Task PutCreateAndParentsAsync()
    {
        var setup = await SetupAsync("put-create").ConfigureAwait(false);
        var content = LocalChecks.Bytes("hello new");
        var action = new SyncAction("act-create", 1, ActionKind.CopyNew, Side.A, "x/y/new.txt", null,
            Source(content), null, false, null);
        var state = await setup.Writer.PutAsync(action, new MemoryStream(content), CancellationToken.None);
        var target = Path.Combine(setup.Root, "x", "y", "new.txt");
        LocalChecks.Require(File.Exists(target) && File.ReadAllText(target) == "hello new",
            "a create writes the bytes and its missing parent folders");
        LocalChecks.Require(state.Hash == LocalChecks.BlockHash(content) && state.Kind == ItemKind.File &&
            state.Size == content.Length, "a create returns the published state carrying the source hash");
        await RequirePublishedMtimeAsync(setup, action, state);
    }

    private static async Task PutCreateOccupiedMismatchAsync()
    {
        var setup = await SetupAsync("put-occupied").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "taken.txt", "taken");
        var content = LocalChecks.Bytes("new");
        var action = new SyncAction("act-occupied", 1, ActionKind.CopyNew, Side.A, "taken.txt", null,
            Source(content), null, false, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.PutAsync(action, new MemoryStream(content), CancellationToken.None));
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "taken.txt")) == "taken",
            "a create-only put never overwrites an occupant and reports a mismatch");
    }

    private static async Task PutUpdateMatchesAsync()
    {
        var setup = await SetupAsync("put-update").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var current = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var content = LocalChecks.Bytes("new-bytes-longer");
        var action = new SyncAction("act-update", 2, ActionKind.Update, Side.A, "file.txt", null, Source(content),
            current, false, null);
        var state = await setup.Writer.PutAsync(action, new MemoryStream(content), CancellationToken.None);
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "file.txt")) == "new-bytes-longer",
            "a matched update replaces the file bytes");
        LocalChecks.Require(state.Hash == LocalChecks.BlockHash(content),
            "a matched update returns the new content hash");
        await RequirePublishedMtimeAsync(setup, action, state);
    }

    private static async Task PutUpdateStaleAsync()
    {
        var setup = await SetupAsync("put-update-stale").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var current = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var content = LocalChecks.Bytes("new-bytes");
        var staleHash = new SyncAction("act-stale-hash", 2, ActionKind.Update, Side.A, "file.txt", null,
            Source(content), current with { Hash = new string('0', 64) }, false, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.PutAsync(staleHash, new MemoryStream(content), CancellationToken.None));
        var staleMtime = new SyncAction("act-stale-time", 2, ActionKind.Update, Side.A, "file.txt", null,
            Source(content), current with { Mtime = current.Mtime.AddSeconds(-5) }, false, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.PutAsync(staleMtime, new MemoryStream(content), CancellationToken.None));
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "file.txt")) == "old-bytes",
            "a stale expected hash or mtime refuses the update and changes nothing");
    }

    private static async Task PutWrongBytesFailedAsync()
    {
        var setup = await SetupAsync("put-wrong").ConfigureAwait(false);
        var action = new SyncAction("act-wrong", 1, ActionKind.CopyNew, Side.A, "out.txt", null,
            Source(LocalChecks.Bytes("right-bytes")), null, false, null);
        await ExpectAsync<IOException>(() =>
            setup.Writer.PutAsync(action, new MemoryStream(LocalChecks.Bytes("wrong-bytes")), CancellationToken.None));
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "out.txt")),
            "a stream that does not hash to the source publishes nothing");
    }

    private static async Task PreserveThenPutAsync()
    {
        var setup = await SetupAsync("preserve-put").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var reference = await setup.Writer.PreserveAsync(victim, "act-preserve-1", CancellationToken.None);
        var expected = Path.Combine(setup.Recovery, ".syncshark-recovery-" + LocalChecks.PairId,
            LocalChecks.OperationId("act-preserve-1") + ".recovery");
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "file.txt")),
            "preserve archives the victim out of the pair root");
        LocalChecks.Require(File.Exists(expected) && File.ReadAllText(expected) == "old-bytes",
            "preserve writes the victim bytes under the recovery folder");
        LocalChecks.Require(reference.Location == expected && reference.Hash == victim.Hash &&
            reference.Size == victim.Size, "preserve returns the recovery reference for the archived victim");
        var content = LocalChecks.Bytes("new-bytes");
        var action = new SyncAction("act-preserve-1", 2, ActionKind.Update, Side.A, "file.txt", null,
            Source(content), victim, true, null);
        var state = await setup.Writer.PutAsync(action, new MemoryStream(content), CancellationToken.None);
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "file.txt")) == "new-bytes" &&
            state.Hash == LocalChecks.BlockHash(content),
            "after preserve a recovery-first put publishes the new bytes over the emptied path");
        await RequirePublishedMtimeAsync(setup, action, state);
    }

    private static async Task PreserveTwiceIdempotentAsync()
    {
        var setup = await SetupAsync("preserve-twice").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        var first = await setup.Writer.PreserveAsync(victim, "act-twice", CancellationToken.None);
        var second = await setup.Writer.PreserveAsync(victim, "act-twice", CancellationToken.None);
        LocalChecks.Require(second.Location == first.Location && second.Hash == first.Hash &&
            second.Size == first.Size, "a repeated preserve returns the same recovery copy");
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "file.txt")),
            "a repeated preserve neither re-archives nor restores the victim");
    }

    private static async Task PreserveThenDeleteNoOpAsync()
    {
        var setup = await SetupAsync("preserve-delete").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "victim");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        await setup.Writer.PreserveAsync(victim, "act-preserve-delete", CancellationToken.None);
        var action = new SyncAction("act-preserve-delete", 6, ActionKind.Delete, Side.A, "file.txt", null, null,
            victim, true, null);
        await setup.Writer.DeleteAsync(action, CancellationToken.None);
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "file.txt")),
            "deleting an already-preserved victim is a harmless no-op");
    }

    // RQ-033/RQ-141: no prewarming or retry is needed for a fresh pair's parallel deletes.
    private static async Task ConcurrentFirstRecoveryDeletesAsync()
    {
        const int count = 10;
        var setup = await SetupAsync("concurrent-first-recovery");
        var recoveryRoot = Path.Combine(setup.Recovery, ".syncshark-recovery-" + LocalChecks.PairId);
        var victims = new ItemState[count];
        for (var i = 0; i < count; i++)
        {
            LocalChecks.Seed(setup.Root, $"victim-{i}.txt", $"original-{i}");
            victims[i] = (await setup.Writer.ObserveAsync($"victim-{i}.txt", CancellationToken.None))!;
        }
        LocalChecks.Require(!Directory.Exists(recoveryRoot), "parallel deletes start without a per-pair Recovery root");
        using var start = new Barrier(count);
        var workers = Enumerable.Range(0, count).Select(i => Task.Factory.StartNew(async () =>
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("Recovery workers did not reach the start barrier.");
            var actionId = $"act-parallel-delete-{i}";
            var reference = await setup.Writer.PreserveAsync(victims[i], actionId, CancellationToken.None);
            await setup.Writer.DeleteAsync(new SyncAction(actionId, 6, ActionKind.Delete, Side.A,
                $"victim-{i}.txt", null, null, victims[i], true, null), CancellationToken.None);
            var expectedPath = Path.Combine(recoveryRoot, LocalChecks.OperationId(actionId) + ".recovery");
            LocalChecks.Require(reference.Location == expectedPath && reference.Hash == victims[i].Hash &&
                reference.Size == victims[i].Size && File.ReadAllText(expectedPath) == $"original-{i}" &&
                !File.Exists(Path.Combine(setup.Root, $"victim-{i}.txt")),
                $"concurrent Recovery-first delete {i} retains its exact bytes and removes its source");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()).ToArray();
        await Task.WhenAll(workers);
        LocalChecks.Require(Directory.GetFiles(recoveryRoot, "*.recovery").Length == count &&
            Directory.GetFiles(recoveryRoot, "*.part*").Length == 0,
            "all parallel deletes finish with one archive each and no partial files");
    }

    private static async Task PlantedRecoveryRootRefusedAsync()
    {
        foreach (var kind in new[] { "junction", "file" })
        {
            var setup = await SetupAsync("planted-recovery-" + kind);
            LocalChecks.Seed(setup.Root, "victim.txt", "untouched victim");
            var victim = (await setup.Writer.ObserveAsync("victim.txt", CancellationToken.None))!;
            var recoveryRoot = Path.Combine(setup.Recovery, ".syncshark-recovery-" + LocalChecks.PairId);
            var outside = Path.Combine(Path.GetDirectoryName(setup.Root)!, "outside");
            Directory.CreateDirectory(outside);
            LocalChecks.Seed(outside, "sentinel.txt", "untouched sentinel");
            if (kind == "junction") LocalChecks.CreateJunction(recoveryRoot, outside);
            else File.WriteAllText(recoveryRoot, "planted file");
            try
            {
                await ExpectAsync<IOException>(() =>
                    setup.Writer.PreserveAsync(victim, "act-planted", CancellationToken.None));
                LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "victim.txt")) == "untouched victim" &&
                    File.ReadAllText(Path.Combine(outside, "sentinel.txt")) == "untouched sentinel" &&
                    Directory.GetFileSystemEntries(outside).Length == 1,
                    $"a planted Recovery {kind} is refused without changing source or outside target");
                if (kind == "file")
                    LocalChecks.Require(File.ReadAllText(recoveryRoot) == "planted file", "planted file is unchanged");
            }
            finally
            {
                if (kind == "junction") LocalChecks.RemoveJunction(recoveryRoot);
            }
        }
    }

    private static async Task DeleteStaleMismatchAsync()
    {
        var setup = await SetupAsync("delete-stale").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "f.txt", "data");
        var current = (await setup.Writer.ObserveAsync("f.txt", CancellationToken.None))!;
        var action = new SyncAction("act-del-stale", 6, ActionKind.Delete, Side.A, "f.txt", null, null,
            current with { Size = current.Size + 1 }, false, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() => setup.Writer.DeleteAsync(action, CancellationToken.None));
        LocalChecks.Require(File.Exists(Path.Combine(setup.Root, "f.txt")),
            "a delete whose expected target is stale leaves the file in place");
    }

    private static async Task HeldDeleteChangedHashAsync()
    {
        var setup = await SetupAsync("held-delete-changed-hash").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "f.txt", "before");
        var current = (await setup.Writer.ObserveAsync("f.txt", CancellationToken.None))!;
        var action = new SyncAction("act-held-del-stale", 19, ActionKind.Delete, Side.A, "f.txt", null, null,
            current, false, null) { NeedsReview = true };
        var path = Path.Combine(setup.Root, "f.txt");
        File.WriteAllText(path, "edited"); // Same length, same identity, restored mtime: only the hash catches it.
        File.SetLastWriteTimeUtc(path, current.Mtime.UtcDateTime);
        var changed = (await setup.Writer.ObserveAsync("f.txt", CancellationToken.None))!;
        LocalChecks.Require(changed.Size == current.Size && changed.Mtime == current.Mtime &&
            changed.ObjectId == current.ObjectId && changed.Hash != current.Hash,
            "the held-delete race changes content without changing identity, size or mtime");
        await ExpectAsync<ExpectedTargetMismatchException>(() => setup.Writer.DeleteAsync(action, CancellationToken.None));
        LocalChecks.Require(File.ReadAllText(path) == "edited",
            "a no-Recovery held delete cannot delete content edited after its plan");
    }

    private static async Task DeleteNonEmptyFolderMismatchAsync()
    {
        var setup = await SetupAsync("delete-nonempty").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "folder/child.txt", "child");
        var state = (await setup.Writer.ObserveAsync("folder", CancellationToken.None))!;
        var action = new SyncAction("act-del-nonempty", 6, ActionKind.Delete, Side.A, "folder", null, null, state,
            false, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() => setup.Writer.DeleteAsync(action, CancellationToken.None));
        LocalChecks.Require(Directory.Exists(Path.Combine(setup.Root, "folder")),
            "a non-empty folder is refused and retained, never recursively removed");
    }

    private static async Task DeleteEmptyFolderOkAsync()
    {
        var setup = await SetupAsync("delete-empty").ConfigureAwait(false);
        Directory.CreateDirectory(Path.Combine(setup.Root, "empty"));
        var state = (await setup.Writer.ObserveAsync("empty", CancellationToken.None))!;
        var action = new SyncAction("act-del-empty", 6, ActionKind.Delete, Side.A, "empty", null, null, state,
            false, null);
        await setup.Writer.DeleteAsync(action, CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(setup.Root, "empty")),
            "an empty folder is removed");
    }

    private static async Task MoveFileAsync()
    {
        var setup = await SetupAsync("move-file").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "a.txt", "move-me");
        var current = (await setup.Writer.ObserveAsync("a.txt", CancellationToken.None))!;
        var action = new SyncAction("act-mv", 3, ActionKind.Move, Side.A, "sub/b.txt", "a.txt", current, current,
            false, null);
        var moved = await setup.Writer.MoveAsync(action, CancellationToken.None);
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "a.txt")) &&
            File.ReadAllText(Path.Combine(setup.Root, "sub", "b.txt")) == "move-me",
            "a file moves to a new path and its missing parent is created");
        LocalChecks.Require(moved.ObjectId == current.ObjectId, "a moved file keeps its NTFS object id");
    }

    private static async Task MoveFolderAsync()
    {
        var setup = await SetupAsync("move-folder").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "d/x.txt", "x");
        var current = (await setup.Writer.ObserveAsync("d", CancellationToken.None))!;
        var action = new SyncAction("act-mvf", 3, ActionKind.Move, Side.A, "e", "d", current, current, false, null);
        var moved = await setup.Writer.MoveAsync(action, CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(setup.Root, "d")) &&
            File.Exists(Path.Combine(setup.Root, "e", "x.txt")), "a folder moves with its subtree");
        LocalChecks.Require(moved.ObjectId == current.ObjectId, "a moved folder keeps its NTFS object id");
    }

    private static async Task MoveOccupiedMismatchAsync()
    {
        var setup = await SetupAsync("move-occupied").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "a.txt", "a");
        LocalChecks.Seed(setup.Root, "b.txt", "b");
        var current = (await setup.Writer.ObserveAsync("a.txt", CancellationToken.None))!;
        var action = new SyncAction("act-mvo", 3, ActionKind.Move, Side.A, "b.txt", "a.txt", current, current,
            false, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() => setup.Writer.MoveAsync(action, CancellationToken.None));
        LocalChecks.Require(File.Exists(Path.Combine(setup.Root, "a.txt")) &&
            File.ReadAllText(Path.Combine(setup.Root, "b.txt")) == "b",
            "an occupied move destination is refused with nothing changed");
    }

    private static async Task MkDirAsync()
    {
        var setup = await SetupAsync("mkdir").ConfigureAwait(false);
        var first = await setup.Writer.MkDirAsync("newdir", CancellationToken.None);
        var second = await setup.Writer.MkDirAsync("newdir", CancellationToken.None);
        LocalChecks.Require(Directory.Exists(Path.Combine(setup.Root, "newdir")) &&
            first.ObjectId == second.ObjectId && first.Kind == ItemKind.Folder,
            "mkdir creates the folder once and is idempotent on a repeat");
    }

    private static async Task MkDirOverFileMismatchAsync()
    {
        var setup = await SetupAsync("mkdir-over-file").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "f.txt", "x");
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.MkDirAsync("f.txt", CancellationToken.None));
        LocalChecks.Require(File.Exists(Path.Combine(setup.Root, "f.txt")),
            "mkdir over an existing file reports a mismatch and changes nothing");
    }

    // A recovery copy whose bytes were altered (same length) must not satisfy IsPreserved.
    private static async Task PreserveCorruptedRecoveryAsync()
    {
        var setup = await SetupAsync("preserve-corrupt").ConfigureAwait(false);
        LocalChecks.Seed(setup.Root, "file.txt", "old-bytes");
        var victim = (await setup.Writer.ObserveAsync("file.txt", CancellationToken.None))!;
        await setup.Writer.PreserveAsync(victim, "act-corrupt", CancellationToken.None);
        var recoveryPath = Path.Combine(setup.Recovery, ".syncshark-recovery-" + LocalChecks.PairId,
            LocalChecks.OperationId("act-corrupt") + ".recovery");
        File.WriteAllBytes(recoveryPath, LocalChecks.Bytes("new-bytes"));
        var content = LocalChecks.Bytes("replacement!");
        var action = new SyncAction("act-corrupt", 2, ActionKind.Update, Side.A, "file.txt", null, Source(content),
            victim, true, null);
        await ExpectAsync<ExpectedTargetMismatchException>(() =>
            setup.Writer.PutAsync(action, new MemoryStream(content), CancellationToken.None));
        LocalChecks.Require(!File.Exists(Path.Combine(setup.Root, "file.txt")),
            "a same-length corrupted recovery copy is not accepted as the preserved victim");
    }

    internal static async Task<WriterSetup> SetupAsync(string name)
    {
        var root = LocalChecks.FreshRoot(name);
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        var writer = await LocalChecks.OpenWriter(root, recovery, CancellationToken.None);
        return new WriterSetup(root, recovery, writer);
    }

    private static async Task RequirePublishedMtimeAsync(WriterSetup setup, SyncAction action, ItemState published)
    {
        var observed = (await setup.Writer.ObserveAsync(action.Path, CancellationToken.None))!;
        LocalChecks.Require(File.GetLastWriteTimeUtc(Path.Combine(setup.Root, action.Path)) == action.Source!.Mtime.UtcDateTime &&
            published.Mtime == observed.Mtime && published == observed,
            "stream publication preserves the source UTC mtime and returns the observed baseline state");
    }

    internal static ItemState Source(byte[] content) =>
        new("source", ItemKind.File, "id:local:00000001:0000000000000001", null, LocalChecks.BlockHash(content),
            content.Length, new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.FromHours(-4)).AddTicks(1234567),
            ItemFlags.None);

    internal static async Task ExpectAsync<TException>(Func<Task> operation) where TException : Exception
    {
        try
        {
            await operation();
        }
        catch (TException)
        {
            Console.WriteLine("PASS rejects via " + typeof(TException).Name);
            return;
        }
        throw new InvalidOperationException("Expected " + typeof(TException).Name + ".");
    }

    internal sealed record WriterSetup(string Root, string Recovery, LocalWriter Writer);
}
