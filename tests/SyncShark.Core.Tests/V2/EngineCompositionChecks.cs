namespace Massdex.SyncShark.V2;

/// <summary>Fast checks for the single composition home: temp folder only, and any HTTP request fails the check.</summary>
internal static class EngineCompositionChecks
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "engine-composition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new FailingHandler());
        var host = new EngineHostInputs(root, null, http);

        using (var first = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System))
        {
            Require(File.Exists(first.DatabasePath), "the database is created at DatabasePath");
            Require(first.DatabasePath == SqliteStateStore.DatabasePath(root), "DatabasePath has one source");
            Require(first.Store.Pairs().Count == 0, "listing pairs opens no endpoint");
            Require(Throws<SyncRunRefusedException>(() => first.Endpoints.Inventory(LocalRoot("missing", root))),
                "an endpoint not opened for a run is refused");

            var missing = Path.Combine(root, "no-such-folder");
            var pair = new PairDef("p1", "p1", LocalRoot("a", missing),
                new RootDef("b", SyncProviders.Dropbox, "acct", "/x", "id:x"), [], [], true);
            Require(await ThrowsAsync<SyncRunRefusedException>(() => first.Endpoints.OpenAsync(pair, default)),
                "a missing local root is refused before any effect");
            var cloud = pair with { A = LocalRoot("a", root) };
            Require(await ThrowsAsync<SyncRunRefusedException>(() => first.Endpoints.OpenAsync(cloud, default)),
                "a pair whose sides cannot be bound is refused (no HTTP request made)");

            var id = await first.Roots.ObjectIdAsync(SyncProviders.Local, null, root, default);
            var (volume, fileId) = LocalItems.ParseObjectId(id);
            using var lease = PilotDirectoryLease.Open(root);
            Require(volume == lease.Identity.VolumeSerialNumber && fileId == lease.Identity.FileId,
                "the local root object id round-trips to the folder's NTFS identity");
            using (var watch = await first.Endpoints.WatchAsync(
                       new RootDef("w", SyncProviders.Local, null, root, id), default))
                Require(watch is LocalChangeWatch, "a local root is watched by a local change watch");
            Require(await ThrowsAsync<Exception>(() => first.Endpoints.WatchAsync(LocalRoot("wm", missing), default)),
                "watching a missing local root faults");
            Require(await ThrowsAsync<SyncRunRefusedException>(() =>
                    first.Roots.ObjectIdAsync(SyncProviders.Local, null, missing, default)),
                "probing a missing local folder is refused");
        }

        using (var again = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System))
        {
            Require(again.Store.Pairs().Count == 0, "the database reopens");
            var auto = again.StartAutoSync(_ => false, () => TimeSpan.Zero);
            await auto.StopAsync();
            Require(ReferenceEquals(auto, again.StartAutoSync(_ => false, () => TimeSpan.Zero)),
                "StartAutoSync returns the same instance and restarts it after a stop");
        }

        await DropboxSideAsync(Path.Combine(root, "dropbox"), http);
        foreach (var scenario in new[] { "immediate", "standalone", "same-pair", "other-pair" })
            await ClassificationAdoptionAsync(Path.Combine(root, "classification-adoption", scenario), scenario);
        await RecoverySettingAsync(Path.Combine(root, "recovery"), http);
        await WorkerSettingAsync(Path.Combine(root, "workers"), http);
        await BatchSettingAsync(Path.Combine(root, "batches"), http);
        await CliMappingAsync(Path.Combine(root, "cli"), http);
        await NoEffectBeforeRefusalAsync(Path.Combine(root, "effects"), http);
        await AccessRefusalAsync(Path.Combine(root, "access"), http);
        LongpollTimeout(root, http);
        await DataFolderOverlapAsync(Path.Combine(root, "overlap"), http);
        await RecoveryRootedPairAsync(Path.Combine(root, "recovery-pair"), http);
        await RehearsalIsolatedAsync(Path.Combine(root, "rehearsal-copy"), http);

        var damagedRoot = Path.Combine(root, "damaged");
        var damagedDb = SqliteStateStore.DatabasePath(damagedRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(damagedDb)!);
        File.WriteAllBytes(damagedDb, Enumerable.Repeat((byte)0x5A, 8192).ToArray());
        Require(Throws<StateDamagedException>(() =>
                EngineComposition.Open(host with { ProductRoot = damagedRoot }, V2Settings.Default, TimeProvider.System)
                    .Dispose()),
            "a garbage database is reported damaged, never recreated");
        Require(new FileInfo(damagedDb).Length == 8192, "the damaged database is left untouched");
        Console.WriteLine("Engine composition checks passed. Temp folder: " + root);
    }

    // Dropbox side: the connection comes from root.AccountId; a missing connection, credential or root identity is
    // refused, and a fully bound side opens, all without one HTTP request.
    private static async Task DropboxSideAsync(string root, HttpClient http)
    {
        Directory.CreateDirectory(root);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();
        var pair = new PairDef("pd", "pd", DropboxRoot("da", binding.ConnectionId, "/a", "id:a"),
            DropboxRoot("db", binding.ConnectionId, "/b", "id:b"), [], [], true);

        using (var stateless = EngineComposition.Open(new EngineHostInputs(Path.Combine(root, "stateless"), null, http),
                   V2Settings.Default, TimeProvider.System))
            Require(await ThrowsAsync<SyncRunRefusedException>(() => stateless.Endpoints.OpenAsync(pair, default)),
                "a Dropbox side without product state is refused");

        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var credentials = new Credentials();
        var host = new EngineHostInputs(root, instance.StateAuthority, http, DropboxCredentials: credentials);
        using var composition = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System);
        Require(await ThrowsAsync<SyncRunRefusedException>(() => composition.Endpoints.OpenAsync(pair, default)),
            "a Dropbox side whose connection is not retained is refused");
        Require(await ThrowsAsync<SyncRunRefusedException>(() =>
                composition.Roots.ObjectIdAsync(SyncProviders.Dropbox, binding.ConnectionId, "/a", default)),
            "probing a Dropbox root without a retained connection is refused");
        Require(await ThrowsAsync<SyncRunRefusedException>(() =>
                composition.Roots.ObjectIdAsync(SyncProviders.Dropbox, null, "/a", default)),
            "probing a Dropbox root that names no connection is refused");
        Require(await ThrowsAsync<SyncRunRefusedException>(() => composition.Endpoints.WatchAsync(pair.A, default)),
            "watching a Dropbox root without a retained connection is refused (no HTTP request)");
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        Require(await ThrowsAsync<SyncRunRefusedException>(() => composition.Endpoints.OpenAsync(pair, default)),
            "a Dropbox side whose connection has no saved credential is refused");
        Require(await ThrowsAsync<SyncRunRefusedException>(() =>
                composition.Roots.ObjectIdAsync(SyncProviders.Dropbox, binding.ConnectionId, "/a", default)),
            "probing a Dropbox root without a saved credential is refused");

        credentials.Token = "fixture-refresh";
        var unbound = pair with { B = pair.B with { RootObjectId = "" } };
        Require(await ThrowsAsync<SyncRunRefusedException>(() => composition.Endpoints.OpenAsync(unbound, default)),
            "a Dropbox side with no recorded root identity is refused");
        var opened = await composition.Endpoints.OpenAsync(pair, default);
        Require(opened.InventoryA is DropboxInventory && opened.WriterB is DropboxWriter,
            "a retained connection with a credential opens Dropbox inventory and writer (no HTTP request)");
    }

    // The process keeps one ProductionEndpoints for its whole lifetime, so a later run must never adopt the Dropbox
    // classification an earlier run left behind. The apply's reopen within one run still adopts its preview's read.
    private static async Task ClassificationAdoptionAsync(string root, string scenario)
    {
        var product = Path.Combine(root, "product");
        var local = Path.Combine(root, "local");
        var recovery = Path.Combine(root, "Recovery");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(local);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(product, "state", "instance.lock"));
        var binding = Binding();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        await new SyncPairStore(instance.StateAuthority).GetOrCreateInstallationIdAsync();
        await new SyncTransferSettingsStore(instance.StateAuthority).SaveAsync(
            new SyncTransferSettings(LocalRecoveryRoot: recovery));
        using var wire = new DropboxWire();
        wire.SeedFile("keep.txt", "kept");
        wire.SeedDeletedEntry("gone.txt");
        using var http = new HttpClient(wire);
        using var composition = EngineComposition.Open(
            new EngineHostInputs(product, instance.StateAuthority, http, new Credentials { Token = "fixture-refresh" }),
            V2Settings.Default, TimeProvider.System);
        var localId = await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, local, default);
        var pair = new PairDef("pk", "pk", new RootDef("ka", SyncProviders.Local, null, local, localId),
            DropboxRoot("kb", binding.ConnectionId, "/Pilot", DropboxWire.RootId), [], [], true);

        composition.Store.SavePair(pair);
        var progress = new Progress<RunProgress>();
        var run = composition.Run;
        var preview = await run.PreviewFirstSyncApplyResolvedAsync(pair.PairId, null, null, [], null, progress, default);
        Require(preview.Plan.RunUnavailable is null && preview.Continuation is not null,
            scenario + ": real run produces a usable preview and continuation");
        var classified = (DropboxInventory)composition.Endpoints.Inventory(pair.B);
        await classified.PrepareDeletionEvidenceAsync(["gone.txt"], default);
        Require(classified.IsAtOrUnderExactTombstone("gone.txt"), "preview classifies tombstones");

        if (scenario == "same-pair")
        {
            var next = await run.PreviewFirstSyncApplyResolvedAsync(pair.PairId, null, null, [], null, progress, default);
            Require(next.Plan.RunUnavailable is null && next.Continuation != preview.Continuation,
                "successive runs own distinct previews");
        }
        if (scenario == "other-pair")
        {
            var left = Path.Combine(root, "other-left");
            var right = Path.Combine(root, "other-right");
            Directory.CreateDirectory(left);
            Directory.CreateDirectory(right);
            var leftId = await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, left, default);
            var rightId = await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, right, default);
            var other = new PairDef("other", "other", new RootDef("oa", SyncProviders.Local, null, left, leftId),
                new RootDef("ob", SyncProviders.Local, null, right, rightId), [], [], true);
            composition.Store.SavePair(other);
            var otherPlan = await run.PreviewFirstSyncAsync(other.PairId, progress, default);
            Require(otherPlan.RunUnavailable is null, "another pair runs between preview and apply");
        }

        // No transfer is needed to exercise the real apply's reopen. Ask that opened inventory for evidence
        // afterwards, measuring the provider requests instead of inspecting a private cache implementation.
        var empty = preview.Plan with { Actions = [] };
        var beforeApply = Listings();
        var report = scenario == "standalone"
            ? await run.ApplyAsync(pair.PairId, empty, progress, default)
            : await run.ApplyAsync(pair.PairId, empty, progress, default, null, preview.Rebuildables, preview.Continuation);
        Require(report.Outcome != RunOutcomes.Failed, scenario + ": apply runs successfully");
        var applied = (DropboxInventory)composition.Endpoints.Inventory(pair.B);
        Require(!ReferenceEquals(applied, classified), "apply exercises the production endpoint reopen");
        await applied.PrepareDeletionEvidenceAsync(["gone.txt"], default);
        var reuse = scenario is "immediate" or "other-pair";
        Require(reuse ? Listings() == beforeApply : Listings() > beforeApply,
            scenario + (reuse ? ": evidence reuses its own preview" : ": evidence refreshes without its own preview"));
        Require(applied.IsAtOrUnderExactTombstone("gone.txt"), scenario + ": refreshed or reused evidence stays correct");

        int Listings() => wire.Calls.Count(path => path == "/2/files/list_folder");
    }

    // Recovery location and retention (decision 55): the product settings store, no code default; missing or
    // unusable is refused.
    private static async Task RecoverySettingAsync(string root, HttpClient http)
    {
        Directory.CreateDirectory(root);
        Require(new SyncTransferSettings().LocalRecoveryRoot is null, "the Recovery location has no code default");
        Require(Throws<InvalidDataException>(() => SyncTransferSettings.Normalize(
                new SyncTransferSettings(LocalRecoveryRoot: "relative\\recovery"))),
            "the settings store rejects a relative Recovery location");
        Require(Throws<InvalidDataException>(() => SyncTransferSettings.Normalize(
                new SyncTransferSettings(RecoveryRetentionDays: 0))),
            "the settings store rejects a non-positive Recovery retention");
        Require(Throws<SyncRunRefusedException>(() => new EnginePaths(root, null).RequireRecoveryRoot()),
            "an unset Recovery location is refused");
        Require(Throws<SyncRunRefusedException>(() => new EnginePaths(root, "relative\\recovery").RequireRecoveryRoot()),
            "a relative Recovery location is refused");
        var absent = Enumerable.Range('D', 23).Select(letter => (char)letter)
            .FirstOrDefault(letter => !Directory.Exists(letter + ":\\"));
        if (absent != default)
            Require(Throws<SyncRunRefusedException>(() =>
                    new EnginePaths(root, absent + ":\\Recovery").RequireRecoveryRoot()),
                "a Recovery location on an absent drive is refused");

        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var host = new EngineHostInputs(root, instance.StateAuthority, http);
        using (var unset = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System))
            Require(unset.Paths.RecoveryRoot is null && unset.Settings.RecoveryRetention == TimeSpan.FromDays(30),
                "with nothing saved the Recovery location is unset and retention is the 30-day default");
        var recovery = Path.Combine(root, "Recovery");
        await new SyncTransferSettingsStore(instance.StateAuthority).SaveAsync(
            new SyncTransferSettings(LocalRecoveryRoot: recovery, RecoveryRetentionDays: 7));
        using var composition = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System);
        Require(composition.Paths.RecoveryRoot == recovery &&
                composition.Settings.RecoveryRetention == TimeSpan.FromDays(7),
            "the composition takes the Recovery location and retention from the settings store");
        var paths = composition.Paths;
        Require(paths.RequireRecoveryRoot() == recovery, "a usable Recovery location is returned in full");
        var owned = paths.ProductOwnedPaths(recovery);
        Require(owned.Contains(Path.GetFullPath(root)) && owned.Contains(Path.GetFullPath(paths.Spool)) &&
                owned.Contains(recovery), "the data folder, spool and Recovery are product-owned for local scans");
    }

    private static async Task WorkerSettingAsync(string root, HttpClient http)
    {
        Directory.CreateDirectory(root);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var host = new EngineHostInputs(root, instance.StateAuthority, http);
        var settingsStore = new SyncTransferSettingsStore(instance.StateAuthority);
        foreach (var cap in new[] { 0, 128 })
        {
            await settingsStore.SaveAsync(new SyncTransferSettings(ResourceMode: "Performance", MaximumConcurrentFiles: cap));
            using var composition = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System);
            Require(composition.Settings.MaxWorkers == (cap == 0 ? V2Settings.Default.MaxWorkers : cap),
                "explicit transfer concurrency governs v2 workers; automatic preserves the default");
        }
    }

    // The engine batch tunables are adjustable through the product transfer settings the same way as the worker cap:
    // an explicit value governs the engine, automatic (zero) keeps the engine default (the single home of each default).
    private static async Task BatchSettingAsync(string root, HttpClient http)
    {
        Directory.CreateDirectory(root);
        // Defaults live only in V2Settings.Default.
        Require(V2Settings.Default.UploadBatchSize == 1000, "the upload batch size default is the provider maximum (1000)");
        Require(V2Settings.Default.UploadCommitConcurrency == 1, "the upload commit concurrency default is one");
        Require(V2Settings.Default.DeleteBatchSize == 1000, "the delete batch size default is the provider maximum (1000)");
        // The product settings validate their own range (0 = automatic).
        Require(Throws<InvalidDataException>(() => SyncTransferSettings.Normalize(new SyncTransferSettings(UploadBatchSize: 1001))),
            "the settings store rejects an upload batch size above the provider maximum");
        Require(Throws<InvalidDataException>(() => SyncTransferSettings.Normalize(new SyncTransferSettings(DeleteBatchSize: 1001))),
            "the settings store rejects a delete batch size above the provider maximum");
        Require(Throws<InvalidDataException>(() => SyncTransferSettings.Normalize(new SyncTransferSettings(UploadCommitConcurrency: 5))),
            "the settings store rejects an upload commit concurrency above four");
        Require(Throws<InvalidDataException>(() => SyncTransferSettings.Normalize(new SyncTransferSettings(UploadBatchSize: -1))),
            "the settings store rejects a negative upload batch size");
        var accepted = SyncTransferSettings.Normalize(new SyncTransferSettings(UploadBatchSize: 1000,
            UploadCommitConcurrency: 4, DeleteBatchSize: 1000));
        Require(accepted.UploadBatchSize == 1000 && accepted.UploadCommitConcurrency == 4 &&
                accepted.DeleteBatchSize == 1000,
            "the settings store accepts the maximum batch size and concurrency");

        using var instance = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var host = new EngineHostInputs(root, instance.StateAuthority, http);
        var settingsStore = new SyncTransferSettingsStore(instance.StateAuthority);
        foreach (var value in new[] { 0, 1000 })
        {
            await settingsStore.SaveAsync(new SyncTransferSettings(UploadBatchSize: value,
                UploadCommitConcurrency: value == 0 ? 0 : 4, DeleteBatchSize: value));
            using var composition = EngineComposition.Open(host, V2Settings.Default, TimeProvider.System);
            var expectedBatch = value == 0 ? V2Settings.Default.UploadBatchSize : value;
            Require(composition.Settings.UploadBatchSize == expectedBatch &&
                    composition.Settings.DeleteBatchSize == expectedBatch &&
                    composition.Settings.UploadCommitConcurrency == (value == 0 ? V2Settings.Default.UploadCommitConcurrency : 4),
                value == 0
                    ? "automatic batch settings preserve the engine defaults"
                    : "explicit transfer settings govern the engine batch tunables");
        }
    }

    // Run commands report an engine refusal as a review outcome, never a crash.
    private static async Task CliMappingAsync(string root, HttpClient http)
    {
        Directory.CreateDirectory(root);
        using var composition = EngineComposition.Open(new EngineHostInputs(root, null, http), V2Settings.Default,
            TimeProvider.System);
        var missing = Path.Combine(root, "no-such-folder");
        var pair = new PairDef("pc", "pc", LocalRoot("ca", missing), LocalRoot("cb", root), [], [], true);
        composition.Store.SavePair(pair);
        var runtime = new V2CliRuntime(null, composition.Store, composition.Run, composition.Roots, root);
        Require(await ThrowsAsync<SyncCliReviewException>(() =>
                runtime.PrepareFirstSyncAsync(pair.PairId, _ => { }, default)),
            "first-sync prepare maps an engine refusal to a review outcome");
        Require(await ThrowsAsync<SyncCliReviewException>(() => runtime.LoadPlanAsync(pair.PairId, default, 10, 0)),
            "plan maps an engine refusal to a review outcome");
        Require(await ThrowsAsync<SyncCliReviewException>(() => runtime.RunPairAsync(pair.PairId, _ => { }, default)),
            "run maps an engine refusal to a review outcome");
    }

    // Both sides' prerequisites are checked before any writer opens: a missing Dropbox credential on side B leaves
    // side A's local folder without a control namespace and the Recovery folder uncreated.
    private static async Task NoEffectBeforeRefusalAsync(string root, HttpClient http)
    {
        var product = Path.Combine(root, "product");
        var local = Path.Combine(root, "local");
        var recovery = Path.Combine(root, "Recovery");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(local);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(product, "state", "instance.lock"));
        var binding = Binding();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        await new SyncPairStore(instance.StateAuthority).GetOrCreateInstallationIdAsync();
        await new SyncTransferSettingsStore(instance.StateAuthority).SaveAsync(
            new SyncTransferSettings(LocalRecoveryRoot: recovery));
        var credentials = new Credentials();
        using var composition = EngineComposition.Open(
            new EngineHostInputs(product, instance.StateAuthority, http, credentials, new FailingHandler()),
            V2Settings.Default, TimeProvider.System);
        var localId = await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, local, default);
        var pair = new PairDef("pe", "pe", new RootDef("ea", SyncProviders.Local, null, local, localId),
            DropboxRoot("eb", binding.ConnectionId, "/e", "id:e"), [], [], true);
        Require(await ThrowsAsync<SyncRunRefusedException>(() => composition.Endpoints.OpenAsync(pair, default)),
            "a pair whose Dropbox side has no credential is refused");
        Require(!Directory.EnumerateFileSystemEntries(local).Any(),
            "the refusal allocated no control namespace in the local folder");
        Require(!Directory.Exists(recovery), "the refusal created no Recovery folder");
        credentials.Token = "fixture-refresh";
        await composition.Endpoints.OpenAsync(pair, default);
        Require(Directory.Exists(recovery), "once both sides pass, the writers open (Recovery folder created)");
    }

    // A revoked sign-in while creating a pair is an account-access refusal, which the CLI reports as review.
    private static async Task AccessRefusalAsync(string root, HttpClient failing)
    {
        var product = Path.Combine(root, "product");
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(local);
        using var instance = PilotInstanceLock.Acquire(Path.Combine(product, "state", "instance.lock"));
        var binding = Binding();
        await new DropboxConnectionStore(instance.StateAuthority).SaveNewAsync(binding);
        using var revoked = new HttpClient(new RevokedHandler());
        using var composition = EngineComposition.Open(
            new EngineHostInputs(product, instance.StateAuthority, revoked, new Credentials { Token = "fixture" },
                new FailingHandler()), V2Settings.Default, TimeProvider.System);
        Require(await ThrowsAsync<EndpointAccessException>(() =>
                composition.Roots.ObjectIdAsync(SyncProviders.Dropbox, binding.ConnectionId, "/a", default)),
            "a revoked sign-in while probing a Dropbox root is an account-access refusal");
        var runtime = new V2CliRuntime(null, composition.Store, composition.Run, composition.Roots, product);
        Require(await ThrowsAsync<SyncCliReviewException>(() =>
                runtime.CreatePairAsync(binding.ConnectionId, "/a", local, null, null, false, default)),
            "the CLI reports a revoked sign-in during pair create as a reconnect review");
    }

    // The production combination: a default HttpClient's 100 s timeout would cut a quiet long-poll short (why the
    // long-poll uses a separate client); the pair-sync route client instead takes the batch transport timeout.
    private static void LongpollTimeout(string root, HttpClient http)
    {
        using var composition = EngineComposition.Open(new EngineHostInputs(Path.Combine(root, "longpoll"), null,
            http, LongpollHandler: new FailingHandler()), V2Settings.Default, TimeProvider.System);
        var poll = V2Settings.Default.DropboxLongpollTimeout;
        Require(composition.LongpollHttpTimeout >= poll + TimeSpan.FromSeconds(90) + TimeSpan.FromSeconds(30),
            "the long-poll HTTP timeout covers the long-poll, Dropbox's 90 s jitter and headroom");
        Require(new HttpClient().Timeout < poll + TimeSpan.FromSeconds(90),
            "the shared client's default timeout alone would cut a quiet long-poll short (why a separate client)");
        Require(EngineComposition.DropboxHttpTimeoutFor(V2Settings.Default) > V2Settings.Default.BatchOperationTimeout,
            "the pair-sync route client's timeout sits above the explicit batch operation timeout");
    }

    // A local root never equals, contains or sits inside the data folder (database and spool live there).
    private static async Task DataFolderOverlapAsync(string root, HttpClient http)
    {
        var product = Path.Combine(root, "product");
        var inside = Path.Combine(product, "inside");
        Directory.CreateDirectory(inside);
        using var composition = EngineComposition.Open(new EngineHostInputs(product, null, http,
            LongpollHandler: new FailingHandler()), V2Settings.Default, TimeProvider.System);
        foreach (var path in new[] { product, inside, root })
        {
            var pair = new PairDef("po", "po", LocalRoot("oa", path), LocalRoot("ob", path), [], [], true);
            Require(await ThrowsAsync<SyncRunRefusedException>(() => composition.Endpoints.OpenAsync(pair, default)),
                "a local root equal to, inside or containing the data folder is refused: " + path);
        }
    }

    // The user may pair the Recovery folder itself (e.g. to back it up). Its own Recovery copies land in an
    // artifact-named subfolder (.syncshark-recovery-<pairId>) that the scan never inventories: no feedback loop.
    private static async Task RecoveryRootedPairAsync(string root, HttpClient http)
    {
        var product = Path.Combine(root, "product");
        var recovery = Path.Combine(root, "Recovery");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(product);
        Directory.CreateDirectory(recovery);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(recovery, "x.txt"), "keep me");
        File.WriteAllText(Path.Combine(other, "y.txt"), "only here");
        using var instance = PilotInstanceLock.Acquire(Path.Combine(product, "state", "instance.lock"));
        await new SyncPairStore(instance.StateAuthority).GetOrCreateInstallationIdAsync();
        await new SyncTransferSettingsStore(instance.StateAuthority).SaveAsync(
            new SyncTransferSettings(LocalRecoveryRoot: recovery));
        using var composition = EngineComposition.Open(new EngineHostInputs(product, instance.StateAuthority, http,
            new Credentials(), new FailingHandler()), V2Settings.Default, TimeProvider.System);
        var a = new RootDef("rr-a", SyncProviders.Local, null, recovery,
            await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, recovery, default));
        var b = new RootDef("rr-b", SyncProviders.Local, null, other,
            await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, other, default));
        var pair = new PairDef("p-recovery-rooted", "prr", a, b, [], [], true);
        composition.Store.SavePair(pair);
        var progress = new Progress<RunProgress>();
        // B-165: the real local scan reports files without hashes; one-sided copies are hashed while planning.
        var plan = await composition.Run.PreviewFirstSyncAsync(pair.PairId, progress, default);
        var again = await composition.Run.PreviewFirstSyncAsync(pair.PairId, progress, default);
        Require(Text(plan) == Text(again) && plan.Actions.Count(action => action.Kind == ActionKind.CopyNew) == 2 &&
                plan.Actions.All(action => action.Kind != ActionKind.CopyNew || action.Source?.Hash is not null),
            "the preview plans both one-sided copies with their hashes, the same plan each time: " + Text(plan) + " | again: " + Text(again));
        Require(composition.Store.Cursor(a.RootId) is null && composition.Store.Cursor(b.RootId) is null &&
                !composition.Store.FirstSyncDone(pair.PairId),
            "repeated previews retain observations without committing effect cursors or completing first sync");
        Require(!((ILocalGapStore)composition.Store).HasGapProtection(a.RootId) &&
                !((ILocalGapStore)composition.Store).HasGapProtection(b.RootId),
            "preview-only hashed inventory does not invent a local journal gap");
        if (NtfsUsnCheckpoint.TryParseCursor(plan.ScanCursorA, out _, out _))
        {
            // Same metadata does not mean unchanged content: replay must observe the journal write.
            var sourcePath = Path.Combine(recovery, "x.txt");
            var mtime = File.GetLastWriteTimeUtc(sourcePath);
            File.WriteAllText(sourcePath, "kept me");
            File.SetLastWriteTimeUtc(sourcePath, mtime);
            var changed = await composition.Run.PreviewFirstSyncAsync(pair.PairId, progress, default);
            Require(changed.Actions.Count(action => action.Kind == ActionKind.CopyNew) == 2 &&
                    changed.Actions.Single(action => action.Path == "x.txt").Source?.Hash !=
                    plan.Actions.Single(action => action.Path == "x.txt").Source?.Hash,
                "a later preview replays same-size same-time writes without dropping pending one-sided copies");
            plan = changed;
        }
        var applied = await composition.Run.ApplyAsync(pair.PairId, plan, progress, default);
        Require(applied.Failed == 0 && File.Exists(Path.Combine(other, "x.txt")) &&
                File.Exists(Path.Combine(recovery, "y.txt")),
            "the reviewed plan copies both one-sided files with the real local writers (" + applied.Outcome + " " +
            string.Join("; ", applied.Failures.Select(f => f.Outcome + " " + f.Reason)) + ")");

        File.Delete(Path.Combine(other, "x.txt"));
        var deleted = await composition.Run.IncrementalAsync(pair.PairId, progress, default);
        Require(!File.Exists(Path.Combine(recovery, "x.txt")) && deleted.Deleted == 1,
            "a delete in the Recovery-rooted pair removes the file there (" + deleted.Outcome + " done " + deleted.Done +
            " deleted " + deleted.Deleted + " failed " + deleted.Failed + " held " + deleted.Held + " " +
            string.Join("; ", deleted.Failures.Select(f => f.Outcome + " " + f.Reason)) + " | " +
            string.Join("; ", composition.Store.OpenNotices(pair.PairId).Select(n => n.Rule + " " + n.Path + " " + n.Detail)) + ")");
        var copies = Directory.GetDirectories(recovery, ".syncshark-recovery-*");
        Require(copies.Length == 1 && Directory.EnumerateFiles(copies[0]).Any(),
            "the deleted file is preserved into the pair's own Recovery subfolder inside its root");

        var next = await composition.Run.IncrementalAsync(pair.PairId, progress, default);
        Require(next.Done == 0 && next.Uploaded == 0 && next.Downloaded == 0 && !File.Exists(Path.Combine(other,
                Path.GetFileName(copies[0]))) &&
                Directory.GetFileSystemEntries(other).Select(Path.GetFileName).SequenceEqual(["y.txt"]),
            "the next scan does not see the Recovery copy (nothing synced back)");
        Require(composition.Store.Live(a.RootId, [Path.GetFileName(copies[0])]).Count == 0,
            "no Recovery artifact is recorded as data");
    }
    private static async Task RehearsalIsolatedAsync(string root, HttpClient http)
    {
        var product = Path.Combine(root, "source-product");
        var isolated = Path.Combine(root, "isolated-product");
        var left = Path.Combine(root, "left");
        var right = Path.Combine(root, "right");
        var recovery = Path.Combine(root, "recovery");
        foreach (var directory in new[] { product, isolated, left, right, recovery }) Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(left, "initial.txt"), "initial fixture bytes");
        const string pairId = "rehearsal-composition";
        using (var instance = PilotInstanceLock.Acquire(Path.Combine(product, "state", "instance.lock")))
        {
            await new SyncPairStore(instance.StateAuthority).GetOrCreateInstallationIdAsync();
            await new SyncTransferSettingsStore(instance.StateAuthority).SaveAsync(new SyncTransferSettings(LocalRecoveryRoot: recovery));
            using var composition = EngineComposition.Open(new EngineHostInputs(product, instance.StateAuthority, http,
                new Credentials(), new FailingHandler()), V2Settings.Default, TimeProvider.System);
            var a = new RootDef("rehearsal-left", SyncProviders.Local, null, left,
                await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, left, default));
            var b = new RootDef("rehearsal-right", SyncProviders.Local, null, right,
                await composition.Roots.ObjectIdAsync(SyncProviders.Local, null, right, default));
            composition.Store.SavePair(new PairDef(pairId, "Fixture pair", a, b, [], [], true));
            var progress = new Progress<RunProgress>();
            var plan = await composition.Run.PreviewFirstSyncAsync(pairId, progress, default);
            var applied = await composition.Run.ApplyAsync(pairId, plan, progress, default);
            Require(applied.Failed == 0 && File.Exists(Path.Combine(right, "initial.txt")),
                "production fixture establishes first-sync state and local control namespaces");
        }
        // Copy only this closed fixture, never a running/user store. Copy its configuration too.
        foreach (var directory in Directory.EnumerateDirectories(product, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(isolated, Path.GetRelativePath(product, directory)));
        foreach (var file in Directory.EnumerateFiles(product, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(isolated, Path.GetRelativePath(product, file)));
        File.WriteAllText(Path.Combine(left, "new.txt"), "new fixture bytes");
        var beforeLeft = FixtureTree(left);
        var beforeRight = FixtureTree(right);
        var beforeProduct = FixtureTree(product);
        var beforeRecovery = FixtureTree(recovery);
        var volume = Path.GetPathRoot(left)!.TrimEnd('\\').ToUpperInvariant();
        var journalBefore = SyncSharkJournalNative.Query(volume);
        var export = Path.Combine(root, "effects.csv");
        var messages = new List<string>();
        var runtime = V2CliRuntime.AcquireProduction(["run", "--pair", pairId, "--rehearse",
            "--data-root", isolated, "--export", export, "--runs", "2"]);
        try { await runtime.RehearseAsync(pairId, export, 2, message => { lock (messages) messages.Add(message); }, default); }
        finally { await runtime.DisposeAsync(); }
        var rows = File.ReadAllLines(export).Skip(1).ToArray();
        Require(rows.Any(row => row.StartsWith("1,CopyNew,", StringComparison.Ordinal)) &&
            !rows.Any(row => row.StartsWith("2,", StringComparison.Ordinal)),
            "isolated production SQLite rehearsal applies fixture changes once and converges on run two: " + string.Join(" | ", rows));
        Require(beforeLeft.SequenceEqual(FixtureTree(left)) && beforeRight.SequenceEqual(FixtureTree(right)) &&
            beforeProduct.SequenceEqual(FixtureTree(product)) && beforeRecovery.SequenceEqual(FixtureTree(recovery)),
            "two production rehearsals preserve real fixture folders, source store/configuration and recovery bytes");
        var journalAfter = SyncSharkJournalNative.Query(volume);
        if (journalBefore is not null)
            Require(journalAfter is not null && journalBefore[4] == journalAfter[4] && journalBefore[5] == journalAfter[5],
                "observe-only rehearsal does not resize the native journal");
        else Console.WriteLine("WARN rehearsal fixture: native journal sizing comparison unavailable on this runner.");
        Require(messages.Any(message => message.StartsWith("not simulated:", StringComparison.Ordinal)),
            "production rehearsal reports unsupported simulation paths");
    }

    private static string[] FixtureTree(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path) + (Directory.Exists(path) ? "/" : ":" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))))
        .OrderBy(value => value, StringComparer.Ordinal).ToArray();

    private static string Text(Plan plan) => string.Join("; ", plan.Actions.Select(action =>
        $"{action.Kind} {action.Target} {action.Path} {action.Source?.Hash}"));

    private static DropboxConnectionBinding Binding() =>
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, "account-id",
            "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();

    private sealed class RevokedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":\"invalid_grant\",\"error_description\":\"revoked\"}",
                    System.Text.Encoding.UTF8, "application/json")
            });
    }
    private static RootDef DropboxRoot(string id, string connectionId, string path, string objectId) =>
        new(id, SyncProviders.Dropbox, connectionId, path, objectId);

    private static RootDef LocalRoot(string id, string path) => new(id, SyncProviders.Local, null, path, "id:local:0");

    private sealed class Credentials : IDropboxRefreshTokenStore
    {
        public string? Token { get; set; }
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static async Task<bool> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); return false; }
        catch (T) { return true; }
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("No HTTP request is allowed in the composition checks: " + request.RequestUri);
    }
}
