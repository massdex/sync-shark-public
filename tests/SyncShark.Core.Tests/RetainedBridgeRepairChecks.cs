using System.Security.Cryptography;
using System.Diagnostics;
using System.Text.Json;
using Massdex.SyncShark;
using Microsoft.Data.Sqlite;

// Read-only consistent backup of the explicitly supplied incident database. All
// production observation/planning calls below operate on a second isolated copy.
// No executor, provider adapter, credential store or user folder is opened.
internal static class RetainedBridgeRepairChecks
{
    internal static async Task RunAsync(string source, string root)
    {
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TestData", "Projects", "sync-shark", "build") + Path.DirectorySeparatorChar;
        root = Path.GetFullPath(root);
        if (!root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || Directory.Exists(root))
            throw new InvalidOperationException("This proof requires a fresh isolated Sync Shark build fixture.");
        Directory.CreateDirectory(root);
        var backup = Path.Combine(root, "source-backup.db");
        var candidate = Path.Combine(root, "candidate-state.db");
        await using (var original = Connection(source, true))
        await using (var destination = Connection(backup, false))
        {
            await original.OpenAsync();
            await destination.OpenAsync();
            original.BackupDatabase(destination);
        }
        var backupHash = Hash(backup);
        File.Copy(backup, candidate, false);
        var db = new SyncAuthorityDatabase(candidate);
        var pair = (await db.LoadPairSetAsync(readOnly: true)).Pairs.Single(item => item.Id == "f00d0000000000000007");
        var oldOperationId = await Scalar(candidate,
            "SELECT operation_id FROM operation WHERE pair_id=$pair AND status='ready' AND kind='create_local' LIMIT 1;", pair.Id);
        var old = await db.LoadOperationAsync(oldOperationId) ?? throw new InvalidDataException("Missing retained operation.");
        var groupId = await Scalar(candidate, "SELECT operation_group_id FROM operation WHERE operation_id=$pair;", oldOperationId);
        var oldOperations = await Count(candidate, "SELECT count(*) FROM operation WHERE pair_id=$pair;", pair.Id);
        var oldAttempts = await Count(candidate, "SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair;", pair.Id);
        var groupAttempts = await Count(candidate,
            "SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.operation_group_id=$pair;", groupId);
        Require(groupAttempts == 0, "The captured Child sync group has no issued file attempt; historical setup attempts remain separately preserved.");
        await db.SuspendOperationGroupForPlanningAsync(groupId);
        var originalRows = new Dictionary<string, IReadOnlyList<SyncAuthorityObservedEntry>>();
        foreach (var side in new[] { "local", "dropbox" })
        {
            var entries = await db.LoadLatestCompleteObservationAsync(pair.Id, side);
            originalRows.Add(side, entries);
            // Input preparation is not the production scan: load the retained identities
            // once so thousands of fixture-only connection opens cannot hide scan timing.
            var identities = new Dictionary<string, string>();
            await using (var read = Connection(candidate, true))
            {
                await read.OpenAsync();
                await using var command = read.CreateCommand();
                command.CommandText = "SELECT v.object_version_id,v.identity_json FROM observation o JOIN object_version v ON v.object_version_id=o.object_version_id WHERE o.scan_generation_id=(SELECT scan_generation_id FROM scan_generation WHERE pair_id=$pair AND side=$side AND status='complete' ORDER BY generation DESC LIMIT 1);";
                command.Parameters.AddWithValue("$pair", pair.Id);
                command.Parameters.AddWithValue("$side", side);
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) identities[reader.GetString(0)] = reader.GetString(1);
            }
            var inputs = new List<SyncAuthorityObservationInput>();
            foreach (var entry in entries)
            {
                inputs.Add(new(entry.RelativePath, entry.ObjectKind, identities[entry.ObjectVersionId],
                    entry.ContentHash, entry.Size, entry.EvidenceJson));
            }
            var binding = await db.LoadActiveRootBindingIdAsync(pair.Id, side);
            var scan = await db.BeginScanGenerationAsync(pair.Id, binding, side, "manual", "{}");
            var observationTimer = Stopwatch.StartNew();
            await db.AppendObservationEntriesAsync(scan, inputs);
            Console.WriteLine($"MEASURE {side}: append finished in {observationTimer.ElapsedMilliseconds} ms");
            var completionTimer = Stopwatch.StartNew();
            await db.CompleteScanGenerationAsync(scan);
            Console.WriteLine($"MEASURE {side}: publication finished in {completionTimer.ElapsedMilliseconds} ms");
            Console.WriteLine($"MEASURE {side}: {inputs.Count} observed records saved in {observationTimer.ElapsedMilliseconds} ms");
            var fresh = await db.LoadLatestCompleteObservationAsync(pair.Id, side);
            Require(fresh.Count == entries.Count && fresh.Select(item => item.ObjectId).Distinct().Count() == fresh.Count,
                side + ": a fresh real inventory separates every retained collision without losing a path.");
        }
        var localControl = await Scalar(candidate,
            "SELECT control_namespace_id FROM pair_control_namespace WHERE pair_id=$pair AND side='local' AND status='active' ORDER BY binding_generation DESC LIMIT 1;", pair.Id);
        var remoteControl = await Scalar(candidate,
            "SELECT recovery_root_binding_id FROM recovery_root_binding WHERE json_extract(identity_json,'$.folderId')=$pair AND side='dropbox' AND status='active';",
            pair.RecoveryRoot?.RootFolderId ?? throw new InvalidDataException("The captured pair lacks its retained recovery root."));
        var reviewedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var selected = Connection(candidate, true))
        {
            await selected.OpenAsync();
            await using var command = selected.CreateCommand();
            command.CommandText = "SELECT DISTINCT m.expected_path_key FROM operation_member m JOIN operation o ON o.operation_id=m.operation_id WHERE o.operation_group_id=$group AND m.expected_path_key IS NOT NULL;";
            command.Parameters.AddWithValue("$group", groupId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) reviewedPaths.Add(reader.GetString(0));
        }
        Require(reviewedPaths.Count > 0 && localControl.Length > 0 && remoteControl.Length > 0,
            "The copied plan binds the retained local control, provider recovery authority and original reviewed paths.");
        var admission = await db.CreateAcceptedCommandAdmissionAsync(null, "run_pair", pair.Id,
            null, null, "{}", SyncAuthorityCommandPurpose.NormalEffect);
        await db.TransitionCommandRequestAsync(admission.CommandRequestId, "executing");
        var planningTimer = Stopwatch.StartNew();
        var outcomes = await new SyncAuthorityReconciliationPlanner(db,
            new(pair.PolicyRevision, old.CapabilityProfileId, localControl, remoteControl,
                EnableReplacementEffects: true, EnableDeletionEffects: pair.PropagateDeletions,
                EnableMoveEffects: true, Direction: SyncEndpointEngineLayout.EngineDirection(pair)),
            effectPathFilter: reviewedPaths, operationGroupId: groupId, admission: admission).PlanAsync(pair.Id);
        Console.WriteLine($"MEASURE Child: {outcomes.Count} decisions planned in {planningTimer.ElapsedMilliseconds} ms");
        Require(outcomes.Count > 0 && outcomes.Any(item => item.OperationId is not null),
            "The real planner passes the formerly failing identity boundary and creates fresh bounded work.");
        Require(await Count(candidate, "SELECT count(*) FROM operation WHERE pair_id=$pair;", pair.Id) >= oldOperations &&
                await Count(candidate, "SELECT count(*) FROM operation_attempt a JOIN operation o ON o.operation_id=a.operation_id WHERE o.pair_id=$pair;", pair.Id) == oldAttempts,
            "Planning preserves all old operations and dispatches no file/provider effect.");
        await using (var proof = Connection(candidate, true))
        {
            await proof.OpenAsync();
            await using var attach = proof.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $backup AS original;";
            attach.Parameters.AddWithValue("$backup", backup);
            await attach.ExecuteNonQueryAsync();
            foreach (var table in new[] { "object_version", "membership", "observation", "operation_member", "operation_attempt", "command_request" })
            {
                await using var check = proof.CreateCommand();
                check.CommandText = $"SELECT count(*) FROM (SELECT * FROM original.{table} EXCEPT SELECT * FROM main.{table});";
                Require(Convert.ToInt64(await check.ExecuteScalarAsync()) == 0, table + ": every original history row remains byte-for-byte unchanged.");
            }
        }
        Require(Hash(backup) == backupHash, "The consistent source backup is unchanged by candidate planning.");
        Console.WriteLine(JsonSerializer.Serialize(new { result = "PASS", backup, backupSha256 = backupHash,
            candidate, candidateSha256 = Hash(candidate), oldOperations, oldAttempts,
            localEntries = originalRows["local"].Count, remoteEntries = originalRows["dropbox"].Count,
            planned = outcomes.Count(item => item.OperationId is not null),
            planningMilliseconds = planningTimer.ElapsedMilliseconds,
            blocked = outcomes.Count(item => item.Kind == SyncAuthorityPlannerOutcomeKind.Blocked),
            sourceMutation = false, providerEffects = false }));
    }

    private static SqliteConnection Connection(string path, bool readOnly) => new(new SqliteConnectionStringBuilder
    { DataSource = Path.GetFullPath(path), Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
    private static async Task<string> Scalar(string path, string sql, string pair)
    {
        await using var connection = Connection(path, true);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$pair", pair);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }
    private static async Task<long> Count(string path, string sql, string pair) => long.Parse(await Scalar(path, sql, pair));
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(); }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidDataException(message); Console.WriteLine("PASS " + message); }
}
