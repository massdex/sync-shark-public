using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Stores a notice slot and its indexed subject members as one change.</summary>
public sealed class NoticeTable
{
    private const string Owner=PairTable.PairKey;
    private const string Columns="i.issue_id,i.kind,i.cause,i.path,i.subject_json,i.at_ticks,i.dismissed,i.ignored";
    private const string Select="SELECT "+Columns+" FROM issue i WHERE pair_id="+Owner+" AND ";
    private static Notice Row(SqliteDataReader rd)
    {
        var id=rd.GetInt64(0); var kind=(NoticeKind)rd.GetInt32(1);
        if(!Enum.IsDefined(kind) || !EnumNames.TryExact<TextKey>(rd.GetString(2),out var cause)) throw new InvalidDataException();
        var subject=Encoding.DecodeSubject(rd.GetString(4));
        return new(id,kind,cause,rd.GetString(3),subject,new DateTimeOffset(rd.GetInt64(5),TimeSpan.Zero),rd.GetBoolean(6),rd.GetBoolean(7));
    }
    private static async Task<Notice> Read(SqliteConnection db,SqliteTransaction? tx,SqliteDataReader rd,CancellationToken ct)
    {
        var notice=Row(rd);
        if(notice.Cause!=TextKey.InfoLinks) return notice;
        // Aggregate in SQLite: the read projection needs counts and distinct top folders, not all link identities.
        using var cmd=StorageCommand.Create(db,tx,"""
            WITH names(path) AS (SELECT json_extract(subject_json,'$.Value.Items[0].Path') FROM issue_member WHERE issue_id=$id),
            folders(name) AS (SELECT CASE WHEN instr(path,'/')=0 THEN '/' ELSE substr(path,1,instr(path,'/')-1) END FROM names)
            SELECT min(name),count(*) FROM folders GROUP BY pathkey(name) ORDER BY min(name)
            ""","$id",notice.Id);
        using var rows=await cmd.ExecuteReaderAsync(ct); int count=0; var folders=new List<string>();
        while(await rows.ReadAsync(ct)) { folders.Add(rows.GetString(0)); count=checked(count+rows.GetInt32(1)); }
        return notice with { LinkCount=count,LinkFolders=folders };
    }
    private static async Task<Notice?> One(SqliteConnection db,SqliteTransaction? tx,string pair,string suffix,CancellationToken ct,params (string,object?)[] args)
    {
        using var cmd=StorageCommand.Create(db,tx,Select+suffix,args); cmd.Parameters.AddWithValue("$p",pair);
        using var rd=await cmd.ExecuteReaderAsync(ct); return await rd.ReadAsync(ct)?await Read(db,tx,rd,ct):null;
    }
    /// <summary>Page all pair notices including ignored and dismissed slots.</summary>
    public async IAsyncEnumerable<Notice> ListAsync(SqliteConnection db,string pairId,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize); string path=string.Empty,cause=string.Empty; int kind=-1, count;
        do
        {
            count=0; using var cmd=StorageCommand.Create(db,null,Select+"(path,kind,cause)>($path,$kind,$cause) ORDER BY path,kind,cause LIMIT $size",("$p",pairId),("$path",path),("$kind",kind),("$cause",cause),("$size",pageSize));
            using var rd=await cmd.ExecuteReaderAsync(ct);
            while(await rd.ReadAsync(ct)) { var notice=await Read(db,null,rd,ct); path=notice.Path; kind=(int)notice.Kind; cause=notice.Cause.ToString(); count++; yield return notice; }
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
        using var cmd=StorageCommand.Create(tx.Connection!,tx,"INSERT INTO issue(pair_id,kind,cause,path,subject_json,at_ticks,dismissed,ignored) VALUES("+Owner+",$kind,$cause,$path,$subject,$at,$dismissed,$ignored) ON CONFLICT(pair_id,path,kind,cause) DO UPDATE SET subject_json=excluded.subject_json,at_ticks=excluded.at_ticks,dismissed=excluded.dismissed,ignored=excluded.ignored RETURNING issue_id",
            ("$p",pair),("$kind",(int)notice.Kind),("$cause",notice.Cause.ToString()),("$path",notice.Path),("$subject",JsonSerializer.Serialize(notice.Subject)),("$at",notice.At.UtcTicks),("$dismissed",notice.Dismissed),("$ignored",notice.Ignored));
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidDataException());
    }
    private static async Task Member(SqliteTransaction tx,string pair,long id,NoticeItem item,IssueSubject subject,TextKey? skip,CancellationToken ct)
    {
        if(skip!=null) await ReleaseSkipSlot(tx,pair,id,item,ct);
        using var cmd=StorageCommand.Create(tx.Connection!,tx,"INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json,skip_cause) VALUES($id,"+Owner+",$s,$k,$identity,$subject,$skip)",
            ("$id",id),("$p",pair),("$s",(int)item.Side),("$k",PathNames.KeyFor(item.Side,item.Path)),("$identity",item.Id.Length==0?null:Encoding.IdFor(item.Side,item.Id)),("$subject",Encoding.Envelope(subject with {Items=new[]{item}})),("$skip",skip?.ToString()));
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task ReleaseSkipSlot(SqliteTransaction tx,string pair,long next,NoticeItem item,CancellationToken ct)
    {
        var db=tx.Connection!;var key=PathNames.KeyFor(item.Side,item.Path);
        using var remove=StorageCommand.Create(db,tx,"DELETE FROM issue_member WHERE pair_id="+Owner+" AND side=$s AND path_key=$k AND skip_cause IS NOT NULL RETURNING issue_id",("$p",pair),("$s",(int)item.Side),("$k",key));
        if(await remove.ExecuteScalarAsync(ct) is not long previous || previous==next) return;
        using var empty=StorageCommand.Create(db,tx,"DELETE FROM issue WHERE issue_id=$id AND NOT EXISTS(SELECT 1 FROM issue_member WHERE issue_id=$id)","$id",previous);
        if(await empty.ExecuteNonQueryAsync(ct)!=0) return;
        using var header=StorageCommand.Create(db,tx,Select+"issue_id=$id AND kind=$kind",("$p",pair),("$id",previous),("$kind",(int)NoticeKind.Skipped));
        Notice skipped;
        using(var row=await header.ExecuteReaderAsync(ct)) {if(!await row.ReadAsync(ct)) return;skipped=Row(row);}
        var survivors=new List<NoticeItem>();Side? incoming=null;string? cause=null;
        using var read=StorageCommand.Create(db,tx,"SELECT subject_json,skip_cause FROM issue_member WHERE issue_id=$id ORDER BY side,path_key","$id",previous);
        using(var rows=await read.ExecuteReaderAsync(ct)) while(await rows.ReadAsync(ct))
        {
            var subject=Encoding.DecodeMemberSubject(rows.GetString(0));survivors.AddRange(subject.Items);incoming??=subject.Incoming;
            var name=rows.GetString(1);if(cause==null || name==skipped.Cause.ToString()) cause=name;
        }
        using var update=StorageCommand.Create(db,tx,"UPDATE issue SET subject_json=$subject,cause=$cause WHERE issue_id=$id",("$subject",JsonSerializer.Serialize(skipped.Subject with { Items=survivors, Incoming=incoming })),("$cause",cause),("$id",previous));
        await update.ExecuteNonQueryAsync(ct);
    }
    /// <summary>Upsert the slot and its members atomically without changing another kind at the path.</summary>
    public async Task SaveNoticeAsync(SqliteConnection db,string pairId,Notice notice,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false); await SaveNoticeAsync(tx,pairId,notice,ct); tx.Commit();
    }
    /// <summary>Publish a notice inside the binding writer's existing immediate transaction.</summary>
    public async Task SaveNoticeAsync(SqliteTransaction tx,string pairId,Notice notice,CancellationToken ct)
    {
        if(notice.Kind==NoticeKind.Failure)
        {
            var prior=await One(tx.Connection!,tx,pairId,"path=$path AND kind=$kind",ct,("$path",notice.Path),("$kind",(int)notice.Kind));
            if(prior?.Subject.Move is {} move && notice.Subject.Move!=move) return;
            if(prior!=null && prior.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None &&
                (notice.Subject.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.None || prior.Subject.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice) &&
                prior.Subject.Items.Any(old=>old.Side==Side.Local && old.Id.Length!=0 && notice.Subject.Items.Any(next=>next.Side==Side.Local && next.Id==old.Id && PathNames.Same(next.Path,old.Path))))
                notice=notice with {Subject=notice.Subject with { FirstRunDeletion=prior.Subject.FirstRunDeletion }};
            if(prior!=null && prior.Cause==notice.Cause && prior.Subject.HasSameItems(notice.Subject) && !(prior.Dismissed && prior.Cause==TextKey.FailRecycledLate))
            {
                var subject=notice.Subject;
                if(prior.Subject.Incoming==subject.Incoming && prior.Subject.FirstRunDeletion==subject.FirstRunDeletion && prior.Subject.Holder==subject.Holder) return;
                notice=prior with {Subject=subject};
            }
            else if(prior!=null && prior.Cause!=notice.Cause)
            {
                using var diagnosis=StorageCommand.Create(tx.Connection!,tx,"UPDATE issue SET cause=$cause WHERE issue_id=$id",("$cause",notice.Cause.ToString()),("$id",prior.Id));
                await diagnosis.ExecuteNonQueryAsync(ct);
            }
        }
        if(notice.Kind==NoticeKind.PutBack)
        {
            // Reselect by canonical path, not by cause: a later put-back owns this path even if its diagnosis changes.
            using var select=StorageCommand.Create(tx.Connection!,tx,"SELECT issue_id,at_ticks,path,cause FROM issue WHERE pair_id="+Owner+" AND pathkey(path)=$key AND kind=$kind",("$p",pairId),("$key",PathNames.Key(notice.Path)),("$kind",(int)notice.Kind));
            long? rekey=null;
            using(var row=await select.ExecuteReaderAsync(ct)) if(await row.ReadAsync(ct))
            {
                if(row.GetInt64(1)>notice.At.UtcTicks) return;
                if(row.GetString(2)!=notice.Path || row.GetString(3)!=notice.Cause.ToString()) rekey=row.GetInt64(0);
            }
            if(rekey.HasValue)
            {
                using var rename=StorageCommand.Create(tx.Connection!,tx,"UPDATE issue SET path=$path,cause=$cause WHERE issue_id=$id",("$path",notice.Path),("$cause",notice.Cause.ToString()),("$id",rekey.Value));
                await rename.ExecuteNonQueryAsync(ct);
            }
        }
        var links=notice.Cause==TextKey.InfoLinks;
        var id=await Slot(tx,pairId,links?notice with { Subject=notice.Subject with { Items=[] } }:notice,ct);
        if(links) return; // Link membership changes only through the observed-member operation.
        using var clear=StorageCommand.Create(tx.Connection!,tx,"DELETE FROM issue_member WHERE issue_id=$id","$id",id); await clear.ExecuteNonQueryAsync(ct);
        foreach(var item in notice.Subject.Items.Distinct()) await Member(tx,pairId,id,item,notice.Subject,notice.Kind==NoticeKind.Skipped?notice.Cause:null,ct);
    }
    /// <summary>Set visibility only for the exact reviewed notice still present in the transaction.</summary>
    public async Task<bool> UpdateNoticeVisibilityAsync(SqliteConnection db,string pairId,Notice expected,bool ignored,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        var current=await One(db,tx,pairId,"issue_id=$id",ct,("$id",expected.Id));
        if(current is null || !current.Matches(expected)) return false;
        using var cmd=StorageCommand.Create(db,tx,"UPDATE issue SET ignored=$value WHERE issue_id=$id",("$id",expected.Id),("$value",ignored));
        await cmd.ExecuteNonQueryAsync(ct); tx.Commit(); return true;
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
    public IAsyncEnumerable<Notice> NoticesUnderAsync(SqliteConnection db,string pairId,Scope footprint,CancellationToken ct) =>
        NoticesUnderAsync(db,pairId,footprint,DefaultPageSize.Value,ct);
    private static readonly Lazy<int> DefaultPageSize=new(()=>SettingsParser.Load().StoragePageSize);
    /// <summary>Deliver scoped link members as they are read, using the configured page size and the last indexed key.</summary>
    public async IAsyncEnumerable<Notice> NoticesUnderAsync(SqliteConnection db,string pairId,Scope footprint,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        var local=PathNames.Key(footprint.LocalPath); var cloud=PathNames.DropboxKey(footprint.DropboxPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        using(var header=StorageCommand.Create(db,null,Select+"path='' AND kind=$kind AND cause=$cause",("$p",pairId),("$kind",(int)NoticeKind.Info),("$cause",nameof(TextKey.InfoLinks))))
        {
            Notice? link;
            using(var h=await header.ExecuteReaderAsync(ct)) link=await h.ReadAsync(ct)?Row(h):null;
            if(link!=null)
            foreach(var side in new[]{Side.Local,Side.Dropbox})
            {
                var key=side==Side.Local?local:cloud;
                var (low,high)=PathNames.DescendantBounds(key);
                // At most one link per side/key. Ancestors are exact probes; descendants resume on that same index.
                foreach(var exact in (key.Length==0?Enumerable.Empty<string>():PathNames.Ancestors(key).Append(key)).Cast<string?>().Append(null))
                {
                    string? after=null; int count;
                    do
                    {
                        var range=exact!=null?"path_key=$key":key.Length==0?"1=1":"path_key>=$low AND path_key<$high";
                        using var cmd=StorageCommand.Create(db,null,"SELECT path_key,subject_json FROM issue_member WHERE issue_id=$id AND side=$side AND "+range+(after==null?"":" AND path_key>$after")+" ORDER BY path_key LIMIT $take",("$id",link.Id),("$side",(int)side),("$take",pageSize));
                        if(exact!=null) cmd.Parameters.AddWithValue("$key",exact);
                        else if(key.Length!=0) { cmd.Parameters.AddWithValue("$low",low);cmd.Parameters.AddWithValue("$high",high); }
                        if(after!=null) cmd.Parameters.AddWithValue("$after",after);
                        using var rows=await cmd.ExecuteReaderAsync(ct);count=0;
                        while(await rows.ReadAsync(ct)) { after=rows.GetString(0);count++;yield return link with {Subject=Encoding.DecodeMemberSubject(rows.GetString(1))}; }
                    } while(exact==null && count==pageSize);
                }
            }
        }
        // Separate range and ancestor seeks. An OR mixed with an empty-root predicate would scan the whole pair.
        string Branch(bool members,bool pc)
        {
            var key=pc?local:cloud; var k=pc?"$l":"$d";
            var column=members?"path_key":"pathkey(path)";
            var prefix=members?"SELECT issue_id,member_id FROM issue_member WHERE pair_id="+Owner+" AND side="+(pc?"0":"1"):
                "SELECT issue_id,NULL FROM issue WHERE pair_id="+Owner;
            var extra=members?" AND skip_cause IS NOT 'SkipLink'":" AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=issue.issue_id)";
            if(key.Length==0) return prefix+extra;
            return prefix+" AND "+column+" IN (SELECT value FROM json_each("+k+"parents))"+extra+" UNION ALL "+prefix+
                " AND "+column+">="+k+"low AND "+column+"<"+k+"high"+extra;
        }
        var hits=string.Join(" UNION ALL ",Branch(true,true),Branch(true,false),Branch(false,true),Branch(false,false));
        var sql="WITH hits(id,member) AS ("+hits+") SELECT "+Columns+" FROM (SELECT DISTINCT id FROM hits) r CROSS JOIN issue i ON i.issue_id=r.id";
        using var query=StorageCommand.Create(db,null,sql,"$p",pairId);
        void Bind(string prefix,string key)
        {
            if(key.Length==0) return;var (low,high)=PathNames.DescendantBounds(key);
            query.Parameters.AddWithValue(prefix+"low",low);query.Parameters.AddWithValue(prefix+"high",high);
            query.Parameters.AddWithValue(prefix+"parents",JsonSerializer.Serialize(PathNames.Ancestors(key).Append(key)));
        }
        Bind("$l",local); Bind("$d",cloud);
        using var rd=await query.ExecuteReaderAsync(ct);while(await rd.ReadAsync(ct)) yield return Row(rd);
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
        if(present) await Member(tx,pairId,id,member,subject,TextKey.SkipLink,ct);
        else
        {
            using var remove=StorageCommand.Create(db,tx,"DELETE FROM issue_member WHERE issue_id=$id AND side=$s AND path_key=$k AND identity IS $identity",("$id",id),("$s",(int)member.Side),("$k",key),("$identity",member.Id.Length==0?null:Encoding.IdFor(member.Side,member.Id)));
            await remove.ExecuteNonQueryAsync(ct);
            using var empty=StorageCommand.Create(db,tx,"DELETE FROM issue WHERE issue_id=$id AND NOT EXISTS(SELECT 1 FROM issue_member WHERE issue_id=$id)","$id",id);await empty.ExecuteNonQueryAsync(ct);
        }
        tx.Commit();
    }
}
