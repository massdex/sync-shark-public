using System.Globalization;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // A refresh scan can be handed a stored path whose case spelling lags a case-only rename. The scanner
    // must resolve the requested components to the on-disk spelling, so the requested entry and the
    // enumerated entry are one object. A genuine collision (two ordinal names, different identities) must
    // stay blocked, and a case-sensitive parent must never answer one request from its case variant's
    // listing. These checks pin every half of that contract.
    private static async Task CaseScanRefreshAsync()
    {
        await RefreshResolvesOnDiskSpellingAsync();
        await CaseSensitiveRefreshIsBlockedNotBorrowedAsync();
        await MissingOrUnlistableRefreshParentAsync();
        await SinglePathRefreshKeepsPriorBehaviourAsync();
        CollidingIdentitiesStayBlocked();
        await OneSidedFolderCaseMoveAsync();
        await DivergentFolderBaselineChoiceAsync();
        Console.WriteLine("PASS CaseScanRefresh: stale refresh spelling resolves while genuine collisions stay blocked");
    }

    private static string CollisionReason =>
        "A case- or Unicode-normalization-colliding local name is not accepted.";

    // One real local root wired through SyncRun to the fake remote, matching the incident's shape.
    private static async Task<Fixture> LocalCaseFixtureAsync(string folder, SqliteStateStore store,
        string localRootId = "ra")
    {
        var directory = Path.Combine(folder, "root");
        Directory.CreateDirectory(directory);
        var fx = Build(suppliedStore: store);
        fx.A.DropboxHashes = fx.B.DropboxHashes = true;
        var root = LocalChecks.RootOf(directory, localRootId);
        var pair = fx.Pair with { A = root };
        store.SavePair(pair);
        var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
            TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
        {
            WalkCheckpointForTest = () => (new NtfsUsnCheckpoint(1, 0, 100, 0), false),
            JournalReadForTest = NoJournalChanges,
            GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => false)
        };
        fx.Provider.AddInventory(root, inventory);
        fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
            LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
            isStateDamage: StorageFailureClassifier.IsStateDamage));
        return fx;
    }

    private static async Task RefreshResolvesOnDiskSpellingAsync()
    {
        var folder = FixtureFolder("case-scan-refresh");
        try
        {
            var root = Path.Combine(folder, "root");
            Directory.CreateDirectory(root);
            LocalChecks.Seed(root, "Child/file.txt", "edited bytes");
            using var lease = PilotDirectoryLease.Open(root);
            var log = RunLog.Open(Path.Combine(folder, "trace"), "Parent", "scan", verbose: false);
            PilotLocalScan scan;
            using (log)
                scan = await new PilotLocalScanner { MetadataOnly = true }.ScanAsync(lease,
                    refreshPaths: ["child", "child/file.txt"], includePath: _ => true);
            await log.Completion;
            var spellings = scan.Entries.Values.Select(entry => entry.RelativePath)
                .OrderBy(path => path, StringComparer.Ordinal).ToArray();
            Require(spellings.SequenceEqual(["Child", "Child/file.txt"]),
                "a stale refresh spelling publishes the on-disk spelling for the folder and its child");
            Require(scan.Entries.TryGetValue("child", out var folderEntry) &&
                folderEntry is { Kind: PilotEntryKind.Directory, RelativePath: "Child" },
                "the requested case-only renamed folder keeps its on-disk spelling and identity");
            Require(scan.Decisions.Count == 0,
                "the stale requested spelling is one entry, never a collision decision");
            var template = MessageCatalog.Default.Template("refresh-case-resolved");
            var trace = File.ReadAllText(log.Path!);
            Require(trace.Contains(string.Format(CultureInfo.InvariantCulture, template, "child", "Child"),
                    StringComparison.Ordinal) &&
                trace.Contains(string.Format(CultureInfo.InvariantCulture, template, "child/file.txt",
                    "Child/file.txt"), StringComparison.Ordinal),
                "the catalog template renders each requested path and its on-disk spelling into the trace");
        }
        finally { DeleteFixture(folder); }
    }

    // A case-sensitive directory can hold "Foo" and "foo" as two objects. A refresh for either (or for
    // children of both) must not reuse one folder's listing for the other: that publishes a borrowed
    // identity or admits a real child as absent, which can reach LocalDeletes as a delete candidate.
    private static async Task CaseSensitiveRefreshIsBlockedNotBorrowedAsync()
    {
        var folder = FixtureFolder("case-scan-sensitive");
        try
        {
            var root = Path.Combine(folder, "root");
            Directory.CreateDirectory(root);
            LocalChecks.EnableCaseSensitiveDirectory(root);
            LocalChecks.Seed(root, "Foo/b.txt", "upper bytes");
            LocalChecks.Seed(root, "foo/c.txt", "lower bytes");
            using var lease = PilotDirectoryLease.Open(root);
            var scanner = new PilotLocalScanner { MetadataOnly = true };
            // Requesting both folders cannot choose one case variant's identity for the other.
            var both = await scanner.ScanAsync(lease, refreshPaths: ["Foo", "foo"], includePath: _ => true);
            Require(both.Entries.Count == 0 && both.Decisions.Count(decision => decision.Kind == PilotDecisionKind.Blocked) == 2 &&
                both.Decisions.All(decision => decision.Reason == CollisionReason),
                "two ordinal folders at one canonical key are two collision decisions, never a borrowed entry");
            // The false-omission shape: one folder's listing must not answer for its case variant, so a
            // requested child that exists is never admitted absent.
            var children = await scanner.ScanAsync(lease,
                refreshPaths: ["Foo/b.txt", "foo/c.txt"], includePath: _ => true);
            Require(children.Entries.Count == 0 && children.Decisions.Count(decision => decision.Kind == PilotDecisionKind.Blocked) == 2 &&
                children.Decisions.All(decision => decision.Reason == CollisionReason) &&
                !children.Decisions.Any(decision => decision.QuarantineCode is not null),
                "a case-ambiguous refresh blocks both paths and never publishes or quarantines an entry");

            // The same refresh through LocalInventory: a stored child in one spelling must not become a delete.
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var pair = new PairDef("p1", "p1",
                new RootDef("ra", SyncProviders.Local, null, root, LocalChecks.ObjectIdOf(root)),
                new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"), [], [], true);
            store.SavePair(pair);
            var inventory = new LocalInventory(pair.A, Side.A, store, new HashSet<string>(), Settings,
                TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId);
            // Seed the durable row under its requested spelling, as an earlier scan or baseline would.
            var seeded = await inventory.ChangesForPathsAsync(["foo/c.txt"], "",
                store.BeginGeneration(pair.A.RootId, "seed"), _ => true, default);
            Require(seeded.Changes.Count(change => change.Kind == ChangeKind.Upsert) == 1,
                "setup: the case-sensitive child is observed under its requested spelling");
            store.WriteDelta(seeded);
            var delta = await inventory.ChangesForPathsAsync(["Foo/b.txt", "foo/c.txt"], "",
                store.BeginGeneration(pair.A.RootId, "ambiguous"), _ => true, default);
            Require(!delta.Changes.Any(change => change.Kind == ChangeKind.Delete),
                "a case-ambiguous refresh never turns a real child into an absent/delete candidate");
            Require(!delta.Changes.Any(change => change.Kind == ChangeKind.Upsert),
                "a case-ambiguous refresh publishes no entry under either spelling");
            Require(delta.IncompleteSubtrees.Any(path => PairPath.Same(path, "Foo/b.txt")) &&
                delta.IncompleteSubtrees.Any(path => PairPath.Same(path, "foo/c.txt")),
                "both ambiguous paths stay incomplete, so the retry and notice owners keep them");
            Require(store.Live(pair.A.RootId, ["foo/c.txt"]).SingleOrDefault()?.Path == "foo/c.txt",
                "the stored row under the requested spelling survives the ambiguous refresh");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task MissingOrUnlistableRefreshParentAsync()
    {
        var folder = FixtureFolder("case-scan-parents");
        try
        {
            var root = Path.Combine(folder, "root");
            Directory.CreateDirectory(root);
            LocalChecks.Seed(root, "present.txt", "present bytes");
            File.WriteAllText(Path.Combine(root, "blocked"), "a file, not a folder");
            using var lease = PilotDirectoryLease.Open(root);
            var scan = await new PilotLocalScanner { MetadataOnly = true }.ScanAsync(lease,
                refreshPaths: ["missing/child.txt", "blocked/child.txt", "present.txt"], includePath: _ => true);
            Require(scan.Complete && scan.Entries.TryGetValue("present.txt", out var present) &&
                present.Kind == PilotEntryKind.File,
                "an absent or unlistable refresh parent never aborts the whole walk");
            Require(!scan.Entries.Keys.Any(path => path.EndsWith("child.txt", StringComparison.Ordinal)),
                "no child is invented under an absent or unlistable parent");
            Require(scan.Decisions.Count(decision => decision.RelativePath == "blocked/child.txt" &&
                    decision.Kind == PilotDecisionKind.Blocked) == 1,
                "a parent that is a file stays one per-item scan decision");
            Require(!scan.Decisions.Any(decision => decision.RelativePath == "missing/child.txt"),
                "an absent parent keeps the ordinary absence behaviour, not a collision decision");
            Require(!scan.Decisions.Any(decision => decision.Reason == CollisionReason),
                "an absent or unlistable parent is never reported as a spelling collision");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task SinglePathRefreshKeepsPriorBehaviourAsync()
    {
        var folder = FixtureFolder("case-scan-single");
        try
        {
            var root = Path.Combine(folder, "root");
            Directory.CreateDirectory(root);
            LocalChecks.Seed(root, "Child/file.txt", "single bytes");
            using var lease = PilotDirectoryLease.Open(root);
            var scanner = new PilotLocalScanner { MetadataOnly = true };
            var full = await scanner.ScanAsync(lease, includePath: _ => true);
            var expected = full.Entries["Child/file.txt"].FileId;
            // Resolution is intentionally off for one requested path: keep the prior requested spelling
            // and the matching identity, and never invent a collision block.
            var single = await scanner.ScanAsync(lease, refreshPaths: ["child/file.txt"], includePath: _ => true);
            Require(single.Entries.TryGetValue("child/file.txt", out var entry) && entry.FileId == expected &&
                single.Decisions.Count == 0,
                "a single-path refresh keeps its requested spelling and identity with no collision block");
        }
        finally { DeleteFixture(folder); }
    }

    private static void CollidingIdentitiesStayBlocked()
    {
        var entries = new Dictionary<string, PilotLocalEntry>(StringComparer.Ordinal);
        var blocked = new HashSet<string>(StringComparer.Ordinal);
        var decisions = new List<PilotDecision>();
        var write = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        // Two ordinal names in one canonical key with different identities are genuinely different objects.
        var first = new PilotLocalEntry("Doc.txt", PilotEntryKind.File, "Doc.txt", 1, write, null, 1, 11);
        var second = first with { RelativePath = "doc.txt", FullPath = "doc.txt", FileId = 12 };
        LocalScanEntries.AddEntry(entries, blocked, decisions, first);
        LocalScanEntries.AddEntry(entries, blocked, decisions, second);
        Require(decisions.Count(decision => decision.Kind == PilotDecisionKind.Blocked) == 2 &&
            decisions.All(decision => decision.Reason == CollisionReason) &&
            blocked.Contains(PilotPath.CanonicalComparisonKey("Doc.txt")) &&
            LocalScanEntries.PublishEntries(entries).Count == 0,
            "two distinct identities at one canonical key remain blocked and unpublished");
    }

    // The baselines agree on the pre-rename spelling on both sides and only the local disk is re-spelled.
    // That is one baseline-relative folder rename: rule 25 moves the remote folder, and the edited child
    // follows it. No choice is involved and no entry may appear twice.
    private static async Task OneSidedFolderCaseMoveAsync()
    {
        var folder = FixtureFolder("case-scan-move");
        try
        {
            var directory = Path.Combine(folder, "root");
            Directory.CreateDirectory(directory);
            LocalChecks.Seed(directory, "child/file.txt", "original bytes");
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = await LocalCaseFixtureAsync(folder, store);
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var pairId = fx.Pair.PairId;
            Require(store.Baselines(pairId).Any(baseline => baseline.A.Kind == ItemKind.Folder &&
                    baseline.A.Path == "child" && baseline.B.Path == "child"),
                "the pre-rename folder baseline holds the same lower-case spelling on both sides");
            // A case-only rename needs two moves so Windows never treats the target as an existing name.
            Directory.Move(Path.Combine(directory, "child"), Path.Combine(directory, "child-temp"));
            Directory.Move(Path.Combine(directory, "child-temp"), Path.Combine(directory, "Child"));
            LocalChecks.Seed(directory, "Child/file.txt", "edited after the case-only rename");
            // The incident's durable shape: stored scopes under mixed subtree spellings, each with the collision error.
            foreach (var scope in new[] { "child", "Child/file.txt" })
                store.SaveSyncRetry(pairId, new SyncRetry(scope, "scan", 1, CollisionReason,
                    DateTimeOffset.MinValue, ReadRoots: [fx.Pair.A.RootId]));
            fx.B.AppliedActions.Clear();
            var first = await run.IncrementalAsync(pairId, new RecordingProgress(), default);
            Require(first.Failed == 0 && first.Deleted == 0 && first.KeptBoth == 0 && first.Moved == 1,
                $"the one-sided folder case rename is one remote Move, never a delete or keep-both: {first}");
            Require(store.SyncRetryAt(pairId, "child") is null && store.SyncRetryAt(pairId, "Child/file.txt") is null &&
                !store.ReadSyncRetries(pairId).Any(retry => retry.LastError == CollisionReason),
                "a successful targeted reread retires the stale-case scan retries");
            var moves = fx.B.AppliedActions.Where(action => action.Kind == ActionKind.Move).ToArray();
            Require(moves.Length == 1 && moves[0] is { Rule: 25, Target: Side.B, FromPath: "child", Path: "Child" },
                "the baseline-relative folder rename is exactly one rule-25 remote folder Move child->Child");
            // A follow-up run carries the edited bytes after the move; later runs must settle.
            var followUps = new List<RunReport>();
            for (var turn = 0; turn < 3; turn++)
                followUps.Add(await run.IncrementalAsync(pairId, new RecordingProgress(), default));
            Require(followUps.All(report => report.Moved == 0 && report.Failed == 0 &&
                    report.Deleted == 0 && report.KeptBoth == 0),
                "no second folder Move, delete or keep-both once the baseline rename is settled");
            Require(followUps[^1].Uploaded + followUps[^1].Downloaded == 0,
                "the follow-up runs settle with no further content effect");
            Require(first.Uploaded + first.Downloaded +
                    followUps.Sum(report => report.Uploaded + report.Downloaded) == 1,
                "the edited child propagates exactly once");
            var remote = await fx.B.FullAsync(0, _ => true, SilentProgress, CancellationToken.None);
            var remotePaths = remote.Items.Select(item => item.Path).ToArray();
            Require(remotePaths.Length == 2 && remotePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
                remotePaths.Select(path => path.ToLowerInvariant()).OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(["child", "child/file.txt"]),
                $"the remote holds exactly one folder and one child with no duplicate entries: {string.Join(", ", remotePaths)}");
            Require((await fx.B.ObserveAsync("Child", default)) is { Kind: ItemKind.Folder, Path: "Child" } &&
                (await fx.B.ObserveAsync("Child/file.txt", default))?.Hash ==
                    LocalChecks.BlockHash(LocalChecks.Bytes("edited after the case-only rename")),
                "the folder keeps its identity under the on-disk spelling and the edited bytes follow it");
            Require(store.Live(fx.Pair.A.RootId, ["Child/file.txt"]).SingleOrDefault()?.Path == "Child/file.txt" &&
                store.Live(fx.Pair.A.RootId, ["Child"]).SingleOrDefault()?.Path == "Child",
                "the local rows adopt the on-disk spelling after the rename");
        }
        finally { DeleteFixture(folder); }
    }

    // Both sides already match their own baselines with different spellings (a pair adopted while both
    // folders existed). The scan must surface the frozen rule-20 spelling choice, and resolving it through
    // the ordinary decision path must perform one folder Move and leave the children converged.
    private static async Task DivergentFolderBaselineChoiceAsync()
    {
        var folder = FixtureFolder("case-scan-choice");
        try
        {
            var directory = Path.Combine(folder, "root");
            Directory.CreateDirectory(directory);
            LocalChecks.Seed(directory, "Child/file.txt", "shared bytes");
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = await LocalCaseFixtureAsync(folder, store);
            fx.B.Seed("child", "", ItemKind.Folder);
            fx.B.Seed("child/file.txt", "shared bytes");
            var run = fx.Run();
            await FirstSyncAsync(run, fx);
            var pairId = fx.Pair.PairId;
            var folderBaseline = store.Baselines(pairId).Single(baseline => baseline.A.Kind == ItemKind.Folder);
            Require(folderBaseline.A.Path == "Child" && folderBaseline.B.Path == "child",
                "a pair adopted with both spellings present keeps Child (A) and child (B) as its baseline");
            var remoteChild = await fx.B.ObserveAsync("child/file.txt", default);
            // Mixed stored subtree spellings plus the collision scan retries from the incident.
            foreach (var scope in new[] { "child", "Child/file.txt" })
                store.SaveSyncRetry(pairId, new SyncRetry(scope, "scan", 1, CollisionReason,
                    DateTimeOffset.MinValue, ReadRoots: [fx.Pair.A.RootId]));
            fx.B.AppliedActions.Clear();
            var held = await run.IncrementalAsync(pairId, new RecordingProgress(), default);
            Require(held.Failed == 0 && held.Moved == 0 && held.Uploaded + held.Downloaded + held.Deleted == 0 &&
                fx.B.AppliedActions.Count == 0,
                $"a both-baselines spelling mismatch performs no effect before the choice: {held}");
            Require(store.SyncRetryAt(pairId, "child") is null && store.SyncRetryAt(pairId, "Child/file.txt") is null,
                "the resolved refresh retires the collision scan retries even while the spelling choice is open");
            var notice = store.OpenNotices(pairId)
                .SingleOrDefault(open => open.Rule == 20 && open.Kind == NoticeKind.UserDecision);
            Require(notice is not null && PairPath.Same(notice.Path, "Child"),
                "the folder spelling mismatch is the visible rule-20 user decision");
            var spelling = notice!;
            var snapshot = (await run.ObserveDecisionsAsync(pairId, [(spelling.NoticeId!, spelling.Path)], default)).Single();
            var decision = await run.ResolveDecisionAsync(pairId, spelling.Path, snapshot.Token, Side.A,
                new RecordingProgress(), default);
            Require(decision.Failed == 0 && !store.OpenNotices(pairId).Any(open => open.NoticeId == spelling.NoticeId),
                "choosing the local spelling closes the rule-20 notice");
            var moves = fx.B.AppliedActions.Where(action => action.Kind == ActionKind.Move).ToArray();
            Require(moves.Length == 1 && moves[0] is { Rule: 25, Target: Side.B, FromPath: "child", Path: "Child" } &&
                fx.B.AppliedActions.All(action => action.Kind == ActionKind.Move),
                "the spelling choice performs exactly one rule-25 folder Move and no child effects");
            var remote = await fx.B.FullAsync(0, _ => true, SilentProgress, CancellationToken.None);
            var remotePaths = remote.Items.Select(item => item.Path).ToArray();
            Require(remotePaths.Length == 2 && remotePaths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2 &&
                remotePaths.Select(path => path.ToLowerInvariant()).OrderBy(path => path, StringComparer.Ordinal)
                    .SequenceEqual(["child", "child/file.txt"]),
                $"after the choice the remote holds exactly one folder and one child: {string.Join(", ", remotePaths)}");
            Require((await fx.B.ObserveAsync("Child", default)) is { Kind: ItemKind.Folder, Path: "Child" } &&
                (await fx.B.ObserveAsync("Child/file.txt", default)) is { } movedChild &&
                movedChild.ObjectId == remoteChild!.ObjectId && movedChild.Hash == remoteChild!.Hash,
                "the children converge under the chosen spelling with their identities and content unchanged");
        }
        finally { DeleteFixture(folder); }
    }
}
