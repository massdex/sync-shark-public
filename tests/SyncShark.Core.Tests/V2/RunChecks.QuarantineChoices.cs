namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static async Task QuarantineKeepExclusionAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "keep-exclusion"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var local = fx.A.Seed("kept.txt", "current file");
        await FirstSyncAsync(fx.Run(), fx);
        var cloud = (await fx.B.ObserveAsync(local.Path, default))!;
        var path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, local.Path);
        var grant = new DefenderGrant("keep-grant", "old-owner", cloud.ObjectId, null, path, AttendedElevatedAction.DownloadStagePath(path));
        store.StoreDefenderGrant(grant);
        async Task<Notice> Observe()
        {
            var gen = store.BeginGeneration("ra", "keep-observation");
            store.WriteSnapshot(await fx.A.FullAsync(gen, _ => true, new Progress<string>(), default));
            var current = store.Live("ra", ["kept.txt"]).Single();
            return store.OpenNotices("p1").Single(n => n.Quarantine?.ConflictingGrantId == grant.GrantId &&
                n.Quarantine.ExpectedTarget?.ObjectId == current.ObjectId);
        }
        var notice = await Observe();
        Require(NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.DefenderKeep),
            "grant notice offers cheap explicit keep-exclusion confirmation for current file");
        fx.A.Seed("kept.txt", "replacement after review");
        Require((await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default)).Outcome == Outcome.Skipped &&
            store.DefenderGrantById(grant.GrantId) == grant, "keep-exclusion refuses a changed reviewed current entity");
        var rewritten = (await fx.A.ObserveAsync("kept.txt", default))!;
        await fx.A.DeleteAsync(new SyncAction("replace-reviewed", 30, ActionKind.Delete, Side.A, rewritten.Path, null, null, rewritten, false, null), default);
        var replacement = fx.A.Seed("kept.txt", "new reviewed object");
        Require(replacement.ObjectId != local.ObjectId, "a genuinely new object receives a new review rather than replacing an existing hash");
        var originalNoticeId = notice.NoticeId;
        notice = await Observe();
        Require(notice.NoticeId != originalNoticeId && store.NoticeById("p1", originalNoticeId!)?.Notice.Quarantine?.ExpectedTarget?.ObjectId == local.ObjectId,
            "replacement review has a distinct identity key and cannot overwrite the prior entity's review");
        local = (await fx.A.ObserveAsync("kept.txt", default))!;
        var runtime = new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        await runtime.ResolveConflictAsync("p1", notice.Path, NoticeChoices.DefenderKeep, notice.NoticeId, 0, null, default);
        var bound = store.DefenderGrantById(grant.GrantId)!;
        Require(bound.LocalObject == local.ObjectId && bound.SourceObject == grant.SourceObject && bound.Path == grant.Path && bound.StagePath == grant.StagePath,
            "explicit CLI keep-exclusion rebinds only confirmed current file and preserves exact historical paths");
        Require(!store.AcceptDefenderGrant(grant, local, cloud), "keep-exclusion compare rejects stale grant metadata");
        fx.A.Seed("kept.txt", "", ItemKind.Folder);
        notice = await Observe();
        Require(!NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.DefenderKeep) &&
            (await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default)).Outcome == Outcome.Skipped &&
            store.DefenderGrantById(grant.GrantId) == bound, "keep-exclusion never authorizes a folder or subtree");
    }

    // Real LocalWriter and metadata inventory in the supplied test-owned root; no Defender or UAC.
    private static async Task QuarantineKeepNativeAsync(string root)
    {
        var owned = Path.Combine(root, "quarantine-keep-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(owned) || File.Exists(owned)) throw new IOException("Native fixture must be fresh.");
        var localPath = Path.Combine(owned, "root");
        Directory.CreateDirectory(localPath);
        Console.WriteLine("Native keep-exclusion fixture (retained): " + owned);
        var full = Path.Combine(localPath, "kept.txt");
        await File.WriteAllTextAsync(full, "reviewed bytes 0001");
        using var store = SqliteStateStore.Open(Path.Combine(root, "keep-native"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var nativeRoot = LocalChecks.RootOf(localPath, "ra");
        store.SavePair(fx.Pair with { A = nativeRoot });
        var writer = await LocalWriter.OpenAsync(nativeRoot, Side.A, "p1", LocalChecks.InstallationId, Path.Combine(owned, "recovery"), default);
        fx.Provider.AddWriter(nativeRoot, writer);
        var path = AttendedElevatedAction.ValidatePath(localPath, "kept.txt");
        var grant = new DefenderGrant("keep-native-grant", "old-owner", "cloud-object", null, path, AttendedElevatedAction.DownloadStagePath(path));
        store.StoreDefenderGrant(grant);
        var inventory = new LocalInventory(nativeRoot, Side.A, store, new HashSet<string>(StringComparer.OrdinalIgnoreCase), Settings, TimeProvider.System);
        async Task<Notice> Observe()
        {
            var gen = store.BeginGeneration("ra", "keep-native-observation");
            store.WriteSnapshot(await inventory.FullAsync(gen, _ => true, new Progress<string>(), default));
            var current = store.Live("ra", ["kept.txt"]).Single();
            return store.OpenNotices("p1").Single(n => n.Quarantine?.ConflictingGrantId == grant.GrantId &&
                n.Quarantine.ExpectedTarget?.ObjectId == current.ObjectId);
        }
        var notice = await Observe();
        Require(store.Live("ra", ["kept.txt"]).Single() is { Kind: ItemKind.File, Hash: null },
            "native inventory stays metadata-only; notice hashing does not hash the inventory");
        Require(notice.Quarantine!.ExpectedTarget is { Kind: ItemKind.File, Hash: not null },
            "native Defender notice captures the reviewed file content hash");
        var reviewedTime = File.GetLastWriteTimeUtc(full);
        var reviewedObject = notice.Quarantine.ExpectedTarget!.ObjectId;
        var reviewedHash = notice.Quarantine.ExpectedTarget.Hash;
        await File.WriteAllTextAsync(full, "reviewed bytes 0002");
        File.SetLastWriteTimeUtc(full, reviewedTime);
        Require((await writer.ObserveAsync("kept.txt", default))!.ObjectId == reviewedObject &&
            File.GetLastWriteTimeUtc(full) == reviewedTime, "rewrite retains object identity and restores the exact reviewed timestamp");
        Require((await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default)).Outcome == Outcome.Skipped &&
            store.DefenderGrantById(grant.GrantId) == grant, "native keep-exclusion refuses a same-size rewrite after review");
        notice = await Observe();
        Require(notice.Quarantine!.ExpectedTarget!.Hash == reviewedHash &&
            (await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default)).Outcome == Outcome.Skipped &&
            store.DefenderGrantById(grant.GrantId) == grant,
            "metadata-only grant republish preserves the reviewed hash and cannot authorize the in-place rewrite");
        store.AddNotices([notice with { Quarantine = notice.Quarantine with { ExpectedTarget = await writer.ObserveAsync("kept.txt", default) } }]);
        var rereview = store.NoticeById("p1", notice.NoticeId!)!.Value.Notice;
        Require(rereview.Quarantine!.ExpectedTarget!.Hash == reviewedHash && rereview.Detail.Contains("content differs") &&
            (await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default)).Outcome == Outcome.Skipped,
            "a supplied replacement hash is visibly flagged but never overwrites recorded review evidence");
        // Restore exactly the reviewed bytes before exercising successful native Keep.
        await File.WriteAllTextAsync(full, "reviewed bytes 0001");
        File.SetLastWriteTimeUtc(full, reviewedTime);
        var runtime = new V2CliRuntime(null, store, fx.Run(), new EndpointRootProbe(fx.Provider), root);
        await runtime.ResolveConflictAsync("p1", notice.Path, NoticeChoices.DefenderKeep, notice.NoticeId, 0, null, default);
        var observed = (await writer.ObserveAsync("kept.txt", default))!;
        var bound = store.DefenderGrantById(grant.GrantId)!;
        Require(observed.Hash is not null && bound.LocalObject == observed.ObjectId && bound.Path == grant.Path && bound.StagePath == grant.StagePath,
            "native LocalWriter keep-exclusion binds the reviewed current file by its real identity");
    }

    private static async Task QuarantineLegacyHashAsync(string root)
    {
        foreach (var closed in new[] { false, true })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "legacy-hash-" + closed), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var local = fx.A.Seed("legacy.txt", "review these bytes");
            var path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, local.Path);
            var grant = new DefenderGrant("legacy-hash", "old-owner", null, null, path, AttendedElevatedAction.DownloadStagePath(path));
            store.StoreDefenderGrant(grant);
            store.AddNotices([new Notice("p1", 30, NoticeKind.UserDecision, local.Path, "old hashless notice", QuarantineFailure.NoticeKey,
                Quarantine: new(Side.A, null, local with { Hash = null }, ScanOnly: true, ConflictingGrantId: grant.GrantId))]);
            var notice = store.OpenNotices("p1").Single();
            Require(notice.Quarantine!.ExpectedTarget!.Hash is null, "legacy fixture begins without a recorded hash");
            if (closed) store.CloseNotice(notice.NoticeId!, NoticeChoices.QuarantineIgnore);
            var result = await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default);
            var saved = store.NoticeById("p1", notice.NoticeId!)!.Value;
            Require(result.Outcome == Outcome.Skipped && store.DefenderGrantById(grant.GrantId) == grant,
                "legacy hashless notice never grants on the first Keep confirmation");
            if (closed)
                Require(saved.Closed && saved.Notice.Quarantine!.ExpectedTarget!.Hash is null && result.Reason!.Contains("not refreshed"),
                    "closed ignored notice reports accurately when its hash refresh is not persisted");
            else
            {
                Require(saved.Notice.Quarantine!.ExpectedTarget!.Hash == local.Hash,
                    "legacy notice without hash refreshes current content and requires a second confirmation without granting");
                Require((await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.DefenderKeep, default)).Outcome == Outcome.Done,
                    "a second explicit confirmation accepts the now-reviewed legacy file");
            }
        }
    }

    private static void QuarantineHashFailureBatch(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "hash-failure-batch"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.SavePair(fx.Pair with { A = fx.Pair.A with { RootPath = Path.Combine(root, "missing-hash-root"),
            RootObjectId = "id:local:00000001:0000000000000001" } });
        var blocked = fx.A.Seed("blocked.txt", "unreadable") with { Hash = null };
        var good = fx.A.Seed("good.txt", "committed bytes");
        var gen = store.BeginGeneration("ra", "capture-failure");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "", [blocked, good], []));
        var failed = new SyncAction("capture-failed", 30, ActionKind.CopyNew, Side.B, blocked.Path, null, blocked, null, false, "blocked-key");
        var succeeded = new SyncAction("capture-good", 30, ActionKind.CopyNew, Side.B, good.Path, null, good, null, false, "good-key");
        var copied = good with { ObjectId = "copied-good" };
        store.RecordIntents("p1", [failed, succeeded]);
        store.CommitResults("p1", gen, [(failed, new ActionResult(failed.ActionId, Outcome.Skipped, null, null, null, "test quarantine") { Quarantined = true }),
            (succeeded, new ActionResult(succeeded.ActionId, Outcome.Done, null, copied, null, null))]);
        Require(store.OpenNotices("p1").Single(n => n.Quarantine is not null).Quarantine!.Source!.Hash is null &&
            store.UnresolvedIntents("p1").Count == 0 && store.Baselines("p1").Any(b => b.A.ObjectId == good.ObjectId && b.B.ObjectId == copied.ObjectId),
            "failed file hashing records no hash and does not roll back the unrelated successful result");
        try
        {
            store.AddNotices([new Notice("unknown-pair", 30, NoticeKind.UserDecision, blocked.Path, "unknown", QuarantineFailure.NoticeKey,
                Quarantine: new(Side.B, blocked, null))]);
            throw new Exception("unknown pair was accepted");
        }
        catch (InvalidOperationException error)
        {
            Require(error.Message.Contains("Unknown pair unknown-pair"), "unknown quarantine pair reports a clear error before hashing");
        }
    }

    private static async Task QuarantineChoicesAsync(string root)
    {
        QuarantineHashFailureBatch(root);
        await QuarantineLegacyHashAsync(root);
        await QuarantineKeepExclusionAsync(root);
        await QuarantineKeepNativeAsync(root);
        string[] cli = ["--cli", "conflicts", "resolve", "--pair", "p1", "--path", "blocked.txt", "--choice", "defender-exclude", "--key", "n1", "--generation", "0"];
        try { SyncCliRegistry.Parse(cli); throw new InvalidOperationException("CLI accepted unattended exclusion"); }
        catch (SyncCliUsageException) { }
        SyncCliRegistry.Parse([.. cli, "--yes"]);
        var verifyCli = cli.Select(arg => arg == "defender-exclude" ? "defender-verify" : arg).ToArray();
        try { SyncCliRegistry.Parse(verifyCli); throw new InvalidOperationException("CLI accepted unattended verification"); }
        catch (SyncCliUsageException) { }
        SyncCliRegistry.Parse([.. verifyCli, "--yes"]);
        Require(true, "CLI exclusion and read-only admin verification require explicit --yes and accept notice generation0");
        foreach (var choice in new[] { NoticeChoices.QuarantineExclude, NoticeChoices.QuarantineDeleteDropbox, NoticeChoices.DefenderExclude, "replacement", "already-deleted" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "choice-" + choice), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var original = fx.B.Seed("blocked.txt", "old cloud bytes");
            var writer = new QuarantineWriter(fx.A, "write", 225);
            fx.Provider.AddWriter(fx.Pair.A, writer);
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var notice = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
            if (choice == "replacement")
            {
                await fx.B.DeleteAsync(new SyncAction("fake-replace", 30, ActionKind.Delete, Side.B, "blocked.txt", null, null, original, false, null), default);
                var replacement = fx.B.Seed("blocked.txt", "replacement cloud bytes");
                Require(replacement.ObjectId != original.ObjectId, "replacement scenario changes source object identity");
                writer.Enabled = false;
                var refresh = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineRetry, default);
                Require(refresh.Outcome == Outcome.Skipped, "source replacement requires explicit review of refreshed notice");
                var refreshedNotice = store.OpenNotices("p1").Single(n => n.Quarantine?.Source?.ObjectId == replacement.ObjectId);
                Require(refreshedNotice.NoticeId != notice.NoticeId && store.NoticeById("p1", notice.NoticeId!)!.Value.Closed &&
                    refreshedNotice.Quarantine!.HeldSource?.ObjectId == original.ObjectId,
                    "source replacement gets a separate review retaining old hold evidence before prior notice closes");
                var result = await run.ResolveQuarantineAsync("p1", refreshedNotice.NoticeId!, NoticeChoices.QuarantineRetry, default);
                Require(result.Outcome == Outcome.Done && !store.Live("rb", ["blocked.txt"]).Single().Flags.HasFlag(ItemFlags.Quarantined) &&
                    !store.OpenNotices("p1").Any(n => n.Quarantine is not null), "reviewed new identity retry clears retained old hold and closes notice");
                continue;
            }
            if (choice == "already-deleted")
            {
                await fx.B.DeleteAsync(new SyncAction("external-cloud-delete", 30, ActionKind.Delete, Side.B, original.Path, null, null, original, false, null), default);
                var released = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineDeleteDropbox, default);
                Require(released.Outcome == Outcome.Done, "explicit delete settles already-absent reviewed cloud entity");
                await run.IncrementalAsync("p1", new RecordingProgress(), default);
                Require(store.LiveFlagged("rb", ItemFlags.Quarantined).Count == 0 && !store.OpenNotices("p1").Any(n => n.Quarantine is not null),
                    "already-absent source leaves no permanent ghost hold");
                continue;
            }
            if (choice == NoticeChoices.DefenderExclude)
            {
                var finalOnly = new SuccessfulElevation(() =>
                {
                    writer.TargetRoot = fx.Pair.A.RootPath;
                    writer.AllowedPaths = new(StringComparer.OrdinalIgnoreCase) { AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, "blocked.txt") };
                });
                var blockedStage = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, choice, default, finalOnly);
                Require(blockedStage.Outcome != Outcome.Done && await fx.A.ObserveAsync("blocked.txt", default) is null,
                    "final-path-only exclusion cannot bypass antivirus on download stage");
            }
            var elevation = new SuccessfulElevation(() =>
            {
                var path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, "blocked.txt");
                writer.TargetRoot = fx.Pair.A.RootPath;
                writer.AllowedPaths = new(StringComparer.OrdinalIgnoreCase) { path, AttendedElevatedAction.DownloadStagePath(path) };
            });
            var resolved = await run.ResolveQuarantineAsync("p1", notice.NoticeId!, choice, default, elevation);
            Require(resolved.Outcome == Outcome.Done, choice + " succeeds: " + resolved.Reason);
            if (choice == NoticeChoices.QuarantineExclude)
                Require(store.Pair("p1")!.Excluded.Contains("blocked.txt") && await fx.B.ObserveAsync("blocked.txt", default) is not null,
                    "exclude records exact path and keeps cloud bytes");
            if (choice == NoticeChoices.QuarantineDeleteDropbox)
            {
                Require(await fx.B.ObserveAsync("blocked.txt", default) is null, "explicit reviewed Dropbox revision deletion executes");
                await run.IncrementalAsync("p1", new RecordingProgress(), default);
                Require(store.LiveFlagged("ra", ItemFlags.Quarantined).Count == 0 && store.LiveFlagged("rb", ItemFlags.Quarantined).Count == 0 &&
                    !store.OpenNotices("p1").Any(n => n.ReasonKey == QuarantineFailure.NoticeKey), "completed cloud deletion leaves no ghost hold or notice on next run");
            }
            if (choice == NoticeChoices.DefenderExclude)
            {
                var written = await fx.A.ObserveAsync("blocked.txt", default);
                Require(elevation.Calls == 1 && written is not null, "fake verified exclusion makes one explicit download retry");
                var retained = store.DefenderGrantConflict("p1", new SyncAction("folder-probe", 30, ActionKind.MkDir, Side.A,
                    "blocked.txt", null, null, null, false, null));
                Require(retained?.LocalObject == written!.ObjectId && retained.SourceObject == original.ObjectId,
                    "verified publication rebinds entity-owned grant from committed action result");
                Require(store.DefenderGrantConflict("p1", new SyncAction("same-update", 30, ActionKind.Update, Side.A,
                    "blocked.txt", null, original, written, false, null)) is null, "legitimate same-entity update is not blocked");
                var missingRetry = new SyncAction("missing-retry", 30, ActionKind.CopyNew, Side.A, "blocked.txt", null, original, null, false, "missing-retry-key")
                    { QuarantineStage = true };
                Require(store.DefenderGrantConflict("p1", missingRetry with { Source = original with { ObjectId = "changed-cloud-object" } }) is not null &&
                    store.DefenderGrantConflict("p1", missingRetry with { Source = original with { Kind = ItemKind.Folder } }) is not null,
                    "explicit missing-target restore never authorizes changed source or folder");
                await fx.A.DeleteAsync(new SyncAction("external-local-removal", 30, ActionKind.Delete, Side.A, "blocked.txt", null, null, written, false, null), default);
                store.AddNotices([new Notice("p1", 30, NoticeKind.UserDecision, "blocked.txt", "fake removed local target", QuarantineFailure.NoticeKey,
                    Quarantine: new(Side.A, original, written, GrantedExclusionPath: retained!.Path, GrantedStagePath: retained.StagePath))]);
                var removedNotice = store.OpenNotices("p1").Single(n => n.Quarantine?.ExpectedTarget?.ObjectId == written!.ObjectId &&
                    n.Quarantine.Source?.ObjectId == original.ObjectId);
                var restored = await run.ResolveQuarantineAsync("p1", removedNotice.NoticeId!, NoticeChoices.QuarantineRetry, default);
                var restoredLocal = await fx.A.ObserveAsync("blocked.txt", default);
                Require(restored.Outcome == Outcome.Done && restoredLocal is not null && restoredLocal.ObjectId != written.ObjectId &&
                    store.DefenderGrantById(retained.GrantId)!.LocalObject == restoredLocal.ObjectId,
                    "explicit same-source restore after local disappearance rebinds only verified committed result");
            }
        }
    }

    private static async Task QuarantineGrantGuardAsync(string root)
    {
        await QuarantineGrantObservationAsync(root);
        QuarantineGrantMigration(root);
        await QuarantineGrantResumeAsync(root);
        foreach (var kind in new[] { ItemKind.File, ItemKind.Folder })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "grant-guard-" + kind), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            fx.B.Seed("blocked", "new entity", kind);
            if (kind == ItemKind.Folder) fx.B.Seed("blocked/child", "held subtree");
            fx.B.Seed("sibling.txt", "independent");
            var path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, "blocked");
            var grant = new DefenderGrant("old-grant", "removed-old-entity", "removed-cloud-entity", "removed-local-entity", path,
                AttendedElevatedAction.DownloadStagePath(path));
            store.StoreDefenderGrant(grant); // possible grant survives even though its old file row no longer exists
            var other = fx.B.Seed("other", "move candidate", ItemKind.Folder);
            foreach (var destination in new[] { "blocked", "blocked/child", "" })
                Require(store.DefenderGrantConflict("p1", new SyncAction("folder-move", 30, ActionKind.Move, Side.A,
                    destination, "other", other, other, false, null)) is not null,
                    "grant guards folder move at, below, or enclosing original historical path");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            Require(await fx.A.ObserveAsync("blocked", default) is null && await fx.A.ObserveAsync("sibling.txt", default) is not null,
                "durable exclusion guard blocks new " + kind + " before local materialization without blocking sibling");
            Require(store.FirstSyncDone("p1") && await fx.A.ObserveAsync("blocked/child", default) is null,
                "settled grant hold and descendants do not keep first sync pending");
            var note = store.OpenNotices("p1").Single(n => n.Quarantine?.ConflictingGrantId == grant.GrantId);
            Require(note.Kind == NoticeKind.UserDecision && NoticeChoices.Options(note).Any(o => o.Choice == NoticeChoices.DefenderVerify),
                "grant guard exposes stable decision and explicit read-only admin option");
            foreach (var view in new[] { new DefenderExclusionView(false, []), new DefenderExclusionView(true, [grant.Path]), new DefenderExclusionView(true, [grant.StagePath]) })
            {
                var result = await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.QuarantineRetry, default,
                    exclusions: new FakeExclusions(view));
                Require(result.Outcome == Outcome.Skipped && store.DefenderGrantById(grant.GrantId) is not null,
                    "hidden or remaining exact exclusion cannot release durable grant");
            }
            var changed = await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.DefenderVerify, default,
                new VerifyElevation(AttendedElevatedAction.ExclusionsConfirmedAbsent, () =>
                    store.AddNotices([store.NoticeById("p1", note.NoticeId!)!.Value.Notice with { Detail = "changed during fake UAC" }])));
            Require(changed.Outcome == Outcome.Skipped && store.DefenderGrantById(grant.GrantId) is not null,
                "changed notice during admin verification cannot retire grant");
            var changedGrant = await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.DefenderVerify, default,
                new VerifyElevation(AttendedElevatedAction.ExclusionsConfirmedAbsent, () => store.StoreDefenderGrant(grant with { Verified = true })));
            Require(changedGrant.Outcome == Outcome.Skipped && store.DefenderGrantById(grant.GrantId) is { Verified: true },
                "changed grant during admin verification cannot be retired by stale result");
            var ordinaryExit = await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.DefenderVerify, default,
                new VerifyElevation(0));
            Require(ordinaryExit.Outcome == Outcome.Skipped && store.DefenderGrantById(grant.GrantId) is not null,
                "ordinary elevated success is not absence confirmation");
            var verified = kind == ItemKind.File
                ? await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.DefenderVerify, default,
                    new VerifyElevation(AttendedElevatedAction.ExclusionsConfirmedAbsent))
                : await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.QuarantineRetry, default,
                    new FakeElevation(), new FakeExclusions(new DefenderExclusionView(true, [])));
            Require(verified.Outcome == Outcome.Done && store.DefenderGrantById(grant.GrantId) is null,
                "explicit authoritative absent confirmation retires only reviewed grant");
            await run.IncrementalAsync("p1", new RecordingProgress(), default);
            Require(await fx.A.ObserveAsync("blocked", default) is not null, "verified removal lets current entity replan normally");
        }
    }

    private static async Task QuarantineGrantObservationAsync(string root)
    {
        using (var readStore = SqliteStateStore.Open(Path.Combine(root, "grant-read-only"), Settings, TimeProvider.System))
        {
            var readFx = Build(suppliedStore: readStore);
            var current = readFx.A.Seed("observed", "local bytes");
            var generation = readStore.BeginGeneration("ra", "observed-before-grant");
            readStore.WriteSnapshot(new Snapshot("ra", Side.A, generation, "", [current], []));
            var full = AttendedElevatedAction.ValidatePath(readFx.Pair.A.RootPath, current.Path);
            readStore.StoreDefenderGrant(new("read-only", "old-owner", null, "old-local", full, AttendedElevatedAction.DownloadStagePath(full)));
            Require(readStore.OpenNotices("p1").Count == 0, "read-only fixture has unmatched grant but no previously emitted notice");
            Require(LinkExclusions.Of(readStore, readFx.Pair, false).Covers(current.Path) && readStore.OpenNotices("p1").Count == 0,
                "grant-derived preview getter is read-only even before any notice was emitted");
        }
        foreach (var kind in new[] { ItemKind.File, ItemKind.Folder })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "grant-observed-" + kind), Settings, TimeProvider.System);
            var fx = Build(suppliedStore: store);
            var local = fx.A.Seed("blocked", "identical", kind);
            var cloud = fx.B.Seed("blocked", "identical", kind);
            fx.B.Seed("sibling", "independent");
            var path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, "blocked");
            var grant = new DefenderGrant("observed", "old-owner", "old-cloud", "old-local", path, AttendedElevatedAction.DownloadStagePath(path));
            store.StoreDefenderGrant(grant);
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            Require(store.OpenNotices("p1").Any(n => n.Quarantine?.ConflictingGrantId == grant.GrantId) &&
                store.BaselinesAt("p1", Side.A, ["blocked"]).Count == 0 && await fx.A.ObserveAsync("sibling", default) is not null,
                "observed same-content replacement " + kind + " is held before adoption while sibling syncs");
            var note = store.OpenNotices("p1").Single(n => n.Quarantine?.ConflictingGrantId == grant.GrantId);
            await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.QuarantineIgnore, default);
            Require(LinkExclusions.Of(store, fx.Pair, false).Covers("blocked/child") &&
                !LinkExclusions.Of(store, fx.Pair, false).NarrowAction(_ => true)(""),
                "ignored grant notice still derives entity/subtree and enclosing-action hold");
            var adopt = new SyncAction("explicit-adopt", 30, ActionKind.Adopt, Side.A, "blocked", null, cloud, local, false, "observed-adopt");
            var adoption = new List<ActionResult>();
            await foreach (var result in fx.Executor.ApplyAsync(new Plan("p1", store.BeginGeneration("ra", "adopt-probe"), [adopt], []), fx.A, fx.B, default)) adoption.Add(result);
            Require(adoption.Single().Outcome == Outcome.Skipped && store.DefenderGrantById(grant.GrantId) == grant,
                "commit-only adoption cannot bypass grant binding or silently rebind it");
            await run.ResolveQuarantineAsync("p1", note.NoticeId!, NoticeChoices.QuarantineRetry, default,
                exclusions: new FakeExclusions(new DefenderExclusionView(true, [])));
            var retryMarker = store.OpenNotices("p1").Single(n => n.ReasonKey == "defender-grant-retired");
            Require(retryMarker.Kind == NoticeKind.Info && NoticeChoices.Options(retryMarker).Count == 0,
                "grant retirement replan marker is informational and has no action choices");
            await run.IncrementalAsync("p1", new RecordingProgress(), default);
            Require(store.BaselinesAt("p1", Side.A, ["blocked"]).Any(b => b.A.ObjectId == local.ObjectId),
                "verified grant retirement permits ordinary same-content adoption");
            Require(!store.OpenNotices("p1").Any(n => n.ReasonKey == "defender-grant-retired"),
                "grant retirement replan marker closes after next-run evaluation");
            var currentGrant = grant with { GrantId = "current-owner", SourceObject = cloud.ObjectId, LocalObject = local.ObjectId };
            store.StoreDefenderGrant(currentGrant);
            await fx.A.DeleteAsync(new SyncAction("external-remove", 30, ActionKind.Delete, Side.A, "blocked", null, null, local, false, null), default);
            var replacement = fx.A.Seed("blocked", "identical", kind);
            await run.IncrementalAsync("p1", new RecordingProgress(), default);
            Require(store.OpenNotices("p1").Any(n => n.Quarantine?.ConflictingGrantId == currentGrant.GrantId) &&
                !store.BaselinesAt("p1", Side.A, ["blocked"]).Any(b => b.A.ObjectId == replacement.ObjectId) &&
                store.DefenderGrantById(currentGrant.GrantId)!.LocalObject == local.ObjectId,
                "incremental external same-content replacement remains held without observation rebind");
        }
    }

    private static async Task QuarantineGrantResumeAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "grant-resume"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var source = fx.B.Seed("blocked", "verified bytes");
        var path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, source.Path);
        var grant = new DefenderGrant("resume-grant", "old-owner", "old-source", "old-local", path,
            AttendedElevatedAction.DownloadStagePath(path));
        store.StoreDefenderGrant(grant);
        var stage = new SyncDownloadStage(grant.StagePath, "stage-object", "fake-recovery");
        var action = new SyncAction("resume-new-entity", 30, ActionKind.CopyNew, Side.A, source.Path, null, source, null, false, "resume-object")
        { ProviderEffect = new(SyncDirectDownloadCheckpoint.Kind, System.Text.Json.JsonSerializer.Serialize(stage)) };
        store.RecordIntent("p1", action);
        var writer = new GrantResumeWriter(fx.A);
        var sibling = new SyncAction("resume-sibling", 30, ActionKind.CopyNew, Side.A, "sibling", null,
            fx.B.Seed("sibling", "sibling bytes"), null, false, null);
        store.RecordIntent("p1", sibling);
        var results = await fx.Executor.ReconcileAsync([action, sibling], store.BeginGeneration("ra", "guard-resume"), writer, fx.B, default);
        Require(results[0].Outcome == Outcome.Uncertain && results[1].Outcome == Outcome.Skipped && writer.ResumeCalls == 0 &&
            writer.StagePresent && writer.AclRestores == 0 && await fx.A.ObserveAsync("blocked", default) is null &&
            store.DefenderGrantById(grant.GrantId) == grant,
            "reconcile guard leaves target/stage/ACL/grant untouched and still reconciles independent sibling");
        Require(store.OpenNotices("p1").Any(n => n.Quarantine?.ConflictingGrantId == grant.GrantId), "blocked resume offers grant verification");
        var published = fx.A.Seed("blocked", "verified bytes");
        var completed = action with { ActionId = "already-published", ProviderEffect = new(SyncDirectDownloadCheckpoint.Kind,
            System.Text.Json.JsonSerializer.Serialize(stage with { ObjectId = published.ObjectId })) };
        store.RecordIntent("p1", completed);
        var acknowledged = (await fx.Executor.ReconcileAsync([completed], store.BeginGeneration("ra", "guard-ack"), writer, fx.B, default)).Single();
        Require(acknowledged.Outcome == Outcome.Done && writer.ResumeCalls == 0 && writer.AclRestores == 0 && writer.StagePresent &&
            store.DefenderGrantById(grant.GrantId) == grant, "exact stage identity and hash acknowledge completed publication without resuming or rebinding unrelated grant");
        var unknown = completed with { ActionId = "missing-source-hash", Source = source with { Hash = null } };
        store.RecordIntent("p1", unknown);
        Require((await fx.Executor.ReconcileAsync([unknown], store.BeginGeneration("ra", "guard-unknown"), writer, fx.B, default)).Single().Outcome == Outcome.Uncertain,
            "missing hash never proves safe completed publication");
    }

    private sealed class GrantResumeWriter(IEndpointWriter inner) : QuarantineWriter(inner, "none", 225), ISyncDirectDownloadTarget
    {
        internal int ResumeCalls, AclRestores;
        internal bool StagePresent = true;
        public Task<ItemState> PutDownloadedAsync(SyncAction action, ISyncDirectDownloadSource source,
            Func<SyncDownloadStage, CancellationToken, Task>? callback, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActionResult> ResumeDownloadAsync(SyncAction action, ProviderEffectCheckpoint checkpoint, CancellationToken ct)
        {
            ResumeCalls++; AclRestores++; StagePresent = false;
            return Task.FromResult(new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, "fake resume touched stage"));
        }
    }

    private static void QuarantineGrantMigration(string root)
    {
        foreach (var prior in new[] { 8, 9, 10, 11 })
        foreach (var interrupted in new[] { false, true })
        {
            var data = Path.Combine(root, "grant-migration-" + prior + "-" + interrupted);
            string path;
            string ignoredId = "", legacyId = "";
            using (var store = SqliteStateStore.Open(data, Settings, TimeProvider.System))
            {
                var fx = Build(suppliedStore: store);
                path = AttendedElevatedAction.ValidatePath(fx.Pair.A.RootPath, "legacy");
                var source = fx.B.Seed("legacy", "old bytes");
                var note = new Notice("p1", 30, NoticeKind.UserDecision, "legacy", "old verified grant", QuarantineFailure.NoticeKey,
                    Quarantine: new(Side.A, source, null, GrantedExclusionPath: path, GrantedStagePath: AttendedElevatedAction.DownloadStagePath(path)));
                store.AddNotices([note, note with { Path = "ignored" }]);
                foreach (var saved in store.OpenNotices("p1"))
                {
                    if (saved.Path == "ignored") ignoredId = saved.NoticeId!;
                    else legacyId = saved.NoticeId!;
                    store.CloseNotice(saved.NoticeId!, saved.Path == "ignored" ? NoticeChoices.QuarantineIgnore : NoticeChoices.QuarantineRetry);
                }
            }
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + SqliteStateStore.DatabasePath(data)))
            {
                db.Open();
                using var cmd = db.CreateCommand();
                cmd.CommandText = (interrupted ? "DELETE FROM defender_grant; " : "DROP TABLE defender_grant; DROP TABLE defender_deferral; ") +
                    "DELETE FROM quarantine_upgrade; UPDATE notice SET created=0; PRAGMA user_version=" + prior + ";";
                cmd.ExecuteNonQuery(); // synthetic isolated prior-version fixture, never a live rollback
            }
            using (var store = SqliteStateStore.Open(data, Settings, TimeProvider.System))
            {
                var action = new SyncAction("new-folder", 30, ActionKind.MkDir, Side.A, "legacy", null, null, null, false, null);
                Require(store.DefenderGrantConflict("p1", action) is not null, "schema " + prior + " to12 preserves legacy granted path before notice pruning");
                store.Prune();
                Require(store.DefenderGrantConflict("p1", action) is not null &&
                    store.QuarantineRecoveryRetained("p1", ignoredId),
                    "grant survives pruning while ignored recovery remains retained");
                Require(store.NoticeById("p1", legacyId) is null,
                    "closed non-ignore quarantine notice expires under retention");
            }
        }
    }

    private sealed class FakeExclusions(DefenderExclusionView view) : IDefenderExclusions
    {
        public Task<DefenderExclusionView> ReadAsync(CancellationToken ct) => Task.FromResult(view);
    }
    private sealed class VerifyElevation(int exit, Action? beforeReturn = null) : IElevatedAction
    {
        public Task<int> RunAsync(string verb, IReadOnlyList<string> args, CancellationToken ct)
        {
            Require(verb == AttendedElevatedAction.DefenderVerify && args.Count == 3 && args[2] == AttendedElevatedAction.DownloadStagePath(args[1]),
                "admin verifier receives only recorded exact file and sibling stage");
            var command = AttendedElevatedAction.Command(verb, args);
            Require(!command.Contains("Add-MpPreference") && !command.Contains("Remove-MpPreference") && command.Contains("exit 23"),
                "admin verification fixed script is read-only and uses distinct confirmation exit");
            beforeReturn?.Invoke();
            return Task.FromResult(exit);
        }
    }

    private static async Task QuarantineScanReplacementAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "scan-replacement"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var old = fx.A.Seed("blocked.txt", "old local");
        await FirstSyncAsync(fx.Run(), fx);
        store.HoldQuarantine("ra", old);
        var note = new Notice("p1", 30, NoticeKind.Info, old.Path, "fake scan hold", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.A, null, old, ScanOnly: true));
        store.AddNotices([note]);
        await fx.A.DeleteAsync(new SyncAction("fake-replace", 30, ActionKind.Delete, Side.A, old.Path, null, null, old, false, null), default);
        var replacement = fx.A.Seed(old.Path, "new local");
        var gen = store.BeginGeneration("ra", "replacement-observed");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "", [replacement], []));
        Require(store.Live("ra", [old.Path]).Single().Flags.HasFlag(ItemFlags.Quarantined), "inventory replacement inherits sticky scan hold");
        var notice = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
        var result = await fx.Run().ResolveQuarantineAsync("p1", notice.NoticeId!, NoticeChoices.QuarantineRetry, default);
        Require(result.Outcome == Outcome.Done && !store.Live("ra", [old.Path]).Single().Flags.HasFlag(ItemFlags.Quarantined),
            "scan-only readable replacement clears observed identity hold before closing");
        store.HoldQuarantine("ra", replacement);
        store.AddNotices([note with { Quarantine = new(Side.A, null, replacement, true) }]);
        var ignored = store.OpenNotices("p1").Single(n => n.Quarantine is not null);
        await fx.Run().ResolveQuarantineAsync("p1", ignored.NoticeId!, NoticeChoices.QuarantineIgnore, default);
        Require(store.QuarantineRecoveryRetained("p1", ignored.NoticeId!), "ignored hold retains explicit exact-key recovery");
        result = await fx.Run().ResolveQuarantineAsync("p1", ignored.NoticeId!, NoticeChoices.QuarantineRetry, default);
        Require(result.Outcome == Outcome.Done && !store.QuarantineRecoveryRetained("p1", ignored.NoticeId!), "ignored exact-key retry settles and disables stale recovery");
        store.HoldQuarantine("ra", replacement);
        store.AddNotices([note with { Quarantine = new(Side.A, null, replacement, true) }]);
        await fx.Run().ResolveQuarantineAsync("p1", ignored.NoticeId!, NoticeChoices.QuarantineIgnore, default);
        var newer = replacement with { ObjectId = "new-entity-after-ignore" };
        gen = store.BeginGeneration("ra", "new-ignored-path-entity");
        store.WriteSnapshot(new Snapshot("ra", Side.A, gen, "", [newer], []));
        store.AddNotices([note with { Quarantine = new(Side.A, null, newer, true) }]);
        Require(store.OpenNotices("p1").Single(n => n.Quarantine is not null).Quarantine!.ExpectedTarget!.ObjectId == newer.ObjectId,
            "new entity at ignored path reopens notice with matching held identity");
        var folderNote = note with { Quarantine = new(Side.A, null, newer with { Kind = ItemKind.Folder }, true) };
        Require(!NoticeChoices.Options(folderNote).Any(o => o.Choice is NoticeChoices.DefenderExclude or NoticeChoices.QuarantineDeleteDropbox),
            "folder holds never offer broad Defender exclusion or unsupported cloud deletion");
    }

    private static async Task QuarantineRenameAsync(string root)
    {
        using var store = SqliteStateStore.Open(Path.Combine(root, "rename-hold"), Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        var original = fx.A.Seed("blocked.txt", "retained rename bytes");
        var run = fx.Run();
        await FirstSyncAsync(run, fx);
        var cloud = await fx.B.ObserveAsync(original.Path, default);
        store.HoldQuarantine("ra", original);
        store.HoldQuarantine("rb", cloud!);
        store.AddNotices([new Notice("p1", 30, NoticeKind.Info, original.Path, "fake quarantine", QuarantineFailure.NoticeKey,
            Quarantine: new(Side.B, original, cloud))]);
        var moved = await fx.A.MoveAsync(new SyncAction("fake-rename", 30, ActionKind.Move, Side.A, "renamed.txt", original.Path,
            original, original, false, null), default);
        Require(moved.ObjectId == original.ObjectId, "quarantine rename preserves the exact source object identity");
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(store.Live("ra").Any(i => i.ObjectId == original.ObjectId && i.Flags.HasFlag(ItemFlags.Quarantined)),
            "same-identity rename retains protected entity evidence");
        Require(await fx.A.ObserveAsync("renamed.txt", default) is not null && await fx.B.ObserveAsync("blocked.txt", default) is not null,
            "renamed quarantined item never deletes either surviving endpoint copy");
        store.ForgetCursor("ra");
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(store.Live("ra").Any(i => i.ObjectId == original.ObjectId && i.Flags.HasFlag(ItemFlags.Quarantined)) &&
            await fx.A.ObserveAsync("renamed.txt", default) is not null && await fx.B.ObserveAsync("blocked.txt", default) is not null,
            "full refresh after quarantine rename still retains hold and both copies");
        fx.A.Seed("other", "", ItemKind.Folder);
        var across = await fx.A.MoveAsync(new SyncAction("across-directory", 30, ActionKind.Move, Side.A,
            "other/renamed.txt", moved.Path, moved, moved, false, null), default);
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(store.Live("ra", [across.Path]).Single().Flags.HasFlag(ItemFlags.Quarantined) &&
            store.OpenNotices("p1").Any(n => n.Path == across.Path && n.Quarantine?.Source?.ObjectId == original.ObjectId &&
                n.Quarantine.Source.Path == across.Path) &&
            await fx.A.ObserveAsync(across.Path, default) is not null && await fx.B.ObserveAsync("blocked.txt", default) is not null,
            "cross-directory same-identity rename keeps its hold and both copies; the held notice follows the new path");
    }

    private sealed class SuccessfulElevation(Action succeed) : IElevatedAction
    {
        internal int Calls;
        public Task<int> RunAsync(string verb, IReadOnlyList<string> args, CancellationToken ct)
        { Calls++; succeed(); return Task.FromResult(0); }
    }
}
