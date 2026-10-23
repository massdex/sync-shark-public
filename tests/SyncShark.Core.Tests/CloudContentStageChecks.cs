using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;

// CC3A focused proofs: the real provider-neutral verified cloud-content staging component over real isolated
// SQLite authority and allocation rows, a real protected Windows private stage under a real held private
// control-root lease, and the real typed cloud endpoint ports.
//
// The Dropbox side of the positive proofs is the production DropboxPilotClient together with the production
// staging component: the read is a real /files/download call into the guarded stage writer and the digests are
// recomputed by the production content-identity seams over exactly the bytes that call delivered. A scripted
// adapter supplies input for the Google side and for the cases where a misbehaving or hostile transport (a
// truncated, oversized, corrupt or lying body) is the very input being refused. No fixture replaces the
// decision under proof.
//
// Nothing here reaches a live account, credential, network, installed database, product state, schema change,
// Host wiring, activation, provider upload/delete/move or any target effect, and nothing here claims cloud
// runtime or effect completion.
internal static class CloudContentStageChecks
{
    private const string InstallationId = "installation-cloud-content-stage-316";
    private const string SourceRevision = "cloud-content-stage-316";
    private const string ProfileId = "profile-cloud-content-stage-316";
    private const long ProductSafetyReserve = 64L * 1024 * 1024;
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static int _unexpectedProviderEffects;
    private static int _wireDownloadCalls;
    private static int _wireUnexpectedRoutes;
    private static int _fixtureDownloadCalls;
    private static int _fixtureRefusedEffects;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated cloud content stage fixture root.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;
        _unexpectedProviderEffects = 0;

        await CaseAsync("A-dropbox-block-source",
            "A real Dropbox revision is staged as the exact protected private object with both native and common digests",
            DropboxBlockSourceAsync);
        await CaseAsync("B-google-sha-source",
            "A Google SHA-256 source stages through the same component and yields the Dropbox target digest over the same bytes",
            GoogleShaSourceAsync);
        await CaseAsync("C-empty-and-tiny",
            "Empty and one-byte sources stage with the exact registered empty and single-byte digests",
            EmptyAndTinyAsync);
        await CaseAsync("D-exact-byte-cap",
            "An oversized, truncated or corrupt body refuses and never writes a byte beyond the reviewed size",
            ExactByteCapAsync);
        await CaseAsync("E-source-refusals",
            "A directory, blocked, duplicated, foreign or drifted source identity refuses before any private object exists",
            SourceRefusalsAsync);
        await CaseAsync("F-drift-and-cancellation",
            "A source that drifts during the read, and a cancelled read, both refuse and retain the private stage",
            DriftAndCancellationAsync);
        await CaseAsync("G-device-space-admission",
            "The 64 MiB product reserve with checked twice-size headroom admits and refuses at the exact boundary without a 20 GiB floor",
            DeviceSpaceAdmissionAsync);
        await CaseAsync("H-allocation-authority-refusals",
            "A foreign, misnested, misnamed, non-fresh or pre-existing allocation and a foreign held root all refuse",
            AllocationAuthorityRefusalsAsync);
        await CaseAsync("I-held-identity-and-cleanup",
            "The held stage cannot be hardlinked or swapped into accepted output and cleanup is retain-versus-clean exact",
            HeldIdentityAndCleanupAsync);
        await CaseAsync("J-no-target-effect",
            "Every refusal and every success issued no upload, delete, move or recovery request",
            NoTargetEffectAsync);
        await CaseAsync("K-retained-stage-reopen",
            "A resident verified stage reopens after disposal and restart at the exact boundary capacity with zero new body reservation and no read or write, and every altered, missing, substituted, hardlinked, under-reserve or foreign-root reopen refuses while retaining the exact private evidence",
            RetainedStageReopenAsync);

