using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using IOPath = System.IO.Path;

// CC3E-1..CC3E-3 focused proofs: the one internal generic cloud endpoint-native move engine over real isolated
// SQLite authority, the real planner, the real typed CC2C cloud endpoint ports and the real CC4B runner.
//
// The planner, the journal, the attempt and receipt records, the version and membership registration, the
// ordered synchronization point, the operation lease and fencing and the completion transitions are the shipped
// production code. Only the two provider transports are scripted adapters: they hold a real in-memory
// inventory, perform a real identity-preserving path move inside their own store (minting a new revision
// exactly as a provider metadata update does, while the stable identity, kind, size and native content stay
// exact), and refuse every effect they were not given. Nothing here reaches a live account, credential,
// network, installed database, product state, Host wiring or NTFS pair root.

internal static class CloudMoveExecutionChecks
{
    private const string InstallationId = "installation-cloud-move-execution-318";
    private const string SourceRevision = "cloud-move-execution-318";
    private const string ProfileId = "profile-cloud-move-execution-318";
    private const string Path = "reports/quarterly.txt";
    private const string MovedPath = "archive/2026/quarterly.txt";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static int _unexpectedEffects;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud move execution fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        _unexpectedEffects = 0;

        await CaseAsync("A-ordered-move-matrix",
            "Each of the four ordered cloud combinations moves one file in both logical directions natively: the target keeps its exact stable identity and bytes at the new path with no upload, copy or delete, the ordered synchronization point is committed and a second unchanged run issues nothing",
            OrderedMatrixAsync);
        await CaseAsync("B-empty-folder-move",
            "An empty folder moves natively through the real runner and both endpoints converge on the new path after fresh observations",
            EmptyFolderMoveAsync);
        await CaseAsync("C-nonempty-folder-and-child-edit",
            "A nonempty folder move stops the stale plan before any queued descendant executes, and a concurrent child edit is preserved and surfaces as a change after fresh observations instead of being deleted or overwritten",
            FolderWithConcurrentChildEditAsync);
        await CaseAsync("D-plan-and-authority-refusals",
            "Foreign pair or root, drifted source, drifted predecessor, occupied or case-ambiguous destination, another kind and an allocation-bearing move all refuse with zero move requests",
            PlanAuthorityRefusalsAsync);
        await CaseAsync("E-unsupported-move",
            "An adapter that cannot perform an identity-preserving move yields a durable conclusive non-application and never a synthesised one, and only that exact evidence permits a fenced retry",
            UnsupportedMoveAsync);
        await CaseAsync("F-lost-move-response",
            "A move applied or refused with a lost response is unknown, is never repeated, and an exact destination alone never commits",
            LostMoveResponseAsync);
        await CaseAsync("G-known-receipt-restart",
            "A retained known-success move receipt plus the exact readback commits exactly once after a restart and completes without another move",
            KnownReceiptRestartAsync);
        await CaseAsync("H-committed-restart",
            "A committed move resumes only its own completion, creates and removes no private object and never reissues the move",
            CommittedRestartAsync);
        await CaseAsync("I-other-routes-preserved",
            "The same cloud runner still routes the create and delete engines after a move, and no non-move kind is ever diverted into the move engine",
            OtherRoutesPreservedAsync);
        await CaseAsync("J-revision-advancing-restart",
            "A move whose adapter advanced the revision and whose known-success receipt arrived before the commit resumes from the ACTUAL verified moved identity on both typed target sides, and a duplicate desired-path identity refuses instead of falsely completing",
            RevisionAdvancingRestartAsync);
        await CaseAsync("K-adapter-contradicted-readback",
            "An adapter that reports a different object identity after a real move is never trusted: the outcome stays unresolved with zero repeated move, no known-success receipt and nothing committed, even though the exact moved identity is visible at the desired path",
            ContradictedReadbackAsync);

