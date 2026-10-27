using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark;

// These process-local capabilities are issued only after binding the current
// retained cursor. Neither a cursor string nor a deserialized DTO is a writer.
public sealed class SyncAuthorityMonitorWriter
{
    internal SyncAuthorityDatabase Owner { get; }
    internal SyncAuthorityMonitorCursorSnapshot Binding { get; }
    internal string Epoch { get; }
    internal long Sequence { get; set; }
    internal string? Cursor { get; set; }
    internal object Gate { get; } = new();
    internal SyncAuthorityMonitorBatch? Pending { get; set; }
    internal SyncAuthorityMonitorWriter(SyncAuthorityDatabase owner,
        SyncAuthorityMonitorCursorSnapshot binding, string epoch)
        { Owner=owner; Binding=binding; Epoch=epoch; Cursor=binding.CursorValue; }
}

public sealed class SyncAuthorityMonitorBatch
{
    internal SyncAuthorityMonitorWriter Writer { get; }
    internal long Sequence { get; }
    internal string? PriorCursor { get; }
    internal string? NextCursor { get; }
    internal IReadOnlyList<SyncAuthorityMonitorEventInput> Events { get; }
    internal string Digest { get; }
    internal SyncAuthorityMonitorBatch(SyncAuthorityMonitorWriter writer,long sequence,
        string? prior,string? next,IReadOnlyList<SyncAuthorityMonitorEventInput> events,string digest)
        { Writer=writer; Sequence=sequence; PriorCursor=prior; NextCursor=next; Events=events; Digest=digest; }
}

public sealed class SyncAuthorityMonitorWatermark
{
    internal SyncAuthorityDatabase Owner { get; }
    internal string PairId { get; }
    internal long Sequence { get; }
    internal SyncAuthorityMonitorWatermark(SyncAuthorityDatabase owner,string pair,long sequence)
        { Owner=owner; PairId=pair; Sequence=sequence; }
}

public sealed class SyncMonitorWriterReplacedException : InvalidOperationException
{
    internal SyncMonitorWriterReplacedException() : base("This monitor writer is no longer the current cursor owner.") { }
}

public sealed partial class SyncAuthorityDatabase
{
    internal Action? MonitorCommittedBeforeAcknowledgementForTest { get; set; }

    public async Task<SyncAuthorityMonitorWriter> BeginMonitorWriterAsync(string cursorId,
        string pairId,string rootBindingId,string kind,long generation,string sourceIdentityJson,
        CancellationToken token=default)
    {
        RequireIdentifier(cursorId,nameof(cursorId));
        RequireJson(sourceIdentityJson,nameof(sourceIdentityJson));
        var epoch=Guid.NewGuid().ToString("N");
        SyncAuthorityMonitorCursorSnapshot? binding=null;
        await WriteAsync(async (connection,tx)=>
        {
            await using var q=connection.CreateCommand();q.Transaction=tx;
            q.CommandText="""
                SELECT cursor_value FROM monitor_cursor WHERE monitor_cursor_id=$id AND pair_id=$pair
                    AND root_binding_id=$root AND kind=$kind AND generation=$generation
                    AND source_identity_json=$identity AND status IN ('active','unavailable')
                    AND EXISTS(SELECT 1 FROM root_binding r WHERE r.root_binding_id=$root AND r.pair_id=$pair AND r.state='active');
                """;
            q.Parameters.AddWithValue("$id",cursorId);q.Parameters.AddWithValue("$pair",pairId);
            q.Parameters.AddWithValue("$root",rootBindingId);q.Parameters.AddWithValue("$kind",kind);
            q.Parameters.AddWithValue("$generation",generation);q.Parameters.AddWithValue("$identity",sourceIdentityJson);
            string? cursor;
            await using(var rows=await q.ExecuteReaderAsync(token))
            {
                if(!await rows.ReadAsync(token))throw new SyncMonitorWriterReplacedException();
                cursor=rows.IsDBNull(0)?null:rows.GetString(0);
            }
            await ExecuteAsync(connection,tx,"""
                UPDATE monitor_cursor SET writer_epoch=$epoch,committed_batch_sequence=0,
                    last_batch_digest=NULL,last_batch_prior_cursor=NULL
                WHERE monitor_cursor_id=$id;
                """,token,("$epoch",epoch),("$id",cursorId));
            binding=new(cursorId,pairId,rootBindingId,kind,generation,cursor,sourceIdentityJson,"active");
        },token);
        return new(this,binding!,epoch);
    }

