using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns pair configuration, source boundaries and exact unfinished operations.</summary>
public sealed class PairTable
{
    internal const string PairKey = "(SELECT id FROM pair WHERE pair_id=$p)";
    private static async Task<int> Write(SqliteConnection db, SqliteTransaction? tx, string sql, CancellationToken ct, params (string,object?)[] args)
    {
        using var cmd=StorageCommand.Create(db,tx,sql,args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<object?> Value(SqliteConnection db, SqliteTransaction? tx, string sql, CancellationToken ct, params (string,object?)[] args)
    {
        using var cmd=StorageCommand.Create(db,tx,sql,args);
        return await cmd.ExecuteScalarAsync(ct);
    }
    private static async Task<PairConfig?> Pair(SqliteConnection db,SqliteTransaction? tx,string id,CancellationToken ct) =>
        await Value(db,tx,"SELECT def_json FROM pair WHERE pair_id=$p",ct,("$p",id)) is string json ? Encoding.DecodePair(json) : null;

    /// <summary>Read one stored pair by its public ID.</summary>
    public Task<PairConfig?> ReadAsync(SqliteConnection db,string pairId,CancellationToken ct) => Pair(db,null,pairId,ct);

    /// <summary>Check removal evidence and publish the edit and returned scopes in the caller's immediate transaction, advancing the owner's revision even for an already-owed scope.</summary>
    public async Task<TextKey?> PublishAsync(SqliteTransaction tx,string pairId,PairConfig? replacement,IReadOnlyList<(string Owner,Scope Scope)> returnedScopes,CancellationToken ct)
    {
        var db=tx.Connection!;
        if(replacement is null)
        {
            var guard=await Value(db,tx,"""
                SELECT CASE WHEN EXISTS(SELECT 1 FROM accepted_job WHERE pair_id=p.id) THEN $job
                            WHEN pending_json IS NOT NULL THEN $armed
                            WHEN EXISTS(SELECT 1 FROM issue WHERE pair_id=p.id AND kind=$kind) THEN $put END
                FROM pair p WHERE pair_id=$p
                """,ct,("$job",(int)TextKey.PairJobOutstanding),("$armed",(int)TextKey.PairPcNoteOutstanding),
                ("$put",(int)TextKey.PairPutBackOutstanding),("$kind",(int)NoticeKind.PutBack),("$p",pairId));
            if(guard is null) return TextKey.PairUnknown;
            if(guard is long cause) return (TextKey)cause;
            await Write(db,tx,"DELETE FROM pair WHERE pair_id=$p",ct,("$p",pairId));
        }
        else
        {
            if(replacement.PairId!=pairId) throw new ArgumentException(nameof(replacement));
            var existing=await Pair(db,tx,pairId,ct);
            var saved=replacement with { OwedScopes=existing?.OwedScopes };
            await Write(db,tx,"INSERT INTO pair(pair_id,def_json) VALUES($p,$json) ON CONFLICT(pair_id) DO UPDATE SET def_json=excluded.def_json",ct,("$p",pairId),("$json",JsonSerializer.Serialize(saved)));
        }
        foreach(var group in returnedScopes.GroupBy(s=>s.Owner))
        {
            var owner=await Pair(db,tx,group.Key,ct) ?? throw new InvalidDataException();
            var scopes=(owner.OwedScopes ?? []).Concat(group.Select(s=>s.Scope)).Distinct().ToArray();
            await Write(db,tx,"UPDATE pair SET def_json=$json WHERE pair_id=$p",ct,("$json",JsonSerializer.Serialize(owner with { OwedScopes=scopes })),("$p",group.Key));
            await Advance(db,tx,group.Key,Side.Dropbox,null,ct);
        }
        return null;
    }
    private const string Revision="coalesce((SELECT repair_revision FROM source_state WHERE pair_id=pair.id AND side=";
    /// <summary>Only a complete Repair matching both captured revisions publishes first_repair_done and its summary and clears both reasons.</summary>
    public async Task<bool> FinishRepairAsync(SqliteTransaction tx,string pairId,(long Local,long Dropbox) revisions,string completedRepairJson,CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completedRepairJson);
        var db=tx.Connection!;
        if(await Write(db,tx,"UPDATE pair SET first_repair_done=1,last_repair_json=$json WHERE pair_id=$p AND "+Revision+"0),0)=$local AND "+Revision+"1),0)=$cloud",ct,("$p",pairId),("$local",revisions.Local),("$cloud",revisions.Dropbox),("$json",completedRepairJson))==0) return false;
        await Write(db,tx,"UPDATE source_state SET repair_reason=NULL WHERE pair_id="+PairKey,ct,("$p",pairId));
        return true;
    }
    /// <summary>Retire only this mapped scope at the captured Dropbox revision, without publishing a full Repair.</summary>
    public async Task<bool> FinishRepairAsync(SqliteTransaction tx,string pairId,Scope scope,long revision,CancellationToken ct)
    {
        var db=tx.Connection!; var pair=await Pair(db,tx,pairId,ct);
        if(pair?.OwedScopes is not { } owed || !owed.Contains(scope)) return false;
        return await Write(db,tx,"UPDATE pair SET def_json=$json WHERE pair_id=$p AND "+Revision+"1),0)=$rev",ct,("$p",pairId),("$rev",revision),("$json",JsonSerializer.Serialize(pair with { OwedScopes=owed.Where(s=>s!=scope).ToArray() })))!=0;
    }
    /// <summary>Read the last completed full Repair and first-run flag together.</summary>
    public async Task<(bool FirstRepairDone,string? LastRepairJson)> RepairStateAsync(SqliteConnection db,string pairId,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,"SELECT first_repair_done,last_repair_json FROM pair WHERE pair_id=$p","$p",pairId); using var rd=await cmd.ExecuteReaderAsync(ct);
        if(!await rd.ReadAsync(ct)) throw new KeyNotFoundException();
        return (rd.GetBoolean(0),rd.IsDBNull(1)?null:rd.GetString(1));
    }
    /// <summary>Install the fresh volume boundary and advance Local Repair debt only for the durably bound pairs in this transaction.</summary>
    public async Task ResetJournalAsync(SqliteTransaction tx,string volume,JournalPosition fresh,IReadOnlyList<string> affectedPairs,string reason,CancellationToken ct)
    {
        await Position(tx.Connection!,tx,volume,fresh,ct);
        // The caller snapshot predates the write lock. Only persisted root bindings own this cursor.
        await Write(tx.Connection!,tx,"""
            INSERT INTO source_state(pair_id,side,repair_reason,repair_revision)
            SELECT id,0,$reason,1 FROM pair WHERE def_json -> '$.Roots.Local.VolumeSerialNumber' = $volume
            ON CONFLICT(pair_id,side) DO UPDATE SET repair_reason=excluded.repair_reason,repair_revision=repair_revision+1
            """,ct,("$volume",volume),("$reason",reason));
    }
    /// <summary>Install the reset cursor and raise its owner's Dropbox Repair debt atomically.</summary>
    public async Task ResetCursorAsync(SqliteTransaction tx,string pairId,string fresh,string reason,CancellationToken ct)
    {
        await Cursor(tx.Connection!,tx,pairId,fresh,ct); await Advance(tx.Connection!,tx,pairId,Side.Dropbox,reason,ct);
    }
    /// <summary>Save the accepted job's ordered launch; a token cannot be reassigned to other members.</summary>
    public async Task SaveJobAsync(SqliteConnection db,AcceptedJob job,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        var prior=await Launch(db,tx,job,ct);
        if(prior==null) await Write(db,tx,"INSERT INTO accepted_job VALUES("+PairKey+",$route,$job,$launch)",ct,("$p",job.PairId),("$route",job.Route),("$job",job.JobId),("$launch",JsonSerializer.Serialize(job.Launch)));
        else if(!prior.SequenceEqual(job.Launch)) throw new InvalidDataException();
        tx.Commit();
    }
    /// <summary>Stream returned jobs with their original launch order.</summary>
    public async IAsyncEnumerable<AcceptedJob> JobsAsync(SqliteConnection db,string pairId,[EnumeratorCancellation]CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,"SELECT route,job_id,launch FROM accepted_job WHERE pair_id="+PairKey,"$p",pairId); using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct)) yield return new(pairId,rd.GetString(0),rd.GetString(1),DecodeLaunch(rd.GetString(2)));
    }
    private const string JobKey="pair_id="+PairKey+" AND route=$route AND job_id=$job";
    private static JobMember[] DecodeLaunch(string json)=>JsonSerializer.Deserialize<JobMember[]>(json) ?? throw new InvalidDataException();
    private static async Task<JobMember[]?> Launch(SqliteConnection db,SqliteTransaction tx,AcceptedJob job,CancellationToken ct)=>
        await Value(db,tx,"SELECT launch FROM accepted_job WHERE "+JobKey,ct,("$p",job.PairId),("$route",job.Route),("$job",job.JobId)) is string json ? DecodeLaunch(json) : null;
    /// <summary>Retire only the exact ordered launch the caller settled, independently of JSON spelling.</summary>
    public async Task SettleJobAsync(SqliteConnection db,AcceptedJob job,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        if(await Launch(db,tx,job,ct) is {} prior && prior.SequenceEqual(job.Launch)) await Write(db,tx,"DELETE FROM accepted_job WHERE "+JobKey,ct,("$p",job.PairId),("$route",job.Route),("$job",job.JobId));
        tx.Commit();
    }
    private static string Note(RemovalSelection victim,StageProof? stage) => JsonSerializer.Serialize(new { Victim=victim, Stage=stage });
    /// <summary>Arm one exact victim before removal without replacing another armed operation.</summary>
    public async Task<bool> ArmAsync(SqliteConnection db,string pairId,RemovalSelection victim,StageProof? stage,CancellationToken ct)
    {
        return await Write(db,null,"UPDATE pair SET pending_json=$note WHERE pair_id=$p AND (pending_json IS NULL OR pending_json=$note)",ct,("$p",pairId),("$note",Note(victim,stage)))==1;
    }
    /// <summary>Read the exact victim and optional replacement stage.</summary>
    public async Task<(RemovalSelection Victim,StageProof? Stage)?> ArmedAsync(SqliteConnection db,string pairId,CancellationToken ct)
    {
        if(await Value(db,null,"SELECT pending_json FROM pair WHERE pair_id=$p",ct,("$p",pairId)) is not string json) return null;
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        return (root.GetProperty("Victim").Deserialize<RemovalSelection>() ?? throw new InvalidDataException(),root.GetProperty("Stage").Deserialize<StageProof>());
    }
    /// <summary>Clear only the note matching the victim and stage that actually settled.</summary>
    public Task DisarmAsync(SqliteConnection db,string pairId,RemovalSelection expected,StageProof? expectedStage,CancellationToken ct) => Write(db,null,"UPDATE pair SET pending_json=NULL WHERE pair_id=$p AND pending_json=$note",ct,("$p",pairId),("$note",Note(expected,expectedStage)));
    /// <summary>Page configured pairs in persistent ID order.</summary>
    public async IAsyncEnumerable<PairConfig> PairsAsync(SqliteConnection db,int pageSize,List<string>? malformed,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        long after=0; int count;
        do
        {
            count=0;
            using var cmd=StorageCommand.Create(db,null,"SELECT id,def_json,pair_id FROM pair WHERE id>$after ORDER BY id LIMIT $size",("$after",after),("$size",pageSize));
            using var rd=await cmd.ExecuteReaderAsync(ct);
            var id=rd.GetOrdinal("id"); var definition=rd.GetOrdinal("def_json"); var pairId=rd.GetOrdinal("pair_id");
            while(await rd.ReadAsync(ct))
            {
                after=rd.GetInt64(id); count++;
                PairConfig pair;
                try { pair=Encoding.DecodePair(rd.GetString(definition)); }
                catch(Exception e) when(e is InvalidDataException or JsonException) { malformed?.Add(rd.GetString(pairId)); continue; }
                yield return pair;
            }
        } while(count==pageSize);
    }
    /// <summary>Read the volume's exact saved journal and USN bits.</summary>
    public async Task<JournalPosition?> PositionAsync(SqliteConnection db,string volume,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,"SELECT journal_id,next_usn FROM journal_position WHERE volume_key=$v","$v",volume); using var rd=await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)?new(unchecked((ulong)rd.GetInt64(0)),unchecked((ulong)rd.GetInt64(1))):null;
    }
    private static Task Position(SqliteConnection db,SqliteTransaction? tx,string volume,JournalPosition position,CancellationToken ct) => Write(db,tx,"INSERT INTO journal_position VALUES($v,$j,$n) ON CONFLICT(volume_key) DO UPDATE SET journal_id=excluded.journal_id,next_usn=excluded.next_usn",ct,("$v",volume),("$j",unchecked((long)position.JournalId)),("$n",unchecked((long)position.NextUsn)));
    /// <summary>Save the settled volume boundary.</summary>
    public Task SavePositionAsync(SqliteConnection db,string volume,JournalPosition position,CancellationToken ct) => Position(db,null,volume,position,ct);
    /// <summary>Read the settled Dropbox cursor for one pair.</summary>
    public async Task<string?> CursorAsync(SqliteConnection db,string pairId,CancellationToken ct) => await Value(db,null,"SELECT cursor FROM source_state WHERE pair_id="+PairKey+" AND side=1",ct,("$p",pairId)) as string;
    private static Task Cursor(SqliteConnection db,SqliteTransaction? tx,string id,string cursor,CancellationToken ct) => Write(db,tx,"INSERT INTO source_state(pair_id,side,cursor,repair_revision) VALUES("+PairKey+",1,$c,0) ON CONFLICT(pair_id,side) DO UPDATE SET cursor=excluded.cursor",ct,("$p",id),("$c",cursor));
    /// <summary>Advance a settled cursor without replacing outstanding debt.</summary>
    public Task SaveCursorAsync(SqliteConnection db,string pairId,string cursor,CancellationToken ct) => Cursor(db,null,pairId,cursor,ct);
    private static async Task<long> Advance(SqliteConnection db,SqliteTransaction? tx,string id,Side side,string? reason,CancellationToken ct) => (long)(await Value(db,tx,"INSERT INTO source_state(pair_id,side,repair_reason,repair_revision) VALUES("+PairKey+",$side,$reason,1) ON CONFLICT(pair_id,side) DO UPDATE SET repair_revision=repair_revision+1,repair_reason=coalesce(excluded.repair_reason,repair_reason) RETURNING repair_revision",ct,("$p",id),("$side",(int)side),("$reason",reason)) ?? throw new InvalidDataException());
    /// <summary>Advance the affected side's Repair revision and record its reason.</summary>
    public Task<long> OweRepairAsync(SqliteConnection db,string pairId,Side side,string reason,CancellationToken ct) => Advance(db,null,pairId,side,reason,ct);
    /// <summary>Read the side revision before acquisition, with zero for an untouched source.</summary>
    public async Task<(long Revision,string? Reason)> ReadRepairAsync(SqliteConnection db,string pairId,Side side,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,"SELECT repair_revision,repair_reason FROM source_state WHERE pair_id="+PairKey+" AND side=$s",("$p",pairId),("$s",(int)side)); using var rd=await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)?(rd.GetInt64(0),rd.IsDBNull(1)?null:rd.GetString(1)):(0,null);
    }
    /// <summary>Consume the reviewed drive notice and publish its choice atomically; only FirstSync resets bindings.</summary>
    public async Task<bool> ResolveDriveAsync(SqliteConnection db,PairConfig pair,Notice reviewed,ResolutionChoice choice,CancellationToken ct)
    {
        if(reviewed.Kind!=NoticeKind.DriveChange || choice is not (ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally)) return false;
        using var tx=db.BeginTransaction(deferred:false);
        if(!await NoticeTable.Close(tx,pair.PairId,reviewed,ct)) return false;
        await PublishAsync(tx,pair.PairId,pair,[],ct);
        if(choice==ResolutionChoice.FirstSync)
            await Write(db,tx,"DELETE FROM synced_entry WHERE pair_id="+PairKey+"; UPDATE pair SET first_repair_done=0 WHERE pair_id=$p",ct,("$p",pair.PairId));
        tx.Commit(); return true;
    }
}
