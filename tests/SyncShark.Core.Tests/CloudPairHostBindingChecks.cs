using System.Collections.Concurrent;
using System.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using IOPath = System.IO.Path;
using Massdex.SyncShark;

// CC4A retained-authority snapshot proofs: the narrow internal read-only typed snapshot the Host uses to
// resolve EVERY retained root and recovery boundary through that root's own bound account and the
// provider-native hierarchy producer, with the existing transactional registration rechecking the full
// retained set and unknown, unreachable, missing or stale evidence failing closed.
//
// Everything exercised here is shipped production code: the authority database, the typed cloud root
// projection, the provider-account identity derivation, the one shared cloud root comparison, the real
// CloudEndpointRootBindingAsync transaction and the real DropboxPilotClient adapter. Only the provider
// HTTP transport is a synthetic in-process handler with synthetic tokens; no credential, network, live
// account, installed state or product data is reached.
internal static class CloudPairHostBindingChecks
{
    private const string InstallationId = "installation-cloud-pair-host-binding-316";
    private const string SourceRevision = "cloud-pair-host-binding-316";
    private const string ProfileId = "profile-cloud-pair-host-binding-316";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    /// <summary>
    /// Test-only seam for the CC4B journey fixture: it lets that fixture reuse this file's real Host harness on
    /// its own isolated root. It changes no production behaviour and no assertion of this route.
    /// </summary>
    internal static void UseFixtureRoot(string root) => _root = IOPath.GetFullPath(root);

