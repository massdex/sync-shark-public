using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Attach KEEP rules to indexed bindings and project each surviving physical leg.</summary>
internal static class KeepRows
{
    // A null endpoint follows both identities. A fixed path retains an unmatched endpoint after replacement.
    internal sealed record Flag([property:System.Text.Json.Serialization.JsonRequired] string Id, [property:System.Text.Json.Serialization.JsonRequired] Side Origin, [property:System.Text.Json.Serialization.JsonRequired] string Tail, Side? Endpoint=null, string? FixedPath=null);
    private static Flag Decode(string type,string value,string localId,string remoteId)
    {
        Flag flag;
        try { flag=type switch {
            "text" => new Flag(JsonSerializer.Serialize(new[]{localId,remoteId,value}),Side.Local,value),
            "object" => JsonSerializer.Deserialize<Flag>(value) ?? throw new InvalidDataException(),
            _ => throw new InvalidDataException() }; }
        catch(JsonException error) { throw new InvalidDataException(null,error); }
        if(string.IsNullOrEmpty(flag.Id) || flag.Tail is null || !Enum.IsDefined(flag.Origin) ||
            flag.Endpoint is {} side && !Enum.IsDefined(side) || flag.FixedPath!=null && flag.Endpoint==null) throw new InvalidDataException();
        try { if(flag.Tail.Length!=0) PathNames.NormalizeInventoryPath(flag.Tail); if(flag.FixedPath!=null) PathNames.NormalizeInventoryPath(flag.FixedPath); }
        catch(ArgumentException error) { throw new InvalidDataException(null,error); }
        return flag;
    }

    internal static async Task<List<Exclusion>> Derive(SqliteConnection db,SqliteTransaction? tx,string pairId,CancellationToken ct)
    {
        var legs=new List<Exclusion>();
        using var cmd=StorageCommand.Create(db,tx,"""
            SELECT s.rowid,s.local_path,s.dropbox_path,s.local_id,s.dropbox_id,j.key,j.type,j.value FROM synced_entry s,json_each(s.keep) j
            WHERE s.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND s.keep IS NOT NULL
            ""","$p",pairId);
        using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct))
        {
            if(rd.IsDBNull(7)) continue;
            var flag=Decode(rd.GetString(6),rd.GetString(7),rd.GetString(3),rd.GetString(4));
            Add(Side.Local,rd.GetString(1)); Add(Side.Dropbox,rd.GetString(2));
            void Add(Side side,string root)
            {
                if(flag.Endpoint!=null && flag.Endpoint!=side) return;
                var path=flag.FixedPath ?? (flag.Tail.Length==0?root:PathNames.Child(root,flag.Tail));
                legs.Add(new(side,path,true){KeptBy=(flag.Id,flag.Origin,flag.Tail,rd.GetInt64(0),rd.GetInt32(5))});
            }
        }
        return legs;
    }

    internal static async Task<List<Exclusion>> Publish(SqliteConnection db,SqliteTransaction tx,string pairId,IReadOnlyList<Exclusion> viewed,CancellationToken ct,Exclusion? selected=null)
    {
        if(selected?.Token is {} id)
        {
            var current=await Derive(db,tx,pairId,ct);
            if(selected.KeptBy!=null && !current.Any(e=>e.Side==selected.Side && e.Path==selected.Path && e.Token==id)) throw new EngineRequestException(TextKey.ResolveStale);
            foreach(var leg in current.Select(e=>e.KeptBy!.Value).Where(k=>k.Id==id).Distinct().OrderByDescending(k=>k.Index))
            {
                using var clear=StorageCommand.Create(db,tx,"UPDATE synced_entry SET keep=nullif(json_remove(keep,$idx),'[]') WHERE rowid=$row",("$row",leg.Row),("$idx","$["+leg.Index+"]"));
                await clear.ExecuteNonQueryAsync(ct);
            }
        }
        var raw=new List<Exclusion>();
        foreach(var e in viewed)
            if(e.KeptBy==null && (!e.Keep || !await Attach(db,tx,pairId,e,ct))) raw.Add(e);
        return raw;
    }

    private static async Task<bool> Attach(SqliteConnection db,SqliteTransaction tx,string pairId,Exclusion rule,CancellationToken ct)
    {
        const string from=" FROM synced_entry WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND ";
        const string keys=" IN (SELECT value FROM json_each($keys))";
        var lookup=rule.Side==Side.Local?"SELECT rowid,local_path AS path"+from+"pathkey(local_path)"+keys:
            "SELECT rowid,dropbox_path AS path"+from+"dropbox_key_delta"+keys+" UNION ALL SELECT rowid,dropbox_path AS path"+from+"dropbox_key_delta IS NULL AND pathkey(local_path)"+keys;
        var ancestors=PathNames.Ancestors(rule.Path).Prepend(rule.Path).Select(p=>PathNames.KeyFor(rule.Side,p));
        long row; string root;
        using(var find=StorageCommand.Create(db,tx,"SELECT rowid,path FROM ("+lookup+") ORDER BY length(path) DESC LIMIT 1",("$p",pairId),("$keys",JsonSerializer.Serialize(ancestors))))
        using(var rd=await find.ExecuteReaderAsync(ct))
        { if(!await rd.ReadAsync(ct)) return false; row=rd.GetInt64(0); root=rd.GetString(1); }
        var tail=PathNames.Same(root,rule.Path)?string.Empty:rule.Path[(root.Length+1)..];
        var flag=new Flag(rule.RuleId??Guid.NewGuid().ToString(),rule.ConfiguredSide,tail,rule.RuleId==null?null:rule.Side);
        using var add=StorageCommand.Create(db,tx,"UPDATE synced_entry SET keep=json_insert(coalesce(keep,'[]'),'$[#]',json($flag)) WHERE rowid=$r",("$r",row),("$flag",JsonSerializer.Serialize(flag)));
        await add.ExecuteNonQueryAsync(ct); return true;
    }

    internal static IEnumerable<Flag> Carry(string json,SyncedEntry before,SyncedEntry first,SyncedEntry? second,SyncedEntry result)
    {
        using var data=JsonDocument.Parse(json);
        foreach(var item in data.RootElement.EnumerateArray())
        {
            var flag=item.ValueKind==JsonValueKind.String?Decode("text",item.GetString()!,before.LocalId,before.DropboxId):Decode("object",item.GetRawText(),before.LocalId,before.DropboxId);
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            {
                if(flag.Endpoint!=null && flag.Endpoint!=side) continue;
                var owner=first.IdOn(side)==before.IdOn(side)?first:second?.IdOn(side)==before.IdOn(side)?second:null;
                if((owner??first)!=result) continue;
                var fixedPath=flag.FixedPath ?? (owner!=null?null:flag.Tail.Length==0?before.PathOn(side):PathNames.Child(before.PathOn(side),flag.Tail));
                yield return flag with { Endpoint=side,FixedPath=fixedPath };
            }
        }
    }
}
