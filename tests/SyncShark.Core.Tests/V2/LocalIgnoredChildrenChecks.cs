namespace Massdex.SyncShark.V2;

/// <summary>Offline NTFS checks for ignored children carried by enclosing folder operations (RQ-025).</summary>
internal static class LocalIgnoredChildrenChecks
{
    public static async Task RunAsync()
    {
        await DeleteIgnoredChildrenAsync();
        await UnexpectedChildBlocksAsync();
        await LateUnexpectedChildBlocksAsync();
        await FrozenCarriedRetryWithoutInventoryAsync();
        await FrozenCarriedUnchangedRetryAsync();
        await RealKindMattersAsync();
        await MoveCarriesIgnoredChildrenAsync();
        await RedirectedChildBlocksAsync();
        await PagedSqliteRemovalAsync();
        await SyncRunLocalFolderDeletionAsync();
        await RunChecks.HeldRecovery_RealIgnoredOnlyFolderAsync();
    }

    // More than one physical batch, with no inventory attached to the writer. Every carried file is preserved.
    private static async Task PagedSqliteRemovalAsync()
    {
        var root = LocalChecks.FreshRoot("ignored-paged");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        var localRoot = LocalChecks.RootOf(root, "root-local");
        var rules = new[] { new SyncIgnoreRule(SyncIgnoreKind.FileExtension, "tmp") };
        const int children = 300;
        Directory.CreateDirectory(Path.Combine(root, "folder"));
        for (var index = 0; index < children; index++)
            LocalChecks.Seed(root, $"folder/f{index:D4}.tmp", "ignored bytes");
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetDirectoryName(root)!, "state"),
            V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var gen = store.BeginGeneration(localRoot.RootId, "full");
        var snapshot = await LocalChecks.Inventory(root, store, TimeProvider.System)
            .FullAsync(gen, _ => true, new Progress<string>(), CancellationToken.None);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        var folder = snapshot.Items.Single(item => item.Path == "folder");
        var writer = await LocalWriter.OpenAsync(localRoot, Side.A, LocalChecks.PairId, LocalChecks.InstallationId,
            recovery, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, ignoreRules: rules);
        await LocalWriterChecks.AuthorizedDeleteAsync(writer, new SyncAction("delete-paged", 6, ActionKind.Delete, Side.A, "folder",
            null, null, folder, false, null), CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(root, "folder")),
            "physical streaming cleanup removes the folder and every carried child");
        LocalChecks.Require(Directory.GetFiles(recovery, "*.recovery", SearchOption.AllDirectories).Length == children,
            "every extension-only carried file is preserved before it is removed");
    }

    public static async Task SyncRunCloudFolderDeletionAsync()
    {
        var setup = await SetupAsync("ignored-sync-run", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/synced.txt", "synced bytes");
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "ignored shell metadata");
        using var store = new InMemoryStateStore();
        var localRoot = LocalChecks.RootOf(setup.Root, "root-local");
        var remoteRoot = new RootDef("root-cloud", SyncProviders.Dropbox, "test-account", "/remote", "id:remote");
        var pair = new PairDef(LocalChecks.PairId, "ignored deletion", localRoot, remoteRoot,
            [], ["FileName:desktop.ini"], true);
        store.SavePair(pair);
        var remote = new FakeEndpoint(Side.B, remoteRoot.RootId) { DropboxHashes = true, RequireHashes = true };
        var provider = new FakeEndpointProvider().Add(remoteRoot, remote)
            .AddWriter(localRoot, setup.Writer)
            .AddInventory(localRoot, LocalChecks.Inventory(setup.Root, store, TimeProvider.System));
        var settings = V2Settings.Default with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            provider, settings, TimeProvider.System);
        var progress = new RecordingProgress();
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, progress, CancellationToken.None);
        var first = await run.ApplyAsync(pair.PairId, plan, progress, CancellationToken.None);
        LocalChecks.Require(first.Outcome == RunOutcomes.Complete &&
            await remote.ObserveAsync("folder/synced.txt", CancellationToken.None) is not null &&
            await remote.ObserveAsync("folder/desktop.ini", CancellationToken.None) is null &&
            File.Exists(Path.Combine(setup.Root, "folder", "desktop.ini")),
            "SyncRun first sync copies ordinary content but leaves desktop.ini local and untouched");
        foreach (var path in new[] { "folder/synced.txt", "folder" })
        {
            var victim = (await remote.ObserveAsync(path, CancellationToken.None))!;
            await remote.DeleteAsync(new SyncAction("external-delete-" + path, 6, ActionKind.Delete,
                Side.B, path, null, null, victim, false, null), CancellationToken.None);
        }
        var deleted = await run.IncrementalAsync(pair.PairId, progress, CancellationToken.None);
        LocalChecks.Require(deleted.Outcome == RunOutcomes.Complete &&
            !Directory.Exists(Path.Combine(setup.Root, "folder")),
            "SyncRun cloud folder deletion physically removes synced files, desktop.ini, and the local folder");
    }

    private static async Task SyncRunLocalFolderDeletionAsync()
    {
        var setup = await SetupAsync("ignored-local-delete-run", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/synced.txt", "synced bytes");
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "ignored shell metadata");
        using var store = new InMemoryStateStore();
        var localRoot = LocalChecks.RootOf(setup.Root, "root-local");
        var remoteRoot = new RootDef("root-cloud", SyncProviders.Dropbox, "test-account", "/remote", "id:remote");
        var pair = new PairDef(LocalChecks.PairId, "local ignored deletion", localRoot, remoteRoot,
            [], ["FileName:desktop.ini"], true);
        store.SavePair(pair);
        var remote = new FakeEndpoint(Side.B, remoteRoot.RootId) { DropboxHashes = true, RequireHashes = true };
        var inventory = new ExplicitFolderDeltaInventory(LocalChecks.Inventory(setup.Root, store, TimeProvider.System));
        var provider = new FakeEndpointProvider().Add(remoteRoot, remote)
            .AddWriter(localRoot, setup.Writer).AddInventory(localRoot, inventory);
        var settings = V2Settings.Default with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero };
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException),
            provider, settings, TimeProvider.System);
        var progress = new RecordingProgress();
        var plan = await run.PreviewFirstSyncAsync(pair.PairId, progress, CancellationToken.None);
        var first = await run.ApplyAsync(pair.PairId, plan, progress, CancellationToken.None);
        LocalChecks.Require(first.Outcome == RunOutcomes.Complete &&
            store.Live(localRoot.RootId).Any(item => item.Path == "folder/desktop.ini") &&
            await remote.ObserveAsync("folder/synced.txt", default) is not null &&
            await remote.ObserveAsync("folder/desktop.ini", default) is null,
            "first sync inventories the real ignored local child without uploading it");

        // Preview closes its generations without committing cursors; Apply consumes that plan, not a scan.
        // Establish a committed checkpoint through an ordinary incremental pass before testing the delta.
        var checkpoint = await run.IncrementalAsync(pair.PairId, progress, CancellationToken.None);
        LocalChecks.Require(checkpoint.Outcome == RunOutcomes.Complete && store.Cursor(localRoot.RootId) is not null,
            "ordinary incremental pass commits the local delta checkpoint after first sync");
        var deltaReadsBefore = inventory.DeltaReads;
        Directory.Delete(Path.Combine(setup.Root, "folder"), recursive: true);
        inventory.Deleted = true;
        var deleted = await run.IncrementalAsync(pair.PairId, progress, CancellationToken.None);
        LocalChecks.Require(inventory.DeltaReads > deltaReadsBefore,
            "external local folder deletion is read through the real local delta refresher");
        LocalChecks.Require(deleted.Outcome == RunOutcomes.Complete,
            "external local folder deletion run completes: " + deleted.Outcome);
        LocalChecks.Require(await remote.ObserveAsync("folder", default) is null &&
            await remote.ObserveAsync("folder/synced.txt", default) is null,
            "real external local folder deletion propagates through SyncRun to cloud folder and content");
        LocalChecks.Require(!store.Live(localRoot.RootId).Any(item => PairPath.IsSelfOrUnder(item.Path, "folder")),
            "local folder delta removes ignored inventory ghosts");
    }

    // Feed the real local delta refresher the deleted directory, independent of journal permissions.
    // This deliberately does not request desktop.ini itself: parent deletion must clear descendants.
    private sealed class ExplicitFolderDeltaInventory(LocalInventory inner) : IInventorySource
    {
        public bool Deleted { get; set; }
        public int DeltaReads { get; private set; }
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> progress,
            CancellationToken ct) => (await inner.FullAsync(gen, include, progress, ct)) with { CursorAfter = "fixture-cursor" };
        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
        {
            DeltaReads++;
            return inner.ChangesForPathsAsync(Deleted ? ["folder"] : [], cursor, gen, include, ct);
        }
        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.HashAsync(paths, ct);
        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
    }

    private static async Task DeleteIgnoredChildrenAsync()
    {
        await CarriedChildRechecksParentAuthorityAsync();
        var setup = await SetupAsync("ignored-delete", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"),
            new SyncIgnoreRule(SyncIgnoreKind.FileName, "large.ini"),
            new SyncIgnoreRule(SyncIgnoreKind.FileName, "small.ini"),
            new SyncIgnoreRule(SyncIgnoreKind.FileExtension, "tmp"),
            new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache"));
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", new byte[64 * 1024]);
        LocalChecks.Seed(setup.Root, "folder/small.ini", new byte[64 * 1024 - 1]);
        var expected = new Dictionary<string, byte[]>
        {
            ["folder/large.ini"] = Enumerable.Repeat((byte)83, 64 * 1024 + 1).ToArray(),
            ["folder/cache/nested/large.bin"] = Enumerable.Repeat((byte)84, 64 * 1024 + 1).ToArray(),
            ["folder/cache/small.bin"] = LocalChecks.Bytes("small folder-rule bytes"),
            ["folder/cache/desktop.ini"] = LocalChecks.Bytes("overlapping FileName and FolderName"),
            ["folder/cache/empty.bin"] = [],
            ["folder/small.tmp"] = LocalChecks.Bytes("extension-only bytes"),
            ["folder/boundary.tmp"] = Enumerable.Repeat((byte)85, 64 * 1024).ToArray()
        };
        foreach (var (path, bytes) in expected) LocalChecks.Seed(setup.Root, path, bytes);
        Directory.CreateDirectory(Path.Combine(setup.Root, "folder", "cache", "empty"));
        await LocalWriterChecks.AuthorizedDeleteAsync(setup.Writer, await DeleteActionAsync(setup.Writer), CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(setup.Root, "folder")),
            "enclosing delete physically removes desktop.ini and ignored folder descendants");
        var copies = Directory.GetFiles(setup.Recovery, "*.recovery", SearchOption.AllDirectories)
            .Select(File.ReadAllBytes).ToArray();
        LocalChecks.Require(copies.Length == expected.Count &&
            expected.Values.All(bytes => copies.Count(copy => copy.SequenceEqual(bytes)) == 1),
            "Recovery keeps every carried file exactly once except FileName matches at or below 64KiB; " +
            "FolderName wins overlapping small FileName matches and extension-only files always recover");
    }

    private static async Task CarriedChildRechecksParentAuthorityAsync()
    {
        var setup = await SetupAsync("carried-parent-authority", new SyncIgnoreRule(SyncIgnoreKind.FileExtension, "tmp"));
        LocalChecks.Seed(setup.Root, "folder/child.tmp", "retained bytes");
        var action = await DeleteActionAsync(setup.Writer);
        var previous = ProviderEffectContext.Current.Value;
        var absent = true;
        var checks = 0;
        try
        {
            ProviderEffectContext.Current.Value = new ProviderEffectContext(_ => Task.CompletedTask)
            {
                Action = action,
                RevalidateDelete = () => { checks++; return Task.FromResult(new AbsenceObservation(absent ? true : null, "source unknown")); }
            };
            SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = () => absent = false;
            var refused = false;
            try { await setup.Writer.DeleteAsync(action, default); }
            catch (DeleteSourceUnconfirmedException) { refused = true; }
            LocalChecks.Require(refused && checks >= 2 && File.Exists(Path.Combine(setup.Root, "folder", "child.tmp")),
                "carried child rechecks enclosing action after archive wait and retains original when authority becomes unknown");
        }
        finally
        {
            SyncLocalRecovery.RecoveryCreatedBeforeBindingForTest = null;
            ProviderEffectContext.Current.Value = previous;
        }
    }

    private static async Task FrozenCarriedRetryWithoutInventoryAsync()
    {
        var root = LocalChecks.FreshRoot("frozen-carried-physical");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        LocalChecks.Seed(root, "folder/desktop.ini", "frozen");
        var local = LocalChecks.RootOf(root, "root-local");
        var remote = new RootDef("remote-frozen", SyncProviders.Dropbox, "test", "/remote", "id:remote");
        var pair = new PairDef(LocalChecks.PairId, "frozen physical", local, remote, [], ["FileName:desktop.ini"], true);
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetDirectoryName(root)!, "state"), V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
        store.SavePair(pair);
        var gen = store.BeginGeneration(local.RootId, "full");
        var snapshot = await LocalChecks.Inventory(root, store, TimeProvider.System).FullAsync(gen, _ => true,
            new Progress<string>(), CancellationToken.None);
        store.WriteSnapshot(snapshot);
        store.CompleteGeneration(gen, snapshot.CursorAfter, true);
        const string operation = "frozen-physical";
        store.FreezeChoiceVictims(pair.PairId, operation, local.RootId, "folder");
        var rows = store.ChoiceVictims(operation, ChoiceVictimState.Pending, 256, false);
        store.MarkChoiceVictims(operation, rows.Select(row => (row.Ord, ChoiceVictimState.Carried)).ToArray());
        var failParent = true;
        var writer = await LocalWriter.OpenAsync(local, Side.A, pair.PairId, LocalChecks.InstallationId,
            recovery, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, ignoreRules: [new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini")],
            verifyCarriedChoice: (op, item) =>
            {
                if (failParent) { failParent = false; throw new IOException("injected parent-removal failure"); }
                return store.VerifyCarriedChoice(pair.PairId, op, item);
            });
        var action = (await DeleteActionAsync(writer)) with { FrozenChoiceId = operation, ContentsSettled = true, DescendantsPlanned = true };
        var failed = false;
        try { await LocalWriterChecks.AuthorizedDeleteAsync(writer, action, CancellationToken.None); }
        catch (IOException) { failed = true; }
        LocalChecks.Require(failed && File.Exists(Path.Combine(root, "folder", "desktop.ini")), "parent failure leaves the carried child");
        // Deliberately no inventory refresh: only the writer can see this edit.
        File.WriteAllText(Path.Combine(root, "folder", "desktop.ini"), "changed bytes after freeze");
        var kept = false;
        try { await LocalWriterChecks.AuthorizedDeleteAsync(writer, action, CancellationToken.None); }
        catch (ExpectedTargetMismatchException) { kept = true; }
        LocalChecks.Require(kept && File.ReadAllText(Path.Combine(root, "folder", "desktop.ini")) == "changed bytes after freeze" &&
            store.CountChoiceVictims(operation, ChoiceVictimState.Kept) == 1 &&
            store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "conflict-carried-changed"),
            "physical retry keeps a changed carried child, marks it Kept, and records the reason on a notice");
    }

    private static async Task FrozenCarriedUnchangedRetryAsync()
    {
        foreach (var interrupt in new[] { false, true })
        {
            var root = LocalChecks.FreshRoot("frozen-unchanged-" + interrupt);
            var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
            LocalChecks.Seed(root, "folder/cache/first.bin", "first preserved");
            LocalChecks.Seed(root, "folder/cache/second.bin", "second preserved");
            var local = LocalChecks.RootOf(root, "root-local");
            var remote = new RootDef("remote-frozen", SyncProviders.Dropbox, "test", "/remote", "id:remote");
            var pair = new PairDef(LocalChecks.PairId, "frozen unchanged", local, remote, [], ["FolderName:cache"], true);
            using var store = SqliteStateStore.Open(Path.Combine(Path.GetDirectoryName(root)!, "state"), V2Settings.Default, TimeProvider.System, LocalItems.CaptureOptionalHash);
            store.SavePair(pair);
            var gen = store.BeginGeneration(local.RootId, "full");
            var snapshot = await LocalChecks.Inventory(root, store, TimeProvider.System).FullAsync(gen, _ => true,
                new Progress<string>(), CancellationToken.None);
            store.WriteSnapshot(snapshot);
            store.CompleteGeneration(gen, snapshot.CursorAfter, true);
            const string operation = "unchanged-carried";
            store.FreezeChoiceVictims(pair.PairId, operation, local.RootId, "folder");
            var rows = store.ChoiceVictims(operation, ChoiceVictimState.Pending, 256, false);
            store.MarkChoiceVictims(operation, rows.Select(row => (row.Ord, ChoiceVictimState.Carried)).ToArray());
            var verified = new HashSet<string>();
            var fileChecks = 0;
            var stopped = false;
            var writer = await LocalWriter.OpenAsync(local, Side.A, pair.PairId, LocalChecks.InstallationId,
                recovery, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, ignoreRules: [new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache")],
                verifyCarriedChoice: (op, item) =>
                {
                    if (interrupt && !stopped && item.Kind == ItemKind.File && ++fileChecks == 2)
                    {
                        stopped = true;
                        throw new IOException("stop after first carried child removal");
                    }
                    var matches = store.VerifyCarriedChoice(pair.PairId, op, item);
                    if (matches) verified.Add(item.Path);
                    return matches;
                });
            var action = (await DeleteActionAsync(writer)) with { FrozenChoiceId = operation, ContentsSettled = true, DescendantsPlanned = true };
            if (interrupt)
            {
                var failed = false;
                try { await LocalWriterChecks.AuthorizedDeleteAsync(writer, action, default); }
                catch (IOException) { failed = true; }
                LocalChecks.Require(failed && stopped && Directory.GetFiles(Path.Combine(root, "folder", "cache")).Length == 1,
                    "interruption occurs after exactly one carried file was removed");
                // Force a distinguishable folder timestamp even on coarse filesystems. Identity is unchanged.
                Directory.SetLastWriteTimeUtc(Path.Combine(root, "folder", "cache"), DateTime.UtcNow.AddMinutes(2));
            }
            await LocalWriterChecks.AuthorizedDeleteAsync(writer, action, default);
            LocalChecks.Require(!Directory.Exists(Path.Combine(root, "folder")) && verified.Count == 3 &&
                store.CountChoiceVictims(operation, ChoiceVictimState.Kept) == 0,
                "unchanged carried files and folder are verified and removed, including after interrupted cleanup");
            LocalChecks.Require(Directory.GetFiles(recovery, "*.recovery", SearchOption.AllDirectories).Length == 2,
                "every carried file remains preserved on success and retry");
        }
    }

    private static async Task LateUnexpectedChildBlocksAsync()
    {
        var root = LocalChecks.FreshRoot("ignored-late-child");
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        LocalChecks.Seed(root, "folder/first.tmp", "preserve me");
        var injected = false;
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "root-local"), Side.A,
            LocalChecks.PairId, LocalChecks.InstallationId, recovery, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage,
            ignoreRules: [new SyncIgnoreRule(SyncIgnoreKind.FileExtension, "tmp")],
            providerNameRefused: item =>
            {
                // Ignored entries short-circuit this callback in validation and enumeration. Its first
                // call here is at removal, after validation, before the next physical sweep.
                if (!injected && item.Path == "folder/first.tmp")
                {
                    injected = true;
                    LocalChecks.Seed(root, "folder/late.txt", "new user bytes");
                }
                return false;
            });
        var action = await DeleteActionAsync(writer);
        var blocked = false;
        try { await LocalWriterChecks.AuthorizedDeleteAsync(writer, action, CancellationToken.None); }
        catch (ExpectedTargetMismatchException) { blocked = true; }
        LocalChecks.Require(injected && blocked && File.ReadAllText(Path.Combine(root, "folder", "late.txt")) == "new user bytes",
            "late non-carried child keeps the parent and is never removed");
        LocalChecks.Require(Directory.GetFiles(recovery, "*.recovery", SearchOption.AllDirectories)
            .Any(path => File.ReadAllText(path) == "preserve me"), "earlier swept child remains recoverable after late-child stop");
    }

    private static async Task UnexpectedChildBlocksAsync()
    {
        var setup = await SetupAsync("ignored-unexpected", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "ignored");
        LocalChecks.Seed(setup.Root, "folder/ordinary.txt", "unexpected");
        var action = await DeleteActionAsync(setup.Writer);
        ExpectedTargetMismatchException? mismatch = null;
        try { await LocalWriterChecks.AuthorizedDeleteAsync(setup.Writer, action, CancellationToken.None); }
        catch (ExpectedTargetMismatchException error) { mismatch = error; }
        LocalChecks.Require(mismatch is not null &&
            mismatch.Message.Contains("unexpected child 'folder/ordinary.txt'", StringComparison.Ordinal) &&
            !mismatch.Message.Contains("changed kind", StringComparison.Ordinal),
            "an ordinary unexpected child is explained truthfully, not reported as a kind change");
        LocalChecks.Require(File.ReadAllText(Path.Combine(setup.Root, "folder", "desktop.ini")) == "ignored" &&
            File.ReadAllText(Path.Combine(setup.Root, "folder", "ordinary.txt")) == "unexpected",
            "unexpected nonignored descendants block before any ignored child is removed");
    }

    private static async Task RealKindMattersAsync()
    {
        var folder = await SetupAsync("ignored-folder-kind", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        Directory.CreateDirectory(Path.Combine(folder.Root, "folder", "desktop.ini"));
        var folderAction = await DeleteActionAsync(folder.Writer);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            LocalWriterChecks.AuthorizedDeleteAsync(folder.Writer, folderAction, CancellationToken.None));
        LocalChecks.Require(Directory.Exists(Path.Combine(folder.Root, "folder", "desktop.ini")),
            "file-name ignore never treats a same-name folder as ignored");
        var file = await SetupAsync("ignored-file-kind", new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache"));
        LocalChecks.Seed(file.Root, "folder/cache", "ordinary file");
        var fileAction = await DeleteActionAsync(file.Writer);
        await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
            LocalWriterChecks.AuthorizedDeleteAsync(file.Writer, fileAction, CancellationToken.None));
        LocalChecks.Require(File.Exists(Path.Combine(file.Root, "folder", "cache")),
            "folder-name ignore never treats a same-name file as ignored");
    }

    private static async Task MoveCarriesIgnoredChildrenAsync()
    {
        var setup = await SetupAsync("ignored-move", new SyncIgnoreRule(SyncIgnoreKind.FileName, "desktop.ini"));
        LocalChecks.Seed(setup.Root, "folder/desktop.ini", "carried");
        var target = (await setup.Writer.ObserveAsync("folder", CancellationToken.None))!;
        await setup.Writer.MoveAsync(new SyncAction("move-ignored", 3, ActionKind.Move, Side.A,
            "moved", "folder", target, target, false, null), CancellationToken.None);
        LocalChecks.Require(!Directory.Exists(Path.Combine(setup.Root, "folder")) &&
            File.ReadAllText(Path.Combine(setup.Root, "moved", "desktop.ini")) == "carried",
            "whole-directory move carries ignored bytes without individual actions");
    }

    private static async Task RedirectedChildBlocksAsync()
    {
        var setup = await SetupAsync("ignored-junction", new SyncIgnoreRule(SyncIgnoreKind.FolderName, "cache"));
        var outside = Path.Combine(Path.GetDirectoryName(setup.Root)!, "outside");
        LocalChecks.Seed(outside, "sentinel.txt", "unchanged");
        Directory.CreateDirectory(Path.Combine(setup.Root, "folder"));
        var link = Path.Combine(setup.Root, "folder", "cache");
        LocalChecks.CreateJunction(link, outside);
        try
        {
            var action = await DeleteActionAsync(setup.Writer);
            await LocalWriterChecks.ExpectAsync<ExpectedTargetMismatchException>(() =>
                LocalWriterChecks.AuthorizedDeleteAsync(setup.Writer, action, CancellationToken.None));
            LocalChecks.Require(File.ReadAllText(Path.Combine(outside, "sentinel.txt")) == "unchanged",
                "ignored junction does not authorize deletion outside the folder");
        }
        finally { LocalChecks.RemoveJunction(link); }
    }

    private static async Task<SyncAction> DeleteActionAsync(LocalWriter writer) =>
        new("delete-ignored", 6, ActionKind.Delete, Side.A, "folder", null, null,
            (await writer.ObserveAsync("folder", CancellationToken.None))!, false, null);

    private static async Task<LocalWriterChecks.WriterSetup> SetupAsync(string name,
        params SyncIgnoreRule[] rules)
    {
        var root = LocalChecks.FreshRoot(name);
        var recovery = Path.Combine(Path.GetDirectoryName(root)!, "recovery");
        var writer = await LocalWriter.OpenAsync(LocalChecks.RootOf(root, "root-local"), Side.A,
            LocalChecks.PairId, LocalChecks.InstallationId, recovery, CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, ignoreRules: rules);
        return new(root, recovery, writer);
    }
}
