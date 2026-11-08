using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using Rig = CloudPairHostBindingChecks.HostRig;
using Spec = CloudPairHostBindingChecks.EndpointSpec;

// Exact-database-read checks for the bounded planning read lane.
//
// A: the real common-content digest resolver reads one exact retained path through the new best-scoped
// retained-membership lookup. A real isolated SQLite authority holds 1025 distinct paths on each side, the
// observation runs through the production cloud observation component over scripted listing stores, and the
// proof measures the rows the exact lookup materializes (at most two per before/after read, never the whole
// side), that the one-entry decision still equals the whole-side load for every path, that a duplicate
// membership for one path is refused, that a new complete observation cut during the body read is refused,
// and that the provider, algorithm, native hash, version, size, root and account mismatches are refused.
//
// B: the shared bounded planning memo cache. FIFO entry and byte bounds, an oversized item never retained,
// cached and evicted decisions identical across more than 128 keys, and concurrent readers/adders/clearers.
//
// C: the existing final mutable planning authority guard still refuses stale input, on the real
// LoadPlanningObservationSetAsync and IsPlanningAuthorityCurrentAsync entries that read the memos.
//
// D: the recovery projection page boundary across the real Host caller: public page sizes 1/499/500 with
// 0/500/501 retained rows, invalid 0/501 refused, offset/search/pair isolation and the hasMore sentinel.
//
// Fixtures supply listing and byte input only; no fixture replaces the decision under proof. No live account,
// credential, network, installed product state or schema change is reachable from this file.
internal static class BoundedPlanningReadChecks
{
    private const string InstallationId = "installation-bounded-planning-read";
    private const string ProfileId = "bounded-planning-read-profile";
    private const string SourceRevision = "bounded-planning-read-1";
    private const int PathCount = 1025;
    private const int PageArtifactCount = 500;
    private const int TotalArtifactCount = PageArtifactCount + 1;
    private const string PagePathSearch = "page-";
    private const string AllPathSearch = ".bin";
    private const string AbsentPathSearch = "bounded-absent-path";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated bounded planning read fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        var fixture = await BuildPlanningFixtureAsync(Path.Combine(_root, "planning-read"));
        await CaseAsync("A-path-scoped-planning-read",
            "The exact retained-membership lookup materializes at most two rows per before/after read and the resolver still decides every retained identity",
            () => PathScopedReadAsync(fixture));
        await CaseAsync("B-planning-memo-budget",
            "The shared planning memo cache is bounded by entries and bytes, never retains an oversized item and recomputes identical decisions after eviction",
            PlanningMemoAsync);
        await CaseAsync("C-planning-authority-guard",
            "The final mutable planning authority guard still refuses a stale observation cut and accepts the current one",
            async () => await PlanningAuthorityGuardAsync(await BuildPlanningFixtureAsync(Path.Combine(_root, "planning-authority"))));
        await CaseAsync("D-recovery-page-boundary",
            "The real Host recovery catalog page accepts page size 500 through the 501-row internal sentinel, refuses 0/501 and keeps offset, search and pair scope exact",
            RecoveryPageBoundaryAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "BoundedPlanningReadChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "bounded-planning-read-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS bounded planning reads: " + _assertions + " assertions; one exact retained path" +
            " materializes at most two rows per read over real SQLite authority, duplicate, scan-change," +
            " provider, algorithm, version, size, root and account mismatches refuse, the planning memo cache" +
            " is bounded and recomputes identically, the final planning authority guard refuses stale input," +
            " and the real Host recovery page crosses the 500/501 boundary without widening the public limit.");
    }

    private static async Task CaseAsync(string id, string label, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + ": " + label + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["label"] = label, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("Bounded planning read assertion failed: " + label);
    }

    // ----- A: the exact path-scoped planning read ------------------------------------------------

    private sealed class PlanningFixture
    {
        internal SyncPair Pair = null!;
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCloudEndpointSet Endpoints = null!;
        internal ScriptedStore LeftStore = null!;
        internal ScriptedStore RightStore = null!;
        internal string LeftRoot = "";
        internal string RightRoot = "";
        internal string LocalScanId = "";
        internal string DropboxScanId = "";
        internal byte[] Content = [];
        internal string NativeHash = "";
        internal string LeftPath = "";
        internal Dictionary<string, byte[]> ContentByPath = new(StringComparer.Ordinal);

        internal SyncAuthorityCloudCommonDigestResolver Resolver() => new(Db, Endpoints);

        internal byte[] OtherContent = [];

        internal async Task<SyncAuthorityObservationResult> ObserveAsync(string trigger)
        {
            await using var connections = Db.BeginConnectionReuseScope();
            var coordinator = new SyncAuthorityObservationCoordinator(Pair, LeftStore.Store, Db, LeftRoot,
                RightRoot, onInventory: null, cloudEndpoints: Endpoints);
            if (!coordinator.UsesCloudEndpointRouting)
                throw new InvalidDataException("The scripted cloud endpoint set did not route the observation.");
            var result = await coordinator.ObserveAsync(trigger);
            LocalScanId = result.LocalScan.ScanGenerationId;
            DropboxScanId = result.DropboxScan.ScanGenerationId;
            return result;
        }
    }

    private static async Task<PlanningFixture> BuildPlanningFixtureAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var fixture = new PlanningFixture();
        var leftSpec = CloudDropbox("p-left", "id:bounded-planning-left");
        var rightSpec = CloudDrive("p-right", "gd-bounded-planning-right");
        fixture.LeftRoot = "root-bounded-planning-left";
        fixture.RightRoot = "root-bounded-planning-right";
        fixture.Content = Pattern(2048);
        fixture.OtherContent = Pattern(2049);
        fixture.NativeHash = DropboxContentHash.Compute(fixture.Content);
        fixture.Db = new SyncAuthorityDatabase(Path.Combine(directory, "syncshark.db"));
        await fixture.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
        await fixture.Db.RegisterProviderAccountAsync(leftSpec.ProviderAccountId, InstallationId,
            leftSpec.AccountId, leftSpec.RootNamespaceId, leftSpec.HomeNamespaceId, "Bounded left account", 1,
            cancellationToken: default, provider: leftSpec.Provider);
        await fixture.Db.RegisterProviderAccountAsync(rightSpec.ProviderAccountId, InstallationId,
            rightSpec.AccountId, rightSpec.RootNamespaceId, rightSpec.HomeNamespaceId, "Bounded right account", 1,
            cancellationToken: default, provider: rightSpec.Provider);
        var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
            CloudEndpointFor(leftSpec, leftSpec.FolderId), CloudEndpointFor(rightSpec, rightSpec.FolderId),
            SyncMode.TwoWay), "Bounded planning read");
        var normalized = SyncPairStore.NormalizePair(draft);
        await fixture.Db.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
        await fixture.Db.RetainPairDefinitionAsync(normalized, true);
        fixture.Pair = normalized;
        await fixture.Db.RegisterCloudEndpointRootBindingAsync(fixture.LeftRoot, normalized.Id,
            SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId, leftSpec.Provider, leftSpec.ProviderAccountId,
            leftSpec.EndpointNamespaceId, leftSpec.FolderId, leftSpec.FolderPath, cancellationToken: default,
            hierarchies: new[] { CorpusChain(leftSpec, fixture.LeftRoot) });
        await fixture.Db.RegisterCloudEndpointRootBindingAsync(fixture.RightRoot, normalized.Id,
            SyncAuthorityCloudSides.LogicalRight, 1, InstallationId, rightSpec.Provider,
            rightSpec.ProviderAccountId, rightSpec.EndpointNamespaceId, rightSpec.FolderId, rightSpec.FolderPath,
            cancellationToken: default, hierarchies: Array.Empty<SyncAuthorityCloudHierarchySnapshot>());

        var leftEntries = new List<PilotRemoteEntry>();
        var rightEntries = new List<PilotRemoteEntry>();
        for (var index = 0; index < PathCount; index++)
        {
            var path = "path-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".bin";
            var bytes = index % 2 == 0 ? fixture.Content : fixture.OtherContent;
            fixture.ContentByPath[path] = bytes;
            leftEntries.Add(new PilotRemoteEntry(path, PilotEntryKind.File, "id:" + path, "rev-" + path,
                bytes.Length, DropboxContentHash.Compute(bytes)));
            rightEntries.Add(new PilotRemoteEntry(path, PilotEntryKind.File, "gd-" + path, "gd-rev-" + path,
                bytes.Length, Sha256Hex(bytes)));
        }
        fixture.LeftPath = leftEntries[PathCount / 2].RelativePath;
        fixture.LeftStore = ScriptedRangeStoreFor(leftEntries, fixture.ContentByPath);
        fixture.RightStore = ScriptedListStoreFor(rightEntries);
        fixture.Endpoints = SyncAuthorityCloudEndpointSet.FromPair(normalized, fixture.LeftStore.Store,
            fixture.RightStore.Store, fixture.LeftRoot, fixture.RightRoot, leftSpec.ProviderAccountId,
            rightSpec.ProviderAccountId);
        await fixture.ObserveAsync("manual"); // bounded-planning-read
        return fixture;
    }

    private static async Task PathScopedReadAsync(PlanningFixture fixture)
    {
        var full = await fixture.Db.LoadLatestCompleteObservationAsync(fixture.Pair.Id,
            SyncAuthorityCloudSides.LogicalLeft);
        Assert(full.Count == PathCount && full.Count > 1024,
            "the retained left inventory of the fixture is larger than 1024 paths; observed " + full.Count);

        var wholeSide = full.ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        var materialized = new List<int>();
        SyncAuthorityDatabase.PlanningPathReadForTest = (_, _, _, rows) => materialized.Add(rows);
        try
        {
            var bounded = 0;
            var matches = 0;
            foreach (var entry in full)
            {
                var exact = await fixture.Db.LoadLatestCompleteObservationAtPathAsync(fixture.Pair.Id,
                    SyncAuthorityCloudSides.LogicalLeft, entry.RelativePath);
                Assert(exact.Count <= SyncAuthorityDatabase.LatestCompleteObservationAtPathMaximumRows,
                    "the exact lookup materialized at most two rows for " + entry.RelativePath + "; observed " +
                    exact.Count);
                Assert(exact.Count == 1,
                    "exactly one retained membership exists for " + entry.RelativePath + "; observed " +
                    exact.Count);
                var match = exact[0];
                var expected = wholeSide[entry.RelativePath];
                Assert(match == expected,
                    "the exact lookup returns the identical retained membership the whole-side load returns for " +
                    entry.RelativePath);
                matches++;
                bounded = Math.Max(bounded, exact.Count);
            }
            Assert(matches == PathCount, "every one of the 1025 paths was read exactly; observed " + matches);
            Assert(bounded == 1, "every exact lookup returned its single membership; largest was " + bounded);
            Assert(materialized.Count == PathCount,
                "one exact retained read ran for each path; observed " + materialized.Count);
            Assert(materialized.All(rows => rows <= SyncAuthorityDatabase.LatestCompleteObservationAtPathMaximumRows),
                "no exact retained read materialized more than two rows");
            Assert(materialized.Sum() == PathCount,
                "the exact reads materialized exactly one row each; measured " + materialized.Sum());

            // The real resolver over the same authority: two exact lookups (before and after the body read)
            // must each stay inside the two-row bound instead of materializing the 1025-row side.
            materialized.Clear();
            var left = wholeSide[fixture.LeftPath];
            var resolver = fixture.Resolver();
            var digest = await resolver.ResolveAsync(new SyncAuthorityCommonDigestRequest(
                SyncAuthorityCloudCommonDigestResolver.LeftSide, left.RelativePath, left.RootBindingId,
                left.ObjectVersionId, left.ObjectKind, left.Size ?? 0, SyncContentIdentity.DropboxBlockSha256,
                left.ContentHash ?? ""));
            Assert(digest is not null, "the resolver returned a verified digest through the exact path read");
            Assert(digest!.ByteCount == left.Size && digest.Size == left.Size &&
                    digest.NativeHash == left.ContentHash &&
                    digest.Sha256 == Sha256Hex(fixture.Content) &&
                    digest.ObjectVersionId == left.ObjectVersionId &&
                    digest.RootBindingId == left.RootBindingId,
                "the resolved digest binds the exact retained identity, size and whole-file SHA-256");
            Assert(fixture.LeftStore.RangeCalls == 1,
                "exactly one exact range read delivered the bytes; observed " + fixture.LeftStore.RangeCalls);
            Assert(materialized.Count == 2,
                "the resolver performed exactly the before and after exact lookup; observed " +
                materialized.Count);
            Assert(materialized.All(rows => rows <= SyncAuthorityDatabase
                        .LatestCompleteObservationAtPathMaximumRows) && materialized.Sum() <= 4,
                "the resolver materialized at most two rows per exact lookup and at most four in total");
            Assert(materialized.Sum() < PathCount,
                "the resolver never materialized the whole retained side for one path");

            var plan = await fixture.Db.ExplainLatestCompleteObservationAtPathAsync(fixture.Pair.Id,
                SyncAuthorityCloudSides.LogicalLeft, left.RelativePath);
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = "A-plan-capture", ["label"] = "EXPLAIN QUERY PLAN of the exact path-scoped lookup",
                ["result"] = string.IsNullOrWhiteSpace(plan) ? "UNAVAILABLE" : "CAPTURED",
                ["query_plan"] = plan, ["paths"] = PathCount, ["rows_materialized_per_read"] = 1
            });
            Console.WriteLine("PLAN exact path-scoped lookup: " +
                (string.IsNullOrWhiteSpace(plan) ? "<unavailable>" : plan.Trim().Replace('\n', ';')));

            // The request refusals are unchanged by the path-scoped read.
            var valid = new SyncAuthorityCommonDigestRequest(
                SyncAuthorityCloudCommonDigestResolver.LeftSide, left.RelativePath, left.RootBindingId,
                left.ObjectVersionId, left.ObjectKind, left.Size ?? 0, SyncContentIdentity.DropboxBlockSha256,
                left.ContentHash ?? "");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                RelativePath = "path-absent.bin"
            }), "a path that is not retained refuses");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                NativeAlgorithm = SyncContentIdentity.Sha256, NativeHash = Sha256Hex(fixture.Content)
            }), "a provider algorithm mismatch refuses");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                NativeHash = Sha256Hex(fixture.Content)
            }), "a native hash that is not the retained one refuses");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                ObjectVersionId = "version-not-retained"
            }), "an object version that is not retained refuses");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                Size = (left.Size ?? 0) + 1
            }), "a retained size mismatch refuses");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                RootBindingId = fixture.RightRoot
            }), "another root binding refuses");
            await AssertNullDigestAsync(() => fixture.Resolver().ResolveAsync(valid with
            {
                Side = SyncAuthorityCloudSides.LogicalLeft
            }), "the stored compatibility tag used as a request side refuses");
            await AssertNullDigestAsync(() => new SyncAuthorityCloudCommonDigestResolver(fixture.Db,
                    SyncAuthorityCloudEndpointSet.FromPair(fixture.Pair, fixture.LeftStore.Store,
                        fixture.RightStore.Store, fixture.LeftRoot, fixture.RightRoot, "foreign-account",
                        fixture.Endpoints.Right.ProviderAccountId))
                .ResolveAsync(valid), "another retained provider account refuses");
            Assert(fixture.LeftStore.RangeCalls == 1,
                "every refused request happened before any provider body read");

            // A new complete observation cut during the body read invalidates the retained memberships.
            var duringBody = new List<int>();
            SyncAuthorityDatabase.PlanningPathReadForTest = (_, _, _, rows) => duringBody.Add(rows);
            var changedPath = full[0].RelativePath;
            var changed = wholeSide[changedPath];
            fixture.LeftStore.OnRange = () => fixture.ObserveAsync("manual"); // bounded-scan-change
            try
            {
                var result = await fixture.Resolver().ResolveAsync(new SyncAuthorityCommonDigestRequest(
                    SyncAuthorityCloudCommonDigestResolver.LeftSide, changed.RelativePath,
                    changed.RootBindingId, changed.ObjectVersionId, changed.ObjectKind, changed.Size ?? 0,
                    SyncContentIdentity.DropboxBlockSha256, changed.ContentHash ?? ""));
                Assert(result is null,
                    "a scan that changes during the body read leaves the resolver with no verified digest");
                Assert(fixture.LeftStore.RangeCalls == 2,
                    "the body read for the changed cut really ran; observed " + fixture.LeftStore.RangeCalls);
                Assert(duringBody.Count == 2 && duringBody.All(rows => rows <= 2),
                    "the changed-cut read still materialized at most two rows per exact lookup");
            }
            finally
            {
                fixture.LeftStore.OnRange = null;
            }

            // Two memberships for one exact path stay visible as a duplicate and refuse.
            var duplicatePath = full[1].RelativePath;
            await AppendDuplicateMembershipAsync(fixture, duplicatePath);
            var duplicate = await fixture.Db.LoadLatestCompleteObservationAtPathAsync(fixture.Pair.Id,
                SyncAuthorityCloudSides.LogicalLeft, duplicatePath);
            Assert(duplicate.Count == SyncAuthorityDatabase.LatestCompleteObservationAtPathMaximumRows,
                "the exact lookup reads both memberships of a duplicated path; observed " + duplicate.Count);
            var duplicateEntry = full[1];
            Assert(await fixture.Resolver().ResolveAsync(new SyncAuthorityCommonDigestRequest(
                    SyncAuthorityCloudCommonDigestResolver.LeftSide, duplicatePath,
                    duplicateEntry.RootBindingId, duplicateEntry.ObjectVersionId, duplicateEntry.ObjectKind,
                    duplicateEntry.Size ?? 0, SyncContentIdentity.DropboxBlockSha256,
                    duplicateEntry.ContentHash ?? "")) is null,
                "a duplicate membership for one path is refused rather than resolved to the first row");
        }
        finally
        {
            SyncAuthorityDatabase.PlanningPathReadForTest = null;
        }
    }

    /// <summary>
    /// Retains a second membership for one exact path inside the current complete observation cut. The
    /// production observation entry point derives one membership identity from the path, so this duplicate
    /// state cannot be produced through it; the fixture therefore supplies the extra rows directly and the
    /// decision under proof stays the resolver's own duplicate refusal.
    /// </summary>
    private static async Task AppendDuplicateMembershipAsync(PlanningFixture fixture, string relativePath)
    {
        await using var connection = new SqliteConnection("Data Source=" + fixture.Db.DatabasePath);
        await connection.OpenAsync();
        string scanId;
        string rootBindingId;
        long scanGeneration;
        string objectVersionId;
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT g.scan_generation_id,g.root_binding_id,g.generation,o.object_version_id
                FROM scan_generation g
                JOIN effective_observation o ON o.scan_generation_id=g.scan_generation_id
                JOIN membership m ON m.membership_id=o.membership_id
                WHERE g.pair_id=$pair AND g.side='local' AND g.status='complete'
                  AND m.relative_path_key=$path
                ORDER BY g.generation DESC,m.generation DESC LIMIT 1;
                """;
            read.Parameters.AddWithValue("$pair", fixture.Pair.Id);
            read.Parameters.AddWithValue("$path", relativePath);
            await using var reader = await read.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                throw new InvalidDataException("The duplicate-path fixture found no retained membership.");
            scanId = reader.GetString(0);
            rootBindingId = reader.GetString(1);
            scanGeneration = reader.GetInt64(2);
            objectVersionId = reader.GetString(3);
        }
        await using (var write = connection.CreateCommand())
        {
            write.CommandText = """
                INSERT INTO membership(membership_id,object_id,root_binding_id,relative_path_key,generation,
                    state,observed_utc)
                SELECT $membership,m.object_id,m.root_binding_id,m.relative_path_key,
                       (SELECT max(s.generation) + 1 FROM membership s
                        WHERE s.root_binding_id=m.root_binding_id AND s.relative_path_key=m.relative_path_key),
                       'active',$utc
                FROM membership m WHERE m.membership_id=(
                    SELECT membership_id FROM effective_observation WHERE observation_id=(
                        SELECT o.observation_id FROM effective_observation o
                        JOIN membership x ON x.membership_id=o.membership_id
                        WHERE o.scan_generation_id=$scan AND x.relative_path_key=$path LIMIT 1));
                """;
            write.Parameters.AddWithValue("$membership", "bounded-duplicate-membership");
            write.Parameters.AddWithValue("$scan", scanId);
            write.Parameters.AddWithValue("$path", relativePath);
            write.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var written = await write.ExecuteNonQueryAsync();
            Assert(written == 1, "the duplicate-path fixture retained exactly one extra membership");
        }
        await using (var observe = connection.CreateCommand())
        {
            observe.CommandText = """
                INSERT INTO observation(observation_id,scan_generation_id,root_binding_id,scan_generation,
                    complete_generation,object_version_id,membership_id,evidence_json,observed_utc)
                VALUES($observation,$scan,$root,$generation,1,$version,$membership,$evidence,$utc);
                """;
            observe.Parameters.AddWithValue("$observation", "bounded-duplicate-observation");
            observe.Parameters.AddWithValue("$scan", scanId);
            observe.Parameters.AddWithValue("$root", rootBindingId);
            observe.Parameters.AddWithValue("$generation", scanGeneration);
            observe.Parameters.AddWithValue("$version", objectVersionId);
            observe.Parameters.AddWithValue("$membership", "bounded-duplicate-membership");
            observe.Parameters.AddWithValue("$evidence", "{\"state\":\"bounded-duplicate\"}");
            observe.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            var observed = await observe.ExecuteNonQueryAsync();
            Assert(observed == 1, "the duplicate-path fixture retained exactly one extra observation");
        }
    }

    // ----- B: the shared bounded planning memo cache ---------------------------------------------

    private static async Task PlanningMemoAsync()
    {
        var contentWeight = SyncPlanningMemoWeights.ContentFingerprint;
        var defaultBytes = SyncPlanningMemoBudget.MaximumBytes;

        // The documented weight formula, computed independently of the implementation.
        Assert(contentWeight("abc", "defgh") == 128 + 2 * 3 + 2 * 5,
            "the content-fingerprint weight charges UTF-16 key and value plus the per-entry overhead");
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "aaa", "bbbb" };
        Assert(SyncPlanningMemoWeights.ExcludedPaths(("local-scan", "remote-scan"), excluded) ==
                128 + 2 * 10 + 2 * 11 + (2 * 3 + 64) + (2 * 4 + 64),
            "the excluded-path weight charges both keys, every set member and the set member overhead");
        Assert(SyncPlanningMemoWeights.PlanningState(
                ("local-scan", "remote-scan", "pair", 7L, "local-root", "remote-root", "active"),
                "fingerprint") ==
                128 + 8 + 2 * (10 + 11 + 4 + 10 + 11 + 6) + 2 * 11,
            "the planning-state weight charges every string field, the numeric member and the value");

        var entries = new SyncPlanningMemoCache<string, string>(contentWeight, 3, defaultBytes);
        for (var index = 0; index < 5; index++)
        {
            var key = "key-" + index.ToString(CultureInfo.InvariantCulture);
            Assert(entries.TryAdd(key, "value-" + index.ToString(CultureInfo.InvariantCulture)),
                "entry " + index + " is admitted inside both bounds");
        }
        Assert(entries.Count == 3, "the entry bound retains exactly three entries; observed " + entries.Count);
        Assert(!entries.TryGetValue("key-0", out _) && !entries.TryGetValue("key-1", out _),
            "the two longest-retained entries were evicted first (FIFO)");
        Assert(entries.TryGetValue("key-4", out var newest) && newest == "value-4",
            "the newest entry is retained with its exact value");

        var byBudget = new SyncPlanningMemoCache<string, string>(contentWeight, 128, 256);
        for (var index = 0; index < 5; index++)
            byBudget.TryAdd("budget-" + index.ToString(CultureInfo.InvariantCulture), "v");
        Assert(byBudget.RetainedBytes <= byBudget.MaximumBytes,
            "the byte budget is never exceeded; observed " + byBudget.RetainedBytes);
        Assert(byBudget.Count == 1,
            "two entries that together exceed the byte budget leave exactly one retained; observed " +
            byBudget.Count);

        var oversized = new SyncPlanningMemoCache<string, string>(contentWeight, 128, 256);
        var oversizedKey = new string('k', 200);
        Assert(!oversized.TryAdd(oversizedKey, "v"),
            "an item heavier than the whole byte budget is never retained");
        Assert(oversized.Count == 0 && oversized.RetainedBytes == 0,
            "the oversized item left no retained entry and no charged bytes");
        Assert(oversized.TryAdd("small", "v"), "an in-budget item is retained normally");
        oversized[oversizedKey] = "v";
        Assert(!oversized.TryGetValue(oversizedKey, out _) && oversized.Count == 1,
            "an oversized replacement is refused and never displaces the retained entry");

        var recomputed = new SyncPlanningMemoCache<string, string>(contentWeight);
        var admitted = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < 300; index++)
        {
            admitted[KeyFor(index)] = ValueFor(index);
            recomputed.TryAdd(KeyFor(index), ValueFor(index));
        }
        Assert(recomputed.Count == SyncPlanningMemoBudget.MaximumEntries,
            "the default entry bound retains exactly " + SyncPlanningMemoBudget.MaximumEntries +
            "; observed " + recomputed.Count);
        var hits = 0;
        var misses = 0;
        for (var index = 0; index < 300; index++)
        {
            var key = KeyFor(index);
            var hit = recomputed.TryGetValue(key, out var cached);
            var decided = hit ? cached : ValueFor(index);
            if (hit) hits++; else misses++;
            Assert(decided == admitted[key],
                "the cached or recomputed decision equals the originally admitted value for " + key);
        }
        Assert(hits > 0 && misses > 0,
            "both the cached and the evicted decision paths were exercised; hits " + hits + ", misses " + misses);
        Assert(hits + misses == 300, "every one of the 300 keys decided exactly once");

        var replaced = new SyncPlanningMemoCache<string, string>(contentWeight);
        replaced.TryAdd("replace", "first");
        var beforeReplace = replaced.RetainedBytes;
        replaced["replace"] = "second";
        Assert(replaced.Count == 1 && replaced.TryGetValue("replace", out var replacement) &&
                replacement == "second",
            "replacing an existing key keeps one entry with the new value");
        Assert(replaced.RetainedBytes > beforeReplace,
            "the replacement recharges its own exact weight");

        var concurrent = new SyncPlanningMemoCache<string, string>(contentWeight, 128, 4096);
        var tasks = new List<Task>();
        for (var worker = 0; worker < 8; worker++)
        {
            var id = worker;
            tasks.Add(Task.Run(() =>
            {
                for (var step = 0; step < 400; step++)
                {
                    var key = "worker-" + id.ToString(CultureInfo.InvariantCulture) + "-" + step;
                    concurrent.TryAdd(key, ValueFor(step));
                    concurrent.TryGetValue(key, out _);
                    concurrent[key] = ValueFor(step + 1);
                    if (step % 97 == 0) concurrent.Clear();
                }
            }));
        }
        await Task.WhenAll(tasks);
        Assert(concurrent.Count <= concurrent.MaximumEntries,
            "concurrent admission/eviction never exceeded the entry bound; observed " + concurrent.Count);
        Assert(concurrent.RetainedBytes <= concurrent.MaximumBytes,
            "concurrent admission/eviction never exceeded the byte bound; observed " +
            concurrent.RetainedBytes);

        var exclusions = new SyncPlanningMemoCache<(string Local, string Remote), IReadOnlySet<string>>(
            SyncPlanningMemoWeights.ExcludedPaths, 128, SyncPlanningMemoBudget.MaximumBytes);
        var large = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < 200000; index++)
            large.Add("excluded/nested/folder-" + index.ToString("D6", CultureInfo.InvariantCulture) + ".bin");
        var largeWeight = SyncPlanningMemoWeights.ExcludedPaths(("local-cut", "remote-cut"), large);
        Assert(largeWeight > SyncPlanningMemoBudget.MaximumBytes,
            "the large exclusion set is genuinely above the byte budget; measured " + largeWeight);
        Assert(!exclusions.TryAdd(("local-cut", "remote-cut"), large),
            "a large exclusion set above the budget is never retained");
        Assert(exclusions.Count == 0 && exclusions.RetainedBytes == 0,
            "the oversized exclusion set charged no bytes and retained nothing");
        var small = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "excluded/one.bin" };
        Assert(exclusions.TryAdd(("local-cut", "remote-cut"), small),
            "an in-budget exclusion set is retained normally");
        Assert(exclusions.Count == 1 && exclusions.RetainedBytes <= SyncPlanningMemoBudget.MaximumBytes,
            "the retained exclusion memo stays inside its own byte budget");
    }

    private static string KeyFor(int index) =>
        "generation-key-" + index.ToString("D4", CultureInfo.InvariantCulture);

    private static string ValueFor(int index) =>
        "fingerprint-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(KeyFor(index))))
            .ToLowerInvariant();

    // ----- C: the final mutable planning authority guard ------------------------------------------

    private static async Task PlanningAuthorityGuardAsync(PlanningFixture fixture)
    {
        var set = await LoadObservationSetAsync(fixture, fixture.LocalScanId, fixture.DropboxScanId);
        var authority = set.Bind("observation-set-bounded-planning-read");
        Assert(await fixture.Db.IsPlanningAuthorityCurrentAsync(authority),
            "the current mutable planning authority is accepted as input");

        var repeat = await LoadObservationSetAsync(fixture, fixture.LocalScanId, fixture.DropboxScanId);
        Assert(repeat.ContentFingerprint == set.ContentFingerprint &&
                repeat.PlanningStateFingerprint == set.PlanningStateFingerprint &&
                repeat.SynchronizationPointSetFingerprint == set.SynchronizationPointSetFingerprint &&
                repeat.RootAuthorityFingerprint == set.RootAuthorityFingerprint,
            "the memo-backed planning fingerprints are identical whether recomputed or read from the memo");

        await fixture.ObserveAsync("manual"); // bounded-authority-guard
        Assert(!await fixture.Db.IsPlanningAuthorityCurrentAsync(authority),
            "the final mutable planning authority guard refuses the superseded observation cut");
        var refreshed = await LoadObservationSetAsync(fixture, fixture.LocalScanId, fixture.DropboxScanId);
        Assert(await fixture.Db.IsPlanningAuthorityCurrentAsync(
                refreshed.Bind("observation-set-bounded-planning-read-2")),
            "the fresh observation cut is accepted again, so eviction and recomputation changed no decision");
    }

    private static async Task<SyncAuthorityPairObservationSet> LoadObservationSetAsync(PlanningFixture fixture,
        string localScanId, string dropboxScanId) =>
        await fixture.Db.LoadPlanningObservationSetAsync(fixture.Pair.Id, fixture.Pair.PolicyRevision,
            fixture.LeftRoot, fixture.RightRoot, localScanId, dropboxScanId);

    // ----- D: the recovery projection page boundary ----------------------------------------------

    private static async Task RecoveryPageBoundaryAsync()
    {
        var hostRoot = Path.Combine(_root, "recovery-page");
        Directory.CreateDirectory(hostRoot);
        CloudPairHostBindingChecks.UseFixtureRoot(hostRoot);
        using var rig = await Rig.CreateAsync("recovery-page-boundary");
        var left = CloudPairHostBindingChecks.HostDropbox("bpage-l", "/Bounded page left");
        var right = CloudPairHostBindingChecks.HostDrive("bpage-r", "/Bounded page right");
        await rig.AddEndpointAsync(left);
        await rig.AddEndpointAsync(right);
        var hostPair = await rig.AddPairAsync("bpage", left, right);
        var otherLeft = CloudPairHostBindingChecks.HostDropbox("bpage-ol", "/Bounded other left");
        var otherRight = CloudPairHostBindingChecks.HostDrive("bpage-or", "/Bounded other right");
        await rig.AddEndpointAsync(otherLeft);
        await rig.AddEndpointAsync(otherRight);
        var otherPair = await rig.AddPairAsync("bpage-other", otherLeft, otherRight);
        await rig.Db.InitializeAsync(rig.InstallationId, ProfileId, SourceRevision, "{}");
        var hostCommand = await OpenCommandAsync(rig, hostPair.Pair);
        var otherCommand = await OpenCommandAsync(rig, otherPair.Pair);

        var pairIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < TotalArtifactCount; index++)
        {
            var relativePath = index < PageArtifactCount
                ? "page-" + index.ToString("D4", CultureInfo.InvariantCulture) + ".bin"
                : "sentinel-tail.bin";
            pairIds.Add(await RegisterArtifactAsync(rig, hostPair.Pair, hostCommand, relativePath));
        }
        var otherIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 3; index++)
            otherIds.Add(await RegisterArtifactAsync(rig, otherPair.Pair, otherCommand, "other-" + index + ".bin"));
        Assert(pairIds.Count == TotalArtifactCount && otherIds.Count == 3,
            "the fixture retained 501 artifacts for the main pair and 3 for the other pair");

        var requestsBefore = rig.Wire.TotalRequests;
        var empty = await HostPageAsync(rig, hostPair.Pair.Id, 500, 0, AbsentPathSearch);
        var exact500 = await HostPageAsync(rig, hostPair.Pair.Id, 500, 0, PagePathSearch);
        var one = await HostPageAsync(rig, hostPair.Pair.Id, 1, 0, PagePathSearch);
        var exact499 = await HostPageAsync(rig, hostPair.Pair.Id, 499, 0, PagePathSearch);
        var all501 = await HostPageAsync(rig, hostPair.Pair.Id, 500, 0, AllPathSearch);
        var second = await HostPageAsync(rig, hostPair.Pair.Id, 500, all501.NextArchiveOffset, AllPathSearch);
        var offset1 = await HostPageAsync(rig, hostPair.Pair.Id, 499, 2, AllPathSearch);
        var offset2 = await HostPageAsync(rig, hostPair.Pair.Id, 498, 2, PagePathSearch);
        var other = await HostPageAsync(rig, otherPair.Pair.Id, 500, 0, AllPathSearch);

        Assert(rig.Wire.TotalRequests == requestsBefore,
            "every recovery catalog page served no provider transport request");

        Assert(empty.Items.Count == 0 && !empty.HasMoreArchives && empty.NextArchiveOffset == 0,
            "a page over zero matching rows returns no item and no continuation");
        Assert(one.Items.Count == 1 && one.HasMoreArchives && one.NextArchiveOffset == 1,
            "page size 1 over 500 rows returns one item and continues");
        Assert(exact499.Items.Count == 499 && exact499.HasMoreArchives && exact499.NextArchiveOffset == 499,
            "page size 499 over 500 rows returns 499 items and continues");
        Assert(exact500.Items.Count == 500 && !exact500.HasMoreArchives &&
                exact500.NextArchiveOffset == 500,
            "page size 500 over exactly 500 rows returns all 500 with no continuation; observed " +
            exact500.Items.Count);
        Assert(all501.Items.Count == 500 && all501.HasMoreArchives && all501.NextArchiveOffset == 500,
            "page size 500 over 501 rows returns 500 items and reports the sentinel continuation");
        Assert(second.Items.Count == 1 && !second.HasMoreArchives && second.NextArchiveOffset == 501,
            "the continuation page returns the last row and stops");
        Assert(offset1.Items.Count == 499 && !offset1.HasMoreArchives && offset1.NextArchiveOffset == 501,
            "an offset page returns the remaining rows of the same search without a false continuation");
        Assert(offset2.Items.Count == 498 && !offset2.HasMoreArchives && offset2.NextArchiveOffset == 500,
            "a later offset page stays inside the same pair and search scope");
        Assert(other.Items.Count == 3 && !other.HasMoreArchives &&
                other.Items.All(item => otherIds.Contains(item.Key)),
            "the other pair's page contains only its own retained artifacts");
        Assert(one.Items.Concat(exact499.Items).Concat(all501.Items).Concat(second.Items)
                .Concat(other.Items).All(item => !otherIds.Contains(item.Key) || item.PairId == otherPair.Pair.Id),
            "no page ever borrows another pair's artifact identity");
        var paged = all501.Items.Concat(second.Items).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        Assert(paged.SetEquals(pairIds),
            "the two full pages cover every retained artifact exactly once");
        Assert(one.Items[0].PairId == hostPair.Pair.Id && empty.Warnings is null && !empty.HasMoreDeleted,
            "the page reports the requesting pair and no deleted-item continuation");

        await RefusedAsync(() => HostPageAsync(rig, hostPair.Pair.Id, 0, 0, null),
            "the public Host caller refuses page size 0");
        await RefusedAsync(() => HostPageAsync(rig, hostPair.Pair.Id, 501, 0, null),
            "the public Host caller refuses page size 501");
        await RefusedAsync(() => HostPageAsync(rig, hostPair.Pair.Id, -1, 0, null),
            "the public Host caller refuses a negative page size");
        await RefusedAsync(() => rig.Db.LoadRecoveryArtifactsPageAsync(hostPair.Pair.Id, 502, 0, null),
            "the internal recovery projection refuses 502 rows");
        await RefusedAsync(() => rig.Db.LoadRecoveryArtifactsPageAsync(hostPair.Pair.Id, 0, 0, null),
            "the internal recovery projection refuses an empty page");
        var sentinel = await rig.Db.LoadRecoveryArtifactsPageAsync(hostPair.Pair.Id, 501, 0, null);
        Assert(sentinel.Count == TotalArtifactCount,
            "the internal recovery projection accepts exactly the 501-row sentinel read; observed " +
            sentinel.Count);
        var publicMaximum = await rig.Db.LoadRecoveryArtifactsPageAsync(hostPair.Pair.Id, 500, 0, null);
        Assert(publicMaximum.Count == 500 && publicMaximum.All(item => pairIds.Contains(item.RecoveryArtifactId)),
            "the public maximum of 500 rows is preserved and pair-scoped");
        var unsearched = await rig.Db.LoadRecoveryArtifactsPageAsync(hostPair.Pair.Id, 500, 0, "page-");
        Assert(unsearched.Count == PageArtifactCount,
            "database-side search selects exactly the 500 matching rows");
        var isolated = await rig.Db.LoadRecoveryArtifactsPageAsync("bounded-absent-pair", 500, 0, null);
        Assert(isolated.Count == 0, "the recovery projection leaks no artifact across pair authority");
    }

    private static async Task<SyncRecoveryCatalogPage> HostPageAsync(Rig rig, string pairId, int pageSize,
        int offset, string? search) =>
        await rig.Host.LoadRecoveryCatalogPageAsync(pairId, archiveOffset: offset, deletedCursor: null,
            includeDropboxDeletedItems: false, pageSize: pageSize, search: search);

    private static async Task<string> OpenCommandAsync(Rig rig, SyncPair pair)
    {
        // The retained command id deliberately avoids the "page-" discriminator the search cases use, so the
        // search proof cannot be satisfied by the fixture's own bookkeeping text.
        var admission = await rig.Db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await rig.Db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        return admission.CommandRequestId;
    }

    private static async Task<string> RegisterArtifactAsync(Rig rig, SyncPair pair, string commandId, string relativePath)
    {
        var suffix = ShortId(pair.Id + "/" + relativePath);
        var id = "bounded-copy-" + suffix;
        var copy = new SyncArchiveResult("bounded-file-" + suffix, "bounded-revision-" + suffix,
            "retained-" + suffix + ".bin", 17,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair.Id + "/" + relativePath)))
                .ToLowerInvariant());
        await rig.Db.RegisterManualRecoveryCopyAsync(id, commandId, pair.Id,
            pair.PolicyRevision, ProfileId, relativePath, copy, CancellationToken.None);
        return id;
    }

    private static string ShortId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..16];

    // ----- scripted provider input ---------------------------------------------------------------

    private interface IScriptedRangeContract : ISyncRemoteStore, ISyncRemoteRangeStore
    {
    }

    public class ScriptedStore : DispatchProxy
    {
        internal IReadOnlyList<PilotRemoteEntry> Entries { get; set; } = [];
        internal Dictionary<string, byte[]> ContentByPath { get; set; } = new(StringComparer.Ordinal);
        internal Func<Task>? OnRange { get; set; }
        internal int VerifyCalls;
        internal int ListCalls;
        internal int RangeCalls;

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => List(),
            nameof(ISyncRemoteRangeStore.DownloadRangeAsync) => Download(arguments),
            _ => throw new NotSupportedException(
                "Unexpected provider effect in a bounded planning read fixture: " + method?.Name)
        };

        private object Verify()
        {
            VerifyCalls++;
            return Task.CompletedTask;
        }

        private object List()
        {
            ListCalls++;
            return Task.FromResult(Entries);
        }

        private object Download(object?[]? arguments)
        {
            RangeCalls++;
            var expected = (PilotRemoteEntry)arguments![0]!;
            var offset = (long)arguments[1]!;
            var length = (long)arguments[2]!;
            var destination = (Stream)arguments[3]!;
            OnRange?.Invoke().GetAwaiter().GetResult();
            var content = ServiceContent(expected);
            var slice = new byte[length];
            Array.Copy(content, offset, slice, 0, Math.Min(length, Math.Max(0, content.Length - offset)));
            destination.Write(slice, 0, slice.Length);
            return Task.FromResult(new SyncRangeDownloadResult(expected.Id!, expected.Revision!,
                expected.Size, DropboxContentHash.Compute(content), offset, length));
        }

        private byte[] ServiceContent(PilotRemoteEntry expected) =>
            ContentByPath.TryGetValue(expected.RelativePath, out var content)
                ? content
                : throw new InvalidDataException(
                    "The bounded planning read fixture has no bytes for " + expected.RelativePath + ".");
    }

    private static ScriptedStore ScriptedRangeStoreFor(IReadOnlyList<PilotRemoteEntry> entries,
        Dictionary<string, byte[]> contentByPath)
    {
        var store = (ScriptedStore)(object)DispatchProxy.Create<IScriptedRangeContract, ScriptedStore>();
        store.Entries = entries;
        store.ContentByPath = contentByPath;
        return store;
    }

    private static ScriptedStore ScriptedListStoreFor(IReadOnlyList<PilotRemoteEntry> entries)
    {
        var store = (ScriptedStore)(object)DispatchProxy.Create<ISyncRemoteStore, ScriptedStore>();
        store.Entries = entries;
        return store;
    }

    // ----- shared helpers ------------------------------------------------------------------------

    // F2: production ResolveAsync deliberately returns null for unavailable, unretained or mismatched
    // identity instead of throwing, so a refused resolver request is asserted by its typed null result.
    private static async Task AssertNullDigestAsync(Func<Task<SyncAuthorityCommonDigest?>> resolve,
        string label)
    {
        var digest = await resolve();
        Assert(digest is null, label + ": the resolver returns no digest for a refused request");
    }

    private static async Task RefusedAsync(Func<Task> action, string label)
    {
        try
        {
            await action();
        }
        catch (ArgumentOutOfRangeException)
        {
            Assert(true, label);
            return;
        }
        throw new InvalidDataException("Bounded planning read refusal failed: " + label);
    }

    private static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var index = 0; index < length; index++)
            bytes[index] = (byte)((index * 31 + 7) % 251);
        return bytes;
    }

    private static string Sha256Hex(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private sealed record CloudSpec(SyncEndpointKind Kind, string Provider, string ConnectionId,
        string AccountId, string RootNamespaceId, string HomeNamespaceId, string EndpointNamespaceId,
        string ProviderAccountId, string FolderId, string FolderPath);

    private static CloudSpec CloudDropbox(string index, string folderId) => new(
        SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "conn-d" + index, "dbid-" + index,
        "ns-root-" + index, "ns-home-" + index, "ns-root-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, "dbid-" + index,
            "ns-root-" + index, "ns-home-" + index),
        folderId, "/Bounded Dropbox " + index);

    private static CloudSpec CloudDrive(string index, string folderId) => new(
        SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "conn-g" + index, "perm-" + index,
        "key-" + index, "key-" + index, "perm-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, "perm-" + index,
            "key-" + index, "key-" + index),
        folderId, "/Bounded Drive " + index);

    private static SyncEndpoint CloudEndpointFor(CloudSpec spec, string folderId) =>
        SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId, spec.EndpointNamespaceId, folderId, spec.FolderPath);

    private static SyncAuthorityCloudHierarchySnapshot CorpusChain(CloudSpec spec, string rootBindingId) =>
        new(spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, rootBindingId, spec.FolderId,
            new[] { CorpusRoot(spec), spec.FolderId })
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = CorpusRoot(spec),
            ChainNodeKeys = new[]
            {
                NodeKeysFor(spec.Provider, CorpusRoot(spec)),
                NodeKeysFor(spec.Provider, spec.FolderId)
            },
            ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(spec.Provider)
        };

    private static SyncAuthorityCloudHierarchyNodeKeys NodeKeysFor(string provider, string folderId) =>
        new(folderId, new[] { SyncCloudObjectKey.Native(provider, folderId) });

    private static string CorpusRoot(CloudSpec spec) =>
        SyncEndpointProviders.Get(spec.Kind).PathsDescribeAncestry
            ? PilotPath.DropboxNamespaceRootIdentity(spec.RootNamespaceId)
            : "corpus-" + spec.FolderId;
}

