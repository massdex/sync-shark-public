using System.Globalization;
using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;
using Rig = CloudPairHostBindingChecks.HostRig;
using Spec = CloudPairHostBindingChecks.EndpointSpec;
using Wire = CloudPairHostBindingChecks.HostWire;

// M7/G-4's Recovery-dependency subcase only, not a history-pruning or live-migration proof.
// Every decision below is the real public Host over actual isolated SQLite. The wire supplies
// provider input, including a reply lost AFTER its real synthetic mutation. It never supplies an
// admission/retention answer. Disposing/recreating HostRig retains the same DB and the same wire.
[SupportedOSPlatform("windows")]
internal static class CloudRecoveryRetentionDependencyChecks
{
    private const string RelativePath = "retention.txt";
    private const string OriginalText = "Original retained predecessor\n";
    private const string ChangedText = "Current destination before restore\n";
    private static int _checks;
    private static readonly HashSet<string> Covered = new(StringComparer.Ordinal);

    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh Recovery retention-dependency fixture root.");
        Directory.CreateDirectory(root);
        CloudPairHostBindingChecks.UseFixtureRoot(root);
        _checks = 0;
        Covered.Clear();
        var cases = new (string Name, Func<Context, Task> Run)[]
        {
            ("protected-window", ProtectedWindowAsync),
            ("expired-replay", ExpiredReplayAsync),
            ("pending-completed", PendingThenCompletedAsync),
            ("release-first", ReleaseFirstAsync),
            ("lost-restore-reopen", LostRestoreReplyAsync),
            ("lost-cleanup-reopen", LostCleanupReplyAsync),
            ("no-effect-reopen", NoEffectSupersedeAsync)
        };
        var providers = new[] { SyncEndpointKind.Dropbox, SyncEndpointKind.GoogleDrive };
        var priorPhase = SyncAuthorityRuntimeHost.CloudRestorePhaseForTest;
        try
        {
            foreach (var provider in providers)
            foreach (var test in cases)
            {
                using var context = await CreateAsync(provider, test.Name);
                await test.Run(context);
                Check(Covered.Add(context.Name), context.Name + ": duplicate case completion");
                Console.WriteLine("PASS Recovery retention dependency: " + context.Name);
            }
        }
        finally { SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = priorPhase; }
        var required = providers.SelectMany(provider => cases.Select(test =>
            CaseName(provider, test.Name))).ToHashSet(StringComparer.Ordinal);
        Check(Covered.SetEquals(required) && Covered.Count == 14,
            "Required retention dependency coverage missing: " + string.Join(", ", required.Except(Covered)) +
            "; unexpected: " + string.Join(", ", Covered.Except(required)));
        Console.WriteLine("PASS public Recovery retention dependencies: " + _checks +
                          " assertions; required cases 14, covered cases 14, missing cases 0; " +
                          "history expiration and live migration NOT covered");
        await LegacyDependenciesAsync(Path.Combine(root, "legacy"));
    }

    private static string CaseName(SyncEndpointKind provider, string test) =>
        "retention-dependency-" + provider + "-" + test;

    private static Spec Endpoint(SyncEndpointKind provider, string name) => provider == SyncEndpointKind.Dropbox
        ? CloudPairHostBindingChecks.HostDropbox(name, "/Retention " + name)
        : CloudPairHostBindingChecks.HostDrive(name, "/Retention " + name);

    private sealed record Artifact(SyncRecoveryArtifactProjection Row, SyncAuthorityCloudRecoveryDescriptor Descriptor)
    {
        internal string Key => Row.RecoveryArtifactId;
    }

    private sealed class Context(string name, Rig rig, Wire wire, BoundaryTransport transport,
        HttpClient readerHttp, ISyncRemoteStore reader, SyncPair pair, Artifact artifact) : IDisposable
    {
        internal string Name { get; } = name;
        internal Rig Rig { get; private set; } = rig;
        internal Wire Wire { get; } = wire;
        internal BoundaryTransport Transport { get; } = transport;
        internal ISyncRemoteStore Reader { get; } = reader;
        internal SyncPair Pair { get; } = pair;
        internal Artifact Artifact { get; } = artifact;
        internal async Task ReopenAsync()
        {
            Rig.Dispose();
            Rig = await CloudPairHostBindingChecks.HostRig.CreateAsync(Name, Wire, Transport);
            Check((await Rig.Host.LoadPairsAsync()).Pairs.Any(item => item.Id == Pair.Id),
                Name + ": the same retained pair reopens without registering a replacement");
            Check(Rig.Host.LoadTransferSettings().ResourceMode == "Performance",
                Name + ": functional resource mode persists on this isolated Host across reopen");
        }
        public void Dispose()
        {
            Rig.Dispose();
            readerHttp.Dispose();
            Transport.Dispose();
            Wire.Dispose();
        }
    }

    // Reader adapters retain their own HttpClient across Host disposal. Re-registering endpoints on
    // reopen would modify fixture provider state, so these read-only clients use persisted bindings.
    private sealed class SyntheticReaderToken(string value) : IDropboxAccessTokenSource, IGoogleDriveAccessTokenSource
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(value);
        }
    }

    private static async Task<Context> CreateAsync(SyncEndpointKind provider, string test)
    {
        var name = CaseName(provider, test);
        var wire = new Wire();
        var transport = new BoundaryTransport(wire);
        var http = new HttpClient(transport, disposeHandler: false);
        Rig? rig = null;
        try
        {
            rig = await Rig.CreateAsync(name, wire, transport);
            await FunctionalTransferModeAsync(rig.Host, name);
            var left = Endpoint(provider, name + "-L");
            var right = Endpoint(provider, name + "-R");
            await rig.AddEndpointAsync(left);
            await rig.AddEndpointAsync(right);
            rig.SeedFile(left, RelativePath, OriginalText);
            rig.SeedFile(right, RelativePath, OriginalText);
            var draft = SyncEndpointPairDraft.Create(new SyncPairEndpoints(
                SyncEndpoint.Cloud(left.Kind, left.ConnectionId, left.EndpointNamespaceId, left.FolderId, left.FolderPath),
                SyncEndpoint.Cloud(right.Kind, right.ConnectionId, right.EndpointNamespaceId, right.FolderId, right.FolderPath),
                SyncMode.TwoWay), name) with { RecoveryRetentionDays = 30 };
            var bound = await rig.Host.BindEndpointPairAsync(draft);
            await rig.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id);
            await rig.Host.ApproveBootstrapMergeAsync(bound.Pair.Id);
            var enabled = await rig.Host.SetPairEnabledAsync(bound.Pair.Id, true);
            var pair = enabled.Pairs.Single(item => item.Id == bound.Pair.Id);
            await RunPairAsync(rig, pair, name);
            rig.SeedFile(left, RelativePath, ChangedText);
            await RunPairAsync(rig, pair, name);
            var row = (await rig.Db.LoadRecoveryArtifactsAsync()).Single(item => item.PairId == pair.Id);
            Check(SyncAuthorityCloudRecoveryDescriptor.TryParse(row.IdentityJson, out var descriptor) &&
                  descriptor!.LogicalSide == SyncAuthorityCloudSides.LogicalRight,
                name + ": actual update produced the right endpoint's typed Recovery predecessor");
            Check((await rig.Db.LoadOperationAsync(row.OperationId)).Status == SyncAuthorityOperationStatus.Completed,
                name + ": ordinary producer reached completed before dependency tests");
            ISyncRemoteStore reader;
            if (provider == SyncEndpointKind.Dropbox)
            {
                var binding = new DropboxConnectionStore(rig.StateAuthority).Load(right.ConnectionId)
                    ?? throw new InvalidDataException("Fixture Dropbox binding missing.");
                reader = new DropboxPilotClient(http, new SyntheticReaderToken("dbaccess|" + right.AccountId +
                    "|" + right.RootNamespaceId + "|" + right.HomeNamespaceId), right.FolderPath, binding, right.FolderId);
            }
            else
            {
                var binding = new GoogleDriveConnectionStore(rig.StateAuthority).Load(right.ConnectionId)
                    ?? throw new InvalidDataException("Fixture Drive binding missing.");
                reader = new GoogleDrivePilotClient(http, new SyntheticReaderToken("gaccess:" + right.ConnectionId),
                    binding, right.FolderId, right.FolderPath);
            }
            var context = new Context(name, rig, wire, transport, http, reader, pair, new Artifact(row, descriptor!));
            await RequireRetainedAsync(context, context.Artifact, Encoding.UTF8.GetBytes(OriginalText));
            Check(Same(await DestinationBytesAsync(context, RelativePath), Encoding.UTF8.GetBytes(ChangedText)),
                name + ": current target contains the ordinary update successor");
            transport.RecoveryFileId = descriptor!.RetainedFileId;
            return context;
        }
        catch
        {
            rig?.Dispose();
            http.Dispose();
            transport.Dispose();
            wire.Dispose();
            throw;
        }
    }

    private static async Task RunPairAsync(Rig rig, SyncPair pair, string tag)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = await rig.Host.RunPairsAsync(new[] { pair }, "manual", cancellationToken: timeout.Token);
        Check(run.Items.Count == 1 && run.Items[0].FailureMessage is null &&
              run.Items[0].Result is { Blocked: 0, Conflicts: 0 },
            tag + ": actual producer run failed: " + run.Items.FirstOrDefault()?.FailureMessage);
    }

    private static async Task FunctionalTransferModeAsync(SyncAuthorityRuntimeHost host, string name)
    {
        var requested = host.LoadTransferSettings() with { ResourceMode = "Performance" };
        await host.SaveTransferSettingsAsync(requested);
        Check(JsonSerializer.Serialize(host.LoadTransferSettings()) == JsonSerializer.Serialize(requested),
            name + ": public isolated fixture setting changes ResourceMode only, without bypassing actual admission");
    }

    private static async Task<SyncRecoveryCatalogItem> ItemAsync(Context context) =>
        (await context.Rig.Host.LoadRecoveryCatalogAsync(context.Pair.Id)).Single(item => item.Key == context.Artifact.Key);

    private static async Task<string> ArtifactStatusAsync(Context context, string? key = null) =>
        (await context.Rig.Db.LoadRecoveryArtifactsAsync()).Single(item => item.RecoveryArtifactId ==
            (key ?? context.Artifact.Key)).Status;

    // This is the documented fixture input transformation, not a fake of the retention decision.
    // A fresh Host catalogue/action must still calculate its normal 30-day eligibility itself.
    private static async Task ExpireAsync(Context context, string? key = null)
        => await ExpireAsync(context.Rig.Db, key ?? context.Artifact.Key, context.Name);

    private static async Task ExpireAsync(SyncAuthorityDatabase database, string key, string name)
    {
        await using var connection = new SqliteConnection("Data Source=" + database.DatabasePath);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE recovery_artifact SET retained_utc=$utc WHERE recovery_artifact_id=$id;";
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.AddDays(-31).ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$id", key);
        Check(await command.ExecuteNonQueryAsync() == 1, name + ": backdated exactly one fixture artifact");
    }

    private static async Task LegacyDependenciesAsync(string root)
    {
        var cases = new[] { "legacy-blocked-lost-copy-reopen", "legacy-completed-release-reopen" };
        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in cases)
        {
            using var context = await LegacyContext.CreateAsync(Path.Combine(root, name), name);
            var source = context.Source;
            var sourceItem = await context.ItemAsync(source.RecoveryArtifactId);
            var before = context.Wire.Counters;
            if (name == cases[0])
                context.Wire.LoseCopyReplyFromId = LegacyIdentity(source).FileId;
            SyncRecoveryRestoreResult? result = null;
            using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (name == cases[0])
                await RefusedAsync(async () =>
                {
                    result = await context.Host.RestoreRecoveryAsync(sourceItem, false, restoreTimeout.Token);
                }, name);
            else
                result = await context.Host.RestoreRecoveryAsync(sourceItem, false, restoreTimeout.Token);
            var expectedCompleted = name == cases[1];
            Check(expectedCompleted ? result is not null : result is null, name + ": actual public restore outcome is not fabricated");
            var after = context.Wire.Counters;
            Check(after.Uploads == before.Uploads + 1 && after.Deletes == before.Deletes + 1 &&
                  after.Copies == before.Copies + 1 && after.Restores == before.Restores &&
                  context.Wire.LostCopyReplies == (expectedCompleted ? 0 : 1),
                name + ": actual legacy route retained one current preimage, deleted one target and applied one copy");
            Check(context.Wire.Read("/Pilot/" + RelativePath) == OriginalText,
                name + ": actual destination bytes prove copy applied even when its reply was lost");
            var commandRows = await LegacyRowsAsync(context.Db,
                "SELECT command_request_id,status FROM command_request WHERE target_id=" + Sql(source.RecoveryArtifactId) +
                " AND json_extract(payload_json,'$.action')='restore_recovery' ORDER BY rowid;");
            var command = commandRows.Single().Split('|');
            Check(command[1] == (expectedCompleted ? "completed" : "blocked"),
                name + ": public Host durably records the distinct completed/uncertain command outcome");
            Check((await LegacyRowsAsync(context.Db,
                "SELECT operation_id FROM operation WHERE json_extract(preconditions_json,'$.restoreCommandId')=" +
                Sql(command[0]) + ";")).Count == 0,
                name + ": this legacy command has no separate typed consumer operation to protect it indirectly");
            var artifacts = await context.Db.LoadRecoveryArtifactsAsync();
            var preimage = artifacts.Single(row => LegacySourceId(row) == source.RecoveryArtifactId);
            Check(preimage.OperationId == source.OperationId &&
                  (await context.Db.LoadOperationAsync(preimage.OperationId)).Status == SyncAuthorityOperationStatus.Completed &&
                  !SyncAuthorityCloudRecoveryDescriptor.TryParse(preimage.IdentityJson, out _),
                name + ": actual legacy preimage retains source-ID linkage under the already-completed producer");
            await context.RequireBytesAsync(source, OriginalText);
            await context.RequireBytesAsync(preimage, ChangedText);
            await ExpireAsync(context.Db, source.RecoveryArtifactId, name);
            await ExpireAsync(context.Db, preimage.RecoveryArtifactId, name);
            Check((await context.Db.LoadRecoveryArtifactsAsync()).Where(row =>
                      row.RecoveryArtifactId == source.RecoveryArtifactId || row.RecoveryArtifactId == preimage.RecoveryArtifactId)
                  .All(row => row.RetainedUtc < DateTimeOffset.UtcNow.AddDays(-30)),
                name + ": both real artifacts are chronologically expired under the normal 30-day policy");
            var evidence = await LegacyEvidenceAsync(context.Db);
            if (!expectedCompleted)
            {
                await LegacyRefuseBothAsync(context, source, preimage, evidence);
                await context.ReopenAsync();
                Check(await LegacyEvidenceAsync(context.Db) == evidence,
                    name + ": blocked command, original producer, linked artifacts and exact history survive real reopen");
                await LegacyRefuseBothAsync(context, source, preimage, evidence);
                Check(context.Wire.Read("/Pilot/" + RelativePath) == OriginalText,
                    name + ": cleanup refusals/reopen never replay the uncertain legacy restore");
            }
            else
            {
                await context.ReopenAsync();
                Check(await LegacyEvidenceAsync(context.Db) == evidence,
                    name + ": completed command and retained provenance survive the same real reopen");
                var producer = await LegacyProducerEvidenceAsync(context.Db, source.OperationId);
                var mutations = context.Wire.Counters;
                var releasedItems = new List<SyncRecoveryCatalogItem>();
                foreach (var row in new[] { source, preimage })
                {
                    var item = await context.ItemAsync(row.RecoveryArtifactId);
                    releasedItems.Add(item);
                    await context.Host.RemoveRecoveryCopyAsync(item);
                    Check((await context.Db.LoadRecoveryArtifactsAsync()).Single(candidate =>
                              candidate.RecoveryArtifactId == row.RecoveryArtifactId).Status == "released" &&
                          await context.Client.GetRecoveryAsync(context.Pair.RecoveryRoot!, LegacyIdentity(row).RecoveryPath) is null,
                        name + ": completed legacy dependency allows exact source/preimage expiry and absent readback");
                }
                Check(context.Wire.Counters == (mutations with { Deletes = mutations.Deletes + 2 }) &&
                      await LegacyProducerEvidenceAsync(context.Db, source.OperationId) == producer,
                    name + ": exactly two expired-artifact deletions preserve original producer attempts/receipts/history");
                await context.ReopenAsync();
                var replay = context.Wire.Counters;
                foreach (var item in releasedItems)
                    await context.Host.RemoveRecoveryCopyAsync(item);
                Check(context.Wire.Counters == replay && context.Wire.Read("/Pilot/" + RelativePath) == OriginalText,
                    name + ": released source/preimage replay causes no additional provider effect or destination change");
            }
            Check(covered.Add(name), name + ": duplicate legacy coverage");
            Console.WriteLine("PASS Recovery retention dependency: " + name);
        }
        Check(covered.SetEquals(cases) && covered.Count == 2 && Covered.Count == 14,
            "Required legacy retention coverage missing: " + string.Join(", ", cases.Except(covered)));
        Console.WriteLine("PASS complete public Recovery retention dependencies: original typed 14 plus legacy 2; " +
                          "required cases 16, covered cases 16, missing cases 0; history expiration and live migration NOT covered");
    }

    private static async Task LegacyRefuseBothAsync(LegacyContext context, SyncRecoveryArtifactProjection source,
        SyncRecoveryArtifactProjection preimage, string evidence)
    {
        var before = context.Wire.Counters;
        foreach (var row in new[] { source, preimage })
        {
            var item = await context.ItemAsync(row.RecoveryArtifactId);
            await RefusedAsync(() => context.Host.RemoveRecoveryCopyAsync(item with { CanRemove = true }),
                context.Name + ": " + row.RecoveryArtifactId, "still required");
            Check((await context.Db.LoadRecoveryArtifactsAsync()).Single(candidate =>
                      candidate.RecoveryArtifactId == row.RecoveryArtifactId).Status == "retained",
                context.Name + ": unresolved legacy source/preimage remains retained, not release-authorized");
        }
        Check(context.Wire.Counters == before && await LegacyEvidenceAsync(context.Db) == evidence,
            context.Name + ": source AND linked-preimage cleanup refuse without effects or evidence mutation");
        await context.RequireBytesAsync(source, OriginalText);
        await context.RequireBytesAsync(preimage, ChangedText);
    }

    private static string? LegacySourceId(SyncRecoveryArtifactProjection row)
    {
        using var identity = JsonDocument.Parse(row.IdentityJson);
        return identity.RootElement.TryGetProperty("sourceRecoveryArtifactId", out var value) &&
               value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static SyncArchiveResult LegacyIdentity(SyncRecoveryArtifactProjection row)
    {
        using var identity = JsonDocument.Parse(row.IdentityJson);
        string Value(string name) => identity.RootElement.GetProperty(name).GetString()
            ?? throw new FixtureFailure("Actual legacy artifact has no " + name);
        return new SyncArchiveResult(Value("FileId"), Value("Revision"), Value("RecoveryPath"), row.Size, row.ContentHash);
    }

    private static async Task<List<string>> LegacyRowsAsync(SyncAuthorityDatabase database, string sql)
    {
        await using var connection = new SqliteConnection("Data Source=" + database.DatabasePath + ";Mode=ReadOnly");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index =>
                reader.IsDBNull(index) ? "<null>" : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        return rows;
    }

    private static async Task<string> LegacyEvidenceAsync(SyncAuthorityDatabase database)
    {
        var rows = new List<string>();
        foreach (var (table, key) in new[]
        {
            ("operation", "operation_id"), ("operation_member", "operation_member_id"),
            ("operation_attempt", "attempt_id"), ("effect_receipt", "effect_receipt_id"),
            ("resource_allocation", "allocation_id"), ("history_event", "history_event_id"),
            ("command_request", "command_request_id"), ("recovery_artifact", "recovery_artifact_id")
        })
        {
            rows.Add(table);
            rows.AddRange(await LegacyRowsAsync(database, "SELECT * FROM " + table + " ORDER BY " + key + ";"));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private static async Task<string> LegacyProducerEvidenceAsync(SyncAuthorityDatabase database, string operationId)
    {
        var rows = new List<string>();
        foreach (var (table, key) in new[]
        {
            ("operation", "operation_id"), ("operation_member", "operation_member_id"),
            ("operation_attempt", "attempt_id"), ("resource_allocation", "allocation_id"),
            ("history_event", "history_event_id")
        })
        {
            rows.Add(table);
            rows.AddRange(await LegacyRowsAsync(database,
                "SELECT * FROM " + table + " WHERE operation_id=" + Sql(operationId) + " ORDER BY " + key + ";"));
        }
        rows.AddRange(await LegacyRowsAsync(database, "SELECT r.* FROM effect_receipt r JOIN operation_attempt a " +
            "ON a.attempt_id=r.attempt_id WHERE a.operation_id=" + Sql(operationId) + " ORDER BY effect_receipt_id;"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private sealed class LegacyContext : IDisposable
    {
        internal string Name { get; }
        private string Root { get; }
        internal LegacyTransport Wire { get; }
        private HttpClient Http { get; }
        private DropboxConnectionBinding Connection { get; }
        private PilotInstanceLock? Instance { get; set; }
        internal SyncAuthorityDatabase Db { get; private set; } = null!;
        internal SyncAuthorityRuntimeHost Host { get; private set; } = null!;
        internal DropboxPilotClient Client { get; }
        internal SyncPair Pair { get; private set; } = null!;
        internal SyncRecoveryArtifactProjection Source { get; private set; } = null!;

        private LegacyContext(string root, string name)
        {
            Root = root;
            Name = name;
            Wire = new LegacyTransport();
            Http = new HttpClient(Wire, disposeHandler: false);
            // Reuse the existing fixture's synthetic binding, never a real credential/provider profile.
            var fixtureType = typeof(RecoveryConsumerCorrection).GetNestedType("Fixture", BindingFlags.NonPublic)
                ?? throw new FixtureFailure("Existing legacy fixture type missing.");
            Connection = (DropboxConnectionBinding)(fixtureType.GetField("Connection", BindingFlags.Static | BindingFlags.NonPublic)
                ?.GetValue(null) ?? throw new FixtureFailure("Existing legacy synthetic connection missing."));
            Client = new DropboxPilotClient(Http, new SyntheticReaderToken("fixture-access"), "/Pilot", Connection, "id:pilot-folder");
        }

        internal static async Task<LegacyContext> CreateAsync(string root, string name)
        {
            Directory.CreateDirectory(root);
            var context = new LegacyContext(root, name);
            try
            {
                var localRoot = Path.Combine(root, "local");
                Directory.CreateDirectory(localRoot);
                await File.WriteAllTextAsync(Path.Combine(localRoot, RelativePath), OriginalText);
                context.Wire.Put("/Pilot/" + RelativePath, OriginalText);
                await context.OpenHostAsync();
                await FunctionalTransferModeAsync(context.Host, name);
                var bound = await context.Host.BindPairAsync(new SyncPairBindingRequest(null, name, localRoot,
                    new DropboxFolderEntry("Pilot", "/Pilot", "id:pilot-folder"),
                    new DropboxFolderEntry("Recovery", SyncDropboxArtifactPaths.RecoveryContainerPath, "id:recovery"),
                    Enabled: true, Priority: 0, PropagateDeletions: true, FilesOnDemandEnabled: false,
                    RecoveryRetentionDays: 30, ConnectionId: context.Connection.ConnectionId));
                await context.Host.RefreshBootstrapInventoryAsync(bound.Pair.Id);
                await context.Host.ApproveBootstrapMergeAsync(bound.Pair.Id);
                context.Pair = (await context.Host.SetPairEnabledAsync(bound.Pair.Id, true)).Pairs.Single(row => row.Id == bound.Pair.Id);
                await context.RunPairAsync();
                await File.WriteAllTextAsync(Path.Combine(localRoot, RelativePath), ChangedText);
                await context.RunPairAsync();
                context.Source = (await context.Db.LoadRecoveryArtifactsAsync()).Single(row => row.PairId == context.Pair.Id);
                Check(context.Source.Side == "dropbox" && !SyncAuthorityCloudRecoveryDescriptor.TryParse(context.Source.IdentityJson, out _) &&
                      (await context.Db.LoadOperationAsync(context.Source.OperationId)).Status == SyncAuthorityOperationStatus.Completed,
                    name + ": ordinary public local/Dropbox update produced an actual terminal legacy Recovery artifact");
                await context.RequireBytesAsync(context.Source, OriginalText);
                Check(context.Wire.Read("/Pilot/" + RelativePath) == ChangedText,
                    name + ": current destination contains public producer successor before restore");
                return context;
            }
            catch { context.Dispose(); throw; }
        }

        private async Task OpenHostAsync()
        {
            Instance = PilotInstanceLock.Acquire(Path.Combine(Root, "host-state", "instance.lock"));
            Db = new SyncAuthorityDatabase(Path.Combine(Root, "authority.db"));
            var connections = new DropboxConnectionStore(Instance.StateAuthority);
            if (connections.Load(Connection.ConnectionId) is null) await connections.SaveNewAsync(Connection);
            Host = new SyncAuthorityRuntimeHost(Instance.StateAuthority, Http, new SyncPairStore(Instance.StateAuthority),
                new SyncRuntimeStateStore(Instance.StateAuthority), connections,
                new MemoryDropboxRefreshTokenStore("fixture-refresh"), Db);
            await Host.LoadPairsAsync();
        }

        internal async Task ReopenAsync()
        {
            var installation = await Db.LoadInstallationIdAsync();
            Host.Dispose();
            Instance!.Dispose();
            Instance = null;
            await OpenHostAsync();
            Check(await Db.LoadInstallationIdAsync() == installation &&
                  (await Host.LoadPairsAsync()).Pairs.Any(row => row.Id == Pair.Id && row.LocalRoot == Pair.LocalRoot),
                Name + ": real new Host/SQLite object reopens same installation/pair without provider reseeding");
            Check(Host.LoadTransferSettings().ResourceMode == "Performance",
                Name + ": isolated functional resource mode persists after legacy Host reopen");
        }

        private async Task RunPairAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = await Host.RunPairsAsync([Pair], "manual", cancellationToken: timeout.Token);
            Check(run.Items.Count == 1 && run.Items[0].FailureMessage is null && run.Items[0].Result is { Blocked: 0, Conflicts: 0 },
                Name + ": actual legacy public producer failed: " + run.Items.FirstOrDefault()?.FailureMessage);
        }

        internal async Task<SyncRecoveryCatalogItem> ItemAsync(string key) =>
            (await Host.LoadRecoveryCatalogAsync(Pair.Id)).Single(row => row.Key == key);

        internal async Task RequireBytesAsync(SyncRecoveryArtifactProjection row, string text)
        {
            var expected = LegacyIdentity(row);
            var actual = await Client.GetRecoveryAsync(Pair.RecoveryRoot!, expected.RecoveryPath);
            Check(actual == expected, Name + ": actual legacy artifact identity/revision/path/size/hash remains exact");
            using var bytes = new MemoryStream();
            await Client.DownloadRecoveryAsync(Pair.RecoveryRoot!, actual!, bytes);
            Check(Same(bytes.ToArray(), Encoding.UTF8.GetBytes(text)), Name + ": actual legacy source/preimage bytes remain exact");
        }

        public void Dispose()
        {
            Host?.Dispose();
            Instance?.Dispose();
            Http.Dispose();
            Wire.Dispose();
        }
    }

    private sealed record LegacyMutations(int Uploads, int Deletes, int Copies, int Restores, int Folders);

    // Reuse the existing legacy wire rather than implement another provider model. Reflection is limited
    // to this test-owned private supplier and its synthetic input/read counters; production decisions are
    // all called through public Host/adapter APIs above. Missing members fail closed, not skip coverage.
    private sealed class LegacyTransport : HttpMessageHandler
    {
        private readonly object _store;
        private readonly Type _type;
        private readonly HttpMessageInvoker _invoker;
        internal string? LoseCopyReplyFromId { get; set; }
        internal int LostCopyReplies { get; private set; }
        internal LegacyTransport()
        {
            _type = typeof(RecoveryConsumerCorrection).GetNestedType("WireStore", BindingFlags.NonPublic)
                ?? throw new FixtureFailure("Existing legacy wire missing.");
            _store = Activator.CreateInstance(_type, nonPublic: true)
                ?? throw new FixtureFailure("Existing legacy wire did not construct.");
            _invoker = new HttpMessageInvoker((HttpMessageHandler)_store, disposeHandler: true);
        }
        private object Field(string name) => _type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_store) ?? throw new FixtureFailure("Existing legacy wire field missing: " + name);
        internal LegacyMutations Counters => new(((ICollection)Field("Uploads")).Count,
            (int)Field("Deletes"), (int)Field("Copies"), (int)Field("Restores"), (int)Field("FoldersCreated"));
        private object Call(string name, params object[] arguments) => _type.GetMethod(name,
            BindingFlags.Instance | BindingFlags.NonPublic, null, arguments.Select(value => value.GetType()).ToArray(), null)
            ?.Invoke(_store, arguments) ?? throw new FixtureFailure("Existing legacy wire method missing: " + name);
        internal void Put(string path, string text) => _ = Call("Put", path, text);
        internal string Read(string path) => (string)Call("Read", path);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var lose = false;
            if (LoseCopyReplyFromId is { } source && request.Method == HttpMethod.Post &&
                request.RequestUri?.AbsolutePath.EndsWith("/files/copy_v2", StringComparison.Ordinal) == true)
            {
                using var input = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                lose = input.RootElement.GetProperty("from_path").GetString() == source;
            }
            var response = await _invoker.SendAsync(request, token);
            if (!lose) return response;
            Check(response.IsSuccessStatusCode, "Legacy lost-copy input requires an actually successful provider copy.");
            LoseCopyReplyFromId = null;
            LostCopyReplies++;
            response.Dispose();
            throw new IOException("Synthetic reply loss AFTER exact legacy recovery copy applied.");
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) _invoker.Dispose();
            base.Dispose(disposing);
        }
    }

    private static async Task ProtectedWindowAsync(Context context)
    {
        var item = await ItemAsync(context);
        Check(!item.CanRemove, context.Name + ": newly retained copy is protected by 30-day policy");
        var before = Counters(context);
        await RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(item with { CanRemove = true }),
            context.Name, "protected");
        Check(Counters(context) == before && await ArtifactStatusAsync(context) == "retained",
            context.Name + ": forged presentation flag cannot authorize a cleanup effect");
        await context.ReopenAsync();
        await RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(item with { CanRemove = true }),
            context.Name + " reopened", "protected");
        Check(Counters(context) == before, context.Name + ": persisted protection issues no delete after reopen");
        await RequireRetainedAsync(context, context.Artifact, Encoding.UTF8.GetBytes(OriginalText));
    }

    private static async Task ExpiredReplayAsync(Context context)
    {
        await ExpireAsync(context);
        var item = await ItemAsync(context);
        Check(item.CanRemove, context.Name + ": real catalogue calculates chronological expiration");
        var producer = await JournalAsync(context, context.Artifact.Row.OperationId);
        context.Wire.AllowedRecoveryDeletionIds.Add(context.Artifact.Descriptor.RetainedFileId);
        var deletes = context.Wire.RecoveryDeleteRequests;
        await context.Rig.Host.RemoveRecoveryCopyAsync(item);
        Check(context.Wire.RecoveryDeleteRequests == deletes + 1 && await ArtifactStatusAsync(context) == "released",
            context.Name + ": exactly one expired copy is removed and durably released");
        Check((await RecoveryBytesAsync(context, context.Artifact)).Member is null,
            context.Name + ": provider readback confirms this exact copy absent");
        Check(await JournalAsync(context, context.Artifact.Row.OperationId) == producer,
            context.Name + ": manual removal does not erase producer attempts/receipts/history");
        await context.ReopenAsync();
        await context.Rig.Host.RemoveRecoveryCopyAsync(item);
        Check(context.Wire.RecoveryDeleteRequests == deletes + 1 && await ArtifactStatusAsync(context) == "released",
            context.Name + ": released replay after real reopen issues no second delete");
    }

    private static async Task PendingThenCompletedAsync(Context context)
    {
        await ExpireAsync(context);
        var item = await ItemAsync(context);
        var phases = 0;
        var prior = SyncAuthorityRuntimeHost.CloudRestorePhaseForTest;
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase != "operation-leased-before-stage") return;
            phases++;
            var operation = RestoreOperationAsync(context).GetAwaiter().GetResult();
            Check(operation.Status == "leased" && operation.ArtifactId == context.Artifact.Key,
                context.Name + ": a separate actual restore intent binds this completed producer's artifact");
            Check(CountAsync(context, "SELECT count(*) FROM operation_attempt WHERE operation_id=" + Sql(operation.Id))
                      .GetAwaiter().GetResult() == 0,
                context.Name + ": refusal is challenged before a target attempt/stage is issued");
            var journal = JournalAsync(context, operation.Id).GetAwaiter().GetResult();
            var before = Counters(context);
            RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(item), context.Name, "still required")
                .GetAwaiter().GetResult();
            Check(Counters(context) == before && ArtifactStatusAsync(context).GetAwaiter().GetResult() == "retained" &&
                  JournalAsync(context, operation.Id).GetAwaiter().GetResult() == journal,
                context.Name + ": pending-consumer denial deletes nothing and retains exact operation evidence");
            RequireRetainedAsync(context, context.Artifact, Encoding.UTF8.GetBytes(OriginalText))
                .GetAwaiter().GetResult();
        };
        SyncRecoveryRestoreResult result;
        try { result = await context.Rig.Host.RestoreRecoveryAsync(item, true); }
        finally { SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = prior; }
        Check(phases == 1 && Same(await DestinationBytesAsync(context, result.TargetRelativePath),
                  Encoding.UTF8.GetBytes(OriginalText)),
            context.Name + ": the legitimate restore still succeeds after attempted cleanup denial");
        var completed = await RestoreOperationAsync(context);
        Check(completed.Status == "completed" && await CommandStatusAsync(context, completed.CommandId) == "completed",
            context.Name + ": actual successful restore reaches terminal operation and command");
        await context.ReopenAsync();
        await RemoveExpiredAsync(context);
    }

    private static async Task ReleaseFirstAsync(Context context)
    {
        await ExpireAsync(context);
        var staleItem = await ItemAsync(context);
        _ = await context.Rig.Host.PreviewRecoveryAsync(staleItem, true);
        context.Wire.AllowedRecoveryDeletionIds.Add(context.Artifact.Descriptor.RetainedFileId);
        var calls = 0;
        context.Transport.BeforeRecoveryDeleteAsync = async () =>
        {
            calls++;
            Check(await ArtifactStatusAsync(context) == "release_authorized",
                context.Name + ": actual public cleanup durably authorizes release before its delete request");
            var before = Counters(context);
            var allocations = await CountAsync(context, "SELECT count(*) FROM resource_allocation;");
            var operations = await CountAsync(context, "SELECT count(*) FROM operation WHERE kind='reconcile';");
            await RefusedAsync(() => context.Rig.Host.RestoreRecoveryAsync(staleItem, true), context.Name);
            Check(Counters(context) == before &&
                  await CountAsync(context, "SELECT count(*) FROM resource_allocation;") == allocations &&
                  await CountAsync(context, "SELECT count(*) FROM operation WHERE kind='reconcile';") == operations,
                context.Name + ": release-first stale preview cannot reserve a stage or issue any restore mutation");
            Check(Same(await DestinationBytesAsync(context, RelativePath), Encoding.UTF8.GetBytes(ChangedText)),
                context.Name + ": refused restore preserves the current destination");
        };
        var deletes = context.Wire.RecoveryDeleteRequests;
        await context.Rig.Host.RemoveRecoveryCopyAsync(staleItem);
        Check(calls == 1 && context.Wire.RecoveryDeleteRequests == deletes + 1 &&
              await ArtifactStatusAsync(context) == "released",
            context.Name + ": release-first cleanup itself completes exactly once");
        await context.ReopenAsync();
        var afterRelease = Counters(context);
        await RefusedAsync(() => context.Rig.Host.RestoreRecoveryAsync(staleItem, true), context.Name + " reopened");
        Check(Counters(context) == afterRelease,
            context.Name + ": persisted released-source refusal issues no restore effect");
    }

    private static async Task LostRestoreReplyAsync(Context context)
    {
        await ExpireAsync(context);
        var item = await ItemAsync(context);
        context.Transport.LoseConditionalReply = true;
        var before = Counters(context);
        await RefusedAsync(() => context.Rig.Host.RestoreRecoveryAsync(item, false), context.Name, "unknown");
        Check(context.Transport.LostAppliedReplies == 1 && context.Wire.Updates.Total == before.Updates + 2,
            context.Name + ": provider applied exactly one preimage copy and replacement before reply loss");
        Check(Same(await DestinationBytesAsync(context, RelativePath), Encoding.UTF8.GetBytes(OriginalText)),
            context.Name + ": destination read proves that the lost-response replacement actually applied");
        var operation = await RestoreOperationAsync(context);
        Check((operation.Status is "effect_unknown" or "blocked") &&
              await CommandStatusAsync(context, operation.CommandId) == "blocked" &&
              await CountAsync(context, "SELECT count(*) FROM operation_attempt WHERE operation_id=" + Sql(operation.Id) +
                  " AND status IN ('started','request_sent','response_received','effect_unknown');") >= 1 &&
              await CountAsync(context, "SELECT count(*) FROM effect_receipt r JOIN operation_attempt a " +
                  "ON a.attempt_id=r.attempt_id WHERE a.operation_id=" + Sql(operation.Id) + " AND r.certainty='unknown';") >= 1,
            context.Name + ": lost reply retains actual dispatched uncertainty, never a completed restore");
        var journal = await JournalAsync(context, operation.Id);
        var stagePaths = await ExistingStagePathsAsync(context, operation.Id);
        Check(stagePaths.Length == 2 && stagePaths.All(path => File.Exists(path) || Directory.Exists(path)),
            context.Name + ": unresolved restore retains its exact private stage and transaction");
        await RefuseDependentCleanupAsync(context, item, operation.Id, journal);
        var preimages = (await context.Rig.Db.LoadRecoveryArtifactsAsync()).Where(row => row.OperationId == operation.Id)
            .Select(row => new Artifact(row, ParseDescriptor(row))).ToArray();
        Check(preimages.Length == 1, context.Name + ": unresolved operation owns one actual retained destination preimage");
        await ExpireAsync(context, preimages[0].Key);
        var preimageItem = (await context.Rig.Host.LoadRecoveryCatalogAsync(context.Pair.Id))
            .Single(candidate => candidate.Key == preimages[0].Key);
        var beforePreimageRefusal = Counters(context);
        await RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(preimageItem), context.Name + " preimage");
        Check(Counters(context) == beforePreimageRefusal &&
              await ArtifactStatusAsync(context, preimages[0].Key) == "retained" &&
              await JournalAsync(context, operation.Id) == journal,
            context.Name + ": unresolved producer preimage refusal issues no effect or evidence change");
        await RequireRetainedAsync(context, preimages[0], Encoding.UTF8.GetBytes(ChangedText));
        var mutations = Counters(context);
        await context.ReopenAsync();
        Check(await JournalAsync(context, operation.Id) == journal && stagePaths.All(path => File.Exists(path) || Directory.Exists(path)),
            context.Name + ": persisted unresolved attempts/receipts/history/allocations and stage survive reopening");
        await RefuseDependentCleanupAsync(context, await ItemAsync(context), operation.Id, journal);
        await RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(preimageItem), context.Name + " reopened preimage");
        await RequireRetainedAsync(context, preimages[0], Encoding.UTF8.GetBytes(ChangedText));
        Check(Counters(context) == mutations,
            context.Name + ": reopen and cleanup refusals never reissue replacement, preimage copy or delete");
    }

    private static async Task RefuseDependentCleanupAsync(Context context, SyncRecoveryCatalogItem item,
        string operationId, string journal)
    {
        var before = Counters(context);
        await RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(item), context.Name, "still required");
        Check(Counters(context) == before && await ArtifactStatusAsync(context) == "retained" &&
              await JournalAsync(context, operationId) == journal,
            context.Name + ": source cleanup denial retains the exact unresolved dependency and all effects");
        await RequireRetainedAsync(context, context.Artifact, Encoding.UTF8.GetBytes(OriginalText));
    }

    private static async Task LostCleanupReplyAsync(Context context)
    {
        await ExpireAsync(context);
        var item = await ItemAsync(context);
        context.Wire.AllowedRecoveryDeletionIds.Add(context.Artifact.Descriptor.RetainedFileId);
        context.Wire.LoseNextRecoveryDeleteResponse = true;
        var deletes = context.Wire.RecoveryDeleteRequests;
        await RefusedAsync(() => context.Rig.Host.RemoveRecoveryCopyAsync(item), context.Name);
        Check(context.Wire.RecoveryDeleteRequests == deletes + 1 && await ArtifactStatusAsync(context) == "release_authorized" &&
              (await RecoveryBytesAsync(context, context.Artifact)).Member is null,
            context.Name + ": lost cleanup reply retains authorization, not a false released receipt");
        await context.ReopenAsync();
        var mutations = Counters(context);
        await RefusedAsync(() => context.Rig.Host.RestoreRecoveryAsync(item, true), context.Name + " authorized source");
        Check(Counters(context) == mutations,
            context.Name + ": unresolved release refuses stale restore before any effect");
        await context.Rig.Host.RemoveRecoveryCopyAsync(item);
        await context.Rig.Host.RemoveRecoveryCopyAsync(item);
        Check(Counters(context) == mutations && await ArtifactStatusAsync(context) == "released",
            context.Name + ": persisted cleanup retry verifies absence and released replay performs no second effect");
    }

    private static async Task NoEffectSupersedeAsync(Context context)
    {
        await ExpireAsync(context);
        using var cancellation = new CancellationTokenSource();
        var phases = 0;
        var before = Counters(context);
        var prior = SyncAuthorityRuntimeHost.CloudRestorePhaseForTest;
        SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = phase =>
        {
            if (phase != "operation-leased-before-stage") return;
            phases++;
            cancellation.Cancel();
        };
        try
        {
            try
            {
                await context.Rig.Host.RestoreRecoveryAsync(await ItemAsync(context), true, cancellation.Token);
                throw new FixtureFailure("Expected the real pre-dispatch cancellation.");
            }
            catch (OperationCanceledException) { Check(true, context.Name + ": cancellation remains the surfaced failure"); }
        }
        finally { SyncAuthorityRuntimeHost.CloudRestorePhaseForTest = prior; }
        var operation = await RestoreOperationAsync(context);
        Check(phases == 1 && Counters(context) == before && operation.Status == "superseded" &&
              await CommandStatusAsync(context, operation.CommandId) == "blocked" &&
              await CountAsync(context, "SELECT count(*) FROM operation_attempt WHERE operation_id=" + Sql(operation.Id)) == 0 &&
              await CountAsync(context, "SELECT count(*) FROM history_event WHERE operation_id=" + Sql(operation.Id) +
                  " AND event_kind='manual_restore_no_effect_superseded';") == 1,
            context.Name + ": genuine zero-attempt exact-cleanup abandonment is retained, not invented completion");
        var journal = await JournalAsync(context, operation.Id);
        await context.ReopenAsync();
        Check(await JournalAsync(context, operation.Id) == journal,
            context.Name + ": the same safe terminal evidence survives actual reopen");
        await RemoveExpiredAsync(context);
        Check(await JournalAsync(context, operation.Id) == journal,
            context.Name + ": safe no-effect dependency does not impose a permanent cleanup hold or erase history");
    }

    private static async Task RemoveExpiredAsync(Context context)
    {
        context.Wire.AllowedRecoveryDeletionIds.Add(context.Artifact.Descriptor.RetainedFileId);
        var deletes = context.Wire.RecoveryDeleteRequests;
        await context.Rig.Host.RemoveRecoveryCopyAsync(await ItemAsync(context));
        Check(context.Wire.RecoveryDeleteRequests == deletes + 1 && await ArtifactStatusAsync(context) == "released" &&
              (await RecoveryBytesAsync(context, context.Artifact)).Member is null,
            context.Name + ": a resolved dependency permits exactly one legitimate expired-copy removal");
    }

    private static SyncAuthorityCloudRecoveryDescriptor ParseDescriptor(SyncRecoveryArtifactProjection row) =>
        SyncAuthorityCloudRecoveryDescriptor.TryParse(row.IdentityJson, out var descriptor) ? descriptor!
            : throw new FixtureFailure("Expected a typed artifact from the actual cloud update/restore producer.");

    private static async Task<(SyncArchiveResult? Member, byte[] Bytes)> RecoveryBytesAsync(Context context, Artifact artifact)
    {
        var descriptor = artifact.Descriptor;
        var binding = new SyncRemoteRecoveryRootBinding(descriptor.RecoveryPath, descriptor.RecoveryFolderId,
            descriptor.RecoveryNamespaceId);
        var member = await context.Reader.GetRecoveryAsync(binding, Path.GetFileName(descriptor.RetainedPath));
        if (member is null) return (null, []);
        using var bytes = new MemoryStream();
        await context.Reader.DownloadRecoveryAsync(binding, member, bytes);
        return (member, bytes.ToArray());
    }

    private static async Task RequireRetainedAsync(Context context, Artifact artifact, byte[] expected)
    {
        var retained = await RecoveryBytesAsync(context, artifact);
        Check(retained.Member is { } member && member.FileId == artifact.Descriptor.RetainedFileId &&
              member.Revision == artifact.Descriptor.RetainedRevision && member.Size == artifact.Descriptor.Size &&
              member.ContentHash == artifact.Descriptor.ContentHash && Same(retained.Bytes, expected),
            context.Name + ": actual adapter read retains exact Recovery identity, revision, digest and bytes");
    }

    private static async Task<byte[]> DestinationBytesAsync(Context context, string path)
    {
        var entry = (await context.Reader.ListEntriesAsync()).Single(item => item.RelativePath == path && item.Kind == PilotEntryKind.File);
        using var bytes = new MemoryStream();
        await context.Reader.DownloadAsync(entry, bytes);
        return bytes.ToArray();
    }

    private static async Task<(string Id, string CommandId, string ArtifactId, string Status)> RestoreOperationAsync(Context context)
    {
        var rows = await context.Rig.RowsAsync("SELECT operation_id,json_extract(preconditions_json,'$.restoreCommandId')," +
            "json_extract(preconditions_json,'$.recoveryArtifactId'),status FROM operation WHERE kind='reconcile' " +
            "AND json_extract(preconditions_json,'$.restoreKind')='cloud-recovery-restore' ORDER BY rowid DESC LIMIT 1;");
        var parts = rows.Single().Split('|');
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    private static Task<string> CommandStatusAsync(Context context, string commandId) => context.Rig.ScalarAsync(
        "SELECT status FROM command_request WHERE command_request_id=" + Sql(commandId) + ";");

    private static async Task<long> CountAsync(Context context, string sql) =>
        long.Parse(await context.Rig.ScalarAsync(sql), CultureInfo.InvariantCulture);

    private static async Task<string> JournalAsync(Context context, string operationId)
    {
        var id = Sql(operationId);
        var rows = new List<string>();
        foreach (var query in new[]
        {
            "SELECT * FROM operation WHERE operation_id=" + id + " ORDER BY operation_id;",
            "SELECT * FROM operation_member WHERE operation_id=" + id + " ORDER BY operation_member_id;",
            "SELECT * FROM operation_attempt WHERE operation_id=" + id + " ORDER BY attempt_id;",
            "SELECT r.* FROM effect_receipt r JOIN operation_attempt a ON a.attempt_id=r.attempt_id WHERE a.operation_id=" + id + " ORDER BY effect_receipt_id;",
            "SELECT * FROM resource_allocation WHERE operation_id=" + id + " ORDER BY allocation_id;",
            "SELECT * FROM history_event WHERE operation_id=" + id + " ORDER BY history_event_id;"
        })
        {
            rows.Add(query);
            rows.AddRange(await context.Rig.RowsAsync(query));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))));
    }

    private static async Task<string[]> ExistingStagePathsAsync(Context context, string operationId)
    {
        var operation = await context.Rig.Db.LoadOperationAsync(operationId);
        var transaction = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.TransactionDirectory);
        var stage = operation.Allocations.Single(item => item.Kind == SyncAuthorityAllocationKind.StageFile);
        // Private control roots are siblings of the selected state directory,
        // never children of it. Derive the exact registered namespace through
        // the production path policy instead of inventing a second layout.
        var root = Path.Combine(SyncControlNamespace.LocalRootFor(context.Rig.StateRoot,
            transaction.ControlNamespaceId), transaction.DeterministicName);
        return [root, Path.Combine(root, stage.DeterministicName)];
    }

    private sealed record MutationCounters(int Creates, int Updates, int Deletes);
    private static MutationCounters Counters(Context context) =>
        new(context.Wire.Writes.Total, context.Wire.Updates.Total, context.Wire.RecoveryDeleteRequests);
    private static string Sql(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static bool Same(byte[] first, byte[] second) => first.AsSpan().SequenceEqual(second);
    private sealed class FixtureFailure(string message) : Exception(message);
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new FixtureFailure(label);
        _checks++;
    }
    private static async Task RefusedAsync(Func<Task> action, string label, string? messageContains = null)
    {
        try { await action(); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or IOException or
                                      HttpRequestException or NotSupportedException)
        {
            Check(messageContains is null || error.Message.Contains(messageContains, StringComparison.OrdinalIgnoreCase),
                label + ": refusal was not at the required boundary: " + error.Message);
            return;
        }
        throw new FixtureFailure("Expected a real public refusal: " + label);
    }

    // Only provider response/input scheduling lives here. The actual Host, DB admission, exact recovery
    // verification, adapter guard, mutation and reconciliation remain production code. A miss fails the
    // LostAppliedReplies/BeforeRecoveryDelete assertions, so an unexercised seam cannot become a PASS.
    private sealed class BoundaryTransport(Wire wire) : HttpMessageHandler
    {
        internal string? RecoveryFileId { get; set; }
        internal Func<Task>? BeforeRecoveryDeleteAsync { get; set; }
        internal bool LoseConditionalReply { get; set; }
        internal int LostAppliedReplies { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? throw new FixtureFailure("Missing fixture request URI.");
            if (BeforeRecoveryDeleteAsync is { } callback && await IsRecoveryDeleteAsync(request, path, cancellationToken))
            {
                BeforeRecoveryDeleteAsync = null;
                await callback();
            }
            var lose = LoseConditionalReply && IsConditionalReplacement(request, path);
            var response = await wire.SendDirectAsync(request);
            if (!lose) return response;
            Check(response.IsSuccessStatusCode, "Lost-reply input requires an actually applied successful conditional upload.");
            LoseConditionalReply = false;
            LostAppliedReplies++;
            response.Dispose();
            throw new HttpRequestException("Synthetic response loss AFTER this conditional replacement applied.");
        }
        private async Task<bool> IsRecoveryDeleteAsync(HttpRequestMessage request, string path, CancellationToken token)
        {
            if (RecoveryFileId is null) return false;
            if (request.Method == HttpMethod.Patch && path.StartsWith("/drive/v2/files/", StringComparison.Ordinal))
                return Uri.UnescapeDataString(path["/drive/v2/files/".Length..]) == RecoveryFileId;
            if (request.Method != HttpMethod.Post || !path.EndsWith("/files/delete_v2", StringComparison.Ordinal)) return false;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            return body.RootElement.GetProperty("path").GetString() == RecoveryFileId;
        }
        private static bool IsConditionalReplacement(HttpRequestMessage request, string path)
        {
            if (request.Method == HttpMethod.Put && path.StartsWith("/upload/drive/v2/files/", StringComparison.Ordinal)) return true;
            if (request.Method != HttpMethod.Post || !path.EndsWith("/files/upload", StringComparison.Ordinal) ||
                !request.Headers.TryGetValues("Dropbox-API-Arg", out var values)) return false;
            using var body = JsonDocument.Parse(values.Single());
            var root = body.RootElement;
            return root.GetProperty("path").GetString()?.EndsWith("/" + RelativePath, StringComparison.Ordinal) == true &&
                   root.GetProperty("mode").ValueKind == JsonValueKind.Object &&
                   root.GetProperty("mode").GetProperty(".tag").GetString() == "update";
        }
    }
}
