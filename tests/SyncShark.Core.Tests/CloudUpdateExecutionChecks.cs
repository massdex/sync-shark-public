using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using IOPath = System.IO.Path;

// CC3C focused proofs: the one internal shared cloud update executor over real isolated SQLite authority, the
// real reconciliation planner, a real protected Windows private stage, the real typed CC2C cloud endpoint
// ports and a real CC2E-scoped recovery root.
//
// The planner, the operation journal, the allocation rows, the attempt/receipt records, the recovery
// allocation and its bound recovery authority, the version and membership registration, the ordered
// synchronization point, the operation lease/fence transitions and the completion transitions are all the
// shipped production code. Only the two provider transports are scripted adapters: they supply an inventory,
// a body and a real bound recovery root, and they perform a real conditional in-place write into their own
// in-memory inventory, so the executor's own decisions - validation, refusal, recovery-before-replacement,
// conditional dispatch, readback, commit, reconcile and cleanup - are the decisions under proof. No fixture
// replaces the decision being proved, and nothing here reaches a live account, credential, network,
// installed database, product state, Host wiring or NTFS pair root.
internal static class CloudUpdateExecutionChecks
{
    private const string InstallationId = "installation-cloud-update-execution-316";
    private const string SourceRevision = "cloud-update-execution-316";
    private const string ProfileId = "profile-cloud-update-execution-316";
    private const string Path = "reports/quarterly.txt";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static int _unexpectedEffects;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud update execution fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        _unexpectedEffects = 0;

        await CaseAsync("A-dropbox-to-drive-update",
            "A real planned replacement from the Dropbox side onto the Drive target retains the exact predecessor in its bound recovery root",
            async () => await PlannedUpdateAsync("a", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive,
                false, 4096));
        await CaseAsync("B-drive-to-dropbox-update",
            "The opposite direction: a real planned replacement from the Drive side onto the Dropbox target",
            async () => await PlannedUpdateAsync("b", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox,
                false, 4096));
        await CaseAsync("C-each-direction",
            "Each cross-provider ordered pairing replaces in both logical directions, and a same-provider pair is refused without verified ancestry",
            EachDirectionAsync);
        await CaseAsync("D-empty-successor",
            "An update whose successor content is empty stages, commits and registers the exact registered empty digest",
            async () => await PlannedUpdateAsync("d", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive,
                true, 0));
        await CaseAsync("E-plan-authority-refusals",
            "Foreign pair, foreign recovery authority, unretained corpus, drifted predecessor, drifted source and a foreign kind refuse before any recovery or replacement",
            PlanAuthorityRefusalsAsync);
        await CaseAsync("F-recovery-refusals",
            "An unknown recovery copy and a drifting recovery readback admit zero target replacement and preserve the allocation and evidence",
            RecoveryRefusalsAsync);
        await CaseAsync("G-transport-refusals",
            "Stale fence, cancellation, an unsupported conditional replacement, a conditional target race and a replaced stage issue no duplicate or unconditional effect",
            TransportRefusalsAsync);
        await CaseAsync("H-recovery-response-lost",
            "A recovery copy that was applied with a lost response is never followed by a replacement",
            RecoveryResponseLostAsync);
        await CaseAsync("I-verified-recovery-restart",
            "A restart after a verified recovery never copies the predecessor twice and still replaces exactly once",
            VerifiedRecoveryRestartAsync);
        await CaseAsync("J-update-response-lost",
            "A replacement applied with a lost response is unknown, is never retried blindly and is never committed from matching bytes",
            UpdateResponseLostAsync);
        await CaseAsync("K-known-receipt-precommit-crash",
            "A retained known-success replacement receipt with a pre-commit crash reconciles the exact target once and cleans once",
            KnownReceiptCrashAsync);
        await CaseAsync("L-committed-precleanup-crash",
            "A committed pre-cleanup crash resumes only its exact owned cleanup and never re-uploads",
            CommittedCrashAsync);
        await CaseAsync("M-bound-descriptor-evidence",
            "The retained descriptor binds the actual recovery storage, the logical side, the corpus, the native algorithm and the exact predecessor, and the source stays untouched",
            BoundDescriptorEvidenceAsync);
        await CaseAsync("N-retained-stage-ready-retry",
            "A conclusively declined replacement leaves the update Ready with its exact verified retained stage, and the next consumer call reopens that stage instead of preparing a second one",
            RetainedStageReadyRetryAsync);
        await CaseAsync("O-retained-stage-cancellation",
            "A caller cancellation inside the retained-stage rehash of a Ready update issues no replacement request and no recovery copy, retains the exact stage object and still retries to completion",
            RetainedStageCancellationAsync);

