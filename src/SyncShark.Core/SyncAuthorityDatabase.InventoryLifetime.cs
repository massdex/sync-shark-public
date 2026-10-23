using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncInventoryLifetimeStatus(long UnreferencedScans,long UnreferencedIntervals,
    long ExpiredRows,long ExpiredBytes,bool PlanningAccountingPending,string? HeldDetail)
{
    public string Describe()=>$"Inventory detail: {UnreferencedScans:N0} unreferenced scans and {UnreferencedIntervals:N0} closed ranges; " +
        $"{ExpiredRows:N0} metadata rows expired after seven days. Current inventories and exact consumers remain retained." +
        (PlanningAccountingPending?" Predecessor planning-reference accounting is pending.":"")+
        (HeldDetail is null?"":" "+HeldDetail);
}
public sealed record SyncInventoryRetirementResult(int ExaminedRows,int RetiredRows,long RetiredBytes);

public sealed partial class SyncAuthorityDatabase
{
    private static async Task<SyncInventoryLifetimeStatus> LoadInventoryLifetimeStatusCoreAsync(
        SqliteConnection connection,SqliteTransaction? tx,CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT inventory_unreferenced_scans,inventory_unreferenced_intervals,inventory_expired_rows,
                inventory_expired_bytes,EXISTS(SELECT 1 FROM operation WHERE journal_scan_accounted=0),
                inventory_lifetime_last_hold FROM installation;
            """;
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token))throw new InvalidDataException("Inventory lifetime has no installation.");
        return new(rows.GetInt64(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3),rows.GetInt64(4)!=0,
            rows.IsDBNull(5)?null:rows.GetString(5));
    }

    private static readonly string ScanHasNoConsumers="s.planning_reference_count=0 AND "+
        string.Join(" AND ",SyncScanRetentionSchema.References.Select(reference=>
            $"NOT EXISTS(SELECT 1 FROM {reference.Table} WHERE {reference.Expression}=s.scan_generation_id AND {reference.Predicate})"));

    private static readonly string ScanCanRetire=ScanHasNoConsumers+"""
         AND NOT EXISTS(SELECT 1 FROM operation WHERE journal_scan_accounted=0)
         AND EXISTS(SELECT 1 FROM scan_generation newer WHERE newer.root_binding_id=s.root_binding_id
             AND newer.generation>s.generation)
         AND (s.lifetime_retiring=1 OR (s.status='failed' AND s.ended_utc IS NOT NULL)
             OR (s.status='complete' AND (SELECT count(*) FROM
                 (SELECT 1 FROM scan_generation newer WHERE newer.root_binding_id=s.root_binding_id
                    AND newer.status='complete' AND newer.generation>s.generation LIMIT 2))=2))
        """;

    private static readonly string IntervalHasNoConsumers="""
        i.to_generation IS NOT NULL
        AND NOT EXISTS(SELECT 1 FROM observation_delta d WHERE d.expected_prior_interval_id=i.interval_id)
        AND NOT EXISTS(SELECT 1 FROM observation_cut c JOIN scan_generation s ON s.scan_generation_id=c.scan_generation_id
            WHERE c.root_binding_id=i.root_binding_id AND c.state='published' AND s.status='complete'
                AND c.generation>=i.from_generation AND c.generation<i.to_generation)
        AND NOT EXISTS(SELECT 1 FROM observation_cut c JOIN scan_generation s ON s.scan_generation_id=c.scan_generation_id
            WHERE c.root_binding_id=i.root_binding_id AND c.state IN ('open','sealed') AND s.status='incomplete'
                AND c.captured_parent_generation>=i.from_generation AND c.captured_parent_generation<i.to_generation)
        """;

    private Task ClassifyScanLifetimeCoreAsync(SqliteConnection connection,SqliteTransaction tx,string id,CancellationToken token)=>
        ExecuteAsync(connection,tx,$"""
            UPDATE scan_generation AS s SET lifetime_accounted=1,
                unreferenced_since_utc=CASE WHEN {ScanCanRetire}
                    THEN coalesce(unreferenced_since_utc,$now) ELSE NULL END
            WHERE scan_generation_id=$id;
            """,token,("$id",id),("$now",Utc(ActivityRetentionNow)));

    private async Task ClassifyIntervalLifetimeCoreAsync(SqliteConnection connection,SqliteTransaction tx,
        HistoryRetirementScope scope,string id,CancellationToken token)
    {
        scope.Objects.Add(("observation_interval",id));
        await ExecuteAsync(connection,tx,$"""
            UPDATE observation_interval AS i SET unreferenced_since_utc=CASE WHEN {IntervalHasNoConsumers}
                THEN coalesce(unreferenced_since_utc,$now) ELSE NULL END WHERE interval_id=$id;
            """,token,("$id",id),("$now",Utc(ActivityRetentionNow)));
        scope.Objects.Remove(("observation_interval",id));
    }

    // Runs in the existing idle history transaction. The complete cut is first made unavailable
    // under the same checked writer; large owned overlays then drain without exposing partial scans.
    private async Task<SyncInventoryRetirementResult> RetireInventoryDetailsCoreAsync(SqliteConnection connection,
        SqliteTransaction tx,HistoryRetirementScope scope,Stopwatch clock,CancellationToken token,long priorBytes)
    {
        var examined=0;var retired=0;long bytes=0;
        for(var i=0;i<10 && (i==0 || clock.ElapsedMilliseconds<40);i++)
        {
            var action=await ScalarStringAsync(connection,tx,
                "SELECT operation_id FROM operation WHERE journal_scan_accounted=0 ORDER BY operation_id LIMIT 1;",token);
            if(action is null)break;
            await ExecuteAsync(connection,tx,"UPDATE operation SET journal_scan_accounted=1 WHERE operation_id=$id;",
                token,("$id",action));examined++;
        }
        if(await ScalarLongAsync(connection,tx,"SELECT EXISTS(SELECT 1 FROM operation WHERE journal_scan_accounted=0);",token)!=0)
            return new(examined,0,0);
        var slot=await ScalarLongAsync(connection,tx,"SELECT inventory_lifetime_slot FROM installation;",token);
        await ExecuteAsync(connection,tx,"UPDATE installation SET inventory_lifetime_slot=1-inventory_lifetime_slot;",token);
        var cutoff=Utc(ActivityRetentionNow.AddDays(-7));
        if(slot==0)
        {
            for(var i=0;i<10 && (i==0 || clock.ElapsedMilliseconds<42);i++)
            {
                var id=await ScalarStringAsync(connection,tx,
                    "SELECT scan_generation_id FROM scan_generation WHERE lifetime_accounted=0 ORDER BY scan_generation_id LIMIT 1;",token);
                id??=await ScalarStringAsync(connection,tx,"""
                    SELECT scan_generation_id FROM scan_generation
                    WHERE scan_generation_id>(SELECT scan_lifetime_frontier FROM installation)
                    ORDER BY scan_generation_id LIMIT 1;
                    """,token);
                if(id is null)
                {await ExecuteAsync(connection,tx,"UPDATE installation SET scan_lifetime_frontier='';",token);break;}
                await ClassifyScanLifetimeCoreAsync(connection,tx,id,token);
                await ExecuteAsync(connection,tx,"UPDATE installation SET scan_lifetime_frontier=$id;",token,("$id",id));
                examined++;
            }
            // Alternate the resumable retiring frontier with new aged candidates; a retained
            // child's parent FK never pins unrelated content or starves another scan.
            var scan=await ScalarStringAsync(connection,tx,"""
                SELECT scan_generation_id FROM scan_generation
                WHERE lifetime_retiring=1 AND scan_generation_id>(SELECT scan_retirement_frontier FROM installation)
                ORDER BY scan_generation_id LIMIT 1;
                """,token);
            if(scan is null)
            {
                await ExecuteAsync(connection,tx,"UPDATE installation SET scan_retirement_frontier='';",token);
                scan=await ScalarStringAsync(connection,tx,"""
                    SELECT scan_generation_id FROM scan_generation WHERE lifetime_retiring=0
                        AND unreferenced_since_utc IS NOT NULL AND unreferenced_since_utc<=$cutoff
                    ORDER BY unreferenced_since_utc,scan_generation_id LIMIT 1;
                    """,token,("$cutoff",cutoff));
            }
            if(scan is not null)
            {
                examined++;
                await ClassifyScanLifetimeCoreAsync(connection,tx,scan,token);
                if(await ScalarLongAsync(connection,tx,"""
                    SELECT count(*) FROM scan_generation WHERE scan_generation_id=$id
                        AND unreferenced_since_utc<=$cutoff;
                    """,token,("$id",scan),("$cutoff",cutoff))==1)
                {
                    scope.Objects.Add(("scan_generation",scan));
                    await ExecuteAsync(connection,tx,"""
                        UPDATE scan_generation SET lifetime_retiring=1,
                            status=CASE WHEN status='complete' THEN 'superseded' ELSE status END
                        WHERE scan_generation_id=$id;
                        """,token,("$id",scan));
                    foreach(var (table,key,size) in new[]{
                        ("observation_delta","delta_id","coalesce(length(CAST(evidence_json AS BLOB)),0)+length(CAST(semantic_key AS BLOB))+length(delta_id)+256"),
                        ("observation","observation_id","length(CAST(evidence_json AS BLOB))+length(observation_id)+256"),
                        ("observation_cut","cut_id","length(cut_id)+length(cut_digest)+512"),
                        ("scan_generation","scan_generation_id","length(CAST(scope_json AS BLOB))+length(scan_generation_id)+512")})
                    {
                        while(examined<60 && (retired==0 || clock.ElapsedMilliseconds<48))
                        {
                            var extra=table=="observation_cut"?
                                " AND NOT EXISTS(SELECT 1 FROM observation_delta d WHERE d.cut_id=observation_cut.cut_id)":
                                table=="scan_generation"?"""
                                     AND NOT EXISTS(SELECT 1 FROM observation_cut c WHERE c.scan_generation_id=scan_generation.scan_generation_id
                                         OR c.captured_parent_scan_id=scan_generation.scan_generation_id)
                                     AND NOT EXISTS(SELECT 1 FROM observation o WHERE o.scan_generation_id=scan_generation.scan_generation_id)
                                     AND NOT EXISTS(SELECT 1 FROM observation_delta d WHERE d.scan_generation_id=scan_generation.scan_generation_id)
                                    """:"";
                            // One small SQL batch amortizes checkout/statement cost.
                            // Exact row permits, shared row/byte limits and the writer
                            // transaction are unchanged; no arbitrary table/id API exists.
                            var candidates=await ReadInventoryRetirementRowsAsync(connection,tx,
                                $"SELECT {key},{size} FROM {table} WHERE scan_generation_id=$id{extra} ORDER BY {key} LIMIT $limit;",
                                scan,Math.Min(16,60-examined),token);
                            if(candidates.Count==0)break;
                            var selected=new List<(string Id,long Bytes)>();long selectedBytes=0;
                            foreach(var row in candidates)
                            {
                                if(priorBytes+bytes+selectedBytes+row.Bytes>1_048_576)
                                {
                                    if(row.Bytes>1_048_576)
                                        await ExecuteAsync(connection,tx,"""
                                            UPDATE installation SET inventory_lifetime_last_hold=
                                                'An inventory detail larger than one maintenance payload remains held.';
                                            """,token);
                                    break;
                                }
                                selected.Add(row);selectedBytes=checked(selectedBytes+row.Bytes);
                            }
                            if(selected.Count==0)break;
                            examined+=selected.Count;
                            try
                            {
                                foreach(var row in selected)scope.Objects.Add((table,row.Id));
                                foreach(var row in selected)
                                    ActivityRetirementBoundaryForTest?.Invoke("exact-inventory-before-delete");
                                var parameters=selected.Select((row,index)=>("$row"+index,(object?)row.Id)).ToArray();
                                var names=string.Join(",",parameters.Select(item=>item.Item1));
                                await ExecuteAsync(connection,tx,
                                    $"DELETE FROM {table} WHERE {key} IN ({names});",token,parameters);
                                var removed=await ScalarLongAsync(connection,tx,"SELECT changes();",token);
                                if(removed!=selected.Count)
                                    throw new InvalidDataException("The exact inventory retirement batch changed.");
                            }
                            finally
                            {
                                foreach(var row in selected)scope.Objects.Remove((table,row.Id));
                            }
                            retired+=selected.Count;bytes=checked(bytes+selectedBytes);
                        }
                        // Do not advance to the parent until all owned children have drained.
                        if(table is "observation_delta" or "observation")
                        {
                            if(await ScalarLongAsync(connection,tx,$"SELECT EXISTS(SELECT 1 FROM {table} WHERE scan_generation_id=$id);",
                                token,("$id",scan))!=0)break;
                        }
                    }
                    scope.Objects.Remove(("scan_generation",scan));
                }
                await ExecuteAsync(connection,tx,"UPDATE installation SET scan_retirement_frontier=$id;",token,("$id",scan));
            }
        }
        else
        {
            for(var i=0;i<10 && (i==0 || clock.ElapsedMilliseconds<42);i++)
            {
                var id=await ScalarStringAsync(connection,tx,"""
                    SELECT interval_id FROM observation_interval WHERE to_generation IS NOT NULL
                        AND interval_id>(SELECT interval_lifetime_frontier FROM installation)
                    ORDER BY interval_id LIMIT 1;
                    """,token);
                if(id is null)
                {await ExecuteAsync(connection,tx,"UPDATE installation SET interval_lifetime_frontier='';",token);break;}
                await ClassifyIntervalLifetimeCoreAsync(connection,tx,scope,id,token);
                await ExecuteAsync(connection,tx,"UPDATE installation SET interval_lifetime_frontier=$id;",token,("$id",id));
                examined++;
            }
            while(examined<60 && (retired==0 || clock.ElapsedMilliseconds<48))
            {
                var row=await ReadInventoryRetirementRowAsync(connection,tx,"""
                    SELECT interval_id,length(CAST(evidence_json AS BLOB))+length(CAST(semantic_key AS BLOB))
                        +length(interval_id)+length(observation_id)+384
                    FROM observation_interval WHERE unreferenced_since_utc IS NOT NULL
                        AND unreferenced_since_utc<=$id ORDER BY unreferenced_since_utc,interval_id LIMIT 1;
                    """,cutoff,token);
                if(row is null)break;
                examined++;
                await ClassifyIntervalLifetimeCoreAsync(connection,tx,scope,row.Value.Id,token);
                if(await ScalarLongAsync(connection,tx,"""
                    SELECT count(*) FROM observation_interval WHERE interval_id=$id AND unreferenced_since_utc<=$cutoff;
                    """,token,("$id",row.Value.Id),("$cutoff",cutoff))!=1)continue;
                if(priorBytes+bytes+row.Value.Bytes>1_048_576)break;
                scope.Objects.Add(("observation_interval",row.Value.Id));
                ActivityRetirementBoundaryForTest?.Invoke("exact-inventory-before-delete");
                await ExecuteAsync(connection,tx,"DELETE FROM observation_interval WHERE interval_id=$id;",
                    token,("$id",row.Value.Id));
                scope.Objects.Remove(("observation_interval",row.Value.Id));
                retired++;bytes+=row.Value.Bytes;
            }
        }
        await ExecuteAsync(connection,tx,"""
            UPDATE installation SET inventory_expired_rows=inventory_expired_rows+$rows,
                inventory_expired_bytes=inventory_expired_bytes+$bytes;
            """,token,("$rows",retired),("$bytes",bytes));
        return new(examined,retired,bytes);
    }

    private static async Task<List<(string Id,long Bytes)>> ReadInventoryRetirementRowsAsync(
        SqliteConnection connection,SqliteTransaction tx,string sql,string id,int limit,CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;q.CommandText=sql;
        q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$limit",limit);
        var result=new List<(string Id,long Bytes)>();
        await using var rows=await q.ExecuteReaderAsync(token);
        while(await rows.ReadAsync(token))result.Add((rows.GetString(0),rows.GetInt64(1)));
        return result;
    }

    private static async Task<(string Id,long Bytes)?> ReadInventoryRetirementRowAsync(SqliteConnection connection,
        SqliteTransaction tx,string sql,string id,CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;q.CommandText=sql;
        q.Parameters.AddWithValue("$id",id);
        await using var row=await q.ExecuteReaderAsync(token);
        return await row.ReadAsync(token)?(row.GetString(0),row.GetInt64(1)):null;
    }
}

