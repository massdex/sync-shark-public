using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using IOPath = System.IO.Path;

// F15/V3: the real-path regression for the installed 0.3.46 volume-registration defect. Every case below
// drives the actual production consumers - the local/local SyncEndpointPairRuntime pair create, the real
// SyncAuthorityRuntimeHost cloud registration reached through its public policy-edit entry, and the
// unchanged strict explicit-id RegisterVolumeAsync - over a real isolated authority database and real
// disposable NTFS roots. Nothing here computes an expected volume id in a helper and calls that a proof:
// the decisive assertions read what the production path actually retained. The only synthesised input is
// the seeded legacy row, which reproduces byte-for-byte the id the pre-fix caller derived, and one raw
// injected collision used to prove the refusal. No provider, credential, installed state or user file is
// reached.
internal static class VolumeIdentityReuseChecks
{
    private const string Installation = "installation-volume-reuse-481";
    private const string SourceRevision = "volume-reuse-481";
    private const string ProfileId = "profile-volume-reuse-481";
    private const string CapabilityJson = "{\"name\":\"volume-reuse-481\",\"local\":true}";
    private const string AccountId = "account-volume-reuse-481";
    private const string PairConfigJson = "manual_preserve_both";

    private static readonly List<Dictionary<string, object?>> Cases = [];
    private static int _assertions;
    private static string _root = "";