        Assert(_unexpectedEffects == 0,
            "no cloud update execution fixture ever reached an upload, delete, move, copy or recovery request it was not given");
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudUpdateExecutionChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "cloud-update-execution-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud update execution: " + _assertions + " assertions; the real planner, journal," +
            " allocation, attempt, receipt, recovery-artifact, version/membership, synchronization point and" +
            " completion transitions over real isolated SQLite with a real protected Windows private stage and" +
            " a real CC2E-scoped recovery root, for both ordered provider pairings, both logical directions, an" +
            " ordinary and an empty successor, the retained-predecessor recovery and every required refusal," +
            " unknown, reconciliation and cleanup path, including the retained-stage Ready retry and its" +
            " cancellation paths.");
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
        if (!condition) throw new InvalidDataException("CC3C assertion failed: " + label);
    }

    private static byte[] Pattern(int length, byte seed = 31)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * seed + 7) % 251);
        return payload;
    }

    // ----- the real harness -------------------------------------------------------------------------

    private sealed class Site : IAsyncDisposable
    {
        internal string Slug = "";
        internal string CaseRoot = "";
        internal string AnchorRoot = "";
        internal string NamespaceId = "";
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal SyncPair Pair = null!;
        internal SyncEndpointKind LeftKind;
        internal SyncEndpointKind RightKind;
        internal string LeftRoot = "";
        internal string RightRoot = "";
        internal string LeftRecoveryRoot = "";
        internal string RightRecoveryRoot = "";
        internal string LeftRecoveryCorpus = "";
        internal string RightRecoveryCorpus = "";
        internal string LeftRecoveryFolderId = "";
        internal string RightRecoveryFolderId = "";
        internal FixtureStore Left = null!;
        internal FixtureStore Right = null!;
        internal SyncControlNamespaceBinding Control = null!;
        internal SyncAuthorityCloudAllocationConfiguration Allocations = null!;
        private PilotDirectoryLease? _anchor;
        private PilotDirectoryLease? _control;

        internal PilotDirectoryLease ControlRoot => _control!;
        internal string Worker => "worker-cc3c-" + Slug;

        internal static async Task<Site> CreateAsync(string caseName, string slug, SyncEndpointKind leftKind,
            SyncEndpointKind rightKind)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The shared cloud update execution proofs require Windows NTFS identities and protected ACLs.");
            var site = new Site
            {
                CaseName = caseName,
                Slug = slug,
                LeftKind = leftKind,
                RightKind = rightKind,
                NamespaceId = "cloud-update-execution-" + slug,
                CaseRoot = IOPath.Combine(_root, caseName)
            };
            Directory.CreateDirectory(site.CaseRoot);
            site.AnchorRoot = IOPath.Combine(site.CaseRoot, "anchor");
            Directory.CreateDirectory(site.AnchorRoot);
            site._anchor = PilotDirectoryLease.Open(site.AnchorRoot);
            site.Control = new SyncControlNamespaceBinding(InstallationId, site.NamespaceId,
                SyncControlNamespace.BindLocalForTest(site._anchor, InstallationId, site.NamespaceId));
            site._control = SyncControlNamespace.OpenLocal(site.Control, site._anchor);
            site.LeftRoot = "root-" + slug + "-left";
            site.RightRoot = "root-" + slug + "-right";
            site.LeftRecoveryRoot = "recovery-" + slug + "-left";
            site.RightRecoveryRoot = "recovery-" + slug + "-right";
            site.LeftRecoveryCorpus = "corpus-" + slug + "-left";
            site.RightRecoveryCorpus = "corpus-" + slug + "-right";
            site.LeftRecoveryFolderId = RecoveryFolderId(leftKind, slug + "-left");
            site.RightRecoveryFolderId = RecoveryFolderId(rightKind, slug + "-right");
            var leftAccount = "account-" + slug + "-left";
            var rightAccount = "account-" + slug + "-right";
            var leftProvider = SyncEndpointProviders.Get(leftKind).ProviderId!;
            var rightProvider = SyncEndpointProviders.Get(rightKind).ProviderId!;
            var leftFolder = FolderId(leftKind, slug + "-left");
            var rightFolder = FolderId(rightKind, slug + "-right");
            var leftNamespace = EndpointNamespace(leftKind, "id:" + leftAccount, "ns-root-" + slug + "-left");
            var rightNamespace = EndpointNamespace(rightKind, "id:" + rightAccount,
                "ns-root-" + slug + "-right");
            site.Left = FixtureStore.Create(leftProvider, leftAccount, leftNamespace, leftFolder);
            site.Right = FixtureStore.Create(rightProvider, rightAccount, rightNamespace, rightFolder);
            site.Db = new SyncAuthorityDatabase(IOPath.Combine(site.CaseRoot, "syncshark.db"));
            await site.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await site.Db.RegisterProviderAccountAsync(leftAccount, InstallationId, "id:" + leftAccount,
                "ns-root-" + slug + "-left", "ns-home-" + slug + "-left", "CC3C left account", 1,
                cancellationToken: default, provider: leftProvider);
            await site.Db.RegisterProviderAccountAsync(rightAccount, InstallationId, "id:" + rightAccount,
                "ns-root-" + slug + "-right", "ns-home-" + slug + "-right", "CC3C right account", 1,
                cancellationToken: default, provider: rightProvider);
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(leftKind, "conn-" + slug + "-left", leftNamespace, leftFolder, "/left"),
                SyncEndpoint.Cloud(rightKind, "conn-" + slug + "-right", rightNamespace, rightFolder, "/right"),
                SyncMode.TwoWay), "CC3C " + caseName);
            var normalized = SyncPairStore.NormalizePair(draft);
            await site.Db.RegisterPairAsync(normalized.Id, normalized.PolicyRevision);
            await site.Db.RetainPairDefinitionAsync(normalized, true);
            site.Pair = normalized;
            await site.Db.VerifyHealthAsync();
            site.Admission = await site.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", normalized.Id,
                null, null, JsonSerializer.Serialize(new { pairIds = new[] { normalized.Id } }),
                SyncAuthorityCommandPurpose.NormalEffect);
            await site.Db.TransitionCommandRequestAsync(site.Admission.CommandRequestId, "executing");
            await site.Db.RegisterCloudEndpointRootBindingAsync(site.LeftRoot, site.Pair.Id,
                SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId, leftProvider, leftAccount,
                leftNamespace, leftFolder, "/left", cancellationToken: default);
            await site.Db.RegisterCloudEndpointRootBindingAsync(site.RightRoot, site.Pair.Id,
                SyncAuthorityCloudSides.LogicalRight, 1, InstallationId, rightProvider, rightAccount,
                rightNamespace, rightFolder, "/right", cancellationToken: default);
            await site.Db.RegisterCloudRecoveryRootAsync(site.LeftRecoveryRoot, InstallationId, leftProvider,
                leftAccount, leftNamespace, RecoveryFolderId(leftKind, slug + "-left"),
                site.LeftRecoveryCorpus, site.RecoveryPath(SyncAuthorityCloudSides.LogicalLeft), 1,
                cancellationToken: default, hierarchies: new[]
                {
                    Chain(leftKind, leftAccount, leftNamespace, leftFolder, site.LeftRoot),
                    Chain(leftKind, leftAccount, leftNamespace, RecoveryFolderId(leftKind, slug + "-left"),
                        site.LeftRecoveryRoot)
                });
            await site.Db.RegisterCloudRecoveryRootAsync(site.RightRecoveryRoot, InstallationId, rightProvider,
                rightAccount, rightNamespace, RecoveryFolderId(rightKind, slug + "-right"),
                site.RightRecoveryCorpus, site.RecoveryPath(SyncAuthorityCloudSides.LogicalRight), 1,
                cancellationToken: default, hierarchies: new[]
                {
                    Chain(rightKind, rightAccount, rightNamespace, rightFolder, site.RightRoot),
                    Chain(rightKind, rightAccount, rightNamespace, RecoveryFolderId(rightKind, slug + "-right"),
                        site.RightRecoveryRoot)
                });
            site.Allocations = new SyncAuthorityCloudAllocationConfiguration(site.Control.NamespaceId,
                new SyncAuthorityCloudTargetAllocation(site.LeftRecoveryRoot, 1L << 30),
                new SyncAuthorityCloudTargetAllocation(site.RightRecoveryRoot, 1L << 30));
            site.Left.ConfigureRecovery(site.RecoveryPath(SyncAuthorityCloudSides.LogicalLeft),
                RecoveryFolderId(leftKind, slug + "-left"), leftNamespace);
            site.Right.ConfigureRecovery(site.RecoveryPath(SyncAuthorityCloudSides.LogicalRight),
                RecoveryFolderId(rightKind, slug + "-right"), rightNamespace);
            return site;
        }

        internal string CaseName = "";

        internal static string RecoveryFolderId(SyncEndpointKind kind, string suffix) =>
            kind == SyncEndpointKind.Dropbox ? "id:cc3c-rec-" + suffix : "gd-cc3c-rec-" + suffix;

        internal static string EndpointNamespace(SyncEndpointKind kind, string accountId,
            string rootNamespaceId) =>
            kind == SyncEndpointKind.GoogleDrive ? accountId : rootNamespaceId;

        internal static string FolderId(SyncEndpointKind kind, string suffix) =>
            kind == SyncEndpointKind.Dropbox ? "id:cc3c-" + suffix : "gd-cc3c-" + suffix;

        internal string RecoveryPath(string side) =>
            SyncAuthorityCloudRootPersistence.NormalizeDisplayPath("/Recovery/" + Slug + "/" +
                (side == SyncAuthorityCloudSides.LogicalLeft ? "left" : "right"));

        internal string RecoveryFolderFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRecoveryFolderId : RightRecoveryFolderId;

        /// <summary>The exact retained per-side account and recovery authority of one logical side.</summary>
        internal SyncAuthorityCloudSideAuthority SideAuthority(string side)
        {
            var store = StoreFor(side);
            return new SyncAuthorityCloudSideAuthority(
                side,
                store.Provider,
                "conn-" + Slug + "-" + (side == SyncAuthorityCloudSides.LogicalLeft ? "left" : "right"),
                store.ProviderAccountId,
                store.NamespaceId,
                store.FolderId,
                side == SyncAuthorityCloudSides.LogicalLeft ? "/left" : "/right",
                RootFor(side),
                RecoveryRootFor(side),
                RecoveryFolderFor(side),
                store.NamespaceId,
                RecoveryCorpusFor(side),
                RecoveryPath(side));
        }

        internal IReadOnlyList<SyncAuthorityCloudSideAuthority> SideAuthorities() =>
            new[] { SideAuthority(SyncAuthorityCloudSides.LogicalLeft),
                SideAuthority(SyncAuthorityCloudSides.LogicalRight) };

        internal string RecoveryRootFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRecoveryRoot : RightRecoveryRoot;

        internal string RecoveryCorpusFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRecoveryCorpus : RightRecoveryCorpus;

        internal string RootFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRoot : RightRoot;

        internal FixtureStore StoreFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? Left : Right;

        internal SyncAuthorityCloudEndpointSet Endpoints() => SyncAuthorityCloudEndpointSet.FromPair(Pair,
            Left.Store, Right.Store, LeftRoot, RightRoot, Left.ProviderAccountId, Right.ProviderAccountId);

        internal SyncAuthorityCloudExecutionContext Context() => new(Endpoints(), Control, ControlRoot);

        internal SyncAuthorityPlannerOptions Options() => new(1, ProfileId, Control.NamespaceId,
            Control.NamespaceId, Direction: SyncMode.TwoWay, CloudAllocations: Allocations);

        internal SyncAuthorityCloudUpdateExecutor UpdateExecutor() =>
            new(Db, Context(), SideAuthorities());

        /// <summary>The full provider-verified chain of one root, as an input the caller supplies.</summary>
        internal static SyncAuthorityCloudHierarchySnapshot Chain(SyncEndpointKind kind,
            string providerAccountId, string namespaceId, string folderId, string rootBindingId)
        {
            var provider = SyncEndpointProviders.Get(kind).ProviderId!;
            var chain = new List<string>();
            if (kind == SyncEndpointKind.Dropbox)
                chain.Add(PilotPath.DropboxNamespaceRootIdentity(namespaceId));
            else
                chain.Add("corpus-root-" + namespaceId);
            chain.Add(folderId);
            return new SyncAuthorityCloudHierarchySnapshot(provider, providerAccountId, namespaceId,
                rootBindingId, folderId, chain)
            {
                CompleteToCorpusRoot = true,
                CorpusRootId = chain[0],
                ChainNodeKeys = chain.Select(id => new SyncAuthorityCloudHierarchyNodeKeys(id,
                    new[] { SyncCloudObjectKey.Native(provider, id) })).ToArray(),
                ObjectKeyCoverage = kind == SyncEndpointKind.Dropbox
                    ? SyncCloudObjectKeyCoverage.MountAliasesComplete
                    : SyncCloudObjectKeyCoverage.NativeOnly
            };
        }

        /// <summary>One real complete observation generation built from a store's actual current inventory.</summary>
        internal async Task ObserveStoreAsync(string side, string trigger = "initial")
        {
            var store = StoreFor(side);
            var inputs = store.Entries.Select(entry =>
                store.Kind(entry) == "directory"
                    ? Entry(store, entry.RelativePath, "directory", entry.Id!, null, 0, null)
                    : Entry(store, entry.RelativePath, "file", entry.Id!, entry.Revision,
                        store.PayloadOf(entry.RelativePath).Length, store.PayloadOf(entry.RelativePath)))
                .ToArray();
            var scan = await Db.BeginScanGenerationAsync(Pair.Id, RootFor(side), side, trigger, "{}");
            if (inputs.Length > 0) await Db.AppendObservationEntriesAsync(scan, inputs);
            await Db.CompleteScanGenerationAsync(scan);
        }

        internal SyncAuthorityObservationInput Entry(FixtureStore store, string relativePath, string objectKind,
            string fileId, string? revision, long size, byte[]? payload)
        {
            var directory = string.Equals(objectKind, "directory", StringComparison.Ordinal);
            var hash = payload is null || directory ? null : SyncContentIdentity.Compute(store.Provider, payload);
            return new SyncAuthorityObservationInput(relativePath, objectKind,
                store.IdentityFor(fileId, revision), hash, directory ? 0 : size, "{}");
        }

        /// <summary>The real planner's single operation outcome for one path of the expected kind.</summary>
        internal async Task<SyncAuthorityPlannerOutcome> PlanAsync(string path,
            SyncAuthorityOperationKind expected)
        {
            var planner = Planner();
            var outcomes = await planner.PlanAsync(Pair.Id);
            var operation = outcomes.SingleOrDefault(item =>
                item.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                string.Equals(item.RelativePath, path, StringComparison.Ordinal));
            if (operation is null || operation.OperationId is null)
                throw new InvalidDataException("The real planner produced no operation for " + path +
                    " (outcomes: " + string.Join(", ",
                        outcomes.Select(item => item.Kind + ":" + item.RelativePath)) + ")");
            var snapshot = await Db.LoadOperationAsync(operation.OperationId);
            Assert(snapshot.Kind == expected,
                "the real planner produced " + expected + " for " + path + " (actual " + snapshot.Kind + ")");
            return operation;
        }

        /// <summary>
        /// The real provider-aware planner of this pair: the real cross-side comparison with the real CC2F
        /// revision-bound common-digest resolver over the two typed ports.
        /// </summary>
        internal SyncAuthorityReconciliationPlanner Planner() => new(Db, Options(), Admission,
            contentComparison: new SyncAuthorityProviderAwareContentComparison(Left.Provider, Right.Provider,
                new SyncAuthorityCloudCommonDigestResolver(Db, Endpoints())));

        /// <summary>The real planner's own outcome for one converged path, with no expected operation.</summary>
        internal async Task<SyncAuthorityPlannerOutcome> PlanBaselineAsync(string path)
        {
            var outcomes = await Planner().PlanAsync(Pair.Id);
            var outcome = outcomes.SingleOrDefault(item =>
                string.Equals(item.RelativePath, path, StringComparison.Ordinal));
            if (outcome is null)
                throw new InvalidDataException("The real planner produced no outcome for " + path +
                    " (outcomes: " + string.Join(", ",
                        outcomes.Select(item => item.Kind + ":" + item.RelativePath)) + ")");
            return outcome;
        }

        /// <summary>The latest complete observed entry for one side and path, or null when none was retained.</summary>
        internal async Task<SyncAuthorityObservedEntry?> LatestObservationAsync(string side, string relativePath)
        {
            var observations = await Db.LoadLatestCompleteObservationAsync(Pair.Id, side, default);
            return observations.SingleOrDefault(item =>
                string.Equals(item.RelativePath, relativePath, StringComparison.Ordinal));
        }

        internal async Task<SyncAuthorityOperationSnapshot> SnapshotAsync(string operationId) =>
            await Db.LoadOperationAsync(operationId);

        public async ValueTask DisposeAsync()
        {
            SyncAuthorityCloudContentStage.AvailableDeviceBytesForTest = null;
            _control?.Dispose();
            _anchor?.Dispose();
            SqliteConnection.ClearAllPools();
            await Db.TransitionCommandRequestAsync(Admission.CommandRequestId, "completed");
        }
    }

    /// <summary>
    /// A scripted cloud adapter over a real in-memory inventory and one real bound recovery root. It computes
    /// its own provider-native digest with the production content-identity seam over exactly the bytes it
    /// receives, enforces the conditional revision on a replacement, and refuses every effect it was not
    /// given - including the legacy unbound recovery overloads, so only the bound recovery root is used.
    /// </summary>
    /// <remarks>
    /// It is proxied through one combined interface because the real CC2F common-digest resolver reads a
    /// non-SHA-256 revision through the exact range capability, and a scripted adapter that could not serve
    /// that read would silently turn the real cross-side comparison into an unknown.
    /// </remarks>
    public interface IFixtureCloudStore : ISyncRemoteStore, ISyncRemoteRangeStore
    {
    }

    public class FixtureStore : DispatchProxy
    {
        internal string Provider = "";
        internal string ProviderAccountId = "";
        internal string NamespaceId = "";
        internal string FolderId = "";
        internal List<PilotRemoteEntry> Entries { get; } = [];
        internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
        internal List<(string Path, byte[] Bytes, string ExpectedRevision)> Uploads { get; } = [];
        internal Dictionary<string, (SyncArchiveResult Result, byte[] Payload)> RecoveryCopies { get; } =
            new(StringComparer.Ordinal);
        internal (string RootPath, string RootFolderId, string RootNamespaceId)? RecoveryBinding;
        internal int RevisionCounter;
        internal int RefusedEffects;
        internal int RecoveryCopyRequests;
        internal int RecoveryReadbacks;
        internal int LegacyRecoveryCalls;
        internal bool RefuseConditionalUpdate;
        internal bool CorruptUploadResult;
        internal bool CorruptRecoveryReadback;
        internal bool RefuseRecoveryReadback;
        internal bool ApplyThenFailOnUpload;
        internal bool ApplyThenFailOnRecovery;
        internal Action<string>? DuringDownload;

        internal static FixtureStore Create(string provider, string providerAccountId, string namespaceId,
            string folderId)
        {
            var fixture = (FixtureStore)(object)DispatchProxy.Create<IFixtureCloudStore, FixtureStore>();
            fixture.Provider = provider;
            fixture.ProviderAccountId = providerAccountId;
            fixture.NamespaceId = namespaceId;
            fixture.FolderId = folderId;
            return fixture;
        }

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

        internal void ConfigureRecovery(string rootPath, string rootFolderId, string rootNamespaceId) =>
            RecoveryBinding = (rootPath, rootFolderId, rootNamespaceId);

        internal string IdentityFor(string fileId, string? revision) =>
            new SyncAuthorityCloudObservationIdentity(Provider, ProviderAccountId, NamespaceId, fileId,
                revision, SyncEndpointProviders.TryGet(Provider, out var definition)
                    ? definition!.ContentHashAlgorithm
                    : SyncContentIdentity.Sha256).Serialize();

        internal void Seed(string relativePath, string fileId, string? revision, byte[] payload)
        {
            var hash = SyncContentIdentity.Compute(Provider, payload);
            var existing = Entries.FindIndex(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.File, fileId, revision, payload.Length,
                hash);
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
            Payloads[relativePath] = payload;
        }

        internal byte[] PayloadOf(string relativePath) =>
            Payloads.TryGetValue(Normalize(relativePath), out var payload)
                ? payload
                : throw new InvalidDataException("This scripted target retains no payload at " + relativePath + ".");

        internal string Kind(PilotRemoteEntry entry) =>
            entry.Kind == PilotEntryKind.Directory ? "directory" : "file";

        internal PilotRemoteEntry? Find(string relativePath) => Entries.SingleOrDefault(entry =>
            string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));

        private static string Normalize(string relativePath)
        {
            try
            {
                return PilotPath.NormalizeRelative(relativePath);
            }
            catch (ArgumentException)
            {
                return relativePath;
            }
        }

        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => method?.Name switch
        {
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => List(),
            // DispatchProxy does not invoke interface default implementations.
            // Supply fixture inventory through the production exact-lookup decision.
            nameof(ISyncRemoteStore.ReadEntryAsync) => SyncRemoteExactLookup.RequireEntryAsync(
                (ISyncRemoteStore)(object)this, (string)arguments![0]!, (string)arguments[1]!,
                (CancellationToken)arguments[2]!),
            nameof(ISyncRemoteStore.FindEntryAtPathAsync) => SyncRemoteExactLookup.FindEntryAsync(
                (ISyncRemoteStore)(object)this, (string)arguments![0]!, (CancellationToken)arguments[1]!),
            nameof(ISyncRemoteStore.DownloadAsync) => Download(arguments),
            nameof(ISyncRemoteStore.UploadAsync) => Upload(arguments),
            nameof(ISyncRemoteRangeStore.DownloadRangeAsync) => DownloadRange(arguments),
            nameof(ISyncRemoteStore.VerifyRecoveryRootAsync) => VerifyRecovery(arguments),
            // The two recovery overloads share one method name, so the bound recovery-root overload is
            // distinguished by its own binding argument rather than by an argument count.
            nameof(ISyncRemoteStore.CopyToRecoveryAsync) => arguments is [_, SyncRemoteRecoveryRootBinding, ..]
                ? CopyToRecovery(arguments)
                : LegacyRecovery(method),
            nameof(ISyncRemoteStore.GetRecoveryAsync) => arguments is [SyncRemoteRecoveryRootBinding, ..]
                ? GetRecovery(arguments)
                : LegacyRecovery(method),
            _ => Refuse(method)
        };

        private object Verify()
        {
            VerifyCalls++;
            return Task.CompletedTask;
        }

        internal int VerifyCalls;
        internal int ListCalls;

        private object List()
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<PilotRemoteEntry>>(Entries.ToArray());
        }

        private object Download(object?[]? arguments)
        {
            var expected = (PilotRemoteEntry)arguments![0]!;
            var destination = (Stream)arguments[1]!;
            var relativePath = Normalize(expected.RelativePath);
            if (!Payloads.TryGetValue(relativePath, out var payload))
                throw new FileNotFoundException("The scripted source does not retain this path.", relativePath);
            destination.Position = 0;
            destination.SetLength(0);
            if (payload.Length > 0) destination.Write(payload, 0, payload.Length);
            destination.Flush();
            DuringDownload?.Invoke(relativePath);
            return Task.FromResult(new SyncDownloadResult(expected.Id!, expected.Revision ?? "", payload.Length,
                SyncContentIdentity.Compute(Provider, payload)));
        }

        private object Upload(object?[]? arguments)
        {
            var relativePath = Normalize((string)arguments![0]!);
            var source = (Stream)arguments[1]!;
            var expectedRevision = arguments[3] as string;
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            Uploads.Add((relativePath, bytes, expectedRevision ?? ""));
            var nativeHash = SyncContentIdentity.Compute(Provider, bytes);
            var current = Find(relativePath);
            if (string.IsNullOrEmpty(expectedRevision))
            {
                if (current is not null)
                    throw new IOException(
                        "This scripted target replaces an existing item only through its conditional guard.");
            }
            else
            {
                if (RefuseConditionalUpdate)
                    throw new NotSupportedException(
                        "This scripted target adapter cannot conditionally replace an existing item.");
                if (current is null ||
                    !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
                    throw new IOException(
                        "The scripted target rejected the conditional replacement because its revision moved after it was read.");
            }
            if (ApplyThenFailOnUpload)
            {
                ApplyThenFailOnUpload = false;
                Apply(relativePath, bytes, nativeHash, "rev-" + (++RevisionCounter));
                throw new IOException("The scripted provider applied the replacement and then lost the response.");
            }
            var revision = "rev-" + (++RevisionCounter);
            Apply(relativePath, bytes, nativeHash, revision);
            return Task.FromResult(CorruptUploadResult
                ? new PilotUploadResult("id-contradictory", revision, bytes.Length,
                    SyncContentIdentity.Compute(Provider, new byte[] { 1, 2, 3 }))
                : new PilotUploadResult(current?.Id ?? "id-file-" + relativePath.Replace('/', '-'), revision,
                    bytes.Length, nativeHash));
        }

        private void Apply(string relativePath, byte[] bytes, string nativeHash, string revision)
        {
            var existing = Entries.FindIndex(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            var id = existing >= 0 ? Entries[existing].Id! : "id-file-" + relativePath.Replace('/', '-');
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.File, id, revision, bytes.Length,
                nativeHash);
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
            Payloads[relativePath] = bytes;
        }

        /// <summary>
        /// The exact range read the real CC2F resolver uses for a non-SHA-256 revision. It serves bytes from
        /// this store's own retained revision only and returns the exact range receipt that must agree with
        /// the requested identity, size, native hash, offset and length.
        /// </summary>
        private object DownloadRange(object?[]? arguments)
        {
            var expected = (PilotRemoteEntry)arguments![0]!;
            var offset = (long)arguments![1]!;
            var length = (long)arguments![2]!;
            var destination = (Stream)arguments![3]!;
            var relativePath = Normalize(expected.RelativePath);
            if (!Payloads.TryGetValue(relativePath, out var payload))
                throw new FileNotFoundException("The scripted source does not retain this path.", relativePath);
            if (offset < 0 || length < 0 || offset + length > payload.Length)
                throw new IOException("The scripted target refused a byte range outside its retained revision.");
            destination.Write(payload, (int)offset, (int)length);
            destination.Flush();
            return Task.FromResult(new SyncRangeDownloadResult(expected.Id!, expected.Revision ?? "",
                payload.Length, SyncContentIdentity.Compute(Provider, payload), offset, length));
        }

        private object VerifyRecovery(object?[]? arguments)
        {
            var binding = (SyncRemoteRecoveryRootBinding)arguments![0]!;
            RequireBoundRecovery(binding);
            return Task.CompletedTask;
        }

        private void RequireBoundRecovery(SyncRemoteRecoveryRootBinding binding)
        {
            if (RecoveryBinding is not { } configured)
                throw new InvalidOperationException("This scripted target has no bound recovery root.");
            if (!string.Equals(configured.RootPath, binding.RootPath, StringComparison.Ordinal) ||
                !string.Equals(configured.RootFolderId, binding.RootFolderId, StringComparison.Ordinal) ||
                !string.Equals(configured.RootNamespaceId, binding.RootNamespaceId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The requested recovery root is not this scripted target's own bound recovery root.");
        }

        private object CopyToRecovery(object?[]? arguments)
        {
            var expected = (PilotRemoteEntry)arguments![0]!;
            var binding = (SyncRemoteRecoveryRootBinding)arguments[1]!;
            var name = (string)arguments[2]!;
            RequireBoundRecovery(binding);
            RecoveryCopyRequests++;
            var relativePath = Normalize(expected.RelativePath);
            if (!Payloads.TryGetValue(relativePath, out var payload))
                throw new FileNotFoundException("The scripted predecessor does not retain this path.",
                    relativePath);
            var current = Find(relativePath);
            if (current is null || !string.Equals(current.Id, expected.Id, StringComparison.Ordinal) ||
                !string.Equals(current.Revision, expected.Revision, StringComparison.Ordinal))
                throw new IOException(
                    "The scripted recovery source no longer matches the exact reviewed predecessor revision.");
            if (RecoveryCopies.ContainsKey(name))
                throw new IOException(
                    "The bound recovery root already retains this exact deterministic recovery name.");
            var revision = "rev-" + (++RevisionCounter);
            var result = new SyncArchiveResult("id-recovery-" + name, revision,
                binding.RootPath + "/" + name, payload.Length, SyncContentIdentity.Compute(Provider, payload));
            RecoveryCopies[name] = (result, payload);
            if (ApplyThenFailOnRecovery)
            {
                ApplyThenFailOnRecovery = false;
                throw new IOException(
                    "The scripted provider applied the recovery copy and then lost the response.");
            }
            return Task.FromResult(result);
        }

        private object GetRecovery(object?[]? arguments)
        {
            var binding = (SyncRemoteRecoveryRootBinding)arguments![0]!;
            var name = (string)arguments![1]!;
            RequireBoundRecovery(binding);
            RecoveryReadbacks++;
            if (RefuseRecoveryReadback)
                throw new IOException("The scripted recovery root readback failed.");
            if (!RecoveryCopies.TryGetValue(name, out var retained)) return Task.FromResult<SyncArchiveResult?>(null);
            if (CorruptRecoveryReadback)
                return Task.FromResult<SyncArchiveResult?>(retained.Result with
                {
                    ContentHash = SyncContentIdentity.Compute(Provider, new byte[] { 9, 9, 9 })
                });
            return Task.FromResult<SyncArchiveResult?>(retained.Result);
        }

        private object LegacyRecovery(MethodInfo? method)
        {
            LegacyRecoveryCalls++;
            _unexpectedEffects++;
            throw new NotSupportedException(
                "This cloud update execution fixture uses only the bound recovery-root overload: " +
                (method?.Name ?? "unknown"));
        }

        private object Refuse(MethodInfo? method)
        {
            RefusedEffects++;
            _unexpectedEffects++;
            throw new NotSupportedException("This cloud update execution fixture performs no such provider effect: " +
                (method?.Name ?? "unknown"));
        }
    }

    // ----- the real planner and executor drive --------------------------------------------------------

    /// <summary>
    /// Establishes one real baseline and then the real update operation for a changed source. Both logical
    /// endpoints are observed holding independently created equivalent content, so the real planner's own
    /// provider-aware comparison resolves the cross-provider common digest through the real CC2F
    /// revision-bound resolver and adopts one synchronization point without issuing any effect. The source is
    /// then replaced and re-observed, and the operation the real planner produces for that change is returned.
    /// </summary>
    private static async Task<SyncAuthorityOperationSnapshot> PlannedUpdateAsync(Site site, string sourceSide,
        byte[] predecessorPayload, byte[] successorPayload)
    {
        var targetSide = sourceSide == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityCloudSides.LogicalRight
            : SyncAuthorityCloudSides.LogicalLeft;
        var source = site.StoreFor(sourceSide);
        source.Seed(Path, "id-src-file", "rev-src-1", predecessorPayload);
        site.StoreFor(targetSide).Seed(Path, "id-target-file", "rev-target-1", predecessorPayload);
        await site.ObserveStoreAsync(sourceSide, "initial");
        await site.ObserveStoreAsync(targetSide, "initial");
        var adopted = await site.PlanBaselineAsync(Path);
        Assert(adopted.Kind == SyncAuthorityPlannerOutcomeKind.Adopted && adopted.OperationId is null,
            "the real planner adopted the converged baseline without an effect: " + adopted.Kind + " / " +
            adopted.Reason);
        Assert(site.StoreFor(sourceSide).Uploads.Count == 0 &&
               site.StoreFor(targetSide).Uploads.Count == 0,
            "adopting the converged baseline issued no provider effect");
        source.Seed(Path, "id-src-file", "rev-src-2", successorPayload);
        await site.ObserveStoreAsync(sourceSide, "monitor");
        var updateKind = sourceSide == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityOperationKind.UpdateRemote
            : SyncAuthorityOperationKind.UpdateLocal;
        var update = await site.PlanAsync(Path, updateKind);
        return await site.SnapshotAsync(update.OperationId!);
    }

    private static async Task PlannedUpdateAsync(string slug, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind, bool emptySuccessor, int size)
    {
        await using var site = await Site.CreateAsync("case-" + slug, slug, leftKind, rightKind);
        var predecessor = Pattern(Math.Max(size, 512));
        var successor = emptySuccessor ? Array.Empty<byte>() : Pattern(size + 256, 29);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var update = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(update.Status == SyncAuthorityOperationStatus.Completed,
            "the real cloud update completed: " + update.Reason);
        Assert(site.Right.Uploads.Count == 1,
            "exactly one conditional replacement reached the target adapter");
        Assert(site.Right.Uploads[0].ExpectedRevision.Length > 0,
            "the conditional replacement carried the exact retained predecessor revision");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(successor),
            "the target holds the exact staged successor bytes");
        Assert(site.Left.PayloadOf(Path).SequenceEqual(successor) && site.Left.Uploads.Count == 0,
            "the logical source still holds exactly its own staged bytes and received no replacement");
        Assert(site.Right.RecoveryCopyRequests == 1 && site.Right.RecoveryCopies.Count == 1,
            "exactly one predecessor recovery copy was retained in the target's own bound recovery root");
        Assert(site.Right.RecoveryCopies.Values.Single().Payload.SequenceEqual(predecessor),
            "the retained recovery copy holds the exact predecessor bytes");
        Assert(site.Left.RecoveryCopyRequests == 0 && site.Left.RecoveryCopies.Count == 0,
            "the untouched logical source took no recovery copy of itself");
        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed,
            "the journal records the cloud update as completed");
        Assert(completed.Attempts.Count == 2 &&
               completed.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess),
            "both the recovery and the replacement attempt are terminally verified success");
        Assert(completed.Attempts.Count(item =>
                   string.Equals(item.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind,
                       StringComparison.Ordinal)) == 1,
            "the recovery copy journals its own separate attempt");
        var transaction = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var recoveryAllocation = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(transaction.Status == SyncAuthorityAllocationStatus.Released &&
               stageAllocation.Status == SyncAuthorityAllocationStatus.Released,
            "the owned private staging allocations were released after commit");
        Assert(recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified &&
               recoveryAllocation.IntendedParentIdentity == site.RightRecoveryRoot,
            "the recovery allocation stays verified against the target's own recovery authority");
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
        Assert(!Directory.Exists(transactionPath) && !File.Exists(transactionPath),
            "the owned private stage and its empty transaction were removed after commit");
        Assert(string.IsNullOrEmpty(site.Pair.LocalRoot) && site.Pair.LocalVolumeSerialNumber == 0 &&
               site.Pair.LocalRootFileId == 0,
            "no selected NTFS root, volume serial or root file id is projected by the cloud pair");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        var point = points.Single(item =>
            string.Equals(item.ObjectId, completed.ObjectId, StringComparison.Ordinal));
        var sourceVersionId = operation.Members.Single(item => item.Role == "source").ObjectVersionId;
        var predecessorVersionId = operation.Members.Single(item => item.Role == "destination").ObjectVersionId;
        Assert(point.LocalObjectVersionId == sourceVersionId,
            "the retained source keeps its own (local) slot of the ordered point");
        Assert(point.DropboxObjectVersionId is not null &&
               point.DropboxObjectVersionId != predecessorVersionId,
            "the successor occupies a new version of the target (dropbox) slot");
        Assert(point.LocalMembershipId is not null && point.DropboxMembershipId is not null,
            "both ordered memberships are retained on the point");
        var retainedPredecessor = await site.Db.LoadObjectVersionAsync(predecessorVersionId!);
        Assert(string.Equals(retainedPredecessor.ContentHash,
                SyncContentIdentity.Compute(site.Right.Provider, predecessor), StringComparison.OrdinalIgnoreCase),
            "the replaced predecessor version row is retained unchanged");
        Assert(site.Right.LegacyRecoveryCalls == 0 && site.Right.RefusedEffects == 0 &&
               site.Left.RefusedEffects == 0,
            "no legacy unbound recovery overload and no unrequested provider effect was reached");
    }

    // ----- cases N-O: the Ready update retries and cancels from its retained verified stage ---------

    private static async Task RetainedStageReadyRetryAsync()
    {
        await using var site = await Site.CreateAsync("case-n", "n", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(1408, 151);
        var successor = Pattern(1792, 157);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var sourceDownloads = 0;
        site.Left.DuringDownload = _ => sourceDownloads++;
        // The adapter's own contract declares the conditional replacement unsupported for the first request:
        // a conclusive non-application that leaves the operation Ready with its retained verified stage.
        site.Right.RefuseConditionalUpdate = true;
        var refused = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(refused.Status == SyncAuthorityOperationStatus.Ready && !refused.EffectDispatched,
            "a declined conditional replacement is a non-application that leaves the update Ready: " +
            refused.Reason);
        Assert(sourceDownloads == 1, "the refused attempt read the exact retained source exactly once");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(predecessor),
            "the declined replacement left the exact predecessor untouched");
        var ready = await site.SnapshotAsync(operation.OperationId);
        var stageAllocation = ready.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var transactionAllocation = ready.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        Assert(stageAllocation.Status == SyncAuthorityAllocationStatus.Verified &&
               transactionAllocation.Status == SyncAuthorityAllocationStatus.Verified,
            "both owned private staging allocations are retained exactly verified at the Ready boundary");
        Assert(ready.Attempts.Count(item =>
                   string.Equals(item.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind,
                       StringComparison.Ordinal)) == 1 &&
               ready.Attempts.Single(item =>
                   item.EffectKind.EndsWith("-conditional-update", StringComparison.Ordinal)).Status ==
               SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "the retained recovery attempt and the single verified not-applied replacement attempt are exact");
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transactionAllocation.DeterministicName);
        var stagePath = IOPath.Combine(transactionPath, stageAllocation.DeterministicName);
        Assert(File.Exists(stagePath) && (await File.ReadAllBytesAsync(stagePath)).SequenceEqual(successor),
            "the retained private stage holds the exact staged successor bytes");
        var retainedRecoveryRequests = site.Right.RecoveryCopyRequests;
        Assert(retainedRecoveryRequests == 1 && site.Right.Uploads.Count == 1,
            "one predecessor recovery copy and one refused replacement were issued, and nothing else");

        // The live provider source then changes revision and content. The admitted immutable source version is
        // the retained stage, not the current provider object, so the retry must neither re-read it nor rebind.
        site.Left.Seed(Path, "id-src-file", "rev-src-3", Pattern(2048, 163));

        site.Right.RefuseConditionalUpdate = false;
        var restarted = new SyncAuthorityDatabase(site.Db.DatabasePath);
        var retried = await new SyncAuthorityCloudUpdateExecutor(restarted, site.Context(),
                site.SideAuthorities())
            .ExecuteAsync(operation.OperationId, site.Worker);
        Assert(retried.Status == SyncAuthorityOperationStatus.Completed,
            "the Ready update retried from its retained verified stage and completed: " + retried.Reason);
        Assert(sourceDownloads == 1,
            "the retry opened the retained stage and downloaded the provider source zero further times");
        Assert(site.Left.Uploads.Count == 0, "the logical source received no replacement");
        Assert(site.Right.Uploads.Count == 2 && site.Right.Uploads[1].Bytes.SequenceEqual(successor) &&
               site.Right.Uploads[1].ExpectedRevision == site.Right.Uploads[0].ExpectedRevision,
            "the retry issued exactly one permitted replacement over the retained bytes and the same exact predecessor revision");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(successor),
            "the target holds exactly the retained staged successor");
        Assert(site.Right.RecoveryCopyRequests == retainedRecoveryRequests,
            "the retry never retained the predecessor a second time");

        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed,
            "the journal records the retried update as completed");
        Assert(completed.Attempts.Count == 3 &&
               completed.Attempts.Count(item =>
                   item.Status == SyncAuthorityAttemptStatus.VerifiedNotApplied) == 1 &&
               completed.Attempts.Count(item =>
                   item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) == 2,
            "the retry dispatched exactly one new replacement attempt and no duplicate effect");
        var retriedStage = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var retriedTransaction = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        Assert(retriedStage.AllocationId == stageAllocation.AllocationId &&
               retriedStage.BoundIdentity == stageAllocation.BoundIdentity &&
               retriedTransaction.AllocationId == transactionAllocation.AllocationId,
            "the retry reused the exact owned staging allocations; no second stage was created");
        Assert(retriedStage.Status == SyncAuthorityAllocationStatus.Released &&
               retriedTransaction.Status == SyncAuthorityAllocationStatus.Released &&
               !Directory.Exists(transactionPath) && !File.Exists(transactionPath),
            "the owned private stage was removed only after the retry committed");
        Assert(completed.Allocations.Single(item =>
                   item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status ==
               SyncAuthorityAllocationStatus.Verified &&
               site.Right.RecoveryCopies.Values.Single().Payload.SequenceEqual(predecessor),
            "the single retained predecessor recovery is preserved exactly through the retry");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Count(item => string.Equals(item.ObjectId, completed.ObjectId,
            StringComparison.Ordinal)) == 1,
            "the retried replacement committed exactly one synchronization point");
    }

    private static async Task RetainedStageCancellationAsync()
    {
        // A bounded multi-chunk staged successor: the caller's own token is observed between the 128 KiB read
        // chunks of the one rehash pass and is never replaced by a test fake.
        await using var site = await Site.CreateAsync("case-o", "o", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(512, 167);
        var successor = Pattern(4 * 1024 * 1024, 173);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var sourceDownloads = 0;
        site.Left.DuringDownload = _ => sourceDownloads++;
        site.Right.RefuseConditionalUpdate = true;
        var refused = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(refused.Status == SyncAuthorityOperationStatus.Ready && !refused.EffectDispatched,
            "a declined conditional replacement leaves the update Ready: " + refused.Reason);
        Assert(sourceDownloads == 1, "the refused attempt read the exact retained source exactly once");

        var ready = await site.SnapshotAsync(operation.OperationId);
        var stageAllocation = ready.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var transactionAllocation = ready.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transactionAllocation.DeterministicName);
        var stagePath = IOPath.Combine(transactionPath, stageAllocation.DeterministicName);
        Assert(File.Exists(stagePath), "the Ready update retained its exact private stage file");
        var retainedWriteTime = new FileInfo(stagePath).LastWriteTimeUtc;
        var uploadsBefore = site.Right.Uploads.Count;
        var recoveryBefore = site.Right.RecoveryCopyRequests;

        var cancellation = new CancellationTokenSource();
        var signalled = false;
        var bytesAtCancellation = 0L;
        var signalledAt = 0L;
        using (SyncAuthorityCloudContentStage.ObserveHashChunksForTest(bytes =>
        {
            if (!signalled && bytes > 0)
            {
                signalled = true;
                bytesAtCancellation = bytes;
                signalledAt = System.Diagnostics.Stopwatch.GetTimestamp();
                cancellation.Cancel();
            }
        }))
        {
            await ExpectCancelledAsync(() => site.UpdateExecutor().ExecuteAsync(operation.OperationId,
                    site.Worker, cancellation.Token),
                "a caller cancellation inside the retained-stage rehash");
        }
        var measured = System.Diagnostics.Stopwatch.GetElapsedTime(signalledAt);
        Assert(signalled && bytesAtCancellation > 0 && bytesAtCancellation < successor.LongLength,
            "the cancellation was raised after the retained rehash began and before it completed: " +
            bytesAtCancellation + " of " + successor.Length + " bytes hashed");
        Console.WriteLine("MEASURED retained-update-rehash stop: " + bytesAtCancellation + " of " +
            successor.Length + " bytes hashed at cancellation; " + Math.Round(measured.TotalMilliseconds, 3) +
            " ms measured from the caller's cancellation to the observed refusal on this fixture volume " +
            "(a measured fixture latency, not an OS scheduling guarantee).");

        Assert(sourceDownloads == 1, "the cancelled retained rehash read no provider source");
        Assert(site.Right.Uploads.Count == uploadsBefore &&
               site.Right.RecoveryCopyRequests == recoveryBefore,
            "the cancelled retained rehash issued no replacement request and no recovery copy");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(predecessor),
            "the cancelled retained rehash left the exact predecessor untouched");
        Assert(Directory.Exists(transactionPath) && File.Exists(stagePath) &&
               new FileInfo(stagePath).Length == successor.LongLength &&
               new FileInfo(stagePath).LastWriteTimeUtc == retainedWriteTime &&
               (await File.ReadAllBytesAsync(stagePath)).SequenceEqual(successor),
            "the cancellation retained the exact stage object and its exact bytes without a rewrite");
        var cancelled = await site.SnapshotAsync(operation.OperationId);
        var cancelledStage = cancelled.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        Assert(cancelled.Status == SyncAuthorityOperationStatus.Ready &&
               cancelled.Attempts.Count == ready.Attempts.Count &&
               cancelled.Attempts.Single(item =>
                   item.EffectKind.EndsWith("-conditional-update", StringComparison.Ordinal)).Status ==
               SyncAuthorityAttemptStatus.VerifiedNotApplied &&
               cancelledStage.Status == SyncAuthorityAllocationStatus.Verified &&
               cancelledStage.AllocationId == stageAllocation.AllocationId &&
               cancelledStage.BoundIdentity == stageAllocation.BoundIdentity,
            "the cancelled retained rehash preserved the exact journal state, allocation dispositions and identities");

        // The same retained stage then still supports the ordinary permitted retry to completion.
        site.Right.RefuseConditionalUpdate = false;
        var retried = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(retried.Status == SyncAuthorityOperationStatus.Completed,
            "the retained stage still retried to completion after the cancelled rehash: " + retried.Reason);
        Assert(sourceDownloads == 1 && site.Right.Uploads.Count == uploadsBefore + 1 &&
               site.Right.RecoveryCopyRequests == recoveryBefore,
            "the permitted retry issued exactly one replacement request, no further download and no second recovery copy");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(successor),
            "the retried replacement committed the exact retained staged successor");
        Assert(!Directory.Exists(transactionPath),
            "the retained stage was removed only after the permitted retry committed");
    }

    private static async Task EachDirectionAsync()
    {
        // A pair whose two logical endpoints share one provider requires provider-verified native ancestry
        // before the shipped cloud root guard will accept the second root. No fixture here fabricates that
        // proof, so this is asserted as the product's own refusal rather than a completed pairing.
        var refused = false;
        try
        {
            await using var same = await Site.CreateAsync("case-c-same", "csame", SyncEndpointKind.Dropbox,
                SyncEndpointKind.Dropbox);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
        {
            refused = true;
        }
        Assert(refused,
            "a same-provider cloud pair without provider-verified native ancestry is refused by the shared root guard");
        await ReverseDirectionUpdateAsync("c-db-gd", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        await ReverseDirectionUpdateAsync("c-gd-db", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox);
    }

    /// <summary>
    /// The other logical direction: the source is on the right and the replacement targets the left cloud
    /// endpoint through the same executor.
    /// </summary>
    private static async Task ReverseDirectionUpdateAsync(string slug, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind)
    {
        await using var site = await Site.CreateAsync("case-" + slug, slug, leftKind, rightKind);
        var predecessor = Pattern(1536);
        var successor = Pattern(1792, 41);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalRight, predecessor,
            successor);
        Assert(operation.Kind == SyncAuthorityOperationKind.UpdateLocal,
            "a right-side source plans a replacement on the left logical target for " + slug);
        var update = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(update.Status == SyncAuthorityOperationStatus.Completed,
            "the reverse-direction replacement completed on the left target for " + slug + ": " +
            update.Reason);
        Assert(site.Left.Uploads.Count == 1 && site.Right.Uploads.Count == 0,
            "the reverse-direction replacement reached only its own logical target for " + slug);
        Assert(site.Left.PayloadOf(Path).SequenceEqual(successor) &&
               site.Right.PayloadOf(Path).SequenceEqual(successor) && site.Right.Uploads.Count == 0,
            "the left target holds the successor and the right source received no replacement for " + slug);
        Assert(site.Left.RecoveryCopyRequests == 1 && site.Right.RecoveryCopyRequests == 0,
            "the predecessor was retained in the LEFT target's own bound recovery root for " + slug);
        Assert(site.Left.RecoveryCopies.Values.Single().Payload.SequenceEqual(predecessor),
            "the left recovery root holds the exact predecessor bytes for " + slug);
        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Allocations.Single(item =>
                   item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).IntendedParentIdentity ==
               site.LeftRecoveryRoot,
            "the reverse-direction recovery allocation is bound to the left target's own recovery authority for " +
            slug);
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        var point = points.Single(item => string.Equals(item.ObjectId, completed.ObjectId,
            StringComparison.Ordinal));
        var sourceVersionId = operation.Members.Single(item => item.Role == "source").ObjectVersionId;
        Assert(point.DropboxObjectVersionId == sourceVersionId &&
               point.LocalObjectVersionId != sourceVersionId,
            "the reverse-direction point keeps the source version in the dropbox slot for " + slug);
    }

    // ----- refusals -----------------------------------------------------------------------------------

    private static async Task PlanAuthorityRefusalsAsync()
    {
        await using var site = await Site.CreateAsync("case-e", "e", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(640);
        var successor = Pattern(768, 17);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);

        // A typed endpoint set of another pair can never drive this retained operation.
        await using var other = await Site.CreateAsync("case-e-other", "eother", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox);
        var foreign = new SyncAuthorityCloudUpdateExecutor(site.Db,
            new SyncAuthorityCloudExecutionContext(other.Endpoints(), site.Control, site.ControlRoot),
            site.SideAuthorities());
        await ExpectRefusalAsync(() => foreign.ExecuteAsync(operation.OperationId, site.Worker),
            "an operation of another pair");
        Assert(site.Right.Uploads.Count == 0 && site.Right.RecoveryCopyRequests == 0,
            "the foreign pair issued no recovery and no replacement request");

        // A recovery authority that names the target's own pair root is never a recovery boundary.
        var selfRoot = site.SideAuthority(SyncAuthorityCloudSides.LogicalRight) with
        {
            RecoveryRootBindingId = site.RightRoot,
            RecoveryFolderId = site.Right.FolderId
        };
        var selfRootExecutor = new SyncAuthorityCloudUpdateExecutor(site.Db, site.Context(),
            new[] { site.SideAuthority(SyncAuthorityCloudSides.LogicalLeft), selfRoot });
        await ExpectRefusalAsync(() => selfRootExecutor.ExecuteAsync(operation.OperationId, site.Worker),
            "a recovery authority that names the pair root itself");
        Assert(site.Right.Uploads.Count == 0 && site.Right.RecoveryCopyRequests == 0,
            "the pair-root recovery authority issued no request");

        // An unretained corpus can never be routed to a recovery root.
        var foreignCorpus = site.SideAuthority(SyncAuthorityCloudSides.LogicalRight) with
        {
            RecoveryCorpusId = "corpus-not-retained"
        };
        var foreignCorpusExecutor = new SyncAuthorityCloudUpdateExecutor(site.Db, site.Context(),
            new[] { site.SideAuthority(SyncAuthorityCloudSides.LogicalLeft), foreignCorpus });
        await ExpectRefusalAsync(() => foreignCorpusExecutor.ExecuteAsync(operation.OperationId, site.Worker),
            "an unretained recovery corpus");
        Assert(site.Right.Uploads.Count == 0 && site.Right.RecoveryCopyRequests == 0,
            "the unretained corpus issued no request");

        // A recovery authority that names another provider account is refused against the target port.
        var foreignAccount = site.SideAuthority(SyncAuthorityCloudSides.LogicalRight) with
        {
            ProviderAccountId = "account-e-foreign"
        };
        var foreignAccountExecutor = new SyncAuthorityCloudUpdateExecutor(site.Db, site.Context(),
            new[] { site.SideAuthority(SyncAuthorityCloudSides.LogicalLeft), foreignAccount });
        await ExpectRefusalAsync(() => foreignAccountExecutor.ExecuteAsync(operation.OperationId, site.Worker),
            "a recovery authority that names another provider account");
        Assert(site.Right.Uploads.Count == 0 && site.Right.RecoveryCopyRequests == 0,
            "the foreign account issued no request");

        // A recovery authority that names another provider is refused against the target port.
        var foreignProvider = site.SideAuthority(SyncAuthorityCloudSides.LogicalRight) with
        {
            Provider = SyncProviders.Dropbox
        };
        var foreignProviderExecutor = new SyncAuthorityCloudUpdateExecutor(site.Db, site.Context(),
            new[] { site.SideAuthority(SyncAuthorityCloudSides.LogicalLeft), foreignProvider });
        await ExpectRefusalAsync(() => foreignProviderExecutor.ExecuteAsync(operation.OperationId, site.Worker),
            "a recovery authority that names another provider");

        // The untouched plan is still exactly the retained one and no allocation advanced.
        var untouched = await site.SnapshotAsync(operation.OperationId);
        Assert(untouched.Attempts.Count == 0 &&
               untouched.Status is SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased,
            "every refused authority left the retained plan with no dispatched attempt: " + untouched.Status);
        Assert(untouched.Allocations.All(item => item.Status == SyncAuthorityAllocationStatus.Authorized),
            "no refused authority advanced an allocation or created a private staging object");

        // A drifted target predecessor is refused before any recovery copy or replacement.
        await using var drift = await Site.CreateAsync("case-e-drift", "edrift", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var driftOperation = await PlannedUpdateAsync(drift, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(512, 11), Pattern(640, 13));
        drift.Right.Seed(Path, drift.Right.Find(Path)!.Id!, "rev-concurrent", Pattern(704, 19));
        await ExpectRefusalAsync(() => drift.UpdateExecutor().ExecuteAsync(driftOperation.OperationId,
            drift.Worker), "a target predecessor that drifted before the replacement");
        Assert(drift.Right.RecoveryCopyRequests == 0 && drift.Right.Uploads.Count == 0,
            "the drifted predecessor issued no recovery copy and no replacement request");
        Assert(drift.Right.PayloadOf(Path).SequenceEqual(Pattern(704, 19)),
            "the concurrent target content was left exactly as the concurrent writer made it");

        // A drifted source revision is refused while the stage is being taken, before any recovery.
        await using var sourceDrift = await Site.CreateAsync("case-e-source", "esource",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var sourceDriftOperation = await PlannedUpdateAsync(sourceDrift, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(384, 23), Pattern(448, 29));
        sourceDrift.Left.Seed(Path, "id-src-file", "rev-drifted", Pattern(448, 31));
        await ExpectAnyRefusalAsync(() => sourceDrift.UpdateExecutor().ExecuteAsync(
            sourceDriftOperation.OperationId, sourceDrift.Worker), "a source that drifted before its stage");
        Assert(sourceDrift.Right.RecoveryCopyRequests == 0 && sourceDrift.Right.Uploads.Count == 0,
            "the drifted source issued no recovery copy and no replacement request");

        // A create operation can never be routed through the shared update engine.
        await using var foreignKind = await Site.CreateAsync("case-e-kind", "ekind",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        foreignKind.Left.Seed(Path, "id-src-file", "rev-src-1", Pattern(256, 37));
        await foreignKind.ObserveStoreAsync(SyncAuthorityCloudSides.LogicalLeft, "initial");
        await foreignKind.ObserveStoreAsync(SyncAuthorityCloudSides.LogicalRight, "initial");
        var createOperation = await foreignKind.PlanAsync(Path, SyncAuthorityOperationKind.CreateRemote);
        await ExpectRefusalAsync(() => foreignKind.UpdateExecutor().ExecuteAsync(createOperation.OperationId!,
            foreignKind.Worker), "a create operation routed through the shared update executor");
        Assert(foreignKind.Right.Uploads.Count == 0,
            "the foreign kind issued no target request");

    }

    private static async Task RecoveryRefusalsAsync()
    {
        // A recovery copy that reads back as something other than the exact predecessor admits no replacement
        // and preserves the retained allocation and evidence.
        await using var site = await Site.CreateAsync("case-f", "f", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(896);
        var successor = Pattern(1024, 53);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        site.Right.CorruptRecoveryReadback = true;
        var refused = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(refused.Status == SyncAuthorityOperationStatus.Blocked,
            "a drifting recovery readback blocks the replacement: " + refused.Reason);
        Assert(site.Right.Uploads.Count == 0,
            "the drifting recovery readback issued no replacement request");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(predecessor),
            "the target still holds the exact untouched predecessor");
        var blocked = await site.SnapshotAsync(operation.OperationId);
        Assert(blocked.Status != SyncAuthorityOperationStatus.Completed &&
               blocked.Status != SyncAuthorityOperationStatus.Committed,
            "the drifting recovery readback committed nothing");
        Assert(blocked.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.Blocked),
            "the drifting recovery attempt is terminally blocked");
        var retained = blocked.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(retained.Status == SyncAuthorityAllocationStatus.Creating,
            "the refused recovery retained its allocation exactly where the unverified copy left it: " +
            retained.Status);
        var stageAllocation = blocked.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var transaction = blocked.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stagePath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName,
            stageAllocation.DeterministicName);
        Assert(File.Exists(stagePath), "the refused recovery retained its exact private stage");
        var artifacts = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM recovery_artifact WHERE operation_id='" + operation.OperationId + "';");
        Assert(artifacts == "0",
            "no recovery descriptor is registered for a copy whose exact readback never verified");
        Assert(site.Right.RecoveryCopies.Count == 1,
            "the applied-but-unverified recovery copy is retained in its bound recovery root as evidence");
        var drift = await ScalarAsync(site.Db.DatabasePath,
            "SELECT block_detail FROM operation WHERE operation_id='" + operation.OperationId + "';");
        Assert(drift is not null && drift.Contains("recovery readback drift", StringComparison.Ordinal),
            "the drifted recovery copy is durably described by its own blocked attempt detail: " + drift);
        var recents = await ScalarAsync(site.Db.DatabasePath,
            "SELECT event_kind FROM history_event WHERE operation_id='" + operation.OperationId +
            "' ORDER BY occurred_utc DESC LIMIT 1;");
        Assert(recents == "attempt_reconciled",
            "the drifted recovery left a durable reconciliation history event: " + recents);

        // A recovery root whose own readback cannot be read is blocked the same way: the copy may have been
        // applied, so no replacement is admitted and the evidence is preserved.
        await using var failing = await Site.CreateAsync("case-f-readback", "freadback",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var failingOperation = await PlannedUpdateAsync(failing, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(512, 59), Pattern(576, 61));
        failing.Right.RefuseRecoveryReadback = true;
        var failingRefusal = await failing.UpdateExecutor().ExecuteAsync(failingOperation.OperationId,
            failing.Worker);
        Assert(failingRefusal.Status == SyncAuthorityOperationStatus.Blocked,
            "an unreadable recovery root blocks the replacement rather than applying it: " +
            failingRefusal.Reason);
        Assert(failing.Right.Uploads.Count == 0,
            "the unreadable recovery root issued no replacement request");
        Assert(failing.Right.PayloadOf(Path).SequenceEqual(Pattern(512, 59)),
            "the unreadable recovery root left the target predecessor untouched");
    }

    private static async Task TransportRefusalsAsync()
    {
        // A stale foreign fence is never admitted twice.
        await using var fenced = await Site.CreateAsync("case-g-fence", "gfence", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var fencedOperation = await PlannedUpdateAsync(fenced, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(512, 67), Pattern(640, 71));
        var held = await fenced.Db.AcquireOperationLeaseAsync(fencedOperation.OperationId, "another-worker",
            TimeSpan.FromMinutes(5));
        await ExpectAnyRefusalAsync(() => fenced.UpdateExecutor().ExecuteAsync(fencedOperation.OperationId,
            fenced.Worker), "an operation that already holds a live foreign lease");
        Assert(fenced.Right.Uploads.Count == 0 && fenced.Right.RecoveryCopyRequests == 0,
            "the stale-fence refusal issued no recovery and no replacement request");
        await fenced.Db.ReleaseLeaseAsync(held, CancellationToken.None);

        // A cancelled source read issues no recovery copy and retains its private stage.
        await using var cancelled = await Site.CreateAsync("case-g-cancel", "gcancel",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var cancelledOperation = await PlannedUpdateAsync(cancelled, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(448, 73), Pattern(512, 79));
        using var cancellation = new CancellationTokenSource();
        cancelled.Left.DuringDownload = _ => cancellation.Cancel();
        await ExpectCancelledAsync(() => cancelled.UpdateExecutor().ExecuteAsync(cancelledOperation.OperationId,
            cancelled.Worker, cancellation.Token), "a cancelled source read");
        cancelled.Left.DuringDownload = null;
        Assert(cancelled.Right.Uploads.Count == 0 && cancelled.Right.RecoveryCopyRequests == 0,
            "the cancelled update issued no recovery and no replacement request");
        var cancelledSnapshot = await cancelled.SnapshotAsync(cancelledOperation.OperationId);
        Assert(cancelledSnapshot.Attempts.Count == 0,
            "the cancelled update dispatched no attempt");
        Assert(cancelledSnapshot.Allocations.Where(item =>
                item.Kind == SyncAuthorityAllocationKind.StageFile)
            .All(item => item.Status != SyncAuthorityAllocationStatus.Released),
            "the cancelled update retained its private stage allocation for reconciliation");

        // An adapter whose own contract declares conditional replacement unsupported is a known non-application.
        await using var unsupported = await Site.CreateAsync("case-g-unsupported", "gunsupported",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var unsupportedOperation = await PlannedUpdateAsync(unsupported, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(576, 83), Pattern(640, 89));
        unsupported.Right.RefuseConditionalUpdate = true;
        var notApplied = await unsupported.UpdateExecutor().ExecuteAsync(unsupportedOperation.OperationId,
            unsupported.Worker);
        Assert(notApplied.Status == SyncAuthorityOperationStatus.Ready && !notApplied.EffectDispatched,
            "an unsupported conditional replacement is a conclusive non-application: " + notApplied.Reason);
        Assert(unsupported.Right.PayloadOf(Path).SequenceEqual(Pattern(576, 83)),
            "the unsupported capability left the exact predecessor untouched");
        Assert(unsupported.Right.RecoveryCopyRequests == 1,
            "the predecessor was still retained before the replacement was attempted");
        var unsupportedSnapshot = await unsupported.SnapshotAsync(unsupportedOperation.OperationId);
        Assert(unsupportedSnapshot.Attempts.Single(item =>
                   item.EffectKind.EndsWith("-conditional-update", StringComparison.Ordinal)).Status ==
               SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "the unsupported replacement attempt is terminally verified not applied");

        // A conditional target race is never overwritten unconditionally and is never blindly retried.
        await using var race = await Site.CreateAsync("case-g-race", "grace", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var raceOperation = await PlannedUpdateAsync(race, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(704, 97), Pattern(768, 101));
        var raced = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "predecessor-confirmed-before-request" && !raced)
            {
                raced = true;
                race.Right.Seed(Path, race.Right.Find(Path)!.Id!, "rev-race", Pattern(832, 103));
            }
        };
        try
        {
            var racedResult = await race.UpdateExecutor().ExecuteAsync(raceOperation.OperationId,
                race.Worker);
            Assert(racedResult.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                   racedResult.EffectDispatched,
                "a conditional replacement rejected by a target race is an unknown outcome: " +
                racedResult.Reason);
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        Assert(raced, "the scripted target race was injected immediately before the request boundary");
        Assert(race.Right.Uploads.Count == 1,
            "the raced replacement issued exactly one request and never retried it");
        Assert(race.Right.PayloadOf(Path).SequenceEqual(Pattern(832, 103)),
            "the concurrent writer's content was never overwritten unconditionally");
        var raceReconciled = await race.UpdateExecutor().ReconcileAsync(raceOperation.OperationId, race.Worker);
        Assert(raceReconciled.Status is not (SyncAuthorityOperationStatus.Committed or
                   SyncAuthorityOperationStatus.Completed),
            "reconciling a raced replacement never commits from the concurrent writer's bytes: " +
            raceReconciled.Reason);
        Assert(race.Right.Uploads.Count == 1,
            "reconciliation of the raced replacement issued no second replacement request");
        Assert(race.Right.PayloadOf(Path).SequenceEqual(Pattern(832, 103)),
            "the concurrent writer's content was preserved through reconciliation");

        // A contradictory replacement response is never committed.
        await using var corrupt = await Site.CreateAsync("case-g-corrupt", "gcorrupt",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var corruptOperation = await PlannedUpdateAsync(corrupt, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(160, 107), Pattern(224, 109));
        corrupt.Right.CorruptUploadResult = true;
        var contradictory = await corrupt.UpdateExecutor().ExecuteAsync(corruptOperation.OperationId,
            corrupt.Worker);
        Assert(contradictory.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "a response that contradicts the held bytes is treated as an unknown outcome");
        var corruptSnapshot = await corrupt.SnapshotAsync(corruptOperation.OperationId);
        Assert(corruptSnapshot.Status != SyncAuthorityOperationStatus.Completed &&
               corruptSnapshot.Status != SyncAuthorityOperationStatus.Committed,
            "the contradictory response committed no successor");
    }

    private static async Task RecoveryResponseLostAsync()
    {
        await using var site = await Site.CreateAsync("case-h", "h", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(1152);
        var successor = Pattern(1280, 113);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        site.Right.ApplyThenFailOnRecovery = true;
        var unknown = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && unknown.EffectDispatched,
            "a recovery copy applied with a lost response is an unknown outcome: " + unknown.Reason);
        Assert(site.Right.Uploads.Count == 0,
            "the lost recovery response was never followed by a replacement request");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(predecessor),
            "the target predecessor is untouched after the lost recovery response");
        var unresolved = await site.SnapshotAsync(operation.OperationId);
        Assert(unresolved.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "the journal retains the unresolved recovery state");
        Assert(unresolved.Attempts.Single().Status == SyncAuthorityAttemptStatus.EffectUnknown,
            "the recovery attempt itself is unresolved");
        var recoveryAllocation = unresolved.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(recoveryAllocation.BoundIdentity is null &&
               recoveryAllocation.Status != SyncAuthorityAllocationStatus.Verified,
            "the unresolved recovery never bound its allocation to a verified object");
        var stageAllocation = unresolved.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var transaction = unresolved.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        Assert(File.Exists(IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName,
                stageAllocation.DeterministicName)),
            "the unresolved recovery retained its exact private stage as evidence");
        // The operation never becomes runnable again and reconciliation never turns the recovery into a
        // replacement.
        await ExpectAnyRefusalAsync(() => site.UpdateExecutor().ExecuteAsync(operation.OperationId,
            site.Worker), "re-executing an operation with an unresolved recovery");
        // CC3C-CHECK-1 (root-approved scope): a journal whose only unresolved attempt is the predecessor
        // recovery copy no longer throws from the reconciler. It returns an explicit no-effect unresolved
        // result, so the exact retained status, the absence of any dispatched effect, and every durable
        // operation, attempt, receipt, recovery and stage record plus the provider write counters are
        // asserted to be unchanged.
        var receiptsBefore = await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM effect_receipt;");
        var artifactsBefore = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM recovery_artifact;");
        var copiesBefore = site.Right.RecoveryCopies.Count;
        var legacyBefore = site.Right.LegacyRecoveryCalls;
        var refusedBefore = site.Right.RefusedEffects;
        var reconciliation = await site.UpdateExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(reconciliation.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               !reconciliation.EffectDispatched,
            "reconciling a journal whose only unresolved attempt is the recovery returns its exact retained unresolved status without dispatching an effect: " +
            reconciliation.Reason);
        var reconciled = await site.SnapshotAsync(operation.OperationId);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               reconciled.Status == unresolved.Status,
            "the recovery-only reconciliation left the durable operation status exactly as it was: " +
            reconciled.Status);
        Assert(reconciled.Attempts.Count == unresolved.Attempts.Count &&
               reconciled.Attempts.Zip(unresolved.Attempts).All(pair =>
                   pair.First.AttemptId == pair.Second.AttemptId &&
                   pair.First.Status == pair.Second.Status),
            "the recovery-only reconciliation wrote no attempt transition");
        Assert(reconciled.Allocations.Count == unresolved.Allocations.Count &&
               reconciled.Allocations.Zip(unresolved.Allocations).All(pair =>
                   pair.First.AllocationId == pair.Second.AllocationId &&
                   pair.First.Status == pair.Second.Status),
            "the recovery-only reconciliation wrote no allocation transition");
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM effect_receipt;") == receiptsBefore &&
               await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM recovery_artifact;") ==
               artifactsBefore,
            "the recovery-only reconciliation wrote no receipt and no recovery artifact");
        Assert(File.Exists(IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName,
                stageAllocation.DeterministicName)),
            "the recovery-only reconciliation retained the exact private stage as evidence");
        Assert(site.Right.RecoveryCopies.Count == copiesBefore &&
               site.Right.LegacyRecoveryCalls == legacyBefore &&
               site.Right.RefusedEffects == refusedBefore,
            "the recovery-only reconciliation reached no unrequested provider effect");
        Assert(site.Right.RecoveryCopyRequests == 1 && site.Right.Uploads.Count == 0,
            "neither re-execution nor reconciliation issued a second copy or any replacement");
    }

    private static async Task VerifiedRecoveryRestartAsync()
    {
        await using var site = await Site.CreateAsync("case-i", "i", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(1408);
        var successor = Pattern(1536, 127);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var crashed = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-recovery-verified" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the verified predecessor recovery");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.UpdateExecutor().ExecuteAsync(operation.OperationId,
                site.Worker), "a crash after the verified predecessor recovery");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        Assert(crashed, "the scripted post-recovery crash was reached");
        Assert(site.Right.RecoveryCopyRequests == 1 && site.Right.Uploads.Count == 0,
            "the crash left exactly one recovery copy and no replacement request");
        var crashedSnapshot = await site.SnapshotAsync(operation.OperationId);
        Assert(crashedSnapshot.Status == SyncAuthorityOperationStatus.Validating,
            "the crashed operation retained its exact post-recovery validating state: " +
            crashedSnapshot.Status);
        Assert(crashedSnapshot.Allocations.Single(item =>
                   item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status ==
               SyncAuthorityAllocationStatus.Verified,
            "the retained recovery allocation is already exactly verified");
        var resumed = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(resumed.Status == SyncAuthorityOperationStatus.Completed,
            "the restart completed the replacement from the verified recovery: " + resumed.Reason);
        Assert(site.Right.RecoveryCopyRequests == 1,
            "the restart never retained the predecessor a second time");
        Assert(site.Right.Uploads.Count == 1,
            "the restart issued exactly one replacement request");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(successor),
            "the restarted replacement committed the exact staged successor");
        Assert(site.Right.RecoveryCopies.Values.Single().Payload.SequenceEqual(predecessor),
            "the single retained recovery copy still holds the exact predecessor bytes");
        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed &&
               completed.Attempts.Count == 2 &&
               completed.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess),
            "the restarted operation completed with exactly its two verified attempts");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Count(item => string.Equals(item.ObjectId, completed.ObjectId,
            StringComparison.Ordinal)) == 1,
            "the restarted replacement committed exactly one synchronization point");
    }

    private static async Task UpdateResponseLostAsync()
    {
        await using var site = await Site.CreateAsync("case-j", "j", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(1664);
        var successor = Pattern(1792, 131);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        site.Right.ApplyThenFailOnUpload = true;
        var unknown = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && unknown.EffectDispatched,
            "a replacement applied with a lost response is an unknown outcome: " + unknown.Reason);
        Assert(site.Right.Uploads.Count == 1,
            "exactly one replacement request reached the target before the lost response");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(successor),
            "the applied but unreceipted successor is the target's actual content");
        await ExpectAnyRefusalAsync(() => site.UpdateExecutor().ExecuteAsync(operation.OperationId,
            site.Worker), "re-executing an operation with an unresolved replacement");
        var reconciliation = await site.UpdateExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(reconciliation.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "lost replacement reconciliation retains verification-required uncertainty, never an ordinary retryable failure");
        Assert(site.Right.Uploads.Count == 1,
            "reconciliation issued no second replacement request");
        var afterReconcile = await site.SnapshotAsync(operation.OperationId);
        Assert(afterReconcile.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               afterReconcile.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown),
            "the original unresolved operation and replacement attempt remain intact; matching target bytes do not authorize a commit");
        Assert(afterReconcile.Attempts.Count == 2,
            "reconciliation dispatched no new replacement attempt");
        var recoveryAllocation = afterReconcile.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified,
            "the verified predecessor recovery is retained through the unknown replacement");
    }

    private static async Task KnownReceiptCrashAsync()
    {
        await using var site = await Site.CreateAsync("case-k", "k", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(1920);
        var successor = Pattern(2048, 137);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var crashed = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-receipt-before-commit" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the known-success receipt");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.UpdateExecutor().ExecuteAsync(operation.OperationId,
                site.Worker), "a crash after the known-success replacement receipt");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        Assert(crashed && site.Right.Uploads.Count == 1,
            "the scripted pre-commit crash was reached after exactly one replacement request");
        var crashedSnapshot = await site.SnapshotAsync(operation.OperationId);
        Assert(crashedSnapshot.Status is SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved,
            "the crashed operation retains an unresolved journal state: " + crashedSnapshot.Status);
        var reconciled = await site.UpdateExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.Completed,
            "the retained receipt plus the exact readback completed the commit and cleanup: " +
            reconciled.Reason);
        Assert(site.Right.Uploads.Count == 1,
            "reconciliation uploaded nothing for the retained receipt");
        Assert(site.Right.RecoveryCopyRequests == 1,
            "reconciliation retained no second predecessor recovery");
        var final = await site.SnapshotAsync(operation.OperationId);
        Assert(final.Status == SyncAuthorityOperationStatus.Completed &&
               final.Attempts.Count(item =>
                   item.EffectKind.EndsWith("-conditional-update", StringComparison.Ordinal)) == 1,
            "reconciliation committed the one retained replacement attempt exactly once");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Count(item => string.Equals(item.ObjectId, final.ObjectId,
            StringComparison.Ordinal)) == 1,
            "reconciliation committed exactly one synchronization point");
        var transaction = final.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        Assert(!Directory.Exists(IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName)),
            "reconciliation cleaned the owned private stage exactly once");
    }

    private static async Task CommittedCrashAsync()
    {
        await using var site = await Site.CreateAsync("case-l", "l", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(2176);
        var successor = Pattern(2304, 139);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var crashed = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-commit-before-cleanup" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the commit boundary");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.UpdateExecutor().ExecuteAsync(operation.OperationId,
                site.Worker), "a crash after the commit boundary");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        Assert(crashed && site.Right.Uploads.Count == 1,
            "the committed crash was reached after exactly one replacement request");
        var committed = await site.SnapshotAsync(operation.OperationId);
        Assert(committed.Status == SyncAuthorityOperationStatus.Committed,
            "the crashed operation retains the committed boundary: " + committed.Status);
        var transaction = committed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
        Assert(Directory.Exists(transactionPath),
            "the committed pre-cleanup crash retained its exact owned private transaction");
        var resume = await site.UpdateExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(resume.Status == SyncAuthorityOperationStatus.Completed,
            "the committed crash resumed only its exact owned cleanup: " + resume.Reason);
        Assert(site.Right.Uploads.Count == 1,
            "the committed crash resume never re-uploaded the replacement");
        Assert(site.Right.RecoveryCopyRequests == 1,
            "the committed crash resume retained no second predecessor recovery");
        Assert(!Directory.Exists(transactionPath),
            "the committed crash resume removed its exact owned private transaction");
        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed,
            "the resumed committed update reached completion");

        // A replaced stage object after a commit is refused by exact identity rather than force-removed.
        await using var foreign = await Site.CreateAsync("case-l-foreign", "lforeign", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var foreignOperation = await PlannedUpdateAsync(foreign, SyncAuthorityCloudSides.LogicalLeft,
            Pattern(2432, 149), Pattern(2560, 151));
        var staged = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-commit-before-cleanup" && !staged)
            {
                staged = true;
                throw new InvalidOperationException("scripted commit crash for the replaced-stage case");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => foreign.UpdateExecutor().ExecuteAsync(foreignOperation.OperationId,
                foreign.Worker), "a commit-boundary crash before the replaced-stage case");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        var foreignCommitted = await foreign.SnapshotAsync(foreignOperation.OperationId);
        var foreignStage = foreignCommitted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var foreignTransaction = foreignCommitted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var foreignStagePath = IOPath.Combine(foreign.ControlRoot.Path,
            foreignTransaction.DeterministicName, foreignStage.DeterministicName);
        Assert(File.Exists(foreignStagePath), "the commit crash retained the owned private stage");
        File.Delete(foreignStagePath);
        await File.WriteAllBytesAsync(foreignStagePath, Pattern(16, 3));
        var refusedStage = false;
        try
        {
            await foreign.UpdateExecutor().ReconcileAsync(foreignOperation.OperationId, foreign.Worker);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            SyncAuthorityCloudContentStageException)
        {
            refusedStage = true;
        }
        Assert(refusedStage, "a replaced foreign stage object was refused rather than removed");
        Assert(File.Exists(foreignStagePath),
            "the replaced foreign stage object was preserved exactly as it was found");
        Assert((await foreign.SnapshotAsync(foreignOperation.OperationId)).Status !=
               SyncAuthorityOperationStatus.Completed,
            "the replaced-stage case did not claim completion");
    }

    private static async Task BoundDescriptorEvidenceAsync()
    {
        await using var site = await Site.CreateAsync("case-m", "m", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(2688);
        var successor = Pattern(2816, 157);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var update = await site.UpdateExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(update.Status == SyncAuthorityOperationStatus.Completed,
            "the descriptor evidence case completed: " + update.Reason);
        var identityJson = await ScalarAsync(site.Db.DatabasePath,
            "SELECT identity_json FROM recovery_artifact WHERE operation_id='" + operation.OperationId +
            "';");
        Assert(identityJson is not null,
            "the completed replacement retained exactly one recovery artifact identity");
        Assert(SyncAuthorityCloudRecoveryDescriptor.TryParse(identityJson!, out var descriptor) &&
               descriptor is not null,
            "the retained recovery identity is a typed versioned cloud recovery descriptor");
        var recoveryAllocation = (await site.SnapshotAsync(operation.OperationId)).Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        var retainedCopy = site.Right.RecoveryCopies.Values.Single();
        Assert(descriptor!.Provider == SyncProviders.GoogleDrive &&
               descriptor.ProviderAccountId == site.Right.ProviderAccountId,
            "the descriptor carries the actual storage provider and provider account of the recovery root");
        Assert(descriptor.LogicalSide == SyncAuthorityCloudSides.LogicalRight &&
               descriptor.EndpointRootBindingId == site.RightRoot,
            "the descriptor carries the logical endpoint position separately from the storage authority");
        Assert(descriptor.RecoveryRootBindingId == site.RightRecoveryRoot &&
               descriptor.RecoveryFolderId == site.RightRecoveryFolderId &&
               descriptor.RecoveryNamespaceId == site.Right.NamespaceId &&
               descriptor.RecoveryCorpusId == site.RightRecoveryCorpus,
            "the descriptor binds the exact retained recovery root, folder, namespace and ACTUAL corpus");
        Assert(descriptor.NativeAlgorithm == SyncContentIdentity.Sha256,
            "the descriptor records the target provider's own registered native algorithm");
        Assert(descriptor.RetainedFileId == retainedCopy.Result.FileId &&
               descriptor.RetainedRevision == retainedCopy.Result.Revision &&
               descriptor.ContentHash == retainedCopy.Result.ContentHash &&
               descriptor.Size == predecessor.Length,
            "the descriptor binds the exact retained recovery file identity, revision, hash and size");
        Assert(descriptor.RetainedPath == retainedCopy.Result.RecoveryPath &&
               descriptor.OriginalPath == Path,
            "the descriptor binds the retained recovery path and the original replaced path");
        Assert(recoveryAllocation.BoundIdentity == descriptor.RetainedFileId,
            "the recovery allocation is bound to the exact object the descriptor describes");
        Assert(site.Right.RecoveryCopies.Values.Single().Payload.SequenceEqual(predecessor),
            "the bound recovery root holds the predecessor bytes exactly");
        Assert(site.Right.PayloadOf(Path).SequenceEqual(successor),
            "the target holds the exact staged successor bytes");
        Assert(site.Left.PayloadOf(Path).SequenceEqual(successor) && site.Left.Uploads.Count == 0,
            "the logical source still holds exactly its own staged bytes and received no replacement");
        var sourceVersionId = operation.Members.Single(item => item.Role == "source").ObjectVersionId!;
        var predecessorVersionId = operation.Members.Single(item => item.Role == "destination").ObjectVersionId!;
        var retainedPredecessor = await site.Db.LoadObjectVersionAsync(predecessorVersionId);
        Assert(retainedPredecessor.Side == SyncAuthorityCloudSides.LogicalRight &&
               string.Equals(retainedPredecessor.ContentHash,
                   SyncContentIdentity.Compute(site.Right.Provider, predecessor), StringComparison.OrdinalIgnoreCase),
            "the replaced predecessor version is retained unchanged on its own logical side");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        var point = points.Single(item => string.Equals(item.ObjectId, operation.ObjectId,
            StringComparison.Ordinal));
        Assert(point.LocalObjectVersionId == sourceVersionId &&
               point.DropboxObjectVersionId is not null &&
               point.DropboxObjectVersionId != predecessorVersionId,
            "the ordered synchronization point keeps the source slot and moves only the target slot");
        // The next observation of the replaced target must reproduce the exact committed successor version:
        // the committed version identity is the one the observation layer derives, so a real observation
        // generation still completes over the replaced object rather than colliding with its own authority.
        await site.ObserveStoreAsync(SyncAuthorityCloudSides.LogicalRight, "monitor");
        var observedAfter = await site.LatestObservationAsync(SyncAuthorityCloudSides.LogicalRight, Path);
        Assert(observedAfter is not null &&
               string.Equals(observedAfter.ObjectVersionId, point.DropboxObjectVersionId, StringComparison.Ordinal),
            "the next observation of the replaced target reproduces the exact committed successor version");
        Assert(observedAfter!.MembershipId is not null &&
               string.Equals(observedAfter.ContentHash,
                   SyncContentIdentity.Compute(site.Right.Provider, successor),
                   StringComparison.OrdinalIgnoreCase),
            "the observed successor carries the exact staged native content identity");
        var artifacts = await ScalarAsync(site.Db.DatabasePath,
            "SELECT side FROM recovery_artifact WHERE operation_id='" + operation.OperationId + "';");
        Assert(artifacts == "dropbox",
            "the historical physical recovery_artifact side bucket is unchanged and carries no logical side");
        Assert(site.Right.LegacyRecoveryCalls == 0 && site.Right.RefusedEffects == 0 &&
               site.Left.RefusedEffects == 0,
            "only the bound recovery-root overload and the intended provider effects were ever reached");
    }

    // ----- expectation helpers ------------------------------------------------------------------------

    private static async Task ExpectRefusalAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (SyncAuthorityCloudExecutionException error)
        {
            Assert(true, label + " refused: " + error.Message);
            return;
        }
        Assert(false, label + " must refuse and did not");
    }

    private static async Task ExpectAnyRefusalAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            InvalidOperationException or NotSupportedException or ArgumentException)
        {
            Assert(true, label + " refused: " + error.GetType().Name + ": " + error.Message);
            return;
        }
        Assert(false, label + " must refuse and did not");
    }

    private static async Task ExpectCancelledAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
            Assert(true, label + " was cancelled without a target request");
            return;
        }
        Assert(false, label + " must be cancelled and was not");
    }

    private static async Task<string?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteSqlAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
