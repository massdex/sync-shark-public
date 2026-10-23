using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Only isolated allocation evidence is fixture-supplied through the production writer. The collision,
// release transaction, overlap refusal and replay all use production entry points.
[SupportedOSPlatform("windows")]
internal static class PairAuthoringRecoveryChecks
{
    private const string Installation = "installation-authoring-recovery";
    private const string Profile = "profile-authoring-recovery";
    private static int _assertions;

    internal static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use an empty isolated authoring-recovery fixture root.");
        Directory.CreateDirectory(root);
        _assertions = 0;
        var cases = new List<string>();
        await PositiveAsync(Path.Combine(root, "positive"));
        cases.Add("real orphan-control collision, release, parent registration and exact replay");
        await RetryAsync(Path.Combine(root, "retry"));
        cases.Add("same-installation unpublished retry uses fresh authority without adopting old control");
        await RejectAsync(root, "published", async f =>
            await f.Db.RetainPairDefinitionAsync(new SyncPair(f.Pair, f.Pair, f.Local,
                f.Root.Identity.VolumeSerialNumber, f.Root.Identity.FileId,
                "/Authoring/Child", "id:authoring-child", Enabled: false), false));
        cases.Add("published pair stays held");
        await RejectAsync(root, "issued-create", f => RetainAdditionalControlAsync(f,
            effectKind: "local-control-directory-create"));
        cases.Add("issued-create stays held");
        await RejectAsync(root, "bound-identity", f => RetainAdditionalControlAsync(f, bound: true));
        cases.Add("bound-identity stays held");
        await RejectAsync(root, "unknown-receipt", f => RetainAdditionalControlAsync(f, unknown: true));
        cases.Add("unknown-receipt stays held");
        await RejectAsync(root, "unsettled-attempt", f => RetainAdditionalControlAsync(f, pending: true));
        cases.Add("unsettled-attempt stays held");
        await RejectAsync(root, "live-lease", f => RetainAdditionalControlAsync(f, live: true));
        cases.Add("live-lease stays held");
        await RejectAsync(root, "other-operation", async f =>
        {
            await f.Db.RegisterObjectAsync("object-other", "directory");
            await f.Db.CreateOperationAsync(new SyncAuthorityOperationPlan("operation-other",
                "idempotency-other", SyncAuthorityOperationKind.AllocateControl, f.Pair,
                "object-other", 1, Profile, "{}", 1,
                [new SyncAuthorityMember("member-other", f.Pair, "object-other", "lock",
                    "root-child-local", null, null, null, null, "{}")], [], []), admission: f.Admission);
        });
        cases.Add("additional unsettled work stays held");
        await File.WriteAllTextAsync(Path.Combine(root, "pair-authoring-recovery-checks.json"),
            JsonSerializer.Serialize(new { assertions = _assertions, cases,
                entryPoint = "SyncAuthorityDatabase.ReleaseFailedUnpublishedPairRootsAsync",
                productionDataTouched = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"PASS pair authoring recovery: {_assertions} assertions, {cases.Count} cases.");
    }

    private static async Task PositiveAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root);
        Assert((await f.Db.LoadPairSetAsync()).Pairs.Count == 0, "failed child is unpublished");
        var before = await ControlSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        await f.Db.RegisterPairAsync("pair-parent", 1);
        await RefuseAsync(() => f.Db.RegisterRootBindingAsync("root-parent-local", "pair-parent",
            "local", 1, "{}", Path.GetDirectoryName(f.Local)!), "overlaps another active pair");
        Assert(await f.Db.ReleaseFailedUnpublishedPairRootsAsync() == 2,
            "exact non-creating failed candidate releases only its two data reservations");
        Assert(before == await ControlSnapshotAsync(f.Db.DatabasePath),
            "control operation, allocation, attempts, receipts and leases are byte-for-byte unchanged");
        Assert(markerHash == f.MarkerHash(), "physical owner marker is unchanged");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM root_binding WHERE state='blocked' AND side IN ('local','dropbox');") == "2",
            "candidate identity remains retained with blocked data roots");
        await f.Db.RegisterRootBindingAsync("root-parent-local", "pair-parent", "local", 1,
            "{}", Path.GetDirectoryName(f.Local)!);
        await f.Db.RegisterRootBindingAsync("root-parent-cloud", "pair-parent", "dropbox", 1,
            "{}", "/Authoring");
        Assert(await ScalarAsync(f.Db.DatabasePath,
            "SELECT count(*) FROM root_binding WHERE pair_id='pair-parent' AND state='active';") == "2",
            "the normal parent guard no longer sees invisible failed-save reservations");
        var retained = await AllSnapshotAsync(f.Db.DatabasePath);
        Assert(await f.Db.ReleaseFailedUnpublishedPairRootsAsync() == 0, "replay releases nothing");
        Assert(retained == await AllSnapshotAsync(f.Db.DatabasePath), "replay adds no duplicate history or effect");
        await RefuseAsync(async () => await new SyncAuthorityLocalControlNamespaceAllocator(f.Db)
            .BindAsync(f.Root, Installation, f.Pair, "root-child-local", 1, Profile), "not runnable");
        Assert(before == await ControlSnapshotAsync(f.Db.DatabasePath),
            "control collision still refuses without adoption or retry");
    }

    private static async Task RetryAsync(string root)
    {
        using var f = await Fixture.CreateAsync(root);
        await f.Db.ReleaseFailedUnpublishedPairRootsAsync();
        var pair = new SyncPair(f.Pair, f.Pair, f.Local, f.Root.Identity.VolumeSerialNumber,
            f.Root.Identity.FileId, "/Authoring/Child", "id:authoring-child", Enabled: false);
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        Assert((await f.Db.RetryFailedUnpublishedAccountPairAsync(pair, "foreign-installation",
            "account-authoring", "namespace-authoring")).Id == pair.Id, "foreign installation cannot retry");
        Assert((await f.Db.RetryFailedUnpublishedAccountPairAsync(pair with { DropboxFolderId = "id:other" },
            Installation, "account-authoring", "namespace-authoring")).Id == pair.Id, "changed root cannot retry");
        var retry = await f.Db.RetryFailedUnpublishedAccountPairAsync(pair, Installation,
            "account-authoring", "namespace-authoring");
        Assert(retry.Id != pair.Id, "exact orphan gets a fresh candidate and namespace");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath), "old failure evidence is unchanged");
        // Move the old fixture control aside, just as the operator did. Never adopt its marker.
        Directory.Move(Path.GetDirectoryName(f.Marker)!, Path.Combine(root, "old-control"));
        await Fixture.InitializeAsync(f.Db, f, retry.Id, "root-retry-local", "root-retry-cloud");
        var binding = await new SyncAuthorityLocalControlNamespaceAllocator(f.Db).BindAsync(f.Root,
            Installation, retry.Id, "root-retry-local", 1, Profile);
        retry = retry with { ControlNamespace = new SyncControlNamespaceBinding(Installation, retry.Id, binding) };
        await f.Db.RetainPairDefinitionAsync(retry, false);
        Assert((await f.Db.LoadPairSetAsync()).Pairs.Single().Id == retry.Id, "retry publishes normally");
        Assert((await f.Db.RetryFailedUnpublishedAccountPairAsync(retry, Installation,
            "account-authoring", "namespace-authoring")).Id == retry.Id, "published pair is not replaced");
    }

    private static async Task RejectAsync(string root, string name, Func<Fixture, Task> configure)
    {
        using var f = await Fixture.CreateAsync(Path.Combine(root, name));
        await configure(f);
        var before = await AllSnapshotAsync(f.Db.DatabasePath);
        var markerHash = f.MarkerHash();
        Assert(await f.Db.ReleaseFailedUnpublishedPairRootsAsync() == 0, name + " refused");
        var pair = new SyncPair(f.Pair, f.Pair, f.Local, f.Root.Identity.VolumeSerialNumber,
            f.Root.Identity.FileId, "/Authoring/Child", "id:authoring-child", Enabled: false);
        Assert((await f.Db.RetryFailedUnpublishedAccountPairAsync(pair, Installation,
            "account-authoring", "namespace-authoring")).Id == pair.Id, name + " cannot mint retry authority");
        Assert(before == await AllSnapshotAsync(f.Db.DatabasePath), name + " changes no retained row");
        Assert(markerHash == f.MarkerHash(), name + " changes no physical control");
    }

    private sealed class Fixture : IDisposable
    {
        internal SyncAuthorityDatabase Db = null!;
        internal SyncAuthorityCommandAdmission Admission = null!;
        internal PilotDirectoryLease Root = null!;
        internal string Pair = "pair-child-authoring-recovery", Local = "", Marker = "";

        internal static async Task<Fixture> CreateAsync(string root)
        {
            var f = new Fixture { Local = Path.Combine(root, "parent", "child") };
            Directory.CreateDirectory(f.Local);
            f.Root = PilotDirectoryLease.Open(f.Local);
            // A genuine previously owned control survives a state reset. The fresh
            // database may not adopt its marker without the original creation proof.
            var prior = new SyncAuthorityDatabase(Path.Combine(root, "prior.db"));
            await InitializeAsync(prior, f);
            var binding = await new SyncAuthorityLocalControlNamespaceAllocator(prior).BindAsync(
                f.Root, Installation, f.Pair, "root-child-local", 1, Profile);
            f.Marker = Path.Combine(binding.RootPath, SyncControlNamespace.MarkerName);
            f.Db = new SyncAuthorityDatabase(Path.Combine(root, "current.db"));
            await InitializeAsync(f.Db, f);
            await RefuseAsync(async () => await new SyncAuthorityLocalControlNamespaceAllocator(f.Db)
                .BindAsync(f.Root, Installation, f.Pair, "root-child-local", 1, Profile),
                "cannot be adopted without exact retained creation authority");
            Assert(await ScalarAsync(f.Db.DatabasePath,
                "SELECT count(*) FROM operation_attempt WHERE status='blocked' AND effect_kind='local-control-directory-precondition';") == "1",
                "real allocator recorded a precondition-only refusal");
            f.Admission = await f.Db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", f.Pair,
                null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
            await f.Db.TransitionCommandRequestAsync(f.Admission.CommandRequestId, "executing");
            return f;
        }

        internal static async Task InitializeAsync(SyncAuthorityDatabase db, Fixture f,
            string? pairId = null, string localId = "root-child-local", string cloudId = "root-child-cloud")
        {
            pairId ??= f.Pair;
            await db.InitializeAsync(Installation, Profile, "authoring-recovery", "{}");
            await db.RegisterPairAsync(pairId, 1);
            var serial = f.Root.Identity.VolumeSerialNumber.ToString("x8", CultureInfo.InvariantCulture);
            var volume = await db.ResolveVolumeIdAsync(Installation, serial, "NTFS", null, false, false, true);
            await db.RegisterLocalRootBindingAsync(localId, pairId, 1, Installation, volume, serial,
                f.Root.Identity.FileId.ToString("x16", CultureInfo.InvariantCulture), f.Local);
            await db.RegisterProviderAccountAsync("account-authoring", Installation, "account",
                "namespace-authoring", "namespace-authoring", "fixture", 1);
            await db.RegisterDropboxRootBindingAsync(cloudId, pairId, 1, Installation,
                "account-authoring", "namespace-authoring", "id:authoring-child", "/Authoring/Child");
        }
        internal string MarkerHash() => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Marker)));
        public void Dispose() => Root.Dispose();
    }

    private static async Task RefuseAsync(Func<Task> action, string reason)
    {
        try { await action(); }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Assert(e.Message.Contains(reason, StringComparison.Ordinal), "exact expected gate: " + reason);
            return;
        }
        throw new InvalidDataException("Expected refusal: " + reason);
    }

    private static void Assert(bool value, string label)
    {
        _assertions++;
        if (!value) throw new InvalidDataException("Authoring recovery: " + label);
    }
    private static async Task<string?> ScalarAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
    private static async Task RetainAdditionalControlAsync(Fixture f,
        string effectKind = "local-control-directory-precondition", bool bound = false,
        bool unknown = false, bool pending = false, bool live = false)
    {
        const string operation = "operation-extra-control", allocation = "allocation-extra-control";
        await f.Db.RegisterObjectAsync("object-extra-control", "directory");
        await f.Db.CreateOperationAsync(new SyncAuthorityOperationPlan(operation,
            "idempotency-extra-control", SyncAuthorityOperationKind.AllocateControl, f.Pair,
            "object-extra-control", 1, Profile, "{}", 1,
            [new SyncAuthorityMember("member-extra-control", f.Pair, "object-extra-control",
                "lock", "root-child-local", null, null, null, null, "{}")], [], []), admission: f.Admission);
        var lease = await f.Db.AcquireOperationLeaseAsync(operation, "worker-extra-control", TimeSpan.FromMinutes(1));
        await f.Db.AuthorizeNamespaceAllocationAsync(new SyncAuthorityNamespaceAllocation(allocation,
            operation, "local", "fixture-extra-parent", "fixture-extra-control", new string('a', 64)), lease);
        await f.Db.TransitionNamespaceAllocationAsync(allocation, lease, SyncAuthorityAllocationStatus.Creating);
        if (bound)
            await f.Db.TransitionNamespaceAllocationAsync(allocation, lease,
                SyncAuthorityAllocationStatus.Created, JsonSerializer.Serialize(new
                { volumeSerial = f.Root.Identity.VolumeSerialNumber.ToString("x8"),
                    fileId = f.Root.Identity.FileId.ToString("x16") }));
        var attempt = await f.Db.StartDispatchedAttemptAsync(operation, lease, new string('b', 64), effectKind);
        if (!pending)
        {
            if (unknown)
                await f.Db.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.Unknown,
                    "{}", "{}", "{}", new string('c', 64));
            await f.Db.TransitionNamespaceAllocationAsync(allocation, lease, SyncAuthorityAllocationStatus.Blocked);
            await f.Db.RecordReceiptAsync(attempt.AttemptId, lease, SyncAuthorityReceiptCertainty.KnownConflict,
                "{}", "{}", "{}", new string('d', 64));
            await f.Db.ReconcileAttemptAsync(attempt.AttemptId, lease, SyncAuthorityAttemptStatus.Blocked,
                "{\"reason\":\"fixture retained refusal\"}");
        }
        if (!live) await f.Db.ReleaseLeaseAsync(lease);
    }
    private static Task<string> ControlSnapshotAsync(string path) => SnapshotAsync(path,
        ["operation", "operation_member", "namespace_allocation", "operation_attempt", "effect_receipt", "lease", "control_namespace", "pair_control_namespace"]);
    private static Task<string> AllSnapshotAsync(string path) => SnapshotAsync(path,
        ["sync_pair", "root_binding", "operation", "operation_member", "namespace_allocation",
            "operation_attempt", "effect_receipt", "lease", "control_namespace", "pair_control_namespace", "history_event"]);
    private static async Task<string> SnapshotAsync(string path, string[] tables)
    {
        var output = new List<object>();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM " + table + " ORDER BY 1;";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string?[]>();
            while (await reader.ReadAsync())
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null :
                    Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)).ToArray());
            output.Add(new { table, rows });
        }
        return JsonSerializer.Serialize(output);
    }
}
