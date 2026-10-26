using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Only provider input is synthetic. The public Host, adapter, conditional guard, spool,
// operation admission, fenced attempts and receipts are the production implementations.
[SupportedOSPlatform("windows")]
internal static class GoogleRevisionRestoreHostChecks
{
    private const string Account = "revision-host-account";
    private const string RootId = "revision-host-root";
    private const string RecoveryId = "revision-host-recovery";
    private const string FileId = "revision-host-file";
    private const string FileName = "example.bin";
    private const string RevisionId = "revision-host-old";
    private static readonly byte[] OldBytes = Encoding.UTF8.GetBytes("exact older revision bytes\n");
    private static readonly byte[] CurrentBytes = Encoding.UTF8.GetBytes("current destination predecessor\n");
    private static readonly byte[] CollaboratorBytes = Encoding.UTF8.GetBytes("collaborator changed these bytes\n");
    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Md5(byte[] bytes) => Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();

    internal static async Task RunAsync(string scratchRoot)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The public restore fixture uses held NTFS roots.");
        var failures = new List<Exception>();
        async Task Check(string id, Func<Task> action)
        {
            try { await action(); Console.WriteLine("PASS Google public revision " + id); }
            catch (Exception error)
            {
                Console.WriteLine("FAIL Google public revision " + id + ": " + error.GetType().Name + ": " + error.Message);
                failures.Add(new InvalidOperationException(id + ": " + error.Message, error));
            }
        }
        await Check("H9/F2 M13/F4 update retained revision", () => SuccessAsync(scratchRoot, copy: false, retained: true));
        await Check("H9/F2 M13/F4 retention then update", () => SuccessAsync(scratchRoot, copy: false, retained: false));
        await Check("H9/F2 M13/F4 retention then copy/create", () => SuccessAsync(scratchRoot, copy: true, retained: false));
        foreach (var effect in new[] { "retention", "predecessor", "upload" })
            foreach (var response in new[] { "lost", "malformed", "cancelled" })
                await Check("H9/F2 M13/F4 " + effect + "/" + response + " reopen no replay",
                    () => UncertainAsync(scratchRoot, effect, response));
        await Check("H9/F2 M13/F4 independent readback mismatch", () => ReadbackMismatchAsync(scratchRoot));
        await Check("H9/F2 M13/F4 destination changes after strong guard", () => DestinationRaceAsync(scratchRoot));
        await Check("H9/F2 M13/F4 copy destination becomes occupied", () => CopyRaceAsync(scratchRoot));
        await Check("H9/F2 M13/F4 confirmed retention then failed media", () => RetainedReadFailureAsync(scratchRoot));
        await Check("H9/F2 M13/F4 pre-effect read failure safe retry", () => BeforeEffectAsync(scratchRoot));
        await Check("H9/F2 M13/F4 operation registration failure", () => RegistrationFailureAsync(scratchRoot));
        foreach (var stale in new[] { "account", "revision", "file" })
            await Check("H9/F2 M13/F4 stale " + stale + " no effects", () => StaleAsync(scratchRoot, stale));
        if (failures.Count != 0) throw new AggregateException("Google public Host revision restore checks failed", failures);
    }

    private static async Task SuccessAsync(string scratch, bool copy, bool retained)
    {
        using var f = await Harness.CreateAsync(scratch, retained);
        var item = await f.ItemAsync();
        Require(item.Identity == FileId && item.Revision == RevisionId && item.ContentHash == "md5:" + Md5(OldBytes),
            "the public catalog retains the selected exact historical identity");
        Require(f.Wire.Effects.Count == 0, "catalog listing is read-only");
        var result = await f.Host.RestoreRecoveryAsync(item, copy);
        var restored = f.Wire.Nodes.Values.Single(node => node.Parent == RootId && node.Name == result.TargetRelativePath);
        Require(restored.Bytes.SequenceEqual(OldBytes), "the public restore wrote the verified full old revision bytes");
        Require(!copy ? restored.Id == FileId : restored.Id != FileId, "update and copy keep their respective object identities");
        Require(!copy ? restored.Version == 8 : restored.Version == 1, "the exact successor version is independently read back");
        Require(!copy ? f.Wire.RecoveryFiles.Single().Bytes.SequenceEqual(CurrentBytes) : f.Wire.RecoveryFiles.Count == 0,
            "an update retains exactly the displaced predecessor; a fresh copy displaces nothing");
        if (copy) Require(f.Wire.Nodes[FileId].Bytes.SequenceEqual(CurrentBytes), "copy leaves the original destination unchanged");
        var expected = new List<string>();
        if (!retained) expected.Add("retention");
        if (!copy) expected.Add("predecessor");
        expected.Add("upload");
        Require(f.Wire.Effects.SequenceEqual(expected), "all and only the required effects occurred in order");
        var journal = await f.ReadJournalAsync();
        Require(journal.Operations.Count == 1 && journal.Operations.Single().Status == "completed" &&
            journal.Attempts.All(row => row.Status == "verified_success") && journal.Attempts.Count == expected.Count &&
            journal.Receipts.Count == expected.Count && journal.Receipts.All(row => row.Certainty == "known_success") &&
            journal.ManualCommits == 1 && journal.ActiveLeases == 0,
            "one public restore completes the exact attempts and one manual commit, and releases its lease");
        var last = journal.Receipts.Single(row => row.AttemptId == journal.Attempts.Last().Id);
        using var successor = JsonDocument.Parse(last.DestinationJson);
        Require(successor.RootElement.GetProperty("path").GetString() == result.TargetRelativePath &&
            successor.RootElement.GetProperty("Id").GetString() == restored.Id &&
            successor.RootElement.GetProperty("Revision").GetString() == restored.Revision &&
            successor.RootElement.GetProperty("Size").GetInt64() == OldBytes.Length &&
            successor.RootElement.GetProperty("ContentHash").GetString() == Sha(OldBytes),
            "the durable receipt binds path, id, version, length and SHA-256 rather than the revision MD5");
        var ids = journal.Attempts.Select(row => row.Id).ToArray();
        await f.ReopenAsync();
        var reopened = await f.ReadJournalAsync();
        Require(reopened.Attempts.Select(row => row.Id).SequenceEqual(ids) && reopened.ManualCommits == 1 &&
            f.Wire.Effects.SequenceEqual(expected), "Host reopen neither reissues nor changes a committed restore");
        f.AssertPrivateCleanup();
    }

    private static async Task UncertainAsync(string scratch, string effect, string response)
    {
        using var f = await Harness.CreateAsync(scratch, retained: effect != "retention");
        var item = await f.ItemAsync();
        f.Wire.FaultEffect = effect; f.Wire.FaultResponse = response;
        using var cancel = new CancellationTokenSource(); f.Wire.Cancel = cancel;
        var error = await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false, cancel.Token));
        Require(response != "cancelled" || cancel.IsCancellationRequested &&
            error is GoogleDriveEffectUncertainException { InnerException: OperationCanceledException },
            "the actual adapter classifies a cancelled dispatched request as effect-uncertain and retains its cancellation cause");
        Require(f.Wire.Effects.LastOrDefault() == effect && f.Wire.FaultTriggered,
            "the fixture reached and applied the named provider effect before losing its confirmation");
        Require(effect != "upload" ? f.Wire.Nodes[FileId].Bytes.SequenceEqual(CurrentBytes) :
            f.Wire.Nodes[FileId].Bytes.SequenceEqual(OldBytes), "only the dispatched effect changed provider bytes");
        Require(effect == "retention" ? f.Wire.KeepForever && f.Wire.RecoveryFiles.Count == 0 :
            f.Wire.RecoveryFiles.Single().Bytes.SequenceEqual(CurrentBytes),
            "retention is itself applied, or the exact displaced predecessor is retained");
        var before = await f.ReadJournalAsync();
        Require(before.Operations.Count == 1 && before.Operations.Single().Status is not ("completed" or "superseded") &&
            before.Attempts.Last().Status is "effect_unknown" or "request_sent" &&
            before.Receipts.Any(row => row.AttemptId == before.Attempts.Last().Id && row.Certainty == "unknown") &&
            before.ManualCommits == 0 && before.ActiveLeases == 0,
            "lost, malformed and cancelled effect replies retain their precise unresolved authority and never a commit");
        f.AssertPrivateCleanup();
        f.Wire.FaultEffect = null; f.Wire.Cancel = null;
        var effects = f.Wire.Effects.ToArray(); var attemptIds = before.Attempts.Select(row => row.Id).ToArray();
        await f.ReopenAsync();
        Require(f.Wire.Effects.SequenceEqual(effects), "startup reconciliation does not replay a provider mutation");
        // A new public command must not evade the persisted unresolved object by getting a new command id.
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var after = await f.ReadJournalAsync();
        Require(f.Wire.Effects.SequenceEqual(effects) && after.Attempts.Select(row => row.Id).SequenceEqual(attemptIds) &&
            after.ManualCommits == 0 && after.Operations.Any(row => row.Id == before.Operations.Single().Id &&
                row.Status is not ("completed" or "superseded")),
            "reopened public retry refuses without a new retention, predecessor copy, upload or discarded unknown attempt");
        f.AssertPrivateCleanup();
    }

    private static async Task ReadbackMismatchAsync(string scratch)
    {
        using var f = await Harness.CreateAsync(scratch, retained: true);
        var item = await f.ItemAsync(); f.Wire.BadSuccessorReadback = true;
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var journal = await f.ReadJournalAsync();
        Require(f.Wire.Effects.SequenceEqual(new[] { "predecessor", "upload" }) &&
            f.Wire.Nodes[FileId].Bytes.SequenceEqual(OldBytes) && journal.ManualCommits == 0 &&
            journal.Attempts.Last().Status is "effect_unknown" or "request_sent",
            "an applied upload with mismatched independent readback stays unresolved and never commits");
        f.AssertPrivateCleanup();
    }

    private static async Task DestinationRaceAsync(string scratch)
    {
        using var f = await Harness.CreateAsync(scratch, retained: true);
        var item = await f.ItemAsync(); f.Wire.RaceAfterStrongGuard = true;
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var journal = await f.ReadJournalAsync();
        Require(f.Wire.GuardRaced && f.Wire.ConditionalRejected && f.Wire.UploadApplied == 0 &&
            f.Wire.Nodes[FileId].Bytes.SequenceEqual(CollaboratorBytes) &&
            f.Wire.RecoveryFiles.Single().Bytes.SequenceEqual(CurrentBytes) && journal.ManualCommits == 0,
            "the actual adapter carries the stale strong If-Match and the server refuses it, preserving collaborator bytes");
        f.AssertPrivateCleanup();
    }

    private static async Task CopyRaceAsync(string scratch)
    {
        using var f = await Harness.CreateAsync(scratch, retained: false);
        var item = await f.ItemAsync(); f.Wire.OccupyCopyDuringMedia = true;
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, true));
        var journal = await f.ReadJournalAsync();
        Require(f.Wire.CopyRacePath is not null && f.Wire.Nodes.Values.Single(node => node.Name == f.Wire.CopyRacePath)
                .Bytes.SequenceEqual(CollaboratorBytes) && f.Wire.UploadApplied == 0 &&
            f.Wire.Effects.SequenceEqual(new[] { "retention" }) && journal.ManualCommits == 0,
            "copy rebind refuses an occupied preview destination after retention and preserves that collaborator object");
        f.AssertPrivateCleanup();
    }

    private static async Task BeforeEffectAsync(string scratch)
    {
        using var f = await Harness.CreateAsync(scratch, retained: true);
        var item = await f.ItemAsync(); f.Wire.FailMedia = true;
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var before = await f.ReadJournalAsync();
        Require(f.Wire.MediaReads == 1 && f.Wire.Effects.Count == 0 && before.Attempts.Count == 0 &&
            before.Operations.Single().Status == "superseded" && before.NoEffectSupersedes == 1,
            "a failed private read before all effects retires only the proved zero-effect operation");
        f.AssertPrivateCleanup(); f.Wire.FailMedia = false;
        await f.ReopenAsync();
        var result = await f.Host.RestoreRecoveryAsync(item, false);
        var after = await f.ReadJournalAsync();
        Require(result.TargetRelativePath == FileName && after.ManualCommits == 1 &&
            f.Wire.Effects.SequenceEqual(new[] { "predecessor", "upload" }),
            "a clean zero-effect refusal permits one legitimate fresh public restore after reopen");
        f.AssertPrivateCleanup();
    }

    private static async Task RetainedReadFailureAsync(string scratch)
    {
        using var f = await Harness.CreateAsync(scratch, retained: false);
        var item = await f.ItemAsync(); f.Wire.FailMedia = true;
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var before = await f.ReadJournalAsync();
        Require(f.Wire.Effects.SequenceEqual(new[] { "retention" }) && f.Wire.KeepForever &&
            before.Attempts.Single().Status == "verified_success" && before.Receipts.Single().Certainty == "known_success" &&
            before.Operations.Single().Status is not ("completed" or "superseded") && before.NoEffectSupersedes == 0,
            "successful retention followed by a failed byte read remains an effect-bearing operation and cannot become no-effect history");
        f.AssertPrivateCleanup(); f.Wire.FailMedia = false;
        await f.ReopenAsync();
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var after = await f.ReadJournalAsync();
        Require(f.Wire.Effects.SequenceEqual(new[] { "retention" }) && after.Attempts.Count == 1 && after.ManualCommits == 0,
            "the retained effect-bearing operation prevents a fresh command from silently replaying later mutations");
        f.AssertPrivateCleanup();
    }

    private static async Task RegistrationFailureAsync(string scratch)
    {
        using var f = await Harness.CreateAsync(scratch, retained: false);
        var item = await f.ItemAsync();
        await f.FailOperationRegistrationAsync(true, item);
        try
        {
            await f.Host.Authority.VerifyHealthAsync();
            var error = await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
            Require(error is SqliteException && error.Message.Contains("synthetic operation registration failure", StringComparison.Ordinal),
                "the production operation registration propagates the actual injected SQLite failure");
            Require(ReferenceEquals(error, f.OperationRegistrationFailure) && f.OperationRegistrationFaultCalls == 1,
                "the exact selected public restore reached the real registration boundary once");
            await f.Host.Authority.VerifyHealthAsync();
            var before = await f.ReadJournalAsync();
            Require(f.Wire.Effects.Count == 0 && before.Operations.Count == 0 && before.Attempts.Count == 0 &&
                before.ActiveLeases == 0 && f.Wire.Nodes[FileId].Bytes.SequenceEqual(CurrentBytes),
                "failure before durable operation registration cannot dispatch retention, preimage copy or upload");
            f.AssertPrivateCleanup();
        }
        finally { await f.FailOperationRegistrationAsync(false); }
        await f.Host.RestoreRecoveryAsync(item, false);
        Require((await f.ReadJournalAsync()).ManualCommits == 1 &&
            f.Wire.Effects.SequenceEqual(new[] { "retention", "predecessor", "upload" }),
            "removing the exact fixture database fault permits the legitimate public restore");
        f.AssertPrivateCleanup();
    }

    private static async Task StaleAsync(string scratch, string stale)
    {
        using var f = await Harness.CreateAsync(scratch, retained: false);
        var item = await f.ItemAsync();
        if (stale == "account") f.Wire.Account = "different-permission-id";
        else if (stale == "revision") item = item with { Revision = "other-revision" };
        else item = item with { Identity = "other-file" };
        await RejectAsync(() => f.Host.RestoreRecoveryAsync(item, false));
        var journal = await f.ReadJournalAsync();
        Require(f.Wire.Effects.Count == 0 && journal.Attempts.Count == 0 && journal.ManualCommits == 0 &&
            f.Wire.Nodes[FileId].Bytes.SequenceEqual(CurrentBytes), "stale identity/account refuses before any provider effect");
        f.AssertPrivateCleanup();
    }

    private static async Task<Exception> RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception error) when (error is not AssertionFailure) { return error; }
        throw new AssertionFailure("Expected the public Host to refuse this exact unsafe restore.");
    }
    private static void Require(bool value, string description)
    { if (!value) throw new AssertionFailure(description); }
    private sealed class AssertionFailure(string message) : Exception(message);
    private sealed record OperationRow(string Id, string Status);
    private sealed record AttemptRow(string Id, string Effect, string Status);
    private sealed record ReceiptRow(string AttemptId, string Certainty, string DestinationJson);
    private sealed record Journal(List<OperationRow> Operations, List<AttemptRow> Attempts,
        List<ReceiptRow> Receipts, long ManualCommits, long NoEffectSupersedes, long ActiveLeases);

    private sealed class Harness : IDisposable
    {
        private readonly string _root;
        private readonly string _product;
        private readonly string? _previousClient;
        private readonly IDisposable? _productOverride;
        private readonly string _revisionStage;
        private readonly HashSet<string> _initialStageFiles;
        private readonly PilotInstanceLock _owner;
        private readonly HttpClient _http;
        internal Server Wire { get; }
        internal SyncPair Pair { get; private set; } = null!;
        internal SyncAuthorityRuntimeHost Host { get; private set; }
        internal int OperationRegistrationFaultCalls { get; private set; }
        internal SqliteException? OperationRegistrationFailure { get; private set; }
        private string DatabasePath => Path.Combine(_root, "authority.db");

        private Harness(string scratch, bool retained)
        {
            var parent = Path.GetFullPath(scratch);
            var installed = Path.GetFullPath(SyncSharkRuntimePaths.ProductRoot).TrimEnd(Path.DirectorySeparatorChar);
            Require(!string.Equals(parent, installed, StringComparison.OrdinalIgnoreCase) &&
                !parent.StartsWith(installed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "fixture scratch must not be the installed product tree");
            _root = Path.Combine(parent, "google-public-revision-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            // CoreTests already owns an IsolatedTestProductRuntime; UiFixture's revision
            // selector does not. Borrow an established test runtime without changing its
            // lifetime, or own exactly one isolated override for this fixture.
            if (SyncSharkRuntimePaths.HasTestProductRoot)
                _product = Path.GetFullPath(SyncSharkRuntimePaths.ProductRoot);
            else
            {
                _product = Path.Combine(Path.GetTempPath(), "google-public-revision-" + Guid.NewGuid().ToString("N"));
                _productOverride = SyncSharkRuntimePaths.OverrideProductRootForTest(_product);
            }
            Require(SyncSharkRuntimePaths.HasTestProductRoot, "the Host fixture requires an isolated test product runtime");
            _revisionStage = SyncSharkRuntimePaths.ReplacementStagingRoot;
            _initialStageFiles = RevisionStageFiles();
            _previousClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "test-client";
            _owner = PilotInstanceLock.Acquire(Path.Combine(_root, "state", "instance.lock"));
            Wire = new Server(DatabasePath, retained);
            _http = new HttpClient(Wire);
            Host = NewHost();
        }

        internal static async Task<Harness> CreateAsync(string scratch, bool retained)
        {
            var f = new Harness(scratch, retained);
            try
            {
                await new GoogleDriveConnectionStore(f._owner.StateAuthority).SaveAsync(Binding());
                await f.Host.LoadPairsAsync();
                var installation = await f.Host.Authority.LoadInstallationIdAsync() ??
                    throw new AssertionFailure("The public Host did not persist an installation identity.");
                var local = Path.Combine(f._root, "local"); Directory.CreateDirectory(local);
                using var held = PilotDirectoryLease.Open(local);
                var ns = SyncControlNamespace.BindLocalForTest(held, installation, "google-revision-pair");
                f.Pair = new SyncPair("google-revision-pair", "Google revision fixture", local,
                    held.Identity.VolumeSerialNumber, held.Identity.FileId, "/Pair", RootId,
                    ControlNamespace: new SyncControlNamespaceBinding(installation, "google-revision-pair", ns),
                    RecoveryRoot: new SyncRemoteRecoveryRootBinding("/Recovery", RecoveryId, Binding().AccountKey),
                    ConnectionId: Binding().ConnectionId, Provider: SyncProviders.GoogleDrive, InitialReviewApproved: true);
                await f.Host.Authority.RegisterPairAsync(f.Pair.Id, f.Pair.PolicyRevision,
                    SyncAuthorityPairConfiguration.FromPair(f.Pair));
                await f.Host.Authority.RetainPairDefinitionAsync(f.Pair, true);
                await f.Host.Authority.SetPairStateAsync(f.Pair.Id, "active");
                return f;
            }
            catch { f.Dispose(); throw; }
        }
        private SyncAuthorityRuntimeHost NewHost() => new(_owner.StateAuthority, _http,
            new SyncPairStore(_owner.StateAuthority), new SyncRuntimeStateStore(_owner.StateAuthority),
            new DropboxConnectionStore(_owner.StateAuthority), new DenyDropbox(), new SyncAuthorityDatabase(DatabasePath),
            googleDriveCredentialStore: new GoogleCredentials(), googleDriveTokenEndpoint: new GoogleEndpoint());
        internal async Task<SyncRecoveryCatalogItem> ItemAsync() =>
            (await Host.LoadDropboxRevisionCatalogAsync(Pair.Id, FileName)).Single();
        internal async Task ReopenAsync()
        {
            Host.Dispose(); Host = NewHost(); await Host.LoadPairsAsync();
        }
        internal Task FailOperationRegistrationAsync(bool fail, SyncRecoveryCatalogItem? requested = null)
        {
            // A persisted trigger changes the exact schema inventory and fails health admission,
            // not operation registration. Inject only a typed failure on this database instance's
            // real admitted INSERT boundary; no schema, operation, lease or receipt is fabricated.
            if (!fail)
            {
                Host.Authority.BeforeOperationRegistrationForTest = null;
                return Task.CompletedTask;
            }
            ArgumentNullException.ThrowIfNull(requested);
            var failure = new SqliteException("synthetic operation registration failure", 19);
            OperationRegistrationFailure = failure;
            OperationRegistrationFaultCalls = 0;
            Host.Authority.BeforeOperationRegistrationForTest = plan =>
            {
                using var document = JsonDocument.Parse(plan.PreconditionsJson);
                var input = document.RootElement;
                Require(plan.Kind == SyncAuthorityOperationKind.Reconcile && plan.PairId == Pair.Id &&
                    plan.ConfigurationRevision == Pair.PolicyRevision && !string.IsNullOrWhiteSpace(plan.OperationId) &&
                    !string.IsNullOrWhiteSpace(plan.IdempotencyKey) && !string.IsNullOrWhiteSpace(plan.ObjectId) &&
                    input.GetProperty("action").GetString() == "restore_recovery" &&
                    input.GetProperty("revisionRestore").GetBoolean() && !input.GetProperty("restoreAsCopy").GetBoolean() &&
                    input.GetProperty("pairId").GetString() == Pair.Id && input.GetProperty("path").GetString() == FileName &&
                    input.GetProperty("configurationRevision").GetInt64() == Pair.PolicyRevision &&
                    input.GetProperty("recoveryArtifactId").GetString() == requested.Key &&
                    input.GetProperty("Identity").GetString() == requested.Identity &&
                    input.GetProperty("Revision").GetString() == requested.Revision &&
                    input.GetProperty("Size").GetInt64() == requested.Size &&
                    input.GetProperty("ContentHash").GetString() == requested.ContentHash &&
                    plan.Members.Count == 1 && plan.Members[0].PairId == Pair.Id &&
                    plan.Members[0].ObjectId == plan.ObjectId && plan.Members[0].Role == "affected" &&
                    plan.Members[0].RootBindingId == input.GetProperty("endpointRootBindingId").GetString() &&
                    plan.Members[0].ExpectedPathKey == FileName && plan.Members[0].ExpectedStateJson == plan.PreconditionsJson,
                    "the registration fault is bound to the exact selected revision restore plan/member");
                OperationRegistrationFaultCalls++;
                throw failure;
            };
            return Task.CompletedTask;
        }
        internal async Task<Journal> ReadJournalAsync()
        {
            await using var connection = ReadConnection(DatabasePath); await connection.OpenAsync();
            var operations = new List<OperationRow>(); var attempts = new List<AttemptRow>(); var receipts = new List<ReceiptRow>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT operation_id,status FROM operation WHERE json_extract(preconditions_json,'$.revisionRestore')=1 ORDER BY created_utc,operation_id;";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) operations.Add(new(reader.GetString(0), reader.GetString(1)));
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT a.attempt_id,a.effect_kind,a.status FROM operation_attempt a JOIN operation o USING(operation_id) WHERE json_extract(o.preconditions_json,'$.revisionRestore')=1 ORDER BY o.created_utc,o.operation_id,a.attempt_no;";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) attempts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT r.attempt_id,r.certainty,r.destination_identity_json FROM effect_receipt r JOIN operation_attempt a USING(attempt_id) JOIN operation o USING(operation_id) WHERE json_extract(o.preconditions_json,'$.revisionRestore')=1 ORDER BY a.attempt_no,r.observed_utc;";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) receipts.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return new(operations, attempts, receipts,
                await ScalarAsync(connection, "SELECT count(*) FROM history_event WHERE event_kind='manual_restore_committed';"),
                await ScalarAsync(connection, "SELECT count(*) FROM history_event WHERE event_kind='manual_restore_no_effect_superseded';"),
                await ScalarAsync(connection, "SELECT count(*) FROM lease WHERE status='active';"));
        }
        internal void AssertPrivateCleanup()
        {
            Require(SyncSharkRuntimePaths.HasTestProductRoot &&
                string.Equals(Path.GetFullPath(SyncSharkRuntimePaths.ProductRoot), _product, StringComparison.OrdinalIgnoreCase),
                "the caller's isolated product binding remains unchanged during the fixture");
            Require(!Host.HasActiveRun && _initialStageFiles.SetEquals(RevisionStageFiles()) &&
                SyncRecoverySpoolAdmission.SnapshotForTest(Path.GetPathRoot(_product)!) == (0L, 0, 0),
                "the public failure/success releases run ownership, private revision bytes and full spool reservation");
        }
        private HashSet<string> RevisionStageFiles() => Directory.Exists(_revisionStage)
            ? Directory.EnumerateFiles(_revisionStage, "*.tmp", SearchOption.TopDirectoryOnly).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public void Dispose()
        {
            Host.Authority.BeforeOperationRegistrationForTest = null;
            Host.Dispose(); _http.Dispose(); _owner.Dispose(); _productOverride?.Dispose();
            GoogleDriveProviderConfiguration.RegisteredClientIdForTest = _previousClient;
            SqliteConnection.ClearAllPools();
            // The state root is always ours. A borrowed caller product root, including its
            // existing stage members, is never disposed, deleted or claimed by this fixture.
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            if (_productOverride is not null && Directory.Exists(_product)) Directory.Delete(_product, recursive: true);
        }
    }

    private static GoogleDriveConnectionBinding Binding() => new GoogleDriveConnectionBinding("test-client",
        GoogleDriveConnectionBinding.CreateAccountKey(Account), Account, "fixture@example.invalid", "Fixture",
        [GoogleDriveProviderConfiguration.DriveScope], CredentialReference: "synthetic-only").Normalize();
    private static SqliteConnection ReadConnection(string path) => new(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class Node(string id, string name, string? parent, byte[]? bytes = null, int version = 1)
    {
        internal string Id = id;
        internal string Name = name;
        internal string? Parent = parent;
        internal byte[] Bytes = bytes ?? [];
        internal int Version = version;
        internal bool Folder = bytes is null;
        internal string Revision => JsonSerializer.Serialize(new[] { Id, Version.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        internal object Metadata(bool corruptHash = false) => new
        {
            id = Id, name = Name, parents = Parent is null ? Array.Empty<string>() : new[] { Parent },
            mimeType = Folder ? GoogleDrivePilotClient.FolderMimeType : "application/octet-stream", trashed = false,
            version = Version.ToString(System.Globalization.CultureInfo.InvariantCulture),
            size = Bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            md5Checksum = Folder ? null : Md5(Bytes), sha256Checksum = Folder ? null : corruptHash ? new string('0', 64) : Sha(Bytes),
            capabilities = new { canDownload = true, canEdit = true }, ownedByMe = true
        };
    }

    private sealed class Server : HttpMessageHandler
    {
        private readonly string _database;
        internal Dictionary<string, Node> Nodes = new(StringComparer.Ordinal);
        internal List<string> Effects = [];
        internal string Account = GoogleRevisionRestoreHostChecks.Account;
        internal bool KeepForever;
        internal string? FaultEffect, FaultResponse;
        internal CancellationTokenSource? Cancel;
        internal bool FaultTriggered, BadSuccessorReadback, RaceAfterStrongGuard, GuardRaced, ConditionalRejected;
        internal bool OccupyCopyDuringMedia, FailMedia;
        internal string? CopyRacePath;
        internal int UploadApplied, MediaReads;
        internal List<Node> RecoveryFiles => Nodes.Values.Where(node => node.Parent == RecoveryId && !node.Folder).ToList();
        internal Server(string database, bool retained)
        {
            _database = database; KeepForever = retained;
            Nodes[RootId] = new(RootId, "Pair", null);
            Nodes[RecoveryId] = new(RecoveryId, "Recovery", null);
            Nodes[FileId] = new(FileId, FileName, RootId, CurrentBytes.ToArray(), 7);
        }
        private object RevisionMetadata() => new { id = RevisionId, mimeType = "application/octet-stream",
            size = OldBytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), md5Checksum = Md5(OldBytes),
            keepForever = KeepForever, modifiedTime = "2026-09-01T12:00:00Z" };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var uri = request.RequestUri!; var path = uri.AbsolutePath;
            Require(uri.Scheme == "https" && uri.Host == "www.googleapis.com" && uri.Port == 443 &&
                request.Headers.Authorization?.Scheme == "Bearer", "only the bound official provider origin receives synthetic authorization");
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/drive/v3/about") return Json(new { user = new { permissionId = Account }, storageQuota = new { } });
                if (path == "/drive/v3/files/" + FileId + "/revisions")
                    return Json(new { revisions = new[] { RevisionMetadata() } });
                if (path == "/drive/v3/files/" + FileId + "/revisions/" + RevisionId)
                {
                    if (Query(request, "alt") != "media") return Json(RevisionMetadata());
                    MediaReads++;
                    if (FailMedia) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("synthetic unavailable") };
                    if (OccupyCopyDuringMedia)
                    {
                        await using var connection = ReadConnection(_database); await connection.OpenAsync(token);
                        await using var command = connection.CreateCommand();
                        command.CommandText = "SELECT json_extract(preconditions_json,'$.path') FROM operation WHERE json_extract(preconditions_json,'$.revisionRestore')=1 ORDER BY created_utc DESC LIMIT 1;";
                        CopyRacePath = (string?)await command.ExecuteScalarAsync(token) ?? throw new AssertionFailure("No exact preview copy target is journalled.");
                        Nodes["copy-collaborator"] = new("copy-collaborator", CopyRacePath, RootId, CollaboratorBytes.ToArray());
                        OccupyCopyDuringMedia = false;
                    }
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(OldBytes) };
                }
                if (path == "/drive/v3/files") return List(request);
                if (path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
                {
                    var id = Uri.UnescapeDataString(path["/drive/v3/files/".Length..]);
                    Require(Nodes.TryGetValue(id, out var node), "the adapter requested an existing exact synthetic object id");
                    return Json(node!.Metadata(BadSuccessorReadback && id == FileId && UploadApplied > 0));
                }
                if (path == "/drive/v2/files/" + FileId)
                {
                    var node = Nodes[FileId]; var version = node.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var response = Json(new { id = FileId, etag = "\"" + FileId + "-" + version + "\"", version,
                        mimeType = "application/octet-stream", labels = new { trashed = false }, parents = new[] { new { id = RootId } } });
                    if (RaceAfterStrongGuard && !GuardRaced)
                    { GuardRaced = true; node.Bytes = CollaboratorBytes.ToArray(); node.Version++; }
                    return response;
                }
                throw new AssertionFailure("Unexpected synthetic read route " + path);
            }
            if (request.Method == HttpMethod.Patch && path == "/drive/v3/files/" + FileId + "/revisions/" + RevisionId)
            {
                await BeforeEffectAsync("retention", request, token);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                Require(body.RootElement.GetProperty("keepForever").GetBoolean(), "the retention PATCH carries keepForever=true");
                KeepForever = true;
                return Fault("retention") ?? Json(RevisionMetadata());
            }
            if (request.Method == HttpMethod.Post && path == "/drive/v3/files/" + FileId + "/copy")
            {
                await BeforeEffectAsync("predecessor", request, token);
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                var name = body.RootElement.GetProperty("name").GetString()!;
                Require(body.RootElement.GetProperty("parents").EnumerateArray().Select(row => row.GetString()).SequenceEqual(new[] { RecoveryId }),
                    "the predecessor copy uses the exact independently bound Recovery folder");
                var original = Nodes[FileId]; var copied = new Node("retained-" + RecoveryFiles.Count, name, RecoveryId, original.Bytes.ToArray());
                Nodes.Add(copied.Id, copied);
                return Fault("predecessor") ?? Json(copied.Metadata());
            }
            if (request.Method == HttpMethod.Put && path == "/upload/drive/v2/files/" + FileId)
            {
                await BeforeEffectAsync("upload", request, token);
                var node = Nodes[FileId];
                var guard = request.Headers.IfMatch.SingleOrDefault();
                Require(guard is not null && !guard.IsWeak && guard.Tag != "*", "the actual adapter sends a strong exact If-Match");
                if (guard!.Tag != "\"" + FileId + "-" + node.Version + "\"")
                { ConditionalRejected = true; return new(HttpStatusCode.PreconditionFailed) { Content = new StringContent("synthetic stale version") }; }
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                Require(bytes.SequenceEqual(OldBytes), "the conditional request contains only the independently verified historical bytes");
                node.Bytes = bytes; node.Version++; UploadApplied++;
                return Fault("upload") ?? Json(new { id = FileId });
            }
            if (request.Method == HttpMethod.Post && path == "/upload/drive/v3/files")
            {
                await BeforeEffectAsync("upload", request, token);
                Require(!request.Headers.IfMatch.Any() && Query(request, "uploadType") == "multipart",
                    "new copy uses the actual create-only multipart path");
                var (name, parent, bytes) = await ReadMultipartAsync(request, token);
                Require(parent == RootId && Nodes.Values.All(node => node.Parent != parent || node.Name != name) && bytes.SequenceEqual(OldBytes),
                    "copy creates exact verified bytes at its unoccupied reviewed destination");
                var node = new Node("restored-copy", name, parent, bytes); Nodes.Add(node.Id, node); UploadApplied++;
                return Fault("upload") ?? Json(node.Metadata());
            }
            throw new AssertionFailure("Unexpected synthetic provider effect " + request.Method + " " + path);
        }

        private HttpResponseMessage List(HttpRequestMessage request)
        {
            var query = Query(request, "q") ?? throw new AssertionFailure("The provider list must be explicitly scoped.");
            var parent = Regex.Match(query, "'([^']+)' in parents");
            Require(parent.Success && query.Contains("trashed = false", StringComparison.Ordinal),
                "synthetic input obeys the actual exact parent and trash predicate");
            var name = Regex.Match(query, "name = '((?:\\\\.|[^'])*)'");
            var matched = Nodes.Values.Where(node => node.Parent == parent.Groups[1].Value &&
                (!name.Success || node.Name == name.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal)
                    .Replace("\\\\", "\\", StringComparison.Ordinal))).Select(node => node.Metadata()).ToArray();
            return Json(new { files = matched, incompleteSearch = false });
        }

        private async Task BeforeEffectAsync(string effect, HttpRequestMessage request, CancellationToken token)
        {
            await using var connection = ReadConnection(_database); await connection.OpenAsync(token);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT o.operation_id,a.attempt_id,a.attempt_no,o.preconditions_json
                FROM operation o JOIN operation_attempt a USING(operation_id)
                JOIN lease l ON l.lease_id=a.lease_id
                JOIN command_request c ON c.command_request_id=json_extract(o.preconditions_json,'$.restoreCommandId')
                WHERE json_extract(o.preconditions_json,'$.revisionRestore')=1
                  AND o.status='effect_issuing' AND a.status='request_sent' AND a.effect_kind=$effect
                  AND o.current_lease_id=a.lease_id AND o.current_fencing_token=a.fencing_token
                  AND l.status='active' AND l.fencing_token=a.fencing_token
                  AND c.status='executing' AND c.target_id=json_extract(o.preconditions_json,'$.recoveryArtifactId')
                  AND EXISTS(SELECT 1 FROM operation_member m WHERE m.operation_id=o.operation_id
                    AND m.role='affected' AND m.expected_path_key=json_extract(o.preconditions_json,'$.path')
                    AND m.root_binding_id=json_extract(o.preconditions_json,'$.endpointRootBindingId'));
                """;
            command.Parameters.AddWithValue("$effect", effect switch
            { "retention" => "revision-retention", "predecessor" => "revision-predecessor-copy", _ => "revision-conditional-upload" });
            string operationId; int number; string preconditions;
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                Require(await reader.ReadAsync(token), "request_sent, exact affected member, accepted command and current fenced lease exist before " + effect);
                operationId = reader.GetString(0); number = reader.GetInt32(2); preconditions = reader.GetString(3);
                Require(!await reader.ReadAsync(token), "exactly one current dispatched attempt owns the HTTP effect");
            }
            using var identity = JsonDocument.Parse(preconditions);
            Require(identity.RootElement.GetProperty("Identity").GetString() == FileId &&
                identity.RootElement.GetProperty("Revision").GetString() == RevisionId &&
                identity.RootElement.GetProperty("ContentHash").GetString() == "md5:" + Md5(OldBytes),
                "the durable operation binds the exact selected catalog revision before the mutation");
            await using var prior = connection.CreateCommand();
            prior.CommandText = "SELECT count(*) FROM operation_attempt WHERE operation_id=$id AND attempt_no<$number AND status<>'verified_success';";
            prior.Parameters.AddWithValue("$id", operationId); prior.Parameters.AddWithValue("$number", number);
            Require(Convert.ToInt64(await prior.ExecuteScalarAsync(token)) == 0, "prior effects are verified before the next HTTP mutation");
            if (effect == "predecessor")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                await using var allocation = connection.CreateCommand();
                allocation.CommandText = "SELECT count(*) FROM resource_allocation WHERE operation_id=$id AND resource_kind='remote_recovery_file' AND status='creating' AND deterministic_name=$name;";
                allocation.Parameters.AddWithValue("$id", operationId); allocation.Parameters.AddWithValue("$name", body.RootElement.GetProperty("name").GetString());
                Require(Convert.ToInt64(await allocation.ExecuteScalarAsync(token)) == 1,
                    "the exact predecessor allocation is durably creating before server-side copy");
            }
            Effects.Add(effect);
            Console.WriteLine("Google public revision boundary: " + effect + "; attempt=" + number + "; request_sent/fence/member/command verified");
        }

        private HttpResponseMessage? Fault(string effect)
        {
            if (FaultTriggered || effect != FaultEffect) return null;
            FaultTriggered = true;
            if (FaultResponse == "lost") throw new HttpRequestException("Synthetic response lost after " + effect + " applied.");
            if (FaultResponse == "cancelled") { Cancel!.Cancel(); throw new OperationCanceledException(Cancel.Token); }
            if (FaultResponse == "malformed") return new(HttpStatusCode.OK) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
            throw new AssertionFailure("Unknown synthetic response fault.");
        }
        private static async Task<(string Name, string Parent, byte[] Bytes)> ReadMultipartAsync(HttpRequestMessage request, CancellationToken token)
        {
            var boundary = request.Content!.Headers.ContentType!.Parameters.Single(row => row.Name == "boundary").Value!.Trim('"');
            var text = Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(token));
            var parts = text.Split("--" + boundary, StringSplitOptions.None);
            Require(parts.Length == 4, "the actual adapter emits one metadata and one media multipart member");
            static string Content(string part)
            {
                var windows = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                var unix = part.IndexOf("\n\n", StringComparison.Ordinal);
                var start = windows >= 0 ? windows + 4 : unix >= 0 ? unix + 2 : -1;
                Require(start >= 0, "multipart members retain their explicit header boundary");
                var content = part[start..];
                return content.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? content[..^Environment.NewLine.Length] : content;
            }
            using var metadata = JsonDocument.Parse(Content(parts[1]));
            return (metadata.RootElement.GetProperty("name").GetString()!,
                metadata.RootElement.GetProperty("parents")[0].GetString()!, Encoding.UTF8.GetBytes(Content(parts[2])));
        }
        private static string? Query(HttpRequestMessage request, string key) => request.RequestUri!.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries).Select(part => part.Split('=', 2))
            .Where(pair => Uri.UnescapeDataString(pair[0]) == key)
            .Select(pair => pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "").FirstOrDefault();
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }

    private sealed class DenyDropbox : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken token = default) => throw new AssertionFailure("Google revision restore requested Dropbox credentials.");
        public Task WriteAsync(string value, CancellationToken token = default) => throw new AssertionFailure("Credential mutation is forbidden.");
        public Task DeleteAsync(CancellationToken token = default) => throw new AssertionFailure("Credential mutation is forbidden.");
    }
    private sealed class GoogleCredentials : IGoogleDriveRefreshTokenStore
    {
        public Task<string?> ReadAsync(string connection, CancellationToken token = default)
        { Require(connection == Binding().ConnectionId, "credential lookup uses only the bound synthetic Google account"); return Task.FromResult<string?>("synthetic-refresh"); }
        public Task WriteAsync(string connection, string value, CancellationToken token = default) => throw new AssertionFailure("Credential mutation is forbidden.");
        public Task DeleteAsync(string connection, CancellationToken token = default) => throw new AssertionFailure("Credential mutation is forbidden.");
    }
    private sealed class GoogleEndpoint : IGoogleDriveTokenEndpoint
    {
        public Task<GoogleDriveTokenResult> RefreshAsync(string clientId, string refresh, CancellationToken token = default) =>
            Task.FromResult(new GoogleDriveTokenResult("synthetic-access", DateTime.UtcNow.AddHours(1), [GoogleDriveProviderConfiguration.DriveScope]));
        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string access, CancellationToken token = default) =>
            Task.FromResult(new GoogleDriveAccountIdentity(Account, "fixture@example.invalid", "Fixture", ""));
    }
}
