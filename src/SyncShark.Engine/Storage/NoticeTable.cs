using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Stores a notice slot and its indexed subject members as one change.</summary>
public sealed class NoticeTable
{
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
        using var cmd=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableRead1),"$id",notice.Id);
        using var rows=await cmd.ExecuteReaderAsync(ct); int count=0; var folders=new List<string>();
        while(await rows.ReadAsync(ct)) { folders.Add(rows.GetString(0)); count=checked(count+rows.GetInt32(1)); }
        return notice with { LinkCount=count,LinkFolders=folders };
    }
    private static async Task<Notice?> One(SqliteConnection db,SqliteTransaction? tx,string pair,string suffix,CancellationToken ct,params (string,object?)[] args)
    {
        using var cmd=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableSelect)+suffix,args); cmd.Parameters.AddWithValue("$p",pair);
        using var rd=await cmd.ExecuteReaderAsync(ct); return await rd.ReadAsync(ct)?await Read(db,tx,rd,ct):null;
    }
    /// <summary>Page all pair notices including ignored and dismissed slots.</summary>
    public async IAsyncEnumerable<Notice> ListAsync(SqliteConnection db,string pairId,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize); string path=string.Empty,cause=string.Empty; int kind=-1;
        var page=new List<Notice>(); var sql=Sql.Get(SqlKey.NoticeTableList1);
        do
        {
            page.Clear();
            using(var cmd=StorageCommand.Create(db,null,sql,("$p",pairId),("$path",path),("$kind",kind),("$cause",cause),("$size",pageSize)))
            using(var rd=await cmd.ExecuteReaderAsync(ct))
                while(await rd.ReadAsync(ct)) page.Add(await Read(db,null,rd,ct));
            foreach(var notice in page)
            {
                ct.ThrowIfCancellationRequested();
                path=notice.Path; kind=(int)notice.Kind; cause=notice.Cause.ToString(); yield return notice;
            }
        } while(page.Count==pageSize);
    }
    /// <summary>Read every kind at the indexed canonical notice path.</summary>
    public async IAsyncEnumerable<Notice> NoticesAtAsync(SqliteConnection db,string pairId,string pathKey,[EnumeratorCancellation]CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.NoticeTableNoticesAt1),("$p",pairId),("$k",pathKey)); using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct)) yield return await Read(db,null,rd,ct);
    }
    /// <summary>Read one indexed notice for a subsequent reviewed-version comparison.</summary>
    public Task<Notice?> NoticeAsync(SqliteConnection db,string pairId,long id,CancellationToken ct) => One(db,null,pairId,Sql.Get(SqlKey.NoticeTableNotice1),ct,("$id",id));
    private static async Task<long> Slot(SqliteTransaction tx,string pair,Notice notice,CancellationToken ct)
    {
        using var cmd=StorageCommand.Create(tx.Connection!,tx,Sql.Get(SqlKey.NoticeTableSlot1),
            ("$p",pair),("$kind",(int)notice.Kind),("$cause",notice.Cause.ToString()),("$path",notice.Path),("$subject",JsonSerializer.Serialize(notice.Subject)),("$at",notice.At.UtcTicks),("$dismissed",notice.Dismissed),("$ignored",notice.Ignored));
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? throw new InvalidDataException());
    }
    private static async Task<int> Member(SqliteTransaction tx,string pair,long id,NoticeItem item,IssueSubject subject,TextKey? skip,CancellationToken ct,bool releasePrevious=true)
    {
        if(skip!=null && releasePrevious) await ReleaseSkipSlot(tx,pair,id,item,ct);
        using var cmd=StorageCommand.Create(tx.Connection!,tx,Sql.Get(SqlKey.NoticeTableMember1),
            ("$id",id),("$p",pair),("$s",(int)item.Side),("$k",PathNames.KeyFor(item.Side,item.Path)),("$identity",item.Id.Length==0?null:Encoding.IdFor(item.Side,item.Id)),("$subject",Encoding.Envelope(subject with {Items=new[]{item}})),("$skip",skip?.ToString()));
        return await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task ReleaseSkipSlot(SqliteTransaction tx,string pair,long next,NoticeItem item,CancellationToken ct)
    {
        var db=tx.Connection!;var key=PathNames.KeyFor(item.Side,item.Path);
        using var remove=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableReleaseSkipSlot1),("$p",pair),("$s",(int)item.Side),("$k",key));
        if(await remove.ExecuteScalarAsync(ct) is not long previous || previous==next) return;
        using var empty=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableReleaseSkipSlot2),"$id",previous);
        if(await empty.ExecuteNonQueryAsync(ct)!=0) return;
        using var header=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableReleaseSkipSlot3),("$p",pair),("$id",previous),("$kind",(int)NoticeKind.Skipped));
        Notice skipped;
        using(var row=await header.ExecuteReaderAsync(ct)) {if(!await row.ReadAsync(ct)) return;skipped=Row(row);}
        var survivors=new List<NoticeItem>();Side? incoming=null;string? cause=null;
        using var read=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableReleaseSkipSlot4),"$id",previous);
        using(var rows=await read.ExecuteReaderAsync(ct)) while(await rows.ReadAsync(ct))
        {
            var subject=Encoding.DecodeMemberSubject(rows.GetString(0));survivors.AddRange(subject.Items);incoming??=subject.Incoming;
            var name=rows.GetString(1);if(cause==null || name==skipped.Cause.ToString()) cause=name;
        }
        using var update=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableReleaseSkipSlot5),("$subject",JsonSerializer.Serialize(skipped.Subject with { Items=survivors, Incoming=incoming })),("$cause",cause),("$id",previous));
        await update.ExecuteNonQueryAsync(ct);
    }
    /// <summary>Commit the save and return whether it applied and the pre-write identity and stored subject of its replaced slot, if any.</summary>
    public async Task<(bool Applied,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior)> SaveNoticeAsync(SqliteConnection db,string pairId,Notice notice,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false); var result=await SaveNoticeAsync(tx,pairId,notice,ct); tx.Commit(); return result;
    }
    /// <summary>Return the winning write's prior slot from its selection in the caller's immediate transaction. Consume the result only after commit.</summary>
    public async Task<(bool Applied,(long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? Prior)> SaveNoticeAsync(SqliteTransaction tx,string pairId,Notice notice,CancellationToken ct)
    {
        var putBack=notice.Kind==NoticeKind.PutBack;
        // Independent root failures and Info notices share the existing cause-keyed slot.
        var byCause=notice.Kind==NoticeKind.Info || (notice.Kind==NoticeKind.Failure && notice.Path.Length==0);
        var predicate=(putBack?Sql.Get(SqlKey.NoticeTableSaveNotice1):Sql.Get(SqlKey.NoticeTableSaveNotice2))+Sql.Get(SqlKey.NoticeTableSaveNotice3)+(byCause?Sql.Get(SqlKey.NoticeTableSaveNotice4):"");
        (long Id,string Path,NoticeKind Kind,TextKey Cause,string SubjectJson)? displaced=null;
        using(var select=StorageCommand.Create(tx.Connection!,tx,Sql.Get(SqlKey.NoticeTableSelect)+predicate,("$p",pairId),("$path",putBack?PathNames.Key(notice.Path):notice.Path),("$kind",(int)notice.Kind)))
        {
            if(byCause) select.Parameters.AddWithValue("$cause",notice.Cause.ToString());
            using(var row=await select.ExecuteReaderAsync(ct)) if(await row.ReadAsync(ct))
            {
                if(putBack && row.GetInt64(5)>notice.At.UtcTicks) return (false,null);
                if(!EnumNames.TryExact<TextKey>(row.GetString(2),out var cause)) throw new InvalidDataException();
                displaced=(row.GetInt64(0),row.GetString(3),notice.Kind,cause,row.GetString(4));
                if(notice.Kind==NoticeKind.Failure)
                {
                    var (reject,reconciled)=ReconcileFailure(notice,Row(row));
                    if(reject) return (false,null);
                    notice=reconciled;
                }
            }
        }
        if(displaced is {} old && (old.Path!=notice.Path || old.Cause!=notice.Cause))
        {
            using var rekey=StorageCommand.Create(tx.Connection!,tx,Sql.Get(SqlKey.NoticeTableSaveNotice5),("$path",notice.Path),("$cause",notice.Cause.ToString()),("$id",old.Id));
            await rekey.ExecuteNonQueryAsync(ct);
        }
        var links=notice.Cause==TextKey.InfoLinks;
        var id=await Slot(tx,pairId,links?notice with { Subject=notice.Subject with { Items=[] } }:notice,ct);
        if(links) return (true,displaced); // Link membership changes only through the observed-member operation.
        using var clear=StorageCommand.Create(tx.Connection!,tx,Sql.Get(SqlKey.NoticeTableSaveNotice6),"$id",id); await clear.ExecuteNonQueryAsync(ct);
        foreach(var item in notice.Subject.Items.Distinct()) await Member(tx,pairId,id,item,notice.Subject,notice.Kind==NoticeKind.Skipped?notice.Cause:null,ct);
        return (true,displaced);
    }
    // Pure: decides refusal and any carried-over fields for a failure notice against the stored preimage.
    private static (bool Reject,Notice Notice) ReconcileFailure(Notice notice,Notice prior)
    {
        if(prior.Subject.Move is {} move && notice.Subject.Move!=move) return (true,notice);
        if(prior.Subject.FirstRunDeletion!=IssueSubject.FirstRunDeletionKind.None &&
            (notice.Subject.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.None || prior.Subject.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice) &&
            prior.Subject.Items.Any(old=>old.Side==Side.Local && old.Id.Length!=0 && notice.Subject.Items.Any(next=>next.Side==Side.Local && next.Id==old.Id && PathNames.Same(next.Path,old.Path))))
            notice=notice with {Subject=notice.Subject with { FirstRunDeletion=prior.Subject.FirstRunDeletion }};
        if(prior.Cause==notice.Cause && prior.Subject.HasSameItems(notice.Subject) && !(prior.Dismissed && prior.Cause==TextKey.FailRecycledLate))
        {
            var subject=notice.Subject;
            if(prior.Subject.Incoming==subject.Incoming && prior.Subject.FirstRunDeletion==subject.FirstRunDeletion && prior.Subject.Holder==subject.Holder) return (true,notice);
            notice=prior with {Subject=subject};
        }
        return (false,notice);
    }
    /// <summary>Set visibility only for the exact reviewed notice still present in the transaction.</summary>
    public async Task<bool> UpdateNoticeVisibilityAsync(SqliteConnection db,string pairId,Notice expected,bool ignored,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        var current=await One(db,tx,pairId,Sql.Get(SqlKey.NoticeTableUpdateVisibility1),ct,("$id",expected.Id));
        if(current is null || !current.Matches(expected)) return false;
        using var cmd=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableUpdateVisibility2),("$id",expected.Id),("$value",ignored));
        await cmd.ExecuteNonQueryAsync(ct); tx.Commit(); return true;
    }
    internal static async Task<bool> Close(SqliteTransaction tx,string pair,Notice expected,CancellationToken ct)
    {
        var current=await One(tx.Connection!,tx,pair,Sql.Get(SqlKey.NoticeTableClose1),ct,("$id",expected.Id));
        if(current is null || !current.Matches(expected)) return false;
        using var cmd=StorageCommand.Create(tx.Connection!,tx,Sql.Get(SqlKey.NoticeTableClose2),"$id",expected.Id); await cmd.ExecuteNonQueryAsync(ct); return true;
    }
    /// <summary>Close only a still-matching notice, removing its member rows in the same transaction.</summary>
    public async Task<bool> CloseNoticeAsync(SqliteConnection db,string pairId,Notice expected,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false); var closed=await Close(tx,pairId,expected,ct); tx.Commit(); return closed;
    }
    /// <summary>Deliver scoped link members as they are read, using the configured page size and the last indexed key.</summary>
    public async IAsyncEnumerable<Notice> NoticesUnderAsync(SqliteConnection db,string pairId,Scope footprint,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        var local=PathNames.Key(footprint.LocalPath); var cloud=PathNames.DropboxKey(footprint.DropboxPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        using(var header=StorageCommand.Create(db,null,Sql.Get(SqlKey.NoticeTableNoticesUnder1),("$p",pairId),("$kind",(int)NoticeKind.Info),("$cause",nameof(TextKey.InfoLinks))))
        {
            Notice? link;
            using(var h=await header.ExecuteReaderAsync(ct)) link=await h.ReadAsync(ct)?Row(h):null;
            string exactSql=Sql.Get(SqlKey.NoticeTableNoticesUnder2),rootSql=Sql.Get(SqlKey.NoticeTableNoticesUnder3),spanSql=Sql.Get(SqlKey.NoticeTableNoticesUnder4),headSql=Sql.Get(SqlKey.NoticeTableNoticesUnder5),resumeSql=Sql.Get(SqlKey.NoticeTableNoticesUnder6),tailSql=Sql.Get(SqlKey.NoticeTableNoticesUnder7);
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
                        var range=exact!=null?exactSql:key.Length==0?rootSql:spanSql;
                        using var cmd=StorageCommand.Create(db,null,headSql+range+(after==null?"":resumeSql)+tailSql,("$id",link.Id),("$side",(int)side),("$take",pageSize));
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
        using var query=StorageCommand.Create(db,null,RangeSql(local,cloud),"$p",pairId);
        Bind(query,"$l",local); Bind(query,"$d",cloud);
        using var rd=await query.ExecuteReaderAsync(ct);while(await rd.ReadAsync(ct)) yield return Row(rd);
    }
    private static string Branch(bool members,bool pc,string local,string cloud)
    {
        var key=pc?local:cloud; var k=pc?"$l":"$d";
        var column=members?Sql.Get(SqlKey.NoticeTableBranch1):Sql.Get(SqlKey.NoticeTableBranch2);
        var prefix=members?Sql.Get(SqlKey.NoticeTableBranch3)+(pc?Sql.Get(SqlKey.NoticeTableBranch4):Sql.Get(SqlKey.NoticeTableBranch5)):
            Sql.Get(SqlKey.NoticeTableBranch6);
        var extra=members?Sql.Get(SqlKey.NoticeTableBranch7):Sql.Get(SqlKey.NoticeTableBranch8);
        if(key.Length==0) return prefix+extra;
        return prefix+Sql.Get(SqlKey.NoticeTableBranch9)+column+Sql.Get(SqlKey.NoticeTableBranch10)+k+Sql.Get(SqlKey.NoticeTableBranch11)+extra+Sql.Get(SqlKey.NoticeTableBranch12)+prefix+
            Sql.Get(SqlKey.NoticeTableBranch13)+column+Sql.Get(SqlKey.NoticeTableBranch14)+k+Sql.Get(SqlKey.NoticeTableBranch15)+column+Sql.Get(SqlKey.NoticeTableBranch16)+k+Sql.Get(SqlKey.NoticeTableBranch17)+extra;
    }
    private static string RangeSql(string local,string cloud)
    {
        var hits=string.Join(Sql.Get(SqlKey.NoticeTableRangeSql1),Branch(true,true,local,cloud),Branch(true,false,local,cloud),Branch(false,true,local,cloud),Branch(false,false,local,cloud));
        return Sql.Get(SqlKey.NoticeTableRangeSql2)+hits+Sql.Get(SqlKey.NoticeTableRangeSql3);
    }
    private static void Bind(SqliteCommand query,string prefix,string key)
    {
        if(key.Length==0) return;var (low,high)=PathNames.DescendantBounds(key);
        query.Parameters.AddWithValue(prefix+"low",low);query.Parameters.AddWithValue(prefix+"high",high);
        query.Parameters.AddWithValue(prefix+"parents",JsonSerializer.Serialize(PathNames.Ancestors(key).Append(key)));
    }
    /// <summary>Add or retire only the observed link identity; remove the one pair notice when its last member goes.</summary>
    public async Task<bool> UpdateLinkMemberAsync(SqliteConnection db,string pairId,NoticeItem member,bool present,CancellationToken ct)
    {
        using var tx=db.BeginTransaction(deferred:false);
        var key=PathNames.KeyFor(member.Side,member.Path);
        // Read the summary and the existing skip slot together; an identical observation needs no write or codec work.
        using var find=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableUpdateLinkMember1),("$p",pairId),("$kind",(int)NoticeKind.Info),("$cause",nameof(TextKey.InfoLinks)),("$skip",nameof(TextKey.SkipLink)),
            ("$present",present),("$side",(int)member.Side),("$key",key),("$path",member.Path),("$identity",member.Id));
        long? existing=null,previous=null;
        using(var row=await find.ExecuteReaderAsync(ct)) if(await row.ReadAsync(ct))
        {
            var sameMember=row.GetOrdinal("same_member");
            var summaryId=row.GetOrdinal("summary_id");
            var previousOwner=row.GetOrdinal("previous_owner");
            if(!row.IsDBNull(sameMember) && row.GetBoolean(sameMember)) return false;
            if(!row.IsDBNull(summaryId)) existing=row.GetInt64(summaryId);
            if(!row.IsDBNull(previousOwner)) previous=row.GetInt64(previousOwner);
        }
        if(existing is null && !present) return false;
        var subject=new IssueSubject([],null);
        var id=existing ?? await Slot(tx,pairId,new(0,NoticeKind.Info,TextKey.InfoLinks,"",subject,DateTimeOffset.UtcNow,false),ct);
        int affected;
        if(present) affected=await Member(tx,pairId,id,member,subject,TextKey.SkipLink,ct,previous!=null && previous!=id);
        else
        {
            using var remove=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableUpdateLinkMember2),("$id",id),("$s",(int)member.Side),("$k",key),("$identity",member.Id.Length==0?null:Encoding.IdFor(member.Side,member.Id)));
            affected=await remove.ExecuteNonQueryAsync(ct);
            using var empty=StorageCommand.Create(db,tx,Sql.Get(SqlKey.NoticeTableUpdateLinkMember3),"$id",id);await empty.ExecuteNonQueryAsync(ct);
        }
        tx.Commit(); return affected>0;
    }
}
