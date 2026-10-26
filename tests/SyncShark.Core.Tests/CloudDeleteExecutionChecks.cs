using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using IOPath = System.IO.Path;

// CC3D focused proofs: the one internal shared cloud file deletion engine over real isolated SQLite authority,
// the real reconciliation planner and its tombstone/grace evidence, the real typed CC2C cloud endpoint ports,
// a real CC2E-scoped bound recovery root and the real CC4B runner route.
//
// The planner, the operation journal, the allocation rows, the tombstone and its complete-observation grace,
// the attempt and receipt records, the recovery allocation and its bound recovery authority, the deletion
// commit with its exact unresolved-tombstone requirement, the recovery descriptor and the operation
// completion transitions are all the shipped production code. Only the two provider transports are scripted
// adapters: they supply a real inventory, real bytes and a real bound recovery root, and they perform a real
// identity- and revision-conditioned removal inside their own in-memory inventory, so the engine's own
// decisions - validation, refusal, recovery-before-delete, conditional dispatch, readback, commit,
// reconcile, completion and the folder refusal - are the decisions under proof. No fixture replaces the
// decision being proved, and nothing here reaches a live account, credential, network, installed database,
// product state, Host wiring or NTFS pair root.

internal static class CloudDeleteExecutionChecks
{
    private const string InstallationId = "installation-cloud-delete-execution-317";
    private const string SourceRevision = "cloud-delete-execution-317";
    private const string ProfileId = "profile-cloud-delete-execution-317";
    private const string Path = "reports/quarterly.txt";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static int _unexpectedEffects;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud delete execution fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        _unexpectedEffects = 0;

        await CaseAsync("A-ordered-delete-matrix",
            "Each of the four ordered cloud combinations plans and executes one recoverable deletion of the correct logical target: the exact predecessor is retained in that target's own bound recovery root, the target identity is gone, the vanished side and the peer are untouched",
            OrderedMatrixAsync);
        await CaseAsync("B-folder-refusal",
            "A cloud folder deletion is refused before any provider request with its exact capability reason, and the refusal is never a completion",
            FolderRefusalAsync);
        await CaseAsync("C-tombstone-grace-and-deletion-authority",
            "The real planner's tombstone grace, the deletion capability gate and the retained tombstone authority decide whether a deletion operation exists at all",
            TombstoneAndAuthorityAsync);
        await CaseAsync("D-plan-authority-refusals",
            "Foreign pair, missing or foreign recovery authority, drifted or replaced predecessor, wrong retained kind and a missing retained deletion authority all admit zero delete requests",
            PlanAuthorityRefusalsAsync);
        await CaseAsync("E-recovery-refusals",
            "An inexact retained recovery, an unreadable recovery root, a lost recovery response and a cancelled recovery all admit zero delete requests and preserve the evidence",
            RecoveryRefusalsAsync);
        await CaseAsync("F-lost-delete-response",
            "A deletion applied or refused with a lost response is unknown, is never retried blindly and is never committed from absence alone",
            LostDeleteResponseAsync);
        await CaseAsync("G-known-success-and-committed-restart",
            "A retained known-success deletion receipt resumes only the durable commit, a committed boundary resumes only its own completion, and neither reissues a deletion",
            KnownSuccessAndRestartAsync);
        await CaseAsync("H-runner-routing",
            "The real cloud runner routes a deletion through the common direction fence to the one deletion engine, projects the committed result and keeps the other cloud engines intact",
            RunnerRoutingAsync);
        await CaseAsync("I-authority-evidence-and-no-replay",
            "The retained deletion evidence binds the actual recovery storage, logical side, corpus and exact predecessor, the peer stays byte-exact and a completed path never reissues anything",
            AuthorityEvidenceAsync);

        await CaseAsync("J-request-sent-boundary",
            "A crash at the deletion request-sent boundary unlocks no not-applied inference: no synthetic receipt, no retry and no false completion, even when the already-sent request applies later",
            RequestSentBoundaryAsync);
        await CaseAsync("K-delete-post-recovery-restart",
            "The real runner resumes the deletion from its own validating post-recovery state without a second recovery copy or a second deletion, and the direction fence still yields zero new target effect",
            DeletePostRecoveryRestartAsync);
        await CaseAsync("L-update-post-recovery-restart",
            "The real runner resumes the update engine from the same validating post-recovery state without a second recovery copy or a second replacement, and the direction fence still yields zero new replacement",
            UpdatePostRecoveryRestartAsync);
        await CaseAsync("M-unexpected-private-object",
            "A deletion retains its authorized private transaction allocation and creates and deletes no private object, and an unexpected object at that exact path is refused and left untouched",
            UnexpectedPrivateObjectAsync);

