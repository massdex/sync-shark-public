using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.Text;
using Massdex.SyncShark;

namespace SyncShark.UiFixture;

internal static partial class Program
{
    private static async Task RunEndpointCloudHostChecksAsync(string root)
    {
        var priorClient = GoogleDriveProviderConfiguration.RegisteredClientIdForTest;
        GoogleDriveProviderConfiguration.RegisteredClientIdForTest = "fixture-client";
        try
        {
        foreach (var direction in Enum.GetValues<SyncMode>())
        {
            var fixture = Path.Combine(root, direction.ToString());
            var localPath = Directory.CreateDirectory(Path.Combine(fixture, "local")).FullName;
            var dbPath = Path.Combine(fixture, "authority.db");
            using var instance = PilotInstanceLock.Acquire(Path.Combine(fixture, "state", "instance.lock"));
            var binding = new GoogleDriveConnectionBinding("fixture-client", GoogleDriveConnectionBinding.CreateAccountKey("perm-account-one"),
                "perm-account-one", "fixture@example.invalid", "Fixture", [GoogleDriveProviderConfiguration.DriveScope]).Normalize();
            await new GoogleDriveConnectionStore(instance.StateAuthority).SaveAsync(binding);
            using var server = new SyntheticDriveServer { MyDriveRootIdForTest = "fixture-my-drive-root" };
            server.AddFolder("fixture-my-drive-root", "My Drive", null);
            server.AddFolder("root-folder", "Pair", "fixture-my-drive-root");
            server.AddFolder("apps", "Apps", "fixture-my-drive-root");
            server.AddFolder("product", "Sync Shark", "apps");
            server.AddFolder("recovery", "Recovery", "product");
            server.AddFile("cloud-input", "cloud.txt", "root-folder", Encoding.UTF8.GetBytes("cloud source"));
            await File.WriteAllTextAsync(Path.Combine(localPath, "local.txt"), "local source");
            using var http = new HttpClient(server);
            SyncAuthorityRuntimeHost Open() => new(instance.StateAuthority, http,
                new SyncPairStore(instance.StateAuthority), new SyncRuntimeStateStore(instance.StateAuthority),
                new DropboxConnectionStore(instance.StateAuthority), new InspectionCredentialBoundary(),
                new SyncAuthorityDatabase(dbPath),
                googleDriveCredentialStore: new EndpointCloudCredentials(), googleDriveTokenEndpoint: new EndpointCloudTokens());
            var coldBindingTimer = Stopwatch.StartNew();
            using (var host = Open())
            {
                using var local = PilotDirectoryLease.Open(localPath);
                var endpoints = new SyncPairEndpoints(
                    SyncEndpoint.Cloud(SyncEndpointKind.GoogleDrive, binding.ConnectionId, binding.PermissionId, "root-folder", "/Pair"),
                    SyncEndpoint.Local(localPath, new SyncLocalEndpointIdentity(local.Identity.VolumeSerialNumber, local.Identity.FileId)), direction);
                var bound = await host.BindEndpointPairAsync(SyncEndpointPairDraft.Create(endpoints, "Cloud left"));
                coldBindingTimer.Stop();
                Console.WriteLine("MEASURE_SETUP " + JsonSerializer.Serialize(new
                {
                    mode = direction.ToString(), cold_host_and_binding_elapsed_ms = coldBindingTimer.ElapsedMilliseconds,
                    timing_scope = "Host construction and first pair binding; excludes process startup and fixture/server preparation"
                }));
                EndpointUiRequire(bound.Pair.LeftEndpoint?.Kind == SyncEndpointKind.GoogleDrive &&
                    bound.Pair.RightEndpoint?.IsLocal == true && bound.Pair.Direction == direction &&
                    !File.Exists(Path.Combine(localPath, "cloud.txt")), "cloud/local " + direction + " binds without copying");
                var phases = new List<object>();
                if (direction == SyncMode.TwoWay)
                    ReportPhase("initial-empty", phases, dbPath, "pair binding only; no run yet",
                        Directory.GetFiles(localPath).Length, server.Names("root-folder").Count(),
                        0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
                var syncedTimer = Stopwatch.StartNew();
                await host.RefreshBootstrapInventoryAsync(bound.Pair.Id);
                var approved = await host.ApproveBootstrapMergeAsync(bound.Pair.Id);
                var run = await host.RunPairsAsync([approved], "Endpoint orientation fixture");
                if (run.Items.FirstOrDefault(item => item.FailureType is not null) is { } failure)
                    throw new InvalidDataException(failure.FailureType + ": " + failure.FailureMessage);
                EndpointUiRequire(run.Items.All(item => item.Result is { Blocked: 0 }), "cloud/local coordinated host has no hidden blocked item");
                var downloadExpected = direction != SyncMode.DownloadOnly;
                var uploadExpected = direction != SyncMode.UploadOnly;
                EndpointUiRequire(File.Exists(Path.Combine(localPath, "cloud.txt")) == downloadExpected &&
                    (!downloadExpected || await File.ReadAllTextAsync(Path.Combine(localPath, "cloud.txt")) == "cloud source"),
                    "cloud/local " + direction + " applies logical left-to-right policy to actual download bytes");
                EndpointUiRequire(server.Names("root-folder").Contains("local.txt") == uploadExpected &&
                    (!uploadExpected || Encoding.UTF8.GetString(server.BodyFor(server.IdByName("root-folder", "local.txt"))) == "local source"),
                    "cloud/local " + direction + " applies logical right-to-left policy to actual upload bytes");
                if (direction == SyncMode.TwoWay)
                {
                    syncedTimer.Stop();
                    ReportPhase("initial-synced", phases, dbPath,
                        "from the initial baseline sample through RefreshBootstrapInventoryAsync, ApproveBootstrapMergeAsync and the first RunPairsAsync plus readback",
                        Directory.GetFiles(localPath).Length, server.Names("root-folder").Count(),
                        run.Items.Count,
                        run.Items.Sum(item => item.Result?.AppliedItems ?? 0),
                        run.Items.Sum(item => item.Result?.Uploaded ?? 0),
                        run.Items.Sum(item => item.Result?.Downloaded ?? 0),
                        run.Items.Sum(item => item.Result?.Identical ?? 0),
                        run.Items.Sum(item => item.Result?.Blocked ?? 0),
                        run.Items.Sum(item => item.Result?.NeedsVerification ?? 0),
                        run.Items.Count(item => item.FailureType is not null), 0, syncedTimer.ElapsedMilliseconds);
                    for (var repeatNumber = 0; repeatNumber < 2; repeatNumber++)
                    {
                    var repeatTimer = Stopwatch.StartNew();
                    var repeat = await host.RunPairsAsync([approved], "measure unchanged repeat");
                    repeatTimer.Stop();
                    if (repeat.Items.FirstOrDefault(item => item.FailureType is not null) is { } repeatFailure)
                        throw new InvalidDataException("unchanged repeat: " + repeatFailure.FailureType + ": " + repeatFailure.FailureMessage);
                    ReportPhase(repeatNumber == 0 ? "post-sync-reconciliation" : "settled-unchanged-repeat", phases, dbPath,
                        "normal repeat run only; no input edits; " + (repeatNumber == 0 ? "first observation after applied sync" : "after post-sync reconciliation"),
                        Directory.GetFiles(localPath).Length, server.Names("root-folder").Count(),
                        repeat.Items.Count,
                        repeat.Items.Sum(item => item.Result?.AppliedItems ?? 0),
                        repeat.Items.Sum(item => item.Result?.Uploaded ?? 0),
                        repeat.Items.Sum(item => item.Result?.Downloaded ?? 0),
                        repeat.Items.Sum(item => item.Result?.Identical ?? 0),
                        repeat.Items.Sum(item => item.Result?.Blocked ?? 0),
                        repeat.Items.Sum(item => item.Result?.NeedsVerification ?? 0),
                        repeat.Items.Count(item => item.FailureType is not null), 0, repeatTimer.ElapsedMilliseconds);
                    }
                    const string changedText = "local source changed for the phase journey";
                    var changedBytes = Encoding.UTF8.GetByteCount(changedText);
                    var changedTimer = Stopwatch.StartNew();
                    await File.WriteAllTextAsync(Path.Combine(localPath, "local.txt"), changedText);
                    var changedRun = await host.RunPairsAsync([approved], "measure one changed file");
                    if (changedRun.Items.FirstOrDefault(item => item.FailureType is not null) is { } changedFailure)
                        throw new InvalidDataException("changed file: " + changedFailure.FailureType + ": " + changedFailure.FailureMessage);
                    var remoteChanged = Encoding.UTF8.GetString(server.BodyFor(server.IdByName("root-folder", "local.txt")));
                    EndpointUiRequire(remoteChanged == changedText,
                        "measure one changed local file: the real remote bytes read back equal the edited content");
                    changedTimer.Stop();
                    ReportPhase("one-changed-file", phases, dbPath,
                        "one real edit to the existing local file, then a normal Host run and remote byte readback",
                        Directory.GetFiles(localPath).Length, server.Names("root-folder").Count(),
                        changedRun.Items.Count,
                        changedRun.Items.Sum(item => item.Result?.AppliedItems ?? 0),
                        changedRun.Items.Sum(item => item.Result?.Uploaded ?? 0),
                        changedRun.Items.Sum(item => item.Result?.Downloaded ?? 0),
                        changedRun.Items.Sum(item => item.Result?.Identical ?? 0),
                        changedRun.Items.Sum(item => item.Result?.Blocked ?? 0),
                        changedRun.Items.Sum(item => item.Result?.NeedsVerification ?? 0),
                        changedRun.Items.Count(item => item.FailureType is not null), changedBytes, changedTimer.ElapsedMilliseconds);
                    Console.WriteLine("MEASURE_SUMMARY " + JsonSerializer.Serialize(new { mode = direction.ToString(), phases }));
                }
                var enabled = (await host.SetPairEnabledAsync(approved.Id, true)).Pairs.Single();
                var monitors = await host.ConfigureMonitorsAsync([enabled], () => Task.FromResult(true));
                try
                {
                    EndpointUiRequire(monitors is [{ State: "Monitoring", FailureMessage: null }],
                        "R538 reversed Google/local " + direction + " starts both actual endpoint monitors");
                    var projections = await host.LoadMonitorProjectionAsync();
                    EndpointUiRequire(projections.Count(item => item.PairId == enabled.Id && item.Kind == "directory_changes") == 1 &&
                        projections.Count(item => item.PairId == enabled.Id && item.Kind == "dropbox_cursor") == 1,
                        "R538 the reversed Google endpoint gets its own provider feed and local endpoint its own directory watcher");
                }
                finally { await host.StopMonitorsAsync(); }
            }
            using (var restarted = Open())
            {
                var saved = (await restarted.LoadPairsAsync()).Pairs.Single();
                EndpointUiRequire(SyncEndpointEngineLayout.IsReversed(saved) && saved.Direction == direction &&
                    saved.InitialReviewApproved == true, "cloud/local restart retains ordered endpoints, direction and approval");
            }
        }
        Console.WriteLine("PASS actual cloud/local host in all three directions; synthetic Google HTTP, real local filesystem and authority.");
        }
        finally { GoogleDriveProviderConfiguration.RegisteredClientIdForTest = priorClient; }
    }

    private static readonly string[] MeasuredTables =
    [
        "command_request", "operation", "operation_attempt", "operation_member",
        "observation", "observation_interval", "observation_cut", "object_version",
        "activity_event", "activity_context", "consent_header", "consent_item",
        "logical_object", "membership", "synchronization_point", "history_event"
    ];

    private static object SampleAuthorityDatabase(string databasePath)
    {
        var database = new FileInfo(databasePath);
        var wal = new FileInfo(databasePath + "-wal");
        var shm = new FileInfo(databasePath + "-shm");
        long pageSize = 0, pageCount = 0, freelistCount = 0;
        var counts = new Dictionary<string, object?>(StringComparer.Ordinal);
        var present = new HashSet<string>(StringComparer.Ordinal);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table';";
                using var reader = command.ExecuteReader();
                while (reader.Read()) present.Add(reader.GetString(0));
            }
            long Scalar(string sql)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            pageSize = Scalar("PRAGMA page_size;");
            pageCount = Scalar("PRAGMA page_count;");
            freelistCount = Scalar("PRAGMA freelist_count;");
            foreach (var table in MeasuredTables)
                counts[table] = present.Contains(table) ? Scalar("SELECT count(*) FROM " + (char)34 + table + (char)34 + ";") : null;
        }
        return new
        {
            db_bytes = database.Exists ? database.Length : 0,
            wal_bytes = wal.Exists ? wal.Length : 0,
            wal_exists = wal.Exists,
            shm_bytes = shm.Exists ? shm.Length : 0,
            page_size = pageSize,
            page_count = pageCount,
            freelist_count = freelistCount,
            logical_db_bytes = pageSize * pageCount,
            table_counts = counts
        };
    }

    private static void ReportPhase(string label, List<object> phases, string databasePath, string timingScope,
        long localFiles, long cloudFiles, int pairResultCount, int appliedActions, int uploaded, int downloaded,
        int identical, int blocked, int needsVerification, int batchFailures, long changedBytes,
        long elapsedMilliseconds)
    {
        var phase = new
        {
            phase = label,
            timing_scope = timingScope,
            elapsed_ms = label == "initial-empty" ? (long?)null : elapsedMilliseconds,
            changed_file_count = label == "one-changed-file" ? 1 : 0,
            local_file_count = localFiles,
            cloud_file_count = cloudFiles,
            pair_result_count = pairResultCount,
            applied_actions = appliedActions,
            uploaded = uploaded,
            downloaded = downloaded,
            identical = identical,
            blocked = blocked,
            needs_verification = needsVerification,
            batch_failures = batchFailures,
            changed_payload_bytes = changedBytes,
            database = SampleAuthorityDatabase(databasePath)
        };
        phases.Add(phase);
        Console.WriteLine("MEASURE_PHASE " + JsonSerializer.Serialize(phase));
    }
    private sealed class EndpointCloudCredentials : IGoogleDriveRefreshTokenStore
    {
        public Task<string?> ReadAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult<string?>("fixture-refresh");
        public Task WriteAsync(string id, string token, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No credential writes in this fixture.");
        public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => throw new InvalidOperationException("No credential deletes in this fixture.");
    }

    private sealed class EndpointCloudTokens : IGoogleDriveTokenEndpoint
    {
        public Task<GoogleDriveAccountIdentity> GetAccountAsync(string accessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleDriveAccountIdentity("perm-account-one", "fixture@example.invalid", "Fixture", ""));
        public Task<GoogleDriveTokenResult> RefreshAsync(string id, string refresh, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GoogleDriveTokenResult("fixture-access", DateTime.UtcNow.AddHours(1), [GoogleDriveProviderConfiguration.DriveScope]));
    }
}
