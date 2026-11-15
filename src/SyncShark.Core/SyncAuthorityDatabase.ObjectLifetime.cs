using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

public sealed record SyncObjectLifetimeStatus(long UnreferencedVersions,long VersionBytes,
    long UnreferencedMemberships,long MembershipBytes,long ExpiredRows,bool AccountingPending)
{
    public long BothAbsentExpiredObjects { get; init; }
    public long BothAbsentExpiredRows { get; init; }
    public string? BothAbsentHold { get; init; }
    public bool UnprovedBothAbsentHeld { get; init; }
    public string Describe() => $"Unreferenced object detail: {UnreferencedVersions:N0} versions and " +
        $"{UnreferencedMemberships:N0} older paths, {VersionBytes+MembershipBytes:N0} encoded bytes; " +
        $"{ExpiredRows:N0} rows expired after seven days. Current and required state remains retained." +
        (AccountingPending?" Older reference accounting is pending.":"") +
        $" Both-absent cleanup: {BothAbsentExpiredObjects:N0} objects and {BothAbsentExpiredRows:N0} metadata rows retired." +
        (UnprovedBothAbsentHeld?" Older absence records without current closure proof remain retained.":"") +
        (BothAbsentHold is null?"":" "+DescribeAbsenceHold(BothAbsentHold));
    private static string DescribeAbsenceHold(string reason) => reason switch
    {
        "ninety_day_grace" => "Some absent-file metadata is still within its ninety-day retention period.",
        "two_fresh_complete_observations_required" => "Cleanup needs two later complete observations of both locations.",
        "retained_operation" => "A retained operation still needs some absent-file metadata.",
        "retained_original_choice" or "retained_legacy_selection" => "An original selection still needs some absent-file metadata.",
        "active_rewind" => "An active Rewind session still needs some absent-file metadata.",
        "accepted_point_protects_older_baseline" => "Some accepted history remains necessary to keep an older baseline from becoming current.",
        "metadata_row_budget" => "Absent-file cleanup has reached this maintenance pass's row limit.",
        "metadata_payload_budget" => "Some absent-file metadata exceeds this maintenance pass's size limit and remains retained.",
        _ => "Some absent-file metadata remains required by current observations, configuration or recovery references."
    };
}
public sealed record SyncObjectRetirementResult(int ExaminedRows,int RetiredRows,long RetiredBytes);

public sealed partial class SyncAuthorityDatabase
{
    private static void ConfigureObjectRetirementFunction(SqliteConnection connection,HistoryRetirementScope scope)
    {
        var code=SQLitePCL.raw.sqlite3_create_function(connection.Handle!,"syncshark_object_retirement_allowed",2,
            SQLitePCL.raw.SQLITE_UTF8|0x00200000,scope,(context,state,values)=>
                SQLitePCL.raw.sqlite3_result_int(context,values.Length==2 && state is HistoryRetirementScope permission &&
                    permission.Objects.Contains((SQLitePCL.raw.sqlite3_value_text(values[0]).utf8_to_string()??"",
                        SQLitePCL.raw.sqlite3_value_text(values[1]).utf8_to_string()??""))?1:0));
        if(code!=SQLitePCL.raw.SQLITE_OK)throw new InvalidDataException("Object retirement guard could not be registered.");
    }