        Assert(_unexpectedEffects == 0,
            "no cloud deletion fixture ever reached a provider effect it was not given");
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudDeleteExecutionChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "cloud-delete-execution-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud delete execution: " + _assertions + " assertions; the real planner," +
            " tombstone grace and evidence, journal, allocation, attempt, receipt, recovery-artifact, deletion" +
            " commit and completion transitions over real isolated SQLite with a real CC2E-scoped recovery root," +
            " for the four ordered cloud combinations and both logical targets, the retained-predecessor" +
            " recovery, the folder capability refusal and every required refusal, unknown, restart, routing and" +
            " no-replay path.");
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
        if (!condition) throw new InvalidDataException("CC3D assertion failed: " + label);
    }

    private static byte[] Pattern(int length, byte seed = 31)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * seed + 7) % 251);
        return payload;
    }

    private static string OtherSide(string side) =>
        side == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityCloudSides.LogicalRight
            : SyncAuthorityCloudSides.LogicalLeft;

    private static SyncAuthorityOperationKind KindsOfDeletion(string vanishedSide) =>
        vanishedSide == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityOperationKind.DeleteRemote
            : SyncAuthorityOperationKind.DeleteLocal;

    // ----- the real harness -------------------------------------------------------------------------

    private sealed class Site : IAsyncDisposable
    {
        internal string CaseName = "";
        internal string Slug = "";
        internal string CaseRoot = "";
        internal string AnchorRoot = "";
        internal string NamespaceId = "";
        internal SyncAuthorityDatabase Db = null!;
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
        internal DeleteFixtureStore Left = null!;
        internal DeleteFixtureStore Right = null!;
        internal SyncControlNamespaceBinding Control = null!;
        internal SyncAuthorityCloudAllocationConfiguration Allocations = null!;
        private PilotDirectoryLease? _anchor;
        private PilotDirectoryLease? _control;

        // Only one delete-access lease can be held on the private control root at a time, so the fixture's own
        // long-lived lease is opened lazily by the cases that drive an engine directly. A case that routes
        // through the real runner never opens it, so the runner's own per-dispatch lease can be opened and
        // disposed exactly as the production factory does.
        internal PilotDirectoryLease ControlRoot =>
            _control ??= SyncControlNamespace.OpenLocal(Control, _anchor!);
        internal string Worker => "worker-cc3d-" + Slug;

        internal static async Task<Site> CreateAsync(string caseName, string slug, SyncEndpointKind leftKind,
            SyncEndpointKind rightKind)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The shared cloud deletion execution proofs require Windows NTFS identities and protected ACLs.");
            var site = new Site
            {
                CaseName = caseName,
                Slug = slug,
                LeftKind = leftKind,
                RightKind = rightKind,
                NamespaceId = "cloud-delete-execution-" + slug,
                CaseRoot = IOPath.Combine(_root, caseName)
            };
            Directory.CreateDirectory(site.CaseRoot);
            site.AnchorRoot = IOPath.Combine(site.CaseRoot, "anchor");
            Directory.CreateDirectory(site.AnchorRoot);
            site._anchor = PilotDirectoryLease.Open(site.AnchorRoot);
            site.Control = new SyncControlNamespaceBinding(InstallationId, site.NamespaceId,
                SyncControlNamespace.BindLocalForTest(site._anchor, InstallationId, site.NamespaceId));
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
            site.Left = DeleteFixtureStore.Create(leftProvider, leftAccount, leftNamespace, leftFolder);
            site.Right = DeleteFixtureStore.Create(rightProvider, rightAccount, rightNamespace, rightFolder);
            site.Db = new SyncAuthorityDatabase(IOPath.Combine(site.CaseRoot, "syncshark.db"));
            await site.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await site.Db.RegisterProviderAccountAsync(leftAccount, InstallationId, "id:" + leftAccount,
                "ns-root-" + slug + "-left", "ns-home-" + slug + "-left", "CC3D left account", 1,
                cancellationToken: default, provider: leftProvider);
            await site.Db.RegisterProviderAccountAsync(rightAccount, InstallationId, "id:" + rightAccount,
                "ns-root-" + slug + "-right", "ns-home-" + slug + "-right", "CC3D right account", 1,
                cancellationToken: default, provider: rightProvider);
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(leftKind, "conn-" + slug + "-left", leftNamespace, leftFolder, "/left"),
                SyncEndpoint.Cloud(rightKind, "conn-" + slug + "-right", rightNamespace, rightFolder, "/right"),
                SyncMode.TwoWay), "CC3D " + caseName);
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
                leftAccount, leftNamespace, site.LeftRecoveryFolderId,
                site.LeftRecoveryCorpus, site.RecoveryPath(SyncAuthorityCloudSides.LogicalLeft), 1,
                cancellationToken: default, hierarchies: new[]
                {
                    Chain(leftKind, leftAccount, leftNamespace, leftFolder, site.LeftRoot),
                    Chain(leftKind, leftAccount, leftNamespace, site.LeftRecoveryFolderId, site.LeftRecoveryRoot)
                });
            await site.Db.RegisterCloudRecoveryRootAsync(site.RightRecoveryRoot, InstallationId, rightProvider,
                rightAccount, rightNamespace, site.RightRecoveryFolderId,
                site.RightRecoveryCorpus, site.RecoveryPath(SyncAuthorityCloudSides.LogicalRight), 1,
                cancellationToken: default, hierarchies: new[]
                {
                    Chain(rightKind, rightAccount, rightNamespace, rightFolder, site.RightRoot),
                    Chain(rightKind, rightAccount, rightNamespace, site.RightRecoveryFolderId, site.RightRecoveryRoot)
                });
            site.Allocations = new SyncAuthorityCloudAllocationConfiguration(site.Control.NamespaceId,
                new SyncAuthorityCloudTargetAllocation(site.LeftRecoveryRoot, 1L << 30),
                new SyncAuthorityCloudTargetAllocation(site.RightRecoveryRoot, 1L << 30));
            site.Left.ConfigureRecovery(site.RecoveryPath(SyncAuthorityCloudSides.LogicalLeft),
                site.LeftRecoveryFolderId, leftNamespace);
            site.Right.ConfigureRecovery(site.RecoveryPath(SyncAuthorityCloudSides.LogicalRight),
                site.RightRecoveryFolderId, rightNamespace);
            return site;
        }

        internal static string RecoveryFolderId(SyncEndpointKind kind, string suffix) =>
            kind == SyncEndpointKind.Dropbox ? "id:cc3d-rec-" + suffix : "gd-cc3d-rec-" + suffix;

        internal static string EndpointNamespace(SyncEndpointKind kind, string accountId,
            string rootNamespaceId) =>
            kind == SyncEndpointKind.GoogleDrive ? accountId : rootNamespaceId;

        internal static string FolderId(SyncEndpointKind kind, string suffix) =>
            kind == SyncEndpointKind.Dropbox ? "id:cc3d-" + suffix : "gd-cc3d-" + suffix;

        internal string RecoveryPath(string side) =>
            SyncAuthorityCloudRootPersistence.NormalizeDisplayPath("/Recovery/" + Slug + "/" +
                (side == SyncAuthorityCloudSides.LogicalLeft ? "left" : "right"));

        internal SyncAuthorityPlannerOptions Options(SyncMode direction = SyncMode.TwoWay,
            bool deletionEnabled = true, int requiredObservations = 1, TimeSpan? grace = null) =>
            new(1, ProfileId, Control.NamespaceId, Control.NamespaceId,
                EnableDeletionEffects: deletionEnabled,
                DeletionMinimumCompleteObservations: requiredObservations,
                DeletionMinimumInterval: grace ?? TimeSpan.Zero,
                Direction: direction, CloudAllocations: Allocations);

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

        internal string RecoveryFolderFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRecoveryFolderId : RightRecoveryFolderId;

        internal string RootFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRoot : RightRoot;

        internal DeleteFixtureStore StoreFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? Left : Right;

        internal SyncAuthorityCloudEndpointSet Endpoints() => SyncAuthorityCloudEndpointSet.FromPair(Pair,
            Left.Store, Right.Store, LeftRoot, RightRoot, Left.ProviderAccountId, Right.ProviderAccountId);

        internal SyncAuthorityCloudExecutionContext Context() => new(Endpoints(), Control, ControlRoot);

        internal SyncAuthorityCloudDeleteExecutor DeleteExecutor() => new(Db, Context(), SideAuthorities());

        internal SyncAuthorityCloudUpdateExecutor UpdateExecutor() => new(Db, Context(), SideAuthorities());

        internal SyncAuthorityCloudDeleteExecutor DeleteExecutor(
            IReadOnlyList<SyncAuthorityCloudSideAuthority> authorities) => new(Db, Context(), authorities);

        internal SyncAuthorityCloudDeleteExecutor ForeignExecutor(Site other) =>
            new(Db, new SyncAuthorityCloudExecutionContext(other.Endpoints(), Control, ControlRoot),
                SideAuthorities());

        internal SyncAuthorityPreparedCloudPair Prepared(SyncMode direction = SyncMode.TwoWay,
            bool deletionEnabled = true, int requiredObservations = 1) =>
            new(Pair, Endpoints(), Control, SideAuthority(SyncAuthorityCloudSides.LogicalLeft),
                SideAuthority(SyncAuthorityCloudSides.LogicalRight),
                Options(direction, deletionEnabled, requiredObservations), Allocations,
                Array.Empty<SyncAuthorityCloudHierarchySnapshot>());

        /// <summary>The real CC4B runner of this pair, carrying the prepared typed cloud context.</summary>
        internal SyncAuthorityCommandAdmission Admission { get; set; } = null!;

        internal SyncAuthorityPairRunner Runner(SyncMode direction = SyncMode.TwoWay) =>
            new SyncAuthorityPairRunner(Pair, Right.Store, Db, LeftRoot, RightRoot, Options(direction),
                new PilotRunControl(), Admission).AttachCloudContext(new SyncAuthorityCloudRunnerContext(Prepared(direction),
                // The production factory opens one fresh owned control-root lease per dispatch and the shared
                // dispatch disposes exactly that lease, so the fixture supplies the same per-call ownership
                // rather than handing out its own long-lived lease.
                () => SyncControlNamespace.OpenLocal(Control, _anchor!)));

        // A fresh planner result enters coordinated execution, not startup recovery. Recovery must not
        // admit an unissued Ready cloud action merely because it exists in the durable queue.
        internal async Task<SyncRunResult> ExecuteFreshAsync(string operationId, string path,
            SyncMode direction = SyncMode.TwoWay, CancellationToken cancellationToken = default)
        {
            var operation = await SnapshotAsync(operationId);
            Assert(operation.Status == SyncAuthorityOperationStatus.Ready,
                "the fresh runner input is the real planner's retained Ready operation");
            var execution = await Runner(direction).ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(
                Pair.Id, [], [new SyncAuthorityPlannedPairOperation(path, operation)]), cancellationToken);
            return new SyncRunResult(Pair.Id, execution.Decisions);
        }

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
                entry.Kind == PilotEntryKind.Directory
                    ? Entry(store, entry.RelativePath, "directory", entry.Id!, null, 0, null)
                    : Entry(store, entry.RelativePath, "file", entry.Id!, entry.Revision,
                        store.PayloadOf(entry.RelativePath).Length, store.PayloadOf(entry.RelativePath)))
                .ToArray();
            var scan = await Db.BeginScanGenerationAsync(Pair.Id, RootFor(side), side, trigger, "{}");
            if (inputs.Length > 0) await Db.AppendObservationEntriesAsync(scan, inputs);
            await Db.CompleteScanGenerationAsync(scan);
        }

        internal SyncAuthorityObservationInput Entry(DeleteFixtureStore store, string relativePath,
            string objectKind, string fileId, string? revision, long size, byte[]? payload)
        {
            var directory = string.Equals(objectKind, "directory", StringComparison.Ordinal);
            var hash = payload is null || directory ? null : SyncContentIdentity.Compute(store.Provider, payload);
            return new SyncAuthorityObservationInput(relativePath, objectKind,
                store.IdentityFor(fileId, revision), hash, directory ? 0 : size, "{}");
        }

        internal SyncAuthorityReconciliationPlanner Planner(SyncAuthorityPlannerOptions? options = null) =>
            new(Db, options ?? Options(), Admission,
            contentComparison: new SyncAuthorityProviderAwareContentComparison(Left.Provider, Right.Provider,
                new SyncAuthorityCloudCommonDigestResolver(Db, Endpoints())));

        internal async Task<SyncAuthorityPlannerOutcome> PlanBaselineAsync(string path,
            SyncAuthorityPlannerOptions? options = null)
        {
            var outcomes = await Planner(options).PlanAsync(Pair.Id);
            var outcome = outcomes.SingleOrDefault(item =>
                string.Equals(item.RelativePath, path, StringComparison.Ordinal));
            if (outcome is null)
                throw new InvalidDataException("The real planner produced no outcome for " + path +
                    " (outcomes: " + string.Join(", ",
                        outcomes.Select(item => item.Kind + ":" + item.RelativePath)) + ")");
            return outcome;
        }

        /// <summary>The real planner's single operation outcome for one path of the expected kind.</summary>
        internal async Task<SyncAuthorityPlannerOutcome> PlanAsync(string path,
            SyncAuthorityOperationKind expected, SyncAuthorityPlannerOptions? options = null)
        {
            var outcomes = await Planner(options).PlanAsync(Pair.Id);
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

        internal async Task<SyncAuthorityPlannerOutcome> PlanOutcomeAsync(string path,
            SyncAuthorityPlannerOptions? options = null)
        {
            var outcomes = await Planner(options).PlanAsync(Pair.Id);
            return outcomes.SingleOrDefault(item =>
                       string.Equals(item.RelativePath, path, StringComparison.Ordinal)) ??
                   throw new InvalidDataException("The real planner produced no outcome for " + path);
        }

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
            _control?.Dispose();
            _anchor?.Dispose();
            await Db.TransitionCommandRequestAsync(Admission.CommandRequestId, "completed");
            SqliteConnection.ClearAllPools();
        }
    }

    /// <summary>
    /// A scripted cloud adapter over a real in-memory inventory and one real bound recovery root. It computes
    /// its own provider-native digest with the production content-identity seam over exactly the bytes it
    /// receives, enforces the exact stable identity and the retained revision on a removal, and refuses every
    /// effect it was not given - including the legacy unbound recovery overloads, so only the bound recovery
    /// root is used. It is proxied through one combined interface because the real CC2F common-digest resolver
    /// reads a non-SHA-256 revision through the exact range capability.
    /// </summary>
    public interface IDeleteFixtureCloudStore : ISyncRemoteStore, ISyncRemoteRangeStore
    {
    }

    public class DeleteFixtureStore : DispatchProxy
    {
        internal string Provider = "";
        internal string ProviderAccountId = "";
        internal string NamespaceId = "";
        internal string FolderId = "";
        internal List<PilotRemoteEntry> Entries { get; } = [];
        internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
        internal List<(string Path, string? Id, string? Revision)> Deletes { get; } = [];
        internal List<(string Path, byte[] Bytes, string ExpectedRevision)> Uploads { get; } = [];
        internal Dictionary<string, (SyncArchiveResult Result, byte[] Payload)> RecoveryCopies { get; } =
            new(StringComparer.Ordinal);
        internal (string RootPath, string RootFolderId, string RootNamespaceId)? RecoveryBinding;
        internal int RevisionCounter;
        internal int RefusedEffects;
        internal int RecoveryCopyRequests;
        internal int RecoveryReadbacks;
        internal int LegacyRecoveryCalls;
        internal int VerifyCalls;
        internal int ListCalls;
        internal bool CorruptRecoveryReadback;
        internal bool RetainWrongBytesInRecovery;
        internal bool ApplyThenFailOnRecovery;
        internal bool ApplyThenFailOnDelete;
        internal bool RefuseDeleteWithoutApply;
        // The conclusive-non-application path stays available and unweakened: the fixture handler refuses with
        // the adapter own contract when a case opts in, and the default keeps every case exactly as before.
        internal bool RefuseConditionalDelete = false;
        internal bool NullListing;
        internal string? HideIdentityFromListing;
        internal Action<string>? DuringCopy;

        internal int DeleteCalls => Deletes.Count;

        internal static DeleteFixtureStore Create(string provider, string providerAccountId, string namespaceId,
            string folderId)
        {
            var fixture = (DeleteFixtureStore)(object)DispatchProxy.Create<IDeleteFixtureCloudStore,
                DeleteFixtureStore>();
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

        internal void Seed(string relativePath, string fileId, string revision, byte[] payload)
        {
            var hash = SyncContentIdentity.Compute(Provider, payload);
            var existing = Entries.FindIndex(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.File, fileId, revision, payload.Length,
                hash);
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
            Payloads[relativePath] = payload;
        }

        internal void SeedText(string relativePath, string fileId, string revision, string text) =>
            Seed(relativePath, fileId, revision, Encoding.UTF8.GetBytes(text));

        internal void SeedDirectory(string relativePath, string folderId)
        {
            var existing = Entries.FindIndex(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.Directory, folderId, null, 0, null);
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
            Payloads.Remove(relativePath);
        }

        internal void Remove(string relativePath)
        {
            Entries.RemoveAll(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            Payloads.Remove(relativePath);
        }

        internal byte[] PayloadOf(string relativePath) => Payloads.TryGetValue(Normalize(relativePath),
            out var payload)
            ? payload
            : throw new InvalidDataException("This scripted target retains no payload at " + relativePath + ".");

        internal PilotRemoteEntry? Find(string relativePath) => Entries.SingleOrDefault(entry =>
            string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));

        internal bool Holds(string relativePath) => Find(relativePath) is not null;

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
            // A property getter is dispatched like any other interface member, so the capability the deletion
            // engine reads is answered from this scripted adapter's own declared value - the same false value
            // both shipped cloud adapters declare.
            "get_SupportsConditionalDirectoryDelete" => false,
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => List(),
            // DispatchProxy does not run interface defaults. Retain the production exact-lookup decision;
            // the fixture supplies inventory, not a replacement source-identity or target-readback guard.
            nameof(ISyncRemoteStore.ReadEntryAsync) => SyncRemoteExactLookup.RequireEntryAsync(
                (ISyncRemoteStore)(object)this, (string)arguments![0]!, (string)arguments[1]!,
                (CancellationToken)arguments[2]!),
            nameof(ISyncRemoteStore.FindEntryAtPathAsync) => SyncRemoteExactLookup.FindEntryAsync(
                (ISyncRemoteStore)(object)this, (string)arguments![0]!, (CancellationToken)arguments[1]!),
            nameof(ISyncRemoteStore.DownloadAsync) => Download(arguments),
            nameof(ISyncRemoteStore.UploadAsync) => Upload(arguments),
            nameof(ISyncRemoteRangeStore.DownloadRangeAsync) => DownloadRange(arguments),
            nameof(ISyncRemoteStore.DeleteAsync) => Delete(arguments),
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

        private object List()
        {
            ListCalls++;
            if (NullListing) return Task.FromResult<IReadOnlyList<PilotRemoteEntry>>(null!);
            var entries = Entries
                .Where(entry => HideIdentityFromListing is null ||
                                !string.Equals(entry.Id, HideIdentityFromListing, StringComparison.Ordinal))
                .ToArray();
            return Task.FromResult<IReadOnlyList<PilotRemoteEntry>>(entries);
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
                    throw new IOException("This scripted target creates an item only through its create guard.");
            }
            else if (current is null ||
                     !string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new IOException(
                    "The scripted target rejected the conditional replacement because its revision moved after it was read.");
            }
            var revision = "rev-" + (++RevisionCounter);
            var existing = Entries.FindIndex(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            var id = existing >= 0 ? Entries[existing].Id! : "id-file-" + relativePath.Replace('/', '-');
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.File, id, revision, bytes.Length,
                nativeHash);
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
            Payloads[relativePath] = bytes;
            return Task.FromResult(new PilotUploadResult(id, revision, bytes.Length, nativeHash));
        }

        private object Delete(object?[]? arguments)
        {
            var expected = (PilotRemoteEntry)arguments![0]!;
            var relativePath = Normalize(expected.RelativePath);
            // The request is counted as sent before any refusal, exactly like a real adapter's request
            // boundary: a refusal after that boundary is never invisible.
            Deletes.Add((relativePath, expected.Id, expected.Revision));
            if (expected.Kind == PilotEntryKind.Directory)
                throw new NotSupportedException(
                    "This scripted adapter cannot remove a folder atomically only when it is empty.");
            if (RefuseConditionalDelete)
                throw new NotSupportedException(
                    "This scripted target adapter cannot conditionally delete an existing item.");
            var current = Find(relativePath);
            if (current is null || !string.Equals(current.Id, expected.Id, StringComparison.Ordinal))
                throw new IOException(
                    "The scripted target no longer carries the exact reviewed identity at the retained path.");
            if (!string.Equals(current.Revision, expected.Revision, StringComparison.Ordinal))
                throw new IOException(
                    "The scripted target rejected the conditional deletion because its revision moved after it was read.");
            if (RefuseDeleteWithoutApply)
                throw new IOException(
                    "The scripted target refused the deletion before applying it and lost the response.");
            Remove(relativePath);
            if (ApplyThenFailOnDelete)
                throw new IOException("The scripted provider applied the deletion and then lost the response.");
            return Task.CompletedTask;
        }

        /// <summary>
        /// The exact range read the real CC2F resolver uses for a non-SHA-256 revision. It serves bytes from
        /// this store's own retained revision only.
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
            var token = (CancellationToken)arguments![3]!;
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
            DuringCopy?.Invoke(relativePath);
            token.ThrowIfCancellationRequested();
            // A copy that retains other bytes is what an inexact retained recovery looks like to the caller.
            var retained = RetainWrongBytesInRecovery ? Pattern(payload.Length, 7) : payload;
            var revision = "rev-" + (++RevisionCounter);
            var result = new SyncArchiveResult("id-recovery-" + name, revision,
                binding.RootPath + "/" + name, retained.Length, SyncContentIdentity.Compute(Provider, retained));
            RecoveryCopies[name] = (result, retained);
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
            if (!RecoveryCopies.TryGetValue(name, out var retained))
                return Task.FromResult<SyncArchiveResult?>(null);
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
                "This cloud delete execution fixture uses only the bound recovery-root overload: " +
                (method?.Name ?? "unknown"));
        }

        private object Refuse(MethodInfo? method)
        {
            RefusedEffects++;
            _unexpectedEffects++;
            throw new NotSupportedException("This cloud delete execution fixture performs no such provider effect: " +
                (method?.Name ?? "unknown"));
        }
    }

    // ----- the real planner and executor drive ------------------------------------------------------

    /// <summary>
    /// Establishes one real baseline and then the real deletion operation for a legitimate disappearance.
    /// Both logical endpoints are observed holding independently created equivalent content, so the real
    /// planner's own provider-aware comparison resolves the cross-provider common digest through the real
    /// CC2F revision-bound resolver and adopts one synchronization point without issuing any effect. The file
    /// is then legitimately removed from the vanished side and that side is re-observed completely, so the
    /// planner records its tombstone observation under the configured grace and, once the deletion effect is
    /// enabled and the grace is met, plans the deletion of the surviving peer.
    /// </summary>
    private static async Task<SyncAuthorityOperationSnapshot> PlannedDeleteAsync(Site site, string vanishedSide,
        byte[] payload)
    {
        var peerSide = OtherSide(vanishedSide);
        site.StoreFor(vanishedSide).Seed(Path, "id-vanished-file", "rev-vanished-1", payload);
        site.StoreFor(peerSide).Seed(Path, "id-peer-file", "rev-peer-1", payload);
        await site.ObserveStoreAsync(vanishedSide, "initial");
        await site.ObserveStoreAsync(peerSide, "initial");
        var adopted = await site.PlanBaselineAsync(Path);
        Assert(adopted.Kind == SyncAuthorityPlannerOutcomeKind.Adopted && adopted.OperationId is null,
            "the real planner adopted the converged baseline without an effect: " + adopted.Kind + " / " +
            adopted.Reason);
        Assert(site.StoreFor(vanishedSide).Uploads.Count == 0 && site.StoreFor(peerSide).Uploads.Count == 0,
            "adopting the converged baseline issued no provider effect");
        site.StoreFor(vanishedSide).Remove(Path);
        await site.ObserveStoreAsync(vanishedSide, "monitor");
        var deletion = await site.PlanAsync(Path, KindsOfDeletion(vanishedSide));
        return await site.SnapshotAsync(deletion.OperationId!);
    }

    private static async Task OrderedMatrixAsync()
    {
        await DeleteAsync("a-dropbox-left-drive-right-left-vanished", "a1", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive, SyncAuthorityCloudSides.LogicalLeft, 1024);
        await DeleteAsync("a-dropbox-left-drive-right-right-vanished", "a2", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive, SyncAuthorityCloudSides.LogicalRight, 1536);
        await DeleteAsync("a-drive-left-dropbox-right-left-vanished", "a3", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox, SyncAuthorityCloudSides.LogicalLeft, 2048);
        await DeleteAsync("a-drive-left-dropbox-right-right-vanished", "a4", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox, SyncAuthorityCloudSides.LogicalRight, 2560);
    }

    /// <summary>
    /// One complete recoverable file deletion of one logical target: the exact predecessor is retained in that
    /// target's own bound recovery root, the deleted target identity is gone, the vanished side and the
    /// retained journal are exactly as the existing deletion commit leaves them.
    /// </summary>
    private static async Task DeleteAsync(string caseName, string slug, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind, string vanishedSide, int size)
    {
        await using var site = await Site.CreateAsync(caseName, slug, leftKind, rightKind);
        var payload = Pattern(size);
        var operation = await PlannedDeleteAsync(site, vanishedSide, payload);
        var source = operation.Members.Single(item => item.Role == "source");
        var targetSide = SyncAuthorityOperationAllocations.TargetSide(operation.Kind);
        Assert(targetSide == OtherSide(vanishedSide),
            "the planned deletion retires the surviving peer's logical side for " + slug);
        Assert(source.RootBindingId == site.RootFor(targetSide),
            "the retained deletion source member binds the deleted logical target's own authority root for " + slug);
        Assert(operation.Members.Single(item => item.Role == "destination").ObjectVersionId is null,
            "the retained deletion destination is the absent peer for " + slug);
        var target = site.StoreFor(targetSide);
        var vanished = site.StoreFor(vanishedSide);
        var deletion = await site.DeleteExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(deletion.Status == SyncAuthorityOperationStatus.Completed,
            "the real cloud deletion completed for " + slug + ": " + deletion.Reason);
        Assert(target.DeleteCalls == 1,
            "exactly one revision-conditioned deletion reached the deleted target adapter for " + slug);
        Assert(target.Deletes[0].Id == "id-peer-file" && target.Deletes[0].Revision == "rev-peer-1",
            "the deletion carried the exact retained predecessor identity and revision for " + slug);
        Assert(!target.Holds(Path), "the exact target object is gone from the deleted side for " + slug);
        Assert(target.Uploads.Count == 0,
            "the deleted target received no create or replacement request for " + slug);
        Assert(target.RecoveryCopyRequests == 1 && target.RecoveryCopies.Count == 1,
            "exactly one predecessor recovery copy was retained for " + slug);
        Assert(target.RecoveryCopies.Values.Single().Payload.SequenceEqual(payload),
            "the retained recovery copy holds the exact retired predecessor bytes for " + slug);
        Assert(vanished.DeleteCalls == 0 && vanished.RecoveryCopyRequests == 0 &&
               vanished.RecoveryCopies.Count == 0 && vanished.Uploads.Count == 0,
            "the vanished side received no deletion, recovery or replacement request for " + slug);
        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed,
            "the journal records the cloud deletion as completed for " + slug);
        Assert(completed.Attempts.Count == 2 &&
               completed.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess),
            "both the recovery and the deletion attempt are terminally verified success for " + slug);
        Assert(completed.Attempts.Count(item =>
                   string.Equals(item.EffectKind, SyncAuthorityCloudRecovery.RecoveryEffectKind,
                       StringComparison.Ordinal)) == 1,
            "the recovery copy journals its own separate attempt for " + slug);
        Assert(completed.Attempts.Single(item => item.EffectKind.EndsWith("-conditional-delete",
                   StringComparison.Ordinal)).EffectKind == target.Provider + "-conditional-delete",
            "the deletion attempt journals the deleted provider's own effect kind for " + slug);
        var transaction = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var recoveryAllocation = completed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(recoveryAllocation.Status == SyncAuthorityAllocationStatus.Verified &&
               recoveryAllocation.IntendedParentIdentity == site.RecoveryRootFor(targetSide),
            "the recovery allocation stays verified against the deleted side's own recovery authority for " + slug);
        Assert(transaction.Status == SyncAuthorityAllocationStatus.Authorized &&
               !Directory.Exists(IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName)) &&
               !File.Exists(IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName)),
            "a file deletion creates no private object, so its private transaction stays fresh and Authorized for " +
            slug);
        Assert(site.Pair.LocalRoot.Length == 0 && site.Pair.LocalVolumeSerialNumber == 0 &&
               site.Pair.LocalRootFileId == 0,
            "no selected NTFS root, volume serial or root file id is projected by the cloud pair for " + slug);
        var tombstone = await ScalarAsync(site.Db.DatabasePath,
            "SELECT status FROM tombstone WHERE pair_id='" + site.Pair.Id + "' AND object_id='" +
            completed.ObjectId + "';");
        Assert(tombstone == "resolved",
            "the existing deletion commit resolved exactly one retained tombstone for " + slug + ": " + tombstone);
        var committed = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM history_event WHERE operation_id='" + operation.OperationId +
            "' AND event_kind='deletion_committed';");
        Assert(committed == "1",
            "the deletion commit left exactly one durable deletion_committed history event for " + slug);
        var artifacts = await ScalarAsync(site.Db.DatabasePath,
            "SELECT side FROM recovery_artifact WHERE operation_id='" + operation.OperationId + "';");
        Assert(artifacts == "dropbox",
            "the historical physical recovery_artifact side bucket is unchanged for " + slug);
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Count(item => string.Equals(item.ObjectId, completed.ObjectId,
                   StringComparison.Ordinal)) == 1,
            "the deletion reuses the one retained synchronization point instead of fabricating a new one for " +
            slug);
        Assert(target.LegacyRecoveryCalls == 0 && target.RefusedEffects == 0 && vanished.RefusedEffects == 0,
            "only the bound recovery-root overload and the intended deletion request were reached for " + slug);
    }

    private static async Task FolderRefusalAsync()
    {
        foreach (var (leftKind, rightKind, slug) in new[]
                 {
                     (SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive, "b1"),
                     (SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox, "b2")
                 })
        {
            await using var site = await Site.CreateAsync("b-folder-" + slug, slug, leftKind, rightKind);
            const string folder = "reports/folder";
            var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
            var targetSide = OtherSide(vanishedSide);
            var target = site.StoreFor(targetSide);
            site.StoreFor(vanishedSide).SeedDirectory(folder, "id-vanished-folder");
            target.SeedDirectory(folder, "id-peer-folder");
            await site.ObserveStoreAsync(vanishedSide, "initial");
            await site.ObserveStoreAsync(targetSide, "initial");
            var adopted = await site.PlanBaselineAsync(folder);
            Assert(adopted.Kind == SyncAuthorityPlannerOutcomeKind.Adopted && adopted.OperationId is null,
                "the real planner adopted the converged folder baseline for " + slug + ": " + adopted.Reason);
            site.StoreFor(vanishedSide).Remove(folder);
            await site.ObserveStoreAsync(vanishedSide, "monitor");
            var planned = await site.PlanAsync(folder, KindsOfDeletion(vanishedSide));
            var operation = await site.SnapshotAsync(planned.OperationId!);
            Assert(operation.Allocations.Count == 0,
                "a cloud folder deletion reserves no file allocation for " + slug);
            Assert(!target.Store.SupportsConditionalDirectoryDelete,
                "the deleted side's typed adapter declares no atomic empty-folder guard for " + slug);
            var refused = await site.DeleteExecutor().ExecuteAsync(operation.OperationId, site.Worker);
            Assert(!refused.EffectDispatched && refused.Status == operation.Status,
                "the folder deletion refusal preserves the real retained status and dispatches no effect for " +
                slug + ": " + refused.Reason);
            Assert(refused.Reason.Contains("SupportsConditionalDirectoryDelete=false", StringComparison.Ordinal),
                "the refusal names the real retained capability reason for " + slug);
            Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0 && target.Uploads.Count == 0,
                "no provider request was issued for the refused folder deletion for " + slug);
            Assert(target.Holds(folder),
                "the folder remains on the deleted side after the refusal for " + slug);
            var after = await site.SnapshotAsync(operation.OperationId);
            Assert(after.Status == operation.Status && after.Attempts.Count == 0,
                "the refused folder deletion records no attempt and keeps its retained status for " + slug);
            Assert((await site.Db.LoadRecoveryArtifactsAsync()).Count == 0,
                "no fictitious folder recovery artifact is registered for " + slug);
            foreach (var query in new[]
                     {
                         "SELECT count(*) FROM effect_receipt;",
                         "SELECT count(*) FROM history_event WHERE event_kind='deletion_committed';",
                         "SELECT count(*) FROM lease WHERE status='active';"
                     })
                Assert(await ScalarAsync(site.Db.DatabasePath, query) == "0",
                    "the folder refusal leaves zero receipt, deletion commit or active lease for " + slug + ": " +
                    query);
            var reconciled = await site.DeleteExecutor().ReconcileAsync(operation.OperationId, site.Worker);
            Assert(reconciled.Status == after.Status && !reconciled.EffectDispatched && target.DeleteCalls == 0,
                "reconciling the refused folder deletion cannot invent a deletion for " + slug);
            var decision = RunnerDecision(folder, operation.Kind, refused);
            Assert(decision.Kind == SyncDecisionKind.Blocked && decision.Reason == refused.Reason,
                "the real runner projects the folder refusal as Blocked, never as response unknown, for " + slug);
        }
    }

    private static async Task TombstoneAndAuthorityAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var peerSide = OtherSide(vanishedSide);
        await using (var gatedSite = await Site.CreateAsync("c-gate", "cg", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(512);
            gatedSite.StoreFor(vanishedSide).Seed(Path, "id-vanished-file", "rev-vanished-1", payload);
            gatedSite.StoreFor(peerSide).Seed(Path, "id-peer-file", "rev-peer-1", payload);
            await gatedSite.ObserveStoreAsync(vanishedSide, "initial");
            await gatedSite.ObserveStoreAsync(peerSide, "initial");
            await gatedSite.PlanBaselineAsync(Path);
            gatedSite.StoreFor(vanishedSide).Remove(Path);
            await gatedSite.ObserveStoreAsync(vanishedSide, "monitor");
            // 1. The deletion capability gate: with deletion effects disabled the planner records the tombstone
            //    and plans no operation, so no deletion engine is ever handed the path.
            var capabilityGated = await gatedSite.PlanOutcomeAsync(Path,
                gatedSite.Options(deletionEnabled: false));
            Assert(capabilityGated.Kind == SyncAuthorityPlannerOutcomeKind.Tombstone &&
                   capabilityGated.OperationId is null && capabilityGated.TombstoneId is not null,
                "with deletion capability gated the planner records the tombstone and plans no effect: " +
                capabilityGated.Kind + " / " + capabilityGated.Reason);
            // 2. The grace requirement: the same absence generation with two required complete observations
            //    stays a tombstone after the first one.
            var pending = await gatedSite.PlanOutcomeAsync(Path,
                gatedSite.Options(requiredObservations: 2));
            Assert(pending.Kind == SyncAuthorityPlannerOutcomeKind.Tombstone && pending.OperationId is null &&
                   pending.Reason.Contains("Deletion confirmation is pending", StringComparison.Ordinal),
                "the first complete absence observation does not satisfy a two-observation grace: " + pending.Reason);
            Assert(pending.TombstoneId == capabilityGated.TombstoneId,
                "both observations belong to the one retained tombstone generation");
            Assert(gatedSite.StoreFor(peerSide).DeleteCalls == 0,
                "the unsatisfied grace issued no deletion request");
            // 3. A second complete absence observation satisfies the grace, and only then does the real planner
            //    plan the deletion of the surviving peer.
            await gatedSite.ObserveStoreAsync(vanishedSide, "monitor");
            var planned = await gatedSite.PlanAsync(Path, KindsOfDeletion(vanishedSide),
                gatedSite.Options(requiredObservations: 2));
            var operation = await gatedSite.SnapshotAsync(planned.OperationId!);
            Assert(gatedSite.StoreFor(peerSide).DeleteCalls == 0 && operation.Status ==
                   SyncAuthorityOperationStatus.Ready,
                "planning the deletion after the satisfied grace issued no provider request");
            using (var document = JsonDocument.Parse(operation.PreconditionsJson))
                Assert(document.RootElement.TryGetProperty("tombstoneId", out var value) &&
                       value.ValueKind == JsonValueKind.String && value.GetString() == pending.TombstoneId,
                    "the retained deletion operation carries its exact retained tombstone authority");
            var executed = await gatedSite.DeleteExecutor().ExecuteAsync(operation.OperationId, gatedSite.Worker);
            Assert(executed.Status == SyncAuthorityOperationStatus.Completed &&
                   gatedSite.StoreFor(peerSide).DeleteCalls == 1,
                "the planner-authorised deletion executed exactly once after the satisfied grace: " +
                executed.Reason);
        }
        // 4. A retained deletion operation whose preconditions lost their tombstone authority is refused before
        //    any provider request, and a stale retained kind is refused the same way.
        // The production journal holds a planned operation immutable, and the case own planned delete already
        // reserves this root and path. Both hostile-authority scenarios are therefore built on a SEPARATE
        // fresh fixture where no delete has been planned, inserted through the production CreateOperationAsync
        // with the source member taken from the observation after a plain two-sided scan: one whose retained
        // preconditions carry no tombstone authority and one that names another operation kind.
        await using var hostile = await Site.CreateAsync("c-stale", "cst", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        // A DISTINCT path and object per scenario, so each hostile insertion reserves its own root and path and
        // never collides with the other scenario reservation.
        const string secondPath = "reports/second.txt";
        hostile.StoreFor(vanishedSide).Seed(Path, "id-vanished-1", "rev-vanished-1", Pattern(640));
        hostile.StoreFor(peerSide).Seed(Path, "id-peer-1", "rev-peer-1", Pattern(640));
        hostile.StoreFor(vanishedSide).Seed(secondPath, "id-vanished-2", "rev-vanished-2", Pattern(640));
        hostile.StoreFor(peerSide).Seed(secondPath, "id-peer-2", "rev-peer-2", Pattern(640));
        await hostile.ObserveStoreAsync(vanishedSide, "initial");
        await hostile.ObserveStoreAsync(peerSide, "initial");
        var hostileTarget = hostile.StoreFor(peerSide);
        foreach (var (operationId, path, preconditions, label) in new[]
                 {
                     ("operation-c-no-tombstone", Path,
                         JsonSerializer.Serialize(new
                         {
                             path = Path,
                             kind = SyncAuthorityNames.OperationKind(SyncAuthorityOperationKind.DeleteRemote)
                         }),
                         "a deletion whose retained preconditions carry no tombstone authority"),
                     ("operation-c-foreign-kind", secondPath,
                         JsonSerializer.Serialize(new
                         {
                             path = secondPath,
                             kind = SyncAuthorityNames.OperationKind(SyncAuthorityOperationKind.CreateRemote),
                             tombstoneId = "tombstone-cc3d-foreign"
                         }),
                         "a deletion whose retained preconditions name another operation kind")
                 })
        {
            var observed = await hostile.LatestObservationAsync(peerSide, path) ??
                throw new InvalidDataException("the hostile-authority fixture observed no source member");
            await hostile.Db.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
                "idempotency-" + operationId, SyncAuthorityOperationKind.DeleteRemote, hostile.Pair.Id,
                observed!.ObjectId, 1, ProfileId, preconditions, 100,
                [
                    new SyncAuthorityMember("member-" + operationId, hostile.Pair.Id, observed.ObjectId,
                        "source", hostile.RootFor(peerSide), observed.MembershipId, observed.ObjectVersionId,
                        null, path, "{}"),
                    new SyncAuthorityMember("member-" + operationId + "-dest", hostile.Pair.Id,
                        observed.ObjectId, "destination", hostile.RootFor(vanishedSide), null, null, null,
                        path, "{}")
                ], [], []), hostile.Admission);
            await ExpectRefusalAsync(() => hostile.DeleteExecutor().ExecuteAsync(operationId, hostile.Worker),
                label);
            Assert(hostileTarget.DeleteCalls == 0 && hostileTarget.RecoveryCopyRequests == 0,
                label + " issued no recovery and no deletion request");
            Assert((await hostile.SnapshotAsync(operationId)).Attempts.Count == 0,
                label + " dispatched no attempt");
        }
    }

    private static async Task PlanAuthorityRefusalsAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);
        await using var site = await Site.CreateAsync("d-authority", "d", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedDeleteAsync(site, vanishedSide, Pattern(768));
        var target = site.StoreFor(targetSide);

        // A typed endpoint set of another pair can never drive this retained operation.
        await using var other = await Site.CreateAsync("d-other", "dother", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox);
        await ExpectRefusalAsync(() => site.ForeignExecutor(other).ExecuteAsync(operation.OperationId,
            site.Worker), "a deletion operation of another pair");
        Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0,
            "the foreign pair issued no recovery and no deletion request");

        // The deleted side's own retained recovery authority must be supplied; a missing one is refused.
        await ExpectRefusalAsync(() => site.DeleteExecutor(new[] { site.SideAuthority(vanishedSide) })
            .ExecuteAsync(operation.OperationId, site.Worker),
            "a deletion without the deleted side's retained recovery authority");
        Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0,
            "the missing recovery authority issued no request");

        // A recovery authority that names the pair root itself is never a recovery boundary.
        var selfRoot = site.SideAuthority(targetSide) with
        {
            RecoveryRootBindingId = site.RootFor(targetSide),
            RecoveryFolderId = target.FolderId
        };
        await ExpectRefusalAsync(() => site.DeleteExecutor(new[] { site.SideAuthority(vanishedSide), selfRoot })
            .ExecuteAsync(operation.OperationId, site.Worker),
            "a recovery authority that names the pair root itself");
        Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0,
            "the pair-root recovery authority issued no request");

        // An unretained corpus, another provider account and another provider are each refused.
        foreach (var (label, authority) in new[]
                 {
                     ("an unretained recovery corpus",
                         site.SideAuthority(targetSide) with { RecoveryCorpusId = "corpus-not-retained" }),
                     ("a recovery authority that names another provider account",
                         site.SideAuthority(targetSide) with { ProviderAccountId = "account-d-foreign" }),
                     ("a recovery authority that names another provider",
                         site.SideAuthority(targetSide) with { Provider = SyncProviders.Dropbox })
                 })
        {
            await ExpectRefusalAsync(() => site.DeleteExecutor(new[]
                { site.SideAuthority(vanishedSide), authority }).ExecuteAsync(operation.OperationId,
                site.Worker), label);
            Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0,
                label + " issued no request");
        }
        var untouched = await site.SnapshotAsync(operation.OperationId);
        Assert(untouched.Attempts.Count == 0 &&
               untouched.Status is SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased,
            "every refused authority left the retained deletion plan with no dispatched attempt: " +
            untouched.Status);
        Assert(untouched.Allocations.All(item => item.Status == SyncAuthorityAllocationStatus.Authorized),
            "no refused authority advanced an allocation or copied a predecessor");

        // A drifted predecessor is refused before any recovery copy or deletion request.
        await using var drift = await Site.CreateAsync("d-drift", "ddrift", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var driftOperation = await PlannedDeleteAsync(drift, vanishedSide, Pattern(896, 11));
        var driftTarget = drift.StoreFor(targetSide);
        driftTarget.Seed(Path, "id-peer-file", "rev-concurrent", Pattern(960, 13));
        await ExpectRefusalAsync(() => drift.DeleteExecutor().ExecuteAsync(driftOperation.OperationId,
            drift.Worker), "a predecessor that drifted before the deletion");
        Assert(driftTarget.DeleteCalls == 0 && driftTarget.RecoveryCopyRequests == 0,
            "the drifted predecessor issued no recovery copy and no deletion request");
        Assert(driftTarget.PayloadOf(Path).SequenceEqual(Pattern(960, 13)),
            "the concurrent target content was left exactly as the concurrent writer made it");

        // A same-path object with a different stable identity is never touched and never deleted.
        await using var replaced = await Site.CreateAsync("d-replaced", "dreplaced", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var replacedOperation = await PlannedDeleteAsync(replaced, vanishedSide, Pattern(1024, 17));
        var replacedTarget = replaced.StoreFor(targetSide);
        replacedTarget.Seed(Path, "id-replacement-file", "rev-replacement-1", Pattern(1088, 19));
        await ExpectRefusalAsync(() => replaced.DeleteExecutor().ExecuteAsync(replacedOperation.OperationId,
            replaced.Worker), "a same-path replacement with a different stable identity");
        Assert(replacedTarget.DeleteCalls == 0 && replacedTarget.RecoveryCopyRequests == 0,
            "the replaced same-path object issued no recovery copy and no deletion request");
        Assert(replacedTarget.Holds(Path) &&
               replacedTarget.PayloadOf(Path).SequenceEqual(Pattern(1088, 19)),
            "the same-path replacement was left exactly untouched");
        Assert((await replaced.SnapshotAsync(replacedOperation.OperationId)).Attempts.Count == 0,
            "the replaced same-path object dispatched no attempt");

        // A non-deletion kind can never be routed through the shared deletion engine.
        await using var kind = await Site.CreateAsync("d-kind", "dkind", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        kind.StoreFor(vanishedSide).Seed(Path, "id-vanished-file", "rev-vanished-1", Pattern(256, 23));
        await kind.ObserveStoreAsync(vanishedSide, "initial");
        await kind.ObserveStoreAsync(targetSide, "initial");
        var create = await kind.PlanAsync(Path, SyncAuthorityOperationKind.CreateRemote);
        await ExpectRefusalAsync(() => kind.DeleteExecutor().ExecuteAsync(create.OperationId!, kind.Worker),
            "a create operation routed through the shared cloud deletion executor");
        Assert(kind.StoreFor(targetSide).DeleteCalls == 0 && kind.StoreFor(targetSide).Uploads.Count == 0,
            "the foreign kind issued no target request");
    }

    private static async Task RecoveryRefusalsAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);

        // A retained recovery whose own bytes are not the exact predecessor admits no deletion.
        await using (var wrong = await Site.CreateAsync("e-wrong-bytes", "ewb", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1152);
            var operation = await PlannedDeleteAsync(wrong, vanishedSide, payload);
            var target = wrong.StoreFor(targetSide);
            target.RetainWrongBytesInRecovery = true;
            var refused = await wrong.DeleteExecutor().ExecuteAsync(operation.OperationId, wrong.Worker);
            Assert(refused.Status == SyncAuthorityOperationStatus.Blocked && target.DeleteCalls == 0,
                "an inexact retained recovery blocks the deletion with zero deletion requests: " + refused.Reason);
            Assert(target.DeleteCalls == 0, "the inexact retained recovery issued no deletion request");
            Assert(target.Holds(Path) && target.PayloadOf(Path).SequenceEqual(payload),
                "the target predecessor is untouched after the inexact recovery refusal");
            var blocked = await wrong.SnapshotAsync(operation.OperationId);
            Assert(blocked.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.Blocked) &&
                   blocked.Attempts.All(item => !item.EffectKind.EndsWith("-conditional-delete",
                       StringComparison.Ordinal)),
                "the inexact retained recovery left its own blocked attempt and no deletion attempt");
            Assert(blocked.Allocations.Single(item =>
                       item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status ==
                   SyncAuthorityAllocationStatus.Creating,
                "the retained recovery allocation stays exactly where the unverified copy left it");
            Assert(target.RecoveryCopies.Count == 1,
                "the applied-but-inexact recovery copy is retained in the bound recovery root as evidence");
        }

        // A recovery readback that no longer describes the retained copy admits no deletion either.
        await using (var corrupt = await Site.CreateAsync("e-corrupt-readback", "ecr",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1216);
            var operation = await PlannedDeleteAsync(corrupt, vanishedSide, payload);
            var target = corrupt.StoreFor(targetSide);
            target.CorruptRecoveryReadback = true;
            var refused = await corrupt.DeleteExecutor().ExecuteAsync(operation.OperationId, corrupt.Worker);
            Assert(refused.Status == SyncAuthorityOperationStatus.Blocked && target.DeleteCalls == 0,
                "a drifting recovery readback blocks the deletion: " + refused.Reason);
            Assert(target.DeleteCalls == 0 && target.Holds(Path),
                "the drifting recovery readback issued no deletion request and left the target untouched");
        }

        // A recovery copy whose response was lost admits zero deletion, now or on a later reconcile.
        await using (var lost = await Site.CreateAsync("e-lost-recovery", "elr", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1280);
            var operation = await PlannedDeleteAsync(lost, vanishedSide, payload);
            var target = lost.StoreFor(targetSide);
            target.ApplyThenFailOnRecovery = true;
            var unknown = await lost.DeleteExecutor().ExecuteAsync(operation.OperationId, lost.Worker);
            Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && unknown.EffectDispatched &&
                   target.DeleteCalls == 0,
                "a lost recovery response is an unknown outcome with zero deletion: " + unknown.Reason);
            Assert(target.Holds(Path) && target.PayloadOf(Path).SequenceEqual(payload),
                "the target predecessor is untouched after the lost recovery response");
            var reconciled = await lost.DeleteExecutor().ReconcileAsync(operation.OperationId, lost.Worker);
            Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                   !reconciled.EffectDispatched && target.DeleteCalls == 0,
                "reconciliation of an unresolved recovery copy issues no deletion: " + reconciled.Reason);
            Assert(target.RecoveryCopyRequests == 1,
                "the unresolved recovery copy is never copied a second time");
            var retained = await lost.SnapshotAsync(operation.OperationId);
            Assert(retained.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                   retained.Allocations.Single(item =>
                       item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status !=
                   SyncAuthorityAllocationStatus.Verified,
                "the unresolved recovery keeps its exact unverified retained state: " + retained.Status);
        }

        // A cancelled recovery copy issues no deletion and retains its allocation and evidence.
        await using (var cancelled = await Site.CreateAsync("e-cancelled", "ecn", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1344);
            var operation = await PlannedDeleteAsync(cancelled, vanishedSide, payload);
            var target = cancelled.StoreFor(targetSide);
            using var cancellation = new CancellationTokenSource();
            target.DuringCopy = _ => cancellation.Cancel();
            await ExpectCancelledAsync(() => cancelled.DeleteExecutor().ExecuteAsync(operation.OperationId,
                cancelled.Worker, cancellation.Token), "a cancelled recovery copy");
            target.DuringCopy = null;
            Assert(target.DeleteCalls == 0 && target.Holds(Path),
                "the cancelled recovery issued no deletion request and left the target untouched");
            var after = await cancelled.SnapshotAsync(operation.OperationId);
            Assert(after.Attempts.All(item => !item.EffectKind.EndsWith("-conditional-delete",
                       StringComparison.Ordinal)),
                "the cancelled recovery dispatched no deletion attempt");
        }
    }

    private static async Task LostDeleteResponseAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);

        // A deletion the adapter applied with a lost response is unknown; absence alone never commits it.
        await using (var applied = await Site.CreateAsync("f-applied-lost", "fal", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1408);
            var operation = await PlannedDeleteAsync(applied, vanishedSide, payload);
            var target = applied.StoreFor(targetSide);
            target.ApplyThenFailOnDelete = true;
            var unknown = await applied.DeleteExecutor().ExecuteAsync(operation.OperationId, applied.Worker);
            Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && unknown.EffectDispatched,
                "a deletion applied with a lost response is an unknown outcome: " + unknown.Reason);
            Assert(target.DeleteCalls == 1 && !target.Holds(Path),
                "the applied deletion reached the target exactly once and its object is gone");
            Assert(target.RecoveryCopyRequests == 1 && target.RecoveryCopies.Count == 1,
                "the predecessor recovery is retained through the unknown deletion");
            await ExpectRefusalAsync(() => applied.DeleteExecutor().ExecuteAsync(operation.OperationId,
                applied.Worker), "re-executing a deletion with an unresolved deletion request");
            var reconciled = await applied.DeleteExecutor().ReconcileAsync(operation.OperationId,
                applied.Worker);
            Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown && !reconciled.EffectDispatched,
                "reconciling a lost deletion response is never complete without a retained receipt: " +
                reconciled.Reason);
            Assert(target.DeleteCalls == 1,
                "reconciliation of the lost deletion response issued no second deletion request");
            var after = await applied.SnapshotAsync(operation.OperationId);
            Assert(after.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                   after.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown),
                "the lost deletion response was never committed from absence alone");
            Assert(after.Attempts.Count == 2, "reconciliation dispatched no new deletion attempt");
            Assert(await ScalarAsync(applied.Db.DatabasePath, "SELECT count(*) FROM tombstone WHERE pair_id='" +
                       applied.Pair.Id + "' AND status='resolved';") == "0",
                "no tombstone was resolved for the unresolved deletion");
        }

        // A partial inventory is never absence or completion; the unknown deletion remains verifiable, not replayable.
        await using (var partial = await Site.CreateAsync("f-partial-inventory", "fpi",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1472);
            var operation = await PlannedDeleteAsync(partial, vanishedSide, payload);
            var target = partial.StoreFor(targetSide);
            target.RefuseDeleteWithoutApply = true;
            var unknown = await partial.DeleteExecutor().ExecuteAsync(operation.OperationId, partial.Worker);
            Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && target.DeleteCalls == 1 &&
                   target.Holds(Path),
                "a deletion refused before application with a lost response is unknown: " + unknown.Reason);
            target.HideIdentityFromListing = "id-peer-file";
            var reconciled = await partial.DeleteExecutor().ReconcileAsync(operation.OperationId,
                partial.Worker);
            Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown && !reconciled.EffectDispatched,
                "a partial inventory never completes a deletion: " + reconciled.Reason);
            Assert(target.DeleteCalls == 1 && target.Holds(Path) &&
                   target.PayloadOf(Path).SequenceEqual(payload),
                "the omitted target identity was neither deleted again nor treated as absent");
            target.HideIdentityFromListing = null;
            var beforeRepeat = await partial.SnapshotAsync(operation.OperationId);
            var repeated = await partial.DeleteExecutor().ReconcileAsync(operation.OperationId, partial.Worker);
            var afterRepeat = await partial.SnapshotAsync(operation.OperationId);
            Assert(repeated.Status == SyncAuthorityOperationStatus.EffectUnknown && !repeated.EffectDispatched &&
                   afterRepeat.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                   afterRepeat.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown) &&
                   afterRepeat.Attempts.Count == beforeRepeat.Attempts.Count &&
                   target.DeleteCalls == 1 && target.Holds(Path),
                "repeated verification preserves the unknown deletion without a blind replay");
        }

        // A complete listing that still shows the exact retained predecessor with a durable unknown receipt
        // stays unresolved rather than being reissued or committed.
        await using (var notApplied = await Site.CreateAsync("f-not-applied", "fna",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1504);
            var operation = await PlannedDeleteAsync(notApplied, vanishedSide, payload);
            var target = notApplied.StoreFor(targetSide);
            target.RefuseDeleteWithoutApply = true;
            await notApplied.DeleteExecutor().ExecuteAsync(operation.OperationId, notApplied.Worker);
            var reconciled = await notApplied.DeleteExecutor().ReconcileAsync(operation.OperationId,
                notApplied.Worker);
            Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown && !reconciled.EffectDispatched &&
                   target.DeleteCalls == 1,
                "an unresolved deletion retains its unknown receipt with zero reissue: " +
                reconciled.Reason);
            Assert(target.Holds(Path) && target.PayloadOf(Path).SequenceEqual(payload),
                "the exact retained predecessor was left untouched by the unresolved reconciliation");
        }

        // A complete listing that cannot be read at all is never absence either.
        await using (var unreadable = await Site.CreateAsync("f-unreadable", "fur", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1536);
            var operation = await PlannedDeleteAsync(unreadable, vanishedSide, payload);
            var target = unreadable.StoreFor(targetSide);
            target.ApplyThenFailOnDelete = true;
            var unknown = await unreadable.DeleteExecutor().ExecuteAsync(operation.OperationId,
                unreadable.Worker);
            Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && target.DeleteCalls == 1,
                "the applied deletion with a lost response is unknown before the unreadable listing");
            target.NullListing = true;
            var reconciled = await unreadable.DeleteExecutor().ReconcileAsync(operation.OperationId,
                unreadable.Worker);
            Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown &&
                   !reconciled.EffectDispatched && target.DeleteCalls == 1,
                "an unreadable inventory never completes a deletion: " + reconciled.Reason);
            target.NullListing = false;
        }
    }

    private static async Task KnownSuccessAndRestartAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);

        // A crash after the retained known-success receipt but before the commit: the receipt plus a fresh
        // complete listing resume only the durable commit, and the deletion is never reissued.
        await using (var crash = await Site.CreateAsync("g-known-success", "gks", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1600);
            var operation = await PlannedDeleteAsync(crash, vanishedSide, payload);
            var target = crash.StoreFor(targetSide);
            var crashed = false;
            SyncAuthorityCloudDeleteExecutor.PhaseForTest = phase =>
            {
                if (phase == "after-receipt-before-readback" && !crashed)
                {
                    crashed = true;
                    throw new InvalidOperationException(
                        "scripted crash after the known-success deletion receipt");
                }
            };
            try
            {
                await ExpectAnyRefusalAsync(() => crash.DeleteExecutor().ExecuteAsync(operation.OperationId,
                    crash.Worker), "a crash after the known-success deletion receipt");
            }
            finally
            {
                SyncAuthorityCloudDeleteExecutor.PhaseForTest = null;
            }
            Assert(crashed && target.DeleteCalls == 1 && target.RecoveryCopyRequests == 1,
                "the scripted pre-commit crash was reached after exactly one deletion and one recovery copy");
            var crashedState = await crash.SnapshotAsync(operation.OperationId);
            Assert(crashedState.Status == SyncAuthorityOperationStatus.EffectObserved,
                "the crashed deletion retains its post-receipt journal state: " + crashedState.Status);
            var reconciled = await crash.DeleteExecutor().ReconcileAsync(operation.OperationId, crash.Worker);
            Assert(reconciled.Status == SyncAuthorityOperationStatus.Completed,
                "the retained receipt plus the exact absence completed the deletion: " + reconciled.Reason);
            Assert(target.DeleteCalls == 1 && target.RecoveryCopyRequests == 1,
                "reconciliation reissued neither the deletion nor the predecessor recovery");
            var final = await crash.SnapshotAsync(operation.OperationId);
            Assert(final.Status == SyncAuthorityOperationStatus.Completed && final.Attempts.Count == 2 &&
                   final.Attempts.All(item => item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess),
                "the reconciled deletion committed its one retained attempt exactly once");
            Assert(await ScalarAsync(crash.Db.DatabasePath, "SELECT status FROM tombstone WHERE pair_id='" +
                       crash.Pair.Id + "' AND object_id='" + final.ObjectId + "';") == "resolved",
                "the resumed deletion commit resolved its retained tombstone");
            var again = await crash.DeleteExecutor().ReconcileAsync(operation.OperationId, crash.Worker);
            Assert(again.Status == SyncAuthorityOperationStatus.Completed && !again.EffectDispatched &&
                   target.DeleteCalls == 1,
                "a completed deletion is a no-op that reissues nothing");
        }

        // A crash at the committed boundary resumes only the owned completion of the deletion.
        await using (var committed = await Site.CreateAsync("g-committed", "gcm", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var payload = Pattern(1664);
            var operation = await PlannedDeleteAsync(committed, vanishedSide, payload);
            var target = committed.StoreFor(targetSide);
            var crashed = false;
            SyncAuthorityCloudDeleteExecutor.PhaseForTest = phase =>
            {
                if (phase == "after-commit-before-completion" && !crashed)
                {
                    crashed = true;
                    throw new InvalidOperationException("scripted crash after the deletion commit boundary");
                }
            };
            try
            {
                await ExpectAnyRefusalAsync(() => committed.DeleteExecutor().ExecuteAsync(
                    operation.OperationId, committed.Worker), "a crash after the deletion commit boundary");
            }
            finally
            {
                SyncAuthorityCloudDeleteExecutor.PhaseForTest = null;
            }
            Assert(crashed && target.DeleteCalls == 1,
                "the committed crash was reached after exactly one deletion request");
            var atBoundary = await committed.SnapshotAsync(operation.OperationId);
            Assert(atBoundary.Status == SyncAuthorityOperationStatus.Committed,
                "the crashed deletion retains the committed boundary: " + atBoundary.Status);
            var resumed = await committed.DeleteExecutor().ReconcileAsync(operation.OperationId,
                committed.Worker);
            Assert(resumed.Status == SyncAuthorityOperationStatus.Completed && !resumed.EffectDispatched,
                "the committed deletion resumed only its own completion: " + resumed.Reason);
            Assert(target.DeleteCalls == 1 && target.RecoveryCopyRequests == 1,
                "the resumed completion reissued neither the deletion nor the recovery copy");
            var final = await committed.SnapshotAsync(operation.OperationId);
            Assert(final.Status == SyncAuthorityOperationStatus.Completed,
                "the resumed committed deletion reached completion");
            Assert(await ScalarAsync(committed.Db.DatabasePath,
                       "SELECT count(*) FROM history_event WHERE operation_id='" + operation.OperationId +
                       "' AND event_kind='deletion_committed';") == "1",
                "the resumed completion committed the deletion exactly once");
            Assert(final.Allocations.Single(item =>
                       item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile).Status ==
                   SyncAuthorityAllocationStatus.Verified,
                "the predecessor recovery stays verified after the resumed completion");
            var noop = await committed.DeleteExecutor().ExecuteAsync(operation.OperationId, committed.Worker);
            Assert(noop.Status == SyncAuthorityOperationStatus.Completed && !noop.EffectDispatched &&
                   target.DeleteCalls == 1,
                "a completed deletion refuses to reissue anything when executed again");
        }
    }

    private static async Task RunnerRoutingAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);
        await using var site = await Site.CreateAsync("h-runner", "h", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(1728);
        var operation = await PlannedDeleteAsync(site, vanishedSide, payload);
        var target = site.StoreFor(targetSide);
        var peer = site.StoreFor(vanishedSide);
        Assert(operation.Kind == SyncAuthorityOperationKind.DeleteRemote,
            "the vanished logical left plans a deletion of the surviving logical right");

        // The common direction fence runs before any cloud engine: a pair that no longer allows this deletion
        // direction issues no request and preserves the retained operation as runnable.
        var startup = await site.Runner().RecoverAsync();
        Assert(startup.Decisions.Count == 0 && target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0,
            "startup recovery admits no fresh Ready cloud deletion");
        var fenced = await site.ExecuteFreshAsync(operation.OperationId, Path, SyncMode.DownloadOnly);
        Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 0,
            "the direction fence issued no recovery and no deletion request");
        Assert(fenced.Decisions.Single(item => item.RelativePath == Path).Kind == SyncDecisionKind.Blocked,
            "the fenced deletion is projected as blocked");
        var fencedOperation = await site.SnapshotAsync(operation.OperationId);
        Assert(fencedOperation.Attempts.Count == 0 &&
               fencedOperation.Status is SyncAuthorityOperationStatus.Ready or SyncAuthorityOperationStatus.Leased,
            "the fenced deletion keeps its retained runnable status and dispatched no attempt: " +
            fencedOperation.Status);

        // The real cloud runner routes the same operation to the one deletion engine and projects the
        // committed result as an archived path.
        var run = await site.ExecuteFreshAsync(operation.OperationId, Path);
        var decision = run.Decisions.Single(item => item.RelativePath == Path);
        Assert(decision.Kind == SyncDecisionKind.Archived,
            "the real cloud runner projects the completed deletion as archived: " + decision.Kind + " / " +
            decision.Reason);
        Assert(target.DeleteCalls == 1 && target.RecoveryCopyRequests == 1,
            "the runner route issued exactly one deletion and one predecessor recovery copy");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Completed,
            "the runner route completed the deletion");
        var unchanged = await site.Runner().RecoverAsync();
        Assert(target.DeleteCalls == 1 && !unchanged.Decisions.Any(item =>
                   string.Equals(item.RelativePath, Path, StringComparison.Ordinal)),
            "a second runner pass over the completed deletion reissues nothing");

        // The same runner still routes the other cloud engines: a real create reaches the create engine and
        // its own target adapter, so the deletion route neither replaced nor disturbed them.
        const string createdPath = "reports/created.txt";
        peer.SeedText(createdPath, "id-create-file", "rev-create-1", "created after the deletion");
        await site.ObserveStoreAsync(vanishedSide, "monitor");
        // The committed deletion only becomes visible to the planner once the surviving target side has been
        // re-observed as well, so both complete observations describe the post-deletion world for this pass.
        await site.ObserveStoreAsync(targetSide, "monitor");
        var created = await site.PlanAsync(createdPath, SyncAuthorityOperationKind.CreateRemote);
        var createRun = await site.ExecuteFreshAsync(created.OperationId!, createdPath);
        Assert(createRun.Decisions.Single(item => item.RelativePath == createdPath).Kind ==
               SyncDecisionKind.Uploaded,
            "the same runner still projects the cloud create route as uploaded");
        Assert(target.Uploads.Count == 1 && target.Holds(createdPath),
            "the create engine reached its own target adapter through the same runner");
        Assert((await site.SnapshotAsync(created.OperationId!)).Status ==
               SyncAuthorityOperationStatus.Completed,
            "the create routed through the same runner completed");
        Assert(target.DeleteCalls == 1,
            "routing the other cloud engine issued no additional deletion request");
    }

    private static async Task AuthorityEvidenceAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);
        await using var site = await Site.CreateAsync("i-evidence", "i", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(1856);
        var operation = await PlannedDeleteAsync(site, vanishedSide, payload);
        var target = site.StoreFor(targetSide);
        var peer = site.StoreFor(vanishedSide);
        var objectId = operation.ObjectId!;
        var versionsBefore = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM object_version WHERE object_id='" + objectId + "';");
        var membershipsBefore = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM membership WHERE object_id='" + objectId + "';");
        var deletion = await site.DeleteExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(deletion.Status == SyncAuthorityOperationStatus.Completed,
            "the evidence deletion completed: " + deletion.Reason);
        var identityJson = await ScalarAsync(site.Db.DatabasePath,
            "SELECT identity_json FROM recovery_artifact WHERE operation_id='" + operation.OperationId + "';");
        SyncAuthorityCloudRecoveryDescriptor? parsedDescriptor = null;
        var descriptorParsed = identityJson is not null &&
            SyncAuthorityCloudRecoveryDescriptor.TryParse(identityJson, out parsedDescriptor);
        Assert(descriptorParsed && parsedDescriptor is not null,
            "the completed deletion retained exactly one typed versioned cloud recovery descriptor");
        var descriptor = parsedDescriptor!;
        var retainedCopy = target.RecoveryCopies.Values.Single();
        var targetDefinition = SyncEndpointProviders.Get(targetSide == SyncAuthorityCloudSides.LogicalLeft
            ? site.LeftKind
            : site.RightKind);
        Assert(descriptor!.Provider == target.Provider &&
               descriptor!.ProviderAccountId == target.ProviderAccountId,
            "the descriptor carries the actual recovery storage provider and provider account");
        Assert(descriptor.LogicalSide == targetSide &&
               descriptor.EndpointRootBindingId == site.RootFor(targetSide),
            "the descriptor carries the deleted logical endpoint position separately from the storage authority");
        Assert(descriptor.RecoveryRootBindingId == site.RecoveryRootFor(targetSide) &&
               descriptor.RecoveryFolderId == site.RecoveryFolderFor(targetSide) &&
               descriptor.RecoveryNamespaceId == target.NamespaceId &&
               descriptor.RecoveryCorpusId == site.RecoveryCorpusFor(targetSide),
            "the descriptor binds the exact retained recovery root, folder, namespace and ACTUAL corpus");
        Assert(descriptor.NativeAlgorithm == targetDefinition.ContentHashAlgorithm,
            "the descriptor records the deleted provider's own registered native algorithm");
        Assert(descriptor.RetainedFileId == retainedCopy.Result.FileId &&
               descriptor.RetainedRevision == retainedCopy.Result.Revision &&
               descriptor.ContentHash == retainedCopy.Result.ContentHash &&
               descriptor.Size == payload.Length,
            "the descriptor binds the exact retained recovery file identity, revision, hash and size");
        Assert(descriptor.RetainedPath == retainedCopy.Result.RecoveryPath && descriptor.OriginalPath == Path,
            "the descriptor binds the retained recovery path and the original retired path");
        var recoveryAllocation = (await site.SnapshotAsync(operation.OperationId)).Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile);
        Assert(recoveryAllocation.BoundIdentity == descriptor.RetainedFileId &&
               retainedCopy.Payload.SequenceEqual(payload),
            "the recovery allocation is bound to the exact retired predecessor the descriptor describes");
        // The vanished side is the side the predecessor legitimately disappeared from, so the only true
        // invariant is that it stays exactly at that absence and that no delete, upload or recovery request
        // ever reached it: the recovery copy is retained in the deleted target's own recovery authority.
        Assert(!peer.Holds(Path) && peer.DeleteCalls == 0 && peer.Uploads.Count == 0 &&
               peer.RecoveryCopyRequests == 0 && peer.RecoveryCopies.Count == 0,
            "the vanished side retained exactly the absence the deletion started from and received no request at all");
        var versionsAfter = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM object_version WHERE object_id='" + objectId + "';");
        var membershipsAfter = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM membership WHERE object_id='" + objectId + "';");
        Assert(versionsAfter == versionsBefore && membershipsAfter == membershipsBefore,
            "the deletion fabricated and removed no object version or membership row: " + versionsBefore + "/" +
            membershipsBefore + " -> " + versionsAfter + "/" + membershipsAfter);
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM recovery_artifact WHERE " +
                   "operation_id='" + operation.OperationId + "';") == "1",
            "exactly one recovery artifact is retained for the completed deletion");
        Assert((await site.Db.LoadRecoveryArtifactsAsync()).Count == 1,
            "the recovery-artifact projection resolves exactly one retained descriptor");
        Assert(peer.LegacyRecoveryCalls == 0 && target.LegacyRecoveryCalls == 0 &&
               peer.RefusedEffects == 0 && target.RefusedEffects == 0,
            "only the bound recovery-root overload and the intended deletion request were ever reached");
    }

    // ----- CC3D-R1, CC3D-RES-1 and CC3D-RES-2 correction cases ---------------------------------------

    /// <summary>
    /// CC3D-R1: a sent revision-conditioned deletion may still be in flight. The request-sent boundary
    /// therefore unlocks no not-applied inference at all: presence is never proof, no synthetic KnownNotApplied
    /// receipt is written, no retry is issued and no completion is claimed - including after the already-sent
    /// request applies late.
    /// </summary>
    private static async Task RequestSentBoundaryAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);
        await using var site = await Site.CreateAsync("j-request-sent", "j", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(1920);
        var operation = await PlannedDeleteAsync(site, vanishedSide, payload);
        var target = site.StoreFor(targetSide);
        // A bounded guard on the real runner: a recurrence of the shared-runner live-lock must fail here
        // promptly instead of burning CPU for minutes, and every returned pass below proves that the real
        // RecoverAsync settled on the retained unresolved state instead of looping on it.
        using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var crashed = false;
        SyncAuthorityCloudDeleteExecutor.PhaseForTest = phase =>
        {
            if (phase == "request-sent-before-conditional-delete" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash at the deletion request-sent boundary");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.ExecuteFreshAsync(operation.OperationId, Path,
                    cancellationToken: settle.Token),
                "a crash at the deletion request-sent boundary");
        }
        finally
        {
            SyncAuthorityCloudDeleteExecutor.PhaseForTest = null;
        }
        Assert(crashed && target.DeleteCalls == 0 && target.RecoveryCopyRequests == 1,
            "the request-sent crash was reached before any deletion request with the recovery already retained");
        var sent = await site.SnapshotAsync(operation.OperationId);
        var sentAttempt = sent.Attempts.Single(item => item.EffectKind.EndsWith("-conditional-delete",
            StringComparison.Ordinal));
        Assert(sentAttempt.Status == SyncAuthorityAttemptStatus.RequestSent,
            "the retained journal records the deletion attempt as request-sent with no receipt");

        // The target is still present. The real runner must neither infer non-application from that presence
        // nor issue a retry, and it must not write a journal transition for it.
        var present = await site.Runner().RecoverAsync(settle.Token);
        Assert(!settle.IsCancellationRequested,
            "the runner returned from the target-present unresolved pass without timing out");
        Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 1,
            "reconciling a request-sent deletion with the target still present issued no deletion and no second recovery copy");
        var presentOperation = await site.SnapshotAsync(operation.OperationId);
        var presentAttempt = presentOperation.Attempts.Single(item => item.EffectKind.EndsWith(
            "-conditional-delete", StringComparison.Ordinal));
        Assert(presentAttempt.Status == SyncAuthorityAttemptStatus.RequestSent &&
               presentOperation.Status is not (SyncAuthorityOperationStatus.Completed or
                   SyncAuthorityOperationStatus.Committed),
             "presence unlocked no synthetic not-applied transition and no completion: " + presentAttempt.Status +
             "/" + presentOperation.Status);
        Assert(presentAttempt.AttemptId == sentAttempt.AttemptId,
            "the same retained deletion attempt is carried across both unresolved passes; no second attempt was created");
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM effect_receipt WHERE attempt_id='" +
                   presentAttempt.AttemptId + "';") == "0",
            "no receipt of any certainty was synthesised for the still-unresolved request-sent deletion");
        Assert(target.Holds(Path) && target.PayloadOf(Path).SequenceEqual(payload),
            "the still-present target was left exactly untouched");
        Assert(present.Decisions.Single(item => item.RelativePath == Path).Kind != SyncDecisionKind.Archived,
            "the runner never projects an unresolved request-sent deletion as archived");

        // The sent request may still apply later. Even after it does, nothing may become complete, no retry may
        // be issued and no journal transition may be invented.
        target.Remove(Path);
        var late = await site.Runner().RecoverAsync(settle.Token);
        Assert(!settle.IsCancellationRequested,
            "the runner returned from the simulated-late-application pass without timing out");
        Assert(target.DeleteCalls == 0 && target.RecoveryCopyRequests == 1,
            "the late application of the already-sent request issued no second deletion and no second recovery");
        var lateOperation = await site.SnapshotAsync(operation.OperationId);
        Assert(lateOperation.Attempts.Count == 2 &&
               lateOperation.Attempts.Single(item => item.EffectKind.EndsWith("-conditional-delete",
                   StringComparison.Ordinal)).Status == SyncAuthorityAttemptStatus.RequestSent,
            "the late application invented no journal transition");
        Assert(lateOperation.Status is not (SyncAuthorityOperationStatus.Completed or
                   SyncAuthorityOperationStatus.Committed),
            "the late application produced no completion: " + lateOperation.Status);
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM effect_receipt WHERE attempt_id='" +
                   presentAttempt.AttemptId + "';") == "0",
            "the late application still left no synthesised receipt for the request-sent attempt");
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM tombstone WHERE pair_id='" +
                   site.Pair.Id + "' AND status='resolved';") == "0",
            "the late application resolved no retained tombstone");
        Assert(await ScalarAsync(site.Db.DatabasePath,
                   "SELECT count(*) FROM history_event WHERE operation_id='" + operation.OperationId +
                   "' AND event_kind='deletion_committed';") == "0",
            "the late application committed no deletion event");
        Assert(await ScalarAsync(site.Db.DatabasePath,
                   "SELECT count(*) FROM lease WHERE status='active';") == "0",
            "the unresolved request-sent deletion left no active lease");
        Assert(!late.Decisions.Any(item => item.RelativePath == Path &&
                   item.Kind == SyncDecisionKind.Archived),
            "the runner still never projects the unresolved deletion as archived");
    }

    private static async Task DeletePostRecoveryRestartAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);
        await using var site = await Site.CreateAsync("k-delete-restart", "k", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(1984);
        var operation = await PlannedDeleteAsync(site, vanishedSide, payload);
        var target = site.StoreFor(targetSide);
        var crashed = false;
        SyncAuthorityCloudDeleteExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-recovery-verified" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the verified deletion recovery");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.ExecuteFreshAsync(operation.OperationId, Path),
                "a deletion crash after the verified predecessor recovery");
        }
        finally
        {
            SyncAuthorityCloudDeleteExecutor.PhaseForTest = null;
        }
        Assert(crashed && target.RecoveryCopyRequests == 1 && target.DeleteCalls == 0,
            "the post-recovery crash was reached after exactly one recovery copy and no deletion request");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Validating,
            "the crashed deletion retains the engines' own post-recovery validating state");
        var recoverable = await site.Db.ListRecoveryOperationsForPairAsync(site.Pair.Id);
        Assert(recoverable.Single().OperationId == operation.OperationId && recoverable.Single().RecoveryRequired,
            "the actual startup queue retains the interrupted deletion with verified predecessor recovery");
        // The ACTUAL runner resumes that state: no duplicate recovery copy, exactly one deletion, no repeat.
        var resumed = await site.Runner().RecoverAsync();
        Assert(target.RecoveryCopyRequests == 1,
            "the runner route never copied the deletion predecessor recovery a second time");
        Assert(target.DeleteCalls == 1 && !target.Holds(Path),
            "the runner route issued exactly one conditional deletion from the verified recovery");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Completed,
            "the runner route completed the deletion from the validating boundary");
        Assert(resumed.Decisions.Single(item => item.RelativePath == Path).Kind == SyncDecisionKind.Archived,
            "the runner projects the resumed deletion as archived");
        var repeat = await site.Runner().RecoverAsync();
        Assert(target.DeleteCalls == 1 && target.RecoveryCopyRequests == 1 && !repeat.Decisions.Any(item =>
                   string.Equals(item.RelativePath, Path, StringComparison.Ordinal)),
            "a further runner pass over the completed deletion repeats nothing");

        // The same validating boundary is still fenced by the pair's direction before any new target effect.
        await using var fenced = await Site.CreateAsync("k-delete-restart-fence", "kf",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var fencedOperation = await PlannedDeleteAsync(fenced, vanishedSide, payload);
        var fencedTarget = fenced.StoreFor(targetSide);
        crashed = false;
        SyncAuthorityCloudDeleteExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-recovery-verified" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the verified deletion recovery");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => fenced.ExecuteFreshAsync(fencedOperation.OperationId, Path),
                "a deletion crash after the verified predecessor recovery for the fenced pair");
        }
        finally
        {
            SyncAuthorityCloudDeleteExecutor.PhaseForTest = null;
        }
        Assert((await fenced.SnapshotAsync(fencedOperation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Validating && fencedTarget.DeleteCalls == 0,
            "the fenced pair retained its validating post-recovery deletion state");
        var fencedRun = await fenced.Runner(SyncMode.DownloadOnly).RecoverAsync();
        Assert(fencedTarget.DeleteCalls == 0 && fencedTarget.RecoveryCopyRequests == 1,
            "the direction fence issued zero new deletion from the validating boundary and copied no second recovery");
        Assert(fencedRun.Decisions.Single(item => item.RelativePath == Path).Kind == SyncDecisionKind.Blocked,
            "the fenced validating deletion is projected as blocked");
        Assert((await fenced.SnapshotAsync(fencedOperation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Validating,
            "the fenced validating deletion kept its exact retained state");
    }

    /// <summary>
    /// The real planner's update operation for one changed side, mirroring the accepted CC3C drive: both
    /// endpoints are observed holding independently created equivalent content and adopted without an effect,
    /// the source is then changed and re-observed, and the real planner's own update operation is returned.
    /// </summary>
    private static async Task<SyncAuthorityOperationSnapshot> PlannedUpdateAsync(Site site, string sourceSide,
        byte[] predecessorPayload, byte[] successorPayload)
    {
        var targetSide = OtherSide(sourceSide);
        site.StoreFor(sourceSide).Seed(Path, "id-src-file", "rev-src-1", predecessorPayload);
        site.StoreFor(targetSide).Seed(Path, "id-target-file", "rev-target-1", predecessorPayload);
        await site.ObserveStoreAsync(sourceSide, "initial");
        await site.ObserveStoreAsync(targetSide, "initial");
        var adopted = await site.PlanBaselineAsync(Path);
        Assert(adopted.Kind == SyncAuthorityPlannerOutcomeKind.Adopted && adopted.OperationId is null,
            "the real planner adopted the converged update baseline without an effect: " + adopted.Kind + " / " +
            adopted.Reason);
        site.StoreFor(sourceSide).Seed(Path, "id-src-file", "rev-src-2", successorPayload);
        await site.ObserveStoreAsync(sourceSide, "monitor");
        var kind = sourceSide == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityOperationKind.UpdateRemote
            : SyncAuthorityOperationKind.UpdateLocal;
        var update = await site.PlanAsync(Path, kind);
        return await site.SnapshotAsync(update.OperationId!);
    }

    /// <summary>
    /// The update engine's own post-recovery validating boundary, resumed through the ACTUAL runner: no second
    /// recovery copy, exactly one conditional replacement, no repeat on a further pass, and a forbidden
    /// direction still yields zero new replacement while retaining the exact validating state.
    /// </summary>
    private static async Task UpdatePostRecoveryRestartAsync()
    {
        await using var site = await Site.CreateAsync("l-update-restart", "l", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var predecessor = Pattern(2048);
        var successor = Pattern(2176, 43);
        var operation = await PlannedUpdateAsync(site, SyncAuthorityCloudSides.LogicalLeft, predecessor,
            successor);
        var target = site.Right;
        var crashed = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-recovery-verified" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the verified update recovery");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.ExecuteFreshAsync(operation.OperationId, Path),
                "an update crash after the verified predecessor recovery");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        Assert(crashed && target.RecoveryCopyRequests == 1 && target.Uploads.Count == 0,
            "the post-recovery update crash was reached after exactly one recovery copy and no replacement");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Validating,
            "the crashed update retains its own post-recovery validating state");
        var recoverable = await site.Db.ListRecoveryOperationsForPairAsync(site.Pair.Id);
        Assert(recoverable.Single().OperationId == operation.OperationId && recoverable.Single().RecoveryRequired,
            "the actual startup queue retains the interrupted update with verified predecessor recovery");
        var resumed = await site.Runner().RecoverAsync();
        Assert(target.RecoveryCopyRequests == 1,
            "the runner route never copied the update predecessor recovery a second time");
        Assert(target.Uploads.Count == 1 && target.PayloadOf(Path).SequenceEqual(successor),
            "the runner route issued exactly one conditional replacement from the verified recovery");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Completed,
            "the runner route completed the update from the validating boundary");
        Assert(resumed.Decisions.Single(item => item.RelativePath == Path).Kind == SyncDecisionKind.Uploaded,
            "the runner projects the resumed update as uploaded");
        var repeat = await site.Runner().RecoverAsync();
        Assert(target.Uploads.Count == 1 && !repeat.Decisions.Any(item =>
                   string.Equals(item.RelativePath, Path, StringComparison.Ordinal)),
            "a further runner pass over the completed update repeats nothing");

        await using var fenced = await Site.CreateAsync("l-update-restart-fence", "lf", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var fencedOperation = await PlannedUpdateAsync(fenced, SyncAuthorityCloudSides.LogicalLeft, Pattern(2304),
            Pattern(2432, 47));
        var fencedTarget = fenced.Right;
        crashed = false;
        SyncAuthorityCloudUpdateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-recovery-verified" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the verified update recovery");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => fenced.ExecuteFreshAsync(fencedOperation.OperationId, Path),
                "an update crash after the verified predecessor recovery for the fenced pair");
        }
        finally
        {
            SyncAuthorityCloudUpdateExecutor.PhaseForTest = null;
        }
        Assert((await fenced.SnapshotAsync(fencedOperation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Validating && fencedTarget.Uploads.Count == 0,
            "the fenced pair retained its validating post-recovery update state");
        var fencedRun = await fenced.Runner(SyncMode.DownloadOnly).RecoverAsync();
        Assert(fencedTarget.Uploads.Count == 0 && fencedTarget.RecoveryCopyRequests == 1,
            "the direction fence issued zero replacement from the validating update boundary and copied no second recovery");
        Assert(fencedRun.Decisions.Single(item => item.RelativePath == Path).Kind == SyncDecisionKind.Blocked,
            "the fenced validating update is projected as blocked");
        Assert((await fenced.SnapshotAsync(fencedOperation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Validating,
            "the fenced validating update kept its exact retained state");
    }

    /// <summary>
    /// CC3D-RES-2: a deletion creates no private object. Its authorized private transaction allocation is
    /// retained exactly as the planner left it, completion only proves that the expected private transaction
    /// object is absent, and an unexpected object at that exact path is refused and left untouched.
    /// </summary>
    private static async Task UnexpectedPrivateObjectAsync()
    {
        var vanishedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(vanishedSide);
        await using var site = await Site.CreateAsync("m-unexpected-object", "m", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(2560);
        var operation = await PlannedDeleteAsync(site, vanishedSide, payload);
        var target = site.StoreFor(targetSide);
        var crashed = false;
        SyncAuthorityCloudDeleteExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-commit-before-completion" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the deletion commit boundary");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.DeleteExecutor().ExecuteAsync(operation.OperationId,
                site.Worker), "a commit-boundary crash before the unexpected-object case");
        }
        finally
        {
            SyncAuthorityCloudDeleteExecutor.PhaseForTest = null;
        }
        var committed = await site.SnapshotAsync(operation.OperationId);
        Assert(crashed && committed.Status == SyncAuthorityOperationStatus.Committed,
            "the committed deletion boundary was reached: " + committed.Status);
        var transaction = committed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        Assert(transaction.Status == SyncAuthorityAllocationStatus.Authorized,
            "the deletion retained its never-created private transaction allocation as Authorized");
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
        Assert(!Directory.Exists(transactionPath) && !File.Exists(transactionPath),
            "the deletion created no private object at its authorized transaction allocation");
        // An unexpected private object at that exact path must remain untouched and refused, never adopted,
        // removed or completed over.
        Directory.CreateDirectory(transactionPath);
        var foreignPath = IOPath.Combine(transactionPath, "foreign.txt");
        await File.WriteAllTextAsync(foreignPath, "foreign evidence");
        await ExpectAnyRefusalAsync(() => site.DeleteExecutor().ReconcileAsync(operation.OperationId,
            site.Worker), "a deletion whose authorized private transaction path unexpectedly exists");
        Assert(Directory.Exists(transactionPath) && File.Exists(foreignPath) &&
               await File.ReadAllTextAsync(foreignPath) == "foreign evidence",
            "the unexpected private object was preserved exactly as it was found");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Committed,
            "the refused completion left the committed boundary exactly as it was");
        Assert(target.DeleteCalls == 1 && target.RecoveryCopyRequests == 1,
            "the refused completion reissued neither the deletion nor the recovery copy");
    }

    // ----- expectation helpers ------------------------------------------------------------------------

    /// <summary>The real runner's own projection of one execution result, not a test substitute.</summary>
    private static SyncDecision RunnerDecision(string path, SyncAuthorityOperationKind kind,
        SyncAuthorityExecutionResult result)
    {
        var projection = typeof(SyncAuthorityPairRunner).GetMethod("Decision", BindingFlags.Static |
            BindingFlags.NonPublic, null,
            [typeof(string), typeof(SyncAuthorityOperationKind), typeof(SyncAuthorityExecutionResult)],
            null) ?? throw new InvalidOperationException("The runner's decision projection was not found.");
        return (SyncDecision)projection.Invoke(null, [path, kind, result])!;
    }

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