        Assert(_unexpectedEffects == 0,
            "no cloud move fixture ever reached a provider effect it was not given");
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudMoveExecutionChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "cloud-move-execution-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud move execution: " + _assertions + " assertions; the real planner, journal," +
            " version and membership registration, ordered synchronization point and completion transitions" +
            " over real isolated SQLite for the four ordered cloud combinations, both logical directions, a file," +
            " an empty folder and a nonempty folder with a concurrent child edit, plus every required refusal," +
            " unknown, restart and route-preservation path.");
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
        if (!condition) throw new InvalidDataException("CC3E assertion failed: " + label);
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

    // ----- the real harness -------------------------------------------------------------------------

    private sealed class Site : IAsyncDisposable
    {
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
        internal MoveFixtureStore Left = null!;
        internal MoveFixtureStore Right = null!;
        internal SyncControlNamespaceBinding Control = null!;
        internal SyncAuthorityCloudAllocationConfiguration Allocations = null!;
        private PilotDirectoryLease? _anchor;
        private PilotDirectoryLease? _control;

        // Only one delete-access lease can be held on the private control root at a time, so the fixture's own
        // long-lived lease is opened lazily by the cases that drive an engine or adapter directly. A case that
        // routes through the real runner never opens it, so the runner's own per-dispatch lease can be opened
        // and disposed exactly as the production factory does.
        internal PilotDirectoryLease ControlRoot =>
            _control ??= SyncControlNamespace.OpenLocal(Control, _anchor!);
        internal string Worker => "worker-cc3e-" + Slug;

        internal static async Task<Site> CreateAsync(string caseName, string slug, SyncEndpointKind leftKind,
            SyncEndpointKind rightKind)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The shared cloud move execution proofs require Windows NTFS identities and protected ACLs.");
            var site = new Site
            {
                Slug = slug,
                LeftKind = leftKind,
                RightKind = rightKind,
                NamespaceId = "cloud-move-execution-" + slug,
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
            var leftAccount = "account-" + slug + "-left";
            var rightAccount = "account-" + slug + "-right";
            var leftProvider = SyncEndpointProviders.Get(leftKind).ProviderId!;
            var rightProvider = SyncEndpointProviders.Get(rightKind).ProviderId!;
            var leftFolder = FolderId(leftKind, slug + "-left");
            var rightFolder = FolderId(rightKind, slug + "-right");
            var leftNamespace = EndpointNamespace(leftKind, "id:" + leftAccount, "ns-root-" + slug + "-left");
            var rightNamespace = EndpointNamespace(rightKind, "id:" + rightAccount,
                "ns-root-" + slug + "-right");
            site.Left = MoveFixtureStore.Create(leftProvider, leftAccount, leftNamespace, leftFolder);
            site.Right = MoveFixtureStore.Create(rightProvider, rightAccount, rightNamespace, rightFolder);
            site.Db = new SyncAuthorityDatabase(IOPath.Combine(site.CaseRoot, "syncshark.db"));
            await site.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await site.Db.RegisterProviderAccountAsync(leftAccount, InstallationId, "id:" + leftAccount,
                "ns-root-" + slug + "-left", "ns-home-" + slug + "-left", "CC3E left account", 1,
                cancellationToken: default, provider: leftProvider);
            await site.Db.RegisterProviderAccountAsync(rightAccount, InstallationId, "id:" + rightAccount,
                "ns-root-" + slug + "-right", "ns-home-" + slug + "-right", "CC3E right account", 1,
                cancellationToken: default, provider: rightProvider);
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(leftKind, "conn-" + slug + "-left", leftNamespace, leftFolder, "/left"),
                SyncEndpoint.Cloud(rightKind, "conn-" + slug + "-right", rightNamespace, rightFolder, "/right"),
                SyncMode.TwoWay), "CC3E " + caseName);
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
                leftAccount, leftNamespace, leftFolder + "-recovery", site.LeftRecoveryCorpus,
                site.RecoveryPath(SyncAuthorityCloudSides.LogicalLeft), 1, cancellationToken: default,
                hierarchies: new[]
                {
                    Chain(leftKind, leftAccount, leftNamespace, leftFolder, site.LeftRoot),
                    Chain(leftKind, leftAccount, leftNamespace, leftFolder + "-recovery", site.LeftRecoveryRoot)
                });
            await site.Db.RegisterCloudRecoveryRootAsync(site.RightRecoveryRoot, InstallationId, rightProvider,
                rightAccount, rightNamespace, rightFolder + "-recovery", site.RightRecoveryCorpus,
                site.RecoveryPath(SyncAuthorityCloudSides.LogicalRight), 1, cancellationToken: default,
                hierarchies: new[]
                {
                    Chain(rightKind, rightAccount, rightNamespace, rightFolder, site.RightRoot),
                    Chain(rightKind, rightAccount, rightNamespace, rightFolder + "-recovery", site.RightRecoveryRoot)
                });
            site.Allocations = new SyncAuthorityCloudAllocationConfiguration(site.Control.NamespaceId,
                new SyncAuthorityCloudTargetAllocation(site.LeftRecoveryRoot, 1L << 30),
                new SyncAuthorityCloudTargetAllocation(site.RightRecoveryRoot, 1L << 30));
            return site;
        }

        internal static string EndpointNamespace(SyncEndpointKind kind, string accountId,
            string rootNamespaceId) =>
            kind == SyncEndpointKind.GoogleDrive ? accountId : rootNamespaceId;

        internal static string FolderId(SyncEndpointKind kind, string suffix) =>
            kind == SyncEndpointKind.Dropbox ? "id:cc3e-" + suffix : "gd-cc3e-" + suffix;

        internal string RecoveryPath(string side) =>
            SyncAuthorityCloudRootPersistence.NormalizeDisplayPath("/Recovery/" + Slug + "/" +
                (side == SyncAuthorityCloudSides.LogicalLeft ? "left" : "right"));

        internal SyncAuthorityPlannerOptions Options(SyncMode direction = SyncMode.TwoWay,
            bool moveEnabled = true) =>
            new(1, ProfileId, Control.NamespaceId, Control.NamespaceId,
                EnableMoveEffects: moveEnabled, Direction: direction, CloudAllocations: Allocations);

        internal SyncAuthorityCloudSideAuthority SideAuthority(string side)
        {
            var store = StoreFor(side);
            return new SyncAuthorityCloudSideAuthority(side, store.Provider,
                "conn-" + Slug + "-" + (side == SyncAuthorityCloudSides.LogicalLeft ? "left" : "right"),
                store.ProviderAccountId, store.NamespaceId, store.FolderId,
                side == SyncAuthorityCloudSides.LogicalLeft ? "/left" : "/right", RootFor(side),
                RecoveryRootFor(side), RecoveryFolderFor(side), store.NamespaceId, RecoveryCorpusFor(side),
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
            FolderId(side == SyncAuthorityCloudSides.LogicalLeft ? LeftKind : RightKind,
                Slug + (side == SyncAuthorityCloudSides.LogicalLeft ? "-left" : "-right")) + "-recovery";

        internal string RootFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRoot : RightRoot;

        internal MoveFixtureStore StoreFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? Left : Right;

        internal SyncAuthorityCloudEndpointSet Endpoints() => SyncAuthorityCloudEndpointSet.FromPair(Pair,
            Left.Store, Right.Store, LeftRoot, RightRoot, Left.ProviderAccountId, Right.ProviderAccountId);

        internal SyncAuthorityCloudExecutionContext Context() => new(Endpoints(), Control, ControlRoot);

        internal SyncAuthorityCloudMoveExecutor MoveExecutor() => new(Db, Context());

        internal SyncAuthorityCloudMoveExecutor ForeignExecutor(Site other) =>
            new(Db, new SyncAuthorityCloudExecutionContext(other.Endpoints(), Control, ControlRoot));

        internal SyncAuthorityPreparedCloudPair Prepared(SyncMode direction = SyncMode.TwoWay,
            bool moveEnabled = true) =>
            new(Pair, Endpoints(), Control, SideAuthority(SyncAuthorityCloudSides.LogicalLeft),
                SideAuthority(SyncAuthorityCloudSides.LogicalRight), Options(direction, moveEnabled),
                Allocations, Array.Empty<SyncAuthorityCloudHierarchySnapshot>());

        private SyncAuthorityCommandAdmission Admission { get; set; } = null!;

        internal SyncAuthorityPairRunner Runner(SyncMode direction = SyncMode.TwoWay) =>
            new SyncAuthorityPairRunner(Pair, Right.Store, Db, LeftRoot, RightRoot, Options(direction),
                new PilotRunControl(), Admission).AttachCloudContext(new SyncAuthorityCloudRunnerContext(Prepared(direction),
                // The production factory opens one fresh owned control-root lease per dispatch and the shared
                // dispatch disposes exactly that lease, so the fixture supplies the same per-call ownership
                // rather than handing out its own long-lived lease.
                () => SyncControlNamespace.OpenLocal(Control, _anchor!)));

        // Feed the real retained planner result to the production fresh-plan executor. Startup recovery
        // deliberately excludes unissued Ready cloud work and must not be changed to satisfy this fixture.
        internal async Task<SyncRunResult> ExecuteFreshAsync(string operationId, string path)
        {
            var operation = await SnapshotAsync(operationId);
            Assert(operation.Status == SyncAuthorityOperationStatus.Ready,
                "the fresh runner input is the real planner's retained Ready operation");
            var execution = await Runner().ExecuteBoundAsync(new SyncAuthorityPairPlanningResult(
                Pair.Id, [], [new SyncAuthorityPlannedPairOperation(path, operation)]));
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

        internal SyncAuthorityObservationInput Entry(MoveFixtureStore store, string relativePath,
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

        internal async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> PlanAsync(
            SyncAuthorityPlannerOptions? options = null) => await Planner(options).PlanAsync(Pair.Id);

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
    /// A scripted cloud adapter over a real in-memory inventory that performs a real identity-preserving path
    /// move inside its own store: the stable identity, kind, size and native content survive and only the
    /// revision changes, exactly as a provider metadata-update move does. A folder move re-paths its whole
    /// retained subtree, which is the adapter's own subtree-identity contract. Every other effect is refused, so
    /// an unexpected upload or delete would fail the run.
    /// </summary>
    public interface IMoveFixtureCloudStore : ISyncRemoteStore, ISyncRemoteRangeStore
    {
    }

    public class MoveFixtureStore : DispatchProxy
    {
        internal string Provider = "";
        internal string ProviderAccountId = "";
        internal string NamespaceId = "";
        internal string FolderId = "";
        internal List<PilotRemoteEntry> Entries { get; } = [];
        internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
        internal List<(string Id, string From, string To)> Moves { get; } = [];
        internal List<(string Path, byte[] Bytes, string ExpectedRevision)> Uploads { get; } = [];
        internal int RevisionCounter;
        internal int RefusedEffects;
        internal int VerifyCalls;
        internal int ListCalls;
        internal bool RefuseMove;
        internal bool RefuseMoveWithoutApply;
        internal bool ApplyThenFailOnMove;
        internal bool ContradictoryMoveResult;
        // CC3E-C1: an adapter-visible second entry at one path, used to prove that a duplicate desired-path
        // identity can never be classified as an exact moved result.
        internal PilotRemoteEntry? ExtraListingEntry;

        internal int MoveCalls => Moves.Count;

        internal static MoveFixtureStore Create(string provider, string providerAccountId, string namespaceId,
            string folderId)
        {
            var fixture = (MoveFixtureStore)(object)DispatchProxy.Create<IMoveFixtureCloudStore,
                MoveFixtureStore>();
            fixture.Provider = provider;
            fixture.ProviderAccountId = providerAccountId;
            fixture.NamespaceId = namespaceId;
            fixture.FolderId = folderId;
            return fixture;
        }

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

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

        internal PilotRemoteEntry? FindByIdentity(string fileId) => Entries.SingleOrDefault(entry =>
            string.Equals(entry.Id, fileId, StringComparison.Ordinal));

        internal bool Holds(string relativePath) => Find(relativePath) is not null;

        internal IReadOnlyList<PilotRemoteEntry> ChildrenOf(string folder) => Entries.Where(entry =>
            entry.RelativePath.StartsWith(folder + "/", StringComparison.Ordinal)).ToArray();

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
            "get_SupportsConditionalDirectoryDelete" => false,
            nameof(ISyncRemoteStore.VerifyRootIdentityAsync) => Verify(),
            nameof(ISyncRemoteStore.ListEntriesAsync) => List(),
            // DispatchProxy does not run interface defaults. Use the same production exact-lookup decision
            // as the real store default; do not synthesize a successful identity or readback in this fixture.
            nameof(ISyncRemoteStore.ReadEntryAsync) => SyncRemoteExactLookup.RequireEntryAsync(
                (ISyncRemoteStore)(object)this, (string)arguments![0]!, (string)arguments[1]!,
                (CancellationToken)arguments[2]!),
            nameof(ISyncRemoteStore.FindEntryAtPathAsync) => SyncRemoteExactLookup.FindEntryAsync(
                (ISyncRemoteStore)(object)this, (string)arguments![0]!, (CancellationToken)arguments[1]!),
            nameof(ISyncRemoteStore.UploadAsync) => Upload(arguments),
            nameof(ISyncRemoteStore.DownloadAsync) => Download(arguments),
            nameof(ISyncRemoteRangeStore.DownloadRangeAsync) => DownloadRange(arguments),
            nameof(ISyncRemoteStore.MoveAsync) => Move(arguments),
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
            var entries = ExtraListingEntry is null
                ? Entries.ToArray()
                : Entries.Concat(new[] { ExtraListingEntry }).ToArray();
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

        /// <summary>
        /// The identity-preserving, no-overwrite path move of this scripted endpoint. The stable identity, kind,
        /// size and native content survive exactly; only the revision changes, as a provider metadata-update move
        /// does. A folder move re-paths its retained subtree. A destination occupied by another stable identity is
        /// refused without any effect.
        /// </summary>
        private object Move(object?[]? arguments)
        {
            var expected = (PilotRemoteEntry)arguments![0]!;
            var destination = Normalize((string)arguments![1]!);
            var source = Normalize(expected.RelativePath);
            Moves.Add((expected.Id ?? "", source, destination));
            if (RefuseMove)
                throw new NotSupportedException(
                    "This scripted target adapter cannot perform an identity-preserving move.");
            if (RefuseMoveWithoutApply)
                throw new IOException(
                    "The scripted target refused the move before applying it and lost the response.");
            var current = Find(source);
            if (current is null || !string.Equals(current.Id, expected.Id, StringComparison.Ordinal))
                throw new IOException(
                    "The scripted target no longer carries the exact reviewed identity at the prior path.");
            var canonical = PilotPath.CanonicalComparisonKey(destination);
            if (Entries.Any(entry => string.Equals(PilotPath.CanonicalComparisonKey(entry.RelativePath),
                    canonical, StringComparison.Ordinal) &&
                !string.Equals(entry.Id, expected.Id, StringComparison.Ordinal)))
                throw new IOException("The scripted target destination is occupied.");
            var revision = "rev-" + (++RevisionCounter);
            var subtree = current.Kind == PilotEntryKind.Directory
                ? Entries.Where(entry => entry.RelativePath.StartsWith(source + "/",
                    StringComparison.Ordinal)).ToArray()
                : Array.Empty<PilotRemoteEntry>();
            var index = Entries.IndexOf(current);
            Entries[index] = new PilotRemoteEntry(destination, current.Kind, current.Id, revision, current.Size,
                current.ContentHash);
            if (Payloads.Remove(source, out var payload)) Payloads[destination] = payload;
            foreach (var child in subtree)
            {
                var childIndex = Entries.IndexOf(child);
                var childPath = destination + child.RelativePath[source.Length..];
                Entries[childIndex] = new PilotRemoteEntry(childPath, child.Kind, child.Id, child.Revision,
                    child.Size, child.ContentHash);
                if (Payloads.Remove(child.RelativePath, out var childPayload)) Payloads[childPath] = childPayload;
            }
            if (ApplyThenFailOnMove)
                throw new IOException("The scripted provider applied the move and then lost the response.");
            var moved = Find(destination)!;
            return Task.FromResult(ContradictoryMoveResult
                ? moved with { Id = "id-foreign-moved" }
                : moved);
        }

        private object Refuse(MethodInfo? method)
        {
            RefusedEffects++;
            _unexpectedEffects++;
            throw new NotSupportedException("This cloud move execution fixture performs no such provider effect: " +
                (method?.Name ?? "unknown"));
        }
    }

    // ----- the real planner drive ---------------------------------------------------------------

    private static SyncAuthorityOperationKind MoveKindFor(string movedSide) =>
        movedSide == SyncAuthorityCloudSides.LogicalLeft
            ? SyncAuthorityOperationKind.MoveRemote
            : SyncAuthorityOperationKind.MoveLocal;

    /// <summary>
    /// Establishes one real baseline and then the real move operation: both endpoints are observed holding the
    /// same object at the prior path and adopted without an effect, the object is then re-pathed on one endpoint
    /// only (with its retained subtree when it is a folder) and that endpoint is re-observed, so the real planner
    /// plans the move of the other endpoint's own stable object.
    /// </summary>
    private static async Task<SyncAuthorityOperationSnapshot> PlannedMoveAsync(Site site, string movedSide,
        string priorPath, string desiredPath, bool directory, byte[]? payload, bool withChild = true)
    {
        var moved = site.StoreFor(movedSide);
        var peer = site.StoreFor(OtherSide(movedSide));
        if (directory)
        {
            moved.SeedDirectory(priorPath, "id-moved-folder");
            peer.SeedDirectory(priorPath, "id-peer-folder");
            if (withChild)
            {
                moved.Seed(priorPath + "/child.txt", "id-moved-child", "rev-moved-child-1", payload!);
                peer.Seed(priorPath + "/child.txt", "id-peer-child", "rev-peer-child-1", payload!);
            }
        }
        else
        {
            moved.Seed(priorPath, "id-moved-file", "rev-moved-1", payload!);
            peer.Seed(priorPath, "id-peer-file", "rev-peer-1", payload!);
        }
        await site.ObserveStoreAsync(movedSide, "initial");
        await site.ObserveStoreAsync(OtherSide(movedSide), "initial");
        await site.PlanAsync();
        RePath(moved, priorPath, desiredPath, directory);
        await site.ObserveStoreAsync(movedSide, "monitor");
        var outcomes = await site.PlanAsync();
        // A move outcome is recorded for the moved identity's DESIRED path, so a directory move that also
        // re-paths its retained child legitimately plans more than one move outcome in the same pass: the one
        // this helper drives is the parent's, selected here by its exact desired path.
        var move = outcomes.SingleOrDefault(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move &&
            string.Equals(item.RelativePath, desiredPath, StringComparison.Ordinal));
        Assert(move is not null && move.OperationId is not null,
            "the real planner planned exactly one move for " + priorPath + " -> " + desiredPath);
        var snapshot = await site.SnapshotAsync(move!.OperationId!);
        Assert(snapshot.Kind == MoveKindFor(movedSide),
            "the planned kind follows the moved endpoint's own logical side: " + snapshot.Kind);
        Assert(SyncAuthorityOperationAllocations.TargetSide(snapshot.Kind) == OtherSide(movedSide),
            "the move targets the peer endpoint that still holds the object at the prior path");
        return snapshot;
    }

    /// <summary>The moved endpoint's own store re-paths its object (and its whole retained subtree).</summary>
    private static void RePath(MoveFixtureStore store, string priorPath, string desiredPath, bool directory)
    {
        var entry = store.Find(priorPath) ?? throw new InvalidDataException("The moved object is absent.");
        // The payload must be captured before the prior path is removed, because removing the entry removes
        // its retained bytes with it.
        var retainedPayload = directory ? null : store.PayloadOf(priorPath);
        store.Remove(priorPath);
        if (directory)
        {
            store.SeedDirectory(desiredPath, entry.Id!);
            var child = store.Find(priorPath + "/child.txt");
            if (child is not null)
            {
                var payload = store.PayloadOf(priorPath + "/child.txt");
                store.Remove(priorPath + "/child.txt");
                // A native same-endpoint path move preserves the object's own identity AND its revision: only
                // the path changes, so the retained version the planner compares stays exactly as adopted.
                store.Seed(desiredPath + "/child.txt", child.Id!, child.Revision!, payload);
            }
        }
        else
        {
            // A native same-endpoint path move preserves the object's own identity AND its revision: only the
            // path changes, so the retained version the planner compares stays exactly as adopted. Rewriting the
            // revision here would be a content change, and the planner correctly refuses to read that as a move:
            // it plans a create at the new path plus a tombstone at the old one instead.
            store.Seed(desiredPath, entry.Id!, entry.Revision!, retainedPayload!);
        }
    }

    private static async Task OrderedMatrixAsync()
    {
        await FileMoveAsync("a1", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive,
            SyncAuthorityCloudSides.LogicalLeft, 1024);
        await FileMoveAsync("a2", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive,
            SyncAuthorityCloudSides.LogicalRight, 1536);
        await FileMoveAsync("a3", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox,
            SyncAuthorityCloudSides.LogicalLeft, 2048);
        await FileMoveAsync("a4", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox,
            SyncAuthorityCloudSides.LogicalRight, 2560);
    }

    private static async Task FileMoveAsync(string slug, SyncEndpointKind leftKind, SyncEndpointKind rightKind,
        string movedSide, int size)
    {
        await using var site = await Site.CreateAsync("a-file-move-" + slug, slug, leftKind, rightKind);
        var payload = Pattern(size);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, payload);
        await AssertMoveAsync(site, operation, movedSide, Path, MovedPath, payload, false);
    }

    private static async Task EmptyFolderMoveAsync()
    {
        await using var site = await Site.CreateAsync("b-empty-folder", "b", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, SyncAuthorityCloudSides.LogicalLeft, "reports/folder",
            "archive/folder", true, Pattern(64), withChild: false);
        var target = site.StoreFor(OtherSide(SyncAuthorityCloudSides.LogicalLeft));
        var startup = await site.Runner().RecoverAsync();
        Assert(startup.Decisions.Count == 0 && target.MoveCalls == 0,
            "startup recovery admits no fresh Ready cloud folder move");
        var completed = await site.ExecuteFreshAsync(operation.OperationId, "archive/folder");
        Assert(target.MoveCalls == 1 && !target.Holds("reports/folder") && target.Holds("archive/folder"),
            "the real runner moved the folder natively on its own endpoint");
        Assert(target.Find("archive/folder")!.Kind == PilotEntryKind.Directory &&
               target.FindByIdentity("id-peer-folder") is not null,
            "the moved folder kept its exact stable identity and kind");
        // The runner reports the observed desired path, independent of retained member ID order.
        Assert(completed.Decisions.Any(item => item.RelativePath == "archive/folder" &&
               item.Kind == SyncDecisionKind.Recovered),
            "the runner reports the exact completed move member: " + string.Join(", ",
                completed.Decisions.Select(item => item.Kind + "@" + item.RelativePath)));
        await site.ObserveStoreAsync(OtherSide(SyncAuthorityCloudSides.LogicalLeft), "monitor");
        var converged = (await site.PlanAsync()).Where(item =>
            item.Kind == SyncAuthorityPlannerOutcomeKind.Move ||
            item.Kind == SyncAuthorityPlannerOutcomeKind.Operation).ToArray();
        Assert(converged.Length == 0,
            "after fresh observations of both endpoints the folder move has converged with no further effect");
        var second = await site.Runner().RecoverAsync();
        Assert(target.MoveCalls == 1 && !second.Decisions.Any(item =>
                   string.Equals(item.RelativePath, "reports/folder", StringComparison.Ordinal)),
            "a second unchanged run issues no further folder move");
        var snapshot = await site.SnapshotAsync(operation.OperationId);
        Assert(snapshot.Status == SyncAuthorityOperationStatus.Completed && snapshot.Allocations.Count == 0,
            "the folder move completed with no allocation and no transfer stage");
    }

    private static async Task FolderWithConcurrentChildEditAsync()
    {
        await using var site = await Site.CreateAsync("c-folder-child", "c", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        var payload = Pattern(320);
        var operation = await PlannedMoveAsync(site, movedSide, "reports/folder", "archive/folder", true, payload);
        var target = site.StoreFor(targetSide);
        var peerChildBytes = Encoding.UTF8.GetBytes("concurrent child edit");
        // A real concurrent child edit advances the child's own revision: keeping the earlier revision while
        // changing the bytes would be a contradiction the journal correctly refuses as a colliding version.
        target.Seed("reports/folder/child.txt", "id-peer-child", "rev-peer-child-2", peerChildBytes);
        var generationsBefore = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM scan_generation;");
        var run = await site.ExecuteFreshAsync(operation.OperationId, "archive/folder");
        Assert(target.Moves.Count(item => item.Id == "id-peer-folder") == 1,
            "the parent folder moved natively exactly once through the runner");
        Assert(target.Moves.All(item => item.Id != "id-peer-child"),
            "no still-queued descendant operation executed against the stale pre-move plan");
        Assert(target.Find("archive/folder/child.txt") is not null,
            "the adapter preserved the descendant subtree identity and path under the moved parent");
        Assert(target.PayloadOf("archive/folder/child.txt").SequenceEqual(peerChildBytes),
            "the concurrent child edit was preserved exactly and never deleted or overwritten");
        var generationsAfter = await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM scan_generation;");
        Assert(generationsAfter == generationsBefore,
            "the bound execution that completed the parent move fabricated no fresh observation of its own");
        // The stale pre-move plan is dropped rather than replayed, and it is the NEXT real observation of both
        // endpoints that replans the moved subtree instead of pretending it converged from the parent.
        await site.ObserveStoreAsync(movedSide, "monitor");
        await site.ObserveStoreAsync(targetSide, "monitor");
        var generationsAfterObservation = await ScalarAsync(site.Db.DatabasePath,
            "SELECT count(*) FROM scan_generation;");
        Assert(Convert.ToInt64(generationsAfterObservation) > Convert.ToInt64(generationsBefore),
            "a fresh observation of both endpoints replans the moved subtree before any further effect");
        var replanned = await site.PlanAsync();
        Assert(!replanned.Any(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Move),
            "the fresh plan issues no second move for the already-moved folder");
        Assert(!replanned.Any(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                   item.RelativePath.StartsWith("reports/folder", StringComparison.Ordinal)),
            "no operation is ever planned for the stale pre-move path");
        Assert(target.Uploads.Count == 0 && target.RefusedEffects == 0,
            "the folder move issued no upload, no delete and no copy");
        var snapshot = await site.SnapshotAsync(operation.OperationId);
        Assert(snapshot.Status == SyncAuthorityOperationStatus.Completed,
            "the parent folder move itself completed: " + snapshot.Status);
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM tombstone WHERE status='resolved';") ==
               "0",
            "no descendant was retired as if the whole subtree had converged from parent success");
        Assert(run.Decisions.Count > 0, "the runner reported the pass it performed");
    }

    /// <summary>
    /// The complete ordered-move battery: the retained members and destination lock, the zero-allocation shape,
    /// the single native move, the preserved stable identity, kind, size and native content, the new path, the
    /// new membership, the ordered synchronization point, no extra upload/delete/copy, a second unchanged run
    /// and a repeated call that reissues nothing.
    /// </summary>
    private static async Task AssertMoveAsync(Site site, SyncAuthorityOperationSnapshot operation,
        string movedSide, string priorPath, string desiredPath, byte[] payload, bool directory)
    {
        var targetSide = OtherSide(movedSide);
        var target = site.StoreFor(targetSide);
        var moved = site.StoreFor(movedSide);
        Assert(SyncAuthorityPairRunner.RecoveryDecisionPath(operation) == desiredPath &&
               SyncAuthorityPairRunner.RecoveryDecisionPath(operation with
                   { Members = operation.Members.Reverse().ToArray() }) == desiredPath,
            "move presentation resolves the desired path by retained member role, never row order");
        var source = operation.Members.Single(item => item.Role == "source");
        var destination = operation.Members.Single(item => item.Role == "destination");
        var destinationLock = operation.Members.Single(item => item.Role == "lock");
        Assert(source.RootBindingId == site.RootFor(movedSide) &&
               destination.RootBindingId == site.RootFor(targetSide),
            "the retained move members bind their own endpoints' authority roots");
        Assert(destinationLock.ExpectedPathKey == desiredPath &&
               destinationLock.RootBindingId == site.RootFor(targetSide),
            "the retained destination lock locks the desired path on the target root");
        Assert(operation.Allocations.Count == 0,
            "a cloud move reserves no transfer stage, transaction, recovery or upload session");
        var expectedId = target.Find(priorPath)!.Id!;
        var result = await site.MoveExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(result.Status == SyncAuthorityOperationStatus.Completed, "the cloud move completed: " + result.Reason);
        Assert(target.MoveCalls == 1 && target.Moves[0].Id == expectedId && target.Moves[0].To == desiredPath,
            "exactly one identity-preserving move reached the target adapter for the exact retained object");
        Assert(!target.Holds(priorPath) && target.Holds(desiredPath),
            "the target object left the prior path and now occupies the desired path");
        var movedEntry = target.Find(desiredPath)!;
        Assert(movedEntry.Id == expectedId,
            "the moved object kept its exact stable provider identity");
        if (!directory)
        {
            Assert(movedEntry.Kind == PilotEntryKind.File && movedEntry.Size == payload.Length &&
                   string.Equals(movedEntry.ContentHash,
                       SyncContentIdentity.Compute(target.Provider, payload), StringComparison.OrdinalIgnoreCase),
                "the moved file kept its exact kind, size and native content hash");
            Assert(!string.Equals(movedEntry.Revision, "rev-peer-1", StringComparison.Ordinal),
                "the adapter's own metadata-update contract produced the successor revision while content stayed exact");
        }
        Assert(target.Uploads.Count == 0 && target.RefusedEffects == 0,
            "the move issued no upload, no delete, no copy and no re-upload");
        Assert(moved.MoveCalls == 0 && moved.Uploads.Count == 0,
            "the endpoint that already held the object at the desired path received no effect at all");
        var completed = await site.SnapshotAsync(operation.OperationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed && completed.Attempts.Count == 1 &&
               completed.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "the journal records exactly one verified move attempt and a completed operation");
        var identityJson = target.IdentityFor(movedEntry.Id!, movedEntry.Revision);
        var contentHash = directory ? null : movedEntry.ContentHash;
        var versionId = SyncAuthorityCloudExecution.Id("version-" + targetSide, completed.ObjectId!, identityJson,
            contentHash ?? "none", movedEntry.Size.ToString(CultureInfo.InvariantCulture));
        var successorVersion = await site.Db.LoadObjectVersionAsync(versionId);
        Assert(string.Equals(successorVersion.Side, targetSide, StringComparison.Ordinal) &&
               string.Equals(successorVersion.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase) &&
               successorVersion.Size == movedEntry.Size,
            "the successor native identity and version are registered with the CC2C serializer on the target's own logical side");
        var retainedPredecessor = await site.Db.LoadObjectVersionAsync(destination.ObjectVersionId!);
        Assert(string.Equals(retainedPredecessor.ContentHash,
                   directory ? null : SyncContentIdentity.Compute(target.Provider, payload),
                   StringComparison.OrdinalIgnoreCase),
            "the prior target version row is retained unchanged as history");
        var membershipId = SyncAuthorityCloudExecution.Id("membership-cloud-effect", operation.OperationId,
            desiredPath, versionId);
        Assert(await site.Db.LoadMembershipGenerationAsync(membershipId) >= 1,
            "the successor membership is registered at the desired path on the target root");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        var point = points.Single(item =>
            string.Equals(item.ObjectId, completed.ObjectId, StringComparison.Ordinal));
        var targetIsLeft = targetSide == SyncAuthorityCloudSides.LogicalLeft;
        Assert(string.Equals(targetIsLeft ? point.LocalObjectVersionId : point.DropboxObjectVersionId, versionId,
                   StringComparison.Ordinal) &&
               string.Equals(targetIsLeft ? point.DropboxObjectVersionId : point.LocalObjectVersionId,
                   source.ObjectVersionId, StringComparison.Ordinal),
            "the ordered synchronization point puts the successor in the target slot and keeps the source in its own slot");
        await site.ObserveStoreAsync(movedSide, "monitor");
        await site.ObserveStoreAsync(targetSide, "monitor");
        var after = (await site.PlanAsync()).Where(item => item.Kind is SyncAuthorityPlannerOutcomeKind.Move or
            SyncAuthorityPlannerOutcomeKind.Operation).ToArray();
        Assert(after.Length == 0,
            "a second unchanged run plans no further move and no further operation after fresh observations");
        var repeat = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(repeat.Status == SyncAuthorityOperationStatus.Completed && repeat.EffectDispatched == false &&
               target.MoveCalls == 1,
            "a repeated call on the completed move is a no-op that reissues nothing");
    }

    private static async Task PlanAuthorityRefusalsAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("d-refusals", "d", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(768));
        var target = site.StoreFor(targetSide);
        await using var other = await Site.CreateAsync("d-other", "dother", SyncEndpointKind.GoogleDrive,
            SyncEndpointKind.Dropbox);
        await ExpectRefusalAsync(() => site.ForeignExecutor(other).ExecuteAsync(operation.OperationId, site.Worker),
            "a move operation of another pair");
        Assert(target.MoveCalls == 0, "the foreign pair issued no move request");

        await using var drift = await Site.CreateAsync("d-source-drift", "dsd", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var driftOperation = await PlannedMoveAsync(drift, movedSide, Path, MovedPath, false, Pattern(896, 11));
        var driftTarget = drift.StoreFor(targetSide);
        drift.StoreFor(movedSide).Seed(MovedPath, "id-moved-file", "rev-drifted", Pattern(960, 13));
        await ExpectRefusalAsync(() => drift.MoveExecutor().ExecuteAsync(driftOperation.OperationId,
            drift.Worker), "a moved-source that drifted after the plan");
        Assert(driftTarget.MoveCalls == 0, "the drifted source issued no move request");

        await using var prior = await Site.CreateAsync("d-prior-drift", "dpd", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var priorOperation = await PlannedMoveAsync(prior, movedSide, Path, MovedPath, false, Pattern(1024, 17));
        var priorTarget = prior.StoreFor(targetSide);
        priorTarget.Seed(Path, "id-peer-file", "rev-concurrent", Pattern(1088, 19));
        await ExpectRefusalAsync(() => prior.MoveExecutor().ExecuteAsync(priorOperation.OperationId,
            prior.Worker), "a target predecessor that drifted after the plan");
        Assert(priorTarget.MoveCalls == 0 && priorTarget.PayloadOf(Path).SequenceEqual(Pattern(1088, 19)),
            "the drifted predecessor issued no move request and its content was left untouched");

        foreach (var (collisionPath, slug) in new[] { (MovedPath, "dc1"), (MovedPath.ToUpperInvariant(), "dc2") })
        {
            await using var collision = await Site.CreateAsync("d-collision-" + slug, slug,
                SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
            var collisionOperation = await PlannedMoveAsync(collision, movedSide, Path, MovedPath, false,
                Pattern(1152, 23));
            var collisionTarget = collision.StoreFor(targetSide);
            collisionTarget.Seed(collisionPath, "id-occupying-file", "rev-occupying-1", Pattern(1216, 29));
            await ExpectRefusalAsync(() => collision.MoveExecutor().ExecuteAsync(collisionOperation.OperationId,
                collision.Worker), "an occupied move destination " + collisionPath);
            Assert(collisionTarget.MoveCalls == 0 &&
                   collisionTarget.PayloadOf(collisionPath).SequenceEqual(Pattern(1216, 29)),
                "the occupying object was never touched for " + collisionPath);
        }

        await using var kind = await Site.CreateAsync("d-kind", "dk", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        kind.StoreFor(movedSide).Seed(Path, "id-vanished-file", "rev-vanished-1", Pattern(1280, 31));
        await kind.ObserveStoreAsync(movedSide, "initial");
        await kind.ObserveStoreAsync(targetSide, "initial");
        var create = (await kind.PlanAsync()).Single(item => item.OperationId is not null);
        await ExpectRefusalAsync(() => kind.MoveExecutor().ExecuteAsync(create.OperationId!, kind.Worker),
            "a create operation routed through the shared cloud move executor");
        Assert(kind.StoreFor(targetSide).MoveCalls == 0 && kind.StoreFor(targetSide).Uploads.Count == 0,
            "the foreign kind issued no target request");
    }

    private static async Task UnsupportedMoveAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("e-unsupported", "e", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(1344));
        var target = site.StoreFor(targetSide);
        target.RefuseMove = true;
        var notApplied = await site.MoveExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(notApplied.Status == SyncAuthorityOperationStatus.Ready && !notApplied.EffectDispatched,
            "an adapter without the capability is a conclusive non-application: " + notApplied.Reason);
        Assert(target.MoveCalls == 1 && target.Holds(Path) && !target.Holds(MovedPath),
            "the unsupported move reached the adapter's own contract once and moved nothing");
        var snapshot = await site.SnapshotAsync(operation.OperationId);
        Assert(snapshot.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "the conclusive non-application is durably recorded on its own attempt");
        Assert(await ScalarAsync(site.Db.DatabasePath,
                   "SELECT count(*) FROM effect_receipt WHERE certainty='known_not_applied';") == "1",
            "exactly one durable known-not-applied receipt exists, written at the adapter's own boundary");
        await ExpectAnyRefusalAsync(() => site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker),
            "reconciling a move that was never dispatched");
        Assert(await ScalarAsync(site.Db.DatabasePath,
                   "SELECT count(*) FROM effect_receipt WHERE certainty='known_not_applied';") == "1" &&
               target.MoveCalls == 1,
            "reconciliation synthesised no non-application from the listing and issued no second move");
    }

    private static async Task LostMoveResponseAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("f-lost-move", "f", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(1408));
        var target = site.StoreFor(targetSide);
        target.ApplyThenFailOnMove = true;
        var unknown = await site.MoveExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && unknown.EffectDispatched,
            "a move applied with a lost response is unknown: " + unknown.Reason);
        Assert(target.MoveCalls == 1 && target.Holds(MovedPath) && !target.Holds(Path),
            "the applied move reached the target once and its object now sits at the desired path");
        var reconciled = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown && !reconciled.EffectDispatched &&
               target.MoveCalls == 1,
            "the exact destination alone never commits and never repeats the move: " + reconciled.Reason);
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM history_event WHERE operation_id='" +
                   operation.OperationId + "' AND event_kind='effect_committed';") == "0",
            "the unknown move committed no effect at all");
        var beforeRepeat = await site.SnapshotAsync(operation.OperationId);
        var repeated = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        var afterRepeat = await site.SnapshotAsync(operation.OperationId);
        Assert(repeated.Status == SyncAuthorityOperationStatus.EffectUnknown && !repeated.EffectDispatched &&
               afterRepeat.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               afterRepeat.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown) &&
               afterRepeat.Attempts.Count == beforeRepeat.Attempts.Count && target.MoveCalls == 1,
            "repeated verification preserves the unknown move and original attempt without a blind replay");

        // CC3E-1: a request the adapter refused but whose response was lost is UNKNOWN, never a conclusive
        // non-application. Only the adapter's own typed contract (NotSupportedException) proves non-application,
        // so a transport-shaped failure after the request boundary must never unlock a retry.
        await using var refusedSite = await Site.CreateAsync("f-lost-move-refused", "fr",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var refusedOperation = await PlannedMoveAsync(refusedSite, movedSide, Path, MovedPath, false,
            Pattern(1472));
        var refusedTarget = refusedSite.StoreFor(targetSide);
        refusedTarget.RefuseMoveWithoutApply = true;
        var refusedUnknown = await refusedSite.MoveExecutor().ExecuteAsync(refusedOperation.OperationId,
            refusedSite.Worker);
        Assert(refusedUnknown.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               refusedUnknown.EffectDispatched,
            "a move refused with a lost response is unknown, never a conclusive non-application: " +
            refusedUnknown.Reason);
        Assert(refusedTarget.MoveCalls == 1 && refusedTarget.Holds(Path) && !refusedTarget.Holds(MovedPath),
            "the lost refusal reached the adapter once and left the object exactly at its prior path");
        Assert(await ScalarAsync(refusedSite.Db.DatabasePath, "SELECT count(*) FROM effect_receipt WHERE " +
                   "certainty='known_not_applied';") == "0",
            "a lost refusal wrote no conclusive non-application receipt");
        var refusedReconciled = await refusedSite.MoveExecutor().ReconcileAsync(refusedOperation.OperationId,
            refusedSite.Worker);
        Assert(refusedReconciled.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               !refusedReconciled.EffectDispatched && refusedTarget.MoveCalls == 1 &&
               refusedTarget.Holds(Path) && !refusedTarget.Holds(MovedPath),
            "the unresolved refused move was never repeated and the target was not touched: " +
            refusedReconciled.Reason);
        var refusedRetained = await refusedSite.SnapshotAsync(refusedOperation.OperationId);
        Assert(refusedRetained.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               refusedRetained.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown),
            "the lost refusal retains the unresolved operation and effect attempt");
        Assert(await ScalarAsync(refusedSite.Db.DatabasePath, "SELECT count(*) FROM history_event WHERE " +
                   "operation_id='" + refusedOperation.OperationId + "' AND " +
                   "event_kind='effect_committed';") == "0",
            "the lost refusal committed nothing");
    }

    private static async Task KnownReceiptRestartAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("g-receipt-restart", "g", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(1472));
        var target = site.StoreFor(targetSide);
        var crashed = false;
        SyncAuthorityCloudMoveExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-receipt-before-readback" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the known-success move receipt");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.MoveExecutor().ExecuteAsync(operation.OperationId, site.Worker),
                "a crash after the known-success move receipt");
        }
        finally
        {
            SyncAuthorityCloudMoveExecutor.PhaseForTest = null;
        }
        Assert(crashed && target.MoveCalls == 1 && target.Holds(MovedPath),
            "the pre-commit crash was reached after exactly one move and the object is at the desired path");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.EffectObserved,
            "the crashed move retains its post-receipt journal state");
        var reconciled = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.Completed,
            "the retained receipt plus the exact readback committed the move once: " + reconciled.Reason);
        Assert(target.MoveCalls == 1, "the resumed commit issued no second move");
        var final = await site.SnapshotAsync(operation.OperationId);
        Assert(final.Status == SyncAuthorityOperationStatus.Completed && final.Attempts.Count == 1 &&
               final.Attempts.Single().Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "the resumed move committed its one retained attempt exactly once");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Count(item => string.Equals(item.ObjectId, final.ObjectId, StringComparison.Ordinal)) == 1,
            "the resumed move committed exactly one synchronization point");
        var again = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(again.Status == SyncAuthorityOperationStatus.Completed && target.MoveCalls == 1,
            "a completed move is a no-op that reissues nothing");
    }

    private static async Task CommittedRestartAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("h-committed-restart", "h", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(1536));
        var target = site.StoreFor(targetSide);
        var crashed = false;
        SyncAuthorityCloudMoveExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-commit-before-completion" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the move commit boundary");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => site.MoveExecutor().ExecuteAsync(operation.OperationId, site.Worker),
                "a crash after the move commit boundary");
        }
        finally
        {
            SyncAuthorityCloudMoveExecutor.PhaseForTest = null;
        }
        var committed = await site.SnapshotAsync(operation.OperationId);
        Assert(crashed && committed.Status == SyncAuthorityOperationStatus.Committed,
            "the crashed move retains the committed boundary: " + committed.Status);
        Assert(committed.Allocations.Count == 0,
            "the committed move still carries no allocation and created no private object");
        var resumed = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(resumed.Status == SyncAuthorityOperationStatus.Completed && !resumed.EffectDispatched,
            "the committed move resumed only its own completion: " + resumed.Reason);
        Assert(target.MoveCalls == 1 && target.Uploads.Count == 0 && target.RefusedEffects == 0,
            "the resumed completion issued no second move, no upload and no delete");
        Assert((await site.SnapshotAsync(operation.OperationId)).Status ==
               SyncAuthorityOperationStatus.Completed,
            "the resumed committed move reached completion");
    }

    private static async Task OtherRoutesPreservedAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("i-other-routes", "i", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(1600));
        var target = site.StoreFor(targetSide);
        var run = await site.ExecuteFreshAsync(operation.OperationId, MovedPath);
        Assert(run.Decisions.Any(item => item.RelativePath == MovedPath) && target.MoveCalls == 1,
            "the real runner routed the move through the cloud dispatch");
        const string createdPath = "reports/created.txt";
        site.StoreFor(movedSide).SeedText(createdPath, "id-create-file", "rev-create-1", "created after the move");
        await site.ObserveStoreAsync(movedSide, "monitor");
        await site.ObserveStoreAsync(targetSide, "monitor");
        var created = (await site.PlanAsync()).Single(item => item.OperationId is not null &&
            item.RelativePath == createdPath);
        var createRun = await site.ExecuteFreshAsync(created.OperationId!, createdPath);
        Assert(createRun.Decisions.Single(item => item.RelativePath == createdPath).Kind ==
               SyncDecisionKind.Uploaded,
            "the same runner still routes the create engine");
        Assert(target.Uploads.Count == 1 && target.Holds(createdPath),
            "the create engine reached its own target adapter through the same runner");
        Assert(target.MoveCalls == 1,
            "routing the create engine issued no further move and no delete");
        await ExpectRefusalAsync(() => site.MoveExecutor().ExecuteAsync(created.OperationId!, site.Worker),
            "a create operation routed through the shared cloud move executor");
    }

    // ----- expectation helpers ------------------------------------------------------------------------

    /// <summary>
    /// CC3E-C1: a move whose adapter advanced the revision and whose known-success receipt arrived before the
    /// commit must resume the successor from the ACTUAL verified moved identity - never from the pre-move
    /// predecessor's older revision - on both typed target sides, and a duplicate entry at the desired path must
    /// refuse rather than falsely complete.
    /// </summary>
    private static async Task RevisionAdvancingRestartAsync()
    {
        foreach (var movedSide in new[]
                 {
                     SyncAuthorityCloudSides.LogicalLeft, SyncAuthorityCloudSides.LogicalRight
                 })
        {
            var slug = movedSide == SyncAuthorityCloudSides.LogicalLeft ? "j1" : "j2";
            await using var site = await Site.CreateAsync("j-revision-restart-" + slug, slug,
                SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
            var payload = Pattern(movedSide == SyncAuthorityCloudSides.LogicalLeft ? 1664 : 1792, 53);
            var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, payload);
            var targetSide = OtherSide(movedSide);
            var target = site.StoreFor(targetSide);
            var crashed = false;
            SyncAuthorityCloudMoveExecutor.PhaseForTest = phase =>
            {
                if (phase == "after-receipt-before-readback" && !crashed)
                {
                    crashed = true;
                    throw new InvalidOperationException("scripted crash after the known-success move receipt");
                }
            };
            try
            {
                await ExpectAnyRefusalAsync(() => site.MoveExecutor().ExecuteAsync(operation.OperationId,
                    site.Worker), "a crash after the known-success move receipt for " + slug);
            }
            finally
            {
                SyncAuthorityCloudMoveExecutor.PhaseForTest = null;
            }
            Assert(crashed && target.MoveCalls == 1 && target.Holds(MovedPath) && !target.Holds(Path),
                "the receipted move applied exactly once before the crash for " + slug);
            var movedEntry = target.Find(MovedPath)!;
            Assert(!string.Equals(movedEntry.Revision, "rev-peer-1", StringComparison.Ordinal),
                "the scripted adapter advanced the revision across the move for " + slug);
            // Reopen the database exactly as a restart would, then reconcile through a fresh engine instance.
            var reopened = new SyncAuthorityDatabase(site.Db.DatabasePath);
            var engine = new SyncAuthorityCloudMoveExecutor(reopened, site.Context());
            var reconciled = await engine.ReconcileAsync(operation.OperationId, site.Worker + "-restart");
            Assert(reconciled.Status == SyncAuthorityOperationStatus.Completed && target.MoveCalls == 1,
                "the restart resumed the commit without another move for " + slug + ": " + reconciled.Reason);
            var final = await reopened.LoadOperationAsync(operation.OperationId);
            var identityJson = target.IdentityFor(movedEntry.Id!, movedEntry.Revision);
            var versionId = SyncAuthorityCloudExecution.Id("version-" + targetSide, final.ObjectId!,
                identityJson, movedEntry.ContentHash ?? "none",
                movedEntry.Size.ToString(CultureInfo.InvariantCulture));
            var successor = await reopened.LoadObjectVersionAsync(versionId);
            Assert(string.Equals(successor.Side, targetSide, StringComparison.Ordinal) &&
                   string.Equals(successor.ContentHash, movedEntry.ContentHash,
                       StringComparison.OrdinalIgnoreCase),
                "the resumed successor version carries the ACTUAL verified moved identity and content for " + slug);
            var points = await reopened.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
            var point = points.Single(item =>
                string.Equals(item.ObjectId, final.ObjectId, StringComparison.Ordinal));
            var targetIsLeft = targetSide == SyncAuthorityCloudSides.LogicalLeft;
            var publishedVersionId = targetIsLeft ? point.LocalObjectVersionId : point.DropboxObjectVersionId;
            Assert(string.Equals(publishedVersionId, versionId, StringComparison.Ordinal),
                "the resumed sync point publishes the verified successor version for " + slug);
            var predecessorIdentityJson = target.IdentityFor(movedEntry.Id!, "rev-peer-1");
            var buggyVersionId = SyncAuthorityCloudExecution.Id("version-" + targetSide, final.ObjectId!,
                predecessorIdentityJson, movedEntry.ContentHash ?? "none",
                movedEntry.Size.ToString(CultureInfo.InvariantCulture));
            Assert(!string.Equals(publishedVersionId, buggyVersionId, StringComparison.Ordinal),
                "the pre-move predecessor revision was never published as the new sync-point version for " + slug);
        }
        // A duplicate entry at the desired path must refuse rather than falsely complete.
        await using (var duplicate = await Site.CreateAsync("j-duplicate-desired", "jd", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive))
        {
            var movedSide = SyncAuthorityCloudSides.LogicalLeft;
            var targetSide = OtherSide(movedSide);
            var payload = Pattern(1920, 59);
            var operation = await PlannedMoveAsync(duplicate, movedSide, Path, MovedPath, false, payload);
            var target = duplicate.StoreFor(targetSide);
            var crashed = false;
            SyncAuthorityCloudMoveExecutor.PhaseForTest = phase =>
            {
                if (phase == "after-receipt-before-readback" && !crashed)
                {
                    crashed = true;
                    throw new InvalidOperationException("scripted crash after the known-success move receipt");
                }
            };
            try
            {
                await ExpectAnyRefusalAsync(() => duplicate.MoveExecutor().ExecuteAsync(operation.OperationId,
                    duplicate.Worker), "a crash after the known-success move receipt for the duplicate case");
            }
            finally
            {
                SyncAuthorityCloudMoveExecutor.PhaseForTest = null;
            }
            Assert(crashed && target.Holds(MovedPath), "the duplicate case reached its receipt before the crash");
            target.ExtraListingEntry = new PilotRemoteEntry(MovedPath, PilotEntryKind.File,
                "id-duplicate-desired", "rev-duplicate-1", payload.Length,
                SyncContentIdentity.Compute(target.Provider, payload));
            var reopened = new SyncAuthorityDatabase(duplicate.Db.DatabasePath);
            var engine = new SyncAuthorityCloudMoveExecutor(reopened, duplicate.Context());
            var reconciled = await engine.ReconcileAsync(operation.OperationId, duplicate.Worker + "-restart");
            Assert(reconciled.Status != SyncAuthorityOperationStatus.Completed &&
                   reconciled.Status != SyncAuthorityOperationStatus.Committed && target.MoveCalls == 1,
                "a duplicate desired-path identity refuses rather than falsely completing: " + reconciled.Reason);
            Assert((await reopened.LoadOperationAsync(operation.OperationId)).Status !=
                   SyncAuthorityOperationStatus.Completed,
                "the duplicate destination never reached completion and issued no second move");
            Assert(!target.Holds(Path), "the duplicate destination refusal reissued no move");
            target.ExtraListingEntry = null;
        }
    }

    /// <summary>
    /// CC3E-C1: the adapter's own returned shape is never trusted on its own. An adapter that really moved
    /// the object but reported a different object identity is refused: the outcome stays unresolved with zero
    /// repeated move, no known-success receipt and nothing committed, even though the exact moved identity is
    /// now visible at the desired path.
    /// </summary>
    private static async Task ContradictedReadbackAsync()
    {
        var movedSide = SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = OtherSide(movedSide);
        await using var site = await Site.CreateAsync("k-contradicted-readback", "k",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var operation = await PlannedMoveAsync(site, movedSide, Path, MovedPath, false, Pattern(1600));
        var target = site.StoreFor(targetSide);
        target.ContradictoryMoveResult = true;
        var unknown = await site.MoveExecutor().ExecuteAsync(operation.OperationId, site.Worker);
        Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown && unknown.EffectDispatched,
            "a contradicting adapter shape is unknown, never a completion: " + unknown.Reason);
        Assert(target.MoveCalls == 1 && target.Holds(MovedPath) && !target.Holds(Path),
            "the scripted adapter really moved the object once, so what is proved is the engine's own refusal");
        Assert(await ScalarAsync(site.Db.DatabasePath,
                   "SELECT count(*) FROM effect_receipt WHERE certainty='known_success';") == "0",
            "the contradicted shape wrote no known-success receipt");
        Assert(await ScalarAsync(site.Db.DatabasePath,
                   "SELECT count(*) FROM effect_receipt WHERE certainty='unknown';") == "1",
            "exactly one durable unknown receipt records the contradiction");
        var reconciled = await site.MoveExecutor().ReconcileAsync(operation.OperationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown && !reconciled.EffectDispatched &&
               target.MoveCalls == 1,
            "the visible moved identity alone never commits a contradicted move: " + reconciled.Reason);
        Assert(await ScalarAsync(site.Db.DatabasePath, "SELECT count(*) FROM history_event WHERE " +
                   "operation_id='" + operation.OperationId + "' AND event_kind='effect_committed';") == "0",
            "nothing was committed for the contradicted move");
        var retained = await site.SnapshotAsync(operation.OperationId);
        Assert(retained.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               retained.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown),
            "the contradicted move retains its unresolved state instead of a fabricated completion");
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

    private static async Task<string?> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