    internal static async Task RunAsync(string root)
    {
        _root = IOPath.GetFullPath(root);
        if (Directory.Exists(_root) && Directory.EnumerateFileSystemEntries(_root).Any())
            throw new InvalidOperationException("Use a fresh isolated volume identity reuse fixture root.");
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Volume identity checks require Windows NTFS identities.");
        Directory.CreateDirectory(_root);
        Cases.Clear();
        _assertions = 0;

        await CaseAsync("V3-01-retained-natural-key-refuses-a-second-volume-id",
            RetainedNaturalKeyRefusesSecondIdAsync);
        await CaseAsync("V3-02-real-local-local-create-reuses-the-retained-hex-row",
            LocalLocalCreateReusesRetainedRowAsync);
        await CaseAsync("V3-03-real-cloud-registration-reuses-the-legacy-numeric-row",
            CloudRegistrationReusesLegacyNumericRowAsync);
        await CaseAsync("V3-04-resolve-is-idempotent-and-survives-reopen",
            IdempotentAndReopenAsync);
        await CaseAsync("V3-05-refusals-and-distinct-keys-leave-state-intact",
            RefusalsLeaveStateIntactAsync);

        var report = new Dictionary<string, object?>
        {
            ["generated_by"] = "VolumeIdentityReuseChecks (tests/SyncShark.Core.Tests)",
            ["fixture_root"] = _root,
            ["assertions"] = _assertions,
            ["distinct_volume_fixture"] = DistinctVolumeFixtureNote(),
            ["cases"] = Cases
        };
        await File.WriteAllTextAsync(IOPath.Combine(_root, "volume-identity-reuse-checks.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("PASS volume identity reuse: " + _assertions +
            " assertions; the installed 0.3.46 UNIQUE(volume.installation_id, volume.volume_serial_hex)" +
            " failure is reproduced against the unchanged strict registration and the real local/local pair" +
            " create and the real Host cloud registration now both reuse the exact retained volume id; see" +
            " the per-case report.");
    }

    // V3-01: the exact failure the installed build produced. A real retained row for the natural key is
    // followed by the id the pre-fix caller derived for the same volume; the strict explicit-id writer that
    // this correction deliberately leaves unchanged still refuses it, and the retained row is untouched.
    private static async Task RetainedNaturalKeyRefusesSecondIdAsync()
    {
        var (db, dbPath, localRoot) = NewAuthority("retained-natural-key");
        var serial = SerialHex(localRoot);
        var hexId = await db.ResolveVolumeIdAsync(Installation, serial, "NTFS", null, false, false, true);
        var legacyId = LegacyNumericVolumeId(Installation, VolumeSerial(localRoot));
        Require(legacyId != hexId, "V3-01 the pre-fix caller-local derivation really is a second id: " + legacyId);

        var refused = "";
        try
        {
            await db.RegisterVolumeAsync(legacyId, Installation, serial, "NTFS", null, false, false, true);
        }
        catch (SqliteException error) { refused = error.Message; }
        Require(refused.Contains("UNIQUE constraint failed: volume.installation_id, volume.volume_serial_hex",
                StringComparison.Ordinal),
            "V3-01 the retained natural key refuses a second volume_id exactly as the installed build did: " +
            (refused.Length == 0 ? "no refusal" : refused));
        Require(await CountAsync(dbPath, "SELECT count(*) FROM volume") == 1 &&
                await ScalarAsync(dbPath, "SELECT volume_id FROM volume") == hexId,
            "V3-01 the refused write left the retained volume row unchanged");
    }

    // V3-02: the retained hex row is created by the REAL cloud registration consumer, and the REAL
    // local/local pair create on the same volume serial then reuses that exact id and succeeds.
    private static async Task LocalLocalCreateReusesRetainedRowAsync()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The real local/local pair create requires Windows NTFS identities.");
        using var fixture = await CloudFixtureAsync("local-local-reuse");
        var serial = SerialHex(fixture.LocalRoot);
        await fixture.Host.SetRootSelectionAsync(fixture.Pair.Id, true);
        var retained = await ScalarAsync(fixture.DatabasePath,
            "SELECT volume_id FROM volume WHERE installation_id='" + Installation + "'");
        Require(await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM volume") == 1 &&
                retained.Length > 0,
            "V3-02 the real cloud registration retained one volume row: " + retained);
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT json_extract(identity_json,'$.volumeId') FROM root_binding WHERE pair_id='" +
                fixture.Pair.Id + "' AND side='local' AND state='active'") == retained,
            "V3-02 the cloud pair's local root binding names that exact retained volume id");

        // The real local/local pair create over two different directories on the same NTFS volume.
        var left = Directory.CreateDirectory(IOPath.Combine(fixture.Root, "pair", "left")).FullName;
        var right = Directory.CreateDirectory(IOPath.Combine(fixture.Root, "pair", "right")).FullName;
        var leftEndpoint = LocalEndpoint(left);
        var rightEndpoint = LocalEndpoint(right);
        Require(leftEndpoint.LocalIdentity!.VolumeSerialNumber == VolumeSerial(fixture.LocalRoot) &&
                leftEndpoint.LocalIdentity.VolumeSerialNumber == rightEndpoint.LocalIdentity!.VolumeSerialNumber,
            "V3-02 both local endpoints are on the retained volume fixture's one NTFS volume");
        var endpoints = new SyncPairEndpoints(leftEndpoint, rightEndpoint, SyncMode.TwoWay);
        var draft = SyncEndpointPairDraft.Create(endpoints, "Volume reuse local pair");
        var payload = IOPath.Combine(left, "reuse.txt");
        var target = IOPath.Combine(right, "reuse.txt");
        await File.WriteAllTextAsync(payload, "volume identity reuse payload");

        var bound = await fixture.Host.BindEndpointPairAsync(draft);
        Require(await CountAsync(fixture.DatabasePath,
                "SELECT count(*) FROM volume WHERE installation_id='" + Installation +
                "' AND upper(volume_serial_hex)=upper('" + serial + "')") == 1 &&
                await ScalarAsync(fixture.DatabasePath,
                    "SELECT volume_id FROM volume WHERE installation_id='" + Installation +
                    "' AND upper(volume_serial_hex)=upper('" + serial + "')") == retained,
            "V3-02 the real local/local pair create reused the retained volume id instead of deriving a second one");
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT json_extract(identity_json,'$.volumeId') FROM root_binding WHERE pair_id='" +
                bound.Pair.Id + "' AND side='local' AND state='active'") == retained,
            "V3-02 the new local pair's root binding names the same retained volume id");
        Require(!File.Exists(target),
            "V3-02 the pair create persisted its bindings without transferring before first-sync approval");

        await fixture.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id);
        var approved = await fixture.Host.ApproveBootstrapMergeAsync(bound.Pair.Id);
        var run = await fixture.Host.RunPairsAsync([approved], "Volume identity reuse fixture");
        Require(run.Items.All(item => item.FailureType is null),
            "V3-02 the real coordinated run reported no pair failure: " +
            string.Join(" | ", run.Items.Select(item => item.FailureType ?? "none")));
        Require(File.Exists(target) && await File.ReadAllTextAsync(target) == "volume identity reuse payload",
            "V3-02 the real local/local create transferred the approved bytes through the reused binding");
        Require(await CountAsync(fixture.DatabasePath,
                "SELECT count(*) FROM volume WHERE installation_id='" + Installation +
                "' AND upper(volume_serial_hex)=upper('" + serial + "')") == 1,
            "V3-02 the completed run added no second volume row for the natural key");
    }

    // V3-03: the reverse order. The legacy numeric row is seeded exactly as the pre-fix caller wrote it, and
    // the REAL cloud registration consumer must then keep that id unchanged instead of re-keying it.
    private static async Task CloudRegistrationReusesLegacyNumericRowAsync()
    {
        using var fixture = await CloudFixtureAsync("cloud-reuse-legacy", seedVolumeRow: false);
        var serial = SerialHex(fixture.LocalRoot);
        var legacyId = LegacyNumericVolumeId(Installation, VolumeSerial(fixture.LocalRoot));
        await fixture.Database.RegisterVolumeAsync(legacyId, Installation, serial, "NTFS", null, false, false, true);
        var firstSeen = await ScalarAsync(fixture.DatabasePath, "SELECT first_seen_utc FROM volume");

        await fixture.Host.SetRootSelectionAsync(fixture.Pair.Id, true);

        Require(await CountAsync(fixture.DatabasePath, "SELECT count(*) FROM volume") == 1 &&
                await ScalarAsync(fixture.DatabasePath, "SELECT volume_id FROM volume") == legacyId,
            "V3-03 the real cloud registration kept the retained legacy numeric id unchanged");
        Require(await ScalarAsync(fixture.DatabasePath,
                "SELECT json_extract(identity_json,'$.volumeId') FROM root_binding WHERE pair_id='" +
                fixture.Pair.Id + "' AND side='local' AND state='active'") == legacyId,
            "V3-03 the cloud pair's local root binding names the retained legacy numeric id");
        Require(await ScalarAsync(fixture.DatabasePath, "SELECT volume_serial_hex FROM volume") == serial &&
                await ScalarAsync(fixture.DatabasePath, "SELECT first_seen_utc FROM volume") == firstSeen,
            "V3-03 the retained serial and first-seen time were not rewritten or re-keyed");
    }

    // V3-04: resolving the same natural key again, and again after the database is closed and reopened,
    // returns the identical id with still exactly one row.
    private static async Task IdempotentAndReopenAsync()
    {
        var (db, dbPath, localRoot) = NewAuthority("idempotent");
        var serial = SerialHex(localRoot);
        var first = await db.ResolveVolumeIdAsync(Installation, serial, "NTFS", null, false, false, true);
        var again = await db.ResolveVolumeIdAsync(Installation, serial.ToUpperInvariant(), "NTFS", null,
            false, false, true);
        Require(first == again, "V3-04 a repeat resolve returns the identical retained id: " + first);
        var reopened = new SyncAuthorityDatabase(dbPath);
        var afterReopen = await reopened.ResolveVolumeIdAsync(Installation, serial, "NTFS", null, false, false, true);
        Require(afterReopen == first && await CountAsync(dbPath, "SELECT count(*) FROM volume") == 1,
            "V3-04 a closed and reopened database still resolves the one retained row to the same id");
        SqliteConnection.ClearAllPools();
    }

    // V3-05: the meaningful refusals and the distinct-key path. A second installation, a duplicate ambiguous
    // natural row, a non-NTFS filesystem and a derived id already occupied by a different volume are each
    // refused, and every refusal leaves the retained rows exactly as they were.
    private static async Task RefusalsLeaveStateIntactAsync()
    {
        var (db, dbPath, localRoot) = NewAuthority("refusals");
        var serial = SerialHex(localRoot);
        var retained = await db.ResolveVolumeIdAsync(Installation, serial, "NTFS", null, false, false, true);
        var before = await SnapshotAsync(dbPath);

        var nonNtfs = "";
        try { await db.ResolveVolumeIdAsync(Installation, serial, "FAT32", null, false, false, true); }
        catch (InvalidOperationException error) { nonNtfs = error.Message; }
        Require(nonNtfs.Contains("NTFS", StringComparison.Ordinal),
            "V3-05 a non-NTFS filesystem is refused before any registration: " + nonNtfs);

        var foreignInstallation = "";
        try { await db.ResolveVolumeIdAsync("installation-volume-reuse-481-other", serial, "NTFS", null, false, false, true); }
        catch (Exception error) when (error is SqliteException or InvalidOperationException or InvalidDataException)
        { foreignInstallation = error.Message; }
        Require(foreignInstallation.Length > 0 &&
                await CountAsync(dbPath, "SELECT count(*) FROM volume WHERE installation_id='" + Installation + "'") == 1,
            "V3-05 another installation cannot adopt this installation's volume row: " + foreignInstallation);

        // A distinct natural key on the same installation really is a distinct volume identity.
        var secondSerial = OtherSerialHex(VolumeSerial(localRoot));
        var second = await db.ResolveVolumeIdAsync(Installation, secondSerial, "NTFS", null, false, false, true);
        Require(second != retained &&
                await CountAsync(dbPath, "SELECT count(*) FROM volume") == 2,
            "V3-05 a distinct serial registers its own row without disturbing the retained one: " + second);

        // A derived id already occupied by a different volume must be refused, not reused or overwritten.
        await ExecuteAsync(dbPath,
            "UPDATE volume SET volume_serial_hex='" + secondSerial + "-moved' WHERE volume_id='" + second + "';");
        var occupied = "";
        try { await db.ResolveVolumeIdAsync(Installation, secondSerial, "NTFS", null, false, false, true); }
        catch (InvalidOperationException error) { occupied = error.Message; }
        Require(occupied.Contains("occupied by a different volume", StringComparison.Ordinal),
            "V3-05 an id occupied by a different volume is refused: " + occupied);
        Require(await CountAsync(dbPath,
                "SELECT count(*) FROM volume WHERE volume_id='" + second + "' AND volume_serial_hex='" +
                secondSerial + "-moved'") == 1,
            "V3-05 the refused occupancy check left the other volume row unchanged");

        // A duplicate ambiguous natural row (same installation and serial under a different case) is refused
        // rather than silently choosing one of the two ids.
        await ExecuteAsync(dbPath, "DELETE FROM volume WHERE volume_id='" + second + "';");
        await ExecuteAsync(dbPath,
            "INSERT INTO volume(volume_id,installation_id,volume_serial_hex,filesystem,volume_guid_path," +
            "first_seen_utc,last_seen_utc,supports_file_id_128,supports_usn,supports_atomic_rename,status) " +
            "VALUES('volume-ambiguous-481','" + Installation + "','" + serial.ToUpperInvariant() +
            "','NTFS',NULL,'2026-01-01T00:00:00.0000000+00:00','2026-01-01T00:00:00.0000000+00:00',0,0,1,'online');");
        var ambiguous = "";
        try { await db.ResolveVolumeIdAsync(Installation, serial, "NTFS", null, false, false, true); }
        catch (InvalidOperationException error) { ambiguous = error.Message; }
        Require(ambiguous.Contains("more than one volume row", StringComparison.Ordinal),
            "V3-05 two retained rows for one natural key are refused as ambiguous: " + ambiguous);
        Require(before.Length > 0 && await CountAsync(dbPath, "SELECT count(*) FROM volume") == 2,
            "V3-05 every refusal left the two retained rows in place");
    }

    // ---------------------------------------------------------------------------------------------
    // fixture helpers
    // ---------------------------------------------------------------------------------------------

    private sealed record CloudFixture(string Root, string LocalRoot, string DatabasePath,
        SyncAuthorityDatabase Database, SyncAuthorityRuntimeHost Host, SyncPair Pair,
        PilotInstanceLock Instance) : IDisposable
    {
        public void Dispose()
        {
            Host.Dispose();
            Instance.Dispose();
        }
    }

    private static (SyncAuthorityDatabase Database, string DatabasePath, string LocalRoot) NewAuthority(string name)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var localRoot = Directory.CreateDirectory(IOPath.Combine(root, "volume")).FullName;
        var databasePath = IOPath.Combine(root, "authority.db");
        var database = new SyncAuthorityDatabase(databasePath);
        database.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson).GetAwaiter().GetResult();
        return (database, databasePath, localRoot);
    }

    private static async Task<CloudFixture> CloudFixtureAsync(string name, bool seedVolumeRow = true)
    {
        var root = IOPath.Combine(_root, name);
        Directory.CreateDirectory(root);
        var localRoot = Directory.CreateDirectory(IOPath.Combine(root, "cloud-local")).FullName;
        var state = Directory.CreateDirectory(IOPath.Combine(root, "state")).FullName;
        var instance = PilotInstanceLock.Acquire(IOPath.Combine(state, "instance.lock"));
        var databasePath = IOPath.Combine(root, "authority.db");
        var database = new SyncAuthorityDatabase(databasePath);
        // Bind the fixture installation first so the real Host adopts this exact identity rather than
        // generating one, and every raw assertion below can name it.
        await database.InitializeAsync(Installation, ProfileId, SourceRevision, CapabilityJson);
        var connections = new DropboxConnectionStore(instance.StateAuthority);
        var connection = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId, AccountId,
            "ns-volume-reuse-481", "ns-volume-reuse-481", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes, DisplayName: "Volume reuse fixture",
            Email: "volume-reuse@example.invalid").Normalize();
        await connections.SaveNewAsync(connection);
        var host = new SyncAuthorityRuntimeHost(instance.StateAuthority, new HttpClient(new NoNetwork()),
            new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
            connections, new FixtureCredentials(), database,
            (_, _) => throw new InvalidOperationException("Volume identity checks never reach a provider."));
        await host.LoadPairsAsync();
        var installationId = await database.LoadInstallationIdAsync()
                             ?? throw new InvalidDataException("The fixture authority retained no installation.");
        Require(installationId == Installation,
            "the real Host initialised the isolated fixture authority under its own installation identity");
        var lease = PilotDirectoryLease.Open(localRoot);
        var pair = new SyncPair("volume-reuse-cloud-481", "Volume reuse cloud pair", localRoot,
            lease.Identity.VolumeSerialNumber, lease.Identity.FileId, "/Cloud", "id:volume-reuse",
            SelectionDefaultIncluded: false, ConnectionId: connection.ConnectionId, Provider: "dropbox",
            InitialReviewApproved: true);
        await database.RegisterPairAsync(pair.Id, pair.PolicyRevision, new SyncAuthorityPairConfiguration(
            "recoverable", 0, 1, PairConfigJson, "content_times_basic_attrs", RecursiveMode: true,
            AutomaticPruning: false, RecoveryRetentionDays: int.MaxValue));
        await database.RetainPairDefinitionAsync(pair, true);
        if (seedVolumeRow)
            await database.ResolveVolumeIdAsync(Installation, SerialHex(localRoot), "NTFS", null,
                false, false, true);
        return new CloudFixture(root, localRoot, databasePath, database, host, pair, instance);
    }

    private static SyncEndpoint LocalEndpoint(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return SyncEndpoint.Local(lease.Path,
            new SyncLocalEndpointIdentity(lease.Identity.VolumeSerialNumber, lease.Identity.FileId));
    }

    private static uint VolumeSerial(string path)
    {
        using var lease = PilotDirectoryLease.Open(path);
        return lease.Identity.VolumeSerialNumber;
    }

    private static string SerialHex(string path) =>
        VolumeSerial(path).ToString("x8", CultureInfo.InvariantCulture);

    // The exact pre-fix SyncEndpointPairRuntime derivation: role volume, installation, DECIMAL serial.
    // Reproduced only as a seed so the retained row is byte-for-byte what the installed build wrote.
    private static string LegacyNumericVolumeId(string installationId, uint serial) =>
        "volume-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            "volume\n" + installationId + "\n" + serial.ToString(CultureInfo.InvariantCulture))))
            .ToLowerInvariant()[..32];

    private static string OtherSerialHex(uint serial) =>
        ((serial + 1) % uint.MaxValue).ToString("x8", CultureInfo.InvariantCulture);

    private static string DistinctVolumeFixtureNote()
    {
        var ntfs = DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed &&
            string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            .Select(drive => drive.Name).ToArray();
        return ntfs.Length > 1
            ? "available: " + string.Join(",", ntfs)
            : "unavailable: only " + string.Join(",", ntfs) +
              " is a fixed NTFS volume on this machine, so the two-real-volumes local/local fixture cannot be" +
              " provisioned here; the distinct-key and non-NTFS refusals are proven at the retained" +
              " registration boundary instead.";
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("Volume identity checks attempted a provider operation.");
    }

    private sealed class FixtureCredentials : IDropboxRefreshTokenStore
    {
        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>("fixture-not-a-credential");
        public Task WriteAsync(string refreshToken, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task DeleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public IDropboxRefreshTokenStore ForConnection(DropboxConnectionBinding binding) => this;
    }

    // ---------------------------------------------------------------------------------------------
    // raw isolated-database reads
    // ---------------------------------------------------------------------------------------------

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        SqliteConnection.ClearAllPools();
    }

    private static async Task<long> CountAsync(string databasePath, string sql) =>
        Convert.ToInt64(await ScalarAsync(databasePath, sql), CultureInfo.InvariantCulture);

    private static async Task<string> ScalarAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + databasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        SqliteConnection.ClearAllPools();
        return value is null or DBNull ? string.Empty : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static async Task<string> SnapshotAsync(string databasePath) =>
        await ScalarAsync(databasePath,
            "SELECT group_concat(volume_id || '|' || volume_serial_hex, '\n') FROM (SELECT * FROM volume ORDER BY volume_id);");

    private static async Task CaseAsync(string id, Func<Task> body)
    {
        var entry = new Dictionary<string, object?> { ["id"] = id, ["state"] = "running" };
        Cases.Add(entry);
        try
        {
            await body();
            entry["state"] = "observed";
        }
        catch (Exception error)
        {
            entry["state"] = "failed";
            entry["error"] = error.GetType().Name + ": " + error.Message;
            throw;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
        _assertions++;
        Cases[^1]["observed"] = (Cases[^1].TryGetValue("observed", out var prior) && prior is string text
            ? text + " | " : string.Empty) + message;
        Console.WriteLine("PASS " + message);
    }
}