    internal static async Task RunCloudRewindRefusalChecksAsync(string root)
    {
        UseFixtureRoot(root);
        var cases = new[]
        {
            ("dropbox-dropbox", HostDropbox("rewind-dd-left", "/Rewind DD Left"),
                HostDropbox("rewind-dd-right", "/Rewind DD Right")),
            ("dropbox-drive", HostDropbox("rewind-dg-left", "/Rewind DG Left"),
                HostDrive("rewind-dg-right", "/Rewind DG Right")),
            ("drive-dropbox", HostDrive("rewind-gd-left", "/Rewind GD Left"),
                HostDropbox("rewind-gd-right", "/Rewind GD Right")),
            ("drive-drive", HostDrive("rewind-gg-left", "/Rewind GG Left"),
                HostDrive("rewind-gg-right", "/Rewind GG Right"))
        };
        foreach (var (name, left, right) in cases)
        {
            using var rig = await HostRig.CreateAsync("cloud-rewind-" + name);
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            var retained = await rig.AddPairAsync("cloud-rewind-" + name, left, right);
            var prepared = await PrepareBothSidesAsync(rig, retained);
            await rig.Db.RetainPairDefinitionAsync(prepared.Pair with { Enabled = true }, false);
            var pairsBefore = JsonSerializer.Serialize(await rig.Host.LoadPairsAsync());
            var runtimeBefore = JsonSerializer.Serialize(new SyncRuntimeStateStore(rig.StateAuthority).Load());
            var authorityBefore = await rig.AuthoritySnapshotAsync();
            var commandsBefore = await rig.CountAsync("SELECT count(*) FROM command_request;");
            var operationsBefore = await rig.CountAsync("SELECT count(*) FROM operation;");
            var requestsBefore = rig.Wire.TotalRequests;
            var writesBefore = rig.Wire.Writes;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                string? refusal = null;
                try { await rig.Host.PrepareRewindAsync(retained.Id); }
                catch (NotSupportedException error) { refusal = error.Message; }
                Assert(refusal?.Contains("local folder paired with Dropbox", StringComparison.Ordinal) == true &&
                       refusal.Contains("not paused or changed", StringComparison.Ordinal),
                    "M14/F3 " + name + ": public Rewind rejects the bound cloud pair with exact guidance");
                Assert(rig.Wire.TotalRequests == requestsBefore && rig.Wire.Writes == writesBefore &&
                       await rig.CountAsync("SELECT count(*) FROM command_request;") == commandsBefore &&
                       await rig.CountAsync("SELECT count(*) FROM operation;") == operationsBefore,
                    "M14/F3 " + name + ": refusal creates no command, operation or provider request");
                Assert(!rig.Host.HasActiveRun && rig.Host.ActiveRun is null &&
                       (await rig.Host.LoadRewindSessionsAsync()).Count == 0 &&
                       JsonSerializer.Serialize(new SyncRuntimeStateStore(rig.StateAuthority).Load()) == runtimeBefore &&
                       JsonSerializer.Serialize(await rig.Host.LoadPairsAsync()) == pairsBefore &&
                       (await rig.AuthoritySnapshotAsync()).SequenceEqual(authorityBefore),
                    "M14/F3 " + name + ": repeat refusal preserves pair enablement, runtime and checkpoint state");
            }
            Console.WriteLine("PASS M14/F3 public cloud Rewind refusal and repeat no-effect: " + name);
        }
    }

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud pair host binding fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("A-retained-authority-snapshot-complete", RetainedSnapshotCompleteAsync);
        await CaseAsync("B-inactive-root-excluded-and-read-is-read-only", InactiveRootExcludedAsync);
        await CaseAsync("C-host-resolves-each-retained-root-through-its-own-account",
            PerRetainedRootResolutionAsync);
        await CaseAsync("D-caller-supplied-ancestry-is-never-trusted", CallerAncestryRefusedAsync);
        await CaseAsync("E-missing-retained-ancestry-fails-closed", MissingRetainedAncestryRefusedAsync);
        await CaseAsync("F-stale-retained-evidence-fails-closed", StaleRetainedEvidenceRefusedAsync);
        await CaseAsync("G-host-prepare-both-endpoint-orderings", HostPrepareBothOrderingsAsync);
        await CaseAsync("H-host-same-provider-distinct-accounts", SameProviderDistinctAccountsAsync);
        await CaseAsync("I-host-same-account-distinct-folders", SameAccountDistinctFoldersAsync);
        await CaseAsync("J-host-same-account-distinct-folders-drive", SameAccountDistinctFoldersDriveAsync);
        await CaseAsync("K-host-reopen-retains-identity", ReopenRetainsIdentityAsync);
        await CaseAsync("L-host-changed-retained-root-refused", ChangedRetainedRootRefusedAsync);
        await CaseAsync("M-host-missing-and-stale-hierarchy-refused", MissingAndStaleHierarchyRefusedAsync);
        await CaseAsync("N-host-unknown-and-unreachable-evidence-refused", UnknownAndUnreachableEvidenceRefusedAsync);
        await CaseAsync("O-host-late-refusal-leaves-no-authority", LateRefusalNoAuthorityAsync);
        await CaseAsync("P-host-recovery-evidence-refused-after-candidates", RecoveryEvidenceRefusedAsync);
        await CaseAsync("Q-host-create-once-staging-completes", StageCreateOnceCompletesAsync);
        await CaseAsync("Y-host-retained-observation-readback", RetainedObservationReadbackAsync);
        await CaseAsync("Z-opaque-host-run-probe", OpaqueHostRunProbeAsync);
        await CaseAsync("Z2-opaque-host-execution", OpaqueHostExecutionAsync);
        await CaseAsync("R-host-stage-root-authority-refusals", StageRootAuthorityRefusedAsync);
        await CaseAsync("S-host-retained-non-default-policy-revision", RetainedNonDefaultRevisionAsync);
        await CaseAsync("T-host-provider-file-content-seed-and-read", ProviderFileContentAsync);
        await CaseAsync("U-host-create-only-write-routes-and-refusals", WriteRouteRefusalsAsync);
        await CaseAsync("V-host-create-only-writes-and-exact-target-bytes", CreateWritesAndTargetBytesAsync);
        await CaseAsync("W-host-conditional-update-and-retained-predecessor", ConditionalUpdateAndRecoveryAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudPairHostBindingChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "cloud-pair-host-binding-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud pair host binding: " + _assertions +
            " assertions; the real retained-authority snapshot over the authority database, the real" +
            " provider-native hierarchy producer reached through each retained root's own bound account," +
            " the full retained-set recheck of the existing transactional registration, the fail-closed" +
            " refusals for unbound ancestry, missing retained ancestry and stale retained evidence, and the" +
            " real-Host prepare/bind route over BOTH registered adapters with its ordered success set, the" +
            " late-refusal and recovery-evidence partial-authority cases, the two disjoint folders of one" +
            " account and corpus sharing the one canonical Recovery authority with their distinct per-side" +
            " allocations, the create-once staging allocation with its root-authority refusals, and the one" +
            " fixed small nonempty provider file per side - represented by the existing metadata and list" +
            " routes as a file with its own id, revision, length and content hash, previewed as its own" +
            " nonempty row by the real public Host preview, and read back byte-exact through the real" +
            " Dropbox small-file download and the real Drive media read - plus the create-only write half:" +
            " the one small-file upload route on the Dropbox content host and the one multipart create route" +
            " under the Drive upload root, the exact target bytes each real adapter creates and reads back, and" +
            " the fail-closed refusals for an occupied destination, a stale expected revision, an update-shaped" +
            " or resumable request and a foreign parent, with write counters kept separate from every read.");
    }

    private static async Task CaseAsync(string id, Func<Task> body)
    {
        var before = _assertions;
        try
        {
            await body();
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["result"] = "PASS", ["assertions"] = _assertions - before
            });
            Console.WriteLine("PASS " + id + " (" + (_assertions - before) + " assertions)");
        }
        catch (Exception exception)
        {
            Cases.Add(new Dictionary<string, object?>
            {
                ["case"] = id, ["result"] = "FAIL", ["assertions"] = _assertions - before,
                ["error"] = exception.GetType().Name + ": " + exception.Message
            });
            Console.WriteLine("FAIL " + id + ": " + exception.GetType().Name + ": " + exception.Message);
            throw;
        }
    }

    private static void Assert(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("CC4A assertion failed: " + label);
    }

    // ----- the retained-authority fixture ------------------------------------------------------------

    /// <summary>One explicit cloud endpoint of the fixture.</summary>
    internal sealed record EndpointSpec(SyncEndpointKind Kind, string Provider, string ConnectionId,
        string AccountId, string RootNamespaceId, string HomeNamespaceId, string FolderId, string FolderPath)
    {
        internal string EndpointNamespaceId => SyncAuthorityProviderAccountIdentity.RetainedEndpointNamespace(
            Provider, AccountId, RootNamespaceId, HomeNamespaceId);

        internal string ProviderAccountId => SyncAuthorityProviderAccountIdentity.DeriveId(Provider, AccountId,
            RootNamespaceId, HomeNamespaceId);
    }

    private static EndpointSpec Dropbox(string suffix, string path) =>
        new(SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "conn-db-" + suffix, "dbid-" + suffix,
            "ns-root-" + suffix, "ns-home-" + suffix, "id:folder-" + suffix, path);

    private static EndpointSpec Drive(string suffix, string path) =>
        new(SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "conn-gd-" + suffix, "perm-" + suffix,
            "key-" + suffix, "key-" + suffix, "gd-folder-" + suffix, path);

    private static string CorpusRoot(EndpointSpec spec) =>
        SyncEndpointProviders.Get(spec.Kind).PathsDescribeAncestry
            ? "corpus-" + spec.FolderId
            : PilotPath.DropboxNamespaceRootIdentity(spec.RootNamespaceId);

    /// <summary>
    /// One complete, alias-covered provider hierarchy chain bound to exactly one retained authority. The
    /// fixture supplies these only where the guard demands a verified chain; the production Host is the
    /// component that will read them through each root's own adapter.
    /// </summary>
    private static SyncAuthorityCloudHierarchySnapshot Chain(EndpointSpec spec, string authorityId) =>
        new(spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, authorityId, spec.FolderId,
            new[] { CorpusRoot(spec), spec.FolderId })
        {
            CompleteToCorpusRoot = true,
            CorpusRootId = CorpusRoot(spec),
            ChainNodeKeys = new[]
            {
                new SyncAuthorityCloudHierarchyNodeKeys(CorpusRoot(spec),
                    new[] { SyncCloudObjectKey.Native(spec.Provider, CorpusRoot(spec)) }),
                new SyncAuthorityCloudHierarchyNodeKeys(spec.FolderId,
                    new[] { SyncCloudObjectKey.Native(spec.Provider, spec.FolderId) })
            },
            ObjectKeyCoverage = SyncCloudObjectKeySemantics.RequiredCoverage(spec.Provider)
        };

    /// <summary>One cloud/cloud pair of the fixture, retained with both of its explicit cloud roots.</summary>
    private sealed record PairSpec(string Id, EndpointSpec Left, EndpointSpec Right, string LeftRoot,
        string RightRoot);

    private sealed class Fixture
    {
        internal required string DatabasePath { get; init; }
        internal required SyncAuthorityDatabase Db { get; init; }
        internal required List<PairSpec> Pairs { get; init; }
        internal required SyncAuthorityCloudRecoveryRoot Recovery { get; init; }
        internal required EndpointSpec RecoverySpec { get; init; }
        internal required FixtureBuilder Builder { get; init; }

        internal PairSpec Pair(string id) => Pairs.Single(item => item.Id == id);
        internal string RootBindingId(PairSpec pair, string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? pair.LeftRoot : pair.RightRoot;
        internal EndpointSpec SpecFor(PairSpec pair, string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? pair.Left : pair.Right;
    }

    /// <summary>
    /// Two real cross-provider cloud pairs over real isolated SQLite, each with both explicit cloud roots,
    /// plus one real retained recovery boundary for a third Dropbox account. Cross-provider roots are
    /// provably distinct, so no chain is fabricated for them; the one same-provider relationship the guard
    /// must decide is the recovery boundary against the two Dropbox pair roots, and for that the fixture
    /// supplies the exact chains the guard requires.
    /// </summary>
    private static async Task<Fixture> CreateFixtureAsync(string caseName)
    {
        var directory = IOPath.Combine(_root, caseName);
        Directory.CreateDirectory(directory);
        var path = IOPath.Combine(directory, "syncshark.db");
        var db = new SyncAuthorityDatabase(path);
        await db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
        var leftA = Dropbox("lefta", "/Dropbox LeftA");
        var rightA = Drive("righta", "/Drive RightA");
        var leftB = Drive("leftb", "/Drive LeftB");
        var rightB = Dropbox("rightb", "/Dropbox RightB");
        var recovery = Dropbox("recovery", "/Dropbox Recovery");
        foreach (var spec in new[] { leftA, rightA, leftB, rightB, recovery })
            await db.RegisterProviderAccountAsync(spec.ProviderAccountId, InstallationId, spec.AccountId,
                spec.RootNamespaceId, spec.HomeNamespaceId, "CC4A " + spec.FolderId, 1,
                cancellationToken: default, provider: spec.Provider);
        var builder = new FixtureBuilder { Db = db, DatabasePath = path, CaseName = caseName };
        var pairA = await builder.RetainPairAsync("a", leftA, rightA);
        var pairB = await builder.RetainPairAsync("b", leftB, rightB);
        // The recovery boundary is registered AFTER both pairs, so the existing guard really does recheck it
        // against every retained Dropbox pair root of this installation.
        var recoveryRootId = "recovery-" + caseName;
        var recoveryCorpus = CorpusRoot(recovery);
        var recoveryChains = await builder.RequiredChainsAsync(recoveryRootId, recovery);
        var retainedRecovery = await db.RegisterCloudRecoveryRootAsync(recoveryRootId, InstallationId,
            recovery.Provider, recovery.ProviderAccountId, recovery.EndpointNamespaceId, recovery.FolderId,
            recoveryCorpus, recovery.FolderPath, 1, cancellationToken: default, hierarchies: recoveryChains);
        builder.Specs[recoveryRootId] = recovery;
        return new Fixture
        {
            DatabasePath = path,
            Db = db,
            Pairs = [pairA, pairB],
            Recovery = retainedRecovery,
            RecoverySpec = recovery,
            Builder = builder
        };
    }

    /// <summary>
    /// Retains pairs and roots exactly as the Host must: before each registration the current retained set
    /// is read through the same snapshot the Host uses, and an exact-bound chain is supplied for the
    /// candidate plus every retained authority of the candidate's own provider. That is the full retained
    /// set the existing transactional registration rechecks; no retained root is omitted.
    /// </summary>
    private sealed class FixtureBuilder
    {
        internal SyncAuthorityDatabase Db = null!;
        internal string DatabasePath = "";
        internal string CaseName = "";
        internal Dictionary<string, EndpointSpec> Specs { get; } = new(StringComparer.Ordinal);

        internal async Task<PairSpec> RetainPairAsync(string tag, EndpointSpec left, EndpointSpec right)
        {
            var pairId = await RetainPairDefinitionAsync(tag, left, right);
            var leftRoot = "root-" + CaseName + "-" + tag + "-left";
            var rightRoot = "root-" + CaseName + "-" + tag + "-right";
            await RegisterRootAsync(leftRoot, pairId, SyncAuthorityCloudSides.LogicalLeft, left, null);
            await RegisterRootAsync(rightRoot, pairId, SyncAuthorityCloudSides.LogicalRight, right, null);
            return new PairSpec(pairId, left, right, leftRoot, rightRoot);
        }

        internal async Task<string> RetainPairDefinitionAsync(string tag, EndpointSpec left, EndpointSpec right)
        {
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(left.Kind, left.ConnectionId, left.EndpointNamespaceId, left.FolderId,
                    left.FolderPath),
                SyncEndpoint.Cloud(right.Kind, right.ConnectionId, right.EndpointNamespaceId, right.FolderId,
                    right.FolderPath), SyncMode.TwoWay), "CC4A " + CaseName + " " + tag);
            var normalized = SyncPairStore.NormalizePair(draft);
            await Db.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
            await Db.RetainPairDefinitionAsync(normalized, true);
            return normalized.Id;
        }

        internal async Task RegisterRootAsync(string rootId, string pairId, string side, EndpointSpec spec,
            IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>? chains)
        {
            await Db.RegisterCloudEndpointRootBindingAsync(rootId, pairId, side, 1, InstallationId,
                spec.Provider, spec.ProviderAccountId, spec.EndpointNamespaceId, spec.FolderId, spec.FolderPath,
                cancellationToken: default,
                hierarchies: chains ?? await RequiredChainsAsync(rootId, spec));
            Specs[rootId] = spec;
        }

        /// <summary>The candidate chain plus one exact-bound chain for every retained same-provider authority.</summary>
        internal async Task<IReadOnlyList<SyncAuthorityCloudHierarchySnapshot>> RequiredChainsAsync(
            string candidateAuthorityId, EndpointSpec candidateSpec)
        {
            var retained = await Db.LoadRetainedCloudAuthoritiesAsync();
            var chains = new List<SyncAuthorityCloudHierarchySnapshot>
            {
                Chain(candidateSpec, candidateAuthorityId)
            };
            foreach (var identity in retained)
            {
                if (!string.Equals(identity.Provider, candidateSpec.Provider, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!Specs.TryGetValue(identity.AuthorityId, out var spec)) continue;
                chains.Add(Chain(spec, identity.AuthorityId));
            }
            return chains;
        }
    }

    // ----- A. the snapshot is the complete retained set ----------------------------------------------

    private static async Task RetainedSnapshotCompleteAsync()
    {
        var fixture = await CreateFixtureAsync("snapshot-complete");
        var retained = await fixture.Db.LoadRetainedCloudAuthoritiesAsync();

        foreach (var pair in fixture.Pairs)
            foreach (var side in new[] { SyncAuthorityCloudSides.LogicalLeft, SyncAuthorityCloudSides.LogicalRight })
            {
                var root = fixture.RootBindingId(pair, side);
                var spec = fixture.SpecFor(pair, side);
                var found = retained.SingleOrDefault(item => item.AuthorityId == root);
                Assert(found is not null, "the snapshot retains the " + side + " pair root of " + pair.Id);
                Assert(found!.AuthorityKind == SyncAuthorityCloudRootIdentity.PairRootAuthority,
                    "the retained pair root keeps its pair-root authority kind");
                Assert(found.CompatibilitySide == side, "the retained pair root keeps its logical side");
                Assert(found.Provider == spec.Provider && found.ProviderAccountId == spec.ProviderAccountId &&
                       found.NamespaceId == spec.EndpointNamespaceId && found.FolderId == spec.FolderId,
                    "the retained pair root carries its exact provider, account, namespace and folder identity");
            }
        var recovery = retained.SingleOrDefault(item =>
            item.AuthorityId == fixture.Recovery.RecoveryRootBindingId);
        Assert(recovery is not null, "the snapshot retains the active recovery boundary");
        Assert(recovery!.AuthorityKind == SyncAuthorityCloudRootIdentity.RecoveryRootAuthority,
            "the retained recovery boundary keeps its recovery-root authority kind");
        Assert(retained.Count == 5,
            "the snapshot is exactly the five retained cloud authorities (four pair roots and one recovery " +
            "boundary), observed " + retained.Count);
        // Opposite slots of the same pair are independent authorities even for one provider.
        Assert(retained.Select(item => item.AuthorityId).Distinct(StringComparer.Ordinal).Count() == retained.Count,
            "every retained authority is present exactly once");
    }

    // ----- B. a non-active root is not part of the retained set, and the read is read-only -------------

    private static async Task InactiveRootExcludedAsync()
    {
        var fixture = await CreateFixtureAsync("snapshot-inactive");
        var pair = fixture.Pair(fixture.Pairs[1].Id);
        var superseded = pair.LeftRoot;
        var before = await fixture.Db.LoadRetainedCloudAuthoritiesAsync();
        Assert(before.Any(item => item.AuthorityId == superseded), "the root starts inside the retained set");
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE root_binding SET state='superseded' WHERE root_binding_id='" + superseded + "';");
        var after = await fixture.Db.LoadRetainedCloudAuthoritiesAsync();
        Assert(after.All(item => item.AuthorityId != superseded),
            "a superseded root binding is not part of the retained set the comparison rechecks");
        Assert(after.Count == before.Count - 1, "exactly the superseded authority left the retained set");
        Assert(await NumberAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE state='active';") == after.Count(item =>
                   item.AuthorityKind == SyncAuthorityCloudRootIdentity.PairRootAuthority),
            "the snapshot read changed no root binding state");
    }

    // ----- C. every retained root is resolved through its own bound account ---------------------------

    private static async Task PerRetainedRootResolutionAsync()
    {
        var fixture = await CreateFixtureAsync("per-root-resolution");
        var retained = await fixture.Db.LoadRetainedCloudAuthoritiesAsync();
        var resolved = new List<string>();
        foreach (var identity in retained.Where(item => item.Provider == SyncProviders.Dropbox))
        {
            var spec = SpecForAuthority(fixture, identity.AuthorityId);
            var wire = new DropboxWire(spec);
            using var http = new HttpClient(wire);
            var binding = DropboxBinding(spec);
            var client = new DropboxPilotClient(http, new SyntheticTokenSource(), spec.FolderPath,
                binding, spec.FolderId);
            // The Host resolves the retained root's own access route: the connection id the adapter actually
            // retains for that identity's account, never another endpoint's.
            var request = new SyncAuthorityCloudRootHierarchyRequest(identity.Provider!,
                binding.ConnectionId, identity.ProviderAccountId!, identity.NamespaceId!, identity.FolderId,
                identity.FolderPath ?? spec.FolderPath);
            var evidence = await client.ReadRootHierarchyAsync(request);
            var snapshot = evidence.ToSnapshot(identity.AuthorityId);
            Assert(snapshot.BindsTo(identity),
                "the provider-native hierarchy read is bound to exactly the retained authority " +
                identity.AuthorityId);
            Assert(snapshot.IsCompleteFor(identity),
                "the retained authority " + identity.AuthorityId +
                " has a complete provider-verified chain from its corpus root");
            Assert(snapshot.IsDisjointnessCompleteFor(identity),
                "the retained Dropbox authority readback declares the full object-key coverage its provider issues");
            Assert(evidence.VerifiedPassCount >= 2 && wire.AccountCalls >= 1 && wire.MetadataCalls >= 2,
                "the real adapter verified the account and read the ancestry twice before it emitted evidence");
            resolved.Add(identity.AuthorityId);
            // The same request through an adapter bound to a DIFFERENT retained account is refused before any
            // provider read: a retained root is only ever reached through its own bound account.
            var other = retained.First(item => item.Provider == SyncProviders.Dropbox &&
                                               item.AuthorityId != identity.AuthorityId);
            var otherSpec = SpecForAuthority(fixture, other.AuthorityId);
            var otherWire = new DropboxWire(otherSpec);
            using var otherHttp = new HttpClient(otherWire);
            var otherClient = new DropboxPilotClient(otherHttp, new SyntheticTokenSource(),
                otherSpec.FolderPath, DropboxBinding(otherSpec), otherSpec.FolderId);
            var refused = false;
            try { await otherClient.ReadRootHierarchyAsync(request); }
            catch (SyncAuthorityCloudRootHierarchyException) { refused = true; }
            Assert(refused,
                "another retained account's adapter refuses a hierarchy read for " + identity.AuthorityId);
            Assert(otherWire.AccountCalls == 0,
                "the substituted account performed no provider read for the refused request");
        }
        Assert(resolved.Count == 3,
            "all three retained Dropbox authorities were resolved through their own bound accounts, observed " +
            resolved.Count);
    }

    private static EndpointSpec SpecForAuthority(Fixture fixture, string authorityId)
    {
        foreach (var pair in fixture.Pairs)
        {
            if (pair.LeftRoot == authorityId) return pair.Left;
            if (pair.RightRoot == authorityId) return pair.Right;
        }
        if (fixture.Recovery.RecoveryRootBindingId == authorityId) return fixture.RecoverySpec;
        throw new InvalidDataException("The fixture has no endpoint for authority " + authorityId + ".");
    }

    private static DropboxConnectionBinding DropboxBinding(EndpointSpec spec) =>
        new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, spec.AccountId,
            spec.RootNamespaceId, spec.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes).Normalize();

    // ----- D. caller-supplied ancestry is never trusted -----------------------------------------------

    private static async Task CallerAncestryRefusedAsync()
    {
        var fixture = await CreateFixtureAsync("caller-ancestry");
        var extra = Drive("extra", "/Drive Extra");
        await fixture.Db.RegisterProviderAccountAsync(extra.ProviderAccountId, InstallationId, extra.AccountId,
            extra.RootNamespaceId, extra.HomeNamespaceId, "CC4A extra", 1, cancellationToken: default,
            provider: extra.Provider);
        var other = Dropbox("other", "/Dropbox Other");
        await fixture.Db.RegisterProviderAccountAsync(other.ProviderAccountId, InstallationId, other.AccountId,
            other.RootNamespaceId, other.HomeNamespaceId, "CC4A other", 1, cancellationToken: default,
            provider: other.Provider);
        var builder = fixture.Builder;
        var pairId = await builder.RetainPairDefinitionAsync("c", extra, other);
        var candidateRoot = "root-caller-ancestry-c-left";
        // A chain for an authority this database never retained is not ancestry; it is refused rather than
        // accepted as proof that the candidate is disjoint from it.
        var fabricated = Chain(Drive("ghost", "/Drive Ghost"), "root-never-retained");
        var refused = false;
        try
        {
            await builder.RegisterRootAsync(candidateRoot, pairId, SyncAuthorityCloudSides.LogicalLeft, extra,
                new[] { Chain(extra, candidateRoot), fabricated });
        }
        catch (InvalidDataException error) when (error.Message.Contains("not bound to an exact retained",
                     StringComparison.Ordinal))
        {
            refused = true;
        }
        Assert(refused, "a hierarchy for an authority the database never retained is refused, not trusted");
        Assert(await NumberAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE root_binding_id='" + candidateRoot + "';") == 0,
            "the refused registration wrote no root binding");
    }

    // ----- E. missing retained ancestry fails closed --------------------------------------------------

    private static async Task MissingRetainedAncestryRefusedAsync()
    {
        var fixture = await CreateFixtureAsync("missing-ancestry");
        // A same-provider pair: the two Dropbox roots are compared by the one shared guard, which requires a
        // complete, alias-covered chain for BOTH the candidate and the retained root. Supplying the
        // candidate's chain alone leaves the retained root unverified and must refuse.
        var left = Dropbox("same-left", "/Dropbox SameLeft");
        var right = Dropbox("same-right", "/Dropbox SameRight");
        foreach (var spec in new[] { left, right })
            await fixture.Db.RegisterProviderAccountAsync(spec.ProviderAccountId, InstallationId, spec.AccountId,
                spec.RootNamespaceId, spec.HomeNamespaceId, "CC4A " + spec.FolderId, 1,
                cancellationToken: default, provider: spec.Provider);
        var builder = fixture.Builder;
        var pairId = await builder.RetainPairDefinitionAsync("same", left, right);
        var leftRoot = "root-same-left";
        var rightRoot = "root-same-right";
        await builder.RegisterRootAsync(leftRoot, pairId, SyncAuthorityCloudSides.LogicalLeft, left, null);
        // The retained left root is in the snapshot, so the right registration must be compared with it.
        var retained = await fixture.Db.LoadRetainedCloudAuthoritiesAsync();
        Assert(retained.Any(item => item.AuthorityId == leftRoot),
            "the retained same-provider root is in the snapshot the registration rechecks");
        var partial = false;
        try
        {
            await fixture.Db.RegisterCloudEndpointRootBindingAsync(rightRoot, pairId,
                SyncAuthorityCloudSides.LogicalRight, 1, InstallationId, right.Provider, right.ProviderAccountId,
                right.EndpointNamespaceId, right.FolderId, right.FolderPath, cancellationToken: default,
                hierarchies: new[] { Chain(right, rightRoot) });
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            partial = true;
        }
        Assert(partial,
            "a candidate root whose retained same-provider authority has no verified chain is refused");
        Assert(await NumberAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE root_binding_id='" + rightRoot + "';") == 0,
            "the refused same-provider registration wrote no root binding");
        Assert(await NumberAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE root_binding_id='" + leftRoot + "' AND state='active';") == 1,
            "the retained root survives the refused registration unchanged");
        // With the full retained set resolved through the same snapshot, the identical registration is
        // accepted; the only change is that every retained same-provider authority now has its own chain.
        await builder.RegisterRootAsync(rightRoot, pairId, SyncAuthorityCloudSides.LogicalRight, right, null);
        var complete = await fixture.Db.LoadRetainedCloudAuthoritiesAsync();
        Assert(complete.Any(item => item.AuthorityId == rightRoot),
            "the fully resolved retained set lets the same registration complete");
    }

    // ----- F. stale retained evidence fails closed ---------------------------------------------------

    private static async Task StaleRetainedEvidenceRefusedAsync()
    {
        var fixture = await CreateFixtureAsync("stale-evidence");
        var pair = fixture.Pairs[0];
        var spec = pair.Left;
        await ExecuteAsync(fixture.DatabasePath,
            "UPDATE provider_account SET root_namespace_id='ns-root-stale' WHERE provider_account_id='" +
            spec.ProviderAccountId + "';");
        var refused = false;
        try { await fixture.Db.LoadRetainedCloudAuthoritiesAsync(); }
        catch (InvalidOperationException error) when (error.Message.Contains("does not agree with its active",
                     StringComparison.Ordinal))
        {
            refused = true;
        }
        Assert(refused,
            "a retained root whose provider account no longer agrees fails the snapshot closed rather than " +
            "being silently omitted from the comparison set");
        Assert(await NumberAsync(fixture.DatabasePath,
                   "SELECT count(*) FROM root_binding WHERE root_binding_id='" + pair.LeftRoot + "' AND state='active';") == 1,
            "the stale retained root is still retained and was never removed by the failed read");
    }

    // ----- the synthetic Dropbox HTTP transport -------------------------------------------------------

    /// <summary>
    /// The synthetic Dropbox endpoint the real production adapter is driven through: the account read and
    /// folder metadata only, over synthetic in-process HTTP with a synthetic token. Any other route fails the
    /// run, so a fixture can never silently widen what the adapter is allowed to reach.
    /// </summary>
    private sealed class DropboxWire(EndpointSpec spec) : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _byPath = new(StringComparer.OrdinalIgnoreCase)
        {
            [PilotPath.NormalizeDropboxRoot(spec.FolderPath)] = spec.FolderId
        };
        private readonly Dictionary<string, string> _byId = new(StringComparer.Ordinal)
        {
            [spec.FolderId] = PilotPath.NormalizeDropboxRoot(spec.FolderPath)
        };

        internal int AccountCalls { get; private set; }
        internal int MetadataCalls { get; private set; }
        internal List<string> PathRootNamespaces { get; } = [];

        /// <summary>
        /// Test-only provider INPUT failure injection. It is inert unless a fixture sets it, and it fails the
        /// provider READ so the real publication decision fails closed on real provider input; it never
        /// replaces or bypasses a guard, commit or publication decision.
        /// </summary>
        internal Func<HttpRequestMessage, bool>? ProviderInputFailureForTest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProviderInputFailureForTest is { } injectedFailure && injectedFailure(request))
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error_summary\":\"synthetic provider input failure\"}")
                };
            if (request.Method != HttpMethod.Post)
                throw new InvalidDataException("The synthetic Dropbox endpoint serves POST reads only.");
            if (!string.Equals(request.RequestUri!.Host, "api.dropboxapi.com", StringComparison.Ordinal))
                throw new InvalidDataException("The synthetic Dropbox endpoint refuses an unexpected origin.");
            if (request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots))
            {
                using var pathRoot = JsonDocument.Parse(roots.Single());
                PathRootNamespaces.Add(pathRoot.RootElement.GetProperty("root").GetString() ?? string.Empty);
            }
            var route = request.RequestUri.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            {
                AccountCalls++;
                return Json("{\"account_id\":\"" + spec.AccountId + "\",\"root_info\":{\"root_namespace_id\":\"" +
                            spec.RootNamespaceId + "\",\"home_namespace_id\":\"" + spec.HomeNamespaceId + "\"}}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
                MetadataCalls++;
                string? id = null;
                string? display = null;
                if (path.StartsWith("id:", StringComparison.Ordinal))
                {
                    if (_byId.TryGetValue(path, out var found)) { id = path; display = found; }
                }
                else if (_byPath.TryGetValue(path, out var foundId))
                {
                    id = foundId;
                    display = PilotPath.NormalizeDropboxRoot(path);
                }
                if (id is null || display is null)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("{\"error_summary\":\"not_found\"}", Encoding.UTF8,
                            "application/json")
                    };
                var name = display == "/" ? string.Empty : display[(display.LastIndexOf('/') + 1)..];
                return Json("{\".tag\":\"folder\",\"name\":\"" + name + "\",\"id\":\"" + id +
                            "\",\"path_lower\":\"" + display.ToLowerInvariant() + "\",\"path_display\":\"" +
                            display + "\"}");
            }
            throw new InvalidDataException(
                "The synthetic Dropbox endpoint refuses the unexpected route " + route + ".");
        }

        private static HttpResponseMessage Json(string value) => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };
    }

    private sealed class SyntheticTokenSource : IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult("synthetic-not-a-credential");
        }
    }

    // ----- raw isolated-database helpers --------------------------------------------------------------

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> NumberAsync(string databasePath, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // =================================================================================================
    // CC4APROOF: the real-Host CC4A prepare/bind proofs
    // =================================================================================================
    //
    // Everything below constructs the REAL production SyncAuthorityRuntimeHost over a real protected Windows
    // fixture state root leased through the production PilotInstanceLock, real isolated SQLite authority
    // storage, the real central provider adapter factory and the real DropboxPilotClient and
    // GoogleDrivePilotClient adapters. Only the provider HTTP transport is synthetic: one in-process handler
    // with synthetic tokens, no network, no credential-store access and no live account. The Host is
    // constructed and invoked here; it is never installed, activated or run against a real provider.

    private const string RecommendedRecoveryPath = "/Apps/Sync Shark/Recovery";

    internal static EndpointSpec HostDropbox(string suffix, string path) =>
        new(SyncEndpointKind.Dropbox, SyncProviders.Dropbox,
            DropboxConnectionBinding.CreateConnectionId("dbid-" + suffix), "dbid-" + suffix,
            "ns-root-" + suffix, "ns-home-" + suffix, "id:host-folder-" + suffix, path);

    internal static EndpointSpec HostDrive(string suffix, string path)
    {
        var permission = "perm-" + suffix;
        var key = GoogleDriveConnectionBinding.CreateAccountKey(permission);
        return new EndpointSpec(SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive,
            GoogleDriveConnectionBinding.CreateConnectionId(key), permission, key, key, "gd-folder-" + suffix,
            path);
    }

    private static SyncEndpoint HostEndpoint(EndpointSpec spec) =>
        SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId, spec.EndpointNamespaceId, spec.FolderId,
            spec.FolderPath);

    /// <summary>One retained pair definition plus the two authority root binding ids it is bound under.</summary>
    internal sealed record HostPair(SyncPair Pair, EndpointSpec Left, EndpointSpec Right, string LeftRoot,
        string RightRoot)
    {
        internal string Id => Pair.Id;
        internal string RecoveryRootId(string side) =>
            SyncAuthorityCloudExecution.Id("recovery-cloud", Id, side);
    }

    /// <summary>
    /// One real adapter read of a seeded provider file: the exact bytes the adapter delivered and the
    /// identity result the adapter itself derived for them.
    /// </summary>
    internal sealed record AdapterRead(byte[] Bytes, SyncDownloadResult Result);

    /// <summary>
    /// The real production Host over the isolated protected fixture. PilotInstanceLock leases a real Windows
    /// state root, the authority is real isolated SQLite, the adapters are the real provider clients, and only
    /// their HTTP transport and synthetic credential values are supplied here.
    /// </summary>
    internal sealed class HostRig : IDisposable
    {
        private readonly PilotInstanceLock _instance;
        private readonly string? _priorClientId;
        private readonly HttpClient _http;
        /// <summary>The exact connection binding each registered endpoint was retained and served under.</summary>
        private readonly Dictionary<string, DropboxConnectionBinding> _dropboxBindings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GoogleDriveConnectionBinding> _googleBindings = new(StringComparer.Ordinal);

        internal HostWire Wire { get; }
        internal SyncAuthorityDatabase Db { get; }
        internal SyncAuthorityRuntimeHost Host { get; }
        internal string InstallationId { get; }
        internal string StateRoot { get; }
        /// <summary>The retained state authority this rig's Host is built over, for fixture preferences.</summary>
        internal PilotStateAuthority StateAuthority => _instance.StateAuthority;

        private HostRig(PilotInstanceLock instance, string? priorClientId, HostWire wire,
            SyncAuthorityDatabase db, HttpClient http, SyncAuthorityRuntimeHost host, string installationId)
        {
            _instance = instance;
            _priorClientId = priorClientId;
            Wire = wire;
            Db = db;
            _http = http;
            Host = host;
            InstallationId = installationId;
            StateRoot = instance.StateAuthority.StateRoot;
        }

        /// <summary>
        /// Builds the real Host over an isolated fixture root. A caller may supply the provider transport: it
        /// then outlives this Host and models a provider whose stored state did not change while the Host was
        /// closed and reopened, so a reopened Host observes exactly what the previous one left behind. The rig
        /// never disposes a transport it was given.
        /// </summary>
        internal static async Task<HostRig> CreateAsync(string caseName, HostWire? providerTransport = null,
            HttpMessageHandler? providerTransportOverride = null, string? stateDirectory = null,
            string? authorityFileName = null)
        {
            if (providerTransportOverride is not null && providerTransport is null)
                throw new ArgumentException("A transport composition requires its existing isolated provider wire.");
            var root = IOPath.Combine(_root, "host", caseName);
            Directory.CreateDirectory(root);
            // The state directory and the authority database are separable so a second rig can open a FRESH
            // authority database over the SAME state root, which is exactly the product state reset that makes
            // a previously owned private staging namespace collide. Both default to the original layout.
            var stateRoot = stateDirectory ?? IOPath.Combine(root, "state");
            Directory.CreateDirectory(stateRoot);
            var instance = PilotInstanceLock.Acquire(IOPath.Combine(stateRoot, "instance.lock"));
            var priorClientId = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
            try
            {
                var db = new SyncAuthorityDatabase(IOPath.Combine(root, authorityFileName ?? "authority.db"));
                var wire = providerTransport ?? new HostWire();
                var http = providerTransport is null ? new HttpClient(wire) : new HttpClient(providerTransportOverride ?? wire,
                    disposeHandler: false);
                var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, http,
                    new SyncPairStore(instance.StateAuthority),
                    new SyncRuntimeStateStore(instance.StateAuthority),
                    new DropboxConnectionStore(instance.StateAuthority), new FixtureDropboxCredentials(), db,
                    googleDriveCredentialStore: new FixtureGoogleCredentials(),
                    googleDriveTokenEndpoint: new FixtureGoogleEndpoint(wire));
                // The real Host binds, initialises and reads the authority exactly as production does.
                await host.LoadPairsAsync();
                var installationId = await db.LoadInstallationIdAsync()
                                     ?? throw new InvalidDataException(
                                         "The fixture authority retained no installation identity.");
                return new HostRig(instance, priorClientId, wire, db, http, host, installationId);
            }
            catch
            {
                GoogleDriveProviderConfiguration.RegisteredClientIdForTest = priorClientId;
                instance.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Host.Dispose();
            _http.Dispose();
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = _priorClientId;
            _instance.Dispose();
        }

        /// <summary>Registers one retained provider account and its synthetic transport route.</summary>
        internal async Task AddEndpointAsync(EndpointSpec spec)
        {
            await Db.RegisterProviderAccountAsync(spec.ProviderAccountId, InstallationId, spec.AccountId,
                spec.RootNamespaceId, spec.HomeNamespaceId, "CC4A host " + spec.AccountId, 1,
                cancellationToken: default, provider: spec.Provider);
            if (spec.Kind == SyncEndpointKind.Dropbox)
            {
                var store = new DropboxConnectionStore(_instance.StateAuthority);
                var binding = DropboxConnection(spec, primary: store.Load() is null);
                if (store.Load(binding.ConnectionId) is null) await store.SaveAsync(binding);
                _dropboxBindings[spec.ConnectionId] = binding;
                Wire.AddDropboxFolder(binding, spec.FolderPath, spec.FolderId);
                foreach (var ancestor in AncestorPaths(spec.FolderPath))
                    Wire.AddDropboxFolder(binding, ancestor,
                        "id:host-ancestor-" + ancestor.Trim('/').Replace('/', '-'));
                return;
            }
            var google = GoogleConnection(spec);
            var googleStore = new GoogleDriveConnectionStore(_instance.StateAuthority);
            if (googleStore.Load(google.ConnectionId) is null) await googleStore.SaveAsync(google);
            _googleBindings[spec.ConnectionId] = google;
            Wire.AddDriveAccount(google, "drive-root-" + spec.AccountId);
            Wire.AddDriveFolder(google, spec.FolderPath, spec.FolderId);
        }

        /// <summary>
        /// Seeds the one fixed small nonempty provider file inside this endpoint's own retained root and
        /// namespace, through the same connection binding that endpoint was registered and served under.
        /// </summary>
        internal HostWire.SeededFile SeedFile(EndpointSpec spec, string relativePath, string text)
        {
            var path = spec.FolderPath.TrimEnd('/') + "/" + relativePath.TrimStart('/');
            if (spec.Kind == SyncEndpointKind.Dropbox)
                return Wire.AddDropboxFile(_dropboxBindings[spec.ConnectionId], path, text);
            if (spec.Kind == SyncEndpointKind.GoogleDrive)
                return Wire.AddDriveFile(_googleBindings[spec.ConnectionId], path, text);
            throw new InvalidDataException("A cloud pair endpoint is Dropbox or Google Drive only.");
        }

        /// <summary>
        /// Retains one extra folder path for this endpoint's own namespace and account, exactly as the Host's
        /// own root registration retains the product recovery container.
        /// </summary>
        internal void RegisterFolder(EndpointSpec spec, string path, string folderId)
        {
            if (spec.Kind == SyncEndpointKind.Dropbox)
            {
                Wire.AddDropboxFolder(_dropboxBindings[spec.ConnectionId], path, folderId);
                return;
            }
            if (spec.Kind == SyncEndpointKind.GoogleDrive)
            {
                Wire.AddDriveFolder(_googleBindings[spec.ConnectionId], path, folderId);
                return;
            }
            throw new InvalidDataException("A cloud pair endpoint is Dropbox or Google Drive only.");
        }

        /// <summary>
        /// Reads one seeded file back through the REAL Dropbox adapter over this rig's synthetic transport:
        /// its own listing finds the object and its own revision-pinned download returns the exact bytes.
        /// </summary>
        internal async Task<AdapterRead> ReadDropboxFileAsync(EndpointSpec spec, string relativePath)
        {
            var client = DropboxClient(spec);
            var entries = await client.ListEntriesAsync();
            var entry = entries.SingleOrDefault(item => item.Kind == PilotEntryKind.File &&
                string.Equals(item.RelativePath, PilotPath.NormalizeRelative(relativePath),
                    StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    "The real Dropbox listing did not represent the seeded file " + relativePath + ".");
            using var buffer = new MemoryStream();
            var result = await client.DownloadAsync(entry, buffer);
            return new AdapterRead(buffer.ToArray(), result);
        }

        /// <summary>
        /// Reads one seeded file back through the REAL Google Drive adapter over this rig's synthetic
        /// transport: its own inventory finds the object and its own media read returns the exact bytes,
        /// which the adapter itself checks against the provider-issued whole-file SHA-256.
        /// </summary>
        internal async Task<AdapterRead> ReadDriveFileAsync(EndpointSpec spec, string relativePath)
        {
            var client = DriveClient(spec);
            var inventory = await client.ListInventoryAsync();
            var entry = inventory.Entries.SingleOrDefault(item => item.Kind == GoogleDriveEntryKind.Blob &&
                string.Equals(item.RelativePath, PilotPath.NormalizeRelative(relativePath),
                    StringComparison.Ordinal))
                ?? throw new InvalidDataException(
                    "The real Google Drive inventory did not represent the seeded file " + relativePath + ".");
            using var buffer = new MemoryStream();
            var result = await client.DownloadAsync(entry.ToRemoteEntry(), buffer);
            return new AdapterRead(buffer.ToArray(), result);
        }

        /// <summary>The real Dropbox adapter bound to this endpoint's own retained connection and root.</summary>
        internal DropboxPilotClient DropboxClient(EndpointSpec spec)
        {
            var binding = _dropboxBindings[spec.ConnectionId];
            return new DropboxPilotClient(_http,
                new RefreshingDropboxAccessTokenSource(_http, new FixtureDropboxCredentials(), binding),
                spec.FolderPath, binding, spec.FolderId);
        }

        /// <summary>The real Google Drive adapter bound to this endpoint's own retained connection and root.</summary>
        internal GoogleDrivePilotClient DriveClient(EndpointSpec spec)
        {
            var google = _googleBindings[spec.ConnectionId];
            return new GoogleDrivePilotClient(_http,
                new GoogleDriveAccessTokenSource(new FixtureGoogleCredentials(), new FixtureGoogleEndpoint(Wire),
                    google), google, spec.FolderId, spec.FolderPath);
        }

        /// <summary>The adapter's own retained revision for one object, read from its real listing.</summary>
        internal async Task<string> DriveRevisionAsync(EndpointSpec spec, string relativePath)
        {
            var inventory = await DriveClient(spec).ListInventoryAsync();
            return inventory.Entries.Single(item => item.Kind == GoogleDriveEntryKind.Blob &&
                    string.Equals(item.RelativePath, PilotPath.NormalizeRelative(relativePath),
                        StringComparison.Ordinal))
                .Revision;
        }

        /// <summary>The product recovery container both providers retain predecessors under.</summary>
        internal static string RecoveryContainerPath => SyncControlNamespace.RemoteContainerPath;

        /// <summary>The exact folder identity this endpoint registered for the product recovery container.</summary>
        internal string? RecoveryRootFolderId(EndpointSpec spec) =>
            spec.Kind == SyncEndpointKind.Dropbox
                ? Wire.RetainedDropboxFolderId(spec.RootNamespaceId, RecoveryContainerPath)
                : Wire.RetainedDriveFolderId(spec.AccountId, RecoveryContainerPath);

        /// <summary>
        /// Reads the one predecessor bytes copy retained under this endpoint's own recovery container, through
        /// the real adapter bound to that recovery root, and returns its retained name with them.
        /// </summary>
        internal async Task<(string Name, AdapterRead Read)> ReadRetainedPredecessorAsync(EndpointSpec spec)
        {
            var rootFolderId = RecoveryRootFolderId(spec)
                ?? throw new InvalidDataException("This endpoint registered no product recovery container.");
            var relative = PilotPath.NormalizeRelative(spec.Kind == SyncEndpointKind.Dropbox
                ? (await ListDropboxRecoveryFilesAsync(spec, rootFolderId)).Single()
                : (await ListDriveRecoveryFilesAsync(spec, rootFolderId)).Single());
            return (relative, spec.Kind == SyncEndpointKind.Dropbox
                ? await ReadDropboxRecoveryFileAsync(spec, rootFolderId, relative)
                : await ReadDriveRecoveryFileAsync(spec, rootFolderId, relative));
        }

        private async Task<IReadOnlyList<string>> ListDropboxRecoveryFilesAsync(EndpointSpec spec, string rootFolderId)
        {
            var binding = _dropboxBindings[spec.ConnectionId];
            var client = new DropboxPilotClient(_http,
                new RefreshingDropboxAccessTokenSource(_http, new FixtureDropboxCredentials(), binding),
                RecoveryContainerPath, binding, rootFolderId);
            var entries = await client.ListEntriesAsync();
            return entries.Where(item => item.Kind == PilotEntryKind.File)
                .Select(item => item.RelativePath).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        }

        private async Task<IReadOnlyList<string>> ListDriveRecoveryFilesAsync(EndpointSpec spec, string rootFolderId)
        {
            var inventory = await DriveRecoveryClient(spec, rootFolderId).ListInventoryAsync();
            return inventory.Entries.Where(item => item.Kind == GoogleDriveEntryKind.Blob)
                .Select(item => item.RelativePath).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        }

        private async Task<AdapterRead> ReadDropboxRecoveryFileAsync(EndpointSpec spec, string rootFolderId,
            string relativePath)
        {
            var binding = _dropboxBindings[spec.ConnectionId];
            var client = new DropboxPilotClient(_http,
                new RefreshingDropboxAccessTokenSource(_http, new FixtureDropboxCredentials(), binding),
                RecoveryContainerPath, binding, rootFolderId);
            var entries = await client.ListEntriesAsync();
            var entry = entries.Single(item => item.Kind == PilotEntryKind.File &&
                string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal));
            using var buffer = new MemoryStream();
            var result = await client.DownloadAsync(entry, buffer);
            return new AdapterRead(buffer.ToArray(), result);
        }

        private async Task<AdapterRead> ReadDriveRecoveryFileAsync(EndpointSpec spec, string rootFolderId,
            string relativePath)
        {
            var client = DriveRecoveryClient(spec, rootFolderId);
            var inventory = await client.ListInventoryAsync();
            var entry = inventory.Entries.Single(item => item.Kind == GoogleDriveEntryKind.Blob &&
                string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal));
            using var buffer = new MemoryStream();
            var result = await client.DownloadAsync(entry.ToRemoteEntry(), buffer);
            return new AdapterRead(buffer.ToArray(), result);
        }

        private GoogleDrivePilotClient DriveRecoveryClient(EndpointSpec spec, string rootFolderId)
        {
            var google = _googleBindings[spec.ConnectionId];
            return new GoogleDrivePilotClient(_http,
                new GoogleDriveAccessTokenSource(new FixtureGoogleCredentials(), new FixtureGoogleEndpoint(Wire),
                    google), google, rootFolderId, RecoveryContainerPath);
        }

        /// <summary>Every ancestor path of one Dropbox root, so its verified walk can terminate at "/".</summary>
        private static IEnumerable<string> AncestorPaths(string path)
        {
            var current = PilotPath.NormalizeDropboxRoot(path);
            while (true)
            {
                var index = current.LastIndexOf('/');
                if (index <= 0) break;
                current = current[..index];
                yield return current;
            }
        }

        internal static DropboxConnectionBinding DropboxConnection(EndpointSpec spec, bool primary)
        {
            var connectionId = DropboxConnectionBinding.CreateConnectionId(spec.AccountId);
            return new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, spec.AccountId,
                spec.RootNamespaceId, spec.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes,
                CredentialReference: primary
                    ? WindowsDropboxRefreshTokenStore.RegisteredTarget
                    : WindowsDropboxRefreshTokenStore.AccountTarget(connectionId)).Normalize();
        }

        internal static GoogleDriveConnectionBinding GoogleConnection(EndpointSpec spec) =>
            new GoogleDriveConnectionBinding("test-client",
                GoogleDriveConnectionBinding.CreateAccountKey(spec.AccountId), spec.AccountId,
                spec.AccountId + "@example.invalid", "Fixture Drive",
                [GoogleDriveProviderConfiguration.DriveScope], CredentialReference: "synthetic-fixture")
                .Normalize();

        /// <summary>Retains one pair definition exactly as the pair editor does before its roots are bound.</summary>
        internal async Task<HostPair> AddPairAsync(string tag, EndpointSpec left, EndpointSpec right,
            long policyRevision = 0, bool propagateDeletions = false, int? recoveryRetentionDays = null,
            SyncMode direction = SyncMode.TwoWay, bool seedDefaultPolicy = false)
        {
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(HostEndpoint(left),
                HostEndpoint(right), direction), "CC4A host " + tag);
            var pair = SyncPairStore.NormalizePair(draft);
            // A caller-supplied retained revision is applied to the retained definition BEFORE either the
            // configuration row or the definition is written, so the fixture pair really is retained at that
            // revision and no production seam is added to fake it.
            if (policyRevision > 0) pair = pair with { PolicyRevision = policyRevision };
            // A retained non-default deletion/retention policy, so a policy-preservation proof can assert the
            // pair's OWN requested values survive rather than the registration defaults.
            if (propagateDeletions) pair = pair with { PropagateDeletions = true };
            if (recoveryRetentionDays is { } retainedDays)
                pair = pair with { RecoveryRetentionDays = retainedDays };
            // Ordinary fixtures mirror the registered pair's policy. Only the dedicated
            // approval-migration proof requests a historical default revision explicitly.
            if (seedDefaultPolicy)
                await Db.RegisterPairAsync(pair.Id, pair.PolicyRevision);
            else
                await Db.RegisterPairAsync(pair.Id, pair.PolicyRevision,
                    SyncAuthorityPairConfiguration.FromPair(pair));
            await Db.RetainPairDefinitionAsync(pair, true);
            return new HostPair(pair, left, right, "root-" + tag + "-left", "root-" + tag + "-right");
        }

        internal SyncAuthorityRuntimeHost.CloudPairHostPrepareRequest Request(HostPair pair,
            bool createStaging = false, string? leftRoot = null, string? rightRoot = null) =>
            new(pair.Pair, leftRoot ?? pair.LeftRoot, rightRoot ?? pair.RightRoot, ReplacingPairId: pair.Id,
                CreatePrivateStaging: createStaging);
        
        internal Task<SyncAuthorityPreparedCloudPair> PrepareAsync(HostPair pair, bool createStaging = false,
            string? leftRoot = null, string? rightRoot = null)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The real-Host cloud pair binding proof requires the Windows private staging allocator.");
            return Host.PrepareCloudPairBindingAsync(Request(pair, createStaging, leftRoot, rightRoot));
        }

        /// <summary>The refusal text of a prepare that must not succeed, or null when it unexpectedly did.</summary>
        internal async Task<string?> RefusalAsync(
            SyncAuthorityRuntimeHost.CloudPairHostPrepareRequest request)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The real-Host cloud pair binding proof requires the Windows private staging allocator.");
            try
            {
                await Host.PrepareCloudPairBindingAsync(request);
                return null;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return error.GetType().Name + ": " + error.Message;
            }
        }

        internal async Task<string> ScalarAsync(string sql)
        {
            await using var connection =
                new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Db.DatabasePath);
            await connection.OpenAsync();
            string result;
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                var value = await command.ExecuteScalarAsync();
                result = value is null or DBNull
                    ? string.Empty
                    : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                      ?? string.Empty;
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return result;
        }

        internal async Task<List<string>> RowsAsync(string sql)
        {
            var rows = new List<string>();
            await using var connection =
                new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Db.DatabasePath);
            await connection.OpenAsync();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var parts = new List<string>();
                    for (var index = 0; index < reader.FieldCount; index++)
                        parts.Add(reader.IsDBNull(index)
                            ? string.Empty
                            : reader.GetValue(index).ToString() ?? string.Empty);
                    rows.Add(string.Join("|", parts));
                }
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return rows;
        }

        internal async Task<long> CountAsync(string sql) =>
            long.Parse(await ScalarAsync(sql), System.Globalization.CultureInfo.InvariantCulture);

        internal async Task<string> RootBindingAsync(string rootBindingId) =>
            await ScalarAsync("SELECT generation || '|' || identity_json || '|' || display_path || '|' || state "
                              + "FROM root_binding WHERE root_binding_id='" + rootBindingId + "';");

        internal async Task<string> RecoveryBindingAsync(string recoveryRootBindingId) =>
            await ScalarAsync("SELECT identity_json || '|' || display_path || '|' || status || '|' "
                              + "|| provider_corpus_id FROM recovery_root_binding WHERE "
                              + "recovery_root_binding_id='" + recoveryRootBindingId + "';");

        internal async Task<string> PairDefinitionAsync(string pairId) =>
            await ScalarAsync("SELECT configuration_revision || '|' || pair_json FROM sync_pair WHERE pair_id='"
                              + pairId + "';");

        /// <summary>Every retained authority row of this isolated installation, as exact comparable tuples.</summary>
        internal async Task<List<string>> AuthoritySnapshotAsync()
        {
            var rows = await RowsAsync(
                "SELECT 'root|' || root_binding_id || '|' || pair_id || '|' || side || '|' || generation || '|' "
                + "|| identity_json || '|' || display_path || '|' || state FROM root_binding "
                + "ORDER BY root_binding_id;");
            rows.AddRange(await RowsAsync(
                "SELECT 'recovery|' || recovery_root_binding_id || '|' || provider_account_id "
                + "|| '|' || provider_corpus_id || '|' || display_path || '|' || status "
                + "FROM recovery_root_binding ORDER BY recovery_root_binding_id;"));
            return rows;
        }

        /// <summary>The real consumer of a pair's staging identity: its retained control-namespace rows.</summary>
        internal Task<long> ControlNamespaceRowsAsync(string pairId) =>
            CountAsync("SELECT count(*) FROM pair_control_namespace WHERE pair_id='" + pairId + "';");

        /// <summary>Runs one exact statement against this rig's own isolated authority, for fixture state.</summary>
        internal async Task ExecuteSqlAsync(string sql)
        {
            await using var connection =
                new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Db.DatabasePath);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        /// <summary>Runs one parameterized statement against this rig's own isolated authority, for fixture state.</summary>
        internal async Task ExecuteSqlAsync(string sql, params (string Name, string Value)[] parameters)
        {
            await using var connection =
                new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Db.DatabasePath);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        /// <summary>
        /// Enables one retained pair through the same retained definition the application writes, so a
        /// default-route run selects it through ordinary configuration rather than a test switch.
        /// </summary>
        internal Task EnablePairAsync(SyncPair pair) =>
            Db.RetainPairDefinitionAsync(pair with { Enabled = true }, false);

        /// <summary>
        /// Retains one legacy local/cloud pair over a real local directory, exactly as the application's own
        /// registration leaves it: the production-shaped configuration row, the retained and enabled
        /// definition, and the NTFS volume plus local-root binding for that exact directory identity. The
        /// cloud side is an endpoint this rig already registered, so the Host prepares that side itself.
        /// </summary>
        internal async Task<SyncPair> AddLocalCloudPairAsync(EndpointSpec cloudSpec, string localRoot,
            string tag)
        {
            using var lease = PilotDirectoryLease.Open(localRoot);
            var binding = _dropboxBindings[cloudSpec.ConnectionId];
            var pair = SyncPair.FromPilot(new PilotSettings(localRoot, lease.Identity.VolumeSerialNumber,
                    lease.Identity.FileId, cloudSpec.FolderPath, cloudSpec.FolderId),
                "CC4A mixed " + tag, binding.RootNamespaceId) with
            {
                ConnectionId = binding.ConnectionId,
                InitialReviewApproved = false,
                Enabled = true
            };
            await Db.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
                pair.PropagateDeletions ? "recoverable" : "disabled", 0, 1, "manual_preserve_both",
                "content_times_basic_attrs", RecursiveMode: true, AutomaticPruning: false,
                RecoveryRetentionDays: pair.RecoveryRetentionDays ?? int.MaxValue));
            await Db.RetainPairDefinitionAsync(pair, select: false);
            // The volume and the local-root binding are deliberately NOT pre-registered: the real legacy
            // preparation path registers both itself from this exact retained definition and the real directory
            // identity, which is the production behaviour this fixture must exercise rather than replace.
            return pair;
        }
    }

    /// <summary>
    /// The synthetic provider transport the real adapters are driven through. Dropbox and Google Drive both
    /// answer on this one in-process handler. Tokens are synthetic and carry the exact connection they belong
    /// to, so each adapter's own account verification is still exercised for real, and every unexpected route,
    /// origin or identity fails the run.
    /// </summary>
    internal sealed class HostWire : HttpMessageHandler
    {
        private sealed record DropboxAccount(string AccountId, string RootNamespaceId, string HomeNamespaceId);

        /// <summary>
        /// The exact provider object identity this transport retained for one seeded file: the provider's own
        /// stable object id, that object's own revision and its own provider-issued content hash and length.
        /// </summary>
        internal sealed record SeededFile(string Provider, string ObjectId, string Revision, string ContentHash,
            long Size);

        /// <summary>
        /// One seeded Dropbox file object. Its own id, revision, namespace, display path and exact bytes stay
        /// together, so the object is only ever resolvable through the namespace and root it was seeded under
        /// and two seeded files never share an object identity or a revision.
        /// </summary>
        private sealed record DropboxFile(string Id, string NamespaceId, string Path, string Revision,
            string ContentHash, byte[] Bytes, string? ReportedId = null, long? ReportedSize = null,
            string? ReportedHash = null, string? ReportedPath = null);

        private sealed class DriveNode
        {
            internal required string Id { get; init; }
            internal required string Name { get; init; }
            internal string? ParentId { get; set; }
            internal string Version { get; set; } = "1";
            internal bool Trashed { get; set; }
            /// <summary>A folder node until one seeded byte file carries its own type and bytes.</summary>
            internal string MimeType { get; set; } = GoogleDrivePilotClient.FolderMimeType;
            internal byte[]? Bytes { get; set; }
            internal string? Sha256 { get; set; }
            /// <summary>A fault-seam override of the identity/metadata this node's own JSON reports.</summary>
            internal string? ReportedId { get; set; }
            internal long? ReportedSize { get; set; }
            internal string? ReportedSha256 { get; set; }
            internal string? ReportedParentId { get; set; }
        /// <summary>A fault-seam corpus (shared-drive) identity this node's own JSON reports.</summary>
        internal string? DriveId { get; set; }
        /// <summary>
        /// The provider-supplied browser location this node's own JSON reports as webViewLink. It is null
        /// unless a narrow additive fixture call supplies one, so every existing route keeps its exact
        /// behaviour and a web-navigation consumer sees only what the fixture explicitly gave it.
        /// </summary>
        internal string? Url { get; set; }
        }

        private readonly Dictionary<string, DropboxAccount> _dropboxNamespaces = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _dropboxPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Namespace, string Path)> _dropboxId =
            new(StringComparer.Ordinal);
        private readonly HashSet<string> _dropboxMissing = new(StringComparer.Ordinal);
        /// <summary>RootKey(namespace, display path) of every seeded Dropbox file, then its own object and revision.</summary>
        private readonly Dictionary<string, string> _dropboxFilePath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DropboxFile> _dropboxFileById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DropboxFile> _dropboxFileByRevision = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _drivePermission = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _driveRoot = new(StringComparer.Ordinal);
        // Real scheduled file workers publish independent provider objects concurrently. The fixture
        // must not lose/corrupt an acknowledged node through unsupported Dictionary writer races.
        private readonly ConcurrentDictionary<string, DriveNode> _driveNodes = new(StringComparer.Ordinal);
        private readonly HashSet<string> _driveMissing = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _driveBumpAfter = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _driveReads = new(StringComparer.Ordinal);
        private int _folderCounter;

        internal List<string> DropboxReads { get; } = [];
        internal List<string> DriveReads { get; } = [];

        /// <summary>Direct provider metadata reads of one Drive node, exposed for navigation read counting.</summary>
        internal int DriveNodeReadRequests { get; private set; }

        /// <summary>Drive name lookups over one folder's children, exposed for navigation read counting.</summary>
        internal int DriveNameLookupRequests { get; private set; }

        /// <summary>
        /// A narrow one-shot seam invoked exactly once, at the first drive node metadata read served after it
        /// is set, so a case can change fixture-owned stores DURING an awaited provider read. It is cleared
        /// before it runs and is null by default, so every existing route keeps its exact behaviour.
        /// </summary>
        internal Func<Task>? OnDriveNodeReadOnceAsync { get; set; }

        /// <summary>Every provider request this transport served, so a refusal can prove it reached none.</summary>
        internal int TotalRequests { get; private set; }
        /// <summary>
        /// The bounded one-shot manual-restore upload decline seam: the next upload request whose target path
        /// ends with this exact pair-relative path is declined BEFORE any transport mutation, and that decline
        /// travels back through the real adapter as the adapter contract's own conclusive unsupported signal.
        /// </summary>
        internal string? DeclineNextUploadForPath { get; set; }

        /// <summary>
        /// The bounded one-shot Google Drive same-name window: while armed, this exact node is hidden from the
        /// transport's own name lookups AND from its create duplicate-name check until one create has been
        /// served, after which the node is served again and both same-name objects are visible. Google names
        /// are not unique within a folder, so this models a real provider race rather than an error.
        /// </summary>
        internal string? HideDriveNodeUntilNextCreate { get; set; }
        private bool _driveHiddenWindowClosed;

        /// <summary>True while one node sits inside the armed one-shot hidden window.</summary>
        private bool DriveNodeHiddenByWindow(string id) => !_driveHiddenWindowClosed &&
            HideDriveNodeUntilNextCreate is { } hidden && string.Equals(hidden, id, StringComparison.Ordinal);
        internal HashSet<string> AllowedRecoveryDeletionIds { get; } = new(StringComparer.Ordinal);
        internal int RecoveryDeleteRequests { get; private set; }
        internal bool LoseNextRecoveryDeleteResponse { get; set; }
        internal Func<string, byte[], byte[]>? DownloadBodyForTest { get; set; }

        internal void ChangeRecoveryMember(string id)
        {
            if (_dropboxFileById.TryGetValue(id, out var file))
            {
                var changed = file with { Revision = file.Revision + "changed" };
                _dropboxFileById[id] = changed;
                _dropboxFileByRevision[changed.Revision] = changed;
            }
            else if (_driveNodes.TryGetValue(id, out var node))
                node.Version = (long.Parse(node.Version, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
            else throw new InvalidOperationException("No fixture Recovery member has that identity.");
        }

        /// <summary>
        /// The one field of a retained Recovery member's provider response a fault seam changes, so the real
        /// adapter's own validation reads back a response that no longer matches the retained identity.
        /// </summary>
        internal enum RecoveryMemberFault
        {
            NativeId,
            Size,
            Checksum,
            Membership,
            Corpus
        }

        private readonly Dictionary<string, DropboxFile> _dropboxMemberFaults = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string? Id, long? Size, string? Hash, string? Parent, string? Corpus)>
            _driveMemberFaults = new(StringComparer.Ordinal);

        /// <summary>
        /// Changes one retained Dropbox Recovery member's reported provider metadata while keeping its own
        /// stored object id, revision, bytes and recorded path index, so only the adapter's readback changes.
        /// Each fault is applied to the member as retained, and the caller restores it with the clear method.
        /// </summary>
        internal void FaultDropboxRecoveryMember(string id, RecoveryMemberFault fault)
        {
            if (!_dropboxFileById.TryGetValue(id, out var file))
                throw new InvalidOperationException("No fixture Dropbox Recovery member has that identity.");
            var original = _dropboxMemberFaults.TryGetValue(id, out var snapshot) ? snapshot : file;
            _dropboxMemberFaults[id] = original;
            var path = PilotPath.NormalizeDropboxRoot(original.Path);
            var faulted = fault switch
            {
                RecoveryMemberFault.NativeId => original with { ReportedId = original.Id + "-moved" },
                RecoveryMemberFault.Size => original with { ReportedSize = original.Bytes.LongLength + 1 },
                RecoveryMemberFault.Checksum => original with { ReportedHash = new string('0', 64) },
                RecoveryMemberFault.Membership => original with
                {
                    ReportedPath = "/Outside recovery/" + path[(path.LastIndexOf('/') + 1)..]
                },
                _ => throw new ArgumentOutOfRangeException(nameof(fault),
                    "Dropbox has no retained Recovery member fault of that kind.")
            };
            _dropboxFileById[id] = faulted;
            _dropboxFileByRevision[original.Revision] = faulted;
        }

        /// <summary>Restores the retained Dropbox Recovery member's own reported provider metadata.</summary>
        internal void ClearDropboxRecoveryMemberFault(string id)
        {
            if (!_dropboxMemberFaults.Remove(id, out var original)) return;
            _dropboxFileById[id] = original;
            _dropboxFileByRevision[original.Revision] = original;
        }

        /// <summary>
        /// Changes one retained Google Drive Recovery member's reported provider metadata, keeping its own
        /// stored id, version, bytes and parent, and its recovery-root registration. The corpus overload also
        /// reports a shared-drive identity so the adapter's own corpus binding check is exercised.
        /// </summary>
        internal void FaultDriveRecoveryMember(string id, RecoveryMemberFault fault, string? corpusId = null)
        {
            if (!_driveNodes.TryGetValue(id, out var node))
                throw new InvalidOperationException("No fixture Google Drive Recovery member has that identity.");
            if (!_driveMemberFaults.ContainsKey(id))
                _driveMemberFaults[id] = (node.ReportedId, node.ReportedSize, node.ReportedSha256,
                    node.ReportedParentId, node.DriveId);
            node.ReportedId = null;
            node.ReportedSize = null;
            node.ReportedSha256 = null;
            node.ReportedParentId = null;
            node.DriveId = null;
            switch (fault)
            {
                case RecoveryMemberFault.NativeId: node.ReportedId = node.Id + "-moved"; break;
                case RecoveryMemberFault.Size: node.ReportedSize = (node.Bytes?.LongLength ?? 0) + 1; break;
                case RecoveryMemberFault.Checksum: node.ReportedSha256 = new string('0', 64); break;
                case RecoveryMemberFault.Membership: node.ReportedParentId = "drive-folder-outside-recovery"; break;
                case RecoveryMemberFault.Corpus:
                    node.DriveId = string.IsNullOrWhiteSpace(corpusId) ? "drive-corpus-moved" : corpusId;
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(fault));
            }
        }

        /// <summary>Restores the retained Google Drive Recovery member's own reported provider metadata.</summary>
        internal void ClearDriveRecoveryMemberFault(string id)
        {
            if (!_driveMemberFaults.Remove(id, out var original) || !_driveNodes.TryGetValue(id, out var node)) return;
            node.ReportedId = original.Id;
            node.ReportedSize = original.Size;
            node.ReportedSha256 = original.Hash;
            node.ReportedParentId = original.Parent;
            node.DriveId = original.Corpus;
        }

        /// <summary>
        /// The account identity the synthetic Dropbox transport reports for its current-account read, so the
        /// real adapter's own account/namespace binding check can be exercised against a provider-side change.
        /// </summary>
        internal (string AccountId, string RootNamespaceId, string HomeNamespaceId)? CurrentAccountOverride { get; set; }

        /// <summary>
        /// A bounded transport stream for one retained Recovery member's content read, so the real adapter reads
        /// through it while the response still declares the whole object's own metadata and length.
        /// </summary>
        internal Func<string, Stream?>? RecoveryTransportStreamForTest { get; set; }

        /// <summary>The response content for one Recovery content read: a fault stream when armed, else the body.</summary>
        private HttpContent RecoveryContent(string id, byte[] bytes) =>
            RecoveryTransportStreamForTest?.Invoke(id) is { } stream
                ? new FaultyStreamContent(stream)
                : new ByteArrayContent(DownloadBodyForTest?.Invoke(id, bytes) ?? bytes);

        /// <summary>
        /// Serves one fault-seam transport stream through exactly the read the real adapter performs, so a
        /// failure or cancellation after the first bytes is observed in the adapter's own streaming branch.
        /// </summary>
        private sealed class FaultyStreamContent(Stream stream) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream target, System.Net.TransportContext? context) =>
                stream.CopyToAsync(target);

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }

            protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        }

        private void MaybeLoseRecoveryDeleteResponse()
        {
            if (!LoseNextRecoveryDeleteResponse) return;
            LoseNextRecoveryDeleteResponse = false;
            throw new HttpRequestException("Synthetic response lost after conditional Recovery removal.");
        }

        /// <summary>
        /// The exact provider WRITE effects this transport served, kept apart from every read so a no-repeat
        /// obligation observes real create traffic rather than silence. A write request is counted when it is
        /// received, before any validation, so even a refused write attempt is visible.
        /// </summary>
        internal sealed record WriteEffects(int DropboxUploads, int DriveCreates)
        {
            internal int Total => DropboxUploads + DriveCreates;
        }

        private int _dropboxUploadRequests;
        private int _driveCreateRequests;
        private int _driveMultipartCreateRequests;
        private int _dropboxDownloadRequests;
        private int _dropboxRangeDownloadRequests;
        private int _driveMediaReadRequests;
        private int _dropboxSimpleUploadRequests;
        private readonly DropboxUploadSessionContractHandler _nativeDropboxSessions = new();
        private HttpMessageInvoker? _nativeDropboxSessionInvoker;

        // Explicit external-I/O capability for the default-size full-Host proof only. Existing fixtures
        // continue to reject every session route when this exact destination binding is absent.
        internal (string NamespaceId, string FolderId)? NativeDropboxSessionTargetForTest { get; set; }
        internal int NativeDropboxSessionStarts => _nativeDropboxSessions.StartCount;
        internal int NativeDropboxSessionChunks => _nativeDropboxSessions.StartCount +
            _nativeDropboxSessions.AppendCount + _nativeDropboxSessions.FinishCount;
        internal int SimpleDropboxUploadRequestsForTest => _dropboxSimpleUploadRequests;

        /// <summary>The retained write effects this transport has served so far.</summary>
        internal WriteEffects Writes => new(_dropboxUploadRequests, _driveCreateRequests);

        /// <summary>
        /// Sends one exact request straight to this transport, so a route-surface obligation can be observed
        /// at the transport boundary itself even when no production adapter would issue that request.
        /// </summary>
        internal Task<HttpResponseMessage> SendDirectAsync(HttpRequestMessage request) =>
            SendAsync(request, CancellationToken.None);

        /// <summary>The folder identity this transport retains for one exact namespace and Dropbox path.</summary>
        internal string? RetainedDropboxFolderId(string rootNamespaceId, string path) =>
            _dropboxPath.TryGetValue(RootKey(rootNamespaceId, PilotPath.NormalizeDropboxRoot(path)), out var id)
                ? id
                : null;

        /// <summary>The folder identity this transport retains for one exact account path, walked by name.</summary>
        internal string? RetainedDriveFolderId(string permissionId, string path)
        {
            var current = _driveRoot[permissionId];
            foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = _driveNodes.Values.FirstOrDefault(node => node.Bytes is null && !node.Trashed &&
                    string.Equals(node.ParentId, current, StringComparison.Ordinal) &&
                    string.Equals(node.Name, segment, StringComparison.Ordinal));
                if (match is null) return null;
                current = match.Id;
            }
            return current;
        }

        /// <summary>The Dropbox small-file content uploads this transport received.</summary>
        internal int DropboxUploadRequests => _dropboxUploadRequests;

        /// <summary>Every write request this transport received at the Drive upload root, served or refused.</summary>
        internal int DriveCreateRequests => _driveCreateRequests;

        /// <summary>The Drive multipart creates this transport actually served.</summary>
        internal int DriveMultipartCreateRequests => _driveMultipartCreateRequests;

        /// <summary>Every Dropbox content read served, ranged or whole.</summary>
        internal int DropboxDownloadRequests => _dropboxDownloadRequests;

        /// <summary>The Dropbox content reads that asked for an exact byte range.</summary>
        internal int DropboxRangeDownloadRequests => _dropboxRangeDownloadRequests;

        /// <summary>Every Drive media read served.</summary>
        internal int DriveMediaReadRequests => _driveMediaReadRequests;

        private int _dropboxUpdateRequests;
        private int _driveUpdateRequests;
        private int _dropboxRecoveryCopyRequests;
        private int _driveRecoveryCopyRequests;

        /// <summary>
        /// The conditional target updates and the recovery copies this transport served or refused, kept apart
        /// from creates, downloads and every other kind of traffic so a no-replay obligation observes real
        /// effects rather than silence.
        /// </summary>
        internal sealed record TargetWrites(int DropboxUpdates, int DriveUpdates, int DropboxRecoveryCopies,
            int DriveRecoveryCopies)
        {
            internal int Total => DropboxUpdates + DriveUpdates + DropboxRecoveryCopies + DriveRecoveryCopies;
        }

        /// <summary>The conditional updates and recovery copies served so far.</summary>
        internal TargetWrites Updates => new(_dropboxUpdateRequests, _driveUpdateRequests,
            _dropboxRecoveryCopyRequests, _driveRecoveryCopyRequests);

        /// <summary>Conditional Dropbox uploads received, applied or refused.</summary>
        internal int DropboxUpdateRequests => _dropboxUpdateRequests;

        /// <summary>Conditional Drive media updates received, applied or refused.</summary>
        internal int DriveUpdateRequests => _driveUpdateRequests;

        /// <summary>Dropbox recovery copies received: the add-mode write into the product recovery container.</summary>
        internal int DropboxRecoveryCopyRequests => _dropboxRecoveryCopyRequests;

        /// <summary>Drive recovery copies received: the server-side copy of the predecessor object.</summary>
        internal int DriveRecoveryCopyRequests => _driveRecoveryCopyRequests;

        internal static string RefreshTokenFor(DropboxConnectionBinding binding) =>
            "dbrefresh|" + binding.AccountId + "|" + binding.RootNamespaceId + "|" + binding.HomeNamespaceId;

        internal static string GoogleRefreshFor(string connectionId) => "grefresh:" + connectionId;

        /// <summary>The one fixed small nonempty text file this transport seeds on each provider side.</summary>
        internal const string DropboxSeededFileName = "sync-shark-cc4b-dropbox.txt";
        internal const string DropboxSeededText = "Sync Shark CC4B seeded Dropbox content (nonempty).\n";
        internal const string DriveSeededFileName = "sync-shark-cc4b-drive.txt";
        internal const string DriveSeededText = "Sync Shark CC4B seeded Google Drive content (nonempty).\n";

        internal string DriveRootId(string permissionId) => _driveRoot[permissionId];

        internal string GooglePermissionFor(string accessToken)
        {
            if (!accessToken.StartsWith("gaccess:", StringComparison.Ordinal))
                throw new InvalidDataException("The synthetic Google transport refuses an unknown access token.");
            var connectionId = accessToken["gaccess:".Length..];
            return _drivePermission.TryGetValue(connectionId, out var permission)
                ? permission
                : throw new InvalidDataException("The synthetic Google transport has no account for that token.");
        }

        internal string GooglePermissionForRefresh(string refreshToken)
        {
            if (!refreshToken.StartsWith("grefresh:", StringComparison.Ordinal))
                throw new InvalidDataException("The synthetic Google endpoint refuses an unknown refresh token.");
            var connectionId = refreshToken["grefresh:".Length..];
            return _drivePermission.TryGetValue(connectionId, out var permission)
                ? permission
                : throw new InvalidDataException("The synthetic Google endpoint has no account for that token.");
        }

        internal void AddDropboxFolder(DropboxConnectionBinding binding, string path, string folderId)
        {
            _dropboxNamespaces[binding.RootNamespaceId] = new DropboxAccount(binding.AccountId,
                binding.RootNamespaceId, binding.HomeNamespaceId);
            PutDropboxFolder(binding.RootNamespaceId, PilotPath.NormalizeDropboxRoot(path), folderId);
        }

        private void PutDropboxFolder(string rootNamespace, string path, string folderId)
        {
            _dropboxPath[RootKey(rootNamespace, path)] = folderId;
            _dropboxId[folderId] = (rootNamespace, path);
        }

        private static string RootKey(string rootNamespace, string path) => rootNamespace + "::" + path;

        /// <summary>
        /// Seeds the one fixed small nonempty text file this transport serves inside an already registered
        /// Dropbox root. The object's id and revision are derived from its own retained namespace and display
        /// path, so every seeded file keeps its own object identity and revision and is only resolvable through
        /// the namespace it was seeded under.
        /// </summary>
        internal SeededFile AddDropboxFile(DropboxConnectionBinding binding, string path, string text)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            var bytes = Encoding.UTF8.GetBytes(text);
            var suffix = IdentitySuffix(binding.RootNamespaceId, normalized);
            var contentHash = DropboxContentHash.Compute(bytes);
            // The object keeps its own stable identity, while its revision follows its content: a re-seeded
            // file is a genuinely changed provider revision, never the same revision with different bytes.
            var file = new DropboxFile("id:host-file-" + suffix, binding.RootNamespaceId, normalized,
                IdentitySuffix("rev:" + suffix, contentHash), contentHash, bytes);
            _dropboxFilePath[RootKey(file.NamespaceId, normalized)] = file.Id;
            _dropboxFileById[file.Id] = file;
            _dropboxFileByRevision[file.Revision] = file;
            return new SeededFile(SyncProviders.Dropbox, file.Id, file.Revision, file.ContentHash,
                bytes.LongLength);
        }

        // Restore only a previously observed synthetic transport object, preserving its exact
        // bytes/revision. This is input seeding, not a replacement for product reconciliation.
        internal void RestoreDropboxFixtureFile(DropboxConnectionBinding binding, string path,
            string fileId, string revision, string contentHash, byte[] bytes)
        {
            if ((!fileId.StartsWith("id:host-created-file-", StringComparison.Ordinal) &&
                 !fileId.StartsWith("id:host-file-", StringComparison.Ordinal)) ||
                string.IsNullOrWhiteSpace(revision) || DropboxContentHash.Compute(bytes) != contentHash)
                throw new InvalidDataException("Only exact retained synthetic file identity and bytes may be restored.");
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            var file = new DropboxFile(fileId, binding.RootNamespaceId, normalized, revision, contentHash, bytes);
            _dropboxFilePath[RootKey(file.NamespaceId, normalized)] = file.Id;
            _dropboxFileById[file.Id] = file;
            _dropboxFileByRevision[file.Revision] = file;
        }

        internal byte[] ReadDropboxFixtureBytes(DropboxConnectionBinding binding, string path)
        {
            var id = _dropboxFilePath[RootKey(binding.RootNamespaceId, PilotPath.NormalizeDropboxRoot(path))];
            return _dropboxFileById[id].Bytes.ToArray();
        }

        /// <summary>
        /// The stable per-object identity suffix of one seeded file: derived from that file's own provider
        /// scope and path, so two distinct objects never share an identity and one object keeps one identity.
        /// </summary>
        private static string IdentitySuffix(string scope, string path) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope + "\n" + path)))
                .ToLowerInvariant()[..16];

        /// <summary>A retained root whose provider folder is moved: the retained path becomes stale evidence.</summary>
        internal void MoveDropboxFolder(string folderId, string newPath)
        {
            var node = _dropboxId[folderId];
            _dropboxPath.Remove(RootKey(node.Namespace, node.Path));
            PutDropboxFolder(node.Namespace, PilotPath.NormalizeDropboxRoot(newPath), folderId);
        }

        /// <summary>A candidate or retained root whose provider folder is no longer served.</summary>
        internal void SetDropboxMissing(string folderId, bool missing)
        {
            if (missing) _dropboxMissing.Add(folderId);
            else _dropboxMissing.Remove(folderId);
        }

        internal void AddDriveAccount(GoogleDriveConnectionBinding binding, string rootId)
        {
            _drivePermission[binding.ConnectionId] = binding.PermissionId;
            _driveRoot[binding.PermissionId] = rootId;
            _driveNodes[rootId] = new DriveNode { Id = rootId, Name = "My Drive" };
        }

        internal void AddDriveFolder(GoogleDriveConnectionBinding binding, string path, string folderId,
            string? webViewLink = null)
        {
            var parentId = _driveRoot[binding.PermissionId];
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var last = index == segments.Length - 1;
                var existing = _driveNodes.Values.FirstOrDefault(item =>
                    string.Equals(item.ParentId, parentId, StringComparison.Ordinal) &&
                    string.Equals(item.Name, segments[index], StringComparison.Ordinal));
                if (existing is null)
                {
                    var id = last ? folderId : "drive-folder-" + (++_folderCounter);
                    existing = new DriveNode
                    {
                        Id = id, Name = segments[index], ParentId = parentId,
                        Url = last ? webViewLink : null
                    };
                    _driveNodes[id] = existing;
                }
                else if (last && webViewLink is not null)
                {
                    // Re-registering an existing folder with its own provider-supplied location is additive:
                    // every other field of that node stays exactly as it was.
                    existing.Url = webViewLink;
                }
                parentId = existing.Id;
            }
        }

        /// <summary>
        /// Seeds the one fixed small nonempty text file this transport serves inside an already registered
        /// Drive root. The object carries its own id, its own version and the provider-issued whole-file
        /// SHA-256 of exactly its own bytes, and it keeps the parent folder it was seeded under.
        /// </summary>
        internal SeededFile AddDriveFile(GoogleDriveConnectionBinding binding, string path, string text,
            string? webViewLink = null)
        {
            var parentId = _driveRoot[binding.PermissionId];
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 2)
                throw new InvalidDataException("A seeded Drive file needs its own name inside a registered root.");
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var folder = _driveNodes.Values.FirstOrDefault(item => item.Bytes is null &&
                    string.Equals(item.ParentId, parentId, StringComparison.Ordinal) &&
                    string.Equals(item.Name, segments[index], StringComparison.Ordinal));
                if (folder is null)
                    throw new InvalidDataException(
                        "The seeded Drive file's parent folder is not registered: " + segments[index]);
                parentId = folder.Id;
            }
            var bytes = Encoding.UTF8.GetBytes(text);
            var suffix = IdentitySuffix(binding.PermissionId, PilotPath.NormalizeDropboxRoot(path));
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var node = new DriveNode
            {
                Id = "drive-file-" + suffix,
                Name = segments[^1],
                ParentId = parentId,
                // The object keeps its own stable identity, while its version follows its content: a re-seeded
                // file is a genuinely changed provider version, never the same version with different bytes.
                Version = Convert.ToInt32(IdentitySuffix("ver:" + suffix, sha256)[..7], 16)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                MimeType = "text/plain",
                Bytes = bytes,
                Sha256 = sha256,
                Url = webViewLink
            };
            _driveNodes[node.Id] = node;
            return new SeededFile(SyncProviders.GoogleDrive, node.Id, node.Version, sha256, bytes.LongLength);
        }

        internal void SetDriveMissing(string folderId, bool missing)
        {
            if (missing) _driveMissing.Add(folderId);
            else _driveMissing.Remove(folderId);
        }

        /// <summary>
        /// Default-off fault seam: records the named seeded Drive file as a Google-native document with no byte
        /// content, so an unsupported-native-content read can be proved against the real adapter. It changes
        /// nothing until it is called and adds no production path.
        /// </summary>
        internal void MakeDriveNodeNativeDocument(string fileId)
        {
            if (!_driveNodes.TryGetValue(fileId, out var node))
                throw new InvalidOperationException("No fixture Drive node has that identity.");
            node.MimeType = "application/vnd.google-apps.document";
            node.Bytes = null;
            node.Sha256 = null;
        }

        /// <summary>
        /// Arms one node to change its provider version after the given number of metadata reads, so the
        /// adapter's two fresh readback passes disagree and the ancestry is refused as stale.
        /// </summary>
        internal void DriftDriveVersion(string folderId, int afterReads) =>
            _driveBumpAfter[folderId] = afterReads;

        private DriveNode RequireDriveNode(string fileId)
        {
            if (!_driveNodes.TryGetValue(fileId, out var node) || _driveMissing.Contains(fileId))
                throw new GoogleDriveApiException(System.Net.HttpStatusCode.NotFound,
                    "The synthetic Google transport no longer serves that object identity.");
            var seen = _driveReads.TryGetValue(fileId, out var count) ? count + 1 : 1;
            _driveReads[fileId] = seen;
            if (_driveBumpAfter.TryGetValue(fileId, out var after) && seen > after)
                node.Version = (int.Parse(node.Version, System.Globalization.CultureInfo.InvariantCulture) + 1)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return node;
        }

        /// <summary>
        /// Test-only provider INPUT failure injection. It is inert unless a fixture sets it, and it fails the
        /// provider READ so the real publication decision fails closed on real provider input; it never
        /// replaces or bypasses a guard, commit or publication decision.
        /// </summary>
        internal Func<HttpRequestMessage, bool>? ProviderInputFailureForTest { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProviderInputFailureForTest is { } injectedFailure && injectedFailure(request))
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error_summary\":\"synthetic provider input failure\"}")
                };
            TotalRequests++;
            var uri = request.RequestUri!;
            if (string.Equals(uri.Host, "api.dropboxapi.com", StringComparison.Ordinal))
                return await DropboxAsync(request, uri, cancellationToken);
            if (string.Equals(uri.Host, "content.dropboxapi.com", StringComparison.Ordinal))
                return await DropboxAsync(request, uri, cancellationToken);
            if (string.Equals(uri.Host, "www.googleapis.com", StringComparison.Ordinal))
                return await DriveAsync(request, uri, cancellationToken);
            throw new InvalidDataException("The synthetic provider transport refuses the origin " + uri.Host + ".");
        }

        private async Task<HttpResponseMessage> DropboxAsync(HttpRequestMessage request, Uri uri,
            CancellationToken cancellationToken)
        {
            var route = uri.AbsolutePath;
            if (request.Method != HttpMethod.Post)
                throw new InvalidDataException("The synthetic Dropbox transport serves POST only.");
            var contentHost = string.Equals(uri.Host, "content.dropboxapi.com", StringComparison.Ordinal);
            if (route == "/oauth2/token")
            {
                var form = ParseForm(await request.Content!.ReadAsStringAsync(cancellationToken));
                var account = ParseDropboxToken(form["refresh_token"]);
                return Json("{" + Q("access_token") + ":" + Q(DropboxAccess(account)) + "," +
                            Q("account_id") + ":" + Q(account.AccountId) + "," + Q("expires_in") + ":14400," +
                            Q("scope") + ":" +
                            Q(string.Join(' ', DropboxProviderConfiguration.RequiredScopes)) + "}");
            }
            var bearer = request.Headers.Authorization?.Parameter
                         ?? throw new InvalidDataException("The synthetic Dropbox transport requires a bearer.");
            var caller = ParseDropboxToken(bearer);
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            {
                var account = CurrentAccountOverride ??
                              (caller.AccountId, caller.RootNamespaceId, caller.HomeNamespaceId);
                return Json("{" + Q("account_id") + ":" + Q(account.AccountId) + "," + Q("root_info") + ":{" +
                            Q("root_namespace_id") + ":" + Q(account.RootNamespaceId) + "," +
                            Q("home_namespace_id") + ":" + Q(account.HomeNamespaceId) + "}}");
            }
            // The real adapter addresses the content host for exactly one acquisition route: the revision-pinned
            // small-file read whose argument travels in the Dropbox-API-Arg header and whose metadata comes back
            // in Dropbox-API-Result. No other content route exists for this lane.
            if (contentHost)
            {
                if (route.EndsWith("/files/download", StringComparison.Ordinal))
                    return DropboxDownload(request, caller);
                if (route.EndsWith("/files/upload", StringComparison.Ordinal))
                    return await DropboxUploadAsync(request, caller, cancellationToken);
                if (route is "/2/files/upload_session/start" or "/2/files/upload_session/append_v2" or
                    "/2/files/upload_session/finish")
                    return await NativeDropboxSessionAsync(request, caller, cancellationToken);
                throw new InvalidDataException(
                    "The synthetic Dropbox content transport refuses the route " + route + ".");
            }
            var rootNamespace = PathRoot(request) ?? caller.RootNamespaceId;
            if (route.EndsWith("/files/delete_v2", StringComparison.Ordinal))
            {
                RecoveryDeleteRequests++;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var id = body.RootElement.GetProperty("path").GetString()!;
                var revision = body.RootElement.GetProperty("parent_rev").GetString();
                if (!AllowedRecoveryDeletionIds.Contains(id) || !_dropboxFileById.TryGetValue(id, out var file) ||
                    file.NamespaceId != rootNamespace || file.Revision != revision || _dropboxMissing.Contains(id))
                    return NotFound();
                _dropboxMissing.Add(id);
                _dropboxFilePath.Remove(RootKey(file.NamespaceId, file.Path));
                MaybeLoseRecoveryDeleteResponse();
                return Json("{" + Q("metadata") + ":" + DropboxFileJson(file) + "}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
                DropboxReads.Add(rootNamespace + "::" + path);
                var file = ResolveDropboxFile(rootNamespace, path);
                if (file is not null && !_dropboxMissing.Contains(file.Id)) return Json(DropboxFileJson(file));
                var node = ResolveDropbox(rootNamespace, path);
                return node is { } found && !_dropboxMissing.Contains(found.Id)
                    ? Json(DropboxFolderJson(found.Id, found.Path))
                    : MissingMetadataPath();
            }
            if (route.EndsWith("/files/list_folder/get_latest_cursor", StringComparison.Ordinal))
                return Json("{\"cursor\":" + Q("synthetic-monitor-" + rootNamespace) + "}");
            if (route.EndsWith("/files/list_folder/continue", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var expected = "synthetic-monitor-" + rootNamespace;
                if (body.RootElement.GetProperty("cursor").GetString() != expected)
                    throw new InvalidDataException("The synthetic monitor cursor belongs to another Dropbox endpoint account.");
                return Json("{\"entries\":[],\"cursor\":" + Q(expected) + ",\"has_more\":false}");
            }
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.TryGetProperty("path", out var pathValue)
                    ? pathValue.GetString() ?? string.Empty
                    : string.Empty;
                var recursive = body.RootElement.TryGetProperty("recursive", out var recursiveValue) &&
                                recursiveValue.ValueKind == JsonValueKind.True;
                var parent = BrowsePath(rootNamespace, path);
                var entries = _dropboxId
                    .Where(item => string.Equals(item.Value.Namespace, rootNamespace, StringComparison.Ordinal) &&
                                   InListedScope(item.Value.Path, parent, recursive) &&
                                   !_dropboxMissing.Contains(item.Key))
                    .Select(item => DropboxFolderJson(item.Key, item.Value.Path))
                    .Concat(_dropboxFileById.Values
                        .Where(file => string.Equals(file.NamespaceId, rootNamespace, StringComparison.Ordinal) &&
                                       InListedScope(file.Path, parent, recursive) &&
                                       !_dropboxMissing.Contains(file.Id))
                        .Select(DropboxFileJson));
                return Json("{" + Q("entries") + ":[" + string.Join(',', entries) + "]," + Q("cursor") + ":" +
                            Q("synthetic-cursor") + "," + Q("has_more") + ":false}");
            }
            if (route.EndsWith("/files/create_folder_v2", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = body.RootElement.GetProperty("path").GetString() ?? string.Empty;
                var (parentPath, name) = SplitCreatePath(rootNamespace, path);
                var created = (parentPath == "/" ? string.Empty : parentPath) + "/" + name;
                var id = "id:host-created-" + (++_folderCounter);
                PutDropboxFolder(rootNamespace, PilotPath.NormalizeDropboxRoot(created), id);
                return Json("{" + Q("metadata") + ":" + DropboxFolderJson(id, created) + "}");
            }
            throw new InvalidDataException("The synthetic Dropbox transport refuses the route " + route + ".");
        }

        private async Task<HttpResponseMessage> DriveAsync(HttpRequestMessage request, Uri uri,
            CancellationToken cancellationToken)
        {
            var path = uri.AbsolutePath;
            var query = ParseQuery(uri.Query);
            var bearer = request.Headers.Authorization?.Parameter
                         ?? throw new InvalidDataException("The synthetic Google transport requires a bearer.");
            var permission = GooglePermissionFor(bearer);
            if (path.StartsWith("/upload/", StringComparison.Ordinal))
                return await DriveCreateAsync(request, permission, path, query, cancellationToken);
            // The conditional write guard the real adapter reads before it may replace an existing object.
            if (path.StartsWith("/drive/v2/files/", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Patch)
                {
                    RecoveryDeleteRequests++;
                    var id = Uri.UnescapeDataString(path["/drive/v2/files/".Length..]);
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    if (!AllowedRecoveryDeletionIds.Contains(id) || !_driveNodes.TryGetValue(id, out var node) ||
                        node.Bytes is null || !BelongsToDriveRoot(id, rootId: _driveRoot[permission]) ||
                        body.RootElement.GetProperty("labels").GetProperty("trashed").ValueKind != JsonValueKind.True ||
                        request.Headers.IfMatch.Count != 1 ||
                        request.Headers.IfMatch.First().Tag != "\"" + DriveGuardTag(node) + "\"")
                        return DriveRefusal(System.Net.HttpStatusCode.PreconditionFailed, "Exact Recovery deletion guard required.");
                    node.Trashed = true;
                    node.Version = (long.Parse(node.Version, CultureInfo.InvariantCulture) + 1).ToString(CultureInfo.InvariantCulture);
                    MaybeLoseRecoveryDeleteResponse();
                }
                return DriveConditionalGuard(permission, path);
            }
            if (path.EndsWith("/about", StringComparison.Ordinal))
            {
                DriveReads.Add(permission + "::about");
                return Json("{" + Q("user") + ":{" + Q("permissionId") + ":" + Q(permission) + "," +
                            Q("emailAddress") + ":" + Q(permission + "@example.invalid") + "," +
                            Q("displayName") + ":" + Q("Fixture Drive") + "}," + Q("storageQuota") + ":{" +
                            Q("limit") + ":" + Q("107374182400") + "," + Q("usage") + ":" + Q("1024") + "}}");
            }
            var rootId = _driveRoot[permission];
            if (path.EndsWith("/changes/startPageToken", StringComparison.Ordinal))
                return Json("{\"startPageToken\":" + Q("synthetic-monitor-" + permission) + "}");
            if (path.EndsWith("/changes", StringComparison.Ordinal))
            {
                var expected = "synthetic-monitor-" + permission;
                if (query.GetValueOrDefault("pageToken") != expected)
                    throw new InvalidDataException("The synthetic monitor cursor belongs to another Google endpoint account.");
                return Json("{\"changes\":[],\"newStartPageToken\":" + Q(expected) + "}");
            }
            if (path.EndsWith("/files/root", StringComparison.Ordinal))
            {
                DriveReads.Add(permission + "::files/root");
                return Json("{" + Q("id") + ":" + Q(rootId) + "," + Q("mimeType") + ":" +
                            Q(GoogleDrivePilotClient.FolderMimeType) + "}");
            }
            if (path.EndsWith("/files", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Post)
                {
                    using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var name = body.RootElement.GetProperty("name").GetString() ?? "Folder";
                    var parents = body.RootElement.TryGetProperty("parents", out var parentValues) &&
                                  parentValues.ValueKind == JsonValueKind.Array
                        ? parentValues[0].GetString() ?? rootId
                        : rootId;
                    // The My Drive alias is never a stable provider identity: store the real root id.
                    if (string.Equals(parents, "root", StringComparison.Ordinal)) parents = rootId;
                    var id = "drive-folder-" + (++_folderCounter);
                    var node = new DriveNode { Id = id, Name = name, ParentId = parents };
                    _driveNodes[id] = node;
                    return Json(DriveNodeJson(node));
                }
                var queryText = query.GetValueOrDefault("q", string.Empty);
                var parent = ExtractBetween(queryText, "'", "' in parents") ?? rootId;
                if (string.Equals(parent, "root", StringComparison.Ordinal)) parent = rootId;
                var nameFilter = ExtractBetween(queryText, "name = '", "'");
                DriveNameLookupRequests++;
                var matches = _driveNodes.Values
                    .Where(item => string.Equals(item.ParentId, parent, StringComparison.Ordinal) &&
                                   !item.Trashed && !_driveMissing.Contains(item.Id) &&
                                   !DriveNodeHiddenByWindow(item.Id) &&
                                   (nameFilter is null ||
                                    string.Equals(item.Name, nameFilter, StringComparison.Ordinal)))
                    .OrderBy(item => item.Name, StringComparer.Ordinal)
                    .Select(DriveNodeJson);
                return Json("{" + Q("files") + ":[" + string.Join(',', matches) + "]}");
            }
            var remainder = path[(path.IndexOf("/files/", StringComparison.Ordinal) + 7)..];
            var fileId = Uri.UnescapeDataString(remainder.Split('/')[0]);
            if (path.EndsWith("/copy", StringComparison.Ordinal))
                return await DriveCopyAsync(request, permission, fileId, cancellationToken);
            var driveNode = RequireDriveNode(fileId);
            // The real adapter's one content read. The media route carries raw bytes and nothing else, and it
            // serves them for the seeded byte file's own identity only.
            if (string.Equals(query.GetValueOrDefault("alt"), "media", StringComparison.Ordinal))
            {
                if (driveNode.Bytes is null)
                    throw new GoogleDriveApiException(System.Net.HttpStatusCode.NotFound,
                        "The synthetic Google transport serves no media bytes for this object identity.");
                _driveMediaReadRequests++;
                DriveReads.Add(permission + "::files/" + fileId + "?alt=media");
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = RecoveryContent(fileId, driveNode.Bytes)
                };
            }
            DriveReads.Add(permission + "::files/" + fileId);
            DriveNodeReadRequests++;
            // A narrow one-shot seam so a case can change fixture-owned state DURING an awaited provider read;
            // it fires at most once and is cleared before it runs, so no later read can re-fire it.
            var duringRead = OnDriveNodeReadOnceAsync;
            OnDriveNodeReadOnceAsync = null;
            if (duringRead is not null) await duringRead();
            return Json(DriveNodeJson(driveNode));
        }

        /// <summary>
        /// The one small-file create write this lane serves: the multipart create under the upload root whose
        /// first part is the metadata (name and parents) and whose second part is the exact content. Only a
        /// POST multipart create at the upload root is served; the media update variant, the resumable family
        /// and every other upload shape are refused. The object is only created inside a folder the caller's
        /// own account root already reaches, an occupied sibling name is refused rather than replaced, and the
        /// created object is registered so the existing metadata, list and media-read routes represent it.
        /// </summary>
        private async Task<HttpResponseMessage> DriveCreateAsync(HttpRequestMessage request, string permission,
            string path, Dictionary<string, string> query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _driveCreateRequests);
            var uploadType = query.GetValueOrDefault("uploadType", string.Empty);
            // Exactly one conditional write route is served: the v2 media update whose strong guard is the
            // If-Match of the reviewed revision. The unconditional media variants stay refused.
            if (request.Method == HttpMethod.Put &&
                string.Equals(uploadType, "media", StringComparison.Ordinal) &&
                path.StartsWith("/upload/drive/v2/files/", StringComparison.Ordinal))
                return await DriveConditionalUpdateAsync(request, path, cancellationToken);
            if (request.Method != HttpMethod.Post ||
                !string.Equals(uploadType, "multipart", StringComparison.Ordinal) ||
                !path.EndsWith("/files", StringComparison.Ordinal))
                return DriveRefusal(System.Net.HttpStatusCode.Forbidden,
                    "This build serves the small-file multipart create route only; the requested upload route " +
                    "was refused rather than applied.");
            var boundary = request.Content?.Headers.ContentType?.Parameters
                .FirstOrDefault(item => string.Equals(item.Name, "boundary", StringComparison.OrdinalIgnoreCase))
                ?.Value;
            if (string.IsNullOrWhiteSpace(boundary))
                throw new InvalidDataException(
                    "The synthetic Google transport requires the multipart boundary the create contract carries.");
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var (metadataJson, content) = SplitMultipartRelated(body, boundary!);
            using var metadata = JsonDocument.Parse(metadataJson);
            var name = metadata.RootElement.TryGetProperty("name", out var nameValue) ? nameValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(name)) return DriveRefusal(System.Net.HttpStatusCode.BadRequest,
                "The create metadata names no file.");
            // CC4E integration closure (ISSUE-1 Google parity): the same default-off bounded seam also
            // declines the selected Drive restore upload at its own transport boundary, before this
            // handler mutates any node, so the unsupported conditional-write path is reachable for a
            // Google-to-Google pair without any production Google API or classifier change.
            if (DeclineNextUploadForPath is { } declinedDrive &&
                (string.Equals(name, declinedDrive, StringComparison.Ordinal) ||
                 name!.EndsWith("/" + declinedDrive, StringComparison.Ordinal)))
            {
                DeclineNextUploadForPath = null;
                throw new NotSupportedException(
                    "The synthetic Google Drive content transport declines exactly this bounded restore upload before any transport mutation.");
            }
            var parentId = metadata.RootElement.TryGetProperty("parents", out var parents) &&
                           parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() == 1
                ? parents[0].GetString()
                : null;
            var rootId = _driveRoot[permission];
            if (parentId is null || !_driveNodes.TryGetValue(parentId, out var parent) || parent.Bytes is not null)
                return DriveRefusal(System.Net.HttpStatusCode.NotFound,
                    "The create names a parent this account does not hold as a folder.");
            if (!BelongsToDriveRoot(parentId, rootId))
                return DriveRefusal(System.Net.HttpStatusCode.Forbidden,
                    "The create names a folder outside the caller's own account root.");
            if (_driveNodes.Values.Any(node => !node.Trashed &&
                    string.Equals(node.ParentId, parentId, StringComparison.Ordinal) &&
                    string.Equals(node.Name, name, StringComparison.Ordinal) &&
                    !DriveNodeHiddenByWindow(node.Id)))
                return DriveRefusal(System.Net.HttpStatusCode.Conflict,
                    "The destination name is already used in this folder; the create was refused rather than " +
                    "replacing it.");
            var suffix = IdentitySuffix(permission, parentId + "/" + name);
            var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            var node = new DriveNode
            {
                Id = "drive-created-file-" + suffix,
                Name = name!,
                ParentId = parentId,
                Version = Convert.ToInt32(suffix[..7], 16)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                MimeType = name!.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                    ? "text/plain"
                    : "application/octet-stream",
                Bytes = content,
                Sha256 = sha256
            };
            _driveNodes[node.Id] = node;
            Interlocked.Increment(ref _driveMultipartCreateRequests);
            // The armed one-shot hidden window closes with the first served create, so the hidden same-name
            // sibling is served again from the successor readback onwards.
            if (HideDriveNodeUntilNextCreate is not null) _driveHiddenWindowClosed = true;
            DriveReads.Add(permission + "::created/" + node.Id);
            return Json(DriveNodeJson(node));
        }

        /// <summary>Whether one Drive node is reachable from this account's own root by parentage.</summary>
        private bool BelongsToDriveRoot(string nodeId, string rootId)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = nodeId;
            while (seen.Add(current))
            {
                if (string.Equals(current, rootId, StringComparison.Ordinal)) return true;
                if (!_driveNodes.TryGetValue(current, out var node) || node.ParentId is null) return false;
                current = node.ParentId;
            }
            return false;
        }

        /// <summary>The refusal shape the real Google service uses, so the adapter's own mapping is exercised.</summary>
        private static HttpResponseMessage DriveRefusal(System.Net.HttpStatusCode status, string reason) =>
            new(status)
            {
                Content = new StringContent("{" + Q("error") + ":{" + Q("message") + ":" + Q(reason) + "}}",
                    Encoding.UTF8, "application/json")
            };

        /// <summary>
        /// Splits the one multipart/related create body into its JSON metadata part and its exact content part.
        /// The boundary is the caller's own declared delimiter; a body that does not carry exactly that shape
        /// is refused rather than guessed at.
        /// </summary>
        private static (byte[] Metadata, byte[] Content) SplitMultipartRelated(byte[] body, string boundary)
        {
            var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
            var separator = Encoding.ASCII.GetBytes("\r\n\r\n");
            var first = IndexOfBytes(body, delimiter, 0);
            if (first != 0)
                throw new InvalidDataException("The multipart create body does not start with its own boundary.");
            var metadataHeaders = IndexOfBytes(body, separator, first + delimiter.Length);
            if (metadataHeaders < 0)
                throw new InvalidDataException("The multipart create body carries no metadata part headers.");
            var metadataStart = metadataHeaders + separator.Length;
            var secondDelimiter = IndexOfBytes(body, delimiter, metadataStart);
            if (secondDelimiter < 0)
                throw new InvalidDataException("The multipart create body carries no second part.");
            var contentHeaders = IndexOfBytes(body, separator, secondDelimiter + delimiter.Length);
            if (contentHeaders < 0)
                throw new InvalidDataException("The multipart create body carries no content part headers.");
            var contentStart = contentHeaders + separator.Length;
            var closing = IndexOfBytes(body, delimiter, contentStart);
            if (closing < 0)
                throw new InvalidDataException("The multipart create body carries no closing boundary.");
            var metadata = new byte[secondDelimiter - metadataStart - 2];
            Array.Copy(body, metadataStart, metadata, 0, metadata.Length);
            var content = new byte[closing - contentStart - 2];
            Array.Copy(body, contentStart, content, 0, content.Length);
            return (metadata, content);
        }

        /// <summary>The first index of one exact byte sequence at or after the given offset, or -1.</summary>
        private static int IndexOfBytes(byte[] haystack, byte[] needle, int from)
        {
            if (needle.Length == 0 || from < 0) return -1;
            for (var index = from; index <= haystack.Length - needle.Length; index++)
            {
                var match = true;
                for (var offset = 0; offset < needle.Length; offset++)
                {
                    if (haystack[index + offset] == needle[offset]) continue;
                    match = false;
                    break;
                }
                if (match) return index;
            }
            return -1;
        }

        /// <summary>
        /// The strong conditional guard of one Drive object, in the v2 shape the real adapter reads before it
        /// may replace an existing file. The tag is derived from this object's own identity and content, so it
        /// changes with the content and a stale reviewer guard can never match.
        /// </summary>
        private static string DriveGuardTag(DriveNode node) =>
            "g" + IdentitySuffix(node.Id, node.Version + ":" + (node.Sha256 ?? "-"));

        private HttpResponseMessage DriveConditionalGuard(string permission, string path)
        {
            var fileId = Uri.UnescapeDataString(path["/drive/v2/files/".Length..].Split('/')[0]);
            var node = RequireDriveNode(fileId);
            DriveReads.Add(permission + "::v2-guard/" + fileId);
            return Json("{" + Q("id") + ":" + Q(node.Id) + "," + Q("etag") + ":" +
                        Q("\"" + DriveGuardTag(node) + "\"") + "," + Q("version") + ":" + Q(node.Version) + "," +
                        Q("mimeType") + ":" + Q(node.MimeType) + "," + Q("labels") + ":{" + Q("trashed") + ":" +
                        (node.Trashed ? "true" : "false") + "}," + Q("parents") + ":" +
                        (node.ParentId is null
                            ? "[]"
                            : "[{" + Q("id") + ":" + Q(node.ParentId) + "}]") + "}");
        }

        /// <summary>
        /// The one conditional Drive write this lane serves: the media update against an existing file id whose
        /// If-Match carries the exact strong guard of the reviewed revision. A stale guard is refused with the
        /// real precondition-failed status and no mutation, an absent guard is refused as the unconditional
        /// media update is not served, and an applied update advances the object's own version so the adapter's
        /// independent readback can prove the successor.
        /// </summary>
        private async Task<HttpResponseMessage> DriveConditionalUpdateAsync(HttpRequestMessage request,
            string path, CancellationToken cancellationToken)
        {
            _driveUpdateRequests++;
            var fileId = Uri.UnescapeDataString(path["/upload/drive/v2/files/".Length..].Split('/')[0]);
            if (!_driveNodes.TryGetValue(fileId, out var node) || node.Bytes is null)
                return DriveRefusal(System.Net.HttpStatusCode.NotFound,
                    "The conditional update names no binary object of this account.");
            var guards = request.Headers.IfMatch;
            if (guards is null || guards.Count == 0)
                return DriveRefusal(System.Net.HttpStatusCode.Forbidden,
                    "The unconditional media update is not served; only a conditional update carrying the " +
                    "reviewed strong guard is accepted.");
            // EntityTagHeaderValue keeps the quotes of the wire form, so the comparison is made on the exact
            // quoted guard the adapter sends back after reading it from this transport.
            if (!string.Equals(guards.First().Tag, "\"" + DriveGuardTag(node) + "\"", StringComparison.Ordinal))
                return DriveRefusal(System.Net.HttpStatusCode.PreconditionFailed,
                    "The reviewed Drive revision is no longer current; the stale update was refused and " +
                    "nothing was replaced.");
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            node.Bytes = bytes;
            node.Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            node.Version = (int.Parse(node.Version, System.Globalization.CultureInfo.InvariantCulture) + 1)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Json("{" + Q("id") + ":" + Q(node.Id) + "}");
        }

        /// <summary>
        /// The server-side copy this lane serves for a Drive recovery copy: a new object under the named parent
        /// that carries exactly the source object's bytes. The copy keeps its own reference to the predecessor
        /// bytes, so a later conditional update of the source can never change what the copy retains.
        /// </summary>
        private async Task<HttpResponseMessage> DriveCopyAsync(HttpRequestMessage request, string permission,
            string fileId, CancellationToken cancellationToken)
        {
            _driveRecoveryCopyRequests++;
            if (!_driveNodes.TryGetValue(fileId, out var source) || source.Bytes is null || source.Trashed)
                return DriveRefusal(System.Net.HttpStatusCode.NotFound,
                    "The copy source is not a binary object of this account.");
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var metadata = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var name = metadata.RootElement.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString()
                : null;
            var parentId = metadata.RootElement.TryGetProperty("parents", out var parents) &&
                           parents.ValueKind == JsonValueKind.Array && parents.GetArrayLength() == 1
                ? parents[0].GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name) || parentId is null ||
                !_driveNodes.TryGetValue(parentId, out var parent) || parent.Bytes is not null)
                return DriveRefusal(System.Net.HttpStatusCode.BadRequest,
                    "The copy names no valid destination folder of this account.");
            if (!BelongsToDriveRoot(parentId, _driveRoot[permission]))
                return DriveRefusal(System.Net.HttpStatusCode.Forbidden,
                    "The copy destination is outside the caller's own account root.");
            if (_driveNodes.Values.Any(node => !node.Trashed &&
                    string.Equals(node.ParentId, parentId, StringComparison.Ordinal) &&
                    string.Equals(node.Name, name, StringComparison.Ordinal)))
                return DriveRefusal(System.Net.HttpStatusCode.Conflict,
                    "The copy destination name is already used in this folder; nothing was overwritten.");
            var suffix = IdentitySuffix(permission, parentId + "/" + name);
            var copy = new DriveNode
            {
                Id = "drive-copied-file-" + suffix,
                Name = name!,
                ParentId = parentId,
                Version = Convert.ToInt32(suffix[..7], 16)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
                MimeType = source.MimeType,
                Bytes = source.Bytes,
                Sha256 = source.Sha256
            };
            _driveNodes[copy.Id] = copy;
            DriveReads.Add(permission + "::copied/" + copy.Id);
            return Json(DriveNodeJson(copy));
        }

        private string BrowsePath(string rootNamespace, string path)
        {
            if (string.IsNullOrEmpty(path) || path == "/") return "/";
            if (path.StartsWith("id:", StringComparison.Ordinal))
                return _dropboxId.TryGetValue(path, out var node) ? node.Path : "/";
            return PilotPath.NormalizeDropboxRoot(path);
        }

        private (string ParentPath, string Name) SplitCreatePath(string rootNamespace, string rawPath)
        {
            var slash = rawPath.LastIndexOf('/');
            var name = slash < 0 ? rawPath : rawPath[(slash + 1)..];
            var rawParent = slash <= 0 ? string.Empty : rawPath[..slash];
            if (rawParent.StartsWith("id:", StringComparison.Ordinal))
                return (_dropboxId[rawParent].Path, name);
            return (string.IsNullOrEmpty(rawParent) ? "/" : PilotPath.NormalizeDropboxRoot(rawParent), name);
        }

        private (string Id, string Path)? ResolveDropbox(string rootNamespace, string path)
        {
            if (path.StartsWith("id:", StringComparison.Ordinal))
            {
                if (!path.Contains('/'))
                    return _dropboxId.TryGetValue(path, out var byId) &&
                           string.Equals(byId.Namespace, rootNamespace, StringComparison.Ordinal)
                        ? (path, byId.Path) : null;
                path = ResolveDropboxIdRelativePath(rootNamespace, path) ?? string.Empty;
                if (path.Length == 0) return null;
            }
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            return _dropboxPath.TryGetValue(RootKey(rootNamespace, normalized), out var id)
                ? (id, normalized)
                : null;
        }

        /// <summary>
        /// The one seeded file addressed by this exact retained namespace and path, or by its own object id.
        /// A file seeded under another namespace is never reachable through this one.
        /// </summary>
        private DropboxFile? ResolveDropboxFile(string rootNamespace, string path)
        {
            if (path.StartsWith("id:", StringComparison.Ordinal))
            {
                if (!path.Contains('/'))
                    return _dropboxFileById.TryGetValue(path, out var byId) &&
                           string.Equals(byId.NamespaceId, rootNamespace, StringComparison.Ordinal)
                        ? byId : null;
                path = ResolveDropboxIdRelativePath(rootNamespace, path) ?? string.Empty;
                if (path.Length == 0) return null;
            }
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            return _dropboxFilePath.TryGetValue(RootKey(rootNamespace, normalized), out var id) &&
                   _dropboxFileById.TryGetValue(id, out var found)
                ? found
                : null;
        }

        // Dropbox's documented id:<folder-id>/<relative-path> addressing is still
        // scoped to the request's exact namespace; an unknown or foreign parent
        // is not silently substituted with the account root.
        private string? ResolveDropboxIdRelativePath(string rootNamespace, string path)
        {
            var slash = path.IndexOf('/');
            if (slash < 0 || !_dropboxId.TryGetValue(path[..slash], out var parent) ||
                !string.Equals(parent.Namespace, rootNamespace, StringComparison.Ordinal)) return null;
            var relative = PilotPath.NormalizeRelative(path[(slash + 1)..]);
            return PilotPath.NormalizeDropboxRoot((parent.Path == "/" ? string.Empty : parent.Path) + "/" + relative);
        }

        /// <summary>
        /// Whether one provider path belongs to the listing that asked for exactly this parent: its direct
        /// children, or every descendant when the caller asked for a recursive listing.
        /// </summary>
        private static bool InListedScope(string candidate, string parent, bool recursive)
        {
            if (!recursive)
                return string.Equals(ParentOf(candidate), parent, StringComparison.OrdinalIgnoreCase);
            var prefix = parent == "/" ? "/" : parent + "/";
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The one revision-pinned small-file read this lane serves. The revision names the object, the object
        /// still has to belong to the caller's own retained namespace, and the file's own metadata and exact
        /// bytes come back together in the two places the real adapter reads them from.
        /// </summary>
        private HttpResponseMessage DropboxDownload(HttpRequestMessage request, DropboxAccount caller)
        {
            if (!request.Headers.TryGetValues("Dropbox-API-Arg", out var arguments))
                throw new InvalidDataException(
                    "The synthetic Dropbox content transport requires the Dropbox-API-Arg header.");
            using var argument = JsonDocument.Parse(arguments.Single());
            var path = argument.RootElement.GetProperty("path").GetString() ?? string.Empty;
            if (!path.StartsWith("rev:", StringComparison.Ordinal))
                throw new InvalidDataException(
                    "The synthetic Dropbox content transport serves a revision-pinned source only.");
            var revision = path["rev:".Length..];
            if (!_dropboxFileByRevision.TryGetValue(revision, out var file) ||
                !string.Equals(file.NamespaceId, caller.RootNamespaceId, StringComparison.Ordinal) ||
                _dropboxMissing.Contains(file.Id))
                return NotFound();
            DropboxReads.Add(file.NamespaceId + "::rev:" + revision);
            _dropboxDownloadRequests++;
            // The adapter's whole-object read and its exact-range read are the same route: an explicit
            // Range header is answered with the exact slice and its content range, and no Range header is
            // answered with the whole object. Nothing else about the object's identity changes.
            var range = request.Headers.Range?.Ranges;
            var from = 0L;
            var count = file.Bytes.LongLength;
            var status = System.Net.HttpStatusCode.OK;
            if (range is { Count: > 0 })
            {
                var window = range.Single();
                if (window.From is null || window.To is null || window.From.Value < 0 ||
                    window.To.Value < window.From.Value || window.From.Value >= file.Bytes.LongLength)
                    throw new InvalidDataException(
                        "The synthetic Dropbox content transport serves a bounded two-ended byte range only.");
                from = window.From.Value;
                count = Math.Min(window.To.Value, file.Bytes.LongLength - 1) - from + 1;
                status = System.Net.HttpStatusCode.PartialContent;
                _dropboxRangeDownloadRequests++;
            }
            var body = new byte[count];
            Array.Copy(file.Bytes, from, body, 0, count);
            var response = new HttpResponseMessage(status)
            {
                Content = RecoveryContent(file.Id, body)
            };
            if (status == System.Net.HttpStatusCode.PartialContent)
                response.Content.Headers.TryAddWithoutValidation("Content-Range",
                    "bytes " + from.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" +
                    (from + count - 1L).ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" +
                    file.Bytes.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            response.Headers.Add("Dropbox-API-Result", DropboxFileJson(file));
            return response;
        }

        /// <summary>
        /// The one create-only small-file write this lane serves. The argument travels in the Dropbox-API-Arg
        /// header, the body is the exact content, and the object is only registered under the caller's own
        /// retained namespace and inside a folder that namespace already holds. The add mode with a strict
        /// conflict is the only mode served: an update mode, an occupied destination and a foreign parent are
        /// all refused, so no stored content can be replaced by this route.
        /// </summary>
        private async Task<HttpResponseMessage> DropboxUploadAsync(HttpRequestMessage request,
            DropboxAccount caller, CancellationToken cancellationToken)
        {
            _dropboxSimpleUploadRequests++;
            _dropboxUploadRequests++;
            if (!request.Headers.TryGetValues("Dropbox-API-Arg", out var arguments))
                throw new InvalidDataException(
                    "The synthetic Dropbox content transport requires the Dropbox-API-Arg header.");
            using var argument = JsonDocument.Parse(arguments.Single());
            var target = argument.RootElement.GetProperty("path").GetString() ?? string.Empty;
            if (DeclineNextUploadForPath is { } declined &&
                target.EndsWith("/" + declined, StringComparison.Ordinal))
            {
                DeclineNextUploadForPath = null;
                throw new NotSupportedException(
                    "The synthetic Dropbox content transport declines exactly this bounded restore upload before any transport mutation.");
            }
            var mode = argument.RootElement.TryGetProperty("mode", out var modeValue) ? modeValue : default;
            // The real adapter sends the documented create-only write mode in either wire shape Dropbox
            // accepts: the plain string "add", or the tagged union object. Any other mode is an update.
            var tag = mode.ValueKind switch
            {
                JsonValueKind.Object => mode.TryGetProperty(".tag", out var modeTag) ? modeTag.GetString() : null,
                JsonValueKind.String => mode.GetString(),
                _ => null
            };
            if (string.Equals(tag, "update", StringComparison.Ordinal))
                return await DropboxConditionalUpdateAsync(request, caller, target, mode, cancellationToken);
            if (!string.Equals(tag, "add", StringComparison.Ordinal)) return DropboxConflict();
            var created = UploadTargetPath(caller, target);
            if (created is null) return DropboxConflict();
            var key = RootKey(caller.RootNamespaceId, created);
            if (_dropboxFilePath.ContainsKey(key) || _dropboxPath.ContainsKey(key)) return DropboxConflict();
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            return PublishDropboxCreate(caller, created, bytes);
        }

        private HttpResponseMessage PublishDropboxCreate(DropboxAccount caller, string created, byte[] bytes)
        {
            var key = RootKey(caller.RootNamespaceId, created);
            var suffix = IdentitySuffix(caller.RootNamespaceId, created);
            var file = new DropboxFile("id:host-created-file-" + suffix, caller.RootNamespaceId, created,
                "created-" + suffix, DropboxContentHash.Compute(bytes), bytes);
            _dropboxFilePath[key] = file.Id;
            _dropboxFileById[file.Id] = file;
            _dropboxFileByRevision[file.Revision] = file;
            // The product recovery container is the one destination a recovery copy writes to; that is the
            // exact path the retention step addresses, so the copy and a plain create stay distinguishable.
            if (created.StartsWith(SyncControlNamespace.RemoteContainerPath + "/", StringComparison.Ordinal))
                _dropboxRecoveryCopyRequests++;
            return Json(DropboxFileJson(file));
        }

        private async Task<HttpResponseMessage> NativeDropboxSessionAsync(HttpRequestMessage request,
            DropboxAccount caller, CancellationToken cancellationToken)
        {
            if (NativeDropboxSessionTargetForTest is not { } target || request.Method != HttpMethod.Post ||
                request.RequestUri is not { Scheme: "https", Host: "content.dropboxapi.com" } ||
                caller.RootNamespaceId != target.NamespaceId || PathRoot(request) != target.NamespaceId)
                throw new InvalidDataException("The native session fixture refuses an unbound origin/account/root.");
            var finish = request.RequestUri.AbsolutePath == "/2/files/upload_session/finish";
            string? created = null;
            if (finish)
            {
                using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                var commit = argument.RootElement.GetProperty("commit");
                var path = commit.GetProperty("path").GetString() ?? "";
                if (!path.StartsWith(target.FolderId + "/", StringComparison.Ordinal) ||
                    commit.GetProperty("mode").GetString() != "add" ||
                    commit.GetProperty("autorename").GetBoolean() ||
                    !commit.GetProperty("strict_conflict").GetBoolean())
                    throw new InvalidDataException("The native session fixture refuses a non-exact create-only commit.");
                created = UploadTargetPath(caller, path);
                if (created is null || _dropboxFilePath.ContainsKey(RootKey(target.NamespaceId, created)) ||
                    _dropboxPath.ContainsKey(RootKey(target.NamespaceId, created))) return DropboxConflict();
            }
            _nativeDropboxSessionInvoker ??= new HttpMessageInvoker(_nativeDropboxSessions, false);
            var response = await _nativeDropboxSessionInvoker.SendAsync(request, cancellationToken);
            if (!finish || !response.IsSuccessStatusCode) return response;
            response.Dispose();
            _dropboxUploadRequests++;
            return PublishDropboxCreate(caller, created!, _nativeDropboxSessions.CommittedBytes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _nativeDropboxSessionInvoker?.Dispose();
                _nativeDropboxSessions.ExpireSessions();
                _nativeDropboxSessions.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// The one display path an upload argument addresses inside this caller's own namespace, or null when
        /// the argument names a parent this namespace does not hold.
        /// </summary>
        private string? UploadTargetPath(DropboxAccount caller, string target)
        {
            var slash = target.LastIndexOf('/');
            var name = slash < 0 ? target : target[(slash + 1)..];
            if (name.Length == 0) return null;
            var parentToken = slash <= 0 ? string.Empty : target[..slash];
            string parentPath;
            if (parentToken.Length == 0)
            {
                parentPath = "/";
            }
            else if (parentToken.StartsWith("id:", StringComparison.Ordinal))
            {
                if (!_dropboxId.TryGetValue(parentToken, out var parent) ||
                    !string.Equals(parent.Namespace, caller.RootNamespaceId, StringComparison.Ordinal))
                    return null;
                parentPath = parent.Path;
            }
            else
            {
                parentPath = PilotPath.NormalizeDropboxRoot(parentToken);
                if (!_dropboxPath.ContainsKey(RootKey(caller.RootNamespaceId, parentPath))) return null;
            }
            return PilotPath.NormalizeDropboxRoot((parentPath == "/" ? string.Empty : parentPath) + "/" + name);
        }

        /// <summary>
        /// The one conditional small-file update this lane serves. The argument carries the expected revision,
        /// the target must already be a file of the caller's own namespace at exactly that revision, and the
        /// object is replaced only while it still is. A stale revision, a missing object or an update addressed
        /// outside the caller's namespace is refused without changing anything, and the successor keeps its own
        /// new revision with a content hash of exactly the bytes it received.
        /// </summary>
        private async Task<HttpResponseMessage> DropboxConditionalUpdateAsync(HttpRequestMessage request,
            DropboxAccount caller, string target, JsonElement mode, CancellationToken cancellationToken)
        {
            _dropboxUpdateRequests++;
            var expected = mode.ValueKind == JsonValueKind.Object &&
                           mode.TryGetProperty("update", out var updateValue) ? updateValue.GetString() : null;
            var display = UploadTargetPath(caller, target);
            if (display is null || string.IsNullOrWhiteSpace(expected)) return DropboxConflict();
            if (!_dropboxFilePath.TryGetValue(RootKey(caller.RootNamespaceId, display), out var id) ||
                !_dropboxFileById.TryGetValue(id, out var current) || _dropboxMissing.Contains(current.Id) ||
                !string.Equals(current.Revision, expected, StringComparison.Ordinal))
                return DropboxConflict();
            var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            var successor = current with
            {
                Revision = "upd" + IdentitySuffix("revision", current.Revision),
                ContentHash = DropboxContentHash.Compute(bytes),
                Bytes = bytes
            };
            _dropboxFileByRevision.Remove(current.Revision);
            _dropboxFileById[successor.Id] = successor;
            _dropboxFileByRevision[successor.Revision] = successor;
            return Json(DropboxFileJson(successor));
        }

        /// <summary>The create-only refusal shape the real Dropbox service uses for a conflicting add.</summary>
        private static HttpResponseMessage DropboxConflict() =>
            new(System.Net.HttpStatusCode.Conflict)
            {
                Content = new StringContent("{" + Q("error_summary") + ":" + Q("conflict") + "}", Encoding.UTF8,
                    "application/json")
            };

        private static string ParentOf(string path)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path).TrimEnd('/');
            if (normalized.Length == 0) return "/";
            var index = normalized.LastIndexOf('/');
            return index <= 0 ? "/" : normalized[..index];
        }

        private static string? PathRoot(HttpRequestMessage request)
        {
            if (!request.Headers.TryGetValues("Dropbox-API-Path-Root", out var values)) return null;
            using var document = JsonDocument.Parse(values.First());
            return document.RootElement.GetProperty("root").GetString();
        }

        private static DropboxAccount ParseDropboxToken(string token)
        {
            var parts = token.Split('|');
            if (parts.Length != 4 || parts[0] is not ("dbrefresh" or "dbaccess"))
                throw new InvalidDataException("The synthetic Dropbox transport refuses an unknown token.");
            return new DropboxAccount(parts[1], parts[2], parts[3]);
        }

        private static string DropboxAccess(DropboxAccount account) =>
            "dbaccess|" + account.AccountId + "|" + account.RootNamespaceId + "|" + account.HomeNamespaceId;

        private static string DropboxFolderJson(string id, string path)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(path);
            var name = normalized == "/" ? string.Empty : normalized[(normalized.LastIndexOf('/') + 1)..];
            return "{" + Q(".tag") + ":" + Q("folder") + "," + Q("name") + ":" + Q(name) + "," + Q("id") + ":" +
                   Q(id) + "," + Q("path_lower") + ":" + Q(normalized.ToLowerInvariant().Replace('\\', '/')) + "," +
                   Q("path_display") + ":" + Q(normalized) + "}";
        }

        /// <summary>
        /// The one seeded file's own metadata: its own object id and revision, its exact byte length and the
        /// content hash of exactly those bytes, so the adapter's readback can only match the same object.
        /// </summary>
        private static string DropboxFileJson(DropboxFile file)
        {
            var normalized = PilotPath.NormalizeDropboxRoot(file.ReportedPath ?? file.Path);
            var name = normalized[(normalized.LastIndexOf('/') + 1)..];
            return "{" + Q(".tag") + ":" + Q("file") + "," + Q("name") + ":" + Q(name) + "," + Q("id") + ":" +
                   Q(file.ReportedId ?? file.Id) + "," + Q("rev") + ":" + Q(file.Revision) + "," + Q("size") + ":" +
                   (file.ReportedSize ?? file.Bytes.LongLength)
                   .ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
                   Q("content_hash") + ":" + Q(file.ReportedHash ?? file.ContentHash) + "," + Q("path_lower") + ":" +
                   Q(normalized.ToLowerInvariant().Replace('\\', '/')) + "," + Q("path_display") + ":" +
                   Q(normalized) + "}";
        }

        private static string DriveNodeJson(DriveNode node)
        {
            var parent = node.ReportedParentId ?? node.ParentId;
            var parents = parent is null ? "[]" : "[" + Q(parent) + "]";
            var length = node.ReportedSize ?? (node.Bytes is null ? 0 : node.Bytes.LongLength);
            var json = "{" + Q("id") + ":" + Q(node.ReportedId ?? node.Id) + "," + Q("name") + ":" + Q(node.Name) + "," +
                       Q("mimeType") + ":" + Q(node.MimeType) + "," + Q("parents") + ":" + parents + "," +
                       Q("trashed") + ":" + (node.Trashed ? "true" : "false") + "," + Q("version") + ":" +
                       Q(node.Version) + "," + Q("size") + ":" +
                       Q(length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (node.Bytes is not null)
                json += "," + Q("capabilities") + ":{" + Q("canDownload") + ":true}";
            var sha256 = node.ReportedSha256 ?? node.Sha256;
            if (sha256 is not null) json += "," + Q("sha256Checksum") + ":" + Q(sha256);
            if (node.DriveId is not null) json += "," + Q("driveId") + ":" + Q(node.DriveId);
            if (node.Url is not null) json += "," + Q("webViewLink") + ":" + Q(node.Url);
            return json + "}";
        }

        private static string? ExtractBetween(string value, string start, string end)
        {
            var from = value.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return null;
            from += start.Length;
            var to = value.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? null : value[from..to];
        }

        private static Dictionary<string, string> ParseForm(string body)
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = pair.IndexOf('=');
                if (index <= 0) continue;
                form[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..]);
            }
            return form;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = pair.IndexOf('=');
                if (index <= 0) continue;
                values[Uri.UnescapeDataString(pair[..index])] = Uri.UnescapeDataString(pair[(index + 1)..]);
            }
            return values;
        }

        private static string Q(string value) => JsonSerializer.Serialize(value);

        private static HttpResponseMessage Json(string value) =>
            new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(value, Encoding.UTF8, "application/json")
            };

        // get_metadata returns its path lookup union, not a generic endpoint-family
        // "not_found". Keep the latter refusal fixture unchanged for other routes.
        // Dropbox SDK: https://dropbox-sdk-python.readthedocs.io/en/latest/api/files.html#dropbox.files.GetMetadataError
        private static HttpResponseMessage MissingMetadataPath() =>
            new(System.Net.HttpStatusCode.Conflict)
            {
                Content = new StringContent("{\"error_summary\":\"path/not_found/fixture\",\"error\":{\".tag\":\"path\",\"path\":{\".tag\":\"not_found\"}}}",
                    Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage NotFound() =>
            new(System.Net.HttpStatusCode.Conflict)
            {
                Content = new StringContent("{" + Q("error_summary") + ":" + Q("not_found") + "}",
                    Encoding.UTF8, "application/json")
            };
    }

    /// <summary>The synthetic per-connection Dropbox refresh token the real token source exchanges.</summary>
    internal sealed class FixtureDropboxCredentials : IDropboxRefreshTokenStore
    {
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) =>
            new OneAccount(binding);

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAsync(string value, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class OneAccount(DropboxConnectionBinding binding) : IDropboxRefreshTokenStore
        {
            public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding connection) => this;

            public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult<string?>(HostWire.RefreshTokenFor(binding));

            public Task WriteAsync(string value, CancellationToken cancellationToken = default) =>
                Task.CompletedTask;

            public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        }
    }

    /// <summary>The synthetic per-connection Google refresh token the real token source reads.</summary>
    internal sealed class FixtureGoogleCredentials : IGoogleDriveRefreshTokenStore
    {
        public Task<string?> ReadAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(HostWire.GoogleRefreshFor(connectionId));

        public Task WriteAsync(string connectionId, string refreshToken,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(string connectionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// The real Google token endpoint contract answered synthetically: the access token it issues carries the
    /// exact connection it belongs to, and the account identity it reports is that connection's own account.
    /// </summary>
    internal sealed class FixtureGoogleEndpoint(HostWire wire) : IGoogleDriveTokenEndpoint
    {
        public Task<GoogleDriveTokenResult> RefreshAsync(string clientId, string refreshToken,
            CancellationToken cancellationToken = default)
        {
            wire.GooglePermissionForRefresh(refreshToken);
            return Task.FromResult(new GoogleDriveTokenResult(
                "gaccess:" + refreshToken["grefresh:".Length..], DateTime.UtcNow.AddHours(1),
                GoogleDriveProviderConfiguration.RequiredScopes));
        }

        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleDriveAccountIdentity(wire.GooglePermissionFor(accessToken), string.Empty,
                string.Empty, string.Empty));
    }

    /// <summary>One resolved logical side must be exactly the retained identity it was asked to bind.</summary>
    private static void AssertSide(SyncAuthorityCloudSideAuthority side, string logicalSide, EndpointSpec spec,
        string rootBindingId, string pairId, bool sharedRecoveryAuthority = false)
    {
        Assert(side.CompatibilitySide == logicalSide,
            "the " + logicalSide + " side keeps its exact logical compatibility tag");
        Assert(side.Provider == spec.Provider && side.ConnectionId == spec.ConnectionId &&
               side.ProviderAccountId == spec.ProviderAccountId && side.NamespaceId == spec.EndpointNamespaceId &&
               side.FolderId == spec.FolderId,
            "the " + logicalSide + " side carries exactly its own retained provider, account, namespace and "
            + "folder identity");
        Assert(side.PairRootBindingId == rootBindingId,
            "the " + logicalSide + " side is bound under its exact retained authority root binding id");
        Assert(side.FolderPath == SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(spec.FolderPath),
            "the " + logicalSide + " side presents its own verified display path");
        var ownRecovery = SyncAuthorityCloudExecution.Id("recovery-cloud", pairId, logicalSide);
        var canonicalRecovery = SyncAuthorityCloudExecution.Id("recovery-cloud", pairId,
            SyncAuthorityCloudSides.LogicalLeft);
        Assert(sharedRecoveryAuthority
                ? side.RecoveryRootBindingId == canonicalRecovery
                : side.RecoveryRootBindingId == ownRecovery,
            sharedRecoveryAuthority
                ? "the " + logicalSide + " side maps to the ONE canonical Recovery authority shared by both " +
                  "disjoint folders of this account and corpus"
                : "the " + logicalSide + " recovery authority is derived from this pair and this logical side");
        Assert(side.RecoveryPath == RecommendedRecoveryPath,
            "the " + logicalSide + " recovery authority is the recommended Recovery container");
        Assert(!string.IsNullOrWhiteSpace(side.RecoveryFolderId) &&
               !string.IsNullOrWhiteSpace(side.RecoveryNamespaceId) &&
               !string.IsNullOrWhiteSpace(side.RecoveryCorpusId),
            "the " + logicalSide + " recovery authority carries its stable folder, namespace and corpus identity");
    }

    /// <summary>The candidate root is registered only through the existing transactional registration.</summary>
    private static async Task AssertRegisteredAsync(HostRig rig, HostPair pair, string side)
    {
        var spec = side == SyncAuthorityCloudSides.LogicalLeft ? pair.Left : pair.Right;
        var rootBindingId = side == SyncAuthorityCloudSides.LogicalLeft ? pair.LeftRoot : pair.RightRoot;
        var row = await rig.RootBindingAsync(rootBindingId);
        Assert(row.Length > 0,
            "the " + side + " candidate root is registered only through the existing transactional registration");
        var fields = row.Split('|');
        Assert(fields[0] == "1" && fields[3] == "active" &&
               fields[2] == SyncAuthorityCloudRootPersistence.NormalizeDisplayPath(spec.FolderPath),
            "the registered " + side + " root binding is active at generation 1 and its verified display path");
        Assert(fields[1].Contains(spec.Provider, StringComparison.Ordinal) &&
               fields[1].Contains(spec.ProviderAccountId, StringComparison.Ordinal) &&
               fields[1].Contains(spec.EndpointNamespaceId, StringComparison.Ordinal) &&
               fields[1].Contains(spec.FolderId, StringComparison.Ordinal),
            "the registered " + side + " root binding carries its exact provider, account, namespace and folder");
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE root_binding_id='" + rootBindingId
                   + "' AND pair_id='" + pair.Id + "' AND side='" + side
                   + "' AND generation=1 AND state='active';") == 1,
            "the registered " + side + " root binding is bound to exactly this pair, side and generation");
    }

    private static Task<long> RecoveryCountAsync(HostRig rig, HostPair pair) =>
        rig.CountAsync("SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id IN ('"
                       + pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalLeft) + "','"
                       + pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalRight) + "');");

    /// <summary>
    /// Both sides resolve, both roots register and both recovery authorities retain, in one prepare. The pair's
    /// private staging identity is created exactly once by the Host's OWN create-once path - the fixture never
    /// hand-creates a local root or a control namespace for it - and every later preparation only reopens it.
    /// </summary>
    private static async Task<SyncAuthorityPreparedCloudPair> PrepareBothSidesAsync(HostRig rig, HostPair pair,
        bool withStaging = true, bool sharedRecoveryAuthority = false)
    {
        var created = withStaging ? await rig.PrepareAsync(pair, createStaging: true) : null;
        if (created is not null)
        {
            Assert(created.PrivateStaging.NamespaceId == pair.Id &&
                   created.PrivateStaging.Local.RootPath.Length > 0 &&
                   created.PrivateStaging.Local.MarkerContentHash.Length > 0,
                "the real create-once staging path completed this pair's own private staging identity with no " +
                "fixture-created local root");
            Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
                "the real creation retained exactly one private staging binding for the pair");
        }
        var prepared = await rig.PrepareAsync(pair);
        Assert(prepared.Pair.Id == pair.Id, "the prepared context is the exact retained pair that was prepared");
        foreach (var (port, targetAllocation) in new[]
                 {
                     (prepared.Endpoints.Left, prepared.CloudAllocations.Left),
                     (prepared.Endpoints.Right, prepared.CloudAllocations.Right)
                 })
        {
            Assert(port.Store is DropboxPilotClient or GoogleDrivePilotClient &&
                   port.Store is ISyncResumableRemoteStore && port.Store is ISyncUploadSessionPolicySource,
                "the actual Host factory retains the native receiving adapter's session and limit interfaces, " +
                "rather than a capability-losing store wrapper");
            var policy = SyncUploadLimits.ForStore(port.Store);
            // long.MaxValue is the adapter's explicit no-finite-cutoff value for guarded media
            // replacement. There is no representable larger source; wrapping to long.MinValue
            // would test a negative size, not the declared production boundary.
            bool AboveLimit(long limit, bool replacement) => limit == long.MaxValue
                ? !policy.RequiresSession(long.MaxValue, replacement)
                : policy.RequiresSession(checked(limit + 1), replacement);
            Assert(targetAllocation.UploadSessionPolicy == policy &&
                   !policy.RequiresSession(policy.MaximumSimpleCreateBytes, replacement: false) &&
                   AboveLimit(policy.MaximumSimpleCreateBytes, replacement: false) &&
                   !policy.RequiresSession(policy.MaximumSimpleReplacementBytes, replacement: true) &&
                   AboveLimit(policy.MaximumSimpleReplacementBytes, replacement: true),
                "Host planning uses this receiving adapter's exact production create/replacement thresholds; " +
                "finite limits switch only above the boundary and an unlimited limit does not overflow (" +
                policy.MaximumSimpleCreateBytes + "/" +
                policy.MaximumSimpleReplacementBytes + ")");
        }
        if (created is not null)
            Assert(prepared.PrivateStaging == created.PrivateStaging,
                "ordinary preparation reopened the identical created private staging identity");
        AssertSide(prepared.Left, SyncAuthorityCloudSides.LogicalLeft, pair.Left, pair.LeftRoot, pair.Id,
            sharedRecoveryAuthority);
        AssertSide(prepared.Right, SyncAuthorityCloudSides.LogicalRight, pair.Right, pair.RightRoot, pair.Id,
            sharedRecoveryAuthority);
        await AssertRegisteredAsync(rig, pair, SyncAuthorityCloudSides.LogicalLeft);
        await AssertRegisteredAsync(rig, pair, SyncAuthorityCloudSides.LogicalRight);
        if (sharedRecoveryAuthority)
        {
            var shared = await rig.RecoveryBindingAsync(pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalLeft));
            Assert(shared.Contains(RecommendedRecoveryPath, StringComparison.Ordinal) &&
                   shared.Contains("|active|", StringComparison.Ordinal),
                "the one canonical Recovery authority of this account and corpus is retained active: " + shared);
            Assert(await RecoveryCountAsync(rig, pair) == 1,
                "exactly one of the pair's two candidate Recovery identities was retained, so both disjoint " +
                "folders of the one account and corpus share a single canonical recovery authority");
        }
        else foreach (var side in new[]
                 {
                     SyncAuthorityCloudSides.LogicalLeft, SyncAuthorityCloudSides.LogicalRight
                 })
        {
            var row = await rig.RecoveryBindingAsync(pair.RecoveryRootId(side));
            Assert(row.Contains(RecommendedRecoveryPath, StringComparison.Ordinal) &&
                   row.Contains("|active|", StringComparison.Ordinal),
                "the " + side + " recommended-Recovery authority is retained for its own account and corpus: "
                + row);
        }
        return prepared;
    }

    /// <summary>
    /// CORRPROOF-2: through the REAL allocation and recovery paths, two OPPOSITE-side update operations that
    /// share one verified canonical Recovery authority keep distinct operation allocations and deterministic
    /// names, an exact replay of one operation retains its exact identity, and every substituted fact - another
    /// logical target, another account, another corpus, a minted Recovery identity, or a pair root claimed as
    /// its own recovery boundary - is still refused before any provider effect.
    /// </summary>
    private static async Task AssertSharedRecoveryAllocationAndRefusalsAsync(HostRig rig, HostPair pair,
        SyncAuthorityPreparedCloudPair prepared)
    {
        var left = prepared.SideAuthority(SyncAuthorityCloudSides.LogicalLeft);
        var right = prepared.SideAuthority(SyncAuthorityCloudSides.LogicalRight);
        Assert(left.RecoveryRootBindingId == right.RecoveryRootBindingId &&
               left.RecoveryFolderId == right.RecoveryFolderId &&
               left.RecoveryNamespaceId == right.RecoveryNamespaceId &&
               left.RecoveryCorpusId == right.RecoveryCorpusId,
            "two disjoint folders of one account and corpus legitimately map both logical sides to the ONE " +
            "exact canonical Recovery authority");
        Assert(left.PairRootBindingId != right.PairRootBindingId && left.FolderId != right.FolderId,
            "the shared Recovery authority never merges the two pair-root ids or the two folder identities");

        // The REAL allocation path: the configuration the Host itself prepared, consumed by the shared policy.
        var options = prepared.PlannerOptions with { CloudAllocations = prepared.CloudAllocations };
        const string leftOperationId = "operation-correction-left-update";
        const string rightOperationId = "operation-correction-right-update";
        var leftRows = SyncAuthorityOperationAllocations.Build(leftOperationId,
            SyncAuthorityOperationKind.UpdateLocal, "shared/correction.txt", left.PairRootBindingId,
            right.PairRootBindingId, "file", 32, options);
        var rightRows = SyncAuthorityOperationAllocations.Build(rightOperationId,
            SyncAuthorityOperationKind.UpdateRemote, "shared/correction.txt", left.PairRootBindingId,
            right.PairRootBindingId, "file", 32, options);
        var leftRecovery = leftRows.Single(item => item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        var rightRecovery = rightRows.Single(item => item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(leftRecovery.IntendedParentIdentity == left.RecoveryRootBindingId &&
               rightRecovery.IntendedParentIdentity == right.RecoveryRootBindingId,
            "each opposite-side update allocation is parented by the one shared verified Recovery authority");
        Assert(leftRecovery.AllocationId != rightRecovery.AllocationId &&
               leftRecovery.DeterministicName != rightRecovery.DeterministicName,
            "the two opposite-side operations sharing that Recovery parent keep distinct allocations and names: "
            + leftRecovery.DeterministicName + " / " + rightRecovery.DeterministicName);
        Assert(!leftRows.Select(item => item.AllocationId)
            .Intersect(rightRows.Select(item => item.AllocationId), StringComparer.Ordinal).Any(),
            "no allocation identity is shared between the two opposite-side operations");
        var replay = SyncAuthorityOperationAllocations.Build(leftOperationId,
            SyncAuthorityOperationKind.UpdateLocal, "shared/correction.txt", left.PairRootBindingId,
            right.PairRootBindingId, "file", 32, options);
        Assert(replay.SequenceEqual(leftRows),
            "an exact replay of one operation retains its identical allocations, parents and deterministic names");

        // The REAL recovery path: each target's own typed port and retained side authority, against the rows.
        var leftAuthority = await SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
            prepared.Port(SyncAuthorityCloudSides.LogicalLeft), left, leftRecovery,
            prepared.PrivateStaging.NamespaceId, default);
        var rightAuthority = await SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
            prepared.Port(SyncAuthorityCloudSides.LogicalRight), right, rightRecovery,
            prepared.PrivateStaging.NamespaceId, default);
        Assert(leftAuthority.LogicalSide == SyncAuthorityCloudSides.LogicalLeft &&
               rightAuthority.LogicalSide == SyncAuthorityCloudSides.LogicalRight &&
               leftAuthority.EndpointRootBindingId == left.PairRootBindingId &&
               rightAuthority.EndpointRootBindingId == right.PairRootBindingId,
            "each target keeps its own logical side and its own endpoint pair root under the shared Recovery");
        Assert(leftAuthority.RecoveryRootBindingId == rightAuthority.RecoveryRootBindingId &&
               leftAuthority.RecoveryRootBindingId == left.RecoveryRootBindingId &&
               leftAuthority.RecoveryCorpusId == rightAuthority.RecoveryCorpusId,
            "both targets verify against the same canonical retained Recovery row and the same actual corpus");

        var requestsBefore = rig.Wire.TotalRequests;
        await AssertRecoveryRefusedAsync(() => SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
                prepared.Port(SyncAuthorityCloudSides.LogicalRight), left, rightRecovery,
                prepared.PrivateStaging.NamespaceId, default),
            "another logical side than the exact target port");
        await AssertRecoveryRefusedAsync(() => SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
                prepared.Port(SyncAuthorityCloudSides.LogicalRight),
                right with { ProviderAccountId = "provider-account-substituted-" + pair.Id }, rightRecovery,
                prepared.PrivateStaging.NamespaceId, default),
            "not this exact logical port's own provider account");
        await AssertRecoveryRefusedAsync(() => SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
                prepared.Port(SyncAuthorityCloudSides.LogicalRight),
                right with { RecoveryCorpusId = "corpus-substituted-" + pair.Id }, rightRecovery,
                prepared.PrivateStaging.NamespaceId, default),
            "folder and ACTUAL corpus");
        await AssertRecoveryRefusedAsync(() => SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
                prepared.Port(SyncAuthorityCloudSides.LogicalRight),
                right with { RecoveryRootBindingId = "recovery-minted-" + pair.Id }, rightRecovery,
                prepared.PrivateStaging.NamespaceId, default),
            "belongs to another recovery authority");
        await AssertRecoveryRefusedAsync(() => SyncAuthorityCloudRecovery.RequireAuthorityAsync(rig.Db,
                prepared.Port(SyncAuthorityCloudSides.LogicalRight),
                right with { RecoveryRootBindingId = right.PairRootBindingId }, rightRecovery,
                prepared.PrivateStaging.NamespaceId, default),
            "a pair root is never its own recovery boundary");
        Assert(rig.Wire.TotalRequests == requestsBefore,
            "every substituted-fact refusal is decided from retained rows and reached no provider request");
    }

    /// <summary>The exact refusal of one recovery fact that must never be accepted.</summary>
    private static async Task AssertRecoveryRefusedAsync(Func<Task> action, string fragment)
    {
        string? refusal = null;
        try
        {
            await action();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            refusal = error.GetType().Name + ": " + error.Message;
        }
        Assert(refusal is not null && refusal.Contains(fragment, StringComparison.OrdinalIgnoreCase),
            "the substituted recovery facts are refused for the exact reason '" + fragment + "': " +
            (refusal ?? "the call unexpectedly succeeded instead of refusing"));
    }

    // ----- G. both endpoint orderings through the real Host and both real adapters ---------------------

    private static async Task HostPrepareBothOrderingsAsync()
    {
        using var rig = await HostRig.CreateAsync("both-orderings");
        var dropboxLeft = HostDropbox("bl", "/Dropbox Left");
        var driveRight = HostDrive("dr", "/Drive Right");
        var driveLeft = HostDrive("dl", "/Drive Left");
        var dropboxRight = HostDropbox("br", "/Dropbox Right");
        foreach (var spec in new[] { dropboxLeft, driveRight, driveLeft, dropboxRight })
            await rig.AddEndpointAsync(spec);
        var first = await rig.AddPairAsync("orderings-one", dropboxLeft, driveRight);
        var second = await rig.AddPairAsync("orderings-two", driveLeft, dropboxRight);

        // The prerequisite is the Host's own admission, not a fixture convention: the existing root
        // registration refuses a pair that is not retained, so the caller retains the pair definition first,
        // and the prepare path then refuses that retained pair's own identity unless the caller names the pair
        // it is binding. A caller that omits ReplacingPairId is refused by that same guard.
        var unnamed = await rig.RefusalAsync(new SyncAuthorityRuntimeHost.CloudPairHostPrepareRequest(first.Pair,
            first.LeftRoot, first.RightRoot));
        Assert(unnamed is not null && unnamed.Contains("already uses this identity or name", StringComparison.Ordinal),
            "the Host refuses a retained pair's identity unless the caller names the pair it is binding: "
            + unnamed);

        var prepared = await PrepareBothSidesAsync(rig, first, withStaging: true);
        Assert(prepared.Left.RecoveryCorpusId == PilotPath.DropboxNamespaceRootIdentity(dropboxLeft.RootNamespaceId),
            "the Dropbox recovery authority is scoped to the namespace-root corpus its verified walk terminated at");
        Assert(prepared.Right.RecoveryCorpusId == rig.Wire.DriveRootId(driveRight.AccountId),
            "the Drive recovery authority is scoped to the opaque My Drive root id files/root returned");
        Assert(prepared.PrivateStaging.NamespaceId == first.Id &&
               prepared.PrivateStaging.Local.RootPath.Length > 0 &&
               prepared.PrivateStaging.Local.MarkerContentHash.Length > 0,
            "private staging is this pair's own completed control namespace beside the protected state anchor");
        Assert(await rig.ControlNamespaceRowsAsync(first.Id) == 1,
            "the pair keeps exactly one retained private staging binding");

        var reopened = await PrepareBothSidesAsync(rig, first, withStaging: false);
        Assert(reopened.PrivateStaging == prepared.PrivateStaging,
            "the read-only InspectBoundAsync reopen returns the identical private staging identity");
        Assert(reopened.Left == prepared.Left && reopened.Right == prepared.Right,
            "the reopen re-resolves the identical retained account, root and recovery authority for both sides");

        var secondPrepared = await PrepareBothSidesAsync(rig, second);
        Assert(secondPrepared.Left.Provider == SyncProviders.GoogleDrive &&
               secondPrepared.Right.Provider == SyncProviders.Dropbox,
            "the reversed endpoint ordering resolves Drive on the left and Dropbox on the right");
        Assert(rig.Wire.DropboxReads.Contains(dropboxLeft.RootNamespaceId + "::" + dropboxLeft.FolderId),
            "the reversed ordering re-read the retained Dropbox authority through its own bound account");
        Assert(rig.Wire.DriveReads.Contains(driveRight.AccountId + "::files/" + driveRight.FolderId),
            "the reversed ordering re-read the retained Drive authority through its own bound account");
    }

    // ----- H. same provider, two distinct accounts -----------------------------------------------------

    private static async Task SameProviderDistinctAccountsAsync()
    {
        using var rig = await HostRig.CreateAsync("same-provider-accounts");
        var left = HostDropbox("spa-left", "/Dropbox Spa Left");
        var right = HostDropbox("spa-right", "/Dropbox Spa Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        Assert(left.ProviderAccountId != right.ProviderAccountId,
            "the fixture retains two distinct Dropbox accounts");
        var pair = await rig.AddPairAsync("same-provider-distinct-accounts", left, right);
        var prepared = await PrepareBothSidesAsync(rig, pair);
        Assert(prepared.Left.ProviderAccountId != prepared.Right.ProviderAccountId &&
               prepared.Left.ConnectionId != prepared.Right.ConnectionId,
            "each logical side of a same-provider pair resolves through its own distinct retained account");
        Assert(rig.Wire.DropboxReads.Contains(left.RootNamespaceId + "::" + left.FolderId),
            "registering the right root re-read the retained same-provider left root through its own account");
    }

    // ----- I. one account, two distinct folders, ONE shared canonical Recovery authority ----------------

    private static async Task SameAccountDistinctFoldersAsync()
    {
        using var rig = await HostRig.CreateAsync("same-account-distinct-folders");
        var firstFolder = HostDropbox("sadf", "/Dropbox Sadf One");
        var secondFolder = firstFolder with
        {
            FolderId = "id:host-folder-sadf-two", FolderPath = "/Dropbox Sadf Two"
        };
        await rig.AddEndpointAsync(firstFolder);
        await rig.AddEndpointAsync(secondFolder);
        Assert(firstFolder.ProviderAccountId == secondFolder.ProviderAccountId &&
               firstFolder.FolderId != secondFolder.FolderId,
            "the fixture retains one Dropbox account with two distinct folder identities");
        var pair = await rig.AddPairAsync("same-account-distinct-folders", firstFolder, secondFolder);
        var definition = await rig.PairDefinitionAsync(pair.Id);

        // Two disjoint folders of ONE retained account and corpus legitimately map their two logical sides to
        // the SAME verified CC2E Recovery authority, so this composition prepares rather than being refused.
        var prepared = await PrepareBothSidesAsync(rig, pair, withStaging: true, sharedRecoveryAuthority: true);
        Assert(prepared.Left.PairRootBindingId == pair.LeftRoot &&
               prepared.Right.PairRootBindingId == pair.RightRoot &&
               prepared.Left.PairRootBindingId != prepared.Right.PairRootBindingId,
            "both disjoint folders keep their own distinct retained pair-root authority");
        Assert(prepared.Left.FolderId == firstFolder.FolderId &&
               prepared.Right.FolderId == secondFolder.FolderId &&
               prepared.Left.NamespaceId == firstFolder.EndpointNamespaceId &&
               prepared.Right.NamespaceId == secondFolder.EndpointNamespaceId,
            "each side keeps its own exact folder and namespace identity under the one shared account");
        Assert(prepared.Left.ProviderAccountId == prepared.Right.ProviderAccountId &&
               prepared.Left.ProviderAccountId == firstFolder.ProviderAccountId,
            "both sides resolve through the one exact retained Dropbox account");
        Assert(prepared.Left.RecoveryRootBindingId == prepared.Right.RecoveryRootBindingId &&
               prepared.Left.RecoveryRootBindingId == pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalLeft),
            "both sides share the ONE exact canonical Recovery authority of that account and corpus: "
            + prepared.Left.RecoveryRootBindingId);
        Assert(await RecoveryCountAsync(rig, pair) == 1 &&
               await rig.CountAsync("SELECT count(*) FROM recovery_root_binding;") == 1,
            "exactly one canonical recovery authority was retained, with no per-side duplicate and no second "
            + "Recovery folder for the shared corpus");
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + pair.Id
                   + "' AND side IN ('" + SyncAuthorityCloudSides.LogicalLeft + "','"
                   + SyncAuthorityCloudSides.LogicalRight + "') AND state='active';") == 2,
            "both distinct folder roots were resolved and registered as this pair's two active logical roots");
        Assert(rig.Wire.DropboxReads.Contains(firstFolder.RootNamespaceId + "::" + firstFolder.FolderId) &&
               rig.Wire.DropboxReads.Contains(secondFolder.RootNamespaceId + "::" + secondFolder.FolderId),
            "each folder root was read back through the real adapter over the shared account's own transport");
        Assert(await rig.PairDefinitionAsync(pair.Id) == definition,
            "the prepared one-account pair's retained definition is byte-identical to the retained pair");
        Assert(prepared.PrivateStaging.NamespaceId == pair.Id,
            "the prepared one-account pair reached its own completed private staging identity");

        await AssertSharedRecoveryAllocationAndRefusalsAsync(rig, pair, prepared);
    }

    // ----- J. one Google account, two distinct folders, the same shared canonical Recovery authority ----

    private static async Task SameAccountDistinctFoldersDriveAsync()
    {
        using var rig = await HostRig.CreateAsync("same-account-distinct-folders-drive");
        var firstFolder = HostDrive("sadd", "/Drive Sadd One");
        var secondFolder = firstFolder with
        {
            FolderId = "gd-folder-sadd-two", FolderPath = "/Drive Sadd Two"
        };
        await rig.AddEndpointAsync(firstFolder);
        await rig.AddEndpointAsync(secondFolder);
        Assert(firstFolder.ProviderAccountId == secondFolder.ProviderAccountId &&
               firstFolder.FolderId != secondFolder.FolderId,
            "the fixture retains one Google account with two distinct folder identities");
        var pair = await rig.AddPairAsync("same-account-distinct-folders-drive", firstFolder, secondFolder);
        var definition = await rig.PairDefinitionAsync(pair.Id);

        // The same one-account/one-corpus composition on the Drive provider, which also prepares: the exact
        // CC2E recovery scope resolves to the one retained recovery authority for both logical sides.
        var prepared = await PrepareBothSidesAsync(rig, pair, withStaging: true, sharedRecoveryAuthority: true);
        Assert(prepared.Left.PairRootBindingId == pair.LeftRoot &&
               prepared.Right.PairRootBindingId == pair.RightRoot &&
               prepared.Left.PairRootBindingId != prepared.Right.PairRootBindingId,
            "both disjoint Drive folders keep their own distinct retained pair-root authority");
        Assert(prepared.Left.FolderId == firstFolder.FolderId &&
               prepared.Right.FolderId == secondFolder.FolderId &&
               prepared.Left.NamespaceId == firstFolder.EndpointNamespaceId &&
               prepared.Right.NamespaceId == secondFolder.EndpointNamespaceId,
            "each Drive side keeps its own exact folder and namespace identity under the one shared account");
        Assert(prepared.Left.ProviderAccountId == prepared.Right.ProviderAccountId &&
               prepared.Left.ProviderAccountId == firstFolder.ProviderAccountId,
            "both Drive sides resolve through the one exact retained Google account");
        Assert(prepared.Left.RecoveryRootBindingId == prepared.Right.RecoveryRootBindingId &&
               prepared.Left.RecoveryRootBindingId == pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalLeft),
            "both Drive sides share the ONE exact canonical Recovery authority of that account and corpus: "
            + prepared.Left.RecoveryRootBindingId);
        Assert(await RecoveryCountAsync(rig, pair) == 1 &&
               await rig.CountAsync("SELECT count(*) FROM recovery_root_binding;") == 1,
            "exactly one canonical recovery authority was retained for the one Google account and corpus");
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + pair.Id
                   + "' AND side IN ('" + SyncAuthorityCloudSides.LogicalLeft + "','"
                   + SyncAuthorityCloudSides.LogicalRight + "') AND state='active';") == 2,
            "both Drive folder roots of that one account were resolved and registered as active logical roots");
        // D2: the verified-hierarchy producer retained one observation per just-verified active Drive root.
        Assert(await rig.CountAsync("SELECT count(*) FROM history_event WHERE " +
                   "event_kind='opaque_hierarchy_retained';") == 2,
            "the verified-hierarchy producer retained exactly one observation per active Google Drive root");
        Assert(rig.Wire.DriveReads.Contains(firstFolder.AccountId + "::files/" + firstFolder.FolderId) &&
               rig.Wire.DriveReads.Contains(firstFolder.AccountId + "::files/" + secondFolder.FolderId),
            "each Drive root was read back through the real adapter over the shared account's own transport");
        Assert(await rig.PairDefinitionAsync(pair.Id) == definition,
            "the prepared one-account Drive pair's retained definition is byte-identical to the retained pair");
        Assert(prepared.PrivateStaging.NamespaceId == pair.Id,
            "the prepared one-account Drive pair reached its own completed private staging identity");

        await AssertSharedRecoveryAllocationAndRefusalsAsync(rig, pair, prepared);
    }

    // ----- K. the reopen retains identity and writes nothing -------------------------------------------

    private static async Task ReopenRetainsIdentityAsync()
    {
        using var rig = await HostRig.CreateAsync("reopen-retains-identity");
        var left = HostDropbox("rk-left", "/Dropbox Rk Left");
        var right = HostDrive("rk-right", "/Drive Rk Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("reopen-retains-identity", left, right);
        var created = await PrepareBothSidesAsync(rig, pair, withStaging: true);
        var authorities = await rig.AuthoritySnapshotAsync();
        var definition = await rig.PairDefinitionAsync(pair.Id);
        var reopened = await PrepareBothSidesAsync(rig, pair, withStaging: false);
        Assert(reopened.PrivateStaging == created.PrivateStaging,
            "the ordinary prepare reopened the identical completed staging identity without reallocating it");
        Assert(reopened.Left == created.Left && reopened.Right == created.Right,
            "the reopen retained both sides' exact account, root and recovery identity");
        Assert((await rig.AuthoritySnapshotAsync()).SequenceEqual(authorities),
            "the read-only reopen wrote no new and no changed retained authority row");
        Assert(await rig.PairDefinitionAsync(pair.Id) == definition,
            "the reopen left the retained pair definition byte-identical");
        Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
            "the reopen did not allocate a second private staging binding");
    }

    // ----- L. a changed retained root refuses the new binding ------------------------------------------

    private static async Task ChangedRetainedRootRefusedAsync()
    {
        using var rig = await HostRig.CreateAsync("changed-retained-root");
        var dropboxOne = HostDropbox("cr-one", "/Dropbox Cr One");
        var driveOne = HostDrive("cr-one-right", "/Drive Cr One");
        var dropboxTwo = HostDropbox("cr-two", "/Dropbox Cr Two");
        var driveTwo = HostDrive("cr-two-right", "/Drive Cr Two");
        foreach (var spec in new[] { dropboxOne, driveOne, dropboxTwo, driveTwo })
            await rig.AddEndpointAsync(spec);
        var first = await rig.AddPairAsync("cr-one", dropboxOne, driveOne);
        await PrepareBothSidesAsync(rig, first);
        var before = await rig.AuthoritySnapshotAsync();

        // The retained root's provider folder moved, so its retained display path is now stale evidence.
        rig.Wire.MoveDropboxFolder(dropboxOne.FolderId, "/Dropbox Cr One Moved");
        var second = await rig.AddPairAsync("cr-two", dropboxTwo, driveTwo);
        var refusal = await rig.RefusalAsync(rig.Request(second));
        Assert(refusal is not null && refusal.Contains("no longer resolves", StringComparison.Ordinal),
            "a changed retained root refuses the new binding instead of being omitted: " + refusal);
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + second.Id + "';") == 0,
            "the refused binding wrote no root authority for the new pair");
        Assert(await RecoveryCountAsync(rig, second) == 0,
            "the refused binding wrote no recovery authority for the new pair");
        Assert((await rig.AuthoritySnapshotAsync()).SequenceEqual(before),
            "every existing legitimate authority row is unchanged by the refusal");
    }

    // ----- M. a missing or stale hierarchy refuses -----------------------------------------------------

    private static async Task MissingAndStaleHierarchyRefusedAsync()
    {
        using var rig = await HostRig.CreateAsync("missing-and-stale-hierarchy");

        // (a) the candidate's exact provider folder identity is no longer served at all.
        var missing = HostDropbox("mh-missing", "/Dropbox Mh Missing");
        var missingRight = HostDrive("mh-missing-right", "/Drive Mh Missing");
        foreach (var spec in new[] { missing, missingRight }) await rig.AddEndpointAsync(spec);
        rig.Wire.SetDropboxMissing(missing.FolderId, true);
        var missingPair = await rig.AddPairAsync("mh-missing", missing, missingRight);
        var missingRefusal = await rig.RefusalAsync(rig.Request(missingPair));
        Assert(missingRefusal is not null,
            "a candidate cloud root whose provider folder is missing fails closed: " + missingRefusal);
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + missingPair.Id + "';") == 0,
            "the missing-hierarchy refusal wrote no root authority");

        // (b) the candidate's ancestry changes between the two fresh readback passes.
        var stale = HostDrive("mh-stale", "/Drive Mh Stale");
        var staleRight = HostDropbox("mh-stale-right", "/Dropbox Mh Stale");
        foreach (var spec in new[] { stale, staleRight }) await rig.AddEndpointAsync(spec);
        rig.Wire.DriftDriveVersion(stale.FolderId, afterReads: 1);
        var stalePair = await rig.AddPairAsync("mh-stale", stale, staleRight);
        var staleRefusal = await rig.RefusalAsync(rig.Request(stalePair));
        Assert(staleRefusal is not null && staleRefusal.Contains("drifted", StringComparison.Ordinal),
            "a stale hierarchy that drifts between the two readbacks is refused rather than used: "
            + staleRefusal);
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + stalePair.Id + "';") == 0,
            "the stale-hierarchy refusal wrote no root authority");
    }

    // ----- N. unknown and unreachable retained evidence refuses ---------------------------------------

    private static async Task UnknownAndUnreachableEvidenceRefusedAsync()
    {
        // (a) unknown evidence: a retained authority whose active provider account no longer agrees fails the
        // whole retained-set snapshot closed rather than being quietly dropped from the comparison.
        using (var rig = await HostRig.CreateAsync("unknown-retained-evidence"))
        {
            var oneLeft = HostDropbox("un-one", "/Dropbox Un One");
            var oneRight = HostDrive("un-one-right", "/Drive Un One");
            var twoLeft = HostDropbox("un-two", "/Dropbox Un Two");
            var twoRight = HostDrive("un-two-right", "/Drive Un Two");
            foreach (var spec in new[] { oneLeft, oneRight, twoLeft, twoRight })
                await rig.AddEndpointAsync(spec);
            var first = await rig.AddPairAsync("un-one", oneLeft, oneRight);
            await PrepareBothSidesAsync(rig, first);
            await rig.ScalarAsync("UPDATE provider_account SET status='revoked' WHERE provider_account_id='"
                                  + oneLeft.ProviderAccountId + "';");
            var second = await rig.AddPairAsync("un-two", twoLeft, twoRight);
            var refusal = await rig.RefusalAsync(rig.Request(second));
            Assert(refusal is not null &&
                   refusal.Contains("does not agree with its active provider account", StringComparison.Ordinal),
                "a retained root whose provider account no longer matches fails the comparison closed: " + refusal);
            Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + second.Id + "';") == 0,
                "the unknown-evidence refusal wrote no root authority");
        }

        // (b) unreachable evidence: a retained same-provider authority whose own exact account is no longer
        // reachable at all, rather than a retained root omitted from the comparison set.
        using (var rig = await HostRig.CreateAsync("unreachable-retained-evidence"))
        {
            var ghost = HostDropbox("un-ghost", "/Dropbox Un Ghost");
            var ghostRight = HostDrive("un-ghost-right", "/Drive Un Ghost");
            await rig.Db.RegisterProviderAccountAsync(ghost.ProviderAccountId, rig.InstallationId,
                ghost.AccountId, ghost.RootNamespaceId, ghost.HomeNamespaceId, "CC4A host unreachable", 1,
                cancellationToken: default, provider: ghost.Provider);
            var ghostPair = await rig.AddPairAsync("un-ghost", ghost, ghostRight);
            const string ghostRoot = "root-un-ghost-retained";
            await rig.Db.RegisterCloudEndpointRootBindingAsync(ghostRoot, ghostPair.Id,
                SyncAuthorityCloudSides.LogicalLeft, 1, rig.InstallationId, ghost.Provider,
                ghost.ProviderAccountId, ghost.EndpointNamespaceId, ghost.FolderId, ghost.FolderPath,
                cancellationToken: default, hierarchies: new[] { Chain(ghost, ghostRoot) });

            var real = HostDropbox("un-real", "/Dropbox Un Real");
            var realRight = HostDrive("un-real-right", "/Drive Un Real");
            foreach (var spec in new[] { real, realRight }) await rig.AddEndpointAsync(spec);
            var pair = await rig.AddPairAsync("un-real", real, realRight);
            var refusal = await rig.RefusalAsync(rig.Request(pair));
            Assert(refusal is not null && refusal.Contains("no connected account", StringComparison.Ordinal),
                "a retained authority whose own account is unreachable is refused rather than omitted: "
                + refusal);
            Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + pair.Id + "';") == 0,
                "the unreachable-evidence refusal wrote no root authority for the new pair");
            Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE root_binding_id='" + ghostRoot
                       + "' AND state='active';") == 1,
                "the unreachable retained authority itself is left exactly as it was retained");
        }
    }

    // ----- O. a late refusal must leave no usable partial authority -----------------------------------

    private static async Task LateRefusalNoAuthorityAsync()
    {
        using var rig = await HostRig.CreateAsync("late-refusal");
        var left = HostDropbox("lr-left", "/Dropbox Lr Left");
        var right = HostDrive("lr-right", "/Drive Lr Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("late-refusal", left, right);
        var before = await rig.AuthoritySnapshotAsync();
        var definition = await rig.PairDefinitionAsync(pair.Id);

        // the two-sided registration is atomic, so the right side being refused rolls BOTH own rows
        // back rather than leaving the left side's row behind.
        rig.Wire.SetDriveMissing(right.FolderId, true);
        var refusal = await rig.RefusalAsync(rig.Request(pair));
        Assert(refusal is not null,
            "the right-hand side is refused inside the atomic two-sided registration: " + refusal);

        var after = await rig.AuthoritySnapshotAsync();
        var added = after.Except(before, StringComparer.Ordinal).ToArray();
        Assert(added.Length == 0,
            "the atomic two-sided registration left NO retained authority row when the right side was refused: "
            + string.Join(" ; ", added));
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE root_binding_id='" + pair.RightRoot
                   + "';") == 0,
            "the refused right side left no root authority behind");
        Assert(await RecoveryCountAsync(rig, pair) == 0,
            "the refused binding reached no recovery authority for either side");
        Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 0 &&
               await rig.CountAsync("SELECT count(*) FROM control_namespace;") == 0,
            "the refused binding created no private staging binding, so the pair has no staging identity");
        Assert(await rig.PairDefinitionAsync(pair.Id) == definition,
            "the retained pair definition is byte-identical, so no partial pair became runnable");
        Assert(before.SequenceEqual(after.Where(row => !added.Contains(row, StringComparer.Ordinal))),
            "every pre-existing legitimate authority row is unchanged by the refusal");

        // the rolled-back refusal leaves NO row, so the identical request is refused again, and only a
        // genuine re-read of both providers through the real adapters lets the identical binding complete.
        var again = await rig.RefusalAsync(rig.Request(pair));
        Assert(again is not null, "the rolled-back refusal does not make the refused binding preparable");
        var leftover = await rig.RootBindingAsync(pair.LeftRoot);
        Assert(string.IsNullOrWhiteSpace(leftover),
            "the atomic two-sided refusal left no left-root preparation row: " + leftover);
        rig.Wire.SetDriveMissing(right.FolderId, false);
        var prepared = await PrepareBothSidesAsync(rig, pair);
        Assert(prepared.Left.PairRootBindingId == pair.LeftRoot &&
               !string.IsNullOrWhiteSpace(await rig.RootBindingAsync(pair.LeftRoot)),
            "the completed binding wrote the left root row after the atomic rollback");
    }

    // ----- P. a refused recovery decision after both candidate checks ---------------------------------

    private static async Task RecoveryEvidenceRefusedAsync()
    {
        using var rig = await HostRig.CreateAsync("recovery-evidence-refused");
        // The selected left root IS the recommended Recovery container, so the recovery decision refuses
        // AFTER both candidate checks have already registered their roots.
        var insideRecovery = HostDropbox("re-inside", "/Apps/Sync Shark/Recovery");
        var insideRight = HostDrive("re-inside-right", "/Drive Re Inside");
        foreach (var spec in new[] { insideRecovery, insideRight }) await rig.AddEndpointAsync(spec);
        var insidePair = await rig.AddPairAsync("re-inside", insideRecovery, insideRight);
        var refusal = await rig.RefusalAsync(rig.Request(insidePair));
        Assert(refusal is not null &&
               refusal.Contains("already contains the recommended Recovery", StringComparison.Ordinal),
            "a selected root that is the recommended Recovery container is refused: " + refusal);
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + insidePair.Id
                   + "' AND state='active';") == 2,
            "both candidate roots were registered before the recovery decision refused");
        Assert(await RecoveryCountAsync(rig, insidePair) == 0,
            "the refused recovery decision retained no recovery authority");
        Assert(await rig.ControlNamespaceRowsAsync(insidePair.Id) == 0,
            "the refused recovery decision created no private staging binding");
        var again = await rig.RefusalAsync(rig.Request(insidePair));
        Assert(again is not null &&
               again.Contains("already contains the recommended Recovery", StringComparison.Ordinal),
            "the refused recovery decision is deterministic and grants nothing on a retry");

        // The two candidate rows confer no authority by themselves: a disjoint pair in the same authority
        // binds completely and re-verifies the retained candidate through its own account.
        var cleanLeft = HostDropbox("re-clean", "/Dropbox Re Clean");
        var cleanRight = HostDrive("re-clean-right", "/Drive Re Clean");
        foreach (var spec in new[] { cleanLeft, cleanRight }) await rig.AddEndpointAsync(spec);
        var cleanPair = await rig.AddPairAsync("re-clean", cleanLeft, cleanRight);
        var prepared = await PrepareBothSidesAsync(rig, cleanPair);
        Assert(prepared.PrivateStaging.NamespaceId == cleanPair.Id,
            "the later legitimate pair reached its own completed private staging identity");
        Assert(rig.Wire.DropboxReads.Contains(insideRecovery.RootNamespaceId + "::" + insideRecovery.FolderId),
            "the later binding re-read the retained candidate root through its own bound account");
    }


    // ----- Q. the Host's own create-once staging path, exercised through real SQLite -------------------
    // CORRPROOF-3: CreatePrivateStaging=true completes on the first cloud/cloud binding with no fixture-created
    // local root, the operation member references this pair's exact retained logical-left root, a repeat of the
    // exact request creates no second namespace or allocation, and CreatePrivateStaging=false reopens the
    // identical completed identity.

    private static async Task StageCreateOnceCompletesAsync()
    {
        using var rig = await HostRig.CreateAsync("stage-create-once");
        var left = HostDropbox("sco-left", "/Dropbox Sco Left");
        var right = HostDrive("sco-right", "/Drive Sco Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("stage-create-once", left, right);
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + pair.Id + "';") == 0 &&
               await rig.CountAsync("SELECT count(*) FROM control_namespace;") == 0 &&
               await rig.CountAsync("SELECT count(*) FROM namespace_allocation;") == 0,
            "the fixture hand-creates no local root, no control namespace and no allocation before the prepare");

        var created = await rig.PrepareAsync(pair, createStaging: true);
        Assert(created.PrivateStaging.NamespaceId == pair.Id &&
               created.PrivateStaging.Local.RootPath.Length > 0 &&
               created.PrivateStaging.Local.MarkerContentHash.Length > 0,
            "the real CreatePrivateStaging=true path completed this pair's own private staging identity");
        Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1 &&
               await rig.CountAsync("SELECT count(*) FROM control_namespace;") == 1,
            "the completed binding retains exactly one private staging control namespace for the pair");
        var stagingMembers = await rig.RowsAsync(
            "SELECT member.root_binding_id FROM operation_member member "
            + "JOIN operation operation ON operation.operation_id=member.operation_id "
            + "WHERE operation.pair_id='" + pair.Id + "' AND member.role='lock';");
        Assert(stagingMembers.Count == 1 && stagingMembers[0] == pair.LeftRoot,
            "the staging operation member references this pair's exact retained logical-left root, not the pair "
            + "id and not a recovery authority: " + string.Join(" ; ", stagingMembers));
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + pair.Id
                   + "' AND side='local' AND state='active';") == 1,
            "the pair retains exactly one active logical-left root for the staging member to reference");
        var allocationsBefore = await rig.CountAsync(
            "SELECT count(*) FROM namespace_allocation allocation JOIN operation operation "
            + "ON operation.operation_id=allocation.operation_id WHERE operation.pair_id='" + pair.Id + "';");
        var controlRootsBefore = await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='"
                   + pair.Id + "' AND side='local_control';");
        Assert(allocationsBefore == 1 && controlRootsBefore == 1,
            "the completed binding registered exactly one namespace allocation and one local control root");

        var repeat = await rig.PrepareAsync(pair, createStaging: true);
        Assert(repeat.PrivateStaging == created.PrivateStaging,
            "a repeat of the exact create-once request returns the identical completed staging identity");
        Assert(await rig.CountAsync("SELECT count(*) FROM namespace_allocation allocation JOIN operation operation "
                   + "ON operation.operation_id=allocation.operation_id WHERE operation.pair_id='" + pair.Id
                   + "';") == allocationsBefore &&
               await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + pair.Id
                   + "' AND side='local_control';") == controlRootsBefore &&
               await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
            "the repeat created no second physical namespace, control root binding or staging binding");
        Assert(await rig.CountAsync("SELECT count(*) FROM resource_allocation;") == 0,
            "the staging allocation reserves no resource-allocation row of its own");

        var reopened = await rig.PrepareAsync(pair, createStaging: false);
        Assert(reopened.PrivateStaging == created.PrivateStaging,
            "CreatePrivateStaging=false reopens the identical completed staging identity");
        Assert(reopened.Left == created.Left && reopened.Right == created.Right,
            "the read-only reopen also re-resolves both sides' identical account, root and recovery authority");
        Assert(rig.Wire.TotalRequests > 0,
            "both prepares reached their provider reads through the real adapters");
    }

    // ----- Z. OV637-6: the nested opaque pair now completes through the real Host preparation ---------------

    private static async Task OpaqueHostRunProbeAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The opaque Host probe requires the Windows-native Host rig.");
        using var rig = await HostRig.CreateAsync("opaque-host-run");
        var first = HostDrive("ohr-a", "/Drive Ohr Parent");
        var firstMirror = first with { FolderId = "gd-folder-ohr-a2", FolderPath = "/Drive Ohr Parent Two" };
        var nested = first with { FolderId = "gd-folder-ohr-b", FolderPath = "/Drive Ohr Parent/Nested" };
        var nestedMirror = first with { FolderId = "gd-folder-ohr-b2", FolderPath = "/Drive Ohr Parent Two/Nested" };
        foreach (var spec in new[] { first, firstMirror, nested, nestedMirror }) await rig.AddEndpointAsync(spec);
        var parent = await rig.AddPairAsync("ohr-parent", first, firstMirror);
        var child = await rig.AddPairAsync("ohr-child", nested, nestedMirror);
        try
        {
            // The one-account nested composition shares the canonical Recovery authority across both pairs, so
            // the pair-specific recovery assertions of PrepareBothSidesAsync do not apply; the real production
            // prepare entry is used directly.
            var preparedParent = await rig.PrepareAsync(parent, createStaging: true);
            Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + parent.Id +
                       "' AND state='active';") == 3,
                "the enclosing opaque pair prepared both own roots plus its one canonical Recovery authority");
            var preparedChild = await rig.PrepareAsync(child, createStaging: true);
            Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + child.Id +
                       "' AND state='active';") == 3,
                "the NESTED opaque corresponding child pair completed through the real Host preparation");
            Assert(await rig.CountAsync("SELECT count(*) FROM history_event WHERE " +
                       "event_kind='opaque_hierarchy_retained';") == 4,
                "the D2 producer retained one observation per active opaque root of both pairs");
            Assert(preparedChild.Left.PairRootBindingId == child.LeftRoot &&
                   preparedChild.Right.PairRootBindingId == child.RightRoot,
                "the nested child kept its own two exact retained pair-root authorities");
        }
        catch (Exception error)
        {
            Assert(false, "the nested opaque Host preparation refused: " + error.GetType().Name + ": " +
                error.Message);
        }
    }
    // ----- Z2. OV637-6: the opaque execution half - a real related run with a real effect -------------------

    private static async Task OpaqueHostExecutionAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The opaque Host run requires the Windows-native Host rig.");
        using var rig = await HostRig.CreateAsync("opaque-host-execution");
        var leftSpec = HostDrive("ohe-a", "/Drive Ohe Parent");
        var leftMirror = leftSpec with { FolderId = "gd-folder-ohe-a2", FolderPath = "/Drive Ohe Parent Two" };
        var nested = leftSpec with { FolderId = "gd-folder-ohe-b", FolderPath = "/Drive Ohe Parent/Nested" };
        var nestedMirror = leftSpec with { FolderId = "gd-folder-ohe-b2", FolderPath = "/Drive Ohe Parent Two/Nested" };
        foreach (var spec in new[] { leftSpec, leftMirror, nested, nestedMirror }) await rig.AddEndpointAsync(spec);
        var enclosing = await rig.AddPairAsync("ohe-parent", leftSpec, leftMirror);
        var nestedPair = await rig.AddPairAsync("ohe-child", nested, nestedMirror);
        await rig.PrepareAsync(enclosing, createStaging: true);
        await rig.PrepareAsync(nestedPair, createStaging: true);
        // A real provider-issued file inside the enclosing left root, so the run has a real effect to make.
        rig.SeedFile(leftSpec, "ohe-payload.txt", "opaque host execution payload");
        var definitions = await rig.Db.LoadAllPairDefinitionsAsync();
        var enclosingReady = definitions.Single(item => item.Id == enclosing.Id) with
        {
            InitialReviewApproved = true, Enabled = true
        };
        var nestedReady = definitions.Single(item => item.Id == nestedPair.Id) with
        {
            InitialReviewApproved = true, Enabled = true
        };
        await rig.Db.RetainPairDefinitionAsync(enclosingReady, true);
        await rig.Db.RetainPairDefinitionAsync(nestedReady, false);
        try
        {
            var run = await rig.Host.RunPairsAsync([enclosingReady, nestedReady], "OV637 opaque related run");
            var moved = run.Items.Sum(item => (item.Result?.Uploaded ?? 0) + (item.Result?.Downloaded ?? 0));
            Assert(run.Items.All(item => item.FailureType is null),
                "both related opaque pairs finished their real run without a pair failure: " + string.Join(" ; ",
                    run.Items.Select(item => item.FailureType + ": " + item.FailureMessage)));
            Assert(moved > 0, "the real related run made a real transfer effect");
            var sweep = await rig.Host.RunPairsAsync([enclosingReady, nestedReady],
                "OV637 opaque closing sweep");
            var swept = sweep.Items.Sum(item => (item.Result?.Uploaded ?? 0) + (item.Result?.Downloaded ?? 0));
            Assert(swept == 0, "the current closing sweep replayed no effect: " + swept);

            // (a) the REVERSE order: the nested child leads the run and the enclosing pair still makes the effect.
            rig.SeedFile(leftSpec, "ohe-second.txt", "opaque host execution second payload");
            var reverse = await rig.Host.RunPairsAsync([nestedReady, enclosingReady], "OV637 opaque reverse run");
            var reversed = reverse.Items.Sum(item => (item.Result?.Uploaded ?? 0) + (item.Result?.Downloaded ?? 0));
            Assert(reverse.Items.All(item => item.FailureType is null) && reversed > 0,
                "the reverse-order related run still made the real effect: " + reversed + " ; " + string.Join(" ; ",
                    reverse.Items.Select(item => item.FailureType + ": " + item.FailureMessage)));

            // (b) a provider-INPUT-failed effect reaches the REAL effect_unknown state through the executor's own
            // RetainExecutionFailureAsync: the create is issued and its outcome is lost.
            rig.SeedFile(nested, "ohe-unknown.txt", "opaque host effect_unknown payload");
            rig.Wire.ProviderInputFailureForTest = request =>
                request.RequestUri!.AbsoluteUri.Contains("/upload/", StringComparison.Ordinal);
            try { await rig.Host.RunPairsAsync([enclosingReady, nestedReady], "OV637 opaque effect_unknown run"); }
            catch (Exception) { /* the run may surface the failure; the retained state is what matters */ }
            rig.Wire.ProviderInputFailureForTest = null;
            var unknown = await rig.CountAsync("SELECT count(*) FROM operation WHERE status='effect_unknown';");
            Assert(unknown > 0, "the provider-INPUT-failed effect really reached effect_unknown: " + unknown);
            // OV637-6 (c) the ALIASED UNKNOWN collision, corrected: the effect_unknown operation is a
            // create_remote whose DESTINATION member is the one that reserves a path, so the aliased
            // reservation must be constructed against the enclosing pair RIGHT root at the translated path.
            await rig.Db.RegisterObjectAsync("object-ohe-unknown-alias", "file");
            var aliasProfile = await rig.ScalarAsync(
                "SELECT capability_profile_id FROM capability_profile ORDER BY capability_profile_id LIMIT 1;");
            var aliasRevision = long.Parse(await rig.ScalarAsync(
                "SELECT configuration_revision FROM sync_pair WHERE pair_id='" + enclosing.Id + "';"),
                System.Globalization.CultureInfo.InvariantCulture);
            var admission = await rig.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", enclosing.Id,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await rig.Db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
            string? aliasRefusal = null;
            try
            {
                await rig.Db.CreateOperationAsync(new SyncAuthorityOperationPlan("op-ohe-unknown-alias",
                    "idempotency-ohe-unknown-alias", SyncAuthorityOperationKind.UpdateRemote, enclosing.Id,
                    "object-ohe-unknown-alias", aliasRevision, aliasProfile, "{}", 1,
                    [new SyncAuthorityMember("member-ohe-unknown-alias", enclosing.Id, "object-ohe-unknown-alias",
                        "lock", enclosing.RightRoot, null, null, null, "Nested/ohe-unknown.txt", "{}")], [], []), admission: admission);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                aliasRefusal = error.GetType().Name + ": " + error.Message;
            }
            Assert(aliasRefusal is not null &&
                   aliasRefusal.Contains("overlaps this exact root/path", StringComparison.Ordinal),
                "the ALIASED reservation against the effect_unknown destination is refused by the real fence: " +
                aliasRefusal);


        }
        catch (Exception error)
        {
            Assert(false, "the opaque execution half refused: " + error.GetType().Name + ": " + error.Message);
        }
    }
    // ----- R. the staging allocation's root authority, anchor and ownership stay fail-closed -------------
    // CORRPROOF-3: missing, foreign-pair and wrong-side root authority is refused at the real preparation and
    // allocator entry boundary, and a foreign state anchor, an unowned object at the anchored path or a
    // disappeared owned object stays fail-closed rather than being adopted, repaired or reallocated.

    private static async Task StageRootAuthorityRefusedAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The real-Host cloud pair binding proof requires the Windows private staging allocator.");
        using var rig = await HostRig.CreateAsync("stage-root-authority");
        var left = HostDropbox("sra-left", "/Dropbox Sra Left");
        var right = HostDrive("sra-right", "/Drive Sra Right");
        var otherLeft = HostDropbox("sra-other-left", "/Dropbox Sra Other Left");
        var otherRight = HostDrive("sra-other-right", "/Drive Sra Other Right");
        var unLeft = HostDropbox("sra-un-left", "/Dropbox Sra Un Left");
        var unRight = HostDrive("sra-un-right", "/Drive Sra Un Right");
        foreach (var spec in new[] { left, right, otherLeft, otherRight, unLeft, unRight })
            await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("sra", left, right);
        var other = await rig.AddPairAsync("sra-other", otherLeft, otherRight);
        var unprepared = await rig.AddPairAsync("sra-unprepared", unLeft, unRight);
        var created = await rig.PrepareAsync(pair, createStaging: true);
        await rig.PrepareAsync(other, createStaging: true);

        // (a) a foreign pair's retained root authority may never be this pair's staging member reference.
        var leftRootBefore = await rig.RootBindingAsync(pair.LeftRoot);
        var foreignRoot = await rig.RefusalAsync(rig.Request(pair, createStaging: true, leftRoot: other.LeftRoot));
        // added the atomic two-sided registration's preimage refusal, which now refuses this foreign
        // root id EARLIER and more precisely than the previous downstream authorities did. The case's intent -
        // a foreign pair's root authority is refused, never referenced - is unchanged and still required.
        Assert(foreignRoot is not null &&
               (foreignRoot.Contains("not bound to an exact retained cloud root", StringComparison.Ordinal) ||
                foreignRoot.Contains("collides with different authority", StringComparison.Ordinal) ||
                foreignRoot.Contains("already belongs to another pair", StringComparison.Ordinal)),
            "a foreign pair's root authority is refused instead of being referenced by this pair's staging: "
            + foreignRoot);
        Assert(await rig.RootBindingAsync(pair.LeftRoot) == leftRootBefore &&
               await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
            "the refusal left this pair's own retained logical-left root and its staging binding unchanged");
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE root_binding_id='" + other.LeftRoot
                   + "' AND pair_id='" + other.Id + "' AND state='active';") == 1,
            "the foreign pair's own retained root authority is left exactly as it was");

        // (b) the same retained root may not be reassigned to the opposite logical side.
        var wrongSide = await rig.RefusalAsync(rig.Request(pair, createStaging: true, leftRoot: pair.RightRoot,
            rightRoot: pair.LeftRoot));
        Assert(wrongSide is not null,
            "a retained root authority assigned to the wrong logical side is refused: " + wrongSide);
        Assert(await rig.RootBindingAsync(pair.LeftRoot) == leftRootBefore,
            "the pair's own retained logical-left root is unchanged by the refused wrong-side preparation");
        Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1 &&
               await rig.CountAsync("SELECT count(*) FROM namespace_allocation allocation JOIN operation operation "
                   + "ON operation.operation_id=allocation.operation_id WHERE operation.pair_id='" + pair.Id
                   + "';") == 1,
            "the refused wrong-side preparation created no second staging binding or allocation");

        // (c) a missing root authority is refused at the real allocator entry boundary, with no partial write.
        var unpreparedOperationsBefore = await rig.CountAsync("SELECT count(*) FROM operation WHERE pair_id='"
            + unprepared.Id + "';");
        var unpreparedControlBefore = await rig.CountAsync("SELECT count(*) FROM control_namespace;");
        var unpreparedPairControlBefore = await rig.ControlNamespaceRowsAsync(unprepared.Id);
        using (var anchor = PilotDirectoryLease.Open(rig.StateRoot))
        {
            anchor.VerifyBinding();
            var profileId = await rig.ScalarAsync(
                "SELECT capability_profile_id FROM capability_profile ORDER BY capability_profile_id LIMIT 1;");
            string? missing = null;
            try
            {
                await new SyncAuthorityLocalControlNamespaceAllocator(rig.Db).BindAsync(anchor, rig.InstallationId,
                    unprepared.Id, "root-missing-" + unprepared.Id, unprepared.Pair.PolicyRevision, profileId,
                    cancellationToken: default, side: SyncAuthorityCloudSides.LogicalLeft);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                missing = error.GetType().Name + ": " + error.Message;
            }
            Assert(missing is not null &&
                   (missing.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                    missing.Contains("constraint", StringComparison.OrdinalIgnoreCase)),
                "a missing root authority is refused at the real allocator entry boundary: " + missing);

            // (d) a foreign state anchor is never treated as this pair's own staging location.
            var foreignAnchorPath = IOPath.Combine(_root, "host", "stage-root-authority", "foreign", "state");
            Directory.CreateDirectory(foreignAnchorPath);
            using var foreignAnchor = PilotDirectoryLease.Open(foreignAnchorPath);
            foreignAnchor.VerifyBinding();
            string? foreignAnchorRefusal = null;
            try
            {
                await new SyncAuthorityLocalControlNamespaceAllocator(rig.Db).InspectBoundAsync(foreignAnchor,
                    pair.Id);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                foreignAnchorRefusal = error.GetType().Name + ": " + error.Message;
            }
            Assert(foreignAnchorRefusal is not null &&
                   foreignAnchorRefusal.Contains("no private staging allocation", StringComparison.Ordinal),
                "a foreign state anchor is refused rather than treated as this pair's staging location: "
                + foreignAnchorRefusal);
        }
        Assert(await rig.CountAsync("SELECT count(*) FROM operation WHERE pair_id='" + unprepared.Id + "';") ==
               unpreparedOperationsBefore &&
               await rig.CountAsync("SELECT count(*) FROM control_namespace;") == unpreparedControlBefore &&
               await rig.ControlNamespaceRowsAsync(unprepared.Id) == unpreparedPairControlBefore,
            "the refused allocator call left no operation and no control namespace behind");
        Assert(await rig.CountAsync("SELECT count(*) FROM root_binding WHERE pair_id='" + unprepared.Id + "';") == 0
               && await RecoveryCountAsync(rig, unprepared) == 0,
            "the refused allocator call wrote no root or recovery authority for the unprepared pair");

        // (e) an unowned object at the anchored path, and a disappeared owned object, both fail closed.
        static bool FailClosed(string? refusal) => refusal is not null &&
            (refusal.Contains("private staging", StringComparison.Ordinal) ||
             refusal.Contains("no longer resolves", StringComparison.Ordinal));
        var controlPath = created.PrivateStaging.Local.RootPath;
        Directory.Delete(controlPath, recursive: true);
        Directory.CreateDirectory(controlPath);
        var foreignObject = await rig.RefusalAsync(rig.Request(pair));
        Assert(FailClosed(foreignObject),
            "an unowned object at the pair's anchored path is refused rather than adopted: " + foreignObject);
        Directory.Delete(controlPath, recursive: true);
        var missingObject = await rig.RefusalAsync(rig.Request(pair));
        Assert(FailClosed(missingObject),
            "a missing owned staging object is refused rather than repaired: " + missingObject);
        var neverRecreated = await rig.RefusalAsync(rig.Request(pair, createStaging: true));
        Assert(FailClosed(neverRecreated),
            "the create-once path never silently re-creates a disappeared staging identity: " + neverRecreated);

        Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
            "every refusal left the pair's own retained staging binding intact");
        Assert(await rig.CountAsync("SELECT count(*) FROM recovery_root_binding WHERE recovery_root_binding_id='"
                   + pair.RecoveryRootId(SyncAuthorityCloudSides.LogicalLeft) + "' AND status='active';") == 1,
            "the pair's own canonical Recovery authority is still retained active after every refusal");
    }

    // ----- S. a retained non-default policy revision flows into the allocation it authorises -------------
    // Root-requested runtime evidence: the private staging allocation must carry the pair's OWN retained
    // policy revision rather than a hardcoded default, and an exact repeat and a read-only reopen must both
    // preserve the same physical namespace and allocation identity.

    private static async Task RetainedNonDefaultRevisionAsync()
    {
        using var rig = await HostRig.CreateAsync("retained-non-default-revision");
        var left = HostDropbox("rnd-left", "/Dropbox Rnd Left");
        var right = HostDrive("rnd-right", "/Drive Rnd Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("rnd", left, right, policyRevision: 7);
        Assert(pair.Pair.PolicyRevision == 7,
            "the fixture retains this pair at the non-default policy revision 7, not the initial revision");
        Assert(await rig.CountAsync("SELECT count(*) FROM pair_config_revision WHERE pair_id='" + pair.Id
                   + "' AND revision=7;") == 1 &&
               await rig.CountAsync("SELECT count(*) FROM pair_config_revision WHERE pair_id='" + pair.Id
                   + "' AND revision=1;") == 0,
            "only the revision 7 configuration row is retained for this pair");

        var created = await rig.PrepareAsync(pair, createStaging: true);
        var retainedOperations = await rig.RowsAsync(
            "SELECT operation_id || '|' || kind || '|' || configuration_revision FROM operation WHERE pair_id='"
            + pair.Id + "';");
        Assert(retainedOperations.Count == 1 &&
               retainedOperations[0].EndsWith("|7", StringComparison.Ordinal),
            "the one retained operation of this pair is its private staging allocation at the pair's own " +
            "retained revision 7, not a hardcoded revision: " + string.Join(" ; ", retainedOperations));
        Assert(await rig.CountAsync("SELECT configuration_revision FROM operation WHERE pair_id='" + pair.Id
                   + "';") == 7,
            "the stored AllocateControl operation configuration_revision is 7");
        var allocations = await rig.RowsAsync(
            "SELECT allocation.namespace_allocation_id FROM namespace_allocation allocation "
            + "JOIN operation operation ON operation.operation_id=allocation.operation_id "
            + "WHERE operation.pair_id='" + pair.Id + "';");
        Assert(allocations.Count == 1 && created.PrivateStaging.NamespaceId == pair.Id,
            "the revision 7 allocation retained exactly one physical namespace allocation for the pair");
        Assert(await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
            "the revision 7 allocation retained exactly one private staging binding");

        var repeat = await rig.PrepareAsync(pair, createStaging: true);
        Assert(repeat.PrivateStaging == created.PrivateStaging,
            "an exact repeat at revision 7 returns the identical completed staging identity");
        var reopened = await rig.PrepareAsync(pair, createStaging: false);
        Assert(reopened.PrivateStaging == created.PrivateStaging,
            "a read-only reopen at revision 7 preserves the same staging identity");
        Assert(await rig.CountAsync("SELECT configuration_revision FROM operation WHERE pair_id='" + pair.Id
                   + "';") == 7,
            "the repeat and the reopen both kept the operation at revision 7");
        var allocationsAfter = await rig.RowsAsync(
            "SELECT allocation.namespace_allocation_id FROM namespace_allocation allocation "
            + "JOIN operation operation ON operation.operation_id=allocation.operation_id "
            + "WHERE operation.pair_id='" + pair.Id + "';");
        Assert(allocationsAfter.SequenceEqual(allocations) &&
               await rig.ControlNamespaceRowsAsync(pair.Id) == 1,
            "the repeat and the reopen preserved the identical physical namespace allocation and the one " +
            "private staging binding");
    }

    // ----- T. the provider file content one cloud pair actually holds --------------------------------------
    // CC4B content read half: each provider side holds one fixed small nonempty text file, the EXISTING
    // metadata and list routes represent that file as a file with its own id, revision, length and content
    // hash rather than as one more folder node, the real public Host preview shows the two seeded paths as
    // rows of their own, and the real Dropbox and Google Drive adapters read the exact seeded bytes back
    // through the one small-file download route and the one media-read route this lane serves. No upload,
    // upload-session, conditional-update or recovery-copy route exists here, so the read half cannot widen.

    private static async Task ProviderFileContentAsync()
    {
        using var rig = await HostRig.CreateAsync("provider-file-content");
        var left = HostDropbox("pfc-left", "/Dropbox Pfc Left");
        var right = HostDrive("pfc-right", "/Drive Pfc Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var pair = await rig.AddPairAsync("pfc", left, right);
        var leftBytes = Encoding.UTF8.GetBytes(HostWire.DropboxSeededText);
        var rightBytes = Encoding.UTF8.GetBytes(HostWire.DriveSeededText);
        var leftSeed = rig.SeedFile(left, HostWire.DropboxSeededFileName, HostWire.DropboxSeededText);
        var rightSeed = rig.SeedFile(right, HostWire.DriveSeededFileName, HostWire.DriveSeededText);
        Assert(leftBytes.Length > 0 && rightBytes.Length > 0 && leftSeed.Size == leftBytes.Length &&
               rightSeed.Size == rightBytes.Length,
            "each provider side holds its own fixed nonempty seeded file of exactly its own byte length");
        Assert(leftSeed.Provider == SyncProviders.Dropbox &&
               rightSeed.Provider == SyncProviders.GoogleDrive &&
               leftSeed.ObjectId != rightSeed.ObjectId && leftSeed.Revision != rightSeed.Revision,
            "the two seeded files keep separate providers, object identities and revisions");

        var bound = await rig.PrepareAsync(pair, createStaging: true);
        Assert(bound.PrivateStaging.NamespaceId == pair.Id,
            "the pair holding seeded provider content still completed its own private staging identity");
        await rig.Host.RefreshBootstrapInventoryAsync(pair.Id);
        var page = await rig.Host.LoadBootstrapReviewPageAsync(pair.Id);
        Assert(page.InventoryComplete,
            "the real public Host preview resolved both cloud inventories with the seeded files present");
        var rows = page.Items;
        Assert(rows.Count == 2 && rows.All(row => row.RelativePath.Length > 0),
            "the public preview contains exactly the two expected nonempty provider file rows, observed "
            + rows.Count + ": " + string.Join(" ; ",
                rows.Select(row => row.RelativePath + " (" + row.Group + ")")));
        Assert(rows.All(row => row.ItemKind == "file") && rows.All(row => row.Effect.Length > 0) &&
               rows.All(row => row.Recommendation.Length > 0),
            "every previewed provider row is a file row carrying its own recommendation and effect");
        // The retained compatibility tags of a cloud/cloud pair are the legacy pair sides - the logical
        // left side keeps the tag "local" and the logical right side keeps the tag "dropbox" - so the
        // review groups and the previewed effects name REAL sides from the pair's own endpoint document.
        var document = pair.Pair.EndpointDocument
                       ?? throw new InvalidDataException("A cloud pair retains its endpoint document.");
        var leftEndpoint = document.Left
                           ?? throw new InvalidDataException("A cloud pair retains its left endpoint.");
        var rightEndpoint = document.Right
                            ?? throw new InvalidDataException("A cloud pair retains its right endpoint.");
        var dropboxRow = rows.Single(row => row.RelativePath == HostWire.DropboxSeededFileName);
        Assert(dropboxRow.Group == "Local only" && dropboxRow.LocalSize == leftBytes.Length &&
               dropboxRow.DropboxSize is null,
            "the seeded logical-left Dropbox path is previewed as that side's own file with its exact length: "
            + dropboxRow.Group + " / " + dropboxRow.LocalSize);
        Assert(string.Equals(dropboxRow.Effect, "Copy to " + SyncEndpointPresentation.SideLabel(SyncPairSide.Right)
                   + " — " + SyncEndpointPresentation.ShortLabel(rightEndpoint.Kind), StringComparison.Ordinal),
            "the seeded logical-left file's previewed effect copies it to the pair's own logical-right "
            + "endpoint rather than to a computer or to Dropbox by name: " + dropboxRow.Effect);
        var driveRow = rows.Single(row => row.RelativePath == HostWire.DriveSeededFileName);
        Assert(driveRow.Group == "Dropbox only" && driveRow.DropboxSize == rightBytes.Length &&
               driveRow.LocalSize is null,
            "the seeded logical-right Drive path is previewed as that side's own file with its exact length: "
            + driveRow.Group + " / " + driveRow.DropboxSize);
        Assert(string.Equals(driveRow.Effect, "Copy to " + SyncEndpointPresentation.SideLabel(SyncPairSide.Left)
                   + " — " + SyncEndpointPresentation.ShortLabel(leftEndpoint.Kind), StringComparison.Ordinal),
            "the seeded logical-right file's previewed effect copies it to the pair's own logical-left "
            + "endpoint rather than to a computer or to Dropbox by name: " + driveRow.Effect);

        var dropboxRead = await rig.ReadDropboxFileAsync(left, HostWire.DropboxSeededFileName);
        Assert(dropboxRead.Bytes.Length == leftBytes.Length && dropboxRead.Bytes.SequenceEqual(leftBytes),
            "the real Dropbox adapter's small-file download returned the exact seeded Dropbox bytes");
        Assert(dropboxRead.Result.FileId == leftSeed.ObjectId &&
               dropboxRead.Result.Revision == leftSeed.Revision &&
               dropboxRead.Result.Size == leftBytes.Length &&
               dropboxRead.Result.ContentHash == leftSeed.ContentHash,
            "the real Dropbox read reported that same object's own id, revision, length and content hash");
        var driveRead = await rig.ReadDriveFileAsync(right, HostWire.DriveSeededFileName);
        Assert(driveRead.Bytes.Length == rightBytes.Length && driveRead.Bytes.SequenceEqual(rightBytes),
            "the real Google Drive adapter's media read returned the exact seeded Drive bytes");
        Assert(driveRead.Result.FileId == rightSeed.ObjectId && driveRead.Result.Size == rightBytes.Length &&
               driveRead.Result.ContentHash == rightSeed.ContentHash,
            "the real Google Drive read reported that same object's own id, length and provider SHA-256");
        Assert(!dropboxRead.Bytes.SequenceEqual(driveRead.Bytes),
            "each provider read returned its own seeded content rather than the other side's");
        Assert(rig.Wire.DropboxReads.Contains(left.RootNamespaceId + "::rev:" + leftSeed.Revision),
            "the Dropbox read reached the content route pinned to the seeded file's own revision inside its "
            + "own retained namespace");
        Assert(rig.Wire.DriveReads.Contains(right.AccountId + "::files/" + rightSeed.ObjectId + "?alt=media"),
            "the Drive read reached the media route for the seeded file's own object identity");
    }

    // ----- U. the create-only write routes and every refusal around them ----------------------------------
    // CC4B content write boundary: the ONE small-file create route per provider exists, and nothing else
    // does. An occupied destination, a stale expected revision and an update-shaped or resumable request are
    // each refused with the stored object provably unchanged, and the write counters stay separate from every
    // read so an absent effect can never be confused with silence.

    private static async Task WriteRouteRefusalsAsync()
    {
        using var rig = await HostRig.CreateAsync("write-route-refusals");
        var left = HostDropbox("wrr-left", "/Dropbox Wrr Left");
        var right = HostDrive("wrr-right", "/Drive Wrr Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var leftSeed = rig.SeedFile(left, HostWire.DropboxSeededFileName, HostWire.DropboxSeededText);
        var rightSeed = rig.SeedFile(right, HostWire.DriveSeededFileName, HostWire.DriveSeededText);
        var leftBytes = Encoding.UTF8.GetBytes(HostWire.DropboxSeededText);
        var rightBytes = Encoding.UTF8.GetBytes(HostWire.DriveSeededText);
        var dropbox = rig.DropboxClient(left);
        var drive = rig.DriveClient(right);

        // (a) A create-only Dropbox upload over the already occupied destination is refused by the transport,
        // and the object that occupies it keeps its exact bytes, identity, revision and content hash.
        using var leftSource = new MemoryStream(leftBytes);
        var occupied = await RefuseAsync(() => dropbox.UploadAsync(HostWire.DropboxSeededFileName,
            leftSource, leftSeed.ContentHash, null));
        Assert(occupied is not null,
            "the create-only Dropbox upload over an occupied destination is refused: " + occupied);
        Assert(rig.Wire.DropboxUploadRequests == 1,
            "the refused create request really reached the Dropbox upload route, observed " +
            rig.Wire.DropboxUploadRequests);
        var afterCreate = await rig.ReadDropboxFileAsync(left, HostWire.DropboxSeededFileName);
        Assert(afterCreate.Bytes.SequenceEqual(leftBytes) && afterCreate.Result.FileId == leftSeed.ObjectId &&
               afterCreate.Result.Revision == leftSeed.Revision &&
               afterCreate.Result.ContentHash == leftSeed.ContentHash,
            "the refused create changed nothing: the occupying object keeps its exact bytes, id, revision and hash");

        // (b) An update-shaped Dropbox upload carrying a STALE expected revision is refused: the conditional
        // update is served only while the reviewed revision is still current (proved in case W), so the P2
        // refusal boundary is preserved for stale evidence while the P3 grant adds the matching case.
        using var updateSource = new MemoryStream(leftBytes);
        var staleUpdate = await RefuseAsync(() => dropbox.UploadAsync(HostWire.DropboxSeededFileName,
            updateSource, leftSeed.ContentHash, leftSeed.Revision + "-stale"));
        Assert(staleUpdate is not null,
            "the stale update-shaped Dropbox upload is refused rather than served: " + staleUpdate);
        Assert(rig.Wire.DropboxUploadRequests == 2 && rig.Wire.Updates.DropboxUpdates == 1,
            "the refused stale conditional update really reached the Dropbox upload route, observed " +
            rig.Wire.DropboxUploadRequests + " upload requests and " + rig.Wire.Updates.DropboxUpdates +
            " conditional updates");
        var afterUpdate = await rig.ReadDropboxFileAsync(left, HostWire.DropboxSeededFileName);
        Assert(afterUpdate.Bytes.SequenceEqual(leftBytes) && afterUpdate.Result.FileId == leftSeed.ObjectId &&
               afterUpdate.Result.Revision == leftSeed.Revision &&
               afterUpdate.Result.ContentHash == leftSeed.ContentHash,
            "no stored Dropbox content was replaced by the refused update");

        // (c) The Drive adapter refuses a create over an existing name before any write request is issued.
        var createsBefore = rig.Wire.DriveCreateRequests;
        using var driveSource = new MemoryStream(rightBytes);
        var existingSibling = await RefuseAsync(() => drive.UploadAsync(HostWire.DriveSeededFileName,
            driveSource, rightSeed.ContentHash, null));
        Assert(existingSibling is not null,
            "the create-only Drive upload over an existing name is refused: " + existingSibling);
        Assert(rig.Wire.DriveCreateRequests == createsBefore,
            "the refused Drive create issued no write request at all, observed " +
            rig.Wire.DriveCreateRequests + " against " + createsBefore);
        var afterDrive = await rig.ReadDriveFileAsync(right, HostWire.DriveSeededFileName);
        Assert(afterDrive.Bytes.SequenceEqual(rightBytes) && afterDrive.Result.FileId == rightSeed.ObjectId,
            "the refused Drive create left the existing object byte-exact and unchanged");

        // (d) The transport itself serves exactly one Drive write route. The media update variant and the
        // resumable family are refused at the boundary, so no update or upload-session route exists to reach.
        var updateVariant = await SendDriveWriteAsync(rig, right, HttpMethod.Put,
            "/upload/drive/v3/files/" + rightSeed.ObjectId + "?uploadType=media&fields=id&supportsAllDrives=false");
        Assert(updateVariant == System.Net.HttpStatusCode.Forbidden,
            "the Drive media update variant is refused by the transport, observed " + (int)updateVariant);
        var resumableVariant = await SendDriveWriteAsync(rig, right, HttpMethod.Post,
            "/upload/drive/v3/files?uploadType=resumable&fields=id&supportsAllDrives=false");
        Assert(resumableVariant == System.Net.HttpStatusCode.Forbidden,
            "the Drive resumable upload family is refused by the transport, observed " + (int)resumableVariant);
        Assert(rig.Wire.DriveCreateRequests == 2 && rig.Wire.DriveMultipartCreateRequests == 0,
            "every refused Drive write request was counted and no multipart create was ever applied, observed " +
            rig.Wire.DriveCreateRequests + " received and " + rig.Wire.DriveMultipartCreateRequests + " applied");
        var afterProbe = await rig.ReadDriveFileAsync(right, HostWire.DriveSeededFileName);
        Assert(afterProbe.Bytes.SequenceEqual(rightBytes),
            "the refused Drive write variants left the stored bytes exactly as they were");

        // (e) The write counters stay separate from every read, so a later zero-write claim observes real
        // effects rather than silence: two Dropbox writes and three Drive writes were received and none was
        // applied, while the read paths that produced these verdicts were all served.
        Assert(rig.Wire.Writes.DropboxUploads == 2 && rig.Wire.Writes.DriveCreates == 2 &&
               rig.Wire.Writes.Total == 4,
            "the write counters report exactly the received create and upload attempts: " +
            rig.Wire.Writes.DropboxUploads + " Dropbox and " + rig.Wire.Writes.DriveCreates + " Drive");
        Assert(rig.Wire.DropboxDownloadRequests >= 2 && rig.Wire.DriveMediaReadRequests >= 2,
            "the reads behind these refusals were served and counted separately from the writes: " +
            rig.Wire.DropboxDownloadRequests + " Dropbox reads and " + rig.Wire.DriveMediaReadRequests +
            " Drive media reads");
    }

    // ----- V. the create-only writes themselves, and the bytes they leave behind ---------------------------
    // The two create routes the production adapters use are exercised end to end: the real adapter creates one
    // new nonempty object on each provider side, each created object is read back through the same adapter and
    // compared byte-exact with the source bytes, and neither destination can be written a second time.

    private static async Task CreateWritesAndTargetBytesAsync()
    {
        using var rig = await HostRig.CreateAsync("create-writes");
        var left = HostDropbox("cw-left", "/Dropbox Cw Left");
        var right = HostDrive("cw-right", "/Drive Cw Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var dropboxSeed = rig.SeedFile(left, HostWire.DropboxSeededFileName, HostWire.DropboxSeededText);
        var driveSeed = rig.SeedFile(right, HostWire.DriveSeededFileName, HostWire.DriveSeededText);
        var leftBytes = Encoding.UTF8.GetBytes(HostWire.DropboxSeededText);
        var rightBytes = Encoding.UTF8.GetBytes(HostWire.DriveSeededText);
        var dropbox = rig.DropboxClient(left);
        var drive = rig.DriveClient(right);

        // (a) The Dropbox create writes the Drive seed's bytes into the Dropbox root as its own new object.
        var dropboxHash = DropboxContentHash.Compute(rightBytes);
        using (var source = new MemoryStream(rightBytes))
        {
            var uploaded = await dropbox.UploadAsync(HostWire.DriveSeededFileName, source, dropboxHash, null);
            Assert(uploaded.Size == rightBytes.Length && uploaded.ContentHash == dropboxHash &&
                   uploaded.FileId.Length > 0 && uploaded.Revision.Length > 0,
                "the real Dropbox adapter created one new object of the exact source length and content hash");
        }
        Assert(rig.Wire.DropboxUploadRequests == 1 && rig.Wire.Writes.DriveCreates == 0,
            "exactly one Dropbox upload was served and no Drive write was involved, observed " +
            rig.Wire.DropboxUploadRequests);
        var dropboxTarget = await rig.ReadDropboxFileAsync(left, HostWire.DriveSeededFileName);
        Assert(dropboxTarget.Bytes.Length == rightBytes.Length && dropboxTarget.Bytes.SequenceEqual(rightBytes),
            "the created Dropbox object reads back as the exact nonempty source bytes, observed " +
            dropboxTarget.Bytes.Length + " bytes");
        Assert(dropboxTarget.Result.FileId != driveSeed.ObjectId &&
               dropboxTarget.Result.FileId != dropboxSeed.ObjectId,
            "the created Dropbox object is its own object, distinct from both provider seeds");

        // (b) The Drive multipart create writes the Dropbox seed's bytes into the Drive root as its own object.
        var driveHash = Convert.ToHexString(SHA256.HashData(leftBytes)).ToLowerInvariant();
        using (var source = new MemoryStream(leftBytes))
        {
            var created = await drive.UploadAsync(HostWire.DropboxSeededFileName, source, driveHash, null);
            Assert(created.Size == leftBytes.Length && created.ContentHash == driveHash &&
                   created.FileId.Length > 0,
                "the real Google Drive adapter created one new object of the exact source length and SHA-256");
        }
        Assert(rig.Wire.DriveMultipartCreateRequests == 1 && rig.Wire.Writes.DropboxUploads == 1,
            "exactly one Drive multipart create was served beside the one Dropbox upload, observed " +
            rig.Wire.DriveMultipartCreateRequests);
        var driveTarget = await rig.ReadDriveFileAsync(right, HostWire.DropboxSeededFileName);
        Assert(driveTarget.Bytes.Length == leftBytes.Length && driveTarget.Bytes.SequenceEqual(leftBytes),
            "the created Drive object reads back as the exact nonempty source bytes, observed " +
            driveTarget.Bytes.Length + " bytes");
        Assert(driveTarget.Result.FileId != driveSeed.ObjectId && driveTarget.Result.FileId != dropboxSeed.ObjectId,
            "the created Drive object is its own object, distinct from both provider seeds");

        // (c) Neither created destination can be written a second time, and the stored bytes stay exactly as
        // the first create left them: the same path is occupied for every write mode this lane serves.
        using (var repeatDropbox = new MemoryStream(leftBytes))
        {
            var repeat = await RefuseAsync(() => dropbox.UploadAsync(HostWire.DriveSeededFileName,
                repeatDropbox, DropboxContentHash.Compute(leftBytes), null));
            Assert(repeat is not null, "a second Dropbox create at the created path is refused: " + repeat);
        }
        using (var repeatDrive = new MemoryStream(rightBytes))
        {
            var repeat = await RefuseAsync(() => drive.UploadAsync(HostWire.DropboxSeededFileName,
                repeatDrive, Convert.ToHexString(SHA256.HashData(rightBytes)).ToLowerInvariant(), null));
            Assert(repeat is not null, "a second Drive create at the created path is refused: " + repeat);
        }
        var dropboxAfter = await rig.ReadDropboxFileAsync(left, HostWire.DriveSeededFileName);
        var driveAfter = await rig.ReadDriveFileAsync(right, HostWire.DropboxSeededFileName);
        Assert(dropboxAfter.Bytes.SequenceEqual(rightBytes) &&
               dropboxAfter.Result.FileId == dropboxTarget.Result.FileId &&
               dropboxAfter.Result.Revision == dropboxTarget.Result.Revision,
            "the refused repeat left the created Dropbox object byte-exact with its own identity and revision");
        Assert(driveAfter.Bytes.SequenceEqual(leftBytes) && driveAfter.Result.FileId == driveTarget.Result.FileId,
            "the refused repeat left the created Drive object byte-exact with its own identity");
        // The two providers refuse a second create at different boundaries and the counters show exactly that:
        // the Dropbox repeat really reached the content route and was refused there, while the Drive repeat was
        // refused by the adapter's own create-only guard before any write request was issued.
        Assert(rig.Wire.Writes.DropboxUploads == 2 && rig.Wire.Writes.DriveCreates == 1 &&
               rig.Wire.DriveMultipartCreateRequests == 1 && rig.Wire.Writes.Total == 3,
            "the write counters report exactly one applied create per provider plus the Dropbox repeat that " +
            "reached the route, observed " + rig.Wire.Writes.DropboxUploads + " Dropbox and " +
            rig.Wire.Writes.DriveCreates + " Drive write requests with " +
            rig.Wire.DriveMultipartCreateRequests + " multipart applies");
    }

    // ----- W. the conditional update and the separately retained predecessor -------------------------------
    // P3 write half at the adapter boundary: each real adapter applies one conditional successor, each
    // successor reads back byte-exact with its own advanced revision, the predecessor is retained separately
    // under the product recovery container and reads back byte-exact, and stale evidence is refused with
    // nothing replaced.

    private static async Task ConditionalUpdateAndRecoveryAsync()
    {
        using var rig = await HostRig.CreateAsync("conditional-update");
        var left = HostDropbox("cu-left", "/Dropbox Cu Left");
        var right = HostDrive("cu-right", "/Drive Cu Right");
        foreach (var spec in new[] { left, right }) await rig.AddEndpointAsync(spec);
        var dropboxSeed = rig.SeedFile(left, HostWire.DropboxSeededFileName, HostWire.DropboxSeededText);
        var driveSeed = rig.SeedFile(right, HostWire.DriveSeededFileName, HostWire.DriveSeededText);
        rig.RegisterFolder(left, HostRig.RecoveryContainerPath, "id:host-recovery-cu-left");
        rig.RegisterFolder(right, HostRig.RecoveryContainerPath, "drive-recovery-cu-right");
        var dropboxRecovery = new SyncRemoteRecoveryRootBinding(HostRig.RecoveryContainerPath,
            "id:host-recovery-cu-left", left.RootNamespaceId);
        var driveRecovery = new SyncRemoteRecoveryRootBinding(HostRig.RecoveryContainerPath,
            "drive-recovery-cu-right", right.RootNamespaceId);
        var dropbox = rig.DropboxClient(left);
        var drive = rig.DriveClient(right);
        var predecessorBytes = Encoding.UTF8.GetBytes(HostWire.DropboxSeededText);
        var drivePredecessorBytes = Encoding.UTF8.GetBytes(HostWire.DriveSeededText);
        var successorBytes = Encoding.UTF8.GetBytes(HostWire.DropboxSeededText + "successor");
        var driveSuccessorBytes = Encoding.UTF8.GetBytes(HostWire.DriveSeededText + "successor");

        // The production order: the exact predecessor is retained first, then the conditional successor.
        var predecessor = (await dropbox.ListEntriesAsync()).Single(item => item.Kind == PilotEntryKind.File &&
            string.Equals(item.RelativePath, HostWire.DropboxSeededFileName, StringComparison.Ordinal));
        var archived = await dropbox.CopyToRecoveryAsync(predecessor, dropboxRecovery,
            "retained-predecessor-0001");
        Assert(archived.Size == predecessorBytes.Length && archived.ContentHash == dropboxSeed.ContentHash,
            "the real Dropbox adapter retained the exact predecessor by its own size and content hash");
        using (var source = new MemoryStream(successorBytes))
        {
            var updated = await dropbox.UploadAsync(HostWire.DropboxSeededFileName, source,
                DropboxContentHash.Compute(successorBytes), dropboxSeed.Revision);
            Assert(updated.Revision != dropboxSeed.Revision && updated.Size == successorBytes.Length &&
                   updated.ContentHash == DropboxContentHash.Compute(successorBytes),
                "the real Dropbox conditional update produced a successor with its own revision and exact length");
        }
        var dropboxSuccessor = await rig.ReadDropboxFileAsync(left, HostWire.DropboxSeededFileName);
        Assert(dropboxSuccessor.Bytes.SequenceEqual(successorBytes) &&
               dropboxSuccessor.Result.FileId == dropboxSeed.ObjectId,
            "the Dropbox successor reads back byte-exact through the real adapter on the same object identity");
        var retained = await rig.ReadRetainedPredecessorAsync(left);
        Assert(retained.Read.Bytes.SequenceEqual(predecessorBytes) && retained.Name.Length > 0,
            "the retained predecessor reads back byte-exact and separately from the successor, named " +
            retained.Name);
        using (var stale = new MemoryStream(predecessorBytes))
        {
            var refused = await RefuseAsync(() => dropbox.UploadAsync(HostWire.DropboxSeededFileName, stale,
                DropboxContentHash.Compute(predecessorBytes), dropboxSeed.Revision));
            Assert(refused is not null, "the stale Dropbox revision is refused: " + refused);
        }
        var afterDropboxStale = await rig.ReadDropboxFileAsync(left, HostWire.DropboxSeededFileName);
        Assert(afterDropboxStale.Bytes.SequenceEqual(successorBytes) &&
               afterDropboxStale.Result.Revision == dropboxSuccessor.Result.Revision,
            "the refused stale revision left the successor byte-exact with the same revision");
        Assert(rig.Wire.Updates.DropboxUpdates == 2 && rig.Wire.Updates.DropboxRecoveryCopies == 1,
            "the counters separate the applied update, the refused stale update and the recovery copy, observed " +
            rig.Wire.Updates.DropboxUpdates + " updates and " + rig.Wire.Updates.DropboxRecoveryCopies + " copies");

        // The Drive side: the reviewed-revision guard refuses stale evidence before any request, and the
        // transport itself refuses a mismatched strong guard with the real precondition status.
        var driveRevision = await rig.DriveRevisionAsync(right, HostWire.DriveSeededFileName);
        var drivePredecessor = (await drive.ListInventoryAsync()).Entries.Single(item =>
            item.Kind == GoogleDriveEntryKind.Blob &&
            string.Equals(item.RelativePath, HostWire.DriveSeededFileName, StringComparison.Ordinal));
        var driveArchived = await drive.CopyToRecoveryAsync(drivePredecessor.ToRemoteEntry(), driveRecovery,
            "retained-predecessor-0001");
        Assert(driveArchived.Size == drivePredecessorBytes.Length &&
               driveArchived.ContentHash == driveSeed.ContentHash,
            "the real Google Drive adapter retained the exact predecessor by its own size and SHA-256");
        using (var source = new MemoryStream(driveSuccessorBytes))
        {
            var updated = await drive.UploadAsync(HostWire.DriveSeededFileName, source,
                Convert.ToHexString(SHA256.HashData(driveSuccessorBytes)).ToLowerInvariant(), driveRevision);
            Assert(updated.Size == driveSuccessorBytes.Length,
                "the real Google Drive conditional update produced a successor of the exact length");
        }
        var driveSuccessor = await rig.ReadDriveFileAsync(right, HostWire.DriveSeededFileName);
        Assert(driveSuccessor.Bytes.SequenceEqual(driveSuccessorBytes),
            "the Drive successor reads back byte-exact through the real adapter");
        var driveRetained = await rig.ReadRetainedPredecessorAsync(right);
        Assert(driveRetained.Read.Bytes.SequenceEqual(drivePredecessorBytes) && driveRetained.Name.Length > 0,
            "the retained Drive predecessor reads back byte-exact and separately, named " + driveRetained.Name);
        using (var stale = new MemoryStream(drivePredecessorBytes))
        {
            var refused = await RefuseAsync(() => drive.UploadAsync(HostWire.DriveSeededFileName, stale,
                Convert.ToHexString(SHA256.HashData(drivePredecessorBytes)).ToLowerInvariant(), driveRevision));
            Assert(refused is not null,
                "the stale Drive reviewed revision is refused before any request: " + refused);
        }
        var staleGuard = await SendDriveWriteAsync(rig, right, HttpMethod.Put,
            "/upload/drive/v2/files/" + driveSuccessor.Result.FileId +
            "?uploadType=media&newRevision=true&fields=id", ifMatch: "\"stale-guard\"");
        Assert(staleGuard == System.Net.HttpStatusCode.PreconditionFailed,
            "the transport refuses a mismatched strong guard with the real precondition status, observed " +
            (int)staleGuard);
        var afterDriveStale = await rig.ReadDriveFileAsync(right, HostWire.DriveSeededFileName);
        Assert(afterDriveStale.Bytes.SequenceEqual(driveSuccessorBytes) &&
               afterDriveStale.Result.FileId == driveSuccessor.Result.FileId,
            "the refused stale Drive update left the successor byte-exact");
        Assert(rig.Wire.Updates.Total == 6 && rig.Wire.Updates.DriveUpdates == 2 &&
               rig.Wire.Updates.DriveRecoveryCopies == 1 && rig.Wire.Updates.DriveUpdates + rig.Wire.Updates.DriveRecoveryCopies == 3,
            "the update counters separate Dropbox and Drive updates and recovery copies from every other write, " +
            "observed " + rig.Wire.Updates.Total + " update and copy requests in total");
    }

    /// <summary>One exact Drive write request sent straight to the transport, returning its status code.</summary>
    private static async Task<System.Net.HttpStatusCode> SendDriveWriteAsync(HostRig rig, EndpointSpec spec,
        HttpMethod method, string path, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, "https://www.googleapis.com" + path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
            "gaccess:" + spec.ConnectionId);
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        using var response = await rig.Wire.SendDirectAsync(request);
        return response.StatusCode;
    }

    /// <summary>The typed refusal of one real-adapter call, or null when it unexpectedly succeeded.</summary>
    private static async Task<string?> RefuseAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return error.GetType().Name + ": " + error.Message;
        }
    }
    // ----- Y. D2: the retained observation supplies a peer bound context when its provider input is unavailable --

    private static async Task RetainedObservationReadbackAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The retained-observation readback proof requires the Windows-native Host rig.");
        using var rig = await HostRig.CreateAsync("retained-observation-readback");
        var one = HostDrive("yor-one", "/Drive Yor One");
        var two = one with { FolderId = "gd-folder-yor-two", FolderPath = "/Drive Yor Two" };
        var three = HostDrive("yor-three", "/Drive Yor Three");
        var four = three with { FolderId = "gd-folder-yor-four", FolderPath = "/Drive Yor Four" };
        foreach (var spec in new[] { one, two, three, four }) await rig.AddEndpointAsync(spec);
        var candidate = await rig.AddPairAsync("yor-candidate", one, two);
        var peer = await rig.AddPairAsync("yor-peer", three, four);
        await PrepareBothSidesAsync(rig, candidate, withStaging: true, sharedRecoveryAuthority: true);
        await PrepareBothSidesAsync(rig, peer, withStaging: true, sharedRecoveryAuthority: true);
        var allDefinitions = await rig.Db.LoadAllPairDefinitionsAsync();
        var candidateDefinition = allDefinitions.Single(item => item.Id == candidate.Id);
        var peerDefinition = allDefinitions.Single(item => item.Id == peer.Id);
        // The candidate own roots stay live (their provider input is available); only the PEER provider
        // input becomes unavailable, and only its retained observation can answer for it.
        rig.Wire.ProviderInputFailureForTest = request =>
            request.RequestUri!.AbsoluteUri.Contains(peer.Left.FolderId, StringComparison.Ordinal) ||
            request.RequestUri!.AbsoluteUri.Contains(peer.Right.FolderId, StringComparison.Ordinal);
        var withContext = await rig.Host.BuildOpaquePartitionProofAsync(candidateDefinition, null,
            candidateDefinition, peerDefinition, default);
        Assert(withContext is not null,
            "the retained observation supplies the unavailable peer bound context for the partition proof");
        // The identical request refuses once that retained context is stale, so the context is required.
        await rig.ScalarAsync("UPDATE root_binding SET generation=generation+1 WHERE pair_id='" + peer.Id + "';");
        var staleContext = await rig.Host.BuildOpaquePartitionProofAsync(candidateDefinition, null,
            candidateDefinition, peerDefinition, default);
        Assert(staleContext is null,
            "a stale retained peer context yields no partition proof while the provider input is unavailable");
    }
}
