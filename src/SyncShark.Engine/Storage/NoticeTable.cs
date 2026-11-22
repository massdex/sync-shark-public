using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Stores a notice slot and its indexed subject members as one change.</summary>
public sealed class NoticeTable
{
    private const string Owner=PairTable.PairKey;
    private const string Select="SELECT issue_id,kind,cause,path,subject_json,at_ticks,dismissed,ignored FROM issue WHERE pair_id="+Owner+" AND ";
    private static readonly JsonSerializerOptions MemberFormat=new(){Converters={new JsonStringEnumConverter(allowIntegerValues:false)}};
    private static async Task<Notice> Read(SqliteConnection db,SqliteTransaction? tx,SqliteDataReader rd,CancellationToken ct)
    {
        var id=rd.GetInt64(0); var kind=(NoticeKind)rd.GetInt32(1);
        if(!Enum.IsDefined(kind) || !EnumNames.TryExact<TextKey>(rd.GetString(2),out var cause)) throw new InvalidDataException();
        var subject=JsonSerializer.Deserialize<IssueSubject>(rd.GetString(4));
        if(subject?.Items is null || subject.Items.Any(i=>i is null || i.Path is null || i.Id is null || !Enum.IsDefined(i.Side))) throw new InvalidDataException();
        if(cause==TextKey.InfoLinks)
        {
            var items=new List<NoticeItem>();
            using var cmd=StorageCommand.Create(db,tx,"SELECT subject_json FROM issue_member WHERE issue_id=$id ORDER BY side,path_key","$id",id);
            using var members=await cmd.ExecuteReaderAsync(ct);
            while(await members.ReadAsync(ct))
            {
                using var doc=JsonDocument.Parse(members.GetString(0));
                if(doc.RootElement.GetProperty("Version").GetInt32()!=1 || doc.RootElement.GetProperty("Value").Deserialize<IssueSubject>(MemberFormat)?.Items is not {} found) throw new InvalidDataException();
                items.AddRange(found);
            }
            subject=subject with { Items=items };
        }
        return new(id,kind,cause,rd.GetString(3),subject,new DateTimeOffset(rd.GetInt64(5),TimeSpan.Zero),rd.GetBoolean(6),rd.GetBoolean(7));
    }
    private static async Task<Notice?> One(SqliteConnection db,SqliteTransaction? tx,string pair,string suffix,CancellationToken ct,params (string,object?)[] args)
    {
        using var cmd=StorageCommand.Create(db,tx,Select+suffix,args); cmd.Parameters.AddWithValue("$p",pair);
        using var rd=await cmd.ExecuteReaderAsync(ct); return await rd.ReadAsync(ct)?await Read(db,tx,rd,ct):null;
    }
    /// <summary>Page all pair notices including ignored and dismissed slots.</summary>
    public async IAsyncEnumerable<Notice> ListAsync(SqliteConnection db,string pairId,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize); long after=0; int count;
        do
        {
            count=0; using var cmd=StorageCommand.Create(db,null,Select+"issue_id>$after ORDER BY issue_id LIMIT $size",("$p",pairId),("$after",after),("$size",pageSize));
            using var rd=await cmd.ExecuteReaderAsync(ct);
            while(await rd.ReadAsync(ct)) { var notice=await Read(db,null,rd,ct); after=notice.Id; count++; yield return notice; }
        } while(count==pageSize);
    }
    /// <summary>Read every kind at the indexed canonical notice path.</summary>
    public async IAsyncEnumerable<Notice> NoticesAtAsync(SqliteConnection db,string pairId,string pathKey,[EnumeratorCancellation]CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Select+"pathkey(path)=$k ORDER BY kind",("$p",pairId),("$k",pathKey)); using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct)) yield return await Read(db,null,rd,ct);
    }
    /// <summary>Read one indexed notice for a subsequent reviewed-version comparison.</summary>
    public Task<Notice?> NoticeAsync(SqliteConnection db,string pairId,long id,CancellationToken ct) => One(db,null,pairId,"issue_id=$id",ct,("$id",id));
    private static async Task<long> Slot(SqliteTransaction tx,string pair,Notice notice,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(tx.Connection!,tx,"INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks,dismissed,ignored) VALUES("+Owner+",$kind,$cause,$path,$subject,$at,$dismissed,$ignored) ON CONFLICT(pair_id,path,kind) DO UPDATE SET cause=excluded.cause,subject_json=excluded.subject_json,at_ticks=excluded.at_ticks,dismissed=excluded.dismissed,ignored=excluded.ignored RETURNING issue_id",
            ("$p",pair),("$kind",(int)notice.Kind),("$cause",notice.Cause.ToString()),("$path",notice.Path),("$subject",JsonSerializer.Serialize(notice.Subject)),("$at",notice.At.UtcTicks),("$dismissed",notice.Dismissed),("$ignored",notice.Ignored));
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidDataException());
    }
    private static async Task Member(SqliteTransaction tx,string pair,long id,NoticeItem item,IssueSubject subject,TextKey? skip,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(tx.Connection!,tx,"INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json,skip_cause) VALUES($id,"+Owner+",$s,$k,$identity,$subject,$skip)",
            ("$id",id),("$p",pair),("$s",(int)item.Side),("$k",PathNames.KeyFor(item.Side,item.Path)),("$identity",item.Id.Length==0?null:Encoding.IdFor(item.Side,item.Id)),("$subject",Encoding.Envelope(subject with {Items=new[]{item}})),("$skip",skip?.ToString()));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    /// <summary>Upsert the slot and its members atomically without changing another kind at the path.</summary>
    public async Task SaveNoticeAsync(SqliteConnection db,string pairId,Notice notice,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false); await SaveNoticeAsync(tx,pairId,notice,ct); tx.Commit();
    }
    /// <summary>Publish a notice inside the binding writer's existing immediate transaction.</summary>
    public async Task SaveNoticeAsync(SqliteTransaction tx,string pairId,Notice notice,CancellationToken ct)
    {
        var links=notice.Cause==TextKey.InfoLinks;
        var id=await Slot(tx,pairId,links?notice with { Subject=notice.Subject with { Items=[] } }:notice,ct);
        if(links) return; // Link membership changes only through the observed-member operation.
        using var clear=StorageCommand.Create(tx.Connection!,tx,"DELETE FROM issue_member WHERE issue_id=$id","$id",id); await clear.ExecuteNonQueryAsync(ct);
        foreach(var item in notice.Subject.Items.Distinct()) await Member(tx,pairId,id,item,notice.Subject,notice.Kind==NoticeKind.Skipped?notice.Cause:null,ct);
    }
    internal static async Task<bool> Close(SqliteTransaction tx,string pair,Notice expected,CancellationToken ct)
    {
        var current=await One(tx.Connection!,tx,pair,"issue_id=$id",ct,("$id",expected.Id));
        if(current is null || !current.Matches(expected)) return false;
        using var cmd=StorageCommand.Create(tx.Connection!,tx,"DELETE FROM issue_member WHERE issue_id=$id; DELETE FROM issue WHERE issue_id=$id","$id",expected.Id); await cmd.ExecuteNonQueryAsync(ct); return true;
    }
    /// <summary>Close only a still-matching notice, removing its member rows in the same transaction.</summary>
    public async Task CloseNoticeAsync(SqliteConnection db,string pairId,Notice expected,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false); await Close(tx,pairId,expected,ct); tx.Commit();
    }
    /// <summary>Stream path-only and member notices overlapping their own physical side, including ancestors.</summary>
    public async IAsyncEnumerable<Notice> NoticesUnderAsync(SqliteConnection db,string pairId,Scope footprint,[EnumeratorCancellation]CancellationToken ct)
    {
        var local=PathNames.Key(footprint.LocalPath); var cloud=PathNames.DropboxKey(footprint.DropboxPath);
        // Ancestor keys use JSON array membership, not LIKE (which treats names as patterns).
        static string Overlap(string column,string key) => "($"+key+"='' OR "+column+" IN (SELECT value FROM json_each($"+key+"parents)) OR ("+column+">=$"+key+"||'/' AND "+column+"<$"+key+"||'0'))";
        var member="SELECT issue_id FROM issue_member WHERE pair_id="+Owner+" AND ((side=0 AND "+Overlap("path_key","l")+") OR (side=1 AND "+Overlap("path_key","d")+"))";
        using var cmd=StorageCommand.Create(db,null,Select+"(issue_id IN ("+member+") OR (NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=issue.issue_id) AND ("+Overlap("pathkey(path)","l")+" OR "+Overlap("pathkey(path)","d")+")))",("$p",pairId),("$l",local),("$d",cloud),("$lparents",JsonSerializer.Serialize(PathNames.Ancestors(local).Append(local))),("$dparents",JsonSerializer.Serialize(PathNames.Ancestors(cloud).Append(cloud))));
        using var rd=await cmd.ExecuteReaderAsync(ct); while(await rd.ReadAsync(ct)) yield return await Read(db,null,rd,ct);
    }
    /// <summary>Add or retire only the observed link identity; remove the one pair notice when its last member goes.</summary>
    public async Task UpdateLinkMemberAsync(SqliteConnection db,string pairId,NoticeItem member,bool present,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        using var find=StorageCommand.Create(db,tx,"SELECT issue_id FROM issue WHERE pair_id="+Owner+" AND path='' AND kind=$kind AND cause=$cause",("$p",pairId),("$kind",(int)NoticeKind.Info),("$cause",nameof(TextKey.InfoLinks)));
        var existing=await find.ExecuteScalarAsync(ct);
        if(existing is null && !present) return;
        var subject=new IssueSubject([],null);
        var id=existing is long value?value:await Slot(tx,pairId,new(0,NoticeKind.Info,TextKey.InfoLinks,"",subject,DateTimeOffset.UtcNow,false),ct);
        var key=PathNames.KeyFor(member.Side,member.Path);
        using var remove=StorageCommand.Create(db,tx,"DELETE FROM issue_member WHERE issue_id=$id AND side=$s AND path_key=$k"+(present?"":" AND identity IS $identity"),("$id",id),("$s",(int)member.Side),("$k",key));
        if(!present) remove.Parameters.AddWithValue("$identity",(member.Id.Length==0?null:Encoding.IdFor(member.Side,member.Id)) ?? DBNull.Value);
        await remove.ExecuteNonQueryAsync(ct);
        if(present) await Member(tx,pairId,id,member,subject,TextKey.SkipLink,ct);
        else { using var empty=StorageCommand.Create(db,tx,"DELETE FROM issue WHERE issue_id=$id AND NOT EXISTS(SELECT 1 FROM issue_member WHERE issue_id=$id)","$id",id); await empty.ExecuteNonQueryAsync(ct); }
        tx.Commit();
    }
}
