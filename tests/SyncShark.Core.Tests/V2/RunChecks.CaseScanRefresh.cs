namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // A refresh scan can be handed a stored path whose case spelling lags a case-only rename. The scanner
    // must resolve the requested components to the on-disk spelling, so the requested entry and the
    // enumerated entry are one object. A genuine collision (two ordinal names, different identities) must
    // stay blocked. These three checks pin both halves of that contract.
    private static async Task CaseScanRefreshAsync()
    {
        await RefreshResolvesOnDiskSpellingAsync();
        await CaseScanRetrySettlesAsync();
        CollidingIdentitiesStayBlocked();
        Console.WriteLine("PASS CaseScanRefresh: stale refresh spelling resolves while genuine collisions stay blocked");
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
            var trace = File.ReadAllText(log.Path!);
            Require(trace.Contains("refresh path case resolved: child -> Child", StringComparison.Ordinal) &&
                trace.Contains("refresh path case resolved: child/file.txt -> Child/file.txt", StringComparison.Ordinal),
                "the refresh trace names each requested path and its on-disk spelling");
        }
        finally { DeleteFixture(folder); }
    }

    private static async Task CaseScanRetrySettlesAsync()
    {
        var folder = FixtureFolder("case-scan-retry");
        try
        {
            var directory = Path.Combine(folder, "root");
            Directory.CreateDirectory(directory);
            LocalChecks.Seed(directory, "child/file.txt", "original bytes");
            using var store = SqliteStateStore.Open(Path.Combine(folder, "store"), StorageChecks.Fast,
                TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            fx.A.DropboxHashes = fx.B.DropboxHashes = true;
            var root = LocalChecks.RootOf(directory, "ra");
            var pair = fx.Pair with { A = root };
            store.SavePair(pair);
            var checkpoint = new NtfsUsnCheckpoint(1, 0, 100, 0);
            var inventory = new LocalInventory(root, Side.A, store, new HashSet<string>(), Settings,
                TimeProvider.System, StorageFailureClassifier.IsStateDamage, pairId: pair.PairId)
            {
                WalkCheckpointForTest = () => (checkpoint, false), JournalReadForTest = NoJournalChanges,
                GapIo = new LocalGapIo(store, Settings, new HashSet<string>(), () => false)
            };
            fx.Provider.AddInventory(root, inventory);
            fx.Provider.AddWriter(root, await LocalWriter.OpenAsync(root, Side.A, pair.PairId,
                LocalChecks.InstallationId, Path.Combine(folder, "recovery"), default,
                isStateDamage: StorageFailureClassifier.IsStateDamage));
            // The first sync records the pre-rename spelling on both sides and in the baselines.
            await FirstSyncAsync(fx.Run(), fx);
            Require(store.Baselines(pair.PairId).Any(baseline => baseline.A.Kind == ItemKind.Folder &&
                    baseline.A.Path == "child" && baseline.B.Path == "child"),
                "the folder baseline holds the original lower-case spelling on both sides");
            // A case-only rename needs two moves so Windows never treats the target as an existing name.
            Directory.Move(Path.Combine(directory, "child"), Path.Combine(directory, "child-temp"));
            Directory.Move(Path.Combine(directory, "child-temp"), Path.Combine(directory, "Child"));
            LocalChecks.Seed(directory, "Child/file.txt", "edited after the case-only rename");
            const string collision = "A case- or Unicode-normalization-colliding local name is not accepted.";
            // The durable shape the incident left behind: one scan retry per stored spelling in the subtree.
            foreach (var scope in new[] { "child", "child/file.txt" })
                store.SaveSyncRetry(pair.PairId, new SyncRetry(scope, "scan", 1, collision,
                    DateTimeOffset.MinValue, ReadRoots: [root.RootId]));
            var report = await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
            Require(store.SyncRetryAt(pair.PairId, "child") is null &&
                store.SyncRetryAt(pair.PairId, "child/file.txt") is null &&
                !store.ReadSyncRetries(pair.PairId).Any(retry => retry.LastError == collision),
                "a successful targeted reread retires the stale-case scan retries");
            var observed = store.Live(root.RootId, ["child/file.txt"]).Single();
            Require(observed.Path == "Child/file.txt" && observed.Size == LocalChecks.Bytes("edited after the case-only rename").LongLength,
                "the refresh observes the edited file under its on-disk spelling");
            var choice = store.OpenNotices(pair.PairId)
                .Any(notice => notice.Rule == 20 && notice.Kind == NoticeKind.UserDecision);
            Require(report.Failed == 0 && (report.Moved >= 1 || choice),
                "the one-sided folder case rename produces a baseline Move or a visible resolvable choice, never a silent block");
            Require(!store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "sync-retry" &&
                    (PairPath.Same(notice.Path, "child") || PairPath.Same(notice.Path, "child/file.txt"))),
                "no collision notice remains for the resolved refresh");
            if (report.Moved >= 1)
            {
                // The baseline rename is an ordinary Move; a follow-up run carries the edit to the remote spelling.
                await fx.Run().IncrementalAsync(pair.PairId, new RecordingProgress(), default);
                var remote = await fx.B.FullAsync(0, _ => true, SilentProgress, CancellationToken.None);
                var remotePaths = remote.Items.Select(item => item.Path)
                    .OrderBy(path => path, StringComparer.Ordinal).ToArray();
                Require(remotePaths.SequenceEqual(["Child", "Child/file.txt"]) &&
                    remote.Items.Single(item => item.Path == "Child/file.txt").Hash ==
                        LocalChecks.BlockHash(LocalChecks.Bytes("edited after the case-only rename")),
                    "the edited bytes reach the remote under the on-disk spelling only");
            }
            else
            {
                // Both baselines already differ: the spelling is a visible choice, never an automatic effect.
                Require(choice && store.OpenNotices(pair.PairId).Any(notice => notice.Rule == 20 &&
                        notice.Kind == NoticeKind.UserDecision && notice.NoticeId is not null),
                    "a both-baselines case mismatch stays a visible resolvable spelling choice");
            }
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
            decisions.All(decision => decision.Reason ==
                "A case- or Unicode-normalization-colliding local name is not accepted.") &&
            blocked.Contains(PilotPath.CanonicalComparisonKey("Doc.txt")) &&
            LocalScanEntries.PublishEntries(entries).Count == 0,
            "two distinct identities at one canonical key remain blocked and unpublished");
    }
}
