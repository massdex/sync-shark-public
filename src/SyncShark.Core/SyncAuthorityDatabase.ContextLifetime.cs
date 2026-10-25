using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncContextLifetimeStatus(long UnreferencedContexts, long UnreferencedBytes,
    long ExpiredContexts, bool AccountingPending)
{
    public string Describe() => $"Unreferenced activity context: {UnreferencedContexts:N0} snapshots, " +
        $"{UnreferencedBytes:N0} encoded bytes; {ExpiredContexts:N0} expired after seven days without a reference." +
        (AccountingPending ? " Older or newly released context is awaiting bounded classification." : "");
}
public sealed record SyncContextRetirementResult(int ExaminedRows, int RetiredContexts, long RetiredBytes);

public sealed partial class SyncAuthorityDatabase
{
    private static async Task<SyncContextLifetimeStatus> LoadContextLifetimeStatusCoreAsync(
        SqliteConnection connection, SqliteTransaction? tx, CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT unreferenced_context_count,unreferenced_context_bytes,expired_context_count,
                EXISTS(SELECT 1 FROM activity_context WHERE lifetime_accounted=0) FROM installation;
            """;
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token))throw new InvalidDataException("Context lifetime has no installation.");
        return new(rows.GetInt64(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3)!=0);
    }

    // Shares the existing history transaction, private context capability and hard budgets.
    // A released predecessor starts its seven days when first observed unreferenced, never
    // from its original creation date. A new reference resets this clock atomically.
    private async Task<SyncContextRetirementResult> RetireUnreferencedContextsCoreAsync(
        SqliteConnection connection, SqliteTransaction tx, HistoryRetirementScope scope,
        Stopwatch clock, CancellationToken token)
    {
        var examined=0;var retired=0;long bytes=0;
        while(examined<20 && (examined==0 || clock.ElapsedMilliseconds<15))
        {
            var key=await ScalarStringAsync(connection,tx,
                "SELECT context_key FROM activity_context WHERE lifetime_accounted=0 ORDER BY context_key LIMIT 1;",token);
            if(key is null)break;
            await ExecuteAsync(connection,tx,"""
                UPDATE activity_context SET lifetime_accounted=1,
                    unreferenced_since_utc=CASE WHEN ordinary_references=0 AND diagnostic_references=0
                      AND NOT EXISTS(SELECT 1 FROM history_event WHERE activity_context_key=$key)
                      THEN $now ELSE NULL END
                WHERE context_key=$key;
                """,token,("$key",key),("$now",Utc(ActivityRetentionNow)));
            examined++;
        }
        while(examined<40 && (examined==0 || clock.ElapsedMilliseconds<25))
        {
            string? key=null;long size=0;
            await using(var q=connection.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT context_key,encoded_bytes FROM activity_context
                    WHERE unreferenced_since_utc IS NOT NULL AND encoded_bytes<=1048576
                      AND unreferenced_since_utc<=$cutoff
                    ORDER BY unreferenced_since_utc,context_key LIMIT 1;
                    """;
                q.Parameters.AddWithValue("$cutoff",Utc(ActivityRetentionNow.AddDays(-7)));
                await using var rows=await q.ExecuteReaderAsync(token);
                if(await rows.ReadAsync(token)){key=rows.GetString(0);size=rows.GetInt64(1);}
            }
            if(key is null || bytes+size>1_048_576)break;
            examined++;
            if(await ScalarLongAsync(connection,tx,"""
                SELECT count(*) FROM activity_context WHERE context_key=$key
                  AND ordinary_references=0 AND diagnostic_references=0
                  AND NOT EXISTS(SELECT 1 FROM history_event WHERE activity_context_key=$key);
                """,token,("$key",key))!=1)
            {
                await ExecuteAsync(connection,tx,
                    "UPDATE activity_context SET unreferenced_since_utc=NULL WHERE context_key=$key;",token,("$key",key));
                continue;
            }
            scope.Contexts.Add(key);
            ActivityRetirementBoundaryForTest?.Invoke("exact-context-before-delete");
            await ExecuteAsync(connection,tx,"DELETE FROM activity_context WHERE context_key=$key;",token,("$key",key));
            scope.Contexts.Remove(key);
            retired++;bytes+=size;
        }
        return new(examined,retired,bytes);
    }
}
