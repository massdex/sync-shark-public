using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// SS-D18 first corrections batch: the real retained-authority proof that a sent Dropbox move which retained
// no durable receipt is never reclassified from the current local source or the current remote path, that an
// ambiguous receipt never becomes terminal non-application, and that the retained known-success and
// known-not-applied receipt paths still commit or open exactly one permitted retry.
//
// Every authority dependency is shipped production code: the real SyncAuthorityDatabase over an isolated
// SQLite file, its real operation/member/object-version/attempt/receipt/lease/commit transitions, the real
// SyncAuthorityMoveRemoteReconciler, the real SyncAuthorityMoveRemoteExecutor for the one permitted retry and
// the real PilotDirectoryLease/PilotLocalScanner over a real NTFS pair root. Only the provider transport is
// the shared scripted in-memory adapter, which holds a real inventory and performs real identity-preserving
// directory moves: it supplies input and can never replace the production decision under test. Nothing here
// reaches a live account, credential, network, installed database, product state or user pair root.
internal static class MoveRemoteUncertaintyChecks
{
    private const string InstallationId = "installation-move-remote-uncertainty";
    private const string CapabilityProfileId = "profile-move-remote-uncertainty";
    private const string CapabilityProfileJson = "{\"name\":\"move-remote-uncertainty\"}";
    private const string PriorPath = "prior";
    private const string DesiredPath = "moved";
    private const string PredecessorId = "id:folder-move-predecessor";
    private const string EffectKind = "dropbox-native-move";
    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated move-remote uncertainty fixture root.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "The move-remote uncertainty proofs require real Windows NTFS file identities.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("A-request-sent-no-receipt",
            "A sent Dropbox move with no durable receipt returns the current status explicitly unresolved: no known-not-applied from the current path, an unchanged attempt, and zero move calls on a refused retry",
            RequestSentNoReceiptAsync);
        await CaseAsync("B-delayed-original-completion",
            "After the first reconciliation the original request completes late; the second reconciliation still derives nothing from the listing and duplicates no effect",
            DelayedOriginalCompletionAsync);
        await CaseAsync("C-changed-local-source",
            "A changed current local source at the desired path leaves the retained request-sent proof unresolved, unconsumed and un-overwritten",
            ChangedLocalSourceAsync);
        await CaseAsync("D-known-success-readback",
            "A durable known-success receipt plus the exact readback commits the move once, appends no second receipt and never replays",
            KnownSuccessReadbackAsync);
        await CaseAsync("E-provider-rejection-retry",
            "A durable provider-rejection known-not-applied receipt opens exactly one permitted fenced retry, appends no receipt and completes on that retry",
            ProviderRejectionRetryAsync);
        await CaseAsync("F-ambiguous-receipt-unresolved",
            "An ambiguous receipt with the identity still at its prior path and a free destination is not terminal non-application: the operation stays unresolved and no retry is unlocked",
            AmbiguousReceiptUnresolvedAsync);
        await CaseAsync("G-verified-applied-after-ambiguity",
            "A delayed destination observation without a terminal provider receipt stays held, creates no accepted baseline and opens no reissue",
            DestinationAfterAmbiguityHeldAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "MoveRemoteUncertaintyChecks (tests/SyncShark.Core.Tests)",
            ["requirement"] = "SS-D18",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(Path.Combine(_root, "move-remote-uncertainty-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS move remote uncertainty: " + _assertions + " assertions; the real isolated" +
            " authority journal, the real move-remote reconciler and the real move-remote executor prove that a" +
            " sent request with no durable receipt stays unresolved before any source or path observation, that an" +
            " ambiguous receipt never becomes terminal non-application, and that the retained known-success and" +
            " known-not-applied receipt paths still commit once or open exactly one permitted retry.");
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

    private static void Require(bool condition, string label)
    {
        _assertions++;
        if (!condition) throw new InvalidDataException("SS-D18 assertion failed: " + label);
    }

    private static void RequireUnresolved(SyncAuthorityExecutionResult result,
        SyncAuthorityOperationStatus expected, string label)
    {
        Require(result.Status == expected,
            label + ": the operation keeps its retained status " + expected + " instead of a synthesized" +
            " outcome, saw " + result.Status);
        Require(!result.EffectDispatched, label + ": no effect was dispatched at the unresolved boundary");
        Require(result.Reason.Contains("unresolved", StringComparison.Ordinal),
            label + ": the returned result states the unresolved outcome: " + result.Reason);
    }

    // ----- the required cases -------------------------------------------------------------------

    private static async Task RequestSentNoReceiptAsync()
    {
        var fixture = await Fixture.CreateAsync("a-request-sent-no-receipt", identityAtDestination: false);
        await fixture.ReleaseStartLeaseAsync();
        var before = await RetainedStateAsync(fixture);
        var result = await fixture.ReconcileAsync();
        RequireUnresolved(result, SyncAuthorityOperationStatus.EffectIssuing, "A");
        Require(fixture.Remote.ListEntriesCount == 0,
            "A: the request-sent boundary consulted no remote listing (a listing is not evidence about a sent request)");
        Require(fixture.Remote.MoveCount == 0, "A: the request-sent boundary issued no provider move");
        var after = await RetainedStateAsync(fixture);
        Require(after == before,
            "A: no receipt was synthesized and the attempt, receipt and operation authority are unchanged: " +
            before + " -> " + after);
        var rejection = await fixture.TryExecuteAsync("worker-retry-a");
        Require(rejection is not null,
            "A: the unresolved operation refuses the ordinary executor instead of issuing a retry");
        Require(fixture.Remote.MoveCount == 0, "A: the refused retry issued zero move calls");
        Require(await RetainedStateAsync(fixture) == before,
            "A: the refused retry changed no retained authority");
    }

    private static async Task DelayedOriginalCompletionAsync()
    {
        var fixture = await Fixture.CreateAsync("b-delayed-original-completion", identityAtDestination: false);
        await fixture.ReleaseStartLeaseAsync();
        var before = await RetainedStateAsync(fixture);
        var first = await fixture.ReconcileAsync();
        RequireUnresolved(first, SyncAuthorityOperationStatus.EffectIssuing, "B first reconciliation");
        Require(fixture.Remote.ListEntriesCount == 0,
            "B: the first reconciliation observed no remote listing for the sent request");
        // The provider applies the original request after the crash: the identity now exists exactly once at
        // the desired path. A retry unlocked by the earlier 'source still present, destination free' reading
        // would produce a second effect here.
        fixture.Remote.MoveDirectory(PriorPath, DesiredPath);
        var second = await fixture.ReconcileAsync();
        RequireUnresolved(second, SyncAuthorityOperationStatus.EffectIssuing, "B second reconciliation");
        Require(fixture.Remote.ListEntriesCount == 0,
            "B: the second reconciliation also derived nothing from the current remote path");
        Require(fixture.Remote.MoveCount == 0, "B: no provider move was issued by either reconciliation");
        Require(await RetainedStateAsync(fixture) == before,
            "B: the delayed completion changed no retained authority, so no duplicate effect was recorded");
        Require(fixture.Remote.DirectoryExists(DesiredPath) && !fixture.Remote.DirectoryExists(PriorPath),
            "B: the delayed provider completion left exactly one identity at the desired path");
    }

    private static async Task ChangedLocalSourceAsync()
    {
        var fixture = await Fixture.CreateAsync("c-changed-local-source", identityAtDestination: false);
        await fixture.ReleaseStartLeaseAsync();
        var before = await RetainedStateAsync(fixture);
        var first = await fixture.ReconcileAsync();
        RequireUnresolved(first, SyncAuthorityOperationStatus.EffectIssuing, "C first reconciliation");
        // The current local source changes identity while the request-sent proof is retained: the retained
        // authority, not a fresh scan, decides the outcome.
        Directory.Delete(fixture.DesiredLocalPath, recursive: true);
        Directory.CreateDirectory(fixture.DesiredLocalPath);
        var second = await fixture.ReconcileAsync();
        RequireUnresolved(second, SyncAuthorityOperationStatus.EffectIssuing, "C second reconciliation");
        Require(await RetainedStateAsync(fixture) == before,
            "C: the changed local source overwrote nothing and left the retained proof unchanged");
        Require(fixture.Remote.MoveCount == 0,
            "C: the changed local source caused no provider move and no speculative reissue");
    }

    private static async Task KnownSuccessReadbackAsync()
    {
        var fixture = await Fixture.CreateAsync("d-known-success-readback", identityAtDestination: true);
        await fixture.RecordReceiptAsync(SyncAuthorityReceiptCertainty.KnownSuccess, "{}");
        await fixture.ReleaseStartLeaseAsync();
        var result = await fixture.ReconcileAsync();
        Require(result.Status == SyncAuthorityOperationStatus.Completed,
            "D: the durable known-success receipt plus the exact readback completes the move, saw " + result.Status);
        Require(!result.EffectDispatched,
            "D: the readback commit issued no provider effect and no new request");
        Require(fixture.Remote.MoveCount == 0, "D: the readback commit issued no provider move");
        Require(await fixture.OperationStatusAsync() == "completed", "D: the retained operation is completed");
        Require(await fixture.AttemptStatusesAsync() == "verified_success",
            "D: the retained attempt is terminally verified_success");
        Require(await fixture.ReceiptCountAsync() == 1,
            "D: the retained known-success receipt stayed the single durable receipt");
        Require(await fixture.SyncPointCountAsync() == 1,
            "D: exactly one synchronization point was committed");
        Require(await fixture.MembershipCountAsync() == 1,
            "D: exactly one successor membership was registered");
        var committed = await RetainedStateAsync(fixture);
        var replay = await fixture.TryExecuteAsync("worker-replay-d");
        Require(replay is not null, "D: a completed move refuses further reconciliation");
        Require(await RetainedStateAsync(fixture) == committed && fixture.Remote.MoveCount == 0,
            "D: the completed move was never replayed or recommitted");
    }

    private static async Task ProviderRejectionRetryAsync()
    {
        var fixture = await Fixture.CreateAsync("e-provider-rejection-retry", identityAtDestination: false);
        await fixture.RecordReceiptAsync(SyncAuthorityReceiptCertainty.KnownNotApplied,
            "{\"classification\":\"provider_rejected\",\"statusCode\":409}");
        await fixture.ReleaseStartLeaseAsync();
        var result = await fixture.ReconcileAsync();
        Require(result.Status == SyncAuthorityOperationStatus.Ready,
            "E: the provider's own retained non-application receipt opens the permitted retry, saw " + result.Status);
        Require(!result.EffectDispatched, "E: settling the receipt issued no effect at reconciliation");
        Require(await fixture.AttemptStatusesAsync() == "verified_not_applied",
            "E: the retained attempt is terminally verified_not_applied");
        Require(await fixture.ReceiptCountAsync() == 1,
            "E: no second receipt was appended to the provider's rejection evidence");
        Require(fixture.Remote.MoveCount == 0, "E: reconciliation issued no provider move");
        var retried = await new SyncAuthorityMoveRemoteExecutor(fixture.Pair, fixture.Remote, fixture.Authority)
            .ExecuteAsync(fixture.OperationId, "worker-retry-e");
        Require(retried.Status == SyncAuthorityOperationStatus.Completed,
            "E: the one permitted retry completes the move, saw " + retried.Status);
        Require(retried.EffectDispatched && fixture.Remote.MoveCount == 1,
            "E: the permitted retry issued exactly one provider move");
        Require(!fixture.Remote.DirectoryExists(PriorPath) && fixture.Remote.DirectoryExists(DesiredPath),
            "E: the permitted retry moved the exact identity to the desired path");
        Require(await fixture.OperationStatusAsync() == "completed",
            "E: the permitted retry completed the operation");
    }

    private static async Task AmbiguousReceiptUnresolvedAsync()
    {
        var fixture = await Fixture.CreateAsync("f-ambiguous-receipt", identityAtDestination: false);
        await fixture.RecordReceiptAsync(SyncAuthorityReceiptCertainty.Unknown,
            "{\"classification\":\"native move response unknown\"}");
        await fixture.ReleaseStartLeaseAsync();
        var before = await RetainedStateAsync(fixture);
        var result = await fixture.ReconcileAsync();
        RequireUnresolved(result, SyncAuthorityOperationStatus.EffectUnknown, "F");
        Require(fixture.Remote.ListEntriesCount == 1,
            "F: the ambiguous case did consult the current remote listing");
        Require(fixture.Remote.MoveCount == 0, "F: the ambiguous case issued no provider move");
        Require(await RetainedStateAsync(fixture) == before,
            "F: the ambiguous receipt, attempt and operation state stay exactly unresolved");
        Require(await fixture.SyncPointCountAsync() == 0 && await fixture.MembershipCountAsync() == 0,
            "F: no outcome was committed from the current path presence");
        var rejection = await fixture.TryExecuteAsync("worker-retry-f");
        Require(rejection is not null, "F: the unresolved operation refuses the ordinary executor");
        Require(fixture.Remote.MoveCount == 0,
            "F: no fenced retry was unlocked by the free destination path");
        Require(await RetainedStateAsync(fixture) == before,
            "F: the refused retry changed no retained authority");
    }

    private static async Task DestinationAfterAmbiguityHeldAsync()
    {
        var fixture = await Fixture.CreateAsync("g-verified-applied", identityAtDestination: false);
        await fixture.RecordReceiptAsync(SyncAuthorityReceiptCertainty.Unknown,
            "{\"classification\":\"native move response unknown\"}");
        await fixture.ReleaseStartLeaseAsync();
        var first = await fixture.ReconcileAsync();
        RequireUnresolved(first, SyncAuthorityOperationStatus.EffectUnknown, "G first reconciliation");
        Require(await fixture.SyncPointCountAsync() == 0,
            "G: the first reconciliation committed nothing from the free destination path");
        // The delayed original completion makes the applied identity visible at the desired path.
        fixture.Remote.MoveDirectory(PriorPath, DesiredPath);
        var second = await fixture.ReconcileAsync();
        Require(second.Status == SyncAuthorityOperationStatus.Blocked,
            "G: destination identity alone cannot promote an ambiguous request to known success");
        Require(!second.EffectDispatched && fixture.Remote.MoveCount == 0,
            "G: the held destination observation issued no provider effect or move");
        Require(await fixture.SyncPointCountAsync() == 0 && await fixture.MembershipCountAsync() == 0,
            "G: ambiguous receipt plus current destination creates no accepted baseline or membership");
        Require(await fixture.ReceiptCountAsync() == 1,
            "G: the original ambiguous receipt stays the only durable receipt");
        Require(await fixture.AttemptStatusesAsync() == "blocked",
            "G: the contradiction is held without claiming verified_success");
        var held = await RetainedStateAsync(fixture);
        var third = await fixture.TryExecuteAsync("worker-replay-g");
        Require(third is not null, "G: the held ambiguous move refuses ordinary reissue");
        Require(await RetainedStateAsync(fixture) == held && fixture.Remote.MoveCount == 0,
            "G: a possible delayed completion is never duplicated by a new commit or move");
    }

    // ----- retained authority reads -------------------------------------------------------------

    private static async Task<string> RetainedStateAsync(Fixture fixture)
    {
        var fields = new[]
        {
            await fixture.OperationStatusAsync(),
            await fixture.AttemptStatusesAsync(),
            Convert.ToString(await fixture.ReceiptCountAsync(), CultureInfo.InvariantCulture) ?? "",
            Convert.ToString(await fixture.SyncPointCountAsync(), CultureInfo.InvariantCulture) ?? "",
            Convert.ToString(await fixture.MembershipCountAsync(), CultureInfo.InvariantCulture) ?? "",
            Convert.ToString(await fixture.VersionCountAsync(), CultureInfo.InvariantCulture) ?? "",
            await fixture.LocalVersionIdentityAsync()
        };
        return string.Join("|", fields);
    }

    private static async Task<long> ScalarLongAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath + ";Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static string Hash(string role, params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts)))).ToLowerInvariant();

    // ----- the retained-authority fixture -------------------------------------------------------

    private sealed class Fixture
    {
        internal string Slug = "";
        internal string CaseRoot = "";
        internal string LocalRoot = "";
        internal string DesiredLocalPath = "";
        internal string DatabasePath = "";
        internal string PairId = "";
        internal string ObjectId = "";
        internal string LocalRootBinding = "";
        internal string DropboxRootBinding = "";
        internal string LocalVersionId = "";
        internal string DropboxVersionId = "";
        internal string OperationId = "";
        internal string AttemptId = "";
        internal string WorkerId = "";
        internal SyncAuthorityDatabase Authority = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal FakeSyncRemoteStore Remote = null!;
        internal SyncPair Pair = null!;
        internal SyncAuthorityLease StartLease = null!;

        internal static async Task<Fixture> CreateAsync(string slug, bool identityAtDestination)
        {
            var fixture = new Fixture { Slug = slug };
            fixture.CaseRoot = Path.Combine(_root, slug);
            Directory.CreateDirectory(fixture.CaseRoot);
            fixture.LocalRoot = Path.Combine(fixture.CaseRoot, "pair-root");
            Directory.CreateDirectory(fixture.LocalRoot);
            fixture.DesiredLocalPath = Path.Combine(fixture.LocalRoot, DesiredPath);
            Directory.CreateDirectory(fixture.DesiredLocalPath);
            PilotDirectoryIdentity rootIdentity;
            using (var rootLease = PilotDirectoryLease.Open(fixture.LocalRoot))
                rootIdentity = rootLease.Identity;
            PilotDirectoryIdentity desiredIdentity;
            using (var desiredLease = PilotDirectoryLease.Open(fixture.DesiredLocalPath))
                desiredIdentity = desiredLease.Identity;
            var localIdentityJson = JsonSerializer.Serialize(new
            {
                volumeSerial = desiredIdentity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture),
                fileId = desiredIdentity.FileId.ToString("x16", CultureInfo.InvariantCulture)
            });
            fixture.PairId = "pair-move-remote-" + slug;
            fixture.ObjectId = "object-move-remote-" + slug;
            fixture.LocalRootBinding = "root-local-" + slug;
            fixture.DropboxRootBinding = "root-dropbox-" + slug;
            fixture.LocalVersionId = "version-local-" + slug;
            fixture.DropboxVersionId = "version-dropbox-" + slug;
            fixture.OperationId = "operation-move-remote-" + slug;
            fixture.WorkerId = "worker-fixture-" + slug;
            fixture.DatabasePath = Path.Combine(fixture.CaseRoot, "authority.db");
            fixture.Authority = new SyncAuthorityDatabase(fixture.DatabasePath);
            await fixture.Authority.InitializeAsync(InstallationId, CapabilityProfileId, slug,
                CapabilityProfileJson);
            // The pair carries the real NTFS root identity, so the reconciler's own root lease can only open
            // against this exact physical directory.
            fixture.Pair = new SyncPair(fixture.PairId, "Move remote uncertainty " + slug, fixture.LocalRoot,
                rootIdentity.VolumeSerialNumber, rootIdentity.FileId, "/Move", "id:folder-move-root");
            await fixture.Authority.RegisterPairAsync(fixture.PairId, 1);
            fixture.Admission = await fixture.Authority.CreateAcceptedCommandAdmissionAsync(null, "run_pair", fixture.PairId,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await fixture.Authority.TransitionCommandRequestAsync(fixture.Admission.CommandRequestId, "executing");
            await fixture.Authority.RegisterRootBindingAsync(fixture.LocalRootBinding, fixture.PairId, "local",
                1, JsonSerializer.Serialize(new { volumeSerial = rootIdentity.VolumeSerialNumber,
                    fileId = rootIdentity.FileId }), fixture.LocalRoot);
            await fixture.Authority.RegisterRootBindingAsync(fixture.DropboxRootBinding, fixture.PairId,
                "dropbox", 1, "{\"folderId\":\"id:folder-move-root\"}", fixture.Pair.DropboxRoot);
            fixture.Remote = new FakeSyncRemoteStore();
            if (identityAtDestination) fixture.Remote.PutDirectoryWithId(DesiredPath, PredecessorId);
            else fixture.Remote.PutDirectoryWithId(PriorPath, PredecessorId);
            await fixture.Authority.RegisterObjectAsync(fixture.ObjectId, "directory");
            await fixture.Authority.RegisterObjectVersionAsync(fixture.LocalVersionId, fixture.ObjectId, "local",
                localIdentityJson, null, 0);
            await fixture.Authority.RegisterObjectVersionAsync(fixture.DropboxVersionId, fixture.ObjectId,
                "dropbox", "{\"fileId\":\"" + PredecessorId + "\"}", null, 0);
            var preconditions = JsonSerializer.Serialize(new
            {
                observationSetId = "observation-set-" + slug,
                pairId = fixture.PairId,
                kind = "move_remote",
                sourcePath = DesiredPath,
                priorPath = PriorPath,
                configurationRevision = 1L
            });
            var members = new[]
            {
                new SyncAuthorityMember("member-source-" + slug, fixture.PairId, fixture.ObjectId, "source",
                    fixture.LocalRootBinding, null, fixture.LocalVersionId, null, DesiredPath,
                    "{\"present\":true}"),
                new SyncAuthorityMember("member-destination-" + slug, fixture.PairId, fixture.ObjectId,
                    "destination", fixture.DropboxRootBinding, null, fixture.DropboxVersionId, null,
                    PriorPath, "{\"present\":true}"),
                new SyncAuthorityMember("member-lock-" + slug, fixture.PairId, fixture.ObjectId, "lock",
                    fixture.DropboxRootBinding, null, null, null, DesiredPath, "{\"present\":false}")
            };
            await fixture.Authority.CreateOperationAsync(new SyncAuthorityOperationPlan(fixture.OperationId,
                "idempotency-" + fixture.OperationId, SyncAuthorityOperationKind.MoveRemote, fixture.PairId,
                fixture.ObjectId, 1, CapabilityProfileId, preconditions, 80, members, [], []), admission: fixture.Admission);
            // The exact crash boundary: the fenced request is durably recorded as sent and the worker then
            // disappears before any receipt exists. No fixture simulates a receipt the production writer
            // never recorded.
            fixture.StartLease = await fixture.Authority.AcquireOperationLeaseAsync(fixture.OperationId,
                fixture.WorkerId, TimeSpan.FromMinutes(10));
            var attempt = await fixture.Authority.StartDispatchedAttemptAsync(fixture.OperationId,
                fixture.StartLease, Hash("move-remote-uncertainty", slug), EffectKind);
            fixture.AttemptId = attempt.AttemptId;
            // relinquishment is per-case for A/B/C; D-G release explicitly after recording their receipt.
            return fixture;
        }

        internal async Task RecordReceiptAsync(SyncAuthorityReceiptCertainty certainty, string metadataJson)
        {
            await Authority.RecordReceiptAsync(AttemptId, StartLease, certainty,
                "{\"fileId\":\"" + PredecessorId + "\"}", "{}", metadataJson,
                Hash("receipt", Slug, certainty.ToString()));
        }

        private bool _startLeaseReleased;

        internal async Task ReleaseStartLeaseAsync()
        {
            if (_startLeaseReleased) return;
            _startLeaseReleased = true;
            await Authority.ReleaseLeaseAsync(StartLease);
        }

        internal async Task<SyncAuthorityExecutionResult> ReconcileAsync() =>
            await new SyncAuthorityMoveRemoteReconciler(Pair, Remote, Authority)
                .ReconcileAsync(OperationId, "worker-reconcile-" + Slug);

        internal async Task<string?> TryExecuteAsync(string workerId)
        {
            try
            {
                await new SyncAuthorityMoveRemoteExecutor(Pair, Remote, Authority)
                    .ExecuteAsync(OperationId, workerId);
                return null;
            }
            catch (Exception exception)
            {
                return exception.GetType().Name + ": " + exception.Message;
            }
        }

        internal Task<string> OperationStatusAsync() => ScalarStringAsync(DatabasePath,
            "SELECT status FROM operation WHERE operation_id='" + OperationId + "';");

        internal Task<string> AttemptStatusesAsync() => ScalarStringAsync(DatabasePath,
            "SELECT group_concat(status,',') FROM operation_attempt WHERE operation_id='" + OperationId + "';");

        internal Task<long> ReceiptCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id" +
            " WHERE a.operation_id='" + OperationId + "';");

        internal Task<long> SyncPointCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM synchronization_point WHERE pair_id='" + PairId + "';");

        internal Task<long> MembershipCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM membership WHERE object_id='" + ObjectId + "';");

        internal Task<long> VersionCountAsync() => ScalarLongAsync(DatabasePath,
            "SELECT count(*) FROM object_version WHERE object_id='" + ObjectId + "';");

        internal Task<string> LocalVersionIdentityAsync() => ScalarStringAsync(DatabasePath,
            "SELECT identity_json FROM object_version WHERE object_version_id='" + LocalVersionId + "';");
    }
}

