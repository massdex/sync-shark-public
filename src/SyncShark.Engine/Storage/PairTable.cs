using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns pair configuration, source boundaries and exact unfinished operations.</summary>
public sealed class PairTable
{
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
    // The raw definition: derives nothing, so an internal rewrite never persists a kept leg back into def_json.
    private static async Task<PairConfig?> PairRaw(SqliteConnection db,SqliteTransaction? tx,string id,CancellationToken ct) =>
        await Value(db,tx,Sql.Get(SqlKey.PairTablePairRaw1),ct,("$p",id)) is string json ? Encoding.DecodePair(json) : null;

    /// <summary>Read one stored pair by its public ID, with the KEEP legs on its bound rows projected in.</summary>
    public async Task<PairConfig?> ReadAsync(SqliteConnection db,string pairId,CancellationToken ct,SqliteTransaction? tx=null)
    {
        if (await PairRaw(db,tx,pairId,ct) is not {} pair) return null;
        var legs=await KeepRows.Derive(db,tx,pairId,ct);
        return legs.Count==0 ? pair : pair with { Exclusions=pair.Exclusions.Concat(legs).ToArray() };
    }

    /// <summary>Check removal evidence and publish the edit and returned scopes in the caller's immediate transaction, advancing the owner's revision even for an already-owed scope.</summary>
    public async Task<TextKey?> PublishAsync(SqliteTransaction tx,string pairId,PairConfig? replacement,IReadOnlyList<(string Owner,Scope Scope)> returnedScopes,CancellationToken ct,Exclusion? selected=null)
    {
        var db=tx.Connection!;
        if(replacement is null)
        {
            var guard=await Value(db,tx,Sql.Get(SqlKey.PairTablePublish1),ct,("$job",(int)TextKey.PairJobOutstanding),("$armed",(int)TextKey.PairPcNoteOutstanding),
                ("$put",(int)TextKey.PairPutBackOutstanding),("$kind",(int)NoticeKind.PutBack),("$p",pairId));
            if(guard is null) return TextKey.PairUnknown;
            if(guard is long cause) return (TextKey)cause;
            await Write(db,tx,Sql.Get(SqlKey.PairTablePublish2),ct,("$p",pairId));
        }
        else
        {
            if(replacement.PairId!=pairId) throw new ArgumentException(nameof(replacement));
            var existing=await PairRaw(db,tx,pairId,ct);
            if(selected is {KeptBy:null} && existing?.Exclusions.Contains(selected)!=true) throw new EngineRequestException(TextKey.ResolveStale);
            var edited=replacement.Exclusions;
            if(existing?.Exclusions.Any(e=>e.RuleId!=null)==true || edited.Any(e=>e.RuleId!=null))
                edited=edited.Where(e=>e.RuleId==null).Concat(existing?.Exclusions.Where(e=>e.RuleId!=null && e.Token!=selected?.Token) ?? []).ToArray();
            var saved=replacement with { OwedScopes=existing?.OwedScopes, Exclusions=await KeepRows.Publish(db,tx,pairId,edited,ct,selected) };
            await Write(db,tx,Sql.Get(SqlKey.PairTablePublish3),ct,("$p",pairId),("$json",JsonSerializer.Serialize(saved)));
        }
        foreach(var group in returnedScopes.GroupBy(s=>s.Owner)) await OweScopesAsync(tx,group.Key,group.Select(s=>s.Scope),ct);
        return null;
    }
    /// <summary>Owe each scope at its stored pair, keeping any existing reason, and advance that pair's Dropbox revision in the caller's transaction.</summary>
    public async Task OweScopesAsync(SqliteTransaction tx,string pairId,IEnumerable<Scope> owed,CancellationToken ct)
    {
        var db=tx.Connection!;
        var owner=await PairRaw(db,tx,pairId,ct) ?? throw new InvalidDataException();
        var scopes=(owner.OwedScopes ?? []).Concat(owed).Distinct().ToArray();
        await Write(db,tx,Sql.Get(SqlKey.PairTableOweScopes1),ct,("$json",JsonSerializer.Serialize(owner with { OwedScopes=scopes })),("$p",pairId));
        await Advance(db,tx,pairId,Side.Dropbox,null,ct);
    }
    /// <summary>Attach any surviving configured KEEP path rules to their bound rows before a run can lose the old keys, without a config edit (K06).</summary>
    internal async Task EnrollAsync(SqliteTransaction tx,PairConfig pair,CancellationToken ct)
    {
        var db=tx.Connection!;
        if(await PairRaw(db,tx,pair.PairId,ct) is not {} existing) throw new InvalidDataException();
        var saved=existing with { Exclusions=await KeepRows.Publish(db,tx,pair.PairId,existing.Exclusions,ct) };
        await Write(db,tx,Sql.Get(SqlKey.PairTableEnroll1),ct,("$p",pair.PairId),("$json",JsonSerializer.Serialize(saved)));
    }
    /// <summary>Only a complete Repair matching both captured revisions publishes first_repair_done and its summary and clears both reasons.</summary>
    public async Task<bool> FinishRepairAsync(SqliteTransaction tx,string pairId,(long Local,long Dropbox) revisions,string completedRepairJson,CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(completedRepairJson);
        var db=tx.Connection!;
        if(await Write(db,tx,Sql.Get(SqlKey.PairTableFinishRepair1),ct,("$p",pairId),("$local",revisions.Local),("$cloud",revisions.Dropbox),("$json",completedRepairJson))==0) return false;
        await Write(db,tx,Sql.Get(SqlKey.PairTableFinishRepair2),ct,("$p",pairId));
        return true;
    }
    /// <summary>Retire only this mapped scope at the captured Dropbox revision, without publishing a full Repair.</summary>
    public async Task<bool> FinishRepairAsync(SqliteTransaction tx,string pairId,Scope scope,long revision,CancellationToken ct)
    {
        var db=tx.Connection!; var pair=await PairRaw(db,tx,pairId,ct);
        if(pair?.OwedScopes is not { } owed || !owed.Contains(scope)) return false;
        return await Write(db,tx,Sql.Get(SqlKey.PairTableFinishRepair3),ct,("$p",pairId),("$rev",revision),("$json",JsonSerializer.Serialize(pair with { OwedScopes=owed.Where(s=>s!=scope).ToArray() })))!=0;
    }
    /// <summary>Read the last completed full Repair and first-run flag together.</summary>
    public async Task<(bool FirstRepairDone,string? LastRepairJson)> RepairStateAsync(SqliteConnection db,string pairId,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.PairTableRepairState1),"$p",pairId); using var rd=await cmd.ExecuteReaderAsync(ct);
        if(!await rd.ReadAsync(ct)) throw new KeyNotFoundException();
        return (rd.GetBoolean(0),rd.IsDBNull(1)?null:rd.GetString(1));
    }
    /// <summary>Install the fresh volume boundary and advance Local Repair debt only for the durably bound pairs in this transaction.</summary>
    public async Task ResetJournalAsync(SqliteTransaction tx,string volume,JournalPosition fresh,string reason,CancellationToken ct)
    {
        await Position(tx.Connection!,tx,volume,fresh,ct);
        // The caller snapshot predates the write lock. Only persisted root bindings own this cursor.
        await Write(tx.Connection!,tx,Sql.Get(SqlKey.PairTableResetJournal1),ct,("$volume",volume),("$reason",reason));
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
        if(prior==null) await Write(db,tx,Sql.Get(SqlKey.PairTableSaveJob1),ct,("$p",job.PairId),("$route",job.Route),("$job",job.JobId),("$launch",JsonSerializer.Serialize(job.Launch)));
        else if(!prior.SequenceEqual(job.Launch)) throw new InvalidDataException();
        tx.Commit();
    }
    /// <summary>Stream returned jobs with their original launch order.</summary>
    public async IAsyncEnumerable<AcceptedJob> JobsAsync(SqliteConnection db,string pairId,[EnumeratorCancellation]CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.PairTableJobs1),"$p",pairId); using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct)) yield return new(pairId,rd.GetString(0),rd.GetString(1),DecodeLaunch(rd.GetString(2)));
    }
    private static JobMember[] DecodeLaunch(string json)=>JsonSerializer.Deserialize<JobMember[]>(json) ?? throw new InvalidDataException();
    private static async Task<JobMember[]?> Launch(SqliteConnection db,SqliteTransaction tx,AcceptedJob job,CancellationToken ct)=>
        await Value(db,tx,Sql.Get(SqlKey.PairTableLaunch1),ct,("$p",job.PairId),("$route",job.Route),("$job",job.JobId)) is string json ? DecodeLaunch(json) : null;
    /// <summary>Retire only the exact ordered launch the caller settled, independently of JSON spelling.</summary>
    public async Task SettleJobAsync(SqliteConnection db,AcceptedJob job,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        if(await Launch(db,tx,job,ct) is {} prior && prior.SequenceEqual(job.Launch)) await Write(db,tx,Sql.Get(SqlKey.PairTableSettleJob1),ct,("$p",job.PairId),("$route",job.Route),("$job",job.JobId));
        tx.Commit();
    }
    private static string Note(RemovalSelection victim,StageProof? stage) => JsonSerializer.Serialize(new { Victim=victim, Stage=stage });
    /// <summary>Arm one exact victim before removal without replacing another armed operation.</summary>
    public async Task<bool> ArmAsync(SqliteConnection db,string pairId,RemovalSelection victim,StageProof? stage,CancellationToken ct)
    {
        return await Write(db,null,Sql.Get(SqlKey.PairTableArm1),ct,("$p",pairId),("$note",Note(victim,stage)))==1;
    }
    /// <summary>Read the exact victim and optional replacement stage.</summary>
    public async Task<(RemovalSelection Victim,StageProof? Stage)?> ArmedAsync(SqliteConnection db,string pairId,CancellationToken ct)
    {
        if(await Value(db,null,Sql.Get(SqlKey.PairTableArmed1),ct,("$p",pairId)) is not string json) return null;
        using var doc=JsonDocument.Parse(json); var root=doc.RootElement;
        return (root.GetProperty("Victim").Deserialize<RemovalSelection>() ?? throw new InvalidDataException(),root.GetProperty("Stage").Deserialize<StageProof>());
    }
    /// <summary>Clear only the note matching the victim and stage that actually settled.</summary>
    public Task DisarmAsync(SqliteConnection db,string pairId,RemovalSelection expected,StageProof? expectedStage,CancellationToken ct) => Write(db,null,Sql.Get(SqlKey.PairTableDisarm1),ct,("$p",pairId),("$note",Note(expected,expectedStage)));
    /// <summary>Page configured pairs in persistent ID order.</summary>
    public async IAsyncEnumerable<PairConfig> PairsAsync(SqliteConnection db,int pageSize,List<string>? malformed,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        long after=0; int count; var sql=Sql.Get(SqlKey.PairTablePairs1);
        do
        {
            count=0;
            var page=new List<(string Id,PairConfig Pair)>();
            using(var cmd=StorageCommand.Create(db,null,sql,("$after",after),("$size",pageSize)))
            using(var rd=await cmd.ExecuteReaderAsync(ct))
            {
                var id=rd.GetOrdinal("id"); var definition=rd.GetOrdinal("def_json"); var pairId=rd.GetOrdinal("pair_id");
                while(await rd.ReadAsync(ct))
                {
                    after=rd.GetInt64(id); count++;
                    PairConfig pair;
                    string pairKey;
                    try { pair=Encoding.DecodePair(rd.GetString(definition)); pairKey=rd.GetString(pairId); }
                    catch(Exception e) when(e is InvalidDataException or JsonException) { malformed?.Add(rd.GetString(pairId)); continue; }
                    page.Add((pairKey,pair));
                }
            }
            // Derive after the page reader closes; one connection cannot run a second query over an open reader.
            foreach(var (pairKey,pair) in page)
            {
                List<Exclusion> legs;
                try { legs=await KeepRows.Derive(db,null,pairKey,ct); }
                catch(InvalidDataException) { malformed?.Add(pairKey); continue; }
                yield return legs.Count==0 ? pair : pair with { Exclusions=pair.Exclusions.Concat(legs).ToArray() };
            }
        } while(count==pageSize);
    }
    /// <summary>Read the volume's exact saved journal and USN bits.</summary>
    public async Task<JournalPosition?> PositionAsync(SqliteConnection db,string volume,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.PairTablePosition1),"$v",volume); using var rd=await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)?new(unchecked((ulong)rd.GetInt64(0)),unchecked((ulong)rd.GetInt64(1))):null;
    }
    private static Task Position(SqliteConnection db,SqliteTransaction? tx,string volume,JournalPosition position,CancellationToken ct) => Write(db,tx,Sql.Get(SqlKey.PairTablePosition2),ct,("$v",volume),("$j",unchecked((long)position.JournalId)),("$n",unchecked((long)position.NextUsn)));
    /// <summary>Save the settled volume boundary.</summary>
    public Task SavePositionAsync(SqliteConnection db,string volume,JournalPosition position,CancellationToken ct) => Position(db,null,volume,position,ct);
    /// <summary>Read the settled Dropbox cursor for one pair.</summary>
    public async Task<string?> CursorAsync(SqliteConnection db,string pairId,CancellationToken ct) => await Value(db,null,Sql.Get(SqlKey.PairTableCursor1),ct,("$p",pairId)) as string;
    private static Task Cursor(SqliteConnection db,SqliteTransaction? tx,string id,string cursor,CancellationToken ct) => Write(db,tx,Sql.Get(SqlKey.PairTableCursor2),ct,("$p",id),("$c",cursor));
    /// <summary>Advance a settled cursor without replacing outstanding debt.</summary>
    public Task SaveCursorAsync(SqliteConnection db,string pairId,string cursor,CancellationToken ct) => Cursor(db,null,pairId,cursor,ct);
    /// <summary>Advance the settled cursor and append that page's deleted names in one transaction.</summary>
    public async Task SaveCursorAsync(SqliteConnection db,string pairId,string cursor,IReadOnlyList<string> deletedKeys,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        await Cursor(db,tx,pairId,cursor,ct);
        foreach(var key in deletedKeys) await Write(db,tx,Sql.Get(SqlKey.PairTableAppendDeleted1),ct,("$p",pairId),("$k",key));
        tx.Commit();
    }
    private static async Task<long> Advance(SqliteConnection db,SqliteTransaction? tx,string id,Side side,string? reason,CancellationToken ct) => (long)(await Value(db,tx,Sql.Get(SqlKey.PairTableAdvance1),ct,("$p",id),("$side",(int)side),("$reason",reason)) ?? throw new InvalidDataException());
    /// <summary>Advance the affected side's Repair revision and record its reason.</summary>
    public Task<long> OweRepairAsync(SqliteConnection db,string pairId,Side side,string reason,CancellationToken ct) => Advance(db,null,pairId,side,reason,ct);
    /// <summary>Read the side revision before acquisition, with zero for an untouched source.</summary>
    public async Task<(long Revision,string? Reason)> ReadRepairAsync(SqliteConnection db,string pairId,Side side,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.PairTableReadRepair1),("$p",pairId),("$s",(int)side)); using var rd=await cmd.ExecuteReaderAsync(ct);
        return await rd.ReadAsync(ct)?(rd.GetInt64(0),rd.IsDBNull(1)?null:rd.GetString(1)):(0,null);
    }
    /// <summary>Consume the reviewed drive notice and publish its choice atomically; only FirstSync resets bindings.</summary>
    public async Task<bool> ResolveDriveAsync(SqliteConnection db,PairConfig pair,Notice reviewed,ResolutionChoice choice,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        var resolved=await ResolveDriveAsync(tx,pair,reviewed,choice,ct);
        if(resolved) tx.Commit(); return resolved;
    }
    internal async Task<bool> ResolveDriveAsync(SqliteTransaction tx,PairConfig pair,Notice reviewed,ResolutionChoice choice,CancellationToken ct)
    {
        var db=tx.Connection!;
        if(reviewed.Kind!=NoticeKind.DriveChange || choice is not (ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally) || !await NoticeTable.Close(tx,pair.PairId,reviewed,ct)) return false;
        await PublishAsync(tx,pair.PairId,pair,[],ct);
        if(choice==ResolutionChoice.FirstSync)
        {
            // The kept legs become plain path rules before their rows vanish; the reset then keeps their protection.
            var raw=await PairRaw(db,tx,pair.PairId,ct) ?? throw new InvalidDataException();
            var kept=(await KeepRows.Derive(db,tx,pair.PairId,ct)).Select(e=>e with {RuleId=e.Token,Origin=e.ConfiguredSide,KeptBy=null}).ToArray();
            if(kept.Length!=0)
            {
                // Drop only exact duplicates: a KEEP and a no-keep rule at the same path stay distinct (K04).
                var merged=raw.Exclusions.Concat(kept).Distinct().ToArray();
                await Write(db,tx,Sql.Get(SqlKey.PairTableResolveDrive1),ct,("$p",pair.PairId),("$json",JsonSerializer.Serialize(raw with { Exclusions=merged })));
            }
            await Write(db,tx,Sql.Get(SqlKey.PairTableResolveDrive2),ct,("$p",pair.PairId));
        }
        return true;
    }
}
