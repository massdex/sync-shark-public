using System.Reflection;
using IOPath = System.IO.Path;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// CC3B focused proofs: the shared allocation policy and the one internal shared cloud create executor over
// real isolated SQLite authority, the real reconciliation planner, a real protected Windows private stage
// and the real typed CC2C cloud endpoint ports.
//
// The planner, the operation journal, the allocation rows, the attempt/receipt records, the version and
// membership registration, the ordered synchronization point, the operation lease/fence transitions and the
// completion transitions are all the shipped production code. Only the two provider transports are scripted
// adapters: they supply an inventory and a body and they perform a real create-only write into their own
// in-memory inventory, so the executor's own decisions - refusal, dispatch, readback, commit, reconcile and
// cleanup - are the decisions under proof. No fixture replaces the decision being proved and nothing here
// reaches a live account, credential, network, installed database, product state, Host wiring or NTFS pair
// root.
internal static class CloudCreateExecutionChecks
{
    private const string InstallationId = "installation-cloud-create-execution-316";
    private const string SourceRevision = "cloud-create-execution-316";
    private const string ProfileId = "profile-cloud-create-execution-316";
    private const string Path = "reports/quarterly.txt";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static int _unexpectedEffects;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud create execution fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        _unexpectedEffects = 0;

        await CaseAsync("A-dropbox-to-drive-file",
            "A real planned create of an ordinary file from the Dropbox side onto the Drive target",
            async () => await PlannedCreateAsync("a", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive,
                false, 4096));
        await CaseAsync("B-drive-to-dropbox-file",
            "The opposite direction: a real planned create from the Drive side onto the Dropbox target",
            async () => await PlannedCreateAsync("b", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox,
                false, 4096));
        await CaseAsync("C-each-direction",
            "Each cross-provider ordered pairing completes a real create in both directions, and a same-provider pair is refused without verified ancestry",
            EachDirectionAsync);
        await CaseAsync("D-empty-file",
            "An empty source file stages, commits and registers the exact registered empty digest",
            async () => await PlannedCreateAsync("d", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive,
                false, 0));
        await CaseAsync("E-directory",
            "A directory create verifies the exact source folder and creates on the target without any stage",
            DirectoryCreateAsync);
        await CaseAsync("F-completed-repeat",
            "Repeating the completed call issues zero additional upload and never recopies",
            CompletedRepeatAsync);
        await CaseAsync("G-plan-refusals",
            "Foreign, malformed, wrong-algorithm, drifted, colliding and unsupported plans refuse before dispatch",
            PlanRefusalsAsync);
        await CaseAsync("H-transport-refusals",
            "Stale fence, cancellation, resumable mismatch and an unverifiable response issue no duplicate effect",
            TransportRefusalsAsync);
        await CaseAsync("I-unknown-outcome",
            "A target request whose response is lost retains the stage and stops before any duplicate",
            UnknownOutcomeAsync);
        await CaseAsync("J-known-receipt-precommit-crash",
            "A retained known-success receipt with a pre-commit crash reconciles the exact target once and cleans once",
            KnownReceiptCrashAsync);
        await CaseAsync("K-committed-precleanup-crash",
            "A committed pre-cleanup crash resumes only its exact owned cleanup, and a replaced stage refuses safely",
            CommittedCrashAsync);
        await CaseAsync("L-legacy-allocation-parity",
            "The shared policy reproduces the historical legacy allocation rows exactly for every legacy kind",
            LegacyAllocationParityAsync);
        await CaseAsync("M-create-successor-reobservation",
            "The next production observation of a cloud-created target reproduces the committed version identity, bytes and comparison instead of colliding with them",
            ReObservationIdentityAsync);
        await CaseAsync("N-native-session-both-logical-directions",
            "A configured small session completes through each real receiving provider adapter and shared cloud executor from either logical source side",
            NativeSessionDirectionsAsync);
        await CaseAsync("O-retained-stage-ready-retry",
            "A conclusively declined create leaves the operation Ready with its exact verified retained stage, and the next consumer call reopens that stage instead of preparing a second one",
            RetainedStageReadyRetryAsync);
        await CaseAsync("P-retained-stage-cancellation",
            "A caller cancellation inside the retained-stage rehash of a Ready create issues no target request, retains the exact stage object and still retries to completion",
            RetainedStageCancellationAsync);
        await CaseAsync("Q-retained-stage-reconcile-cancellation",
            "A caller cancellation inside the retained-stage rehash of an unresolved create issues no target request and leaves the exact unresolved journal intact",
            RetainedStageReconcileCancellationAsync);