    private static async Task<SyncObjectLifetimeStatus> LoadObjectLifetimeStatusCoreAsync(
        SqliteConnection connection,SqliteTransaction? tx,CancellationToken token)
    {
        await using var q=connection.CreateCommand();q.Transaction=tx;
        q.CommandText="""
            SELECT object_version_unreferenced_count,object_version_unreferenced_bytes,
                membership_unreferenced_count,membership_unreferenced_bytes,
                object_version_expired_count+membership_expired_count,
                EXISTS(SELECT 1 FROM object_version WHERE lifetime_accounted=0)
                    OR EXISTS(SELECT 1 FROM membership WHERE lifetime_accounted=0),
                both_absent_expired_objects,both_absent_expired_rows,both_absent_last_hold,
                EXISTS(SELECT 1 FROM tombstone WHERE absent_side='both' AND both_absent_basis_json IS NULL)
            FROM installation;
            """;
        await using var rows=await q.ExecuteReaderAsync(token);
        if(!await rows.ReadAsync(token))throw new InvalidDataException("Object lifetime has no installation.");
        return new(rows.GetInt64(0),rows.GetInt64(1),rows.GetInt64(2),rows.GetInt64(3),rows.GetInt64(4),rows.GetInt64(5)!=0)
        { BothAbsentExpiredObjects=rows.GetInt64(6), BothAbsentExpiredRows=rows.GetInt64(7),
          BothAbsentHold=rows.IsDBNull(8)?null:rows.GetString(8), UnprovedBothAbsentHeld=rows.GetInt64(9)!=0 };
    }

    private static string ObjectDetailUnreferenced(SyncObjectLifetimeSchema.Target target)
    {
        var predicate=string.Join(" AND ",target.References.Select(reference=>
            $"NOT EXISTS(SELECT 1 FROM {reference.Table} r WHERE r.{reference.Column}=t.{target.Key})"));
        predicate+="""
             AND NOT EXISTS(SELECT 1 FROM operation o WHERE o.object_id=t.object_id)
             AND NOT EXISTS(SELECT 1 FROM conflict c WHERE c.object_id=t.object_id AND c.status NOT IN ('resolved','superseded'))
             AND NOT EXISTS(SELECT 1 FROM recovery_artifact a WHERE a.object_id=t.object_id AND a.status<>'released')
            """;
        if(target.Table=="membership")
            predicate+="""
                 AND EXISTS(SELECT 1 FROM membership newer WHERE newer.root_binding_id=t.root_binding_id
                   AND newer.relative_path_key=t.relative_path_key AND newer.generation>t.generation)
                """;
        else
            predicate+="""
                 AND (EXISTS(SELECT 1 FROM object_version newer WHERE newer.object_id=t.object_id
                        AND newer.side=t.side AND (newer.observed_utc,newer.object_version_id)>(t.observed_utc,t.object_version_id))
                    OR NOT EXISTS(SELECT 1 FROM membership m WHERE m.object_id=t.object_id
                        AND m.state IN ('active','provisional','ambiguous')
                        AND m.generation=(SELECT max(n.generation) FROM membership n
                            WHERE n.root_binding_id=m.root_binding_id AND n.relative_path_key=m.relative_path_key)))
                """;
        return predicate;
    }

    private async Task ClassifyObjectDetailCoreAsync(SqliteConnection connection,SqliteTransaction tx,
        SyncObjectLifetimeSchema.Target target,string id,string predicate,CancellationToken token)
    {
        var t=target.Table;var key=target.Key;
        var size=string.Join("+",target.Scalars.Select(column=>$"coalesce(length(CAST(t.{column} AS BLOB)),0)"))+"+128";
        await ExecuteAsync(connection,tx,$"""
            UPDATE {t} AS t SET lifetime_accounted=1,lifetime_encoded_bytes={size},
                unreferenced_since_utc=CASE WHEN ({size})<=1048576 AND {predicate}
                    THEN CASE WHEN t.unreferenced_since_utc IS NOT NULL
                        AND t.lifetime_reference_epoch=(SELECT lifetime_reference_epoch FROM logical_object WHERE object_id=t.object_id)
                        THEN t.unreferenced_since_utc ELSE $now END
                    ELSE NULL END,
                lifetime_reference_epoch=(SELECT lifetime_reference_epoch FROM logical_object WHERE object_id=t.object_id)
            WHERE t.{key}=$id;
            """,token,("$id",id),("$now",Utc(ActivityRetentionNow)));
    }

