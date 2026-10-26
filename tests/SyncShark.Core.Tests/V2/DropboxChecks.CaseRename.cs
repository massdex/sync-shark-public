namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task CaseRenameLocalSqliteAsync()
    {
        // Native case-sensitive directories may need a different fixture volume than USN tests.
        // Reuse the local fixture owner (SYNCSHARK_TEST_ROOT/TEMP), not the provider suite root.
        var directory = LocalChecks.FreshRoot("case-local-" + Guid.NewGuid().ToString("N"));
        var local = Path.Combine(directory, "local");
        Directory.CreateDirectory(local);
        await File.WriteAllTextAsync(Path.Combine(local, "Doc.txt"), "content");
        using var h = new Harness();
        using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var root = LocalChecks.RootOf(local, "case-local-root");
        var pair = new PairDef("case-local-pair", "Case local", root, Root(), [], [], true);
        store.SavePair(pair);
        var writer = await LocalWriter.OpenAsync(root, Side.A, pair.PairId, LocalChecks.InstallationId,
            Path.Combine(directory, "recovery"), CancellationToken.None, isStateDamage: StorageFailureClassifier.IsStateDamage, StorageChecks.Fast);
        var original = (await writer.ObserveAsync("Doc.txt", CancellationToken.None))!;
        h.Wire.SeedFile("doc.txt", "content");
        var remote = h.Writer();
        var source = (await remote.ObserveAsync("doc.txt", CancellationToken.None))!;
        var basis = new Baseline(pair.PairId, "case-key", original, source with { Path = "Doc.txt" }, 1);
        var options = new PlanOptions(TimeSpan.FromSeconds(2), false,
            new LabelRule("{name} [{side}]{ext}", "s", TimeSpan.Zero, "A", "B"), 10)
        { ObservedAt = new DateTimeOffset(2025, 1, 2, 0, 0, 0, TimeSpan.Zero) };
        var plan = Planner.Decide(pair.PairId, 2,
            [new PlanRow(basis, original, source, new Evidence(false, false, false, false))], options);
        Require(plan.Actions.Single().Kind == ActionKind.Move, "Dropbox case change plans a local Move");
        var executor = new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
        await foreach (var result in executor.ApplyAsync(plan, writer, remote, CancellationToken.None))
            Require(result.Outcome == Outcome.Done, "native NTFS case rename completes");
        var observed = (await writer.ObserveAsync("doc.txt", CancellationToken.None))!;
        Require(observed.ObjectId == original.ObjectId && Directory.GetFiles(local).Select(Path.GetFileName).Single() == "doc.txt",
            "NTFS rename keeps identity and changes physical spelling");
        var saved = store.Baselines(pair.PairId).Single();
        Require(saved.A.Path == "doc.txt" && saved.B.Path == "doc.txt", "local baseline records final exact case");
        Require(Planner.Decide(pair.PairId, 3,
            [new PlanRow(saved, observed, source, new Evidence(false, false, false, false))], options).Actions.Count == 0,
            "equal spelling is a no-op on the next run");

        File.Move(Path.Combine(local, "doc.txt"), Path.Combine(local, "dOc.txt"));
        File.SetLastWriteTimeUtc(Path.Combine(local, "dOc.txt"), new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var changedLocal = (await writer.ObserveAsync("dOc.txt", CancellationToken.None))!;
        var newerRemote = source with { Mtime = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var conflictBase = saved with { A = changedLocal with { Path = "Doc.txt" }, B = newerRemote with { Path = "Doc.txt" } };
        Require(ModificationTime.IsPlausible(changedLocal.Mtime, options.ObservedAt) &&
            ModificationTime.IsPlausible(newerRemote.Mtime, options.ObservedAt) &&
            Content.SameContent(changedLocal, newerRemote) == true &&
            newerRemote.Mtime - changedLocal.Mtime > options.TieWindow,
            "case-conflict fixture has equal content and plausible non-tied metadata times");
        var tied = Planner.Decide(pair.PairId, 4,
            [new PlanRow(conflictBase with { B = conflictBase.B with { Mtime = changedLocal.Mtime } },
                changedLocal, newerRemote with { Mtime = changedLocal.Mtime }, new Evidence(false, false, false, false))], options);
        Require(tied.Actions.All(action => action.Kind == ActionKind.Hold) && tied.Notices.Count == 1 &&
            tied.Notices.Single().ReasonKey == PlanNotices.NeedDecision.Key,
            "equal metadata timestamps hold under a decision notice, never invent a newest rename");
        Directory.CreateDirectory(Path.Combine(local, "Folder"));
        await File.WriteAllTextAsync(Path.Combine(local, "Folder", "child.txt"), "child");
        var folder = (await writer.ObserveAsync("Folder", CancellationToken.None))!;
        h.Wire.SeedFolder("folder");
        var remoteFolder = (await remote.ObserveAsync("folder", CancellationToken.None))!;
        Require(folder.Kind == ItemKind.Folder && remoteFolder.Kind == ItemKind.Folder &&
            folder.Path == "Folder" && remoteFolder.Path == "folder",
            "folder case rename starts with observed folders and differing exact spelling");
        var folderMove = new SyncAction("folder-case", 25, ActionKind.Move, Side.A, "folder", "Folder",
            remoteFolder, folder, false, "folder-key");
        await foreach (var result in executor.ApplyAsync(new Plan(pair.PairId, 4, [folderMove], []), writer, remote, CancellationToken.None))
            Require(result.Outcome == Outcome.Done,
                $"real NTFS folder case rename completes once: {result.Outcome}; {result.Reason}");
        Require(Directory.GetDirectories(local).Select(Path.GetFileName).Contains("folder") &&
            File.Exists(Path.Combine(local, "folder", "child.txt")), "native folder rename carries child content");
        var renamedFolder = (await writer.ObserveAsync("folder", CancellationToken.None))!;
        Require(renamedFolder.ObjectId == folder.ObjectId, "folder case rename retains native identity");
        Directory.CreateDirectory(Path.Combine(local, "Occupied"));
        await File.WriteAllTextAsync(Path.Combine(local, "Occupied", "kept.txt"), "retained");
        var occupiedFolder = (await writer.ObserveAsync("Occupied", CancellationToken.None))!;
        var occupiedMove = folderMove with
        {
            ActionId = "folder-occupied", FromPath = "folder", Path = "Occupied", ExpectedTarget = renamedFolder
        };
        await foreach (var result in executor.ApplyAsync(new Plan(pair.PairId, 5, [occupiedMove], []), writer, remote, CancellationToken.None))
            Require(result.Outcome == Outcome.Failed, "distinct occupied folder is not replaced");
        Require((await writer.ObserveAsync("Occupied", CancellationToken.None))!.ObjectId == occupiedFolder.ObjectId &&
            await File.ReadAllTextAsync(Path.Combine(local, "Occupied", "kept.txt")) == "retained" &&
            await File.ReadAllTextAsync(Path.Combine(local, "folder", "child.txt")) == "child",
            "refused folder move retains both original trees and occupant identity");
        await CaseSensitiveFolderOccupancyAsync(local, writer);
        var conflict = Planner.Decide(pair.PairId, 4,
            [new PlanRow(conflictBase, changedLocal, newerRemote, new Evidence(false, false, false, false))], options);
        Require(conflict.Actions.Single().Kind == ActionKind.Move && conflict.Actions.Single().Target == Side.A &&
            conflict.Notices.Single().Kind == NoticeKind.AutoConflict,
            "both-sided case conflict chooses newer metadata under the existing conflict notice");
        await foreach (var result in executor.ApplyAsync(conflict, writer, remote, CancellationToken.None))
            Require(result.Outcome == Outcome.Done, "case conflict converges without delete or create");
    }

    private static async Task CaseRenameSqliteAsync()
    {
        Require(DropboxPilotClient.SameMoveSpelling("parent/Doc.txt", "Parent/Doc.txt") &&
            !DropboxPilotClient.SameMoveSpelling("Parent/doc.txt", "Parent/Doc.txt"),
            "Dropbox guarantees exact leaf case, not exact ancestor case");
        await CaseRenameLocalSqliteAsync();
        await CaseOutageColdReopenAsync();
        await CaseConflictColdReopenAsync();
        foreach (var scenario in new[] { "normal", "moved", "deleted", "edited", "occupied" })
        foreach (var stopBeforeLeg in scenario == "normal" ? new[] { 0, 1, 2 } : new[] { 2 })
        {
            using var h = new Harness();
            var directory = Path.Combine(_root, "case-rename-" + scenario + "-" + stopBeforeLeg);
            Directory.CreateDirectory(directory);
            using var store = SqliteStateStore.Open(Path.Combine(directory, "state"), StorageChecks.Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var rootA = new RootDef("case-a", "Fake", "account", "/A", "id-a");
            var pair = new PairDef("case-pair", "Case pair", rootA, Root(), [], [], true);
            store.SavePair(pair);
            var a = new FakeEndpoint(Side.A, rootA.RootId);
            var source = a.Seed("doc.txt", "content");
            h.Wire.SeedFile("Doc.txt", "content");
            var writer = h.Writer();
            var original = (await writer.ObserveAsync("Doc.txt", CancellationToken.None))!;
            var action = new SyncAction("case-intent", 20, ActionKind.Move, Side.B, "doc.txt", "Doc.txt",
                source, original, false, "case-key") { RequireMoveRevision = true };
            store.RecordIntent(pair.PairId, action);
            var previous = ProviderEffectContext.Current.Value;
            var count = 0;
            try
            {
                ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
                {
                    action = action with { ProviderEffect = checkpoint };
                    store.RecordIntent(pair.PairId, action);
                    if (++count == stopBeforeLeg) throw new OperationCanceledException("simulated interruption");
                    return Task.CompletedTask;
                });
                try
                {
                    var result = await writer.MoveAsync(action, CancellationToken.None);
                    Require(stopBeforeLeg == 0 && result.Path == "doc.txt" && result.ObjectId == original.ObjectId,
                        "two provider moves retain identity and reach exact case");
                }
                catch (OperationCanceledException) when (stopBeforeLeg != 0) { }
            }
            finally { ProviderEffectContext.Current.Value = previous; }
            var inventory = new DropboxInventory(Root(), Side.B, h.Client, store, pair.PairId);
            var snapshot = await inventory.FullAsync(1, PairInclude.For(pair, Side.B, [pair], []), Sink, CancellationToken.None);
            Require(snapshot.Items.All(item => !LocalItems.IsArtifactPath(item.Path)),
                "interrupted case rename intermediate never enters inventory");
            if (stopBeforeLeg == 2)
            {
                using var checkpoint = System.Text.Json.JsonDocument.Parse(action.ProviderEffect!.Id);
                var temporary = checkpoint.RootElement.GetProperty("Temporary").GetString()!;
                h.Wire.SeedChangePage("case-delta", [h.Wire.ChangeDeleted("/Pilot/Doc.txt"),
                    h.Wire.ChangeFile(temporary, "content")], false, "case-delta-end");
                var delta = await inventory.ChangesSinceAsync("case-delta", 2,
                    PairInclude.For(pair, Side.B, [pair], []), CancellationToken.None);
                Require(delta.Changes.All(change => !LocalItems.IsArtifactPath(change.Path)),
                    "change feed cannot publish the intermediate as a new item");
                if (scenario == "moved") h.Wire.MoveOutsideRoot(temporary, "/Elsewhere/moved.txt");
                if (scenario == "deleted") h.Wire.DeleteRestoredFile(temporary);
                if (scenario == "edited") h.Wire.EditFile(temporary, "new content");
                if (scenario == "occupied") h.Wire.SeedFile("doc.txt", "other identity");
            }
            var executor = new Executor(store, StorageChecks.Fast, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
            var reconciled = await executor.ReconcileAsync(store.UnresolvedIntents(pair.PairId), 2, a, writer, CancellationToken.None);
            if (scenario is "moved" or "deleted")
            {
                Require(reconciled.Single().Outcome == Outcome.Skipped && store.UnresolvedIntents(pair.PairId).Count == 0,
                    "a user move or deletion retires the case intent for ordinary planning");
                continue;
            }
            if (scenario == "occupied")
            {
                Require(reconciled.Single().Outcome == Outcome.Failed && store.UnresolvedIntents(pair.PairId).Count == 1,
                    "occupied final name keeps a known-not-applied retry after exposing the incoming identity");
                var retained = await h.Client.FindIdentityForMoveAsync(original.ObjectId, CancellationToken.None);
                Require(retained.Entry is not null && !LocalItems.IsArtifactPath(retained.Entry.RelativePath),
                    "the incoming item is never left at its hidden intermediate name");
                Require(store.OpenNotices(pair.PairId).Count == 0,
                    "executor leaves notice ownership to the ordinary run failure owner");
                Require((await writer.ObserveAsync("doc.txt", CancellationToken.None))!.ObjectId != original.ObjectId,
                    "recovery never overwrites the unrelated occupant");
                continue;
            }
            Require(reconciled.Single().Outcome == Outcome.Done && store.UnresolvedIntents(pair.PairId).Count == 0,
                "SQLite checkpoint resumes forward before or after first move");
            var observed = (await writer.ObserveAsync("doc.txt", CancellationToken.None))!;
            Require(observed.Path == "doc.txt" && observed.ObjectId == original.ObjectId,
                "recovery preserves the exact provider identity and final spelling");
            var baseline = store.Baselines(pair.PairId).Single();
            Require(baseline.A.Path == "doc.txt" && baseline.B.Path == "doc.txt", "baseline records converged case");
            Require(h.Wire.Calls.Count(call => call == "/2/files/move_v2") == 2,
                "reconciliation never repeats a completed leg");
        }
    }
}