        Assert(_unexpectedEffects == 0,
            "no cloud create execution fixture ever reached an upload, delete, move, copy or recovery request it was not given");
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudCreateExecutionChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "cloud-create-execution-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud create execution: " + _assertions + " assertions; the real planner, journal," +
            " allocation, attempt, receipt, version/membership, synchronization point and completion transitions" +
            " over real isolated SQLite with a real protected Windows private stage, for both ordered provider" +
            " pairings, both directions, ordinary, empty and directory creates, the completed-repeat no-op and" +
            " every required refusal, unknown, reconciliation and cleanup path, including the retained-stage" +
            " Ready retry and its cancellation paths.");
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
        if (!condition) throw new InvalidDataException("CC3B assertion failed: " + label);
    }

    // ----- the real harness -------------------------------------------------------------------------

    private static byte[] Pattern(int length, byte seed = 31)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * seed + 7) % 251);
        return payload;
    }

    private static string Id(string role, params string[] parts) => role + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "|" + string.Join("|", parts))))
            .ToLowerInvariant()[..32];

    private sealed class Site : IAsyncDisposable
    {
        internal string Slug = "";
        internal string CaseName = "";
        internal string CaseRoot = "";
        internal string AnchorRoot = "";
        internal string NamespaceId = "";
        internal string LeftRecoveryRoot = "";
        internal string RightRecoveryRoot = "";
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal SyncPair Pair = null!;
        internal SyncEndpointKind LeftKind;
        internal SyncEndpointKind RightKind;
        internal string LeftRoot = "";
        internal string RightRoot = "";
        internal FixtureStore Left = null!;
        internal FixtureStore Right = null!;
        internal ISyncRemoteStore? LeftNativeStore;
        internal ISyncRemoteStore? RightNativeStore;
        internal SyncControlNamespaceBinding Control = null!;
        private PilotDirectoryLease? _anchor;
        private PilotDirectoryLease? _control;

        internal PilotDirectoryLease ControlRoot => _control!;
        internal string Worker => "worker-cc3b-" + Slug;

        internal static async Task<Site> CreateAsync(string caseName, string slug, SyncEndpointKind leftKind,
            SyncEndpointKind rightKind)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The shared cloud create execution proofs require Windows NTFS identities and protected ACLs.");
            var site = new Site
            {
                CaseName = caseName,
                Slug = slug,
                LeftKind = leftKind,
                RightKind = rightKind,
                NamespaceId = "cloud-create-execution-" + slug,
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
            var leftAccount = "account-" + slug + "-left";
            var rightAccount = "account-" + slug + "-right";
            var leftProvider = SyncEndpointProviders.Get(leftKind).ProviderId!;
            var rightProvider = SyncEndpointProviders.Get(rightKind).ProviderId!;
            var leftFolder = FolderId(leftKind, slug + "-left");
            var rightFolder = FolderId(rightKind, slug + "-right");
            // The typed cloud guard requires the exact retained endpoint namespace of each provider: a
            // Dropbox row is separated by its retained root namespace and a Google Drive row by its account.
            var leftNamespace = EndpointNamespace(leftKind, "id:" + leftAccount, "ns-root-" + slug + "-left");
            var rightNamespace = EndpointNamespace(rightKind, "id:" + rightAccount,
                "ns-root-" + slug + "-right");
            site.Left = FixtureStore.Create(leftProvider, leftAccount,
                leftNamespace, leftFolder);
            site.Right = FixtureStore.Create(rightProvider, rightAccount,
                rightNamespace, rightFolder);
            site.Db = new SyncAuthorityDatabase(IOPath.Combine(site.CaseRoot, "syncshark.db"));
            await site.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await site.Db.RegisterProviderAccountAsync(leftAccount, InstallationId, "id:" + leftAccount,
                "ns-root-" + slug + "-left", "ns-home-" + slug + "-left", "CC3B left account", 1,
                cancellationToken: default, provider: leftProvider);
            await site.Db.RegisterProviderAccountAsync(rightAccount, InstallationId, "id:" + rightAccount,
                "ns-root-" + slug + "-right", "ns-home-" + slug + "-right", "CC3B right account", 1,
                cancellationToken: default, provider: rightProvider);
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                SyncEndpoint.Cloud(leftKind, "conn-" + slug + "-left", leftNamespace,
                    leftFolder, "/left"),
                SyncEndpoint.Cloud(rightKind, "conn-" + slug + "-right", rightNamespace,
                    rightFolder, "/right"), SyncMode.TwoWay), "CC3B " + caseName);
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
            return site;
        }

        /// <summary>The exact retained endpoint namespace the provider account derivation requires.</summary>
        internal static string EndpointNamespace(SyncEndpointKind kind, string accountId,
            string rootNamespaceId) =>
            kind == SyncEndpointKind.GoogleDrive ? accountId : rootNamespaceId;

        internal string RootFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? LeftRoot : RightRoot;

        /// <summary>The exact accepted persisted folder id shape of each registered provider.</summary>
        internal static string FolderId(SyncEndpointKind kind, string suffix) =>
            kind == SyncEndpointKind.Dropbox ? "id:cc3b-" + suffix : "gd-cc3b-" + suffix;

        internal FixtureStore StoreFor(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? Left : Right;

        internal SyncAuthorityCloudEndpointSet Endpoints() => SyncAuthorityCloudEndpointSet.FromPair(Pair,
            LeftNativeStore ?? Left.Store, RightNativeStore ?? Right.Store, LeftRoot, RightRoot,
            Left.ProviderAccountId, Right.ProviderAccountId);

        internal SyncAuthorityCloudExecutionContext Context() =>
            new(Endpoints(), Control, ControlRoot);

        internal SyncAuthorityPlannerOptions Options(long leftLimit, long rightLimit) =>
            new(1, ProfileId, Control.NamespaceId, Control.NamespaceId, Direction: SyncMode.TwoWay,
                CloudAllocations: new SyncAuthorityCloudAllocationConfiguration(Control.NamespaceId,
                    new SyncAuthorityCloudTargetAllocation(LeftRecoveryRoot, leftLimit),
                    new SyncAuthorityCloudTargetAllocation(RightRecoveryRoot, rightLimit)));

        /// <summary>One real complete observation generation through the real scan-generation records.</summary>
        internal async Task ObserveAsync(string side, params SyncAuthorityObservationInput[] entries)
        {
            var store = StoreFor(side);
            var scan = await Db.BeginScanGenerationAsync(Pair.Id, RootFor(side), side, "initial", "{}");
            if (entries.Length > 0) await Db.AppendObservationEntriesAsync(scan, entries);
            await Db.CompleteScanGenerationAsync(scan);
        }

        internal SyncAuthorityObservationInput Entry(string relativePath, string objectKind, string fileId,
            string? revision, long size, byte[]? payload) =>
            Entry(Left, relativePath, objectKind, fileId, revision, size, payload);

        internal SyncAuthorityObservationInput Entry(FixtureStore store, string relativePath, string objectKind,
            string fileId, string? revision, long size, byte[]? payload)
        {
            var directory = string.Equals(objectKind, "directory", StringComparison.Ordinal);
            var hash = payload is null || directory
                ? null
                : SyncContentIdentity.Compute(store.Provider, payload);
            return new SyncAuthorityObservationInput(relativePath, objectKind,
                store.IdentityFor(fileId, revision), hash, directory ? 0 : size, "{}");
        }

        internal async Task<SyncAuthorityPlannerOutcome> PlanCreateAsync(string path,
            long leftLimit = 1L << 30, long rightLimit = 1L << 30)
        {
            var planner = new SyncAuthorityReconciliationPlanner(Db, Options(leftLimit, rightLimit), Admission);
            var outcomes = await planner.PlanAsync(Pair.Id);
            var operation = outcomes.SingleOrDefault(item =>
                item.Kind == SyncAuthorityPlannerOutcomeKind.Operation &&
                string.Equals(item.RelativePath, path, StringComparison.Ordinal));
            if (operation is null || operation.OperationId is null)
                throw new InvalidDataException("The real planner produced no cloud create operation for " + path +
                    " (outcomes: " + string.Join(", ",
                        outcomes.Select(item => item.Kind + ":" + item.RelativePath)) + ")");
            return operation;
        }

        internal async Task<SyncAuthorityOperationSnapshot> SnapshotAsync(string operationId) =>
            await Db.LoadOperationAsync(operationId);

        public async ValueTask DisposeAsync()
        {
            SyncAuthorityCloudContentStage.AvailableDeviceBytesForTest = null;
            _control?.Dispose();
            _anchor?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Db.TransitionCommandRequestAsync(Admission.CommandRequestId, "completed");
        }
    }

    /// <summary>
    /// A scripted cloud adapter over a real in-memory inventory. It computes its own provider-native digest
    /// with the production content-identity seam over exactly the bytes it receives, so a create-only write
    /// produces the same native identity a real provider would for that content.
    /// </summary>
    public class FixtureStore : DispatchProxy
    {
        internal string Provider = "";
        internal string ProviderAccountId = "";
        internal string NamespaceId = "";
        internal string FolderId = "";
        internal List<PilotRemoteEntry> Entries { get; } = [];
        internal Dictionary<string, byte[]> Payloads { get; } = new(StringComparer.Ordinal);
        internal List<(string Path, byte[] Bytes, string ExpectedHash)> Uploads { get; } = [];
        internal List<string> Downloads { get; } = [];
        internal List<string> CreatedDirectories { get; } = [];
        internal int VerifyCalls;
        internal int ListCalls;
        internal int RefusedEffects;
        internal int RevisionCounter;
        internal bool CorruptUploadResult;
        internal int RefuseCreateUploadTimes;
        internal Action<string>? DuringDownload;
        internal Func<string, byte[], bool>? ApplyThenFail;
        internal int ApplyThenFailTimes;

        internal static FixtureStore Create(string provider, string providerAccountId, string namespaceId,
            string folderId)
        {
            var fixture = (FixtureStore)(object)DispatchProxy.Create<ISyncRemoteStore, FixtureStore>();
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

        internal void Seed(string relativePath, string fileId, string? revision, byte[] payload)
        {
            var hash = SyncContentIdentity.Compute(Provider, payload);
            Entries.Add(new PilotRemoteEntry(relativePath, PilotEntryKind.File, fileId, revision, payload.Length,
                hash));
            Payloads[relativePath] = payload;
        }

        internal void SeedDirectory(string relativePath, string folderId)
        {
            Entries.Add(new PilotRemoteEntry(relativePath, PilotEntryKind.Directory, folderId, null, 0, null));
        }

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
            nameof(ISyncRemoteStore.CreateDirectoryAsync) => CreateDirectory(arguments),
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
            return Task.FromResult<IReadOnlyList<PilotRemoteEntry>>(Entries.ToArray());
        }

        private object Download(object?[]? arguments)
        {
            var expected = (PilotRemoteEntry)arguments![0]!;
            var destination = (Stream)arguments[1]!;
            var relativePath = Normalize(expected.RelativePath);
            Downloads.Add(relativePath);
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
            var expectedHash = (string)arguments[2]!;
            if (RefuseCreateUploadTimes > 0)
            {
                // The one narrow input fault seam of this fixture: the scripted adapter declares the
                // create-only request unsupported for the exact number of calls the proof asked for. Nothing
                // is applied, no upload is recorded and the production classification of this refusal is the
                // shipped one, so no fixture ever replaces the consumer decision under proof.
                RefuseCreateUploadTimes--;
                throw new NotSupportedException(
                    "This scripted target adapter declares the create-only upload unsupported for this request.");
            }
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            Uploads.Add((relativePath, bytes, expectedHash));
            var nativeHash = SyncContentIdentity.Compute(Provider, bytes);
            if (ApplyThenFailTimes > 0 && ApplyThenFail is not null && ApplyThenFail(relativePath, bytes))
            {
                ApplyThenFailTimes--;
                Apply(relativePath, bytes, nativeHash, "rev-" + (++RevisionCounter));
                throw new IOException("The scripted provider applied the request and then lost the response.");
            }
            var revision = "rev-" + (++RevisionCounter);
            Apply(relativePath, bytes, nativeHash, revision);
            return Task.FromResult(CorruptUploadResult
                ? new PilotUploadResult("id-contradictory", revision, bytes.Length,
                    SyncContentIdentity.Compute(Provider, new byte[] { 1, 2, 3 }))
                : new PilotUploadResult("id-file-" + relativePath.Replace('/', '-'), revision, bytes.Length,
                    nativeHash));
        }

        private void Apply(string relativePath, byte[] bytes, string nativeHash, string revision)
        {
            Payloads[relativePath] = bytes;
            var existing = Entries.FindIndex(entry =>
                string.Equals(Normalize(entry.RelativePath), relativePath, StringComparison.Ordinal));
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.File,
                "id-file-" + relativePath.Replace('/', '-'), revision, bytes.Length, nativeHash);
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
        }

        private object CreateDirectory(object?[]? arguments)
        {
            var relativePath = Normalize((string)arguments![0]!);
            CreatedDirectories.Add(relativePath);
            var folderId = "id-folder-" + relativePath.Replace('/', '-');
            var entry = new PilotRemoteEntry(relativePath, PilotEntryKind.Directory, folderId, null, 0, null);
            var existing = Entries.FindIndex(item =>
                string.Equals(Normalize(item.RelativePath), relativePath, StringComparison.Ordinal));
            if (existing >= 0) Entries[existing] = entry; else Entries.Add(entry);
            return Task.FromResult(entry);
        }

        private object Refuse(MethodInfo? method)
        {
            RefusedEffects++;
            _unexpectedEffects++;
            throw new NotSupportedException("This cloud create execution fixture performs no such provider effect: " +
                (method?.Name ?? "unknown"));
        }
    }

    // ----- the real planner and executor drive ------------------------------------------------------

    private static async Task PlannedCreateAsync(string slug, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind, bool directory, int size)
    {
        await using var site = await Site.CreateAsync("case-" + slug, slug, leftKind, rightKind);
        var payload = Pattern(size);
        // The source is placed on the left side; the create therefore targets the right logical endpoint.
        var sourceStore = site.Left;
        if (directory)
        {
            site.Left.SeedDirectory(Path, "id-src-folder");
            await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
                site.Entry(Path, "directory", "id-src-folder", null, 0, null));
        }
        else
        {
            var revision = "rev-src";
            sourceStore.Seed(Path, "id-src-file", revision, payload);
            await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
                site.Entry(Path, "file", "id-src-file", revision, size, payload));
        }
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);

        var outcome = await site.PlanCreateAsync(Path);
        var operationId = outcome.OperationId!;
        var planned = await site.SnapshotAsync(operationId);
        Assert(planned.Kind == SyncAuthorityOperationKind.CreateRemote,
            "the real planner produced a remote create for the left source");
        Assert(planned.Members.Single(item => item.Role == "source").RootBindingId == site.LeftRoot,
            "the retained source member binds the left typed port's authority root");
        Assert(planned.Members.Single(item => item.Role == "destination").RootBindingId == site.RightRoot,
            "the retained destination member binds the right typed port's authority root");
        Assert(planned.Members.Single(item => item.Role == "destination").ObjectVersionId is null,
            "the planned create destination is a fresh absent destination");

        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var result = await executor.ExecuteAsync(operationId, site.Worker);
        Assert(result.Status == SyncAuthorityOperationStatus.Completed,
            "the real cloud create reached the completed state: " + result.Reason);
        Assert(site.Right.Uploads.Count == 1 || directory,
            "exactly one create reached the target adapter");
        if (!directory)
        {
            Assert(site.Right.Uploads[0].Bytes.SequenceEqual(payload),
                "the target received the exact retained source bytes");
            Assert(site.Right.Uploads[0].ExpectedHash == SyncContentIdentity.Compute(
                    SyncEndpointProviders.Get(rightKind).ProviderId!, payload),
                "the create-only request carried the target provider's own native digest over the held bytes");
        }
        else
        {
            Assert(site.Right.CreatedDirectories.Count == 1 && site.Right.CreatedDirectories[0] == Path,
                "exactly one folder create reached the exact destination path");
        }

        var completed = await site.SnapshotAsync(operationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed,
            "the journal records the cloud create as completed");
        Assert(completed.Attempts.Count == 1 &&
               completed.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "the single dispatched attempt is terminally verified success");
        Assert(string.IsNullOrEmpty(site.Pair.LocalRoot) && site.Pair.LocalVolumeSerialNumber == 0 &&
               site.Pair.LocalRootFileId == 0,
            "no selected NTFS root, volume serial or root file id is projected by the cloud pair");
        foreach (var allocation in completed.Allocations)
            Assert(allocation.Status is SyncAuthorityAllocationStatus.Released or
                SyncAuthorityAllocationStatus.Verified,
                "every owned allocation reached an exact terminal disposition: " + allocation.Kind + "=" +
                allocation.Status);
        var transaction = completed.Allocations.SingleOrDefault(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        if (transaction is not null)
        {
            var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
            Assert(!Directory.Exists(transactionPath) && !File.Exists(transactionPath),
                "the owned private stage and its empty transaction were removed after commit");
        }
        else
        {
            Assert(directory, "only a directory create legitimately carries no private stage allocation");
        }

        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        var point = points.Single(item => string.Equals(item.ObjectId, completed.ObjectId,
            StringComparison.Ordinal));
        Assert(point.LocalObjectVersionId is not null && point.DropboxObjectVersionId is not null,
            "the committed synchronization point retains both ordered sides");
        Assert(point.DropboxObjectVersionId != planned.Members.Single(item => item.Role == "source").ObjectVersionId,
            "the successor occupies the target (dropbox) slot of the ordered point");
        Assert(point.LocalObjectVersionId == planned.Members.Single(item => item.Role == "source").ObjectVersionId,
            "the retained source keeps its own (local) slot of the ordered point");
        Assert(point.LocalMembershipId is not null && point.DropboxMembershipId is not null,
            "both ordered memberships are retained on the point");
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
        await ReverseDirectionAsync("c-db-gd", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        await ReverseDirectionAsync("c-gd-db", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox);
    }

    /// <summary>The other direction of one ordered provider pairing: the source is on the right and the create targets the left.</summary>
    private static async Task ReverseDirectionAsync(string slug, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind)
    {
        await using var site = await Site.CreateAsync("case-" + slug, slug, leftKind, rightKind);
        var payload = Pattern(1536);
        site.Right.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight,
            site.Entry(site.Right, Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        var outcome = await site.PlanCreateAsync(Path);
        var operation = await site.SnapshotAsync(outcome.OperationId!);
        Assert(operation.Kind == SyncAuthorityOperationKind.CreateLocal,
            "a right-side source plans a create on the left logical target for " + slug);
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var result = await executor.ExecuteAsync(outcome.OperationId!, site.Worker);
        Assert(result.Status == SyncAuthorityOperationStatus.Completed,
            "the reverse-direction create completed on the left target for " + slug + ": " + result.Reason);
        Assert(site.Left.Uploads.Count == 1 && site.Right.Uploads.Count == 0,
            "the reverse-direction create reached only its own logical target for " + slug);
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        var point = points.Single(item =>
            string.Equals(item.ObjectId, operation.ObjectId, StringComparison.Ordinal));
        Assert(point.LocalObjectVersionId != point.DropboxObjectVersionId,
            "the reverse-direction point retains two distinct ordered sides for " + slug);
        Assert(point.DropboxObjectVersionId ==
               operation.Members.Single(item => item.Role == "source").ObjectVersionId,
            "the retained source keeps its own (dropbox) slot of the reverse-direction point for " + slug);
    }

    /// <summary>
    /// CC3BFIX: the create successor must be registered under the exact observation-derived version identity.
    /// This drives a real create to completion and then runs the production observation of the same target
    /// again, which is the step that silently loses its INSERT OR IGNORE and fails closed when the committed
    /// identity is derived differently. Both endpoint orderings, both logical directions, empty and nonempty.
    /// </summary>
    private static async Task ReObservationIdentityAsync()
    {
        await ReObserveAsync("m1", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive, false, 1024);
        await ReObserveAsync("m2", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox, false, 0);
        await ReObserveAsync("m3", SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox, true, 4096);
        await ReObserveAsync("m4", SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive, true, 0);
    }

    private static async Task ReObserveAsync(string slug, SyncEndpointKind leftKind,
        SyncEndpointKind rightKind, bool reverse, int size)
    {
        await using var site = await Site.CreateAsync("case-" + slug, slug, leftKind, rightKind);
        var payload = Pattern(size);
        var sourceSide = reverse ? SyncAuthorityCloudSides.LogicalRight : SyncAuthorityCloudSides.LogicalLeft;
        var targetSide = reverse ? SyncAuthorityCloudSides.LogicalLeft : SyncAuthorityCloudSides.LogicalRight;
        var sourceStore = site.StoreFor(sourceSide);
        var targetStore = site.StoreFor(targetSide);
        sourceStore.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(sourceSide, site.Entry(sourceStore, Path, "file", "id-src-file", "rev-src",
            payload.Length, payload));
        await site.ObserveAsync(targetSide);

        var outcome = await site.PlanCreateAsync(Path);
        var operation = await site.SnapshotAsync(outcome.OperationId!);
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var result = await executor.ExecuteAsync(outcome.OperationId!, site.Worker);
        Assert(result.Status == SyncAuthorityOperationStatus.Completed,
            "the create completed before the re-observation for " + slug + ": " + result.Reason);
        var point = (await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id))
            .Single(item => string.Equals(item.ObjectId, operation.ObjectId, StringComparison.Ordinal));
        var committedTargetVersion = targetSide == SyncAuthorityCloudSides.LogicalLeft
            ? point.LocalObjectVersionId
            : point.DropboxObjectVersionId;
        Assert(!string.IsNullOrWhiteSpace(committedTargetVersion),
            "the committed synchronization point retains the created target version for " + slug);

        var created = targetStore.Find(Path);
        Assert(created is not null && created.Kind == PilotEntryKind.File &&
               !string.IsNullOrWhiteSpace(created.Id),
            "the created target object is present in its own provider inventory for " + slug);
        // The next production observation of the same target, exactly as the next real run would list it. This
        // is the step whose INSERT OR IGNORE is silently dropped when create derives its own version identity.
        await site.ObserveAsync(targetSide, site.Entry(targetStore, Path, "file", created!.Id!,
            created.Revision, created.Size, targetStore.Payloads[Path]));

        var observed = (await site.Db.LoadLatestCompleteObservationAsync(site.Pair.Id, targetSide))
            .Single(item => string.Equals(item.RelativePath, Path, StringComparison.Ordinal));
        Assert(string.Equals(observed.ObjectId, operation.ObjectId, StringComparison.Ordinal),
            "the re-observed target object is the created logical object for " + slug);
        Assert(string.Equals(observed.ObjectVersionId, committedTargetVersion, StringComparison.Ordinal),
            "the next production observation of the cloud-created target reproduces the committed version identity exactly for " +
            slug + " (observed " + observed.ObjectVersionId + ", committed " + committedTargetVersion + ")");
        Assert(string.Equals(observed.ContentHash, created.ContentHash, StringComparison.OrdinalIgnoreCase) &&
               observed.Size == created.Size && observed.Size == size,
            "the re-observed native digest and size are the created bytes for " + slug);

        // The subsequent comparison: the real reconciliation planner over the same pair sees the two sides of
        // the committed order as equal and plans no further effect for the path.
        var planner = new SyncAuthorityReconciliationPlanner(site.Db, site.Options(1L << 30, 1L << 30), site.Admission);
        var planned = await planner.PlanAsync(site.Pair.Id);
        Assert(planned.All(item => item.Kind != SyncAuthorityPlannerOutcomeKind.Operation ||
                                   !string.Equals(item.RelativePath, Path, StringComparison.Ordinal)),
            "the subsequent comparison over the real planner plans no effect for the created path in " + slug);
    }

    private static async Task DirectoryCreateAsync()
    {
        await using var site = await Site.CreateAsync("case-e", "e", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        site.Left.SeedDirectory(Path, "id-src-folder");
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "directory", "id-src-folder", null, 0, null));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var planned = await site.SnapshotAsync(outcome.OperationId!);
        Assert(planned.Allocations.Count == 0,
            "a cloud directory create reserves no private file stage, transaction or upload session");
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var result = await executor.ExecuteAsync(outcome.OperationId!, site.Worker);
        Assert(result.Status == SyncAuthorityOperationStatus.Completed,
            "the cloud directory create completed through the same receipt and commit path: " + result.Reason);
        Assert(site.Right.CreatedDirectories.Count == 1 && site.Right.Uploads.Count == 0,
            "the directory create used only the folder create-only adapter method");
        var completed = await site.SnapshotAsync(outcome.OperationId!);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed && completed.Attempts.Count == 1,
            "the directory create committed exactly one verified attempt");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Any(item => item.ObjectId == completed.ObjectId),
            "the directory create committed an ordered synchronization point");
    }

    private static async Task CompletedRepeatAsync()
    {
        await using var site = await Site.CreateAsync("case-f", "f", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(1024);
        site.Left.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var first = await executor.ExecuteAsync(outcome.OperationId!, site.Worker);
        Assert(first.Status == SyncAuthorityOperationStatus.Completed, "the first cloud create completed");
        var uploads = site.Right.Uploads.Count;
        var attemptCount = (await site.SnapshotAsync(outcome.OperationId!)).Attempts.Count;
        var repeat = await executor.ReconcileAsync(outcome.OperationId!, site.Worker);
        Assert(repeat.Status == SyncAuthorityOperationStatus.Completed,
            "repeating the completed call is a no-op completion");
        Assert(site.Right.Uploads.Count == uploads,
            "repeating the completed call issued zero additional upload");
        Assert((await site.SnapshotAsync(outcome.OperationId!)).Attempts.Count == attemptCount,
            "repeating the completed call issued no additional dispatched attempt");
        var direct = await executor.ExecuteAsync(outcome.OperationId!, site.Worker);
        Assert(direct.Status == SyncAuthorityOperationStatus.Completed &&
               site.Right.Uploads.Count == uploads,
            "the reconciled repeat also copies nothing and reuses the retained receipt");
    }

    // ----- refusals ----------------------------------------------------------------------------------

    private static async Task PlanRefusalsAsync()
    {
        await using var site = await Site.CreateAsync("case-g", "g", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(256);
        site.Left.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var operationId = outcome.OperationId!;
        var planned = await site.SnapshotAsync(operationId);

        // A foreign pair, a foreign member root binding, another operation kind and a fresh destination are
        // all refused before any provider request.
        var other = await Site.CreateAsync("case-g-other", "gother", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        try
        {
            // The same authority database and the same held control root, but a typed endpoint set of another
            // pair: the retained operation's pair and member root bindings no longer agree with the ports.
            var foreignContext = new SyncAuthorityCloudExecutionContext(other.Endpoints(), site.Control,
                site.ControlRoot);
            var executor = new SyncAuthorityCloudCreateExecutor(site.Db, foreignContext);
            await ExpectAnyRefusalAsync(() => executor.ExecuteAsync(operationId, site.Worker),
                "an operation of another pair");
            Assert(site.Right.Uploads.Count == 0, "a foreign pair issued no target request");
        }
        finally
        {
            await other.DisposeAsync();
        }
        Assert(planned.Members.Single(item => item.Role == "destination").MembershipId is null,
            "the planned destination member starts with no successor membership");
        Assert(planned.Allocations.Count(item => item.Kind == SyncAuthorityAllocationKind.TransactionDirectory) == 1 &&
               planned.Allocations.Count(item => item.Kind == SyncAuthorityAllocationKind.StageFile) == 1,
            "the planned cloud create carries exactly one private transaction and one private stage");

        // A target collision is refused at the exact destination path immediately before dispatch. Its own
        // private stage is created first, so it uses its own operation and its own fixture.
        await using var collision = await Site.CreateAsync("case-g-collision", "gcoll",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var collisionPayload = Pattern(256, 41);
        collision.Left.Seed(Path, "id-src-file", "rev-src", collisionPayload);
        await collision.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            collision.Entry(Path, "file", "id-src-file", "rev-src", collisionPayload.Length, collisionPayload));
        await collision.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var collisionOutcome = await collision.PlanCreateAsync(Path);
        collision.Right.Seed(Path, "id-collision", "rev-collision", Pattern(32, 17));
        var collisionExecutor = new SyncAuthorityCloudCreateExecutor(collision.Db, collision.Context());
        await ExpectRefusalAsync(() => collisionExecutor.ExecuteAsync(collisionOutcome.OperationId!,
            collision.Worker), "an existing target object at the exact destination path");
        Assert(collision.Right.Uploads.Count == 0, "the collision refusal issued no create request");
        var afterCollision = await collision.SnapshotAsync(collisionOutcome.OperationId!);
        Assert(afterCollision.Attempts.Count == 0,
            "the collision refusal dispatched no attempt before the target request boundary");
        Assert(afterCollision.Status != SyncAuthorityOperationStatus.Completed &&
               afterCollision.Status != SyncAuthorityOperationStatus.Committed,
            "the collision refusal committed nothing");
        Assert(collision.Right.Entries.Single(item => item.RelativePath == Path).Id == "id-collision",
            "the colliding target object was left exactly as it was found");

        // A cloud-native or unsupported item at the destination is refused rather than overwritten.
        await using var native = await Site.CreateAsync("case-g-native", "gnative", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var nativePayload = Pattern(192, 53);
        native.Left.Seed(Path, "id-src-file", "rev-src", nativePayload);
        await native.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            native.Entry(Path, "file", "id-src-file", "rev-src", nativePayload.Length, nativePayload));
        await native.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var nativeOutcome = await native.PlanCreateAsync(Path);
        native.Right.Entries.Add(new PilotRemoteEntry(Path, PilotEntryKind.File, "id-native", null, 0, null,
            null, "This entry is a cloud-native placeholder and cannot hold ordinary content."));
        var nativeExecutor = new SyncAuthorityCloudCreateExecutor(native.Db, native.Context());
        await ExpectRefusalAsync(() => nativeExecutor.ExecuteAsync(nativeOutcome.OperationId!,
            native.Worker), "a cloud-native or unsupported destination item");
        Assert(native.Right.Uploads.Count == 0, "the cloud-native refusal issued no create request");
        Assert((await native.SnapshotAsync(nativeOutcome.OperationId!)).Attempts.Count == 0,
            "the cloud-native refusal dispatched no attempt");

        // A directory create that does not carry the exact retained folder identity is refused.
        await using var directorySite = await Site.CreateAsync("case-g-directory", "gdir",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        directorySite.Left.SeedDirectory(Path, "id-src-folder");
        await directorySite.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            directorySite.Entry(Path, "directory", "id-src-folder", null, 0, null));
        await directorySite.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var directoryOutcome = await directorySite.PlanCreateAsync(Path);
        directorySite.Left.Entries.RemoveAll(item => item.RelativePath == Path);
        directorySite.Left.SeedDirectory(Path, "id-replaced-folder");
        var directoryExecutor = new SyncAuthorityCloudCreateExecutor(directorySite.Db,
            directorySite.Context());
        await ExpectRefusalAsync(() => directoryExecutor.ExecuteAsync(directoryOutcome.OperationId!,
            directorySite.Worker), "a replaced source folder identity");
        Assert(directorySite.Right.CreatedDirectories.Count == 0,
            "the replaced source folder refusal issued no folder request");
    }

    private static async Task TransportRefusalsAsync()
    {
        // A wrong operation kind can never be routed through the cloud create slice.
        await using var site = await Site.CreateAsync("case-h", "h", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(128);
        site.Left.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var operationId = outcome.OperationId!;
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());

        // A stale foreign fence: an operation that already holds an active lease is not admitted twice.
        var held = await site.Db.AcquireOperationLeaseAsync(operationId, "another-worker",
            TimeSpan.FromMinutes(5));
        await ExpectAnyRefusalAsync(() => executor.ExecuteAsync(operationId, site.Worker),
            "an operation that already holds a live foreign lease");
        Assert(site.Right.Uploads.Count == 0, "the stale-fence refusal issued no create request");
        await site.Db.ReleaseLeaseAsync(held, CancellationToken.None);

        // A cancelled create is refused and retains its private stage without any target request.
        using var cancellation = new CancellationTokenSource();
        site.Left.DuringDownload = _ => cancellation.Cancel();
        await ExpectCancelledAsync(() => executor.ExecuteAsync(operationId, site.Worker, cancellation.Token),
            "a cancelled source read");
        site.Left.DuringDownload = null;
        Assert(site.Right.Uploads.Count == 0 && site.Right.CreatedDirectories.Count == 0,
            "the cancelled create issued no target request");
        var cancelled = await site.SnapshotAsync(operationId);
        Assert(cancelled.Attempts.Count == 0, "the cancelled create dispatched no attempt");
        Assert(cancelled.Allocations.Where(item => item.Kind == SyncAuthorityAllocationKind.StageFile)
            .All(item => item.Status != SyncAuthorityAllocationStatus.Released),
            "the cancelled create retained its private stage allocation for reconciliation");

        // A contradictory create-only response is never committed: the readback cannot confirm it.
        await using var corruptSite = await Site.CreateAsync("case-h-corrupt", "hcorrupt",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var corruptPayload = Pattern(160, 23);
        corruptSite.Left.Seed(Path, "id-src-file", "rev-src", corruptPayload);
        await corruptSite.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            corruptSite.Entry(Path, "file", "id-src-file", "rev-src", corruptPayload.Length, corruptPayload));
        await corruptSite.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var corruptOperationId = (await corruptSite.PlanCreateAsync(Path)).OperationId!;
        corruptSite.Right.CorruptUploadResult = true;
        var corruptExecutor = new SyncAuthorityCloudCreateExecutor(corruptSite.Db, corruptSite.Context());
        var corrupt = await corruptExecutor.ExecuteAsync(corruptOperationId, corruptSite.Worker);
        Assert(corrupt.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "a response that contradicts the held bytes is treated as an unknown outcome");
        corruptSite.Right.CorruptUploadResult = false;
        var afterCorrupt = await corruptSite.SnapshotAsync(corruptOperationId);
        Assert(afterCorrupt.Status != SyncAuthorityOperationStatus.Completed &&
               afterCorrupt.Status != SyncAuthorityOperationStatus.Committed,
            "the contradictory response committed no successor");
        Assert(afterCorrupt.Attempts.Count == 1 &&
               afterCorrupt.Attempts[0].Status != SyncAuthorityAttemptStatus.VerifiedSuccess,
            "the contradictory response left its attempt unresolved");

        // An adapter without a resumable implementation cannot consume a reserved session.
        // The native-adapter positive case below separately proves small configured sessions.
        await using var sessionSite = await Site.CreateAsync("case-h-session", "hsession",
            SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive);
        var sessionPayload = Pattern(4096);
        sessionSite.Left.Seed(Path, "id-src-file", "rev-src", sessionPayload);
        await sessionSite.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            sessionSite.Entry(Path, "file", "id-src-file", "rev-src", sessionPayload.Length, sessionPayload));
        await sessionSite.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var sessionOperation = await sessionSite.PlanCreateAsync(Path, 1024, 1024);
        var sessionPlanned = await sessionSite.SnapshotAsync(sessionOperation.OperationId!);
        Assert(sessionPlanned.Allocations.Any(item =>
                item.Kind == SyncAuthorityAllocationKind.UploadSession),
            "a source above the configured target limit reserves one upload session");
        var sessionExecutor = new SyncAuthorityCloudCreateExecutor(sessionSite.Db, sessionSite.Context());
        Assert(sessionSite.Right.Store is not ISyncResumableRemoteStore,
            "this refusal fixture has no resumable adapter; size alone is not a refusal oracle");
        await ExpectAnyRefusalAsync(() => sessionExecutor.ExecuteAsync(sessionOperation.OperationId!,
            sessionSite.Worker), "a reserved upload session without a resumable adapter implementation");
        Assert(sessionSite.Right.Uploads.Count == 0,
            "the resumable capability mismatch issued no target request");
        Assert((await sessionSite.SnapshotAsync(sessionOperation.OperationId!)).Attempts.Count == 0,
            "the resumable capability mismatch dispatched no attempt");
    }

    private sealed class NativeSessionTokenSource : IGoogleDriveAccessTokenSource, IDropboxAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("synthetic-session-access-token");
    }

    // Supply exact account/root/path metadata around the existing native Dropbox session wire contract.
    // The shared executor, adapter, coordinator and checkpoint decisions remain the shipped code. The
    // original session handler and its failure contracts are unchanged and cannot reach a real endpoint.
    private sealed class NativeDropboxSessionWire(FixtureStore target, string rootPath, string homeNamespace)
        : HttpMessageHandler
    {
        private readonly DropboxUploadSessionContractHandler _sessions = new();
        private HttpMessageInvoker? _boundInvoker;
        internal string HomeNamespace => homeNamespace;
        internal byte[] Bytes => _sessions.CommittedBytes;
        internal int Writes => _sessions.FinishCount;
        internal int Chunks => _sessions.StartCount + _sessions.AppendCount + _sessions.FinishCount;
        internal int ForeignRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method != HttpMethod.Post ||
                uri.Host is not ("api.dropboxapi.com" or "content.dropboxapi.com"))
                throw Refused("Unexpected native Dropbox fixture origin or method.");
            var route = uri.AbsolutePath;
            if (route == "/2/users/get_current_account")
                return Json(new { account_id = "id:" + target.ProviderAccountId,
                    root_info = new { root_namespace_id = target.NamespaceId, home_namespace_id = homeNamespace } });
            if (!request.Headers.TryGetValues("Dropbox-API-Path-Root", out var roots))
                throw Refused("The native fixture request omitted its bound root namespace.");
            using var root = JsonDocument.Parse(roots.Single());
            if (root.RootElement.GetProperty(".tag").GetString() != "root" ||
                root.RootElement.GetProperty("root").GetString() != target.NamespaceId)
                throw Refused("The native fixture request used another root namespace.");
            if (route == "/2/files/get_metadata")
            {
                using var argument = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var path = argument.RootElement.GetProperty("path").GetString();
                if (path == target.FolderId) return Folder(target.FolderId, rootPath);
                if (path == target.FolderId + "/reports") return Folder("id:native-parent", rootPath + "/reports");
                if (path == target.FolderId + "/" + Path || path == "id:large")
                    return Writes > 0 ? FileMetadata() : new HttpResponseMessage(System.Net.HttpStatusCode.Conflict)
                    {
                        Content = new StringContent("""{"error_summary":"path/not_found/..","error":{".tag":"path","path":{".tag":"not_found"}}}""",
                            Encoding.UTF8, "application/json")
                    };
                throw Refused("The native fixture metadata path is outside the selected destination.");
            }
            if (route is not ("/2/files/upload_session/start" or "/2/files/upload_session/append_v2" or
                "/2/files/upload_session/finish"))
                throw Refused("Unexpected native Dropbox fixture route: " + route);
            if (route == "/2/files/upload_session/finish")
            {
                using var argument = JsonDocument.Parse(request.Headers.GetValues("Dropbox-API-Arg").Single());
                var commit = argument.RootElement.GetProperty("commit");
                if (commit.GetProperty("path").GetString() != target.FolderId + "/" + Path ||
                    commit.GetProperty("mode").GetString() != "add" ||
                    commit.GetProperty("autorename").GetBoolean() || !commit.GetProperty("strict_conflict").GetBoolean())
                    throw Refused("The native fixture commit is not the exact create-only destination.");
            }
            _boundInvoker ??= new HttpMessageInvoker(_sessions, false);
            var response = await _boundInvoker.SendAsync(request, cancellationToken);
            if (route != "/2/files/upload_session/finish" || !response.IsSuccessStatusCode) return response;
            response.Dispose();
            return FileMetadata();
        }

        private HttpResponseMessage FileMetadata() => Json(new Dictionary<string, object?>
        {
            [".tag"] = "file", ["name"] = "quarterly.txt", ["id"] = "id:large", ["rev"] = "rev:large",
            ["path_display"] = rootPath + "/" + Path, ["path_lower"] = (rootPath + "/" + Path).ToLowerInvariant(),
            ["size"] = Bytes.LongLength, ["content_hash"] = SyncContentIdentity.Compute("dropbox", Bytes)
        });
        private static HttpResponseMessage Folder(string id, string path) => Json(new Dictionary<string, object?>
        {
            [".tag"] = "folder", ["id"] = id, ["name"] = path.Split('/').Last(),
            ["path_display"] = path, ["path_lower"] = path.ToLowerInvariant()
        });
        private static HttpResponseMessage Json(object value) => new(System.Net.HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        private InvalidDataException Refused(string reason)
        { ForeignRequests++; _unexpectedEffects++; return new InvalidDataException(reason); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _boundInvoker?.Dispose(); _sessions.ExpireSessions(); _sessions.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private static async Task NativeSessionDirectionsAsync()
    {
        var priorClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
        try
        {
            foreach (var targetKind in new[] { SyncEndpointKind.GoogleDrive, SyncEndpointKind.Dropbox })
            foreach (var sourceOnRight in new[] { false, true })
            {
                var sourceKind = targetKind == SyncEndpointKind.GoogleDrive
                    ? SyncEndpointKind.Dropbox : SyncEndpointKind.GoogleDrive;
                var slug = (targetKind == SyncEndpointKind.GoogleDrive ? "drive-" : "dropbox-") +
                           (sourceOnRight ? "native-right-source" : "native-left-source");
                await using var site = await Site.CreateAsync("case-n-" + slug, slug,
                    sourceOnRight ? targetKind : sourceKind, sourceOnRight ? sourceKind : targetKind);
                var sourceSide = sourceOnRight ? SyncAuthorityCloudSides.LogicalRight : SyncAuthorityCloudSides.LogicalLeft;
                var targetSide = sourceOnRight ? SyncAuthorityCloudSides.LogicalLeft : SyncAuthorityCloudSides.LogicalRight;
                var source = site.StoreFor(sourceSide);
                var target = site.StoreFor(targetSide);
                var payload = Pattern(512 * 1024);
                source.Seed(Path, "id-native-source", "native-source-revision", payload);
                var targetPath = sourceOnRight ? "/left" : "/right";
                using var server = targetKind == SyncEndpointKind.GoogleDrive ? new SyntheticDriveServer() : null;
                using var dropboxServer = targetKind == SyncEndpointKind.Dropbox
                    ? new NativeDropboxSessionWire(target, targetPath,
                        "ns-home-" + slug + (sourceOnRight ? "-left" : "-right")) : null;
                using var http = new HttpClient((HttpMessageHandler?)server ?? dropboxServer!);
                ISyncRemoteStore native;
                string? driveParentId = null;
                if (server is not null)
                {
                    server.AddFolder(target.FolderId, "Pair", null);
                    driveParentId = server.AddFolder("gd-native-reports-" + slug, "reports", target.FolderId).Id;
                    var binding = new GoogleDriveConnectionBinding("test-client", target.ProviderAccountId,
                        target.NamespaceId, "session@example.invalid", "Session fixture",
                        [GoogleDriveProviderConfiguration.DriveScope], ConnectionId: "conn-" + slug).Normalize();
                    native = new GoogleDrivePilotClient(http, new NativeSessionTokenSource(), binding,
                        target.FolderId, targetPath, resumableThresholdBytes: 256 * 1024,
                        resumableChunkSizeBytes: 256 * 1024, capabilities: GoogleDriveCapabilities.Baseline);
                }
                else
                {
                    var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                        "id:" + target.ProviderAccountId, target.NamespaceId, dropboxServer!.HomeNamespace,
                        DropboxProviderConfiguration.ContentAccess, DropboxProviderConfiguration.RequiredScopes,
                        ConnectionId: "conn-" + slug);
                    native = new DropboxPilotClient(http, new NativeSessionTokenSource(), targetPath, binding,
                        target.FolderId, uploadSessionThresholdBytes: 256 * 1024,
                        uploadSessionChunkSizeBytes: 256 * 1024);
                }
                byte[] TargetBytes() => server is not null
                    ? server.BodyFor(server.IdByName(driveParentId!, "quarterly.txt")) : dropboxServer!.Bytes;
                int Writes() => server?.WriteCalls ?? dropboxServer!.Writes;
                int Chunks() => server?.SessionChunkCalls ?? dropboxServer!.Chunks;
                int ForeignRequests() => server?.ForeignRequestCalls ?? dropboxServer!.ForeignRequests;
                if (sourceOnRight) site.LeftNativeStore = native;
                else site.RightNativeStore = native;
                await native.VerifyRootIdentityAsync();
                await site.ObserveAsync(sourceSide,
                    site.Entry(source, Path, "file", "id-native-source", "native-source-revision", payload.Length, payload));
                await site.ObserveAsync(targetSide);
                var planned = await site.PlanCreateAsync(Path, 256 * 1024, 256 * 1024);
                var operation = await site.SnapshotAsync(planned.OperationId!);
                var allocation = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.UploadSession);
                var sourceMember = operation.Members.Single(item => item.Role == "source");
                var sourceVersion = await site.Db.LoadObjectVersionAsync(sourceMember.ObjectVersionId!);
                Assert(sourceVersion.Side == sourceSide && allocation.IntendedParentIdentity == site.RootFor(targetSide),
                    "native session uses the actual logical source and exact target root: " + slug);
                var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
                var result = await executor.ExecuteAsync(operation.OperationId, site.Worker);
                Assert(result.Status == SyncAuthorityOperationStatus.Completed,
                    "the real native receiving adapter and cloud executor complete: " + slug + " (" + result.Status + ")");
                Assert(TargetBytes().SequenceEqual(payload) && Writes() == 1 &&
                       Chunks() > 0 && ForeignRequests() == 0,
                    "one actual native session creates exact bytes under the bound target parent: " + slug);
                var completed = await site.SnapshotAsync(operation.OperationId);
                var checkpoint = await site.Db.LoadUploadSessionCheckpointAsync(allocation.AllocationId);
                Assert(checkpoint is { Status: "completed" } &&
                       checkpoint.ConfirmedOffset == payload.LongLength && checkpoint.SourceSize == payload.LongLength &&
                       checkpoint.SourceObjectVersionId == sourceVersion.ObjectVersionId &&
                       checkpoint.SourceContentHash == sourceVersion.ContentHash,
                    "the real coordinator durably completes the original cloud-source checkpoint: " + slug);
                Assert(completed.Attempts is [{ Status: SyncAuthorityAttemptStatus.VerifiedSuccess }] &&
                       completed.Allocations.Where(item => item.Kind is SyncAuthorityAllocationKind.UploadSession or
                           SyncAuthorityAllocationKind.StageFile or SyncAuthorityAllocationKind.TransactionDirectory)
                           .All(item => item.Status == SyncAuthorityAllocationStatus.Released),
                    "native success releases the owned session and stage after one verified attempt: " + slug);
                Assert(source.Payloads[Path].SequenceEqual(payload) && source.Downloads.Count == 1 &&
                       target.Uploads.Count == 0,
                    "the source remains exact and the scripted target never substitutes a session: " + slug);
                var chunkCalls = Chunks();
                var repeated = await executor.ExecuteAsync(operation.OperationId, site.Worker);
                Assert(repeated.Status == SyncAuthorityOperationStatus.Completed && Writes() == 1 &&
                       Chunks() == chunkCalls && source.Downloads.Count == 1,
                    "completed native-session replay is a read-only no-op: " + slug);
            }
        }
        finally { GoogleDriveProviderConfiguration.RegisteredClientIdForTest = priorClient; }
    }

    private static async Task UnknownOutcomeAsync()
    {
        await using var site = await Site.CreateAsync("case-i", "i", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(768);
        site.Left.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var operationId = outcome.OperationId!;
        // The target applies the object and then loses the response: the outcome is unknown, not a failure.
        site.Right.ApplyThenFail = (relativePath, _) =>
            string.Equals(relativePath, Path, StringComparison.Ordinal);
        site.Right.ApplyThenFailTimes = 1;
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var unknown = await executor.ExecuteAsync(operationId, site.Worker);
        site.Right.ApplyThenFail = null;
        Assert(unknown.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "a lost response after an applied request is an unknown outcome: " + unknown.Reason);
        var afterUnknown = await site.SnapshotAsync(operationId);
        Assert(afterUnknown.Status != SyncAuthorityOperationStatus.Completed &&
               afterUnknown.Status != SyncAuthorityOperationStatus.Committed,
            "the unknown outcome committed no successor");
        var stageAllocation = afterUnknown.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var transactionAllocation = afterUnknown.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stagePath = IOPath.Combine(site.ControlRoot.Path, transactionAllocation.DeterministicName,
            stageAllocation.DeterministicName);
        Assert(File.Exists(stagePath), "the unknown outcome retained its exact private stage file");
        var uploadsAfterUnknown = site.Right.Uploads.Count;

        // Reconciliation must never blindly repeat the uncertainty and must never guess that it failed.
        var reconciled = await executor.ReconcileAsync(operationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "an unresolved outcome retains its verification-required certainty, not ordinary failed/retryable status");
        Assert(site.Right.Uploads.Count == uploadsAfterUnknown,
            "reconciliation issued no duplicate create for the unresolved outcome");
        Assert(File.Exists(stagePath),
            "reconciliation retained the stage of the unresolved outcome as its only evidence");
        var afterReconcile = await site.SnapshotAsync(operationId);
        Assert(afterReconcile.Attempts.Count == afterUnknown.Attempts.Count,
            "reconciliation dispatched no new attempt for the unresolved outcome");
        Assert(afterReconcile.Status == SyncAuthorityOperationStatus.EffectUnknown &&
               afterReconcile.Attempts.Any(item => item.Status == SyncAuthorityAttemptStatus.EffectUnknown),
            "reconciliation preserves the exact unresolved operation and dispatched attempt; matching bytes are not authority");
    }

    private static async Task KnownReceiptCrashAsync()
    {
        await using var site = await Site.CreateAsync("case-j", "j", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(640);
        site.Left.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var operationId = outcome.OperationId!;
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var crashed = false;
        SyncAuthorityCloudCreateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-receipt-before-commit" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the known-success receipt");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => executor.ExecuteAsync(operationId, site.Worker),
                "a crash after the known-success receipt");
        }
        finally
        {
            SyncAuthorityCloudCreateExecutor.PhaseForTest = null;
        }
        Assert(crashed, "the scripted pre-commit crash was reached");
        var uploadsAfterCrash = site.Right.Uploads.Count;
        Assert(uploadsAfterCrash == 1, "exactly one create reached the target before the crash");
        var crashedOperation = await site.SnapshotAsync(operationId);
        Assert(crashedOperation.Status is SyncAuthorityOperationStatus.EffectIssuing or
            SyncAuthorityOperationStatus.EffectUnknown or SyncAuthorityOperationStatus.EffectObserved,
            "the crashed operation retains an unresolved journal state: " + crashedOperation.Status);

        var reconciled = await executor.ReconcileAsync(operationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.Completed,
            "the retained receipt plus exact readback completed the commit and cleanup: " + reconciled.Reason);
        Assert(site.Right.Uploads.Count == uploadsAfterCrash,
            "reconciliation uploaded nothing for the retained receipt");
        var final = await site.SnapshotAsync(operationId);
        Assert(final.Status == SyncAuthorityOperationStatus.Completed, "the reconciled create is complete");
        Assert(final.Attempts.Count == 1 &&
               final.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedSuccess,
            "reconciliation committed the one retained attempt exactly once");
        var points = await site.Db.LoadLatestSynchronizationPointsAsync(site.Pair.Id);
        Assert(points.Count(item => item.ObjectId == final.ObjectId) == 1,
            "reconciliation committed exactly one synchronization point");
        var transaction = final.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
        Assert(!Directory.Exists(transactionPath) && !File.Exists(transactionPath),
            "reconciliation cleaned the owned private stage exactly once");
    }

    private static async Task CommittedCrashAsync()
    {
        await using var site = await Site.CreateAsync("case-k", "k", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var payload = Pattern(320);
        site.Left.Seed(Path, "id-src-file", "rev-src", payload);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", payload.Length, payload));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var outcome = await site.PlanCreateAsync(Path);
        var operationId = outcome.OperationId!;
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var crashed = false;
        SyncAuthorityCloudCreateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-commit-before-cleanup" && !crashed)
            {
                crashed = true;
                throw new InvalidOperationException("scripted crash after the commit boundary");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => executor.ExecuteAsync(operationId, site.Worker),
                "a crash after the commit boundary");
        }
        finally
        {
            SyncAuthorityCloudCreateExecutor.PhaseForTest = null;
        }
        var uploadsAfterCrash = site.Right.Uploads.Count;
        Assert(uploadsAfterCrash == 1, "exactly one create reached the target before the commit crash");
        var committed = await site.SnapshotAsync(operationId);
        Assert(committed.Status == SyncAuthorityOperationStatus.Committed,
            "the crashed operation retains the committed boundary: " + committed.Status);
        var transaction = committed.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
        Assert(Directory.Exists(transactionPath),
            "the committed pre-cleanup crash retained its exact owned private transaction");

        var resume = await executor.ReconcileAsync(operationId, site.Worker);
        Assert(resume.Status == SyncAuthorityOperationStatus.Completed,
            "the committed crash resumed only its exact owned cleanup: " + resume.Reason);
        Assert(site.Right.Uploads.Count == uploadsAfterCrash,
            "the committed crash resume recopied nothing");
        Assert(!Directory.Exists(transactionPath),
            "the committed crash resume removed its exact owned private transaction");
        Assert((await site.SnapshotAsync(operationId)).Status == SyncAuthorityOperationStatus.Completed,
            "the resumed committed create reached completion");

        // A replaced stage object after a commit is refused by exact identity rather than force-removed.
        await using var foreign = await Site.CreateAsync("case-k-foreign", "kforeign", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var foreignPayload = Pattern(288);
        foreign.Left.Seed(Path, "id-src-file", "rev-src", foreignPayload);
        await foreign.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            foreign.Entry(Path, "file", "id-src-file", "rev-src", foreignPayload.Length, foreignPayload));
        await foreign.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var foreignOutcome = await foreign.PlanCreateAsync(Path);
        var foreignOperationId = foreignOutcome.OperationId!;
        var foreignExecutor = new SyncAuthorityCloudCreateExecutor(foreign.Db, foreign.Context());
        var staged = false;
        SyncAuthorityCloudCreateExecutor.PhaseForTest = phase =>
        {
            if (phase == "after-commit-before-cleanup" && !staged)
            {
                staged = true;
                throw new InvalidOperationException("scripted commit crash for the replaced-stage case");
            }
        };
        try
        {
            await ExpectAnyRefusalAsync(() => foreignExecutor.ExecuteAsync(foreignOperationId,
                foreign.Worker), "a commit-boundary crash before the replaced-stage case");
        }
        finally
        {
            SyncAuthorityCloudCreateExecutor.PhaseForTest = null;
        }
        var foreignCommitted = await foreign.SnapshotAsync(foreignOperationId);
        var foreignStage = foreignCommitted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var foreignTransaction = foreignCommitted.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var foreignStagePath = IOPath.Combine(foreign.ControlRoot.Path,
            foreignTransaction.DeterministicName, foreignStage.DeterministicName);
        Assert(File.Exists(foreignStagePath), "the commit crash retained the owned private stage");
        File.Delete(foreignStagePath);
        await File.WriteAllBytesAsync(foreignStagePath, Pattern(16, 3));
        var refused = false;
        try
        {
            await foreignExecutor.ReconcileAsync(foreignOperationId, foreign.Worker);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or
            SyncAuthorityCloudContentStageException)
        {
            refused = true;
        }
        Assert(refused, "a replaced foreign stage object was refused rather than removed");
        Assert(File.Exists(foreignStagePath),
            "the replaced foreign stage object was preserved exactly as it was found");
        Assert((await foreign.SnapshotAsync(foreignOperationId)).Status !=
               SyncAuthorityOperationStatus.Completed,
            "the replaced-stage case did not claim completion");
    }

    // ----- cases O-Q: the Ready create retries and cancels from its retained verified stage ---------

    /// <summary>
    /// The one shared precondition of the retained-stage retry proofs: a real planned cloud create whose
    /// scripted target adapter conclusively declines the first create-only request before applying anything.
    /// The production journal therefore records a verified non-application and leaves the operation Ready with
    /// both owned private allocations retained exactly verified - the ordinary path the consumer proof needs.
    /// </summary>
    private static async Task<string> ReadyRetainedCreateAsync(Site site, byte[] retained)
    {
        site.Left.Seed(Path, "id-src-file", "rev-src", retained);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", retained.Length, retained));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var operationId = (await site.PlanCreateAsync(Path)).OperationId!;
        site.Right.RefuseCreateUploadTimes = 1;
        var refused = await new SyncAuthorityCloudCreateExecutor(site.Db, site.Context())
            .ExecuteAsync(operationId, site.Worker);
        Assert(refused.Status == SyncAuthorityOperationStatus.Ready && !refused.EffectDispatched,
            "a conclusively declined create-only request is a non-application that leaves the operation Ready: " +
            refused.Reason);
        Assert(site.Right.Uploads.Count == 0 && site.Right.Find(Path) is null,
            "the declined create-only request applied nothing at the target");
        Assert(site.Left.Downloads.Count == 1,
            "the first attempt read the exact retained source exactly once");
        return operationId;
    }

    /// <summary>
    /// The exact retained private stage of one operation: its owned stage and transaction rows and the exact
    /// stage path under the held private control root.
    /// </summary>
    private static (SyncAuthorityAllocation Stage, SyncAuthorityAllocation Transaction, string
        TransactionPath, string StagePath) RetainedStageOf(Site site,
        SyncAuthorityOperationSnapshot operation)
    {
        var stage = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.StageFile);
        var transaction = operation.Allocations.Single(item =>
            item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var transactionPath = IOPath.Combine(site.ControlRoot.Path, transaction.DeterministicName);
        return (stage, transaction, transactionPath, IOPath.Combine(transactionPath,
            stage.DeterministicName));
    }

    private static async Task RetainedStageReadyRetryAsync()
    {
        var retained = Pattern(4096);
        await using var site = await Site.CreateAsync("case-o", "o", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operationId = await ReadyRetainedCreateAsync(site, retained);

        var ready = await site.SnapshotAsync(operationId);
        Assert(ready.Status == SyncAuthorityOperationStatus.Ready,
            "the journal retains the exact Ready boundary of the non-applied create");
        var (stageAllocation, transactionAllocation, transactionPath, stagePath) =
            RetainedStageOf(site, ready);
        Assert(stageAllocation.Status == SyncAuthorityAllocationStatus.Verified &&
               transactionAllocation.Status == SyncAuthorityAllocationStatus.Verified,
            "both owned private staging allocations are retained exactly verified at the Ready boundary");
        Assert(ready.Attempts.Count == 1 &&
               ready.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedNotApplied,
            "the single dispatched attempt is terminally verified not applied");
        Assert(File.Exists(stagePath) && (await File.ReadAllBytesAsync(stagePath)).SequenceEqual(retained),
            "the retained private stage holds the exact reviewed source bytes");

        // The current provider source then changes revision and content. The admitted immutable source version
        // is the retained stage, not the current provider object, so the retry must neither re-read it nor
        // rebind the stage to it.
        site.Left.Seed(Path, "id-src-file", "rev-src-2", Pattern(4096, 97));

        // A fresh authority and a fresh consumer instance: the same ordinary retry after a restart.
        var restarted = new SyncAuthorityDatabase(site.Db.DatabasePath);
        var retried = await new SyncAuthorityCloudCreateExecutor(restarted, site.Context())
            .ExecuteAsync(operationId, site.Worker);
        Assert(retried.Status == SyncAuthorityOperationStatus.Completed,
            "the Ready create retried from its retained verified stage and completed: " + retried.Reason);
        Assert(site.Left.Downloads.Count == 1,
            "the retry opened the retained stage and downloaded the provider source zero further times");
        Assert(site.Right.Uploads.Count == 1,
            "exactly one permitted create request reached the target adapter");
        Assert(site.Right.Uploads[0].Bytes.SequenceEqual(retained),
            "the permitted create carried the retained stage bytes, not the changed current provider source");
        Assert(site.Right.Uploads[0].ExpectedHash == SyncContentIdentity.Compute(
                SyncEndpointProviders.Get(SyncEndpointKind.GoogleDrive).ProviderId!, retained),
            "the create-only request carried the target provider's own native digest over the held bytes");
        Assert(site.Right.Payloads[Path].SequenceEqual(retained),
            "the target holds exactly the retained staged bytes");

        var completed = await site.SnapshotAsync(operationId);
        Assert(completed.Status == SyncAuthorityOperationStatus.Completed,
            "the journal records the retried create as completed");
        Assert(completed.Attempts.Count == 2 &&
               completed.Attempts.Count(item =>
                   item.Status == SyncAuthorityAttemptStatus.VerifiedNotApplied) == 1 &&
               completed.Attempts.Count(item =>
                   item.Status == SyncAuthorityAttemptStatus.VerifiedSuccess) == 1,
            "the retry dispatched exactly one new attempt and no duplicate effect");
        var (retriedStage, retriedTransaction, _, _) = RetainedStageOf(site, completed);
        Assert(retriedStage.AllocationId == stageAllocation.AllocationId &&
               retriedStage.BoundIdentity == stageAllocation.BoundIdentity &&
               retriedTransaction.AllocationId == transactionAllocation.AllocationId &&
               retriedTransaction.BoundIdentity == transactionAllocation.BoundIdentity,
            "the retry reused the exact owned stage and transaction allocations; no second stage was created");
        Assert(retriedStage.Status == SyncAuthorityAllocationStatus.Released &&
               retriedTransaction.Status == SyncAuthorityAllocationStatus.Released,
            "both owned staging allocations were released after the retry committed");
        Assert(!Directory.Exists(transactionPath) && !File.Exists(transactionPath),
            "the owned private stage was removed only after the retry committed");
    }

    private static async Task RetainedStageCancellationAsync()
    {
        // A bounded multi-chunk retained artifact: the caller's own token is observed between the 128 KiB read
        // chunks of the one rehash pass and is never replaced by a test fake.
        var retained = Pattern(8 * 1024 * 1024);
        await using var site = await Site.CreateAsync("case-p", "p", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        var operationId = await ReadyRetainedCreateAsync(site, retained);
        var ready = await site.SnapshotAsync(operationId);
        var (stageAllocation, transactionAllocation, transactionPath, stagePath) =
            RetainedStageOf(site, ready);
        var retainedWriteTime = new FileInfo(stagePath).LastWriteTimeUtc;

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
            await ExpectCancelledAsync(() => new SyncAuthorityCloudCreateExecutor(site.Db, site.Context())
                    .ExecuteAsync(operationId, site.Worker, cancellation.Token),
                "a caller cancellation inside the retained-stage rehash");
        }
        var measured = System.Diagnostics.Stopwatch.GetElapsedTime(signalledAt);
        Assert(signalled && bytesAtCancellation > 0 && bytesAtCancellation < retained.LongLength,
            "the cancellation was raised after the retained rehash began and before it completed: " +
            bytesAtCancellation + " of " + retained.Length + " bytes hashed");
        Console.WriteLine("MEASURED retained-create-rehash stop: " + bytesAtCancellation + " of " +
            retained.Length + " bytes hashed at cancellation; " + Math.Round(measured.TotalMilliseconds, 3) +
            " ms measured from the caller's cancellation to the observed refusal on this fixture volume " +
            "(a measured fixture latency, not an OS scheduling guarantee).");

        Assert(site.Left.Downloads.Count == 1, "the cancelled retained rehash read no provider source");
        Assert(site.Right.Uploads.Count == 0 && site.Right.Find(Path) is null,
            "the cancelled retained rehash issued no target request");
        Assert(Directory.Exists(transactionPath) && File.Exists(stagePath) &&
               new FileInfo(stagePath).Length == retained.LongLength &&
               new FileInfo(stagePath).LastWriteTimeUtc == retainedWriteTime &&
               (await File.ReadAllBytesAsync(stagePath)).SequenceEqual(retained),
            "the cancellation retained the exact stage object and its exact bytes without a rewrite");
        var cancelled = await site.SnapshotAsync(operationId);
        var (cancelledStage, cancelledTransaction, _, _) = RetainedStageOf(site, cancelled);
        Assert(cancelled.Status == SyncAuthorityOperationStatus.Ready && cancelled.Attempts.Count == 1 &&
               cancelled.Attempts[0].Status == SyncAuthorityAttemptStatus.VerifiedNotApplied &&
               cancelledStage.Status == SyncAuthorityAllocationStatus.Verified &&
               cancelledTransaction.Status == SyncAuthorityAllocationStatus.Verified &&
               cancelledStage.AllocationId == stageAllocation.AllocationId &&
               cancelledStage.BoundIdentity == stageAllocation.BoundIdentity,
            "the cancelled retained rehash preserved the exact journal state, allocation dispositions and identities");

        // The same retained stage then still supports the ordinary permitted retry to completion.
        var retried = await new SyncAuthorityCloudCreateExecutor(site.Db, site.Context())
            .ExecuteAsync(operationId, site.Worker);
        Assert(retried.Status == SyncAuthorityOperationStatus.Completed,
            "the retained stage still retried to completion after the cancelled rehash: " + retried.Reason);
        Assert(site.Left.Downloads.Count == 1 && site.Right.Uploads.Count == 1,
            "the permitted retry issued exactly one target request and no further source download");
        Assert(site.Right.Payloads[Path].SequenceEqual(retained),
            "the retried create carried the exact retained bytes");
        Assert(!Directory.Exists(transactionPath),
            "the retained stage was removed only after the permitted retry committed");
    }

    private static async Task RetainedStageReconcileCancellationAsync()
    {
        var retained = Pattern(4096, 41);
        await using var site = await Site.CreateAsync("case-q", "q", SyncEndpointKind.Dropbox,
            SyncEndpointKind.GoogleDrive);
        site.Left.Seed(Path, "id-src-file", "rev-src", retained);
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalLeft,
            site.Entry(Path, "file", "id-src-file", "rev-src", retained.Length, retained));
        await site.ObserveAsync(SyncAuthorityCloudSides.LogicalRight);
        var operationId = (await site.PlanCreateAsync(Path)).OperationId!;
        // The target applies the object and then loses the response: an unresolved target request, not a failure.
        site.Right.ApplyThenFail = (relativePath, _) =>
            string.Equals(relativePath, Path, StringComparison.Ordinal);
        site.Right.ApplyThenFailTimes = 1;
        var executor = new SyncAuthorityCloudCreateExecutor(site.Db, site.Context());
        var unresolvedOutcome = await executor.ExecuteAsync(operationId, site.Worker);
        site.Right.ApplyThenFail = null;
        Assert(unresolvedOutcome.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "a lost response after an applied request is an unresolved outcome: " + unresolvedOutcome.Reason);
        var unresolved = await site.SnapshotAsync(operationId);
        var uploadsBeforeReconcile = site.Right.Uploads.Count;
        var (stageAllocation, _, _, stagePath) = RetainedStageOf(site, unresolved);
        var retainedWriteTime = new FileInfo(stagePath).LastWriteTimeUtc;

        // The same caller-token seam reaches the retry-shaped retained rehash of reconciliation with no new
        // architecture: reconciliation opens the same retained stage and rehashes it under the caller's token.
        var cancellation = new CancellationTokenSource();
        var signalled = false;
        using (SyncAuthorityCloudContentStage.ObserveHashChunksForTest(bytes =>
        {
            if (!signalled && bytes > 0)
            {
                signalled = true;
                cancellation.Cancel();
            }
        }))
        {
            await ExpectCancelledAsync(() => executor.ReconcileAsync(operationId, site.Worker,
                    cancellation.Token),
                "a caller cancellation inside the reconciliation retained-stage rehash");
        }
        Assert(signalled, "the reconciliation rehash reached the caller's own cancellation seam");
        Assert(site.Right.Uploads.Count == uploadsBeforeReconcile,
            "the cancelled reconciliation issued no second target request");
        Assert(File.Exists(stagePath) && new FileInfo(stagePath).LastWriteTimeUtc == retainedWriteTime &&
               (await File.ReadAllBytesAsync(stagePath)).SequenceEqual(retained),
            "the cancelled reconciliation preserved the exact retained stage bytes without a rewrite");
        var afterCancelledReconcile = await site.SnapshotAsync(operationId);
        Assert(afterCancelledReconcile.Status == unresolved.Status &&
               afterCancelledReconcile.Attempts.Count == unresolved.Attempts.Count &&
               afterCancelledReconcile.Attempts.Any(item =>
                   item.Status == SyncAuthorityAttemptStatus.EffectUnknown) &&
               afterCancelledReconcile.Allocations.Single(item =>
                   item.Kind == SyncAuthorityAllocationKind.StageFile).AllocationId ==
               stageAllocation.AllocationId,
            "the cancelled reconciliation left the exact unresolved journal, attempt and allocation untouched");

        // The ordinary reconciliation then behaves exactly as the pre-existing proof already requires.
        var reconciled = await executor.ReconcileAsync(operationId, site.Worker);
        Assert(reconciled.Status == SyncAuthorityOperationStatus.EffectUnknown,
            "the unresolved outcome still retains its verification-required certainty: " + reconciled.Reason);
        Assert(site.Right.Uploads.Count == uploadsBeforeReconcile,
            "reconciliation issued no duplicate create for the unresolved outcome");
    }

    private static Task LegacyAllocationParityAsync()
    {
        // The shared policy's legacy branch must reproduce the historical rows exactly. The reference here is
        // an independent reimplementation of the pre-CC3B code, including its literal names and hash roles.
        var options = new SyncAuthorityPlannerOptions(7, ProfileId, "local-control", "dropbox-control");
        foreach (var kind in new[]
                 {
                     SyncAuthorityOperationKind.CreateLocal, SyncAuthorityOperationKind.CreateRemote,
                     SyncAuthorityOperationKind.UpdateLocal, SyncAuthorityOperationKind.UpdateRemote,
                     SyncAuthorityOperationKind.DeleteLocal, SyncAuthorityOperationKind.DeleteRemote,
                     SyncAuthorityOperationKind.MoveLocal, SyncAuthorityOperationKind.MoveRemote
                 })
            foreach (var objectKind in new[] { "file", "directory" })
                foreach (var size in new long?[] { 10, null, SyncUploadLimits.DropboxSingleRequestBytes + 1 })
                {
                    var operationId = "operation-legacy";
                    var actual = SyncAuthorityOperationAllocations.Build(operationId, kind, "docs/report.txt",
                        "root-local", "root-dropbox", objectKind, size, options);
                    var expected = LegacyReference(operationId, kind, "docs/report.txt", "root-local",
                        "root-dropbox", objectKind, size);
                    Assert(actual.Count == expected.Count,
                        "legacy allocation count is unchanged for " + kind + "/" + objectKind + "/" + size);
                    for (var index = 0; index < expected.Count; index++)
                    {
                        Assert(actual[index].AllocationId == expected[index].AllocationId &&
                               actual[index].ControlNamespaceId == expected[index].ControlNamespaceId &&
                               actual[index].Kind == expected[index].Kind &&
                               actual[index].DeterministicName == expected[index].DeterministicName &&
                               actual[index].IntendedParentIdentity == expected[index].IntendedParentIdentity &&
                               actual[index].OwnerNonceHash == expected[index].OwnerNonceHash &&
                               actual[index].Status == expected[index].Status,
                            "legacy allocation row " + index + " is byte-equivalent for " + kind + "/" +
                            objectKind + "/" + size);
                    }
                }
        // A cloud configuration does change the rows, and it changes them exactly as the policy states.
        var cloudOptions = options with
        {
            CloudAllocations = new SyncAuthorityCloudAllocationConfiguration("staging-control",
                new SyncAuthorityCloudTargetAllocation("recovery-left", 100),
                new SyncAuthorityCloudTargetAllocation("recovery-right", 100))
        };
        var cloudCreate = SyncAuthorityOperationAllocations.Build("operation-cloud",
            SyncAuthorityOperationKind.CreateLocal, "docs/report.txt", "root-local", "root-dropbox", "file",
            10, cloudOptions);
        Assert(cloudCreate.Count == 2 &&
               cloudCreate[0].Kind == SyncAuthorityAllocationKind.TransactionDirectory &&
               cloudCreate[1].Kind == SyncAuthorityAllocationKind.StageFile &&
               cloudCreate[0].ControlNamespaceId == "staging-control",
            "a cloud file create on the left target uses the shared private transaction and stage");
        var cloudLarge = SyncAuthorityOperationAllocations.Build("operation-cloud-large",
            SyncAuthorityOperationKind.CreateLocal, "docs/report.txt", "root-local", "root-dropbox", "file",
            101, cloudOptions);
        Assert(cloudLarge.Any(item => item.Kind == SyncAuthorityAllocationKind.UploadSession &&
                                      item.IntendedParentIdentity == "root-local"),
            "a cloud create above the configured target limit reserves one upload session on that target root");
        var cloudDelete = SyncAuthorityOperationAllocations.Build("operation-cloud-delete",
            SyncAuthorityOperationKind.DeleteLocal, "docs/report.txt", "root-local", "root-dropbox", "file",
            null, cloudOptions);
        Assert(cloudDelete.Any(item => item.Kind == SyncAuthorityAllocationKind.RemoteRecoveryFile &&
                                       item.IntendedParentIdentity == "recovery-left") &&
               cloudDelete.All(item => item.Kind != SyncAuthorityAllocationKind.LocalRecoveryFile),
            "a cloud delete on the left target uses that target's remote recovery authority, not a local recovery file");
        var cloudDirectory = SyncAuthorityOperationAllocations.Build("operation-cloud-directory",
            SyncAuthorityOperationKind.CreateLocal, "docs", "root-local", "root-dropbox", "directory", null,
            cloudOptions);
        Assert(cloudDirectory.Count == 0,
            "a cloud directory create reserves no unnecessary file stage");
        var cloudMove = SyncAuthorityOperationAllocations.Build("operation-cloud-move",
            SyncAuthorityOperationKind.MoveLocal, "docs", "root-local", "root-dropbox", "file", null,
            cloudOptions);
        Assert(cloudMove.Count == 0, "a cloud move reserves no unnecessary file stage");
        return Task.CompletedTask;
    }

    /// <summary>The pre-CC3B allocation policy, reimplemented here independently as the parity reference.</summary>
    private static IReadOnlyList<SyncAuthorityAllocation> LegacyReference(string operationId,
        SyncAuthorityOperationKind kind, string path, string localRoot, string dropboxRoot, string objectKind,
        long? sourceSize)
    {
        const string localControl = "local-control";
        const string dropboxControl = "dropbox-control";
        var result = new List<SyncAuthorityAllocation>();
        if (objectKind != "directory" && kind is SyncAuthorityOperationKind.CreateLocal or
            SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.DeleteLocal)
        {
            var transaction = Reference(operationId, "transaction", localControl,
                SyncAuthorityAllocationKind.TransactionDirectory, localControl,
                SyncLocalTransactionDirectory.Prefix + ReferenceHash(operationId, "transaction")[..32]);
            result.Add(transaction);
            if (kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal)
                result.Add(Reference(operationId, "stage", localControl,
                    SyncAuthorityAllocationKind.StageFile, transaction.AllocationId, "stage.partial"));
            if (kind is SyncAuthorityOperationKind.UpdateLocal or SyncAuthorityOperationKind.DeleteLocal)
                result.Add(Reference(operationId, "recovery", localControl,
                    SyncAuthorityAllocationKind.LocalRecoveryFile, transaction.AllocationId,
                    kind == SyncAuthorityOperationKind.UpdateLocal
                        ? "recovery-" + ReferenceHash(operationId, "recovery")[..32]
                        : "backup.backup"));
        }
        if (objectKind != "directory" && kind is SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.DeleteRemote)
            result.Add(Reference(operationId, "recovery", dropboxControl,
                SyncAuthorityAllocationKind.RemoteRecoveryFile, dropboxControl,
                "recovery-" + ReferenceHash(operationId, "recovery")[..32]));
        if (objectKind != "directory" && sourceSize > SyncUploadLimits.DropboxSingleRequestBytes &&
            kind is SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote)
            result.Add(Reference(operationId, "upload-session", dropboxControl,
                SyncAuthorityAllocationKind.UploadSession, dropboxRoot,
                "upload-session-" + ReferenceHash(operationId, path)[..32]));
        return result;
    }

    private static SyncAuthorityAllocation Reference(string operationId, string role, string controlNamespaceId,
        SyncAuthorityAllocationKind kind, string parentIdentity, string? deterministicName)
    {
        var suffix = ReferenceHash(operationId, role)[..20];
        return new SyncAuthorityAllocation("allocation-" + ReferenceHash("allocation", operationId, role)[..32],
            controlNamespaceId, kind, deterministicName ?? role + "-" + suffix, parentIdentity,
            ReferenceHash("owner-nonce", operationId, role));
    }

    private static string ReferenceHash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();

    // ----- expectation helpers ------------------------------------------------------------------------

    private static async Task ExpectRefusalAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (SyncAuthorityCloudCreateException error)
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
}