    // Existing history slot and writer transaction; one target class alternates each pass.
    // Reverse-FK changes invalidate exact rows. Object-scoped reference epochs also detect
    // a reference acquired AND released between two maintenance visits.
    private async Task<SyncObjectRetirementResult> RetireUnreferencedObjectDetailsCoreAsync(
        SqliteConnection connection,SqliteTransaction tx,HistoryRetirementScope scope,Stopwatch clock,CancellationToken token,
        long alreadyTouchedBytes)
    {
        var slot=(int)await ScalarLongAsync(connection,tx,"SELECT object_lifetime_slot FROM installation;",token);
        var target=SyncObjectLifetimeSchema.Targets[slot];
        var t=target.Table;var key=target.Key;var predicate=ObjectDetailUnreferenced(target);
        await ExecuteAsync(connection,tx,"UPDATE installation SET object_lifetime_slot=1-object_lifetime_slot;",token);
        int examined=0,retired=0;long bytes=0;
        while(examined<20 && (examined==0 || clock.ElapsedMilliseconds<35))
        {
            var id=await ScalarStringAsync(connection,tx,
                $"SELECT {key} FROM {t} WHERE lifetime_accounted=0 ORDER BY {key} LIMIT 1;",token);
            id??=await ScalarStringAsync(connection,tx,
                $"SELECT {key} FROM {t} WHERE {key}>(SELECT {t}_lifetime_frontier FROM installation) ORDER BY {key} LIMIT 1;",token);
            if(id is null)
            {
                await ExecuteAsync(connection,tx,$"UPDATE installation SET {t}_lifetime_frontier='';",token);
                break;
            }
            await ClassifyObjectDetailCoreAsync(connection,tx,target,id,predicate,token);
            await ExecuteAsync(connection,tx,$"UPDATE installation SET {t}_lifetime_frontier=$id;",token,("$id",id));
            examined++;
        }
        var collectionAttempts=0;
        while(examined<40 && (collectionAttempts==0 || clock.ElapsedMilliseconds<40))
        {
            var id=await ScalarStringAsync(connection,tx,$"""
                SELECT {key} FROM {t} WHERE unreferenced_since_utc IS NOT NULL AND lifetime_encoded_bytes<=1048576
                    AND unreferenced_since_utc<=$cutoff ORDER BY unreferenced_since_utc,{key} LIMIT 1;
                """,token,("$cutoff",Utc(ActivityRetentionNow.AddDays(-7))));
            if(id is null)break;
            examined++;collectionAttempts++;
            // Current refs, newest membership/current entry and epoch are all rechecked
            // in this transaction; prior eligibility alone never grants deletion.
            await ClassifyObjectDetailCoreAsync(connection,tx,target,id,predicate,token);
            if(await ScalarLongAsync(connection,tx,$"""
                SELECT count(*) FROM {t} WHERE {key}=$id AND unreferenced_since_utc<=$cutoff;
                """,token,("$id",id),("$cutoff",Utc(ActivityRetentionNow.AddDays(-7))))!=1)continue;
            var size=await ScalarLongAsync(connection,tx,
                $"SELECT lifetime_encoded_bytes FROM {t} WHERE {key}=$id;",token,("$id",id));
            if(alreadyTouchedBytes+bytes+size>1_048_576)break;
            scope.Objects.Add((t,id));
            ActivityRetirementBoundaryForTest?.Invoke("exact-object-before-delete");
            await ExecuteAsync(connection,tx,$"DELETE FROM {t} WHERE {key}=$id;",token,("$id",id));
            scope.Objects.Remove((t,id));retired++;bytes+=size;
        }
        var absence=await RetireBothAbsentDetailsCoreAsync(connection,tx,scope,clock,
            alreadyTouchedBytes+bytes,token);
        return new(examined+absence.ExaminedRows,retired+absence.RetiredRows,bytes+absence.RetiredBytes);
    }
}
