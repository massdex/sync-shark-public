using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Registration consumes already-verified copy metadata. These checks prove durable authority and
// actual catalog projection, not provider byte verification or a live restore. All state is confined
// to a unique child of the parent's admitted manual-recovery fixture root.
internal static class ManualRecoveryRecordChecks
{
    private const string Installation = "manual-record-installation";
    private const string Profile = "manual-record-profile";
    private const string OtherProfile = "manual-record-other-profile";
    private const string PairId = "manual-record-pair-one";
    private const string OtherPairId = "manual-record-pair-two";
    private const string CommandId = "manual-record-command";
    private const string FirstId = "manual-record-first";
    private const string SecondId = "manual-record-second";
    private const string OriginalPath = "Documents/Original Name.txt";
    private const string OtherPath = "Images/Second Name.txt";
    private static readonly SyncArchiveResult Copy = new("verified-preimage-file", "[\"verified-preimage-file\",\"5\"]",
        "retained-preimage-000001.bin", 17, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("verified preimage"))).ToLowerInvariant());

    internal static async Task RunAsync(string scratchRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The fixture binds real local NTFS root identities.");
        var parent = Path.GetFullPath(scratchRoot).TrimEnd(Path.DirectorySeparatorChar);
        Require(Directory.Exists(parent), "parent must supply the existing admitted manual-recovery fixture root");
        Require((File.GetAttributes(parent) & FileAttributes.ReparsePoint) == 0, "fixture parent must not be a reparse point");
        var root = Path.Combine(parent, "manual-records-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await ExerciseAsync(root); }
        finally { CleanOwnRoot(parent, root); }
    }

    private static async Task ExerciseAsync(string root)
    {
        using var appLock = PilotInstanceLock.Acquire(Path.Combine(root, "state", "instance.lock"));
        var db = new SyncAuthorityDatabase(Path.Combine(root, "db", "authority.db"));
        await db.InitializeAsync(Installation, Profile, "manual-recovery-fixture", "{}");
        await db.InitializeAsync(Installation, OtherProfile, "manual-recovery-other-fixture", "{}");
        var account = new GoogleDriveConnectionBinding(GoogleDriveProviderConfiguration.RegisteredClientId,
            GoogleDriveConnectionBinding.CreateAccountKey("manual-record-account"), "manual-record-account",
            "fixture@example.invalid", "Fixture", [GoogleDriveProviderConfiguration.DriveScope],
            CredentialReference: "synthetic-only").Normalize();
        await new GoogleDriveConnectionStore(appLock.StateAuthority).SaveAsync(account);
        var pair = await CreatePairAsync(db, root, PairId, account);
        var otherPair = await CreatePairAsync(db, root, OtherPairId, account);
        await CreateCommandAsync(db, CommandId, PairId);
        await CreateCommandAsync(db, "manual-record-other-command", OtherPairId);
        await CreateCommandAsync(db, "manual-record-pause-command", PairId, kind: "pause");
        await CreateCommandAsync(db, "manual-record-requested-command", PairId, status: "requested");
        await CreateCommandAsync(db, "manual-record-completed-command", PairId, status: "completed");
        using var network = new NoNetwork(); using var http = new HttpClient(network);
        using var host = new SyncAuthorityRuntimeHost(appLock.StateAuthority, http, new SyncPairStore(appLock.StateAuthority),
            new SyncRuntimeStateStore(appLock.StateAuthority), new DropboxConnectionStore(appLock.StateAuthority),
            new NoCredentials(), db);

        // Baseline must genuinely succeed against the current schema before rejection tests can count.
        await db.RegisterManualRecoveryCopyAsync(FirstId, CommandId, PairId, pair.PolicyRevision,
            Profile, OriginalPath, Copy, CancellationToken.None);
        var first = (await db.LoadRecoveryArtifactsAsync()).Single();
        AssertProjection(first, FirstId, PairId, OriginalPath, Copy);
        await AssertCatalogAsync(host, pair, FirstId, OriginalPath, Copy);
        await AssertCompletedEvidenceAsync(db, FirstId, pair);
        Console.WriteLine("PASS manual recovery: real registration, original-path catalog and completed non-runnable evidence");

        // An identical replay must preserve the original row and retained timestamp, not append a copy.
        await db.RegisterManualRecoveryCopyAsync(FirstId, CommandId, PairId, pair.PolicyRevision,
            Profile, OriginalPath, Copy, CancellationToken.None);
        Require((await db.LoadRecoveryArtifactsAsync()).Single() == first, "same-identity replay preserves exactly one unchanged artifact");
        Console.WriteLine("PASS manual recovery: identical replay is one unchanged record");

        var secondCopy = Copy with { FileId = "second-preimage-file", Revision = "second-preimage-revision", RecoveryPath = "retained-preimage-000002.bin" };
        await db.RegisterManualRecoveryCopyAsync(SecondId, CommandId, PairId, pair.PolicyRevision,
            Profile, OtherPath, secondCopy, CancellationToken.None);
        await db.RegisterManualRecoveryCopyAsync("manual-record-other-pair", "manual-record-other-command", OtherPairId,
            otherPair.PolicyRevision, Profile, "Other Pair.txt", Copy with { FileId = "other-pair-preimage" }, CancellationToken.None);
        await AssertPagesAsync(host, db, pair, otherPair);
        Console.WriteLine("PASS manual recovery: real catalog pagination, search and pair isolation");

        var failures = new List<Exception>();
        async Task Check(string label, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS manual recovery: " + label); }
            catch (Exception error) { failures.Add(new InvalidOperationException(label + ": " + error.Message, error)); }
            Require((await db.LoadRecoveryArtifactsAsync()).Single(row => row.RecoveryArtifactId == FirstId) == first,
                "the earlier verified preimage must remain unchanged after every failed registration");
        }
        async Task Reject(string id, string command, string selectedPair, long revision, string profile,
            string path, SyncArchiveResult copy)
        {
            var before = await SnapshotAsync(db);
            Exception? failure = null;
            try { await db.RegisterManualRecoveryCopyAsync(id, command, selectedPair, revision, profile, path, copy, CancellationToken.None); }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException or ArgumentException)
            { failure = error; }
            catch (SqliteException error) when (error.SqliteErrorCode == 19) { failure = error; }
            // A SQL parse/schema error is deliberately not an accepted safety rejection.
            var after = await SnapshotAsync(db);
            Require(after == before, "rejection transaction must retain no new or changed artifact");
            Require(failure is not null, "the real registration method must refuse changed identity or authority");
            await AssertCatalogAsync(host, pair, FirstId, OriginalPath, Copy);
        }

        var changes = new (string Name, SyncArchiveResult Copy)[]
        {
            ("file ID", Copy with { FileId = "changed-id" }),
            ("revision", Copy with { Revision = "changed-revision" }),
            ("recovery path", Copy with { RecoveryPath = "different-preimage.bin" }),
            ("size", Copy with { Size = Copy.Size + 1 }),
            ("hash", Copy with { ContentHash = new string('0', 64) })
        };
        foreach (var change in changes)
            await Check("replay rejects changed copy " + change.Name,
                () => Reject(FirstId, CommandId, PairId, 1, Profile, OriginalPath, change.Copy));
        await Check("replay rejects changed original path", () => Reject(FirstId, CommandId, PairId, 1, Profile, "Different.txt", Copy));
        await Check("same record ID cannot cross pairs", () => Reject(FirstId, "manual-record-other-command", OtherPairId, 1, Profile, OriginalPath, Copy));
        await Check("same record ID cannot change profile", () => Reject(FirstId, CommandId, PairId, 1, OtherProfile, OriginalPath, Copy));

        foreach (var command in new[] { "manual-record-missing-command", "manual-record-other-command", "manual-record-pause-command",
                     "manual-record-requested-command", "manual-record-completed-command" })
            await Check("reject command " + command,
                () => Reject("rejected-" + command, command, PairId, 1, Profile, OriginalPath, Copy));
        await Check("wrong pair", () => Reject("rejected-wrong-pair", CommandId, OtherPairId, 1, Profile, OriginalPath, Copy));
        await Check("missing pair", () => Reject("rejected-missing-pair", CommandId, "manual-record-missing-pair", 1, Profile, OriginalPath, Copy));
        await Check("stale policy revision", () => Reject("rejected-stale-policy", CommandId, PairId, 2, Profile, OriginalPath, Copy));
        await Check("missing capability profile rolls back", () => Reject("rejected-missing-profile", CommandId, PairId, 1,
            "manual-record-missing-profile", OriginalPath, Copy));
        await Check("unverified metadata rejected", () => Reject("rejected-unverified", CommandId, PairId, 1, Profile,
            OriginalPath, Copy with { ContentHash = "" }));
        await Check("missing copy identity rejected", () => Reject("rejected-missing-copy-id", CommandId, PairId, 1, Profile,
            OriginalPath, Copy with { FileId = "" }));
        await Check("missing copy revision rejected", () => Reject("rejected-missing-copy-revision", CommandId, PairId, 1, Profile,
            OriginalPath, Copy with { Revision = "" }));

        await Check("completed evidence still cannot acquire replay authority", () => AssertCompletedEvidenceAsync(db, FirstId, pair));
        // Create through the genuine planning API, with a real retained member/root and no effects.
        // Registration must not attach a retained artifact to this unrelated runnable operation.
        const string occupied = "manual-record-ready-collision";
        await db.RegisterObjectAsync("manual-record-other-object", "file");
        var fixtureAdmission = await db.CreateAcceptedCommandAdmissionAsync(null, "reconcile", PairId, null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.CreateOperationAsync(new SyncAuthorityOperationPlan(occupied, "manual-record-existing-key",
            SyncAuthorityOperationKind.Reconcile, PairId, "manual-record-other-object", 1, Profile, "{}", 0,
            [new SyncAuthorityMember("manual-record-existing-member", PairId, "manual-record-other-object", "lock",
                "root-" + PairId, null, null, null, "Existing.txt", "{}")], [], []), fixtureAdmission);
        var existing = await db.LoadOperationAsync(occupied);
        Require(existing.Status == SyncAuthorityOperationStatus.Ready, "collision setup is a genuine existing runnable operation");
        await Check("occupied runnable operation ID must be refused", async () =>
        {
            await Reject(occupied, CommandId, PairId, 1, Profile, OriginalPath, Copy);
            var after = await db.LoadOperationAsync(occupied);
            Require(after.Status == existing.Status && after.ObjectId == existing.ObjectId &&
                after.IdempotencyKey == existing.IdempotencyKey && after.PreconditionsJson == existing.PreconditionsJson,
                "collision rejection preserves the unrelated operation's identity and authority");
        });
        Require(network.Attempts == 0, "catalog registration/projection performs no provider transport");
        if (failures.Count != 0) throw new AggregateException("Manual recovery record checks failed", failures);
    }

    private static async Task<SyncPair> CreatePairAsync(SyncAuthorityDatabase db, string root, string id,
        GoogleDriveConnectionBinding account)
    {
        var local = Path.Combine(root, id); Directory.CreateDirectory(local);
        using var lease = PilotDirectoryLease.Open(local);
        var pair = new SyncPair(id, id, local, lease.Identity.VolumeSerialNumber, lease.Identity.FileId,
            "/" + id, "cloud-" + id, ConnectionId: account.ConnectionId, Provider: SyncProviders.GoogleDrive,
            RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Recovery", "recovery-" + id, account.PermissionId));
        await db.RegisterPairAsync(id, 1);
        await db.RetainPairDefinitionAsync(pair, select: id == PairId);
        var serial = lease.Identity.VolumeSerialNumber.ToString("x8");
        var volume = "manual-record-volume-" + serial;
        await db.RegisterVolumeAsync(volume, Installation, serial, "NTFS", null,
            supportsFileId128: false, supportsUsn: false, supportsAtomicRename: true);
        await db.RegisterLocalRootBindingAsync("root-" + id, id, 1, Installation, volume,
            serial, lease.Identity.FileId.ToString("x16"), local);
        return pair;
    }

    private static async Task CreateCommandAsync(SyncAuthorityDatabase db, string id, string pair,
        string kind = "reconcile", string status = "executing")
    {
        await db.CreateCommandRequestAsync(id, kind, pair, "revision:reviewed-revision", null,
            JsonSerializer.Serialize(new { action = "restore_recovery", source = "DropboxRevision", sourcePath = OriginalPath,
                TargetRelativePath = OriginalPath, restoreAsCopy = false }));
        if (status == "requested") return;
        await db.TransitionCommandRequestAsync(id, "accepted");
        await db.TransitionCommandRequestAsync(id, "executing");
        if (status == "completed") await db.TransitionCommandRequestAsync(id, "completed", "{}");
    }

    private static void AssertProjection(SyncRecoveryArtifactProjection row, string id, string pair, string path, SyncArchiveResult copy)
    {
        Require(row.RecoveryArtifactId == id && row.OperationId == id && row.ObjectId == id && row.PairId == pair &&
            row.OriginalRelativePath == path && row.ContentHash == copy.ContentHash && row.Size == copy.Size && row.Status == "retained",
            "database catalog projects exact completed-copy identity and original user path");
        using var identity = JsonDocument.Parse(row.IdentityJson);
        Require(identity.RootElement.GetProperty("originalPath").GetString() == path &&
            identity.RootElement.GetProperty("provenance").GetString() == "manual_restore_preimage" &&
            identity.RootElement.GetProperty("commandId").GetString() == CommandId,
            "retained JSON binds the real command and original path rather than an archive filename");
    }
    private static async Task AssertCatalogAsync(SyncAuthorityRuntimeHost host, SyncPair pair, string id, string path, SyncArchiveResult copy)
    {
        var row = (await host.LoadRecoveryCatalogAsync(pair.Id)).Single(item => item.Key == id);
        Require(row.PairId == pair.Id && row.RelativePath == path && row.Location == copy.RecoveryPath &&
            row.Identity == copy.FileId && row.Revision == copy.Revision && row.Size == copy.Size &&
            row.ContentHash == copy.ContentHash && row.CanRestore && row.ObjectKind == "file",
            "real host catalog renders the exact preimage with its original path and bound archive identity");
        var page = await host.LoadRecoveryCatalogPageAsync(pair.Id, pageSize: 1, search: path);
        Require(page.Items.Count == 1 && page.Items[0] == row && !page.HasMoreArchives && !page.HasMoreDeleted,
            "searched page uses the same real catalog projection");
    }
    private static async Task AssertPagesAsync(SyncAuthorityRuntimeHost host, SyncAuthorityDatabase db, SyncPair pair, SyncPair otherPair)
    {
        var all = await host.LoadRecoveryCatalogAsync(pair.Id);
        var first = await host.LoadRecoveryCatalogPageAsync(pair.Id, pageSize: 1);
        var second = await host.LoadRecoveryCatalogPageAsync(pair.Id, archiveOffset: first.NextArchiveOffset, pageSize: 1);
        Require(all.Count == 2 && first.Items.Count == 1 && first.HasMoreArchives && first.NextArchiveOffset == 1 &&
            second.Items.Count == 1 && !second.HasMoreArchives && second.NextArchiveOffset == 2,
            "real page continuation returns both retained copies once");
        Require(first.Items.Concat(second.Items).Select(item => item.Key).ToHashSet(StringComparer.Ordinal)
            .SetEquals(new[] { FirstId, SecondId }), "paging does not duplicate, omit or borrow another pair's artifact");
        Require((await host.LoadRecoveryCatalogAsync(otherPair.Id)).Single().PairId == otherPair.Id,
            "the other pair's catalog remains separate");
        Require((await db.LoadRecoveryArtifactsPageAsync(pair.Id, 10, search: OriginalPath)).Single().RecoveryArtifactId == FirstId,
            "database page search uses originalPath for memberless completed-copy evidence");
        Require((await host.LoadRecoveryCatalogPageAsync(pair.Id, pageSize: 1, search: "not-present-anywhere")).Items.Count == 0,
            "unmatched search is empty");
    }
    private static async Task AssertCompletedEvidenceAsync(SyncAuthorityDatabase db, string id, SyncPair pair)
    {
        var operation = await db.LoadOperationAsync(id);
        Require(operation.Kind == SyncAuthorityOperationKind.Reconcile && operation.Status == SyncAuthorityOperationStatus.Completed &&
            operation.PairId == pair.Id && operation.ConfigurationRevision == pair.PolicyRevision && operation.CapabilityProfileId == Profile &&
            operation.CurrentLeaseId is null && operation.CurrentFencingToken is null && operation.Members.Count == 0 &&
            operation.Allocations.Count == 0 && operation.Attempts.Count == 0 && operation.PlanningAuthorities.Count == 0,
            "manual record describes completed retention without staging, effect attempts, lease or planning authority");
        Require((await db.ListRunnableOperationsAsync()).All(item => item.OperationId != id) &&
            (await db.ListRunnableOperationsForPairAsync(pair.Id)).All(item => item.OperationId != id),
            "global and per-pair real queues omit completed retention evidence");
        var rejected = false;
        try { await db.AcquireOperationLeaseAsync(id, "manual-record-replay-worker", TimeSpan.FromSeconds(15)); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "the real lease gate refuses replay of a completed retention record");
    }
    private static async Task<string> SnapshotAsync(SyncAuthorityDatabase db) => JsonSerializer.Serialize(
        (await db.LoadRecoveryArtifactsAsync()).OrderBy(row => row.RecoveryArtifactId, StringComparer.Ordinal));

    private static void CleanOwnRoot(string parent, string root)
    {
        var full = Path.GetFullPath(root);
        Require(string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(full).StartsWith("manual-records-", StringComparison.Ordinal), "cleanup must remain within this exact fixture child");
        if (!Directory.Exists(full)) return;
        var pending = new Stack<string>(); pending.Push(full);
        while (pending.TryPop(out var directory))
        {
            Require((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0, "do not clean through a reparse directory");
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                Require((attributes & FileAttributes.ReparsePoint) == 0, "do not clean an unexpected reparse member");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        Directory.Delete(full, recursive: true);
        Require(!Directory.Exists(full), "owned tiny fixture state is removed after handles close");
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new FixtureFailure("Manual recovery record checks: " + message); }
    private sealed class FixtureFailure(string message) : Exception(message);
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Attempts++; throw new FixtureFailure("No provider transport is authorized by this database/catalog fixture"); }
    }
    private sealed class NoCredentials : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => throw new FixtureFailure("No credential reads are authorized");
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) => throw new FixtureFailure("No credential writes are authorized");
        public Task DeleteAsync(CancellationToken cancellationToken = default) => throw new FixtureFailure("No credential deletion is authorized");
    }
}
