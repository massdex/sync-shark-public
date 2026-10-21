using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncMonitorLifetimeStatus(long RetainedEvents,long RetainedBytes,long ExpiredEvents,bool LegacyHeld)
{
    public string Describe()=>$"{RetainedEvents:N0} monitor diagnostics retained ({RetainedBytes:N0} encoded bytes); "+
        $"{ExpiredEvents:N0} expired. Current change hints remain retained."+
        (LegacyHeld?" Legacy monitor evidence awaits exact dirty-state coverage.":"");
}
public sealed record SyncMonitorRetirementResult(int ExaminedRows,int RetiredRows,long RetiredBytes);

public sealed partial class SyncAuthorityDatabase
{
    private static async Task<SyncMonitorLifetimeStatus> LoadMonitorLifetimeStatusCoreAsync(
        SqliteConnection connection,SqliteTransaction? tx,CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT monitor_retained_count,monitor_retained_bytes,monitor_expired_count,
                EXISTS(SELECT 1 FROM monitor_event WHERE lifetime_class='legacy')
            FROM installation;
            """;
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token))throw new InvalidDataException("The monitor has no installation lifetime state.");
        return new(rows.GetInt64(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3)!=0);
    }

    // Shares the existing idle history transaction, row permits and cooperative budget.
    // Event eviction never deletes/clears dirty state. Old callbacks are fenced by
    // the current cursor epoch; retained last-batch proof is independent of this log.
    private async Task<SyncMonitorRetirementResult> RetireMonitorDetailsCoreAsync(SqliteConnection connection,
        SqliteTransaction tx,HistoryRetirementScope scope,Stopwatch clock,CancellationToken token,
        int priorRows,long priorBytes)
    {
        var examined=0;var retired=0;long bytes=0;
        var now=Utc(ActivityRetentionNow);var cutoff=Utc(ActivityRetentionNow.AddDays(-7));
        for(var i=0;i<8&&priorRows+examined<200&&(i==0||clock.ElapsedMilliseconds<40);i++)
        {
            var id=await ScalarStringAsync(connection,tx,"""
                SELECT monitor_event_id FROM monitor_event
                WHERE lifetime_class='legacy' AND monitor_event_id>(SELECT monitor_lifetime_frontier FROM installation)
                ORDER BY monitor_event_id LIMIT 1;
                """,token);
            if(id is null)
            {await ExecuteAsync(connection,tx,"UPDATE installation SET monitor_lifetime_frontier='';",token);break;}
            examined++;
            // No whole-history import at startup. Adopt only a bounded exact row
            // whose durable dirty state already contains its complete remaining hint,
            // or whose v17 generation-safe completed observation closed that state.
            await ExecuteAsync(connection,tx,"""
                UPDATE monitor_event AS e SET lifetime_class='eligible',
                    encoded_bytes=length(CAST(event_json AS BLOB))+length(CAST(monitor_event_id AS BLOB))+
                        length(CAST(source_event_key AS BLOB))+length(CAST(coalesce(normalized_path_key,'') AS BLOB))+
                        length(CAST(pair_id AS BLOB))+length(CAST(coalesce(monitor_cursor_id,'') AS BLOB))+128
                WHERE monitor_event_id=$id AND lifetime_class='legacy'
                    AND EXISTS(SELECT 1 FROM monitor_cursor c WHERE c.monitor_cursor_id=e.monitor_cursor_id
                        AND c.pair_id=e.pair_id AND c.writer_epoch IS NOT NULL)
                    AND EXISTS(SELECT 1 FROM dirty_item d WHERE d.pair_id=e.pair_id
                        AND d.normalized_path_key IS e.normalized_path_key AND
                        ((d.status='dirty' AND d.earliest_event_utc<=e.received_utc AND d.latest_event_utc>=e.received_utc)
                         OR (d.status='closed' AND d.closed_utc IS NOT NULL AND e.consumed_utc IS NOT NULL)));
                """,token,("$id",id));
            await ExecuteAsync(connection,tx,"UPDATE installation SET monitor_lifetime_frontier=$id;",token,("$id",id));
        }
        while(priorRows+examined<200&&(retired==0||clock.ElapsedMilliseconds<50))
        {
            string? id=null;long charge=0;
            await using(var q=connection.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT monitor_event_id,encoded_bytes FROM monitor_event
                    WHERE lifetime_class='eligible' AND encoded_bytes<=$available
                        AND (received_utc<=$cutoff OR
                            (SELECT monitor_retained_count>10000 OR monitor_retained_bytes>8388608 FROM installation))
                    ORDER BY received_utc,monitor_event_id LIMIT 1;
                    """;
                q.Parameters.AddWithValue("$available",Math.Max(0,1_048_576-priorBytes-bytes));
                q.Parameters.AddWithValue("$cutoff",cutoff);
                await using var rows=await q.ExecuteReaderAsync(token);
                if(await rows.ReadAsync(token)){id=rows.GetString(0);charge=rows.GetInt64(1);}
            }
            if(id is null)break;
            examined++;
            scope.Objects.Add(("monitor_event",id));
            try
            {
                await ExecuteAsync(connection,tx,"DELETE FROM monitor_event WHERE monitor_event_id=$id;",token,("$id",id));
                retired++;bytes+=charge;
            }
            finally{scope.Objects.Remove(("monitor_event",id));}
        }
        // Only compact closed markers without any decision, active observation or
        // current monitor uncertainty. A later event reopens the existing marker and
        // resets closed_utc in the same transaction, so this cannot erase a new hint.
        if(priorRows+examined<200&&clock.ElapsedMilliseconds<50)
        {
            string? id=null;long charge=0;
            await using(var q=connection.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT d.dirty_item_id,length(CAST(d.dirty_item_id AS BLOB))+
                        length(CAST(coalesce(d.normalized_path_key,'') AS BLOB))+length(CAST(d.pair_id AS BLOB))+256 AS bytes
                    FROM dirty_item d WHERE d.status='closed' AND d.closed_utc<=$cutoff AND bytes<=$available
                        AND NOT EXISTS(SELECT 1 FROM reconciliation_decision r WHERE r.dirty_item_id=d.dirty_item_id)
                        AND NOT EXISTS(SELECT 1 FROM scan_generation s WHERE s.pair_id=d.pair_id AND s.status='incomplete')
                        AND NOT EXISTS(SELECT 1 FROM monitor_cursor c WHERE c.pair_id=d.pair_id AND c.status NOT IN ('active','retired'))
                    ORDER BY d.closed_utc,d.dirty_item_id LIMIT 1;
                    """;
                q.Parameters.AddWithValue("$cutoff",cutoff);
                q.Parameters.AddWithValue("$available",Math.Max(0,1_048_576-priorBytes-bytes));
                await using var rows=await q.ExecuteReaderAsync(token);
                if(await rows.ReadAsync(token)){id=rows.GetString(0);charge=rows.GetInt64(1);}
            }
            if(id is not null)
            {
                examined++;scope.Objects.Add(("dirty_item",id));
                try{await ExecuteAsync(connection,tx,"DELETE FROM dirty_item WHERE dirty_item_id=$id;",token,("$id",id));retired++;bytes+=charge;}
                finally{scope.Objects.Remove(("dirty_item",id));}
            }
        }
        return new(examined,retired,bytes);
    }
}