        Assert(_unexpectedProviderEffects == 0,
            "no cloud content stage fixture ever reached an upload, delete, move, copy or recovery request");
        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "CloudContentStageChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "cloud-content-stage-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS cloud content stage: " + _assertions + " assertions; real Dropbox downloads" +
            " through the production staging component into a real protected Windows private stage over real" +
            " SQLite operation/allocation records, with the exact counted device-space admission, every identity," +
            " body, drift, cancellation, foreign-authority and pre-existing-transaction refusal and the explicit" +
            " retain-versus-cleanup entry, with the retained verified stage reopened at the exact resident" +
            " boundary share and every altered, missing, substituted, hardlinked, under-reserve or foreign-root" +
            " reopen refused without touching the retained evidence.");
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
        if (!condition) throw new InvalidDataException("CC3A assertion failed: " + label);
    }

    // ----- independent reference implementations and payloads ------------------------------------

    private const int BlockSize = 4 * 1024 * 1024;

    private static byte[] Pattern(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * 31 + 7) % 251);
        return payload;
    }

    private static byte[] OtherPattern(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < length; index++) payload[index] = (byte)((index * 17 + 3) % 253);
        return payload;
    }

    private static string Sha256Hex(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    // An independent reference for the Dropbox block policy, written here without the production seam.
    private static string ReferenceBlockHash(ReadOnlySpan<byte> payload)
    {
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < payload.Length; offset += BlockSize)
            aggregate.AppendData(SHA256.HashData(payload.Slice(offset,
                Math.Min(BlockSize, payload.Length - offset))));
        return Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant();
    }

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();

    private static string TransactionName(string operationId) =>
        SyncLocalTransactionDirectory.Prefix + Hash("cc3a-transaction", operationId)[..32];

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        stream.Position = 0;
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadStageAsync(SyncAuthorityCloudContentStage stage)
    {
        stage.ResetPosition();
        return await ReadAllAsync(stage.Content);
    }

    // ----- shared case harness -------------------------------------------------------------------

    private sealed record EndpointSpec(SyncEndpointKind Kind, string Provider, string ConnectionId,
        string AccountId, string RootNamespaceId, string HomeNamespaceId, string EndpointNamespaceId,
        string ProviderAccountId, string FolderId, string FolderPath);

    private static EndpointSpec DropboxEndpoint(string index, string folderId) => new(
        SyncEndpointKind.Dropbox, SyncProviders.Dropbox, "conn-d" + index, "dbid-" + index, "ns-root-" + index,
        "ns-home-" + index, "ns-root-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.Dropbox, "dbid-" + index,
            "ns-root-" + index, "ns-home-" + index),
        folderId, "/Dropbox " + index);

    private static EndpointSpec DriveEndpoint(string index, string folderId) => new(
        SyncEndpointKind.GoogleDrive, SyncProviders.GoogleDrive, "conn-g" + index, "perm-" + index,
        "key-" + index, "key-" + index, "perm-" + index,
        SyncAuthorityProviderAccountIdentity.DeriveId(SyncProviders.GoogleDrive, "perm-" + index,
            "key-" + index, "key-" + index),
        folderId, "/Drive " + index);

    private static SyncEndpoint EndpointFor(EndpointSpec spec, string folderId) =>
        SyncEndpoint.Cloud(spec.Kind, spec.ConnectionId, spec.EndpointNamespaceId, folderId, spec.FolderPath);

    private sealed record SourceRef(string Side, string Path, string FileId, string? Revision, long Size,
        string NativeHash, string ObjectId, string ObjectVersionId, string MembershipId);

    private sealed class Site
    {
        internal string CaseName = "";
        internal string Slug = "";
        internal string CaseRoot = "";
        internal string SelectedRoot = "";
        internal string NamespaceId = "";
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal SyncPair Pair = null!;
        internal EndpointSpec LeftSpec = null!;
        internal EndpointSpec RightSpec = null!;
        internal string LeftRoot = "";
        internal string RightRoot = "";
        internal SyncControlNamespaceBinding Control = null!;
        internal DropboxWire? Wire;
        internal DropboxPilotClient? LeftClient;
        internal FixtureStore? RightFixture;
        private PilotDirectoryLease? _selected;
        private PilotDirectoryLease? _control;

        internal PilotDirectoryLease ControlRoot => _control!;

        internal PilotDirectoryLease SelectedRootLease => _selected!;

        internal string Worker => "worker-cc3a-" + Slug;

        internal static async Task<Site> CreateAsync(string caseName, string index, string slug)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "The verified cloud content stage requires Windows NTFS identities and protected ACLs.");
            var site = new Site
            {
                CaseName = caseName,
                Slug = slug,
                NamespaceId = "cloud-content-stage-" + slug,
                CaseRoot = Path.Combine(_root, caseName)
            };
            Directory.CreateDirectory(site.CaseRoot);
            site.SelectedRoot = Path.Combine(site.CaseRoot, "pair-root");
            Directory.CreateDirectory(site.SelectedRoot);
            site._selected = PilotDirectoryLease.Open(site.SelectedRoot);
            site.Control = new SyncControlNamespaceBinding(InstallationId, site.NamespaceId,
                SyncControlNamespace.BindLocalForTest(site._selected, InstallationId, site.NamespaceId));
            site._control = SyncControlNamespace.OpenLocal(site.Control, site._selected);
            site.LeftSpec = DropboxEndpoint(index + "l", "id:" + caseName + "-left");
            site.RightSpec = DriveEndpoint(index + "r", "gd-" + caseName + "-right");
            site.LeftRoot = "root-" + caseName + "-left";
            site.RightRoot = "root-" + caseName + "-right";
            site.Db = new SyncAuthorityDatabase(Path.Combine(site.CaseRoot, "syncshark.db"));
            await site.Db.InitializeAsync(InstallationId, ProfileId, SourceRevision, "{}");
            await site.Db.RegisterProviderAccountAsync(site.LeftSpec.ProviderAccountId, InstallationId,
                site.LeftSpec.AccountId, site.LeftSpec.RootNamespaceId, site.LeftSpec.HomeNamespaceId,
                "CC3A left account", 1, cancellationToken: default, provider: site.LeftSpec.Provider);
            await site.Db.RegisterProviderAccountAsync(site.RightSpec.ProviderAccountId, InstallationId,
                site.RightSpec.AccountId, site.RightSpec.RootNamespaceId, site.RightSpec.HomeNamespaceId,
                "CC3A right account", 1, cancellationToken: default, provider: site.RightSpec.Provider);
            var draft = SyncCloudPairConfiguration.CreateDraft(new SyncPairEndpoints(
                EndpointFor(site.LeftSpec, site.LeftSpec.FolderId),
                EndpointFor(site.RightSpec, site.RightSpec.FolderId), SyncMode.TwoWay), "CC3A " + caseName);
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
                SyncAuthorityCloudSides.LogicalLeft, 1, InstallationId, site.LeftSpec.Provider,
                site.LeftSpec.ProviderAccountId, site.LeftSpec.EndpointNamespaceId, site.LeftSpec.FolderId,
                site.LeftSpec.FolderPath, cancellationToken: default);
            await site.Db.RegisterCloudEndpointRootBindingAsync(site.RightRoot, site.Pair.Id,
                SyncAuthorityCloudSides.LogicalRight, 1, InstallationId, site.RightSpec.Provider,
                site.RightSpec.ProviderAccountId, site.RightSpec.EndpointNamespaceId, site.RightSpec.FolderId,
                site.RightSpec.FolderPath, cancellationToken: default);
            // Both typed ports must exist before the endpoint set can be produced, so the real Dropbox
            // client is attached over an in-process endpoint and the scripted right adapter is attached
            // empty. A case that stages replaces either adapter with its own exact fixture.
            site.AttachDropboxWire("unused.bin", "id:unused", "rev-unused", Pattern(8));
            site.AttachRightFixture(null);
            return site;
        }

        internal string RootFor(string side) => side == SyncAuthorityCloudSides.LogicalLeft ? LeftRoot : RightRoot;

        internal ISyncRemoteStore StoreFor(string side) => side == SyncAuthorityCloudSides.LogicalLeft
            ? LeftClient ?? throw new InvalidDataException("The Dropbox adapter is not attached.")
            : (ISyncRemoteStore)(object)(RightFixture ??
                throw new InvalidDataException("The right adapter is not attached."));

        internal SyncAuthorityCloudEndpointSet Endpoints() => SyncAuthorityCloudEndpointSet.FromPair(Pair,
            StoreFor(SyncAuthorityCloudSides.LogicalLeft), StoreFor(SyncAuthorityCloudSides.LogicalRight),
            LeftRoot, RightRoot, LeftSpec.ProviderAccountId, RightSpec.ProviderAccountId);

        internal SyncAuthorityCloudEndpointPort Port(string side) =>
            side == SyncAuthorityCloudSides.LogicalLeft ? Endpoints().Left : Endpoints().Right;

        // The real Dropbox provider client over a real in-process HTTP endpoint, exactly as the shipped
        // adapter is constructed by the runtime.
        internal void AttachDropboxWire(string relativePath, string id, string revision, byte[] content,
            Func<WireFile, long, long, HttpResponseMessage?>? downloadOverride = null,
            Action<WireFile>? afterDownload = null)
        {
            var wire = new DropboxWire(LeftSpec.AccountId, LeftSpec.RootNamespaceId, LeftSpec.HomeNamespaceId,
                "/Left", LeftSpec.FolderId);
            wire.Add(new WireFile
            {
                RelativePath = relativePath,
                Id = id,
                Revision = revision,
                Content = content,
                ContentHash = DropboxContentHash.Compute(content),
                Size = content.Length,
                DownloadOverride = downloadOverride,
                AfterDownload = afterDownload
            });
            Wire = wire;
            LeftClient = new DropboxPilotClient(new HttpClient(wire), new SyntheticTokenSource(), "/Left",
                new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, LeftSpec.AccountId,
                    LeftSpec.RootNamespaceId, LeftSpec.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
                    DropboxProviderConfiguration.RequiredScopes).Normalize(), LeftSpec.FolderId);
        }

        internal void AttachRightFixture(byte[]? payload, params PilotRemoteEntry[] entries)
        {
            var fixture = (FixtureStore)(object)DispatchProxy.Create<ISyncRemoteStore, FixtureStore>();
            fixture.Entries = entries;
            fixture.Payload = payload;
            RightFixture = fixture;
        }

        internal async Task<SourceRef> RegisterAsync(string side, string path, string fileId, string? revision,
            long size, string nativeHash, string? identityJson = null, string? variant = null)
        {
            // The retained identity is exactly what the typed port produces unless a case deliberately
            // supplies a foreign one that must be refused.
            var suffix = Slug + "-" + side + (variant is null ? "" : "-" + variant);
            var reference = new SourceRef(side, path, fileId, revision, size, nativeHash, "object-" + suffix,
                "version-" + suffix, "membership-" + suffix);
            var port = Port(side);
            await Db.RegisterObjectAsync(reference.ObjectId, "file");
            await Db.RegisterObjectVersionAsync(reference.ObjectVersionId, reference.ObjectId, side,
                identityJson ?? port.ObservationIdentity(fileId, revision).Serialize(), nativeHash, size);
            await Db.RegisterMembershipAsync(reference.MembershipId, reference.ObjectId, RootFor(side), path, 1);
            return reference;
        }

        internal async Task<string> CreateOperationAsync(SourceRef source, string? transactionName = null,
            string? allocationNamespaceId = null, string? stageParent = null)
        {
            var operationId = "operation-cc3a-" + Slug;
            var transactionId = "allocation-transaction-cc3a-" + Slug;
            var stageId = "allocation-stage-cc3a-" + Slug;
            var transaction = new SyncAuthorityAllocation(transactionId, allocationNamespaceId ?? NamespaceId,
                SyncAuthorityAllocationKind.TransactionDirectory, transactionName ?? TransactionName(operationId),
                allocationNamespaceId ?? NamespaceId, Hash("cc3a-nonce", operationId, "transaction"));
            var stage = new SyncAuthorityAllocation(stageId, allocationNamespaceId ?? NamespaceId,
                SyncAuthorityAllocationKind.StageFile, SyncLocalTransactionDirectory.StageName,
                stageParent ?? transactionId, Hash("cc3a-nonce", operationId, "stage"));
            var member = new SyncAuthorityMember("member-cc3a-" + Slug, Pair.Id, source.ObjectId, "source",
                RootFor(source.Side), source.MembershipId, source.ObjectVersionId, 1, source.Path, "{}");
            var plan = new SyncAuthorityOperationPlan(operationId, "idempotency-" + operationId,
                SyncAuthorityOperationKind.CreateRemote, Pair.Id, source.ObjectId, 1, ProfileId, "{}", 100,
                new[] { member }, Array.Empty<SyncAuthorityDependency>(), new[] { transaction, stage });
            await Db.CreateOperationAsync(plan, Admission);
            return operationId;
        }

        internal async Task<SyncAuthorityLease> LeaseAsync(string operationId) =>
            await Db.AcquireOperationLeaseAsync(operationId, Worker, TimeSpan.FromMinutes(5));

        internal async Task ReleaseAsync(SyncAuthorityLease lease) =>
            await Db.ReleaseLeaseAsync(lease, CancellationToken.None);

        internal async Task<SyncAuthorityOperationSnapshot> SnapshotAsync(string operationId) =>
            await Db.LoadOperationAsync(operationId);

        internal static SyncAuthorityAllocation OfKind(SyncAuthorityOperationSnapshot operation,
            SyncAuthorityAllocationKind kind) => operation.Allocations.Single(item => item.Kind == kind);

        internal string TransactionPath(SyncAuthorityOperationSnapshot operation) =>
            Path.Combine(ControlRoot.Path, OfKind(operation, SyncAuthorityAllocationKind.TransactionDirectory)
                .DeterministicName);

        internal string StagePath(SyncAuthorityOperationSnapshot operation) =>
            Path.Combine(TransactionPath(operation),
                OfKind(operation, SyncAuthorityAllocationKind.StageFile).DeterministicName);

        internal void SetDeviceSpace(long? available) =>
            SyncAuthorityCloudContentStage.AvailableDeviceBytesForTest =
                available is null ? null : _ => available.Value;

        internal async ValueTask DisposeAsync()
        {
            SyncAuthorityCloudContentStage.AvailableDeviceBytesForTest = null;
            _control?.Dispose();
            _selected?.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            await Db.TransitionCommandRequestAsync(Admission.CommandRequestId, "completed");
        }
    }

    // ----- expectation helpers -------------------------------------------------------------------

    private static async Task<Exception?> ExpectRefusalAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (SyncAuthorityCloudContentStageException error)
        {
            Assert(true, label + " refused: " + error.Message);
            return error;
        }
        Assert(false, label + " must refuse and did not");
        return null;
    }

    private static async Task AssertNothingCreatedAsync(Site site, string operationId, string label)
    {
        var operation = await site.SnapshotAsync(operationId);
        Assert(!Directory.Exists(site.TransactionPath(operation)),
            label + " created no private transaction directory");
        Assert(!File.Exists(site.StagePath(operation)), label + " created no private stage file");
        Assert(OfKindStatus(operation, SyncAuthorityAllocationKind.TransactionDirectory) ==
                SyncAuthorityAllocationStatus.Authorized &&
            OfKindStatus(operation, SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Authorized,
            label + " left both fresh allocations Authorized");
        Assert(operation.Attempts.Count == 0, label + " issued no target attempt");
    }

    private static SyncAuthorityAllocationStatus OfKindStatus(SyncAuthorityOperationSnapshot operation,
        SyncAuthorityAllocationKind kind) => Site.OfKind(operation, kind).Status;

    private static async Task<Exception?> PrepareExpectRefusalAsync(Site site, string operationId,
        SourceRef source, string label)
    {
        var lease = await site.LeaseAsync(operationId);
        try
        {
            return await ExpectRefusalAsync(() => SyncAuthorityCloudContentStage.PrepareAsync(site.Db, lease,
                "member-cc3a-" + site.Slug, site.Port(source.Side), site.Control, site.ControlRoot,
                CancellationToken.None), label);
        }
        finally
        {
            await site.ReleaseAsync(lease);
        }
    }

    private static async Task ExpectAnyRefusalAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException
            or NotSupportedException or ArgumentException)
        {
            Assert(true, label + " refused: " + error.GetType().Name + ": " + error.Message);
            return;
        }
        Assert(false, label + " must refuse and did not");
    }

    private static async Task AssertCancelledAsync(Func<Task> body, string label)
    {
        try
        {
            await body();
        }
        catch (OperationCanceledException)
        {
            Assert(true, label + " was cancelled without a verified stage");
            return;
        }
        Assert(false, label + " must be cancelled and was not");
    }

    private static async Task<SyncAuthorityCloudContentStage> PrepareAsync(Site site, string operationId,
        SourceRef source, string? memberId = null)
    {
        var lease = await site.LeaseAsync(operationId);
        try
        {
            return await SyncAuthorityCloudContentStage.PrepareAsync(site.Db, lease,
                memberId ?? "member-cc3a-" + site.Slug, site.Port(source.Side), site.Control, site.ControlRoot,
                CancellationToken.None);
        }
        finally
        {
            await site.ReleaseAsync(lease);
        }
    }

    private static async Task<SyncAuthorityCloudStageCleanup> CleanupAsync(Site site, string operationId) =>
        await SyncAuthorityCloudContentStage.CleanupAsync(site.Db, operationId, site.Worker, site.Control,
            site.ControlRoot, CancellationToken.None);

    private static PilotRemoteEntry GoogleEntry(string path, string id, string revision, byte[] content) =>
        new(path, PilotEntryKind.File, id, revision, content.Length, Sha256Hex(content));

    private static bool Same(byte[] left, byte[] right) => left.AsSpan().SequenceEqual(right);

    // ----- case A: a real Dropbox revision staged through the production adapter -------------------

    private static async Task DropboxBlockSourceAsync()
    {
        var payload = Pattern(BlockSize + 123);
        var hash = ReferenceBlockHash(payload);
        await using var site = await Site.CreateAsync("A-dropbox-block-source", "a", "a");
        site.AttachDropboxWire("stage-source.bin", "id:cc3a-a", "rev-cc3a-a", payload);
        var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "stage-source.bin",
            "id:cc3a-a", "rev-cc3a-a", payload.Length, hash);
        var operationId = await site.CreateOperationAsync(source);
        var stage = await PrepareAsync(site, operationId, source);
        var operation = await site.SnapshotAsync(operationId);

        Assert(stage.Size == payload.Length && stage.NativeHash == hash &&
            stage.CommonSha256 == Sha256Hex(payload),
            "the stage retained the exact reviewed size with the provider-native and common digests");
        Assert(stage.Source.Provider == SyncProviders.Dropbox &&
            stage.Source.ProviderAccountId == site.LeftSpec.ProviderAccountId &&
            stage.Source.NamespaceId == site.LeftSpec.EndpointNamespaceId &&
            stage.Source.RootBindingId == site.LeftRoot &&
            stage.Source.FileId == "id:cc3a-a" && stage.Source.Revision == "rev-cc3a-a" &&
            stage.Source.NativeAlgorithm == SyncContentIdentity.DropboxBlockSha256,
            "the immutable source evidence binds the exact typed port, root, provider account, namespace, object version, file id and revision");
        Assert(stage.Source.ObjectVersionId == source.ObjectVersionId,
            "the source evidence names the exact retained object version");
        Assert(stage.StagePath == site.StagePath(operation) && stage.TransactionPath ==
            site.TransactionPath(operation),
            "the stage and its private transaction are exactly the operation's owned allocation paths");
        Assert(string.IsNullOrEmpty(site.Pair.LocalRoot),
            "the cloud pair carries no NTFS selected root and the stage is never projected into it");
        Assert(stage.StagePath.StartsWith(site.ControlRoot.Path, StringComparison.OrdinalIgnoreCase) &&
            !stage.StagePath.StartsWith(site.SelectedRoot, StringComparison.OrdinalIgnoreCase),
            "the stage lives inside the held private control namespace and not in the fixture's selected root");
        Assert(File.Exists(stage.StagePath) && new FileInfo(stage.StagePath).Length == payload.Length,
            "the private stage file exists with exactly the reviewed length");
        Assert(Same(await ReadStageAsync(stage), payload),
            "the retained bytes are exactly the source revision's bytes");
        var expectedEntries = new[]
        {
            SyncLocalTransactionDirectory.StageName,
            SyncLocalTransactionDirectory.StageName + SyncLocalFileAuthority.AccessRecoverySuffix
        }.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var entries = Directory.GetFileSystemEntries(stage.TransactionPath)
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert(entries.SequenceEqual(expectedEntries),
            "the private transaction retains only the owned stage file plus the stage's own writable-object access-recovery envelope");
        stage.VerifyHeld();
        Assert(stage.HeldFileIdentity.VolumeSerialNumber != 0,
            "the held exact single-link stage identity is readable");

        var google = await stage.ComputeNativeDigestAsync(SyncProviders.GoogleDrive);
        var dropbox = await stage.ComputeNativeDigestAsync(SyncProviders.Dropbox);
        Assert(google.Digest == Sha256Hex(payload) && google.Algorithm == SyncContentIdentity.Sha256 &&
            google.Provider == SyncProviders.GoogleDrive && google.Size == payload.Length,
            "the cross-provider target digest is the Google whole-file SHA-256 over the same actual bytes");
        Assert(dropbox.Digest == hash && dropbox.Algorithm == SyncContentIdentity.DropboxBlockSha256,
            "the source provider's own native digest is reproduced over the same actual bytes");
        Assert(!SyncContentIdentity.IdentitiesMatch(google.Algorithm, google.Digest, dropbox.Algorithm,
                dropbox.Digest) &&
            SyncContentIdentity.IdentitiesMatch(SyncContentIdentity.DropboxBlockSha256, dropbox.Digest,
                SyncContentIdentity.DropboxBlockSha256, hash),
            "two different native algorithms are never relabelled into a comparison and the native digest still matches its own algorithm");

        var transaction = Site.OfKind(operation, SyncAuthorityAllocationKind.TransactionDirectory);
        var stageAllocation = Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile);
        Assert(transaction.Status == SyncAuthorityAllocationStatus.Verified &&
            stageAllocation.Status == SyncAuthorityAllocationStatus.Verified,
            "both owned allocations reached the exact Verified state");
        Assert(stageAllocation.BoundIdentity == stage.StageIdentity &&
            stageAllocation.ContentHash == hash && stageAllocation.Size == payload.Length &&
            transaction.BoundIdentity == stage.TransactionIdentity &&
            transaction.ContentHash is { Length: 64 },
            "the allocations retain the exact held stage identity, native digest and private security authority");
        Assert(operation.Attempts.Count == 0 &&
            operation.Status == SyncAuthorityOperationStatus.Leased,
            "staging starts no target attempt and leaves the operation at its pre-effect lease boundary");
        Assert(site.Wire!.DownloadCalls == 1 && site.Wire.ListCalls == 0 &&
            site.Wire.FileMetadataCalls == 4 &&
            site.Wire.UnexpectedRoutes == 0,
            "the stage and the real download adapter each checked exact source metadata before and after the one download, with no recursive listing or unexpected route");
        Assert(site.Wire.MetadataCalls > 0,
            "the real adapter verified the exact root identity through the provider");

        await stage.DisposeAsync();
        Assert(File.Exists(site.StagePath(operation)) && Directory.Exists(site.TransactionPath(operation)),
            "stage disposal closed only the held handles and deleted nothing");
        var afterDisposeEntries = Directory.GetFileSystemEntries(site.TransactionPath(operation));
        Assert(afterDisposeEntries.Length == 1 &&
            Path.GetFileName(afterDisposeEntries[0]) == SyncLocalTransactionDirectory.StageName,
            "disposal removed only the stage's own access-recovery envelope and left the verified stage file");
        var afterDispose = await site.SnapshotAsync(operationId);
        Assert(Site.OfKind(afterDispose, SyncAuthorityAllocationKind.StageFile).Status ==
            SyncAuthorityAllocationStatus.Verified,
            "stage disposal changed no allocation state");

        var cleanup = await CleanupAsync(site, operationId);
        Assert(cleanup.Cleaned && cleanup.JournalState == "no-target-attempt-issued" &&
            cleanup.StageRemoved && cleanup.TransactionRemoved && cleanup.ResidualPaths.Count == 0,
            "the explicit cleanup removed the owned stage and its own empty transaction with no residual");
        Assert(!File.Exists(site.StagePath(operation)) &&
            !Directory.Exists(site.TransactionPath(operation)),
            "both private paths are absent after the explicit cleanup");
        var released = await site.SnapshotAsync(operationId);
        Assert(Site.OfKind(released, SyncAuthorityAllocationKind.StageFile).Status ==
                SyncAuthorityAllocationStatus.Released &&
            Site.OfKind(released, SyncAuthorityAllocationKind.TransactionDirectory).Status ==
                SyncAuthorityAllocationStatus.Released,
            "both allocations were released by the explicit cleanup");
        Assert(!Directory.Exists(site.SelectedRoot + Path.DirectorySeparatorChar +
                SyncLocalTransactionDirectory.Prefix),
            "no second staging directory scheme was created beside the selected root");
    }

    // ----- case B: a Google SHA-256 source through the same provider-neutral component ------------

    private static async Task GoogleShaSourceAsync()
    {
        var payload = Pattern(3000);
        var hash = Sha256Hex(payload);
        await using var site = await Site.CreateAsync("B-google-sha-source", "b", "b");
        site.AttachRightFixture(payload,
            GoogleEntry("docs/notes.txt", "gd-cc3a-b", "gdr-cc3a-b", payload));
        var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "docs/notes.txt",
            "gd-cc3a-b", "gdr-cc3a-b", payload.Length, hash);
        var operationId = await site.CreateOperationAsync(source);
        var stage = await PrepareAsync(site, operationId, source);

        Assert(stage.Source.Provider == SyncProviders.GoogleDrive &&
            stage.Source.RootBindingId == site.RightRoot &&
            stage.Source.RelativePath == "docs/notes.txt" &&
            stage.Source.NativeAlgorithm == SyncContentIdentity.Sha256 &&
            stage.NativeHash == hash && stage.CommonSha256 == hash,
            "the Google logical-right source stages through the same component with its own registered SHA-256");
        Assert(Same(await ReadStageAsync(stage), payload),
            "the retained bytes are the Google revision's bytes");
        var dropbox = await stage.ComputeNativeDigestAsync(SyncProviders.Dropbox);
        Assert(dropbox.Digest == ReferenceBlockHash(payload) &&
            dropbox.Algorithm == SyncContentIdentity.DropboxBlockSha256,
            "the Dropbox target native digest over the identical actual bytes equals the independent block reference");
        Assert((await stage.ComputeNativeDigestAsync(SyncProviders.GoogleDrive)).Digest == hash,
            "the Google target digest equals the retained native digest");
        Assert(site.RightFixture!.ListCalls == 2 && site.RightFixture.DownloadCalls == 1 &&
            site.RightFixture.RefusedEffects == 0,
            "the synthetic source adapter was listed twice, downloaded exactly once and asked for nothing else");
        var snapshot = await site.SnapshotAsync(operationId);
        Assert(Site.OfKind(snapshot, SyncAuthorityAllocationKind.StageFile).Status ==
            SyncAuthorityAllocationStatus.Verified,
            "the Google source produced the same verified allocation state");
        await stage.DisposeAsync();
        var cleanup = await CleanupAsync(site, operationId);
        Assert(cleanup.Cleaned && cleanup.ResidualPaths.Count == 0,
            "the Google source stage and its empty transaction were removed by the explicit cleanup");
    }

    // ----- case C: empty and one-byte sources -----------------------------------------------------

    private static async Task EmptyAndTinyAsync()
    {
        var empty = Array.Empty<byte>();
        var emptyHash = ReferenceBlockHash(empty);
        Assert(emptyHash == Sha256Hex(empty) &&
            emptyHash == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            "the independent empty block reference is the standard empty SHA-256");
        await using (var site = await Site.CreateAsync("C-empty-source", "c1", "c1"))
        {
            site.AttachDropboxWire("empty.bin", "id:cc3a-empty", "rev-cc3a-empty", empty);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "empty.bin",
                "id:cc3a-empty", "rev-cc3a-empty", 0, emptyHash);
            var operationId = await site.CreateOperationAsync(source);
            var stage = await PrepareAsync(site, operationId, source);
            Assert(stage.Size == 0 && stage.NativeHash == emptyHash && stage.CommonSha256 == emptyHash,
                "an empty source stages with the exact registered empty digests");
            Assert(new FileInfo(stage.StagePath).Length == 0, "the empty stage file has exactly zero bytes");
            Assert(Same(await ReadStageAsync(stage), empty), "the empty stage retains no bytes");
            Assert((await stage.ComputeNativeDigestAsync(SyncProviders.GoogleDrive)).Digest == emptyHash,
                "the empty cross-provider target digest is the empty SHA-256");
            Assert(site.Wire!.DownloadCalls == 1, "the empty revision was downloaded exactly once");
            await stage.DisposeAsync();
            var cleanup = await CleanupAsync(site, operationId);
            Assert(cleanup.Cleaned && cleanup.StageRemoved && cleanup.TransactionRemoved,
                "the empty stage and its empty transaction were removed by the explicit cleanup");
        }

        var single = Pattern(1);
        var singleHash = Sha256Hex(single);
        await using (var site = await Site.CreateAsync("C-tiny-source", "c2", "c2"))
        {
            site.AttachRightFixture(single, GoogleEntry("tiny.bin", "gd-cc3a-c2", "gdr-cc3a-c2", single));
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "tiny.bin",
                "gd-cc3a-c2", "gdr-cc3a-c2", 1, singleHash);
            var operationId = await site.CreateOperationAsync(source);
            var stage = await PrepareAsync(site, operationId, source);
            Assert(stage.Size == 1 && stage.NativeHash == singleHash &&
                stage.CommonSha256 == singleHash &&
                (await stage.ComputeNativeDigestAsync(SyncProviders.Dropbox)).Digest ==
                    ReferenceBlockHash(single),
                "a one-byte source stages with its exact native, common and target digests");
            await stage.DisposeAsync();
            var cleanup = await CleanupAsync(site, operationId);
            Assert(cleanup.Cleaned && cleanup.StageRemoved,
                "the one-byte stage and its empty transaction were removed by the explicit cleanup");
        }
    }

    // ----- case D: the exact byte cap -------------------------------------------------------------

    private static async Task ExactByteCapAsync()
    {
        var payload = Pattern(9000);
        var hash = Sha256Hex(payload);

        await using (var site = await Site.CreateAsync("D-oversized-body", "d1", "d1"))
        {
            var operationId = await OversizedSiteAsync(site, payload, hash, 64);
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            Assert(File.Exists(stagePath) && new FileInfo(stagePath).Length == payload.Length,
                "the excess bytes were refused before they reached the stage and exactly the reviewed bytes are present");
            Assert(Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).Status !=
                SyncAuthorityAllocationStatus.Verified,
                "an oversized body produced no verified stage allocation");
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the exact owned oversized stage was removed by the explicit identity-checked cleanup");
            Assert(!File.Exists(stagePath), "the oversized stage path is absent after cleanup");
        }

        await using (var site = await Site.CreateAsync("D-truncated-body", "d2", "d2"))
        {
            site.AttachRightFixture(payload, GoogleEntry("short.bin", "gd-cc3a-d2", "gdr-cc3a-d2", payload));
            site.RightFixture!.OmitTail = 1;
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "short.bin",
                "gd-cc3a-d2", "gdr-cc3a-d2", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            await PrepareExpectRefusalAsync(site, operationId, source, "a truncated body");
            var operation = await site.SnapshotAsync(operationId);
            Assert(File.Exists(site.StagePath(operation)) &&
                new FileInfo(site.StagePath(operation)).Length == payload.Length - 1,
                "the short body left exactly the truncated bytes as the exact residual length");
            Assert(Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).Status !=
                SyncAuthorityAllocationStatus.Verified,
                "a truncated body produced no verified stage allocation");
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the retained truncated stage was removed only by the explicit identity-checked cleanup");
        }

        await using (var site = await Site.CreateAsync("D-corrupt-body", "d3", "d3"))
        {
            site.AttachRightFixture(OtherPattern(payload.Length),
                GoogleEntry("corrupt.bin", "gd-cc3a-d3", "gdr-cc3a-d3", payload));
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "corrupt.bin",
                "gd-cc3a-d3", "gdr-cc3a-d3", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            await PrepareExpectRefusalAsync(site, operationId, source, "a same-length corrupt body");
            var operation = await site.SnapshotAsync(operationId);
            Assert(File.Exists(site.StagePath(operation)) &&
                new FileInfo(site.StagePath(operation)).Length == payload.Length,
                "the corrupt same-length body left the exact residual stage as retained evidence");
            Assert(Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).Status !=
                SyncAuthorityAllocationStatus.Verified,
                "a corrupt body produced no verified stage allocation");
            var cleanup = await CleanupAsync(site, operationId);
            Assert(cleanup.Cleaned && cleanup.StageRemoved,
                "the retained corrupt stage was removed only by the explicit identity-checked cleanup");
        }
    }

    private static async Task<string> OversizedSiteAsync(Site site, byte[] payload, string hash,
        int extraBytes)
    {
        site.AttachRightFixture(payload, GoogleEntry("big.bin", "gd-cc3a-d1", "gdr-cc3a-d1", payload));
        site.RightFixture!.ExtraBytes = extraBytes;
        var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "big.bin",
            "gd-cc3a-d1", "gdr-cc3a-d1", payload.Length, hash);
        var operationId = await site.CreateOperationAsync(source);
        var lease = await site.LeaseAsync(operationId);
        try
        {
            var error = await ExpectRefusalAsync(() => SyncAuthorityCloudContentStage.PrepareAsync(site.Db,
                lease, "member-cc3a-" + site.Slug, site.Port(source.Side), site.Control, site.ControlRoot,
                CancellationToken.None), "an oversized body");
            Assert(error is not null &&
                error.Message.Contains("exceeds the exact reviewed object size", StringComparison.Ordinal),
                "the refusal names the exact reviewed object size the excess bytes exceeded");
        }
        finally
        {
            await site.ReleaseAsync(lease);
        }
        return operationId;
    }

    // ----- case E: source identity and listing refusals before any private object exists -----------

    private static async Task SourceRefusalsAsync()
    {
        var content = Pattern(512);
        var hash = Sha256Hex(content);
        var other = Sha256Hex(OtherPattern(512));

        await AssertSourceRefusalAsync("E-directory-source", "e1", "docs/folder", "gd-dir", null, 0,
            Sha256Hex(Array.Empty<byte>()),
            new PilotRemoteEntry("docs/folder", PilotEntryKind.Directory, "gd-dir", null, 0, null),
            "a directory source entry");

        await AssertSourceRefusalAsync("E-blocked-source", "e2", "docs/native.docx", "gd-nat", "gdr-nat",
            content.Length, hash,
            GoogleEntry("docs/native.docx", "gd-nat", "gdr-nat", content) with
            {
                TransferBlockedReason = "This item is cloud-native and has no downloadable bytes."
            },
            "a cloud-native blocked source entry");

        await AssertSourceRefusalsAsync("E-duplicate-path", "e3", "docs/twice.bin", "gd-dup-a", "r-a",
            content.Length, hash, new[]
            {
                GoogleEntry("docs/twice.bin", "gd-dup-a", "r-a", content),
                GoogleEntry("docs/twice.bin", "gd-dup-b", "r-b", content)
            }, "a duplicated retained source path");

        await AssertSourceRefusalAsync("E-wrong-file-id", "e4", "docs/id.bin", "gd-expected", "r-id",
            content.Length, hash, GoogleEntry("docs/id.bin", "gd-actual", "r-id", content),
            "a source file id mismatch");

        await AssertSourceRefusalAsync("E-wrong-revision", "e5", "docs/rev.bin", "gd-rev", "r-expected",
            content.Length, hash, GoogleEntry("docs/rev.bin", "gd-rev", "r-actual", content),
            "a source revision mismatch");

        await AssertSourceRefusalAsync("E-wrong-size", "e6", "docs/size.bin", "gd-size", "r-size",
            content.Length + 1, hash, GoogleEntry("docs/size.bin", "gd-size", "r-size", content),
            "a source size mismatch");

        await AssertSourceRefusalAsync("E-wrong-hash", "e7", "docs/hash.bin", "gd-hash", "r-hash",
            content.Length, other, GoogleEntry("docs/hash.bin", "gd-hash", "r-hash", content),
            "a source native digest mismatch");

        await AssertForeignIdentityAsync("E-foreign-provider", "e8",
            new SyncAuthorityCloudObservationIdentity(SyncProviders.GoogleDrive, "perm-e8", "key-e8",
                "gd-foreign", "r-foreign", SyncContentIdentity.Sha256).Serialize(),
            "a foreign provider identity on the typed port");

        await AssertForeignIdentityAsync("E-foreign-algorithm", "e9",
            new SyncAuthorityCloudObservationIdentity(SyncProviders.Dropbox, "dbid-e9l", "ns-root-e9l",
                "id:foreign-algorithm", "r-foreign", SyncContentIdentity.Md5).Serialize(),
            "an unregistered content algorithm identity");

        await AssertForeignIdentityAsync("E-foreign-account", "e10",
            new SyncAuthorityCloudObservationIdentity(SyncProviders.Dropbox, "dbid-other-account",
                "ns-root-e10l", "id:foreign-account", "r-foreign",
                SyncContentIdentity.DropboxBlockSha256).Serialize(),
            "a foreign provider account identity");

        await AssertForeignIdentityAsync("E-foreign-namespace", "e11",
            new SyncAuthorityCloudObservationIdentity(SyncProviders.Dropbox, "dbid-e11l",
                "ns-root-other-namespace", "id:foreign-namespace", "r-foreign",
                SyncContentIdentity.DropboxBlockSha256).Serialize(),
            "a foreign native namespace identity");
    }

    private static async Task AssertSourceRefusalAsync(string caseName, string slug, string path,
        string fileId, string? revision, long size, string registeredHash, PilotRemoteEntry entry,
        string label) =>
        await AssertSourceRefusalsAsync(caseName, slug, path, fileId, revision, size, registeredHash,
            new[] { entry }, label);

    private static async Task AssertSourceRefusalsAsync(string caseName, string slug, string path,
        string fileId, string? revision, long size, string registeredHash, PilotRemoteEntry[] entries,
        string label)
    {
        await using var site = await Site.CreateAsync(caseName, slug, slug);
        site.AttachRightFixture(new byte[16], entries);
        var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, path, fileId, revision,
            size, registeredHash);
        var operationId = await site.CreateOperationAsync(source);
        await PrepareExpectRefusalAsync(site, operationId, source, label);
        await AssertNothingCreatedAsync(site, operationId, label);
        Assert(site.RightFixture!.DownloadCalls == 0, label + " read no source body");
    }

    private static async Task AssertForeignIdentityAsync(string caseName, string slug, string identityJson,
        string label)
    {
        var content = Pattern(256);
        var hash = ReferenceBlockHash(content);
        await using var site = await Site.CreateAsync(caseName, slug, slug);
        site.AttachDropboxWire("foreign.bin", "id:foreign-listed", "r-listed", content);
        site.AttachRightFixture(new byte[16], GoogleEntry("unused.bin", "gd-unused", "r-unused", content));
        var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "foreign.bin",
            "id:foreign", "r-foreign", content.Length, hash, identityJson);
        var operationId = await site.CreateOperationAsync(source);
        await PrepareExpectRefusalAsync(site, operationId, source, label);
        await AssertNothingCreatedAsync(site, operationId, label);
        Assert(site.Wire!.DownloadCalls == 0 && site.Wire.ListCalls == 0,
            label + " reached the provider only after the retained identity was refused");
    }

    // ----- case F: source drift and cancellation ---------------------------------------------------

    private static async Task DriftAndCancellationAsync()
    {
        var payload = Pattern(2048);
        var hash = ReferenceBlockHash(payload);

        await using (var site = await Site.CreateAsync("F-source-drift", "f1", "f1"))
        {
            site.AttachDropboxWire("drift.bin", "id:cc3a-f1", "rev-cc3a-f1", payload,
                afterDownload: file => file.Revision = "rev-cc3a-f1-drifted");
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "drift.bin",
                "id:cc3a-f1", "rev-cc3a-f1", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var lease = await site.LeaseAsync(operationId);
            try
            {
                await ExpectAnyRefusalAsync(() => SyncAuthorityCloudContentStage.PrepareAsync(site.Db, lease,
                    "member-cc3a-" + site.Slug, site.Port(source.Side), site.Control, site.ControlRoot,
                    CancellationToken.None),
                    "a source revision that drifted during the read through the real adapter");
            }
            finally
            {
                await site.ReleaseAsync(lease);
            }
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            Assert(File.Exists(stagePath) && new FileInfo(stagePath).Length == payload.Length,
                "a drifted source retained the exact completed private stage as evidence");
            Assert(Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).Status !=
                SyncAuthorityAllocationStatus.Verified,
                "a drifted source produced no verified stage allocation");
            Assert(site.Wire!.DownloadCalls == 1,
                "the real adapter read exactly one exact revision before refusing the drift");
            var cleanup = await CleanupAsync(site, operationId);
            Assert(cleanup.Cleaned && cleanup.StageRemoved,
                "the retained drifted stage was removed only by the explicit identity-checked cleanup");
        }

        await using (var site = await Site.CreateAsync("F-source-drift-readback", "f3", "f3"))
        {
            site.AttachRightFixture(payload,
                GoogleEntry("readback.bin", "gd-cc3a-f3", "gdr-cc3a-f3", payload));
            var fixture = site.RightFixture!;
            fixture.AfterDownload = () => fixture.Entries = new[]
            {
                GoogleEntry("readback.bin", "gd-cc3a-f3", "gdr-cc3a-f3-drifted", payload)
            };
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "readback.bin",
                "gd-cc3a-f3", "gdr-cc3a-f3", payload.Length, Sha256Hex(payload));
            var operationId = await site.CreateOperationAsync(source);
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a source revision that drifted before the exact readback");
            Assert(error is not null &&
                error.Message.Contains("revision differs from the exact retained native revision",
                    StringComparison.Ordinal),
                "the exact post-read readback refuses a drifted source revision");
            var operation = await site.SnapshotAsync(operationId);
            Assert(File.Exists(site.StagePath(operation)) &&
                new FileInfo(site.StagePath(operation)).Length == payload.Length,
                "the drifted readback retained the completed private stage as evidence");
            Assert(Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).Status !=
                SyncAuthorityAllocationStatus.Verified,
                "the drifted readback produced no verified stage allocation");
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the retained drifted readback stage was removed by the explicit cleanup");
        }

        await using (var site = await Site.CreateAsync("F-cancellation", "f2", "f2"))
        {
            var cancellation = new CancellationTokenSource();
            site.AttachRightFixture(payload,
                GoogleEntry("cancel.bin", "gd-cc3a-f2", "gdr-cc3a-f2", payload));
            site.RightFixture!.DuringDownload = cancellation.Cancel;
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "cancel.bin",
                "gd-cc3a-f2", "gdr-cc3a-f2", payload.Length, Sha256Hex(payload));
            var operationId = await site.CreateOperationAsync(source);
            var lease = await site.LeaseAsync(operationId);
            try
            {
                await AssertCancelledAsync(() => SyncAuthorityCloudContentStage.PrepareAsync(site.Db,
                    lease, "member-cc3a-" + site.Slug, site.Port(source.Side), site.Control, site.ControlRoot,
                    cancellation.Token), "a cancelled cloud content stage");
            }
            finally
            {
                await site.ReleaseAsync(lease);
            }
            var operation = await site.SnapshotAsync(operationId);
            Assert(File.Exists(site.StagePath(operation)),
                "cancellation retained the exact private stage instead of deleting evidence");
            Assert(Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).Status !=
                SyncAuthorityAllocationStatus.Verified,
                "cancellation produced no verified stage allocation");
            Assert(operation.Attempts.Count == 0 &&
                operation.Status == SyncAuthorityOperationStatus.Leased,
                "cancellation issued no target attempt and left the operation leaseable");
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the retained cancelled stage was removed only by the explicit cleanup");
        }
    }

    // ----- case G: the counted device-space admission ----------------------------------------------

    private static long RequiredStagingBytes(long size) => checked(size * 2) + ProductSafetyReserve;

    private static async Task DeviceSpaceAdmissionAsync()
    {
        var payload = Pattern(4096);
        var hash = ReferenceBlockHash(payload);
        var required = RequiredStagingBytes(payload.Length);
        Assert(SyncAuthorityCloudContentStage.StagingSafetyReserveBytes == ProductSafetyReserve &&
            ProductSafetyReserve == 64L * 1024 * 1024,
            "the production staging reserve is the shipped 64 MiB product reserve and not a 20 GiB Parent scratch floor");
        Assert(required == 8192 + 64L * 1024 * 1024,
            "the required private space is the checked twice-file-size headroom plus the shipped reserve");

        await using (var site = await Site.CreateAsync("G-just-below", "g1", "g1"))
        {
            site.AttachDropboxWire("space.bin", "id:cc3a-g1", "rev-cc3a-g1", payload);
            site.SetDeviceSpace(required - 1);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "space.bin",
                "id:cc3a-g1", "rev-cc3a-g1", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a staging volume one byte below the exact required headroom");
            Assert(error is not null &&
                error.Message.Contains("cannot admit", StringComparison.Ordinal),
                "the just-below refusal is the shipped staging-volume admission rather than a Parent scratch policy");
            await AssertNothingCreatedAsync(site, operationId, "the just-below observation");
            Assert(site.Wire!.DownloadCalls == 0,
                "the just-below observation failed before any private object was created or read");
        }

        await using (var site = await Site.CreateAsync("G-at-threshold", "g2", "g2"))
        {
            site.AttachDropboxWire("space.bin", "id:cc3a-g2", "rev-cc3a-g2", payload);
            site.SetDeviceSpace(required);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "space.bin",
                "id:cc3a-g2", "rev-cc3a-g2", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var stage = await PrepareAsync(site, operationId, source);
            Assert(stage.Size == payload.Length, "an observation exactly at the required headroom is admitted");
            await stage.DisposeAsync();
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the at-threshold stage was removed by the explicit cleanup");
        }

        const long oneGiB = 1024L * 1024 * 1024;
        await using (var site = await Site.CreateAsync("G-under-parent-floor", "g3", "g3"))
        {
            site.AttachDropboxWire("small.bin", "id:cc3a-g3", "rev-cc3a-g3", payload);
            site.SetDeviceSpace(oneGiB);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "small.bin",
                "id:cc3a-g3", "rev-cc3a-g3", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var stage = await PrepareAsync(site, operationId, source);
            Assert(oneGiB < 20L * 1024 * 1024 * 1024 && required < oneGiB,
                "the fixture observes less free space than the old 20 GiB wording required while the product headroom is adequate");
            Assert(stage.Size == payload.Length,
                "a small stage with adequate product headroom is admitted under 20 GiB of free space");
            await stage.DisposeAsync();
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the under-floor stage was removed by the explicit cleanup");
        }

        await using (var site = await Site.CreateAsync("G-overflow", "g4", "g4"))
        {
            site.AttachRightFixture(null,
                new PilotRemoteEntry("huge.bin", PilotEntryKind.File, "gd-huge", "gdr-huge",
                    long.MaxValue, hash));
            site.SetDeviceSpace(long.MaxValue);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalRight, "huge.bin",
                "gd-huge", "gdr-huge", long.MaxValue, hash);
            var operationId = await site.CreateOperationAsync(source);
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a source size beyond the exact twice-size staging bound");
            Assert(error is not null &&
                error.Message.Contains("twice-size", StringComparison.Ordinal),
                "the huge-size refusal is the exact checked twice-size bound and not a partial stage");
            await AssertNothingCreatedAsync(site, operationId, "the huge-size observation");
            Assert(site.RightFixture!.DownloadCalls == 0,
                "the huge-size observation failed before any read or private object creation");
        }
    }

    // ----- case H: private allocation and held-root authority refusals ------------------------------

    private static async Task AllocationAuthorityRefusalsAsync()
    {
        var payload = Pattern(1024);
        var hash = ReferenceBlockHash(payload);

        await using (var site = await Site.CreateAsync("H-foreign-namespace", "h1", "h1"))
        {
            site.AttachDropboxWire("foreign.bin", "id:cc3a-h1", "rev-cc3a-h1", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "foreign.bin",
                "id:cc3a-h1", "rev-cc3a-h1", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source,
                allocationNamespaceId: "cloud-content-stage-foreign-316");
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "an allocation owned by a foreign private control namespace");
            Assert(error is not null &&
                error.Message.Contains("private control namespace", StringComparison.Ordinal),
                "the foreign-namespace refusal names the private control namespace authority");
            await AssertNothingCreatedAsync(site, operationId, "the foreign namespace");
        }

        await using (var site = await Site.CreateAsync("H-foreign-parent", "h2", "h2"))
        {
            site.AttachDropboxWire("parent.bin", "id:cc3a-h2", "rev-cc3a-h2", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "parent.bin",
                "id:cc3a-h2", "rev-cc3a-h2", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source,
                stageParent: "allocation-of-another-operation");
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a stage allocation nested under a foreign parent");
            Assert(error is not null &&
                error.Message.Contains("not nested under", StringComparison.Ordinal),
                "the misnested stage allocation refusal names the exact transaction parent");
            await AssertNothingCreatedAsync(site, operationId, "the foreign parent");
        }

        await using (var site = await Site.CreateAsync("H-bad-transaction-name", "h3", "h3"))
        {
            site.AttachDropboxWire("name.bin", "id:cc3a-h3", "rev-cc3a-h3", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "name.bin",
                "id:cc3a-h3", "rev-cc3a-h3", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source,
                transactionName: "not-a-private-transaction-name");
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a transaction allocation with a non-deterministic name");
            Assert(error is not null &&
                error.Message.Contains("not an accepted private transaction name", StringComparison.Ordinal),
                "the bad transaction name refusal names the existing private transaction naming contract");
            await AssertNothingCreatedAsync(site, operationId, "the bad transaction name");
        }

        await using (var site = await Site.CreateAsync("H-non-fresh-allocation", "h4", "h4"))
        {
            site.AttachDropboxWire("fresh.bin", "id:cc3a-h4", "rev-cc3a-h4", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "fresh.bin",
                "id:cc3a-h4", "rev-cc3a-h4", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var operation = await site.SnapshotAsync(operationId);
            var lease = await site.LeaseAsync(operationId);
            try
            {
                await site.Db.TransitionAllocationAsync(
                    Site.OfKind(operation, SyncAuthorityAllocationKind.TransactionDirectory).AllocationId,
                    lease, SyncAuthorityAllocationStatus.Creating);
            }
            finally
            {
                await site.ReleaseAsync(lease);
            }
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a non-fresh transaction allocation");
            Assert(error is not null &&
                error.Message.Contains("is not a fresh Authorized allocation", StringComparison.Ordinal),
                "a non-fresh allocation fails closed for explicit reconciliation instead of being reused");
            Assert(!Directory.Exists(site.TransactionPath(operation)),
                "the non-fresh refusal created nothing");
        }

        await using (var site = await Site.CreateAsync("H-pre-existing-transaction", "h5", "h5"))
        {
            site.AttachDropboxWire("preexisting.bin", "id:cc3a-h5", "rev-cc3a-h5", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "preexisting.bin",
                "id:cc3a-h5", "rev-cc3a-h5", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var operation = await site.SnapshotAsync(operationId);
            var transactionPath = site.TransactionPath(operation);
            Directory.CreateDirectory(transactionPath);
            var marker = Path.Combine(transactionPath, "foreign-evidence.bin");
            await File.WriteAllBytesAsync(marker, Pattern(64));
            var error = await PrepareExpectRefusalAsync(site, operationId, source,
                "a pre-existing transaction directory path");
            Assert(error is not null &&
                error.Message.Contains("already exists", StringComparison.Ordinal),
                "a pre-existing transaction is retained as evidence and never adopted by a fresh allocation");
            Assert(File.Exists(marker) && Directory.Exists(transactionPath),
                "the pre-existing transaction and its foreign contents were left exactly in place");
            Assert(site.Wire!.DownloadCalls == 0,
                "the pre-existing transaction refusal happened before any provider read");
            Assert(OfKindStatus(operation, SyncAuthorityAllocationKind.TransactionDirectory) ==
                SyncAuthorityAllocationStatus.Authorized,
                "the pre-existing transaction refusal changed no allocation state");
        }

        await using (var site = await Site.CreateAsync("H-foreign-held-root", "h6", "h6"))
        {
            site.AttachDropboxWire("root.bin", "id:cc3a-h6", "rev-cc3a-h6", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "root.bin",
                "id:cc3a-h6", "rev-cc3a-h6", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var lease = await site.LeaseAsync(operationId);
            try
            {
                await ExpectRefusalAsync(() => SyncAuthorityCloudContentStage.PrepareAsync(site.Db, lease,
                    "member-cc3a-" + site.Slug, site.Port(source.Side), site.Control,
                    site.SelectedRootLease, CancellationToken.None),
                    "a held root that is not the private control namespace");
            }
            finally
            {
                await site.ReleaseAsync(lease);
            }
            await AssertNothingCreatedAsync(site, operationId, "the foreign held root");
        }
    }

    // ----- case I: the held identity and the retain-versus-cleanup entry ---------------------------

    private static async Task HeldIdentityAndCleanupAsync()
    {
        var payload = Pattern(2048);
        var hash = ReferenceBlockHash(payload);
        var site = await Site.CreateAsync("I-held-and-cleanup", "i1", "i1");
        await using (site)
        {
            site.AttachDropboxWire("held.bin", "id:cc3a-i1", "rev-cc3a-i1", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "held.bin",
                "id:cc3a-i1", "rev-cc3a-i1", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            var transactionPath = site.TransactionPath(operation);
            var alias = Path.Combine(site.CaseRoot, "held-alias.bin");

            // A held stage file cannot be given a second accepted-output name, and the exact owned
            // identity is what refuses it.
            var stage = await PrepareAsync(site, operationId, source);
            var verifiedBytes = await ReadStageAsync(stage);
            var heldIdentity = stage.StageIdentity;
            Assert(!NativeTestFiles.TryCreateHardLinkFromChild(alias, stagePath),
                "a held stage file cannot be hardlinked into a second accepted-output name");
            Assert(!File.Exists(alias), "the refused hardlink created no alias");
            await ExpectAnyRefusalAsync(
                () => Task.Run(() => SyncLocalFileAuthority.OpenExisting(stagePath)),
                "a second handle by the exact held stage path");

            // Cleanup while the exact stage object is still held fails closed and deletes nothing.
            await ExpectAnyRefusalAsync(() => CleanupAsync(site, operationId),
                "cleanup while the exact stage object is still held");
            Assert(File.Exists(stagePath) && Directory.Exists(transactionPath),
                "both private paths survive a refused cleanup");
            Assert(OfKindStatus(await site.SnapshotAsync(operationId), SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Verified,
                "the refused cleanup changed no allocation state");

            // Disposal closes only the held handles: nothing is deleted and no allocation changes.
            await stage.DisposeAsync();
            Assert(File.Exists(stagePath) && Directory.Exists(transactionPath),
                "stage disposal closed only the held handles and deleted nothing");
            Assert(OfKindStatus(await site.SnapshotAsync(operationId), SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Verified,
                "stage disposal changed no allocation state");
            Assert(Same(verifiedBytes, payload),
                "the verified output is exactly the held object's bytes");

            // The same file can be hardlinked once the held interval ends, so the held identity is what
            // refuses it during the transfer.
            Assert(NativeTestFiles.TryCreateHardLinkFromChild(alias, stagePath),
                "the same file can be hardlinked once the held interval ends, so the held identity is the refusal");
            Assert(File.Exists(alias), "the control alias exists after the held interval");
            await ExpectAnyRefusalAsync(() => CleanupAsync(site, operationId),
                "cleanup of a stage that was hardlinked into a foreign alias");
            Assert(File.Exists(stagePath) && File.Exists(alias),
                "a hardlinked stage and its foreign alias are retained as evidence and never deleted");
            File.Delete(alias);
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "removing the foreign alias restores the exact single-link ownership and cleanup succeeds");

            // A replacement file at the exact stage path is a different object, is detected as such and is
            // never accepted as this operation's owned output.
            site.Wire!.Add(new WireFile
            {
                RelativePath = "swapped.bin",
                Id = "id:cc3a-i1b",
                Revision = "rev-cc3a-i1b",
                Content = payload,
                ContentHash = DropboxContentHash.Compute(payload),
                Size = payload.Length
            });
            var swappedSource = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "swapped.bin",
                "id:cc3a-i1b", "rev-cc3a-i1b", payload.Length, hash, null, "swap");
            var swappedOperationId = await CreateSwapOperationAsync(site, swappedSource);
            var swappedStage = await PrepareAsync(site, swappedOperationId, swappedSource,
                "member-cc3a-" + site.Slug + "-swap");
            var swappedOperation = await site.SnapshotAsync(swappedOperationId);
            var swappedStagePath = site.StagePath(swappedOperation);
            Assert(Same(await ReadStageAsync(swappedStage), payload),
                "the swap fixture staged the exact source bytes");
            await swappedStage.DisposeAsync();
            var movedPath = Path.Combine(site.CaseRoot, "swapped-original.bin");
            File.Move(swappedStagePath, movedPath);
            await File.WriteAllBytesAsync(swappedStagePath, OtherPattern(payload.Length));
            string replacementIdentity;
            using (var probe = SyncLocalFileAuthority.OpenExisting(swappedStagePath))
                replacementIdentity = SyncLocalTransactionDirectory.FormatIdentity(probe.VerifySingleLink());
            Assert(replacementIdentity != swappedStage.StageIdentity,
                "the replacement at the exact stage path is a different object identity");
            await ExpectAnyRefusalAsync(() => CleanupAsync(site, swappedOperationId),
                "cleanup of the exact stage path swapped for a foreign object");
            Assert(File.Exists(swappedStagePath) && File.Exists(movedPath),
                "the foreign replacement and the moved original are both retained and never deleted");
            File.Delete(swappedStagePath);
            File.Move(movedPath, swappedStagePath);
            using (var restored = SyncLocalFileAuthority.OpenExisting(swappedStagePath))
                Assert(SyncLocalTransactionDirectory.FormatIdentity(restored.VerifySingleLink()) ==
                    swappedStage.StageIdentity,
                    "restoring the exact held object at the stage path restores the owned identity");
            var swapCleanup = await CleanupAsync(site, swappedOperationId);
            Assert(swapCleanup.Cleaned && swapCleanup.StageRemoved && swapCleanup.TransactionRemoved,
                "with the exact owned stage restored the explicit cleanup removes it and its empty transaction");
            Assert(!Directory.Exists(site.TransactionPath(swappedOperation)),
                "the swapped operation's empty private transaction is gone");
            Assert(heldIdentity != swappedStage.StageIdentity,
                "the two swap fixtures retained distinct held identities");
        }

        // An issued but uncommitted target request retains the stage for later reconciliation.
        var retainSite = await Site.CreateAsync("I-retain-on-attempt", "i2", "i2");
        await using (retainSite)
        {
            retainSite.AttachDropboxWire("retain.bin", "id:cc3a-i2", "rev-cc3a-i2", payload);
            var source = await retainSite.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "retain.bin",
                "id:cc3a-i2", "rev-cc3a-i2", payload.Length, hash);
            var operationId = await retainSite.CreateOperationAsync(source);
            var stage = await PrepareAsync(retainSite, operationId, source);
            await stage.DisposeAsync();
            var operation = await retainSite.SnapshotAsync(operationId);
            var stagePath = retainSite.StagePath(operation);
            var transactionPath = retainSite.TransactionPath(operation);
            var lease = await retainSite.LeaseAsync(operationId);
            try
            {
                await retainSite.Db.StartDispatchedAttemptAsync(operationId, lease,
                    Hash("cc3a-attempt", operationId), "cloud-target-effect");
            }
            finally
            {
                await retainSite.ReleaseAsync(lease);
            }
            var dispatched = await retainSite.SnapshotAsync(operationId);
            Assert(dispatched.Status == SyncAuthorityOperationStatus.EffectIssuing &&
                dispatched.Attempts.Count == 1 &&
                dispatched.Attempts[0].Status == SyncAuthorityAttemptStatus.RequestSent,
                "the fixture recorded exactly one issued target request");
            var cleanup = await CleanupAsync(retainSite, operationId);
            Assert(!cleanup.Cleaned && cleanup.JournalState == "effect_issuing" &&
                cleanup.ResidualPaths.Count == 2,
                "an issued but uncommitted target request retains the stage and reports both residuals");
            Assert(cleanup.Reason.Contains("retained for later reconciliation", StringComparison.Ordinal),
                "the retained cleanup states its later-reconciliation reason");
            Assert(cleanup.StagePath == stagePath && cleanup.TransactionPath == transactionPath &&
                cleanup.ResidualPaths.Contains(stagePath) && cleanup.ResidualPaths.Contains(transactionPath),
                "the retained cleanup names the exact owned stage and transaction paths");
            Assert(File.Exists(stagePath) && Directory.Exists(transactionPath),
                "the retained stage and its private transaction are still present");
            Assert(OfKindStatus(await retainSite.SnapshotAsync(operationId),
                    SyncAuthorityAllocationKind.StageFile) == SyncAuthorityAllocationStatus.Verified,
                "a retained cleanup changed no allocation state");
        }
    }

    private static async Task<string> CreateSwapOperationAsync(Site site, SourceRef source)
    {
        var operationId = "operation-cc3a-" + site.Slug + "-swap";
        var transactionId = "allocation-transaction-swap-" + site.Slug;
        var stageId = "allocation-stage-swap-" + site.Slug;
        var transaction = new SyncAuthorityAllocation(transactionId, site.NamespaceId,
            SyncAuthorityAllocationKind.TransactionDirectory, TransactionName(operationId), site.NamespaceId,
            Hash("cc3a-nonce", operationId, "transaction"));
        var stage = new SyncAuthorityAllocation(stageId, site.NamespaceId,
            SyncAuthorityAllocationKind.StageFile, SyncLocalTransactionDirectory.StageName, transactionId,
            Hash("cc3a-nonce", operationId, "stage"));
        var member = new SyncAuthorityMember("member-cc3a-" + site.Slug + "-swap", site.Pair.Id, source.ObjectId,
            "source", site.LeftRoot, source.MembershipId, source.ObjectVersionId, 1, source.Path, "{}");
        var plan = new SyncAuthorityOperationPlan(operationId, "idempotency-" + operationId,
            SyncAuthorityOperationKind.CreateRemote, site.Pair.Id, source.ObjectId, 1, ProfileId, "{}", 100,
            new[] { member }, Array.Empty<SyncAuthorityDependency>(), new[] { transaction, stage });
        await site.Db.CreateOperationAsync(plan, site.Admission);
        return operationId;
    }

    // ----- case J: no target effect from any path --------------------------------------------------

    private static Task NoTargetEffectAsync()
    {
        Assert(_wireUnexpectedRoutes == 0,
            "the real Dropbox adapter received no request beyond its exact read surface in any case");
        Assert(_fixtureRefusedEffects == 0 && _unexpectedProviderEffects == 0,
            "no adapter was ever asked for an upload, delete, move, copy, recovery or other provider effect");
        Assert(_wireDownloadCalls > 0 && _fixtureDownloadCalls > 0,
            "both provider paths were exercised by real downloads");
        return Task.CompletedTask;
    }

    // ----- case K: reopening the retained verified stage -------------------------------------------

    // The exact reopened-stage admission boundary: the free space a resident stage actually leaves on the
    // staging volume is the shipped product safety reserve plus the resident object less one byte.
    private static long ResidentReopenHeadroom(long size) => ProductSafetyReserve + size - 1;

    private static async Task<Exception?> ReopenExpectRefusalAsync(SyncAuthorityDatabase authority,
        string operationId, SyncControlNamespaceBinding controlNamespace, PilotDirectoryLease controlRoot,
        string label)
    {
        try
        {
            await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(authority, operationId, controlNamespace,
                controlRoot, CancellationToken.None);
        }
        catch (SyncAuthorityCloudContentStageException error)
        {
            Assert(true, label + " refused: " + error.Message);
            return error;
        }
        Assert(false, label + " must refuse and did not");
        return null;
    }

    private static async Task RetainedStageReopenAsync()
    {
        var payload = Pattern(4096);
        var hash = ReferenceBlockHash(payload);
        var required = RequiredStagingBytes(payload.Length);
        var residentHeadroom = ResidentReopenHeadroom(payload.Length);
        Assert(required > residentHeadroom,
            "a resident stage leaves strictly less free space than its own creation required");

        // K1: the exact boundary. A stage created at the exact checked twice-size boundary is reopened after
        // the held handles are disposed and the authority is reopened from the same durable database, with the
        // free space a resident stage actually leaves. It takes no new body reservation, no provider read and
        // no write, and it retains the exact owned identities.
        await using (var site = await Site.CreateAsync("K-reopen-at-boundary", "k1", "k1"))
        {
            site.AttachDropboxWire("resident.bin", "id:cc3a-k1", "rev-cc3a-k1", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "resident.bin",
                "id:cc3a-k1", "rev-cc3a-k1", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var volume = Path.GetPathRoot(site.ControlRoot.Path)!;
            var idle = SyncRecoverySpoolAdmission.SnapshotForTest(volume);
            Assert(idle.ReservedBytes == 0 && idle.WaitingCount == 0 && idle.ActiveCount == 0,
                "the retained-reopen fixture starts from an idle staging admission volume");

            site.SetDeviceSpace(required);
            var stage = await PrepareAsync(site, operationId, source);
            var created = SyncRecoverySpoolAdmission.SnapshotForTest(volume);
            Assert(created.ReservedBytes == payload.Length * 2 && created.ActiveCount == 1,
                "creating the stage held the exact checked twice-file-size body reservation");
            var createdIdentity = stage.StageIdentity;
            var createdTransactionIdentity = stage.TransactionIdentity;
            Assert(Same(await ReadStageAsync(stage), payload),
                "the created stage retained the exact retained source bytes");
            await stage.DisposeAsync();
            Assert(SyncRecoverySpoolAdmission.SnapshotForTest(volume) == idle,
                "disposing the created stage released its exact body reservation");

            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            var transactionPath = site.TransactionPath(operation);
            var createdWriteTime = new FileInfo(stagePath).LastWriteTimeUtc;
            Assert(site.Wire!.DownloadCalls == 1,
                "the create read the exact retained revision exactly once");

            // The free space a resident stage leaves is strictly less than the creation requirement the old
            // reopen inherited, so the old twice-size creation admission cannot be what admits this reopen.
            site.SetDeviceSpace(residentHeadroom);
            var restarted = new SyncAuthorityDatabase(site.Db.DatabasePath);
            var reopened = await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(restarted, operationId,
                site.Control, site.ControlRoot, CancellationToken.None);
            var held = SyncRecoverySpoolAdmission.SnapshotForTest(volume);
            Assert(held.ReservedBytes == 0 && held.ActiveCount == 1,
                "the retained reopen holds zero new body reservation");
            Assert(reopened.Size == payload.Length && reopened.NativeHash == hash &&
                reopened.CommonSha256 == Sha256Hex(payload) &&
                reopened.StageIdentity == createdIdentity &&
                reopened.TransactionIdentity == createdTransactionIdentity &&
                reopened.StageAllocationId ==
                    Site.OfKind(operation, SyncAuthorityAllocationKind.StageFile).AllocationId &&
                reopened.TransactionAllocationId ==
                    Site.OfKind(operation, SyncAuthorityAllocationKind.TransactionDirectory).AllocationId,
                "the reopened stage retained the exact size, both digests and its exact owned identities");
            Assert(Same(await ReadStageAsync(reopened), payload),
                "the reopened stage reads the exact retained source bytes");
            Assert(site.Wire!.DownloadCalls == 1,
                "reopening the retained stage read no cloud source");
            Assert(File.Exists(stagePath) && new FileInfo(stagePath).Length == payload.Length &&
                new FileInfo(stagePath).LastWriteTimeUtc == createdWriteTime,
                "reopening the retained stage neither rewrote nor recreated the retained object");
            await reopened.DisposeAsync();
            Assert(SyncRecoverySpoolAdmission.SnapshotForTest(volume) == idle,
                "disposing the reopened stage released its zero-byte reservation and left the volume idle");
            var cleanup = await CleanupAsync(site, operationId);
            Assert(cleanup.Cleaned && cleanup.StageRemoved && cleanup.TransactionRemoved,
                "the reopened retained stage is removed only by the explicit identity-checked cleanup");
            Assert(!File.Exists(stagePath) && !Directory.Exists(transactionPath),
                "the explicit cleanup removed the exact owned stage and its empty private transaction");
        }

        // K2: an altered, missing or substituted retained object and a reopen under a foreign held root all
        // refuse while retaining the exact private evidence, and they never repair the evidence themselves.
        await using (var site = await Site.CreateAsync("K-reopen-altered", "k2", "k2"))
        {
            site.AttachDropboxWire("altered.bin", "id:cc3a-k2", "rev-cc3a-k2", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "altered.bin",
                "id:cc3a-k2", "rev-cc3a-k2", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            site.SetDeviceSpace(required);
            var stage = await PrepareAsync(site, operationId, source);
            var heldIdentity = stage.StageIdentity;
            await stage.DisposeAsync();
            site.SetDeviceSpace(residentHeadroom);
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            var transactionPath = site.TransactionPath(operation);

            // A retained stage whose length no longer matches the exact retained source size refuses.
            await File.WriteAllBytesAsync(stagePath, payload.Concat(new byte[] { 0x5A }).ToArray());
            var sizeError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.ControlRoot, "a retained stage whose length changed");
            Assert(sizeError is not null &&
                sizeError.Message.Contains("length differs from the exact retained source size",
                    StringComparison.Ordinal),
                "the changed-length refusal names the exact retained source size");
            Assert(File.Exists(stagePath) && new FileInfo(stagePath).Length == payload.Length + 1,
                "the changed-length refusal retained the altered object exactly as it was found");
            await File.WriteAllBytesAsync(stagePath, payload);

            // A retained stage whose bytes no longer recompute the retained native digest refuses.
            var altered = (byte[])payload.Clone();
            altered[^1] ^= 0x5A;
            await File.WriteAllBytesAsync(stagePath, altered);
            var bytesError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.ControlRoot, "a retained stage whose bytes changed");
            Assert(bytesError is not null && bytesError.Message.Contains(
                "no longer recompute the retained provider-native source digest", StringComparison.Ordinal),
                "the changed-bytes refusal names the retained provider-native source digest");
            Assert(Same(await File.ReadAllBytesAsync(stagePath), altered),
                "the changed-bytes refusal retained the altered object exactly as it was found");
            await File.WriteAllBytesAsync(stagePath, payload);

            // A retained stage that is no longer the exact single-link object of its own allocation refuses.
            var alias = Path.Combine(site.CaseRoot, "reopen-alias.bin");
            Assert(NativeTestFiles.TryCreateHardLinkFromChild(alias, stagePath),
                "the retained stage can be given a second link while no handle is held");
            await ExpectAnyRefusalAsync(() => SyncAuthorityCloudContentStage.ReopenVerifiedAsync(site.Db,
                operationId, site.Control, site.ControlRoot, CancellationToken.None),
                "a reopen of a retained stage that carries a second hard link");
            Assert(File.Exists(stagePath) && File.Exists(alias),
                "the hardlinked retained stage and its foreign alias are both retained as evidence");
            Assert((await site.SnapshotAsync(operationId)).Attempts.Count == 0,
                "the hardlink refusal issued no target attempt");
            File.Delete(alias);

            // A different object at the exact stage path is not the object the owned allocation is bound to.
            var movedPath = Path.Combine(site.CaseRoot, "reopen-original.bin");
            File.Move(stagePath, movedPath);
            await File.WriteAllBytesAsync(stagePath, OtherPattern(payload.Length));
            var allocationError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.ControlRoot, "a retained stage path holding a different object than its owned allocation");
            Assert(allocationError is not null && allocationError.Message.Contains(
                "identity differs from its exact owned allocation", StringComparison.Ordinal),
                "the substitution refusal names the exact owned allocation identity");
            Assert(File.Exists(stagePath) && File.Exists(movedPath),
                "the foreign replacement and the moved original are both retained and never deleted");
            File.Delete(stagePath);
            File.Move(movedPath, stagePath);

            // A held root that is not the private control namespace refuses before opening anything.
            var rootError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.SelectedRootLease, "a reopen under a held root that is not the private control namespace");
            Assert(rootError is not null && rootError.Message.Contains(
                "not the exact private control namespace", StringComparison.Ordinal),
                "the foreign held root refusal names the exact private control namespace authority");

            // With the exact owned object restored the same durable rows reopen it exactly, so none of the
            // refusals above left a partial result or repaired the evidence by itself.
            var recovered = await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(site.Db, operationId,
                site.Control, site.ControlRoot, CancellationToken.None);
            Assert(recovered.StageIdentity == heldIdentity && Same(await ReadStageAsync(recovered), payload),
                "with the exact owned object restored the retained stage reopens exactly after every refusal");
            Assert(site.Wire!.DownloadCalls == 1,
                "no alteration, substitution or foreign-root refusal read the cloud source");
            Assert(OfKindStatus(await site.SnapshotAsync(operationId), SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Verified,
                "the refusals changed no durable allocation state");
            await recovered.DisposeAsync();
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the restored retained stage is removed only by the explicit cleanup");
            Assert(!Directory.Exists(transactionPath),
                "the explicit cleanup removed the restored private transaction");
        }

        // K3: a retained stage file that is gone refuses without creating, redownloading or truncating it.
        await using (var site = await Site.CreateAsync("K-reopen-missing", "k3", "k3"))
        {
            site.AttachDropboxWire("missing.bin", "id:cc3a-k3", "rev-cc3a-k3", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "missing.bin",
                "id:cc3a-k3", "rev-cc3a-k3", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            site.SetDeviceSpace(required);
            var stage = await PrepareAsync(site, operationId, source);
            await stage.DisposeAsync();
            site.SetDeviceSpace(residentHeadroom);
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            var transactionPath = site.TransactionPath(operation);
            File.Delete(stagePath);

            var missingError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.ControlRoot, "a retained stage file that is gone");
            Assert(missingError is not null && missingError.Message.Contains(
                "is not retained; reconciliation never creates one", StringComparison.Ordinal),
                "the missing-stage refusal states that reconciliation never creates one");
            Assert(!File.Exists(stagePath) && Directory.Exists(transactionPath),
                "the missing-stage refusal created no stage and left the private transaction retained");
            var retained = await site.SnapshotAsync(operationId);
            Assert(OfKindStatus(retained, SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Verified && retained.Attempts.Count == 0,
                "the missing-stage refusal changed no allocation and issued no target attempt");
            Assert(site.Wire!.DownloadCalls == 1,
                "the missing-stage refusal downloaded nothing");
        }

        // K4: the shipped reserve is a genuine reopening requirement, a negative observation is refused, and
        // the exact reserve boundary still reopens.
        await using (var site = await Site.CreateAsync("K-reopen-reserve", "k4", "k4"))
        {
            site.AttachDropboxWire("reserve.bin", "id:cc3a-k4", "rev-cc3a-k4", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "reserve.bin",
                "id:cc3a-k4", "rev-cc3a-k4", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            var volume = Path.GetPathRoot(site.ControlRoot.Path)!;
            site.SetDeviceSpace(required);
            var stage = await PrepareAsync(site, operationId, source);
            await stage.DisposeAsync();
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);

            site.SetDeviceSpace(ProductSafetyReserve - 1);
            var reserveError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.ControlRoot, "a reopen one byte below the shipped product safety reserve");
            Assert(reserveError is not null && reserveError.Message.Contains(
                "does not leave the shipped 64 MiB product safety reserve free", StringComparison.Ordinal),
                "the under-reserve refusal names the shipped product safety reserve");
            Assert(Same(await File.ReadAllBytesAsync(stagePath), payload),
                "the under-reserve refusal retained the exact retained bytes and completed nothing");

            site.SetDeviceSpace(-1);
            var negativeError = await ReopenExpectRefusalAsync(site.Db, operationId, site.Control,
                site.ControlRoot, "a reopen against a negative staging volume observation");
            Assert(negativeError is not null &&
                negativeError.Message.Contains("not a usable value", StringComparison.Ordinal),
                "the negative-observation refusal rejects the unusable free-space value");
            Assert(Same(await File.ReadAllBytesAsync(stagePath), payload),
                "the negative-observation refusal retained the exact retained bytes");

            site.SetDeviceSpace(ProductSafetyReserve);
            var boundary = await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(site.Db, operationId,
                site.Control, site.ControlRoot, CancellationToken.None);
            Assert(boundary.Size == payload.Length &&
                SyncRecoverySpoolAdmission.SnapshotForTest(volume).ReservedBytes == 0 &&
                Same(await ReadStageAsync(boundary), payload),
                "an observation exactly at the shipped reserve reopens the retained stage with zero new body reservation");
            await boundary.DisposeAsync();
            var afterRefusals = await site.SnapshotAsync(operationId);
            Assert(OfKindStatus(afterRefusals, SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Verified && afterRefusals.Attempts.Count == 0,
                "the reserve and observation refusals changed no allocation and issued no target attempt");
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the retained stage is removed only by the explicit cleanup after the reserve proofs");
        }

        // K5: a caller cancellation that lands during the retained rehash retains the exact object and
        // completes nothing; the same durable rows still reopen it afterwards.
        await using (var site = await Site.CreateAsync("K-reopen-cancelled-rehash", "k5", "k5"))
        {
            site.AttachDropboxWire("cancel-reopen.bin", "id:cc3a-k5", "rev-cc3a-k5", payload);
            var source = await site.RegisterAsync(SyncAuthorityCloudSides.LogicalLeft, "cancel-reopen.bin",
                "id:cc3a-k5", "rev-cc3a-k5", payload.Length, hash);
            var operationId = await site.CreateOperationAsync(source);
            site.SetDeviceSpace(required);
            var stage = await PrepareAsync(site, operationId, source);
            var retainedIdentity = stage.StageIdentity;
            await stage.DisposeAsync();
            site.SetDeviceSpace(residentHeadroom);
            var operation = await site.SnapshotAsync(operationId);
            var stagePath = site.StagePath(operation);
            var transactionPath = site.TransactionPath(operation);

            var cancellation = new CancellationTokenSource();
            using (SyncByteProgressStream.Begin(site.ControlRoot.Path, report =>
            {
                if (report.Phase.Contains("Verifying staged file hashes", StringComparison.Ordinal))
                    cancellation.Cancel();
                return Task.CompletedTask;
            }))
            {
                await AssertCancelledAsync(() => SyncAuthorityCloudContentStage.ReopenVerifiedAsync(site.Db,
                    operationId, site.Control, site.ControlRoot, cancellation.Token),
                    "a caller cancellation during the retained rehash");
            }

            Assert(File.Exists(stagePath) && new FileInfo(stagePath).Length == payload.Length &&
                Directory.Exists(transactionPath) && Same(await File.ReadAllBytesAsync(stagePath), payload),
                "the cancelled rehash retained the exact private stage and transaction as evidence");
            var retained = await site.SnapshotAsync(operationId);
            Assert(OfKindStatus(retained, SyncAuthorityAllocationKind.StageFile) ==
                SyncAuthorityAllocationStatus.Verified && retained.Attempts.Count == 0,
                "the cancelled rehash changed no allocation and issued no target attempt");
            Assert(site.Wire!.DownloadCalls == 1,
                "the cancelled rehash did not re-read the cloud source");

            var recovered = await SyncAuthorityCloudContentStage.ReopenVerifiedAsync(site.Db, operationId,
                site.Control, site.ControlRoot, CancellationToken.None);
            Assert(recovered.StageIdentity == retainedIdentity &&
                Same(await ReadStageAsync(recovered), payload),
                "the retained stage still reopens exactly after the cancelled rehash, so no partial result survived");
            await recovered.DisposeAsync();
            Assert((await CleanupAsync(site, operationId)).Cleaned,
                "the retained cancelled-rehash stage is removed only by the explicit cleanup");
        }
    }

    // ----- provider fixtures ----------------------------------------------------------------------

    private sealed class WireFile
    {
        internal string RelativePath = "";
        internal string Id = "";
        internal string Revision = "";
        internal string RootPath = "/";
        internal byte[] Content = [];
        internal string? ContentHash;
        internal long? Size;
        internal Func<WireFile, long, long, HttpResponseMessage?>? DownloadOverride;
        internal Action<WireFile>? AfterDownload;

        internal string MetadataJson()
        {
            var display = RootPath == "/" ? "/" + RelativePath : RootPath + "/" + RelativePath;
            var builder = new StringBuilder();
            builder.Append("{\".tag\":\"file\",\"name\":\"").Append(NameOf(RelativePath))
                .Append("\",\"id\":\"").Append(Id).Append("\",\"rev\":\"").Append(Revision)
                .Append("\",\"size\":").Append((Size ?? Content.Length).ToString(CultureInfo.InvariantCulture));
            if (ContentHash is not null)
                builder.Append(",\"content_hash\":\"").Append(ContentHash).Append('"');
            builder.Append(",\"path_display\":\"").Append(display).Append('"')
                .Append(",\"path_lower\":\"").Append(display.ToLowerInvariant()).Append("\"}");
            return builder.ToString();
        }
    }

    private static string NameOf(string relativePath) =>
        relativePath.Contains('/') ? relativePath[(relativePath.LastIndexOf('/') + 1)..] : relativePath;

    // An actual in-process Dropbox endpoint serving the account read, the exact folder metadata, a
    // recursive listing and the full download, counting every call so a proof can assert the real read count.
    private sealed class DropboxWire : HttpMessageHandler
    {
        private readonly Dictionary<string, WireFile> _byId = new(StringComparer.Ordinal);
        private readonly List<WireFile> _files = [];
        private readonly string _root;
        private readonly string _rootId;

        internal DropboxWire(string accountId, string rootNamespaceId, string homeNamespaceId, string rootPath,
            string rootId)
        {
            AccountId = accountId;
            RootNamespaceId = rootNamespaceId;
            HomeNamespaceId = homeNamespaceId;
            _root = PilotPath.NormalizeDropboxRoot(rootPath);
            _rootId = rootId;
        }

        internal string AccountId { get; }
        internal string RootNamespaceId { get; }
        internal string HomeNamespaceId { get; }
        internal int AccountCalls { get; private set; }
        internal int MetadataCalls { get; private set; }
        internal int FileMetadataCalls { get; private set; }
        internal int ListCalls { get; private set; }
        internal int DownloadCalls { get; private set; }
        internal int UnexpectedRoutes { get; private set; }

        internal void Add(WireFile file)
        {
            file.RootPath = _root;
            _files.Add(file);
            _byId[file.Id] = file;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Method != HttpMethod.Post)
            {
                UnexpectedRoutes++;
                _wireUnexpectedRoutes++;
                throw new InvalidDataException("A non-POST Dropbox request is not part of this packet.");
            }
            var route = request.RequestUri!.AbsolutePath;
            if (route.EndsWith("/users/get_current_account", StringComparison.Ordinal))
            {
                AccountCalls++;
                return Json("{\"account_id\":\"" + AccountId + "\",\"root_info\":{\"root_namespace_id\":\"" +
                    RootNamespaceId + "\",\"home_namespace_id\":\"" + HomeNamespaceId + "\"}}");
            }
            if (route.EndsWith("/files/list_folder", StringComparison.Ordinal))
            {
                ListCalls++;
                var entries = string.Join(',', _files.Select(item => item.MetadataJson()));
                return Json("{\"entries\":[" + entries + "],\"cursor\":\"cc3a-cursor\",\"has_more\":false}");
            }
            if (route.EndsWith("/files/get_metadata", StringComparison.Ordinal))
            {
                MetadataCalls++;
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var path = JsonDocument.Parse(body).RootElement.GetProperty("path").GetString() ?? "";
                if (path == _rootId)
                    return Json("{\".tag\":\"folder\",\"name\":\"" + NameOf(_root) + "\",\"id\":\"" +
                        _rootId + "\",\"path_lower\":\"" + _root.ToLowerInvariant() +
                        "\",\"path_display\":\"" + _root + "\"}");
                if (_byId.TryGetValue(path, out var found))
                {
                    FileMetadataCalls++;
                    return Json(found.MetadataJson());
                }
                return Conflict();
            }
            if (route.EndsWith("/files/download", StringComparison.Ordinal))
                return await DownloadAsync(request, cancellationToken);
            UnexpectedRoutes++;
            _wireUnexpectedRoutes++;
            throw new InvalidDataException("An unexpected Dropbox route was requested: " + route);
        }

        private Task<HttpResponseMessage> DownloadAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadCalls++;
            _wireDownloadCalls++;
            var arg = request.Headers.TryGetValues("Dropbox-API-Arg", out var values)
                ? JsonDocument.Parse(values.Single()).RootElement.GetProperty("path").GetString() ?? ""
                : "";
            var file = _files.FirstOrDefault(item => "rev:" + item.Revision == arg);
            if (file is null) return Task.FromResult(Conflict());
            var size = file.Size ?? file.Content.Length;
            if (file.DownloadOverride is { } custom && custom(file, 0, size) is { } response)
            {
                file.AfterDownload?.Invoke(file);
                return Task.FromResult(response);
            }
            var normal = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(file.Content)
            };
            normal.Headers.Add("Dropbox-API-Result", file.MetadataJson());
            file.AfterDownload?.Invoke(file);
            return Task.FromResult(normal);
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json")
        };

        private static HttpResponseMessage Conflict() => new(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error_summary\":\"not_found\"}", Encoding.UTF8,
                "application/json")
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

    // A scripted cloud adapter. It supplies input only: an inventory and a body. Every other interface
    // member - upload, delete, move, copy, recovery, control namespace - is refused and counted, so a proof
    // can show that the staging component never asked its provider for a target effect.
    public class FixtureStore : DispatchProxy
    {
        internal IReadOnlyList<PilotRemoteEntry> Entries { get; set; } = [];
        internal byte[]? Payload { get; set; }
        internal int ExtraBytes { get; set; }
        internal int OmitTail { get; set; }
        internal Action? DuringDownload { get; set; }
        internal Action? AfterDownload { get; set; }
        internal int VerifyCalls;
        internal int ListCalls;
        internal int DownloadCalls;
        internal int RefusedEffects;
        internal string? LastRefusedMethod;

        internal ISyncRemoteStore Store => (ISyncRemoteStore)(object)this;

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
            return Task.FromResult(Entries);
        }

        private object Download(object?[]? arguments)
        {
            DownloadCalls++;
            _fixtureDownloadCalls++;
            var expected = (PilotRemoteEntry)arguments![0]!;
            var destination = (Stream)arguments[1]!;
            destination.Position = 0;
            destination.SetLength(0);
            var payload = Payload ?? Array.Empty<byte>();
            var write = OmitTail > 0 ? Math.Max(0, payload.Length - OmitTail) : payload.Length;
            if (write > 0) destination.Write(payload, 0, write);
            destination.Flush();
            if (ExtraBytes > 0)
            {
                var extra = new byte[ExtraBytes];
                destination.Write(extra, 0, extra.Length);
            }
            destination.Flush();
            DuringDownload?.Invoke();
            AfterDownload?.Invoke();
            return Task.FromResult(new SyncDownloadResult(expected.Id!, expected.Revision ?? "",
                expected.Size, expected.ContentHash!));
        }

        private object Refuse(MethodInfo? method)
        {
            RefusedEffects++;
            _fixtureRefusedEffects++;
            _unexpectedProviderEffects++;
            LastRefusedMethod = method?.Name;
            throw new NotSupportedException(
                "This cloud content stage fixture performs no provider effect: " + (method?.Name ?? "unknown"));
        }
    }
}
