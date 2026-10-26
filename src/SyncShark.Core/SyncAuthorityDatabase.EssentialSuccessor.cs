using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed partial class SyncAuthorityDatabase
{
    internal static Action<string>? EssentialSuccessorBoundaryForTest { get; set; }
    private sealed record EssentialSourceSchema(int Version);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SqliteConnection,EssentialSourceSchema> EssentialSourceSchemas=new();
    private static bool EssentialLegacySource(SqliteConnection c)=>
        EssentialSourceSchemas.TryGetValue(c,out var schema)&&schema.Version==7;
    private static bool EssentialSourceHasTable(SqliteConnection c,string name)=>
        !EssentialLegacySource(c)||ExpectedVersion7SchemaObjects.Contains("table:"+name,StringComparer.Ordinal);
    private static readonly string EssentialLegacyPoints=BuildEssentialLegacyPoints();
    private static string BuildEssentialLegacyPoints()
    {
        var sql=SyncCurrentBaselineSchema.Sql;
        sql=sql[sql.IndexOf("SELECT s.synchronization_point_id",StringComparison.Ordinal)..].Trim().TrimEnd(';');
        return System.Text.RegularExpressions.Regex.Replace(sql,@"\s+INDEXED BY\s+[A-Za-z_][A-Za-z0-9_]*","");
    }
    private static string EssentialSourceSql(SqliteConnection c,string sql)
    {
        sql=ExpandEssentialReviewedLineageSql(ExpandEssentialLegacyOriginalsSql(ExpandEssentialCopySql(c,ExpandEssentialGenerationSql(c,ExpandEssentialHistorySql(c,sql)))));
        return !EssentialLegacySource(c)?sql:EssentialJournalLegacySql(sql.Replace("effective_observation","observation",StringComparison.Ordinal)
            .Replace("current_synchronization_point","("+EssentialLegacyPoints+")",StringComparison.Ordinal)
            .Replace("FROM current_copy_completion","FROM ("+EssentialEmptyCopyReferences+")",StringComparison.Ordinal)
            .Replace("FROM copy_predecessor","FROM ("+EssentialEmptyCopyReferences+")",StringComparison.Ordinal)
            .Replace("FROM monitor_event","FROM (SELECT "+EssentialMonitorEventLegacyColumns+
                ",NULL AS batch_epoch,NULL AS batch_sequence,NULL AS hint_sequence,'legacy' AS lifetime_class,0 AS encoded_bytes FROM monitor_event)",StringComparison.Ordinal)
            .Replace("FROM dirty_item","FROM (SELECT "+EssentialDirtyLegacyColumns+
                ",0 AS latest_hint_sequence,NULL AS closed_utc FROM dirty_item)",StringComparison.Ordinal));
    }
    private static Task<long> EssentialHistoryPolicyAsync(SqliteConnection c,SqliteTransaction? tx,CancellationToken token)=>
        EssentialLegacySource(c)?Task.FromResult(0L):
            ScalarLongAsync(c,tx,"SELECT history_retention_enabled FROM installation;",token);

    private sealed class EssentialSourceGuard : IDisposable
    {
        internal readonly PilotDirectoryLease Parent;
        internal readonly SyncLocalFileAuthority File;
        internal readonly string Path;
        private readonly List<(string Path,SyncLocalFileAuthority File,PilotDirectoryIdentity Id,long Bytes,string Hash)> _sidecars=[];
        internal string SidecarBindingSha256 => _sidecars.Count==0 ? "" :
            Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(_sidecars.Select(x=>
                new { x.Path,x.Id.VolumeSerialNumber,x.Id.FileId,x.Bytes,x.Hash })))).ToLowerInvariant();
        internal EssentialSourceGuard(string path, SyncEssentialSuccessorOptions options)
        {
            Path = RequireEssentialFixturePath(path, options);
            Parent = PilotDirectoryLease.OpenProductDirectoryReadGuard(System.IO.Path.GetDirectoryName(Path)!);
            try
            {
                // ShareRead denies an existing/future writer or rename for the
                // entire identity/hash/read-transaction lifetime.
                File = SyncLocalFileAuthority.OpenExistingReadGuard(Path);
                File.VerifySingleLink();
                if (!string.Equals(File.FinalPath, Path, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Essential source final path differs from its bound path.");
                Span<byte> header = stackalloc byte[100];
                File.Stream.ReadExactly(header);
                if (!header[..16].SequenceEqual("SQLite format 3\0"u8))
                    throw new InvalidDataException("Essential source is not an SQLite database.");
                if(header[18]==1 && header[19]==1) RequireNoEssentialSidecars(Path);
                else if(header[18]==2 && header[19]==2)
                {
                    // Read the ordinary SQLite WAL path, never immutable=1.
                    // All three existing files stay under no-write/no-delete
                    // guards. Only the observed fully checkpointed zero-WAL
                    // snapshot shape is admitted by this adapter.
                    if(System.IO.File.Exists(Path+"-journal") || System.IO.Directory.Exists(Path+"-journal"))
                        throw new IOException("WAL source also has a rollback journal.");
                    foreach(var suffix in new[]{"-wal","-shm"})
                    {
                        var sidecar=SyncLocalFileAuthority.OpenExistingReadGuard(Path+suffix);
                        try
                        {
                            var id=sidecar.VerifySingleLink();
                            if(!string.Equals(sidecar.FinalPath,Path+suffix,StringComparison.OrdinalIgnoreCase) ||
                                (suffix=="-wal" && sidecar.Stream.Length!=0) ||
                                (suffix=="-shm" && sidecar.Stream.Length!=32768))
                                throw new InvalidDataException("Source WAL snapshot is not the exact fully checkpointed shape.");
                            var digest=Convert.ToHexString(SHA256.HashData(sidecar.Stream)).ToLowerInvariant();
                            _sidecars.Add((Path+suffix,sidecar,id,sidecar.Stream.Length,digest));
                        }
                        catch{sidecar.Dispose();throw;}
                    }
                }
                else throw new InvalidDataException("Essential source has an unsupported journal format.");
                File.Stream.Position = 0;
                Parent.VerifyBinding();
            }
            catch { foreach(var sidecar in _sidecars)sidecar.File.Dispose(); File?.Dispose(); Parent.Dispose(); throw; }
        }
        internal void Verify()
        {
            File.VerifySingleLink(); Parent.VerifyBinding();
            if(_sidecars.Count==0) RequireNoEssentialSidecars(Path);
            else
            {
                foreach(var sidecar in _sidecars)
                {
                    sidecar.File.VerifySameSingleLink(sidecar.Id);
                    sidecar.File.Stream.Position=0;
                    if(sidecar.File.Stream.Length!=sidecar.Bytes ||
                        !string.Equals(sidecar.File.FinalPath,sidecar.Path,StringComparison.OrdinalIgnoreCase) ||
                        Convert.ToHexString(SHA256.HashData(sidecar.File.Stream)).ToLowerInvariant()!=sidecar.Hash)
                        throw new IOException("Essential source sidecar identity/bytes changed.");
                }
                if(System.IO.File.Exists(Path+"-journal") || System.IO.Directory.Exists(Path+"-journal"))
                    throw new IOException("A journal appeared on the guarded source.");
            }
            if (!string.Equals(File.FinalPath, Path, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Essential source binding changed.");
        }
        public void Dispose() { foreach(var sidecar in _sidecars)sidecar.File.Dispose(); File.Dispose(); Parent.Dispose(); }
    }

    private static string RequireEssentialFixturePath(string path, SyncEssentialSuccessorOptions options)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.IsolatedRoot));
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root) ||
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(full, DefaultDatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Essential preparation requires an existing isolated owner fixture root.");
        for (var current = Path.GetDirectoryName(full); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Essential preparation refuses a reparse parent.");
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return full;
    }

    private static void RequireNoEssentialSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix) || Directory.Exists(path + suffix))
                throw new IOException("Offline successor source/target has a retained SQLite sidecar.");
    }

    private static async Task<SqliteConnection> OpenEssentialSourceAsync(EssentialSourceGuard source,
        CancellationToken token)
    {
        EnsureSqliteBootstrap();
        var c = new SqliteConnection(ConnectionString(source.Path, SqliteOpenMode.ReadOnly, false));
        try
        {
            await c.OpenAsync(token);
            await ExecuteAsync(c, null, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF; PRAGMA foreign_keys=ON;", token);
            if (SQLitePCL.raw.sqlite3_db_readonly(c.Handle, "main") != 1)
                throw new InvalidDataException("Essential source is not read-only.");
            var version = await ScalarLongAsync(c, null, "PRAGMA user_version;", token);
            if (version is not (7 or 17 or 18))
                throw new InvalidDataException("This essential source adapter supports exact schemas7/17/18 only.");
            await RequireExactSchemaAsync(c, (int)version,
                version==7 ? ExpectedVersion7SchemaObjects :
                version == 17 ? ExpectedVersion17SchemaObjects : ExpectedCurrentSchemaObjects, token);
            EssentialSourceSchemas.Add(c,new((int)version));
            if (version == 18) await RequireCompletedEssentialSuccessorAsync(c, token);
            source.Verify();
            return c;
        }
        catch { await c.DisposeAsync(); throw; }
    }

    internal static async Task<SyncEssentialSourceIdentity> InspectEssentialSourceAsync(string path,
        SyncEssentialSuccessorOptions options, CancellationToken token)
    {
        using var guard = new EssentialSourceGuard(path, options);
        await using var source = await OpenEssentialSourceAsync(guard, token);
        return await ReadEssentialSourceIdentityAsync(guard, source, token);
    }

    private static async Task<SyncEssentialSourceIdentity> ReadEssentialSourceIdentityAsync(
        EssentialSourceGuard guard, SqliteConnection source, CancellationToken token)
    {
        var id = guard.File.VerifySingleLink();
        guard.File.Stream.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(guard.File.Stream, token)).ToLowerInvariant();
        await using var command = source.CreateCommand();
        command.CommandText = "SELECT installation_id,database_generation FROM installation LIMIT 2;";
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) throw new InvalidDataException("Essential source has no installation.");
        var installation = reader.GetString(0); var generation = reader.GetString(1);
        if (await reader.ReadAsync(token)) throw new InvalidDataException("Essential source has multiple installations.");
        await reader.DisposeAsync();
        guard.Verify();
        return new(guard.Path, id.VolumeSerialNumber, id.FileId, guard.File.Stream.Length, hash,
            checked((int)await ScalarLongAsync(source, null, "PRAGMA user_version;", token)), installation, generation,
            guard.SidecarBindingSha256);
    }

    private static string EssentialOptionsHash(SyncEssentialSuccessorOptions options) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.IsolatedRoot)).ToUpperInvariant()))).ToLowerInvariant();

    private static async Task RequireCompletedEssentialSuccessorAsync(SqliteConnection connection,
        CancellationToken token)
    {
        var json = await ScalarStringAsync(connection, null,
            "SELECT essential_successor_json FROM installation LIMIT 1;", token);
        if (json is null) return;
        var receipt = JsonSerializer.Deserialize<SyncEssentialSuccessorReceipt>(json)
            ?? throw new InvalidDataException("Essential successor receipt is unreadable.");
        var generation = await ScalarStringAsync(connection, null,
            "SELECT database_generation FROM installation LIMIT 1;", token);
        if (!receipt.Complete || receipt.TargetGeneration != generation)
            throw new InvalidDataException("Essential successor preparation is incomplete; ordinary runtime access is refused.");
    }

    private static async Task<SyncEssentialSuccessorReceipt> ReadEssentialReceiptAsync(SqliteConnection target,
        CancellationToken token)
    {
        var json = await ScalarStringAsync(target, null,
            "SELECT essential_successor_json FROM installation LIMIT 1;", token);
        return json is null ? throw new InvalidDataException("Target has no source-bound essential preparation receipt.")
            : JsonSerializer.Deserialize<SyncEssentialSuccessorReceipt>(json)
              ?? throw new InvalidDataException("Target essential receipt is unreadable.");
    }

    private static void BindEssentialReceipt(SyncEssentialSuccessorReceipt receipt,
        SyncEssentialSourceIdentity source, SyncEssentialSuccessorOptions options, string generation)
    {
        if (receipt.Source != source || receipt.OptionsSha256 != EssentialOptionsHash(options) ||
            receipt.TargetGeneration != generation || generation == source.DatabaseGeneration)
            throw new InvalidDataException("Essential successor source/options/generation binding does not match.");
    }

    private static async Task SetEssentialReceiptAsync(SqliteConnection target, SqliteTransaction transaction,
        SyncEssentialSuccessorReceipt oldReceipt, SyncEssentialSuccessorReceipt next, CancellationToken token)
    {
        var oldJson = JsonSerializer.Serialize(oldReceipt); var newJson = JsonSerializer.Serialize(next);
        var permitted = true;
        var result = SQLitePCL.raw.sqlite3_create_function(target.Handle!, "syncshark_successor_receipt_allowed", 2,
            SQLitePCL.raw.SQLITE_UTF8 | 0x00200000, null, (context, state, values) =>
            {
                var exact = permitted && values.Length == 2 &&
                    SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string() == oldJson &&
                    SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string() == newJson;
                SQLitePCL.raw.sqlite3_result_int(context, exact ? 1 : 0);
            });
        if (result != SQLitePCL.raw.SQLITE_OK) throw new InvalidDataException("Successor receipt permit registration failed.");
        try
        {
            await ExecuteAsync(target, transaction,
                "UPDATE installation SET essential_successor_json=$next WHERE essential_successor_json=$old;",
                token, ("$next", newJson), ("$old", oldJson));
            if (await ScalarLongAsync(target, transaction, "SELECT changes();", token) != 1)
                throw new InvalidDataException("Essential receipt changed during preparation.");
        }
        finally { permitted = false; }
    }

    internal static async Task<SyncEssentialSuccessorReport> PrepareEssentialSuccessorCoreAsync(
        string sourcePath, string targetPath, SyncEssentialSourceIdentity expected, SyncEssentialSuccessorOptions options,
        IProgress<string>? progress, CancellationToken token)
    {
        using var guard = new EssentialSourceGuard(sourcePath, options);
        var targetFull = RequireEssentialFixturePath(targetPath, options);
        if (string.Equals(guard.Path, targetFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Essential successor source and target are identical.");
        using var targetParent = PilotDirectoryLease.OpenProductDirectoryReadGuard(Path.GetDirectoryName(targetFull)!);
        RequireNoEssentialSidecars(targetFull);
        await using var source = await OpenEssentialSourceAsync(guard, token);
        var actual = await ReadEssentialSourceIdentityAsync(guard, source, token);
        if (actual != expected) throw new InvalidDataException("Essential source identity/bytes changed.");
        var receipt=await InitialEssentialReceiptAsync(targetFull,actual,options,token);
        BindEssentialHistorySelection(source,receipt);
        var selectionTime=receipt.HistoryAsOfUtc;
        await using var snapshot = source.BeginTransaction(deferred: true);
        var dispositions = await InspectEssentialDispositionsAsync(source, snapshot, token);
        // Unsupported state is rejected BEFORE creating any target.
        var generation = receipt.TargetGeneration;
        if (!File.Exists(targetFull))
        {
            var created = new SyncAuthorityDatabase(targetFull)
            { _essentialSuccessorInitialGeneration = generation, _essentialSuccessorInitialReceipt = JsonSerializer.Serialize(receipt) };
            await created.InitializeAsync(actual.InstallationId, "essential-successor", "essential-successor", "{}", token);
        }
        using var targetFile = SyncLocalFileAuthority.OpenExistingMutableReadGuard(targetFull);
        targetFile.VerifySingleLink(); targetParent.VerifyBinding();
        if (!string.Equals(targetFile.FinalPath, targetFull, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Successor target path changed.");
        // Bind existing targets through a read-only connection before enabling
        // any target journal/configuration writes. Unrelated files stay byte-exact.
        await using (var inspect = new SqliteConnection(ConnectionString(targetFull, SqliteOpenMode.ReadOnly, false)))
        {
            await inspect.OpenAsync(token);
            await ExecuteAsync(inspect, null, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;", token);
            await RequireExactCurrentSchemaAsync(inspect, token, allowIncompleteSuccessor: true);
            var bound = await ReadEssentialReceiptAsync(inspect, token);
            var boundGeneration = await ScalarStringAsync(inspect, null, "SELECT database_generation FROM installation;", token);
            BindEssentialReceipt(bound, actual, options, boundGeneration!);
        }
        await using var target = await OpenConnectionAtPathAsync(targetFull, SqliteOpenMode.ReadWrite, false, token);
        await RequireExactSchemaAsync(target, SyncAuthoritySchema.Version, ExpectedCurrentSchemaObjects, token);
        receipt = await ReadEssentialReceiptAsync(target, token);
        generation = await ScalarStringAsync(target, null, "SELECT database_generation FROM installation;", token)
            ?? throw new InvalidDataException("Successor generation is absent.");
        BindEssentialReceipt(receipt, actual, options, generation);
        if(receipt.HistoryAsOfUtc!=selectionTime)throw new InvalidDataException("Successor history selection changed during binding.");
        // The explicit offline target uses rollback journaling while preparing.
        // A later read-only rejection must not leave empty WAL sidecars that
        // prevent resuming the exact incomplete target.
        await ExecuteAsync(target,null,"PRAGMA journal_mode=DELETE;",token);
        EssentialSuccessorBoundaryForTest?.Invoke("source-bound-incomplete-target");
        if (!receipt.Complete)
        {
            await ConfigureEssentialJournalImportAsync(target,token);
            var retention = await EssentialHistoryPolicyAsync(source,snapshot,token);
            await ExecuteAsync(target,null,"UPDATE installation SET history_retention_enabled=$value;",token,("$value",retention));
            foreach (var table in EssentialCopiedTables.Where(x=>EssentialSourceHasTable(source,x.Name)||EssentialDerivedCopyTable(x.Name)||EssentialDerivedConsentTable(x.Name)))
            {
                progress?.Report("Preparing essential configuration: " + table.Name);
                await CopyEssentialConfigurationAsync(source, snapshot, target, table, token);
                EssentialSuccessorBoundaryForTest?.Invoke("after-table:" + table.Name);
            }
            await ExecuteAsync(target,null,"UPDATE installation SET next_monitor_hint_sequence=$next;",token,
                ("$next",await EssentialMonitorNextHintAsync(source,snapshot,token)));
            await ExecuteAsync(target,null,"UPDATE installation SET next_history_sequence=$next;",token,
                ("$next",await EssentialNextHistoryAsync(source,snapshot,token)));
            await SetEssentialConsentAccountingAsync(target,token);
            await SetEssentialActionAccountingAsync(target,token);
            dispositions = await VerifyEssentialContentsAsync(source, snapshot, target, dispositions, token);
            guard.Verify(); targetParent.VerifyBinding(); targetFile.VerifySingleLink();
            EssentialSuccessorBoundaryForTest?.Invoke("before-completion");
            await using var commit = target.BeginTransaction();
            await SetEssentialReceiptAsync(target, commit, receipt, receipt with { Complete = true, Tables = dispositions,
                Cuts = await InspectEssentialCutsAsync(source, snapshot, token) }, token);
            await commit.CommitAsync(token);
        }
        else
        {
            dispositions = await VerifyEssentialContentsAsync(source, snapshot, target, dispositions, token);
            await RequireEssentialReceiptContentsAsync(receipt,dispositions,source,snapshot,token);
        }
        return new(targetFull, generation, true, dispositions);
    }

    internal static async Task<SyncEssentialSuccessorReport> VerifyEssentialSuccessorCoreAsync(
        string sourcePath, string targetPath, SyncEssentialSourceIdentity expected,
        SyncEssentialSuccessorOptions options, CancellationToken token)
    {
        using var sourceGuard = new EssentialSourceGuard(sourcePath, options);
        await using var source = await OpenEssentialSourceAsync(sourceGuard, token);
        var actual = await ReadEssentialSourceIdentityAsync(sourceGuard, source, token);
        if (actual != expected) throw new InvalidDataException("Essential source identity/bytes changed.");
        var fullTarget = RequireEssentialFixturePath(targetPath, options);
        if (string.Equals(sourceGuard.Path, fullTarget, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Essential successor source and target are identical.");
        using var parent = PilotDirectoryLease.OpenProductDirectoryReadGuard(Path.GetDirectoryName(fullTarget)!);
        using var targetFile = SyncLocalFileAuthority.OpenExistingReadGuard(fullTarget);
        targetFile.VerifySingleLink(); parent.VerifyBinding();
        await using var target = new SqliteConnection(ConnectionString(fullTarget, SqliteOpenMode.ReadOnly, false));
        await target.OpenAsync(token);
        await ExecuteAsync(target, null, "PRAGMA query_only=ON; PRAGMA trusted_schema=OFF;", token);
        await RequireExactCurrentSchemaAsync(target, token);
        var receipt = await ReadEssentialReceiptAsync(target, token);
        var generation = await ScalarStringAsync(target, null, "SELECT database_generation FROM installation;", token);
        BindEssentialReceipt(receipt, actual, options, generation!);
        BindEssentialHistorySelection(source,receipt);
        await using var snapshot = source.BeginTransaction(deferred: true);
        var dispositions = await VerifyEssentialContentsAsync(source, snapshot, target,
            await InspectEssentialDispositionsAsync(source, snapshot, token), token);
        await RequireEssentialReceiptContentsAsync(receipt,dispositions,source,snapshot,token);
        sourceGuard.Verify();
        return new(fullTarget, generation!, true, dispositions);
    }

    private sealed record EssentialConfigurationTable(string Name, string Columns, string Keys, string? SourceSql = null);
    private static readonly EssentialConfigurationTable[] EssentialConfigurationTables =
    [
        new("capability_profile","capability_profile_id,source_revision,profile_json,created_utc","capability_profile_id"),
        new("sync_pair","pair_id,configuration_revision,state,created_utc,pair_json,selected","pair_id"),
        new("pair_config_revision","pair_id,revision,created_utc,created_by,deletion_policy,deletion_grace_seconds,required_absence_observations,conflict_policy,metadata_policy,recursive_mode,automatic_pruning,recovery_retention_days","pair_id,revision"),
        new("provider_account","provider_account_id,installation_id,provider,account_id,root_namespace_id,home_namespace_id,display_label,binding_revision,verified_utc,status","provider_account_id"),
        new("recovery_root_binding","recovery_root_binding_id,installation_id,provider_account_id,side,display_path,"+
            "normalized_path_key,identity_json,generation,status,verified_utc,provider_corpus_id","recovery_root_binding_id"),
        new("volume","volume_id,installation_id,volume_serial_hex,filesystem,volume_guid_path,first_seen_utc,last_seen_utc,supports_file_id_128,supports_usn,supports_atomic_rename,status","volume_id"),
        new("root_binding","root_binding_id,pair_id,side,generation,identity_json,display_path,state,observed_utc","root_binding_id"),
        new("scheduler_setting","singleton,global_effect_limit,per_pair_effect_limit,recovery_first,updated_utc","singleton"),
        new("lease_fence","scope_kind,scope_id,last_token","scope_kind,scope_id"),
        new("migration_import","migration_import_id,source_kind,source_identity,source_sha256,imported_json,imported_utc,status","migration_import_id")
    ];

    // Every original and current additive table is named. An unfamiliar table
    // or nonempty not-yet-adapted safety state is a refusal, never implicit loss.
    private static readonly HashSet<string> EssentialKnownTables = new(
        ("activity_context,allocation_transition,attempt_transition,authority_guard,capability_profile," +
         "command_request,conflict,conflict_resolution,conflict_version,consent_header,consent_item," +
         "control_namespace,copy_predecessor,current_copy_completion,dirty_item,effect_receipt,history_event," +
         "installation,lease,lease_fence,logical_object,membership,migration_import,monitor_cursor,monitor_event," +
         "namespace_allocation,object_version,observation,observation_cut,observation_delta,observation_interval," +
         "operation,operation_attempt,operation_dependency,operation_group,operation_member,operation_step," +
         "operation_step_transition,operation_transition,pair_config_revision,pair_control_namespace,process_instance," +
         "projection_watermark,provider_account,reconciliation_decision,recovery_artifact,recovery_root_binding," +
         "resource_allocation,rewind_path_progress,rewind_preview_item,rewind_session,root_binding,scan_generation," +
         "scheduler_setting,schema_migration,sync_pair,sync_path_index,synchronization_point,tombstone," +
         "tombstone_observation,upload_session_checkpoint,volume").Split(','), StringComparer.Ordinal);
    private static readonly HashSet<string> EssentialSeedTables = new(
        ["allocation_transition","attempt_transition","authority_guard","operation_step_transition","operation_transition"],
        StringComparer.Ordinal);

    private static async Task<List<SyncEssentialTableDisposition>> InspectEssentialDispositionsAsync(
        SqliteConnection source, SqliteTransaction snapshot, CancellationToken token)
    {
        var tables = new List<string>();
        await using (var query = source.CreateCommand())
        {
            query.Transaction = snapshot;
            query.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            await using var rows = await query.ExecuteReaderAsync(token);
            while (await rows.ReadAsync(token)) tables.Add(rows.GetString(0));
        }
        var known=EssentialLegacySource(source)
            ? new HashSet<string>(ExpectedVersion7SchemaObjects.Where(x=>x.StartsWith("table:",StringComparison.Ordinal)).Select(x=>x[6..]),StringComparer.Ordinal)
            : EssentialKnownTables;
        if (!known.SetEquals(tables))
            throw new InvalidDataException("Essential successor has an unclassified source table inventory.");
        await RequireEssentialLegacyApprovalsAsync(source,snapshot,token);
        var result = new List<SyncEssentialTableDisposition>();
        var unsupported = new List<string>();
        foreach (var table in EssentialKnownTables.Order(StringComparer.Ordinal))
        {
            if(!EssentialSourceHasTable(source,table))
            {
                var derived=EssentialDerivedCopyTable(table)
                    ? await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"SELECT count(*) FROM ("+
                        EssentialBaselineTables.Single(x=>x.Name==table).SourceSql+");"),token)
                    : EssentialDerivedConsentTable(table)?await EssentialDerivedConsentCountAsync(source,snapshot,table,token):0;
                result.Add(new(table,0,0,0,"Current additive structure has no legacy source rows; typed adapter creates it only when required.",TargetRows:derived));
                continue;
            }
            var count = await ScalarLongAsync(source, snapshot, $"SELECT count(*) FROM {table};", token);
            if (EssentialConfigurationTables.Any(x => x.Name == table))
                result.Add(new(table,count,count,0,"Exact configuration/protected references or monotonic fence/import attribution."));
            else if (EssentialObservationTables.Concat(EssentialBaselineTables).Concat(EssentialMonitorTables)
                .Concat(EssentialJournalHeaders).Concat(EssentialJournalBodies).Concat(EssentialPendingHistoryTables).Concat(EssentialSafetyTables).Concat(EssentialResolutionTables).Concat(EssentialRewindTables).Concat(EssentialDecisionTables).Concat(EssentialExplorerTables).Concat(EssentialConsentTables).Concat(EssentialHistoryContextTables).SingleOrDefault(x => x.Name == table) is { } selected)
            {
                var selectedCount=await ScalarLongAsync(source,snapshot,
                    "SELECT count(*) FROM ("+EssentialSourceSql(source,selected.SourceSql!)+");",token);
                var physicalSelected=table=="observation"
                    ? await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"SELECT count(*) FROM observation WHERE scan_generation_id IN ("+EssentialScans+");"),token)
                    : EssentialDerivedCopyTable(table)?await EssentialPhysicalCopyRowsAsync(source,snapshot,table,token):selectedCount;
                result.Add(new(table,count,physicalSelected,count-physicalSelected,
                    table=="sync_path_index" ? "Current scalar Explorer knowledge including exclusions; folder totals rebuilt, obsolete display-only scan references detached without granting effect authority."
                    : table=="observation" ? "Whole existing effective cuts materialised in the legacy nine-field encoding; original compact attribution bound in receipt."
                    : table=="monitor_cursor" ? "Exact current cursor and uncertainty; source writer epoch/acknowledgement authority closed in the new generation."
                    : table=="monitor_event" ? "Unconsumed exact hint/dedup evidence retained; consumed diagnostics remain original-only."
                    : table=="dirty_item" ? "Every nonclosed dirty obligation retained exactly; closed rows remain original-only."
                    : table=="history_event" ? "Newest permitted detached completed ordinary outcomes plus latest32 pending-run safety rows; exact sequence/highwater and original-only accounting."
                    : "Exact current complete/latest cut and its referenced membership/version/object closure.",
                    TargetRows:selectedCount+(EssentialDerivedConsentTable(table)?await EssentialDerivedConsentCountAsync(source,snapshot,table,token):0)));
            }
            else if(table is "observation_cut" or "observation_interval" or "observation_delta")
                result.Add(new(table,count,0,count,
                    "Original compact storage remains source-only; whole selected effective cuts and source digests are materialised and verified.",TargetRows:0));
            else if (EssentialSeedTables.Contains(table) || table is "schema_migration")
                result.Add(new(table,count,0,count,"Current code creates its own schema/transition policy; source retained as attribution."));
            else if (table == "installation")
                result.Add(new(table,count,1,0,"Same installation; new generation and fresh counters; source receipt binds original identity."));
            else if (table == "activity_context")
                result.Add(new(table,count,0,count,"Contexts without an exactly detached selected event remain available in the unchanged original; no historical action graph is imported."));
            else if (table is "command_request" or "process_instance" or "projection_watermark" or "sync_path_index")
                result.Add(new(table,count,0,count,
                    table == "command_request" ? "Closed commands remain original-only and carry no successor admission."
                    : table == "process_instance" ? "Source worker identities do not transfer."
                    : "Derived state without a selected consumer remains original-only; a populated observed Explorer index requires its rebuild adapter."));
            else
            {
                result.Add(new(table,count,0,count,"Requires its exact typed successor adapter when nonempty."));
                if (count != 0) unsupported.Add(table);
            }
            if(table is "history_event" or "activity_context" or "reconciliation_decision")
            {
                var column=table=="history_event"?"detail_json":table=="activity_context"?"context_json":"decision_json";
                var sourceBytes=await ScalarLongAsync(source,snapshot,
                    "SELECT coalesce(sum(length(CAST("+column+" AS BLOB))),0) FROM "+table+";",token);
                var payloadSelection=table=="history_event"?EssentialPendingHistoryTables[0]:table=="activity_context"?EssentialHistoryContextTables[0]:EssentialDecisionTables[0];
                var selectedBytes=await ScalarLongAsync(source,snapshot,
                    EssentialSourceSql(source,"SELECT coalesce(sum(length(CAST("+column+" AS BLOB))),0) FROM ("+
                        payloadSelection.SourceSql+");"),token);
                result[^1]=result[^1] with { SourcePayloadBytes=sourceBytes,SelectedPayloadBytes=selectedBytes,
                    OriginalOnlyPayloadBytes=checked(sourceBytes-selectedBytes) };
            }
        }
        await RequireEssentialSafetyStateAsync(source,snapshot,token);
        await RequireEssentialRewindAsync(source,snapshot,token);
        await RequireEssentialResolutionsAsync(source,snapshot,token);
        await RequireEssentialDecisionsAsync(source,snapshot,token);
        await RequireEssentialConsentAsync(source,snapshot,token);
        await RequireEssentialJournalPointsAsync(source,snapshot,token);
        await VerifyEssentialClosedActionsAsync(source,snapshot,true,token);
        if(EssentialSourceHasTable(source,"current_copy_completion"))
            await RequireEssentialNormalizedCopyAsync(source,snapshot,token);
        if(EssentialSourceHasTable(source,"current_copy_completion") && await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM current_copy_completion c
            LEFT JOIN synchronization_point p ON p.synchronization_point_id=c.point_id
            LEFT JOIN membership lm ON lm.membership_id=p.local_membership_id
            LEFT JOIN membership rm ON rm.membership_id=p.dropbox_membership_id
            WHERE p.synchronization_point_id IS NULL OR c.pair_id IS NOT p.pair_id OR c.object_id IS NOT p.object_id
               OR c.point_generation IS NOT p.authority_generation
               OR c.local_version_id IS NOT p.local_object_version_id OR c.remote_version_id IS NOT p.dropbox_object_version_id
               OR c.local_membership_id IS NOT p.local_membership_id OR c.remote_membership_id IS NOT p.dropbox_membership_id
               OR c.local_root_id IS NOT lm.root_binding_id OR c.remote_root_id IS NOT rm.root_binding_id
               OR c.path_key IS NOT (CASE WHEN lm.relative_path_key=rm.relative_path_key THEN lm.relative_path_key END);
            """,token)!=0)
            throw new InvalidDataException("Empty current-copy projection does not match its exact accepted point.");
        await RequireEssentialLegacyCopyAsync(source,snapshot,token);
        if(!EssentialLegacySource(source) && await ScalarLongAsync(source,snapshot,EssentialSourceSql(source,"SELECT count(*) FROM scan_generation WHERE lifetime_retiring<>0 AND scan_generation_id IN ("+EssentialScans+");"),token)!=0)
            throw new InvalidDataException("A selected source cut is in partial retirement.");
        if(await ScalarLongAsync(source,snapshot,"SELECT count(*) FROM observation WHERE scan_generation_id IS NULL;",token)!=0)
            throw new InvalidDataException("Unbound observation evidence requires an exact successor disposition.");
        await RequireEssentialExplorerAsync(source,snapshot,token);
        _=await InspectEssentialCutsAsync(source,snapshot,token);
        if (unsupported.Count != 0)
            throw new InvalidDataException("Essential successor needs implemented adapters for nonempty tables: " +
                string.Join(", ",unsupported));
        return result;
    }

    private static object?[] EssentialRow(SqliteDataReader reader)
    {
        var row = new object?[reader.FieldCount];
        for (var i=0;i<row.Length;i++) row[i]=reader.IsDBNull(i)?null:reader.GetValue(i);
        return row;
    }
    private static byte[] EssentialRowBytes(object?[] row) => JsonSerializer.SerializeToUtf8Bytes(row);

    private static async Task CopyEssentialConfigurationAsync(SqliteConnection source, SqliteTransaction snapshot,
        SqliteConnection target, EssentialConfigurationTable table, CancellationToken token)
    {
        // The reader streams the typed projection. At most256 rows/1MiB is kept
        // between target commits; no historical graph or full DB copy is loaded.
        var batch=new List<object?[]>(256);var bytes=0;
        await foreach(var row in ReadEssentialRowsAsync(source,snapshot,table,true,token))
        {
            var length=EssentialRowBytes(row).Length;
            if(length>1_048_576)
                throw new InvalidDataException("A configuration body requires its bounded oversize legacy adapter.");
            if(batch.Count==256 || bytes+length>1_048_576)
            { await WriteEssentialConfigurationBatchAsync(target,table,batch,token);batch.Clear();bytes=0; }
            batch.Add(row);bytes+=length;
        }
        if(batch.Count!=0) await WriteEssentialConfigurationBatchAsync(target,table,batch,token);
    }

    private static async Task WriteEssentialConfigurationBatchAsync(SqliteConnection target,
        EssentialConfigurationTable table,IReadOnlyList<object?[]> rows,CancellationToken token)
    {
        var columns=table.Columns.Split(',');var keys=table.Keys.Split(',');
        await using var tx=target.BeginTransaction();
        // Only this transaction sees the private row-bound INSERT exception.
        // Other connections retain the original guard; commit restores it first.
        // Failure rolls back both schema and rows together.
        var originalGuards=await OpenEssentialRowGuardsAsync(target,tx,table,token);
        foreach(var row in rows)
        {
            await using var existing=target.CreateCommand();existing.Transaction=tx;
            existing.CommandText=$"SELECT {table.Columns} FROM {table.Name} WHERE "+
                string.Join(" AND ",keys.Select((key,i)=>key+" IS $k"+i))+";";
            for(var i=0;i<keys.Length;i++)
                existing.Parameters.AddWithValue("$k"+i,row[Array.IndexOf(columns,keys[i])]??DBNull.Value);
            await using var found=await existing.ExecuteReaderAsync(token);
            if(await found.ReadAsync(token))
            {
                if(!EssentialRowBytes(row).SequenceEqual(EssentialRowBytes(EssentialRow(found))))
                    throw new InvalidDataException("An existing successor row differs from its source-bound selection.");
                continue;
            }
            await found.DisposeAsync();
            if(table.Name=="history_event")
            {
                var sequence=(long)row[0]!;
                if(sequence<await ScalarLongAsync(target,tx,"SELECT next_history_sequence FROM installation;",token))
                    throw new InvalidDataException("A missing successor history row would reuse a spent sequence.");
                await ExecuteAsync(target,tx,"UPDATE installation SET next_history_sequence=$sequence;",token,("$sequence",sequence));
            }
            await using var insert=target.CreateCommand();insert.Transaction=tx;
            insert.CommandText=$"INSERT INTO {table.Name}({table.Columns}) VALUES("+
                string.Join(",",Enumerable.Range(0,row.Length).Select(i=>"$v"+i))+");";
            for(var i=0;i<row.Length;i++)insert.Parameters.AddWithValue("$v"+i,row[i]??DBNull.Value);
            EssentialInsertPermits.TryGetValue(target,out var permit);
            if(permit is not null){permit.Table=table.Name;permit.Row=row;}
            try
            {
                if(originalGuards.Count!=0)EssentialSuccessorBoundaryForTest?.Invoke("journal-row-permit-installed");
                await insert.ExecuteNonQueryAsync(token);
            }
            finally{if(permit is not null){permit.Table=null;permit.Row=null;}}
        }
        await CloseEssentialRowGuardsAsync(target,tx,originalGuards,token);
        EssentialSuccessorBoundaryForTest?.Invoke("configuration-batch-before-commit");
        await tx.CommitAsync(token);
    }

    private static async Task<(long Count,string Digest)> HashEssentialTableAsync(SqliteConnection c,
        SqliteTransaction? tx,EssentialConfigurationTable table,CancellationToken token,bool selectedSource=false)
    {
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long count=0;
        await foreach(var row in ReadEssentialRowsAsync(c,tx,table,selectedSource,token))
        {
            var bytes=EssentialRowBytes(row);
            hash.AppendData(bytes);hash.AppendData("\n"u8);count++;
        }
        return(count,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task<List<SyncEssentialTableDisposition>> VerifyEssentialContentsAsync(
        SqliteConnection source,SqliteTransaction snapshot,SqliteConnection target,
        List<SyncEssentialTableDisposition> dispositions,CancellationToken token)
    {
        if(await ScalarStringAsync(target,null,"PRAGMA integrity_check;",token)!="ok" ||
            await ScalarLongAsync(target,null,"SELECT count(*) FROM pragma_foreign_key_check;",token)!=0)
            throw new InvalidDataException("Essential successor failed integrity/FK verification.");
        if(await ScalarLongAsync(target,null,"SELECT count(*) FROM installation;",token)!=1 ||
            await ScalarStringAsync(target,null,"SELECT installation_id FROM installation;",token)!=
            await ScalarStringAsync(source,snapshot,"SELECT installation_id FROM installation;",token))
            throw new InvalidDataException("Essential successor installation identity differs.");
        if(await ScalarLongAsync(target,null,"SELECT history_retention_enabled FROM installation;",token)!=
            await EssentialHistoryPolicyAsync(source,snapshot,token))
            throw new InvalidDataException("Essential successor history preference differs.");
        if(await ScalarLongAsync(target,null,"SELECT next_history_sequence FROM installation;",token)!=
            await EssentialNextHistoryAsync(source,snapshot,token))
            throw new InvalidDataException("Essential successor history sequence differs.");
        if(await ScalarLongAsync(target,null,"SELECT next_monitor_hint_sequence FROM installation;",token)!=
                await EssentialMonitorNextHintAsync(source,snapshot,token) ||
            await ScalarLongAsync(target,null,"SELECT count(*) FROM monitor_cursor WHERE writer_epoch IS NOT NULL OR committed_batch_sequence<>0 OR last_batch_digest IS NOT NULL OR last_batch_prior_cursor IS NOT NULL;",token)!=0)
            throw new InvalidDataException("Essential successor monitor sequence or closed writer authority differs.");
        var verified=new List<SyncEssentialTableDisposition>();
        foreach(var disposition in dispositions)
        {
            var table=EssentialCopiedTables.SingleOrDefault(x=>x.Name==disposition.Table);
            if(table is not null && (EssentialSourceHasTable(source,table.Name)||EssentialDerivedCopyTable(table.Name)||EssentialDerivedConsentTable(table.Name)))
            {
                var old=await HashEssentialTableAsync(source,snapshot,table,token,selectedSource:true);
                var next=await HashEssentialTableAsync(target,null,table,token);
                if(old!=next || old.Count!=(disposition.TargetRows??disposition.SelectedRows))
                    throw new InvalidDataException("Essential successor typed configuration content differs.");
                verified.Add(disposition with { SelectedDigest=next.Digest });
            }
            else
            {
                var count=await ScalarLongAsync(target,null,$"SELECT count(*) FROM {disposition.Table};",token);
                if(!EssentialSeedTables.Contains(disposition.Table) && disposition.Table!="schema_migration" &&
                    count!=(disposition.TargetRows??disposition.SelectedRows))
                    throw new InvalidDataException("Unexpected rows appeared outside essential selected state.");
                verified.Add(disposition);
            }
        }
        if(await ScalarLongAsync(target,null,"""
            SELECT count(*) FROM consent_item i WHERE i.retained_operation_count<>(
                SELECT count(*) FROM operation o WHERE o.consent_id=i.consent_id AND o.consent_item_ordinal=i.ordinal);
            """,token)!=0)
            throw new InvalidDataException("Successor consent references do not match exact retained operation ownership.");
        await VerifyEssentialHistoryAccountingAsync(target,token);
        await VerifyEssentialActionAccountingAsync(target,token);
        await using(var proofSnapshot=target.BeginTransaction(deferred:true))
            await VerifyEssentialClosedActionsAsync(target,proofSnapshot,false,token);
        await VerifyEssentialCutsAsync(source,snapshot,target,token);
        await VerifyEssentialCopyCandidatesAsync(source,snapshot,target,token);
        return verified;
    }

    private const string EssentialScans = """
        SELECT scan_generation_id FROM scan_generation s
        WHERE s.generation=(SELECT max(a.generation) FROM scan_generation a WHERE a.root_binding_id=s.root_binding_id)
           OR (s.status='complete' AND s.generation=(SELECT max(a.generation) FROM scan_generation a
                WHERE a.root_binding_id=s.root_binding_id AND a.status='complete'))
        """+" UNION SELECT scan_generation_id FROM scan_generation WHERE scan_generation_id IN ("+EssentialJournalScans+" UNION "+EssentialSafetyScans+" UNION "+EssentialConsentScans+" UNION "+EssentialLegacyConsentScans+" UNION "+EssentialRewindScans+" UNION "+EssentialDecisionScans+" UNION "+EssentialAbsenceScans+")";
    private const string EssentialObservations = "SELECT observation_id,scan_generation_id,root_binding_id,"+
        "scan_generation,complete_generation,object_version_id,membership_id,evidence_json,observed_utc "+
        "FROM effective_observation WHERE scan_generation_id IN ("+EssentialScans+")";
    private const string EssentialMembershipIds =
        "SELECT membership_id FROM ("+EssentialObservations+") WHERE membership_id IS NOT NULL "+
        "UNION SELECT local_membership_id FROM current_synchronization_point WHERE local_membership_id IS NOT NULL "+
        "UNION SELECT dropbox_membership_id FROM current_synchronization_point WHERE dropbox_membership_id IS NOT NULL "+
        "UNION SELECT local_membership_id FROM "+EssentialCopyCurrent+" WHERE local_membership_id IS NOT NULL "+
        "UNION SELECT remote_membership_id FROM "+EssentialCopyCurrent+" WHERE remote_membership_id IS NOT NULL "+
        "UNION SELECT local_membership_id FROM "+EssentialCopyPredecessor+" WHERE local_membership_id IS NOT NULL "+
        "UNION SELECT remote_membership_id FROM "+EssentialCopyPredecessor+" WHERE remote_membership_id IS NOT NULL UNION "+EssentialJournalMembers+" UNION "+EssentialSafetyMembers+" UNION "+EssentialPointMembers;
    private const string EssentialVersionIds =
        "SELECT object_version_id FROM ("+EssentialObservations+") WHERE object_version_id IS NOT NULL "+
        "UNION SELECT local_object_version_id FROM current_synchronization_point WHERE local_object_version_id IS NOT NULL "+
        "UNION SELECT dropbox_object_version_id FROM current_synchronization_point WHERE dropbox_object_version_id IS NOT NULL "+
        "UNION SELECT local_version_id FROM "+EssentialCopyCurrent+" WHERE local_version_id IS NOT NULL "+
        "UNION SELECT remote_version_id FROM "+EssentialCopyCurrent+" WHERE remote_version_id IS NOT NULL "+
        "UNION SELECT local_version_id FROM "+EssentialCopyPredecessor+" WHERE local_version_id IS NOT NULL "+
        "UNION SELECT remote_version_id FROM "+EssentialCopyPredecessor+" WHERE remote_version_id IS NOT NULL UNION "+EssentialJournalVersions+" UNION "+EssentialSafetyVersions+" UNION "+EssentialPointVersions;
    private const string EssentialEmptyCopyReferences=
        "SELECT NULL AS point_id,NULL AS local_membership_id,NULL AS remote_membership_id,"+
        "NULL AS local_version_id,NULL AS remote_version_id WHERE 0";
    private static readonly EssentialConfigurationTable[] EssentialObservationTables =
    [
        new("logical_object","object_id,object_kind,created_utc","object_id",
            "SELECT object_id,object_kind,created_utc FROM logical_object WHERE object_id IN "+
            "(SELECT object_id FROM membership WHERE membership_id IN ("+EssentialMembershipIds+") "+
            "UNION SELECT object_id FROM object_version WHERE object_version_id IN ("+EssentialVersionIds+") "+
            "UNION SELECT object_id FROM dirty_item WHERE dirty_item_id IN ("+EssentialDirtyItems+") AND object_id IS NOT NULL "+
            "UNION SELECT object_id FROM reconciliation_decision WHERE reconciliation_decision_id IN ("+EssentialDecisions+") AND object_id IS NOT NULL "+
            "UNION SELECT object_id FROM recovery_artifact WHERE status<>'released' AND object_id IS NOT NULL "+
            "UNION SELECT object_id FROM tombstone WHERE tombstone_id IN ("+EssentialTombstones+") "+
            "UNION SELECT object_id FROM conflict WHERE conflict_id IN ("+EssentialConflicts+") "+
            "UNION SELECT object_id FROM operation WHERE operation_id IN ("+EssentialOperations+") AND object_id IS NOT NULL "+
            "UNION SELECT object_id FROM operation_member WHERE operation_id IN ("+EssentialOperations+"))"),
        new("object_version","object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc",
            "object_version_id","SELECT object_version_id,object_id,side,identity_json,content_hash,size_bytes,observed_utc "+
            "FROM object_version WHERE object_version_id IN ("+EssentialVersionIds+")"),
        new("membership","membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc",
            "membership_id","SELECT membership_id,object_id,root_binding_id,relative_path_key,generation,state,observed_utc "+
            "FROM membership WHERE membership_id IN ("+EssentialMembershipIds+")"),
        new("scan_generation","scan_generation_id,pair_id,root_binding_id,side,generation,trigger_kind,scope_json,"+
            "started_utc,completed_utc,status,entry_count,ended_utc","scan_generation_id",
            "SELECT scan_generation_id,pair_id,root_binding_id,side,generation,trigger_kind,scope_json,"+
            "started_utc,completed_utc,status,entry_count,ended_utc FROM scan_generation WHERE scan_generation_id IN ("+EssentialScans+")"),
        // Materialise the EXISTING effective_observation view whole, preserving
        // its nine exact fields/IDs. No source scan is re-enumerated or inferred.
        new("observation","observation_id,scan_generation_id,root_binding_id,scan_generation,complete_generation,"+
            "object_version_id,membership_id,evidence_json,observed_utc","observation_id",EssentialObservations)
    ];
    private const string EssentialPointColumns="synchronization_point_id,pair_id,object_id,local_object_version_id,"+
        "dropbox_object_version_id,local_membership_id,dropbox_membership_id,accepted_utc,authority_generation";
    private static readonly EssentialConfigurationTable[] EssentialBaselineTables =
    [
        new("synchronization_point",EssentialPointColumns,"synchronization_point_id",
            "SELECT "+EssentialPointColumns+" FROM synchronization_point WHERE synchronization_point_id IN "+
            "("+EssentialPoints+")"),
        new("current_copy_completion",CopySnapshotColumns+",coverage_verified","pair_id,object_id",
            "SELECT "+CopySnapshotColumns+",coverage_verified FROM "+EssentialCopyCurrent+""),
        new("copy_predecessor",CopySnapshotColumns,"pair_id,object_id,local_membership_id",
            "SELECT "+CopySnapshotColumns+" FROM "+EssentialCopyPredecessor+"")
    ];
    private static IEnumerable<EssentialConfigurationTable> EssentialCopiedTables =>
        EssentialConfigurationTables.Concat(EssentialConsentTables).Concat(EssentialJournalHeaders).Concat(EssentialObservationTables)
            .Concat(EssentialBaselineTables).Concat(EssentialJournalBodies).Concat(EssentialMonitorTables).Concat(EssentialSafetyTables).Concat(EssentialResolutionTables).Concat(EssentialRewindTables).Concat(EssentialDecisionTables).Concat(EssentialHistoryContextTables).Concat(EssentialPendingHistoryTables).Concat(EssentialExplorerTables);

    private const string EssentialMonitorEventLegacyColumns="monitor_event_id,monitor_cursor_id,pair_id,source_event_key,"+
        "normalized_path_key,object_identity_json,event_kind,event_json,received_utc,consumed_utc";
    private const string EssentialDirtyLegacyColumns="dirty_item_id,pair_id,object_id,side,normalized_path_key,"+
        "reconciliation_level,earliest_event_utc,latest_event_utc,event_count,planner_input_digest,status,lease_id";
    private const string EssentialMonitorCursorColumns="monitor_cursor_id,pair_id,root_binding_id,kind,generation,"+
        "cursor_value,source_identity_json,status,updated_utc";
    private static readonly EssentialConfigurationTable[] EssentialMonitorTables=
    [
        new("monitor_cursor",EssentialMonitorCursorColumns+",writer_epoch,committed_batch_sequence,last_batch_digest,last_batch_prior_cursor",
            "monitor_cursor_id","SELECT "+EssentialMonitorCursorColumns+
            ",NULL AS writer_epoch,0 AS committed_batch_sequence,NULL AS last_batch_digest,NULL AS last_batch_prior_cursor "+
            "FROM monitor_cursor WHERE status<>'retired' OR monitor_cursor_id IN "+
            "(SELECT monitor_cursor_id FROM monitor_event WHERE consumed_utc IS NULL)"),
        new("monitor_event",EssentialMonitorEventLegacyColumns+",batch_epoch,batch_sequence,hint_sequence,lifetime_class,encoded_bytes",
            "monitor_event_id","SELECT "+EssentialMonitorEventLegacyColumns+
            ",batch_epoch,batch_sequence,hint_sequence,lifetime_class,encoded_bytes FROM monitor_event WHERE consumed_utc IS NULL"),
        new("dirty_item",EssentialDirtyLegacyColumns+",latest_hint_sequence,closed_utc","dirty_item_id",
            "SELECT "+EssentialDirtyLegacyColumns+",latest_hint_sequence,closed_utc FROM dirty_item WHERE dirty_item_id IN ("+EssentialDirtyItems+")")
    ];
    private static Task<long> EssentialMonitorNextHintAsync(SqliteConnection c,SqliteTransaction? tx,CancellationToken token)=>
        EssentialLegacySource(c)?Task.FromResult(1L):
            ScalarLongAsync(c,tx,"SELECT next_monitor_hint_sequence FROM installation;",token);


    private static async Task RequireEssentialNormalizedCopyAsync(SqliteConnection source,SqliteTransaction snapshot,
        CancellationToken token)
    {
        if(await ScalarLongAsync(source,snapshot,"SELECT count(*) FROM current_copy_completion WHERE coverage_verified<>1;",token)!=0)
            throw new InvalidDataException("Unverified current-copy state requires its legacy proof normalization adapter.");
        foreach(var table in new[]{"current_copy_completion","copy_predecessor"})
        {
            // Immutable aliases and the exact original receipt remain scalar
            // evidence. They never recreate the historical operation or grant a lease.
            if(await ScalarLongAsync(source,snapshot,$"""
                SELECT count(*) FROM {table} c
                WHERE (c.copy_operation_id IS NULL AND (c.attempt_id IS NOT NULL OR c.receipt_id IS NOT NULL
                    OR c.destination_identity_json IS NOT NULL OR c.attempt_status IS NOT NULL OR c.receipt_evidence_json IS NOT NULL))
                   OR (c.copy_operation_id IS NOT NULL AND (
                       c.attempt_id IS NULL OR c.receipt_id IS NULL OR c.destination_identity_json IS NULL
                       OR c.attempt_status IS NULL OR c.receipt_evidence_json IS NULL
                       OR json_extract(c.receipt_evidence_json,'$.receiptId') IS NOT c.receipt_id
                       OR json_extract(c.receipt_evidence_json,'$.attemptId') IS NOT c.attempt_id
                       OR json_extract(c.receipt_evidence_json,'$.operationId') IS NOT c.copy_operation_id
                       OR json_extract(c.receipt_evidence_json,'$.certainty') IS NOT 'known_success'
                       OR json_extract(c.receipt_evidence_json,'$.destination') IS NOT c.destination_identity_json
                       OR json_extract(c.destination_identity_json,'$.identity') IS NOT
                           (SELECT identity_json FROM object_version WHERE object_version_id=c.local_version_id)
                       OR json_extract(c.destination_identity_json,'$.path') IS NOT c.path_key));
                """,token)!=0)
                throw new InvalidDataException("Normalized copy receipt aliases or physical destination differ.");
        }
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM copy_predecessor c
            LEFT JOIN membership lm ON lm.membership_id=c.local_membership_id
            LEFT JOIN membership rm ON rm.membership_id=c.remote_membership_id
            LEFT JOIN object_version lv ON lv.object_version_id=c.local_version_id
            LEFT JOIN object_version rv ON rv.object_version_id=c.remote_version_id
            LEFT JOIN root_binding lr ON lr.root_binding_id=c.local_root_id
            LEFT JOIN root_binding rr ON rr.root_binding_id=c.remote_root_id
            WHERE lm.membership_id IS NULL OR rm.membership_id IS NULL
               OR lv.object_version_id IS NULL OR rv.object_version_id IS NULL
               OR lm.object_id IS NOT c.object_id OR rm.object_id IS NOT c.object_id
               OR lv.object_id IS NOT c.object_id OR rv.object_id IS NOT c.object_id
               OR lm.root_binding_id IS NOT c.local_root_id OR rm.root_binding_id IS NOT c.remote_root_id
               OR lr.pair_id IS NOT c.pair_id OR rr.pair_id IS NOT c.pair_id
               OR lm.relative_path_key IS NOT c.path_key OR rm.relative_path_key IS NOT c.path_key;
            """,token)!=0)
            throw new InvalidDataException("Normalized copy predecessor membership/version/root binding differs.");
        if(await ScalarLongAsync(source,snapshot,"""
            SELECT count(*) FROM current_copy_completion c
            WHERE EXISTS(SELECT 1 FROM synchronization_point p WHERE p.pair_id=c.pair_id AND p.object_id=c.object_id
                AND p.authority_generation>c.point_generation);
            """,token)!=0)
            throw new InvalidDataException("A stale current-copy projection requires explicit reconciliation before import.");
    }

    private static async Task<(long Count,string Digest)> HashEssentialCopyCandidatesAsync(SqliteConnection c,
        SqliteTransaction? tx,string pair,string objectId,CancellationToken token,bool sourceEvidence=false)
    {
        await using var query=c.CreateCommand();query.Transaction=tx;
        // Reuse the production candidate predicate and all14 fields; only choose
        // a total order for a streaming comparison instead of loading the lineage.
        var compact=CurrentCopyCandidateQuery(true).Trim().TrimEnd(';');
        var legacy=LegacyCompletedCopyMoveSql(!EssentialLegacySource(c)).Trim().TrimEnd(';');
        var combined=!sourceEvidence? "SELECT * FROM ("+compact+")":
            "SELECT "+EssentialLegacyCandidateColumns+" FROM ("+legacy+")"+
            (EssentialSourceHasTable(c,"current_copy_completion")?" UNION SELECT * FROM ("+compact+")":"");
        query.CommandText="SELECT * FROM ("+combined+") ORDER BY 1,2,3,4,5,6,7,8,9,10,11,12,13,14;";
        query.Parameters.AddWithValue("$limit",-1);
        foreach(var name in new[]{"$prior","$current","$copy"})query.Parameters.AddWithValue(name,DBNull.Value);
        query.Parameters.AddWithValue("$pair",pair);query.Parameters.AddWithValue("$object",objectId);
        query.Parameters.AddWithValue("$unverified",0);
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long count=0;
        await using var rows=await query.ExecuteReaderAsync(token);
        while(await rows.ReadAsync(token))
        {
            var bytes=EssentialRowBytes(EssentialRow(rows));
            hash.AppendData(BitConverter.GetBytes(bytes.Length));hash.AppendData(bytes);count++;
        }
        return(count,Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static async Task VerifyEssentialCopyCandidatesAsync(SqliteConnection source,SqliteTransaction snapshot,
        SqliteConnection target,CancellationToken token)
    {
        await using var query=source.CreateCommand();query.Transaction=snapshot;
        query.CommandText=EssentialSourceSql(source,"SELECT pair_id,object_id FROM "+EssentialCopyCurrent+" ORDER BY pair_id,object_id;");
        await using var objects=await query.ExecuteReaderAsync(token);
        while(await objects.ReadAsync(token))
        {
            var pair=objects.GetString(0);var objectId=objects.GetString(1);
            if(await HashEssentialCopyCandidatesAsync(source,snapshot,pair,objectId,token,sourceEvidence:true)!=
                await HashEssentialCopyCandidatesAsync(target,null,pair,objectId,token))
                throw new InvalidDataException("Successor current-copy candidates differ from all14 original fields.");
        }
    }

    private static async Task RequireEssentialReceiptContentsAsync(SyncEssentialSuccessorReceipt receipt,
        IReadOnlyList<SyncEssentialTableDisposition> dispositions,SqliteConnection source,SqliteTransaction snapshot,
        CancellationToken token)
    {
        var cuts=await InspectEssentialCutsAsync(source,snapshot,token);
        if(JsonSerializer.Serialize(dispositions)!=JsonSerializer.Serialize(receipt.Tables) ||
            JsonSerializer.Serialize(cuts)!=JsonSerializer.Serialize(receipt.Cuts))
            throw new InvalidDataException("Successor content no longer matches its completion receipt.");
    }

    private static string EssentialSelect(EssentialConfigurationTable table,bool selectedSource) =>
        selectedSource && table.SourceSql is not null
            ? $"SELECT {table.Columns} FROM ({table.SourceSql}) ORDER BY {table.Keys};"
            : $"SELECT {table.Columns} FROM {table.Name} ORDER BY {table.Keys};";

    private static async Task<List<SyncEssentialCutReceipt>> InspectEssentialCutsAsync(SqliteConnection source,
        SqliteTransaction? snapshot,CancellationToken token,bool selectedSource=true)
    {
        var scans=new List<(string Id,string Status,long Count,string? Cut,string? Input)>();
        await using(var query=source.CreateCommand())
        {
            query.Transaction=snapshot;
            query.CommandText=(EssentialLegacySource(source)
                ? "SELECT s.scan_generation_id,s.status,s.entry_count,NULL,NULL FROM scan_generation s"
                : "SELECT s.scan_generation_id,s.status,s.entry_count,c.cut_digest,c.sealed_input_digest "+
                  "FROM scan_generation s LEFT JOIN observation_cut c ON c.scan_generation_id=s.scan_generation_id")+
                (selectedSource?" WHERE s.scan_generation_id IN ("+EssentialScans+")":"")+
                " ORDER BY s.scan_generation_id;";
            query.CommandText=EssentialSourceSql(source,query.CommandText);
            await using var rows=await query.ExecuteReaderAsync(token);
            while(await rows.ReadAsync(token))
                scans.Add((rows.GetString(0),rows.GetString(1),rows.GetInt64(2),
                    rows.IsDBNull(3)?null:rows.GetString(3),rows.IsDBNull(4)?null:rows.GetString(4)));
        }
        var result=new List<SyncEssentialCutReceipt>();
        foreach(var scan in scans)
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);long count=0;
            await using var query=source.CreateCommand();query.Transaction=snapshot;
            query.CommandText="""
                SELECT observation_id,scan_generation_id,root_binding_id,scan_generation,complete_generation,
                    object_version_id,membership_id,evidence_json,observed_utc
                FROM effective_observation WHERE scan_generation_id=$scan ORDER BY observation_id;
                """;
            query.CommandText=EssentialSourceSql(source,query.CommandText);
            query.Parameters.AddWithValue("$scan",scan.Id);
            await using var rows=await query.ExecuteReaderAsync(token);
            while(await rows.ReadAsync(token))
            {
                if(scan.Status=="complete"&&rows.GetInt32(4)!=1)
                    throw new InvalidDataException("A complete source scan has incomplete observation evidence.");
                hash.AppendData(EssentialRowBytes(EssentialRow(rows)));hash.AppendData("\n"u8);count++;
            }
            if(scan.Status=="complete"&&count!=scan.Count)
                throw new InvalidDataException("A complete source cut does not have its declared whole row count.");
            result.Add(new(scan.Id,scan.Status,count,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),scan.Cut,scan.Input));
        }
        return result;
    }

    private static async Task VerifyEssentialCutsAsync(SqliteConnection source,SqliteTransaction snapshot,
        SqliteConnection target,CancellationToken token)
    {
        var before=await InspectEssentialCutsAsync(source,snapshot,token);
        var after=await InspectEssentialCutsAsync(target,null,token,selectedSource:false);
        if(before.Count!=after.Count || before.Zip(after).Any(x=>
            x.First.ScanId!=x.Second.ScanId || x.First.Status!=x.Second.Status ||
            x.First.Rows!=x.Second.Rows || x.First.ProjectionSha256!=x.Second.ProjectionSha256))
            throw new InvalidDataException("Essential successor whole-cut evidence differs from the source view.");
    }
}