    public SyncAuthorityMonitorBatch PrepareMonitorBatch(SyncAuthorityMonitorWriter writer,
        IReadOnlyList<SyncAuthorityMonitorEventInput> events,string? expectedPriorCursor,string? nextCursor)
    {
        if(!ReferenceEquals(writer.Owner,this))throw new InvalidOperationException("The monitor writer belongs to another database.");
        ArgumentNullException.ThrowIfNull(events);
        if(events.Count>1000)throw new ArgumentOutOfRangeException(nameof(events));
        var immutable=Array.AsReadOnly(events.Select(item=>
        {
            RequireIdentifier(item.EventId,nameof(item.EventId));
            RequireIdentifier(item.SourceEventKey,nameof(item.SourceEventKey));
            RequireIdentifier(item.EventKind,nameof(item.EventKind));
            RequireJson(item.EventJson,nameof(item.EventJson));
            return item with { RelativePath=item.RelativePath is null?null:PilotPath.NormalizeRelative(item.RelativePath),
                EventJson=CanonicalMonitorJson(item.EventJson) };
        }).ToArray());
        lock(writer.Gate)
        {
            if(writer.Pending is not null)throw new InvalidOperationException("Resolve the monitor's existing immutable batch before preparing another.");
            if(!string.Equals(writer.Cursor,expectedPriorCursor,StringComparison.Ordinal))
                throw new InvalidOperationException("The source page does not start at the writer's retained cursor.");
            if(writer.Sequence==long.MaxValue)throw new InvalidOperationException("The monitor batch sequence is exhausted.");
            var sequence=writer.Sequence+1;
            var digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {
                writer.Binding,writer.Epoch,Sequence=sequence,Prior=expectedPriorCursor,Next=nextCursor,Events=immutable
            }))).ToLowerInvariant();
            return writer.Pending=new(writer,sequence,expectedPriorCursor,nextCursor,immutable,digest);
        }
    }

    private static string CanonicalMonitorJson(string json)
    {
        using var doc=JsonDocument.Parse(json);
        using var bytes=new MemoryStream();
        using(var writer=new Utf8JsonWriter(bytes))
        {
            void Write(JsonElement value)
            {
                if(value.ValueKind==JsonValueKind.Object)
                {
                    writer.WriteStartObject();
                    foreach(var property in value.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal))
                        {writer.WritePropertyName(property.Name);Write(property.Value);}
                    writer.WriteEndObject();
                }
                else if(value.ValueKind==JsonValueKind.Array)
                    {writer.WriteStartArray();foreach(var child in value.EnumerateArray())Write(child);writer.WriteEndArray();}
                else value.WriteTo(writer);
            }
            Write(doc.RootElement);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static async Task RequireMonitorWriterAsync(SqliteConnection connection,SqliteTransaction tx,
        SyncAuthorityMonitorWriter writer,CancellationToken token)
    {
        var b=writer.Binding;
        if(await ScalarLongAsync(connection,tx,"""
            SELECT count(*) FROM monitor_cursor WHERE monitor_cursor_id=$id AND pair_id=$pair
                AND root_binding_id=$root AND kind=$kind AND generation=$generation
                AND source_identity_json=$identity AND writer_epoch=$epoch AND status IN ('active','unavailable')
                AND EXISTS(SELECT 1 FROM root_binding r WHERE r.root_binding_id=$root AND r.pair_id=$pair AND r.state='active');
            """,token,("$id",b.MonitorCursorId),("$pair",b.PairId),("$root",b.RootBindingId),
            ("$kind",b.Kind),("$generation",b.Generation),("$identity",b.SourceIdentityJson),("$epoch",writer.Epoch))!=1)
            throw new SyncMonitorWriterReplacedException();
    }

    public async Task CommitMonitorBatchAsync(SyncAuthorityMonitorWriter writer,SyncAuthorityMonitorBatch batch,
        CancellationToken token=default)
    {
        if(!ReferenceEquals(writer.Owner,this)||!ReferenceEquals(batch.Writer,writer))
            throw new InvalidOperationException("The monitor batch has another owner.");
        lock(writer.Gate)
            if(writer.Pending is not null&&!ReferenceEquals(writer.Pending,batch))
                throw new InvalidOperationException("Another monitor batch is awaiting resolution.");
        await WriteAsync(async(connection,tx)=>
        {
            await RequireMonitorWriterAsync(connection,tx,writer,token);
            var id=writer.Binding.MonitorCursorId;var pair=writer.Binding.PairId;
            long sequence;string? prior,next,digest;
            await using(var q=connection.CreateCommand())
            {
                q.Transaction=tx;q.CommandText="""
                    SELECT committed_batch_sequence,last_batch_prior_cursor,cursor_value,last_batch_digest
                    FROM monitor_cursor WHERE monitor_cursor_id=$id;
                    """;q.Parameters.AddWithValue("$id",id);
                await using var rows=await q.ExecuteReaderAsync(token);
                if(!await rows.ReadAsync(token))throw new SyncMonitorWriterReplacedException();
                sequence=rows.GetInt64(0);prior=rows.IsDBNull(1)?null:rows.GetString(1);
                next=rows.IsDBNull(2)?null:rows.GetString(2);digest=rows.IsDBNull(3)?null:rows.GetString(3);
            }
            if(sequence==batch.Sequence)
            {
                if(digest==batch.Digest&&prior==batch.PriorCursor&&next==batch.NextCursor)
                {
                    // A lost acknowledgement may have published unavailable after
                    // the durable commit. Resolving that same ticket restores health
                    // without replaying events or advancing the source cursor.
                    await ExecuteAsync(connection,tx,"UPDATE monitor_cursor SET status='active',updated_utc=$utc WHERE monitor_cursor_id=$id AND status='unavailable';",
                        token,("$utc",Utc(ActivityRetentionNow)),("$id",id));
                    return;
                }
                throw new InvalidOperationException("A monitor batch sequence was reused with different evidence.");
            }
            if(sequence==long.MaxValue||batch.Sequence!=sequence+1||next!=batch.PriorCursor)
                throw new InvalidOperationException("The monitor batch is stale, out of order, or has a different prior cursor.");
            var hint=await ScalarLongAsync(connection,tx,"SELECT next_monitor_hint_sequence FROM installation;",token);
            if(hint==long.MaxValue)throw new InvalidOperationException("The monitor hint sequence is exhausted.");
            var now=Utc(ActivityRetentionNow);
            foreach(var item in batch.Events)
            {
                await using(var q=connection.CreateCommand())
                {
                    q.Transaction=tx;q.CommandText="""
                        SELECT monitor_event_id,monitor_cursor_id,pair_id,source_event_key,normalized_path_key,
                            event_kind,event_json,object_identity_json
                        FROM monitor_event WHERE monitor_event_id=$id OR (pair_id=$pair AND source_event_key=$key);
                        """;
                    q.Parameters.AddWithValue("$id",item.EventId);q.Parameters.AddWithValue("$pair",pair);
                    q.Parameters.AddWithValue("$key",item.SourceEventKey);
                    await using var rows=await q.ExecuteReaderAsync(token);
                    if(await rows.ReadAsync(token))
                    {
                        if(rows.GetString(0)!=item.EventId||rows.IsDBNull(1)||rows.GetString(1)!=id||
                           rows.GetString(2)!=pair||rows.GetString(3)!=item.SourceEventKey||
                           (rows.IsDBNull(4)?null:rows.GetString(4))!=item.RelativePath||
                           rows.GetString(5)!=item.EventKind||CanonicalMonitorJson(rows.GetString(6))!=item.EventJson||
                           !rows.IsDBNull(7)||await rows.ReadAsync(token))
                            throw new InvalidOperationException("A retained monitor event has different immutable evidence.");
                        continue;
                    }
                }
                var dirtyId="dirty-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pair+"\n"+(item.RelativePath??"<pair>"))))
                    .ToLowerInvariant()[..32];
                await ExecuteAsync(connection,tx,"""
                    INSERT INTO dirty_item(dirty_item_id,pair_id,side,normalized_path_key,reconciliation_level,
                        earliest_event_utc,latest_event_utc,event_count,status,latest_hint_sequence,closed_utc)
                    VALUES($id,$pair,'both',$path,$level,$utc,$utc,1,'dirty',$hint,NULL)
                    ON CONFLICT(dirty_item_id) DO UPDATE SET
                        earliest_event_utc=CASE WHEN dirty_item.status='closed' THEN excluded.earliest_event_utc ELSE dirty_item.earliest_event_utc END,
                        latest_event_utc=excluded.latest_event_utc,
                        event_count=CASE WHEN dirty_item.status='closed' THEN 1 ELSE dirty_item.event_count+1 END,
                        status='dirty',latest_hint_sequence=excluded.latest_hint_sequence,closed_utc=NULL;
                    """,token,("$id",dirtyId),("$pair",pair),("$path",item.RelativePath),
                    ("$level",item.RelativePath is null?"pair":"item"),("$utc",now),("$hint",hint));
                var bytes=Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item))+Encoding.UTF8.GetByteCount(id)+
                    Encoding.UTF8.GetByteCount(pair)+128;
                await ExecuteAsync(connection,tx,"""
                    INSERT INTO monitor_event(monitor_event_id,monitor_cursor_id,pair_id,source_event_key,
                        normalized_path_key,event_kind,event_json,received_utc,batch_epoch,batch_sequence,
                        hint_sequence,lifetime_class,encoded_bytes)
                    VALUES($id,$cursor,$pair,$key,$path,$kind,$json,$utc,$epoch,$sequence,$hint,'eligible',$bytes);
                    """,token,("$id",item.EventId),("$cursor",id),("$pair",pair),("$key",item.SourceEventKey),
                    ("$path",item.RelativePath),("$kind",item.EventKind),("$json",item.EventJson),("$utc",now),
                    ("$epoch",writer.Epoch),("$sequence",batch.Sequence),("$hint",hint),("$bytes",bytes));
            }
            await ExecuteAsync(connection,tx,"UPDATE installation SET next_monitor_hint_sequence=next_monitor_hint_sequence+1;",token);
            await ExecuteAsync(connection,tx,"""
                UPDATE monitor_cursor SET cursor_value=$next,status='active',updated_utc=$utc,
                    committed_batch_sequence=$sequence,last_batch_digest=$digest,last_batch_prior_cursor=$prior
                WHERE monitor_cursor_id=$id AND writer_epoch=$epoch;
                """,token,("$next",batch.NextCursor),("$utc",now),("$sequence",batch.Sequence),
                ("$digest",batch.Digest),("$prior",batch.PriorCursor),("$id",id),("$epoch",writer.Epoch));
        },token);
        MonitorCommittedBeforeAcknowledgementForTest?.Invoke();
        lock(writer.Gate)
        {
            writer.Sequence=Math.Max(writer.Sequence,batch.Sequence);
            writer.Cursor=batch.NextCursor;
            if(ReferenceEquals(writer.Pending,batch))writer.Pending=null;
        }
    }

    public Task SetMonitorCursorStatusAsync(SyncAuthorityMonitorWriter writer,string status,CancellationToken token=default)
    {
        if(!ReferenceEquals(writer.Owner,this)||status is not ("active" or "unavailable"))
            throw new InvalidOperationException("Invalid monitor owner status.");
        return WriteAsync(async(connection,tx)=>
        {
            await RequireMonitorWriterAsync(connection,tx,writer,token);
            await ExecuteAsync(connection,tx,"UPDATE monitor_cursor SET status=$status,updated_utc=$utc WHERE monitor_cursor_id=$id;",
                token,("$status",status),("$utc",Utc(ActivityRetentionNow)),("$id",writer.Binding.MonitorCursorId));
        },token);
    }

    public async Task<SyncAuthorityMonitorWatermark> CaptureMonitorWatermarkAsync(string pairId,CancellationToken token=default)
    {
        RequireIdentifier(pairId,nameof(pairId));
        await using var connection=await OpenConnectionAsync(token);
        var sequence=await ScalarLongAsync(connection,null,"SELECT next_monitor_hint_sequence-1 FROM installation;",token);
        return new(this,pairId,sequence);
    }

    public Task CloseMonitorHintsThroughAsync(string pairId,SyncAuthorityMonitorWatermark watermark,CancellationToken token=default)
    {
        if(!ReferenceEquals(watermark.Owner,this)||pairId!=watermark.PairId)
            throw new InvalidOperationException("The monitor watermark belongs to another observation.");
        return WriteAsync(async(connection,tx)=>
        {
            var now=Utc(ActivityRetentionNow);
            await ExecuteAsync(connection,tx,"""
                UPDATE monitor_event SET consumed_utc=$now WHERE pair_id=$pair AND consumed_utc IS NULL
                    AND coalesce(hint_sequence,0)<=$sequence;
                """,token,("$now",now),("$pair",pairId),("$sequence",watermark.Sequence));
            await ExecuteAsync(connection,tx,"""
                UPDATE dirty_item SET status='closed',closed_utc=$now WHERE pair_id=$pair AND status='dirty'
                    AND latest_hint_sequence<=$sequence;
                """,token,("$now",now),("$pair",pairId),("$sequence",watermark.Sequence));
        },token);
    }
}

