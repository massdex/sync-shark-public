using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Projection = (Massdex.SyncShark.Engine.Notice Notice, Massdex.SyncShark.Engine.NoticeItem? Member, bool Covered, bool Touched);

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Stores each Repair listing and compares its observed endpoints with saved bindings.</summary>
public sealed class ScanTable
{
    private readonly Dictionary<(string Pair,Guid Scan),(SortedSet<string> Exact,PrefixSet Trees)[]> active=new();

    /// <summary>Begin tracking commits before taking this acquisition's listing boundary.</summary>
    public void Begin(string pairId,Guid scanId)
    {
        lock(active)if(!active.ContainsKey((pairId,scanId)))active.Add((pairId,scanId),[(new(StringComparer.Ordinal),new()),(new(StringComparer.Ordinal),new())]);
    }
    /// <summary>Mark a committed physical endpoint on every active acquisition of its pair.</summary>
    public void Touch(string pairId,Side side,string path,bool subtree)
    {
        var key=PathNames.KeyFor(side,path);
        lock(active)foreach(var (owner,marks) in active){if(owner.Pair!=pairId)continue;marks[(int)side].Exact.Add(key);if(subtree)marks[(int)side].Trees.Add(key);}
    }
    /// <summary>Test exact and covering commits, plus descendant commits when a folder is being settled.</summary>
    public bool Touched(string pairId,Guid scanId,Side side,string path,bool descendants)
    {
        var key=PathNames.KeyFor(side,path);lock(active){if(!active.TryGetValue((pairId,scanId),out var marks))return true;
            var (exact,trees)=marks[(int)side];return exact.Contains(key)||trees.ResponsibilityOverlaps(key,false)||descendants&&(key.Length==0?exact.Count!=0:PathNames.HasDescendantKey(exact,key));}
    }
    private const string Pair="(SELECT id FROM pair WHERE pair_id=$p)";
    private const string Slice="pair_id="+Pair+" AND scan_id=$s";
    private const string Seen="SELECT * FROM scan_state.scan WHERE "+Slice;
    private const string Saved="SELECT *,pathkey(local_path) lk FROM synced_entry WHERE pair_id="+Pair;
    private const string EntryFields="x.path,x.is_folder,x.size,x.mtime,x.id,x.rev,x.hash,x.skip,x.shared";
    private const string SavedFields="b.local_path,b.dropbox_key,b.dropbox_path,b.is_folder,b.local_id,b.local_size,b.local_mtime,b.dropbox_id,b.dropbox_hash,b.local_published_journal,b.local_published_usn";

    /// <summary>Append ordered events in bounded transactions; capture the history cursor only after successful enumeration.</summary>
    public async Task AddScanAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchRows);
        if(terminalHistoryCursor!=null && side!=Side.Dropbox)throw new ArgumentException(nameof(side));
        Begin(pairId,scanId);
        if(terminalHistoryCursor!=null)DeletedNames.Forget(db,pairId,scanId);
        var token=DeletedNames.Token(pairId,scanId);var batch=new List<Listed>();
        await foreach(var row in rows.WithCancellation(ct)){batch.Add(row);if(batch.Count==batchRows)Flush();}Flush();
        if(terminalHistoryCursor!=null)DeletedNames.Completed(db,pairId,scanId,terminalHistoryCursor());
        void Flush()
        {
            if(batch.Count==0)return;using var tx=db.BeginTransaction();
            using var clear=StorageCommand.Create(db,tx,"DELETE FROM scan_state.scan WHERE "+Slice+" AND side=$side AND ((pathkey(path)=$k AND $gone) OR (pathkey(path)>=$lo AND pathkey(path)<$hi))",("$p",pairId),("$s",scanId.ToByteArray()),("$side",(int)side),("$k",""),("$gone",false),("$lo",""),("$hi",""));
            using var put=StorageCommand.Create(db,tx,"INSERT INTO scan_state.scan(pair_id,scan_id,side,key_delta,path,is_folder,size,mtime,id,rev,hash,skip,shared) VALUES("+Pair+",$s,$side,$delta,$path,$folder,$size,$time,$id,$rev,$hash,$skip,$shared) ON CONFLICT DO UPDATE SET key_delta=excluded.key_delta,path=excluded.path,is_folder=excluded.is_folder,size=excluded.size,mtime=excluded.mtime,id=excluded.id,rev=excluded.rev,hash=excluded.hash,skip=excluded.skip,shared=excluded.shared",("$p",pairId),("$s",scanId.ToByteArray()),("$side",(int)side));
            foreach(var name in new[]{"$delta","$path","$folder","$size","$time","$id","$rev","$hash","$skip","$shared"})put.Parameters.AddWithValue(name,DBNull.Value);
            foreach(var listed in batch)
            {
                ct.ThrowIfCancellationRequested();var key=PathNames.Key(listed.Path);var entry=listed.Entry;
                if(entry?.IsFolder!=true){var (lo,hi)=PathNames.DescendantBounds(key);clear.Parameters["$k"].Value=key;clear.Parameters["$lo"].Value=lo;clear.Parameters["$hi"].Value=hi;clear.Parameters["$gone"].Value=entry==null;clear.ExecuteNonQuery();}
                if(terminalHistoryCursor!=null){using var history=StorageCommand.Create(db,tx,entry==null?"INSERT OR IGNORE INTO scan_state.deleted_name VALUES($s,$k)":"DELETE FROM scan_state.deleted_name WHERE scan_id=$s AND path_key=$k",("$s",token),("$k",key));history.ExecuteNonQuery();}
                if(entry==null)continue;
                var join=PathNames.KeyFor(side,listed.Path,key);var args=put.Parameters;args["$delta"].Value=join==key?DBNull.Value:join;args["$path"].Value=listed.Path;args["$folder"].Value=entry.IsFolder;args["$size"].Value=entry.Size;args["$time"].Value=entry.Modified.UtcTicks;
                args["$id"].Value=side==Side.Local && long.TryParse(entry.Id,NumberStyles.Integer,CultureInfo.InvariantCulture,out var id)&&id.ToString(CultureInfo.InvariantCulture)==entry.Id?id:entry.Id;
                args["$rev"].Value=entry.Revision;args["$hash"].Value=entry.ContentHash.Length is 0 or 64 && entry.ContentHash.All(c=>c is >= '0' and <= '9' or >= 'a' and <= 'f')?Convert.FromHexString(entry.ContentHash):entry.ContentHash;args["$skip"].Value=(object?)entry.Skip??DBNull.Value;args["$shared"].Value=entry.SharedRoot;put.ExecuteNonQuery();
            }
            tx.Commit();batch.Clear();
        }
    }
    /// <summary>Clear only this pair's acquisition rows, scratch names and pending terminal cursor.</summary>
    public async Task ClearScanAsync(SqliteConnection db,string pairId,Guid scanId,CancellationToken ct)
    {
        using var tx=db.BeginTransaction();using var cmd=StorageCommand.Create(db,tx,"DELETE FROM scan_state.scan WHERE "+Slice+"; DELETE FROM scan_state.deleted_name WHERE scan_id=$n",("$p",pairId),("$s",scanId.ToByteArray()),("$n",DeletedNames.Token(pairId,scanId)));await cmd.ExecuteNonQueryAsync(ct);tx.Commit();DeletedNames.Forget(db,pairId,scanId);lock(active)active.Remove((pairId,scanId));
    }
    private static string Hash(SqliteDataReader r,int i)=>r.GetValue(i) is byte[] b?Convert.ToHexString(b).ToLowerInvariant():r.GetString(i);
    private static Entry EntryAt(SqliteDataReader r,int i)=>new(r.GetString(i),r.GetBoolean(i+1),r.GetInt64(i+2),new DateTimeOffset(r.GetInt64(i+3),TimeSpan.Zero),r.GetString(i+4),r.GetString(i+5),Hash(r,i+6),r.IsDBNull(i+7)?null:r.GetString(i+7),r.GetBoolean(i+8));
    private static SyncedEntry BindingAt(SqliteDataReader r,int i)=>new SyncedEntry(PathNames.Key(r.GetString(i)),r.GetString(i),r.GetString(i+1),r.GetString(i+2),r.GetBoolean(i+3),r.GetString(i+4),r.GetInt64(i+5),r.GetInt64(i+6),r.GetString(i+7),Hash(r,i+8)){LocalPublished=r.IsDBNull(i+9)||r.IsDBNull(i+10)?null:new(unchecked((ulong)r.GetInt64(i+9)),unchecked((ulong)r.GetInt64(i+10)))};
    private static bool InScope(Scope? scope,string local,string cloud)=>scope==null || PathNames.IsSelfOrUnder(local,scope.LocalPath)||PathNames.IsSelfOrUnder(cloud,scope.DropboxPath);
    // Pull at most one page from each indexed key range. A mapped cloud observation joins its saved
    // binding at the PC key; an unrelated cloud file reusing that PC spelling remains a separate free row.
    private const string ComparePrefix = "WITH saved AS NOT MATERIALIZED ("+Saved+"), seen AS NOT MATERIALIZED ("+Seen+"),"+"""
        members AS NOT MATERIALIZED (
          SELECT i.*,m.side,m.path_key,m.identity,m.subject_json member_json,coalesce(b.lk,m.path_key,pathkey(i.path)) k
          FROM issue i LEFT JOIN issue_member m ON m.issue_id=i.issue_id LEFT JOIN saved b ON m.side=1 AND b.dropbox_key_delta=m.path_key
          WHERE i.pair_id=(SELECT id FROM pair WHERE pair_id=$p)),
        keys AS (
          SELECT k FROM (SELECT lk k FROM saved WHERE lk>$after ORDER BY lk LIMIT $take)
          UNION SELECT k FROM (SELECT pathkey(path) k FROM seen WHERE side=0 AND pathkey(path)>$after ORDER BY pathkey(path) LIMIT $take)
          UNION SELECT k FROM (SELECT pathkey(path) k FROM seen WHERE side=1 AND key_delta IS NULL AND pathkey(path)>$after
            AND NOT EXISTS(SELECT 1 FROM saved b WHERE b.dropbox_key_delta=pathkey(seen.path)) ORDER BY pathkey(path) LIMIT $take)
          UNION SELECT k FROM (SELECT DISTINCT key_delta k FROM seen WHERE side=1 AND key_delta>$after
            AND NOT EXISTS(SELECT 1 FROM saved b WHERE b.dropbox_key_delta=seen.key_delta) ORDER BY key_delta LIMIT $take)
          UNION SELECT k FROM (SELECT DISTINCT k FROM members WHERE k>$after ORDER BY k LIMIT $take) ORDER BY k LIMIT $take)
        """;
    private const string CompareRows = """
        ,groups AS (
          SELECT keys.k,1 bound FROM keys JOIN saved b ON b.lk=keys.k
          UNION ALL SELECT k,0 FROM keys WHERE NOT EXISTS(SELECT 1 FROM saved b WHERE b.lk=keys.k)
          OR (EXISTS(SELECT 1 FROM saved b WHERE b.lk=keys.k AND b.dropbox_key<>keys.k)
          AND NOT EXISTS(SELECT 1 FROM saved b WHERE b.dropbox_key_delta=keys.k)
          AND EXISTS(SELECT 1 FROM seen x WHERE x.side=1 AND x.key=keys.k)))
        SELECT g.k,
        """+SavedFields+",x.side,"+EntryFields+"""
        ,(SELECT first_repair_done FROM pair WHERE pair_id=$p),g.bound FROM groups g
        LEFT JOIN saved b ON g.bound=1 AND b.lk=g.k
        LEFT JOIN scan_state.scan x ON x.rowid IN (
          SELECT rowid FROM scan_state.scan WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=0 AND pathkey(path)=g.k
            AND (g.bound=1 OR NOT EXISTS(SELECT 1 FROM saved l WHERE l.lk=g.k))
          UNION ALL SELECT rowid FROM scan_state.scan WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=1 AND key_delta IS NULL AND pathkey(path)=coalesce(b.dropbox_key,g.k)
            AND (g.bound=1 OR NOT EXISTS(SELECT 1 FROM saved d WHERE d.dropbox_key_delta=g.k))
          UNION ALL SELECT rowid FROM scan_state.scan WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=1 AND key_delta=coalesce(b.dropbox_key,g.k)
            AND (g.bound=1 OR NOT EXISTS(SELECT 1 FROM saved d WHERE d.dropbox_key_delta=g.k)))
        ORDER BY g.k,g.bound DESC,x.side,x.path
        """;

    /// <summary>Read pages of complete bindings, including notice-only rows and independently retained put-back notices.</summary>
    public async IAsyncEnumerable<(Row Row,IReadOnlyList<Projection> Notices)> CompareAsync(SqliteConnection db,string pairId,Guid scanId,Scope? scope,Func<Side,string,(bool Covered,bool Touched)> evidence,int pageSize,[EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);string? after=null;
        (bool Covered,bool Touched) Proof(Side side,string path,bool descendants){var result=evidence(side,path);return(result.Covered,result.Touched||Touched(pairId,scanId,side,path,descendants));}
        while(true)
        {
            var page=new List<(Row,IReadOnlyList<Projection>)>();using(var tx=db.BeginTransaction(deferred:true))
            {
                SqliteCommand Page(string sql)=>StorageCommand.Create(db,tx,(after==null?ComparePrefix.Replace(">$after",">=''",StringComparison.Ordinal):ComparePrefix)+sql,("$p",pairId),("$s",scanId.ToByteArray()),("$after",after),("$take",pageSize));
                var notices=new Dictionary<string,List<(Notice Notice,NoticeItem? Member)>>();
                using(var cmd=Page("SELECT m.k,m.issue_id,m.kind,m.cause,m.path,m.subject_json,m.at_ticks,m.dismissed,m.ignored,m.side,m.member_json,m.path_key FROM members m JOIN keys ON keys.k=m.k ORDER BY m.k,m.issue_id"))
                using(var rd=await cmd.ExecuteReaderAsync(ct))while(await rd.ReadAsync(ct))
                {
                    var key=rd.GetString(0);var subject=JsonSerializer.Deserialize<IssueSubject>(rd.GetString(5))??throw new InvalidDataException();var notice=new Notice(rd.GetInt64(1),(NoticeKind)rd.GetInt32(2),Enum.Parse<TextKey>(rd.GetString(3)),rd.GetString(4),subject,new(rd.GetInt64(6),TimeSpan.Zero),rd.GetBoolean(7),rd.GetBoolean(8));NoticeItem? member=null;
                    if(!rd.IsDBNull(10)){using var json=JsonDocument.Parse(rd.GetString(10));var value=json.RootElement.GetProperty("Value");var side=(Side)rd.GetInt32(9);var memberKey=rd.GetString(11);foreach(var item in value.GetProperty("Items").EnumerateArray()){var path=item.GetProperty("Path").GetString()??throw new InvalidDataException();if(PathNames.KeyFor(side,path)==memberKey){member=new(side,path,item.GetProperty("Id").GetString()??string.Empty);break;}}if(member==null)throw new InvalidDataException("Notice member does not match its stored footprint");}
                    if(!notices.TryGetValue(key,out var list))notices[key]=list=[];list.Add((notice,member));
                }
                string? last=null;bool bound=false;Entry? local=null,cloud=null;SyncedEntry? saved=null;List<Entry> extra=[];bool first=false;
                using(var cmd=Page(CompareRows))
                using(var rd=await cmd.ExecuteReaderAsync(ct))while(await rd.ReadAsync(ct))
                {
                    var key=rd.GetString(0);if(last!=key||bound!=rd.GetBoolean(23)){Finish();last=key;bound=rd.GetBoolean(23);saved=rd.IsDBNull(1)?null:BindingAt(rd,1);local=null;cloud=null;extra=[];first=!rd.GetBoolean(22);}
                    if(!rd.IsDBNull(12)){var entry=EntryAt(rd,13);if(rd.GetInt32(12)==0)local=entry;else if(cloud==null)cloud=entry;else extra.Add(entry);}
                }
                Finish();if(last==null)yield break;after=last;tx.Commit();
                void Finish()
                {
                    if(last==null)return;var lp=local?.Path??saved?.LocalPath??last;var dp=cloud?.Path??saved?.DropboxPath??last;if(!InScope(scope,lp,dp))return;var folder=local?.IsFolder==true||cloud?.IsFolder==true||saved?.IsFolder==true;var l=Proof(Side.Local,lp,folder);var d=Proof(Side.Dropbox,dp,folder);
                    var projection=new List<Projection>();if(notices.TryGetValue(last,out var list))foreach(var(n,m) in list){var proof=m==null?(l.Covered&&d.Covered,l.Touched||d.Touched):Proof(m.Side,m.Path,folder);projection.Add((n,m,proof.Item1,proof.Item2));}
                    page.Add((new Row(last,local,cloud,saved,extra,null,RowContext.Neutral with{FirstRun=first,CompleteInventory=l.Covered&&d.Covered&&!l.Touched&&!d.Touched}),projection));
                }
            }
            foreach(var result in page)yield return result;
        }
    }

    /// <summary>Match unchanged physical names first; stream unmatched identities ordered by the binding's shallowest move.</summary>
    public async IAsyncEnumerable<RenameCandidate> RenamesAsync(SqliteConnection db,string pairId,Guid scanId,Scope? scope,int pageSize,[EnumeratorCancellation]CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        db.CreateFunction<string,int>("plandepth",PathNames.Depth,isDeterministic:true);
        db.CreateFunction<string,string,bool>("scansubtree",PathNames.IsSelfOrUnder,isDeterministic:true);
        const string sql="WITH observed AS ("+Seen+"), saved AS ("+Saved+"), endpoints AS (SELECT *,0 side,local_id id,local_path path FROM saved UNION ALL SELECT *,1,dropbox_id,dropbox_path FROM saved), unchanged AS (SELECT x.side,x.path FROM observed x JOIN endpoints b ON x.side=b.side AND x.path=b.path AND x.id=b.id AND x.is_folder=b.is_folder), candidates AS (SELECT x.side,"+EntryFields+","+SavedFields+",b.lk,min(plandepth(x.path)) OVER(PARTITION BY b.lk) depth FROM observed x JOIN endpoints b ON b.side=x.side AND b.id=x.id AND x.is_folder=b.is_folder WHERE x.id<>'' AND NOT EXISTS(SELECT 1 FROM unchanged u WHERE u.side=x.side AND u.path IN(x.path,b.path)) AND ($l IS NULL OR scansubtree(b.local_path,$l) OR scansubtree(b.dropbox_path,$d) OR scansubtree(x.path,CASE WHEN x.side=0 THEN $l ELSE $d END))) SELECT * FROM candidates ORDER BY depth,lk,side,path";
        using var cmd=StorageCommand.Create(db,null,sql,("$p",pairId),("$s",scanId.ToByteArray()),("$l",scope?.LocalPath),("$d",scope?.DropboxPath));using var reader=await cmd.ExecuteReaderAsync(ct);while(await reader.ReadAsync(ct))yield return new((Side)reader.GetInt32(0),EntryAt(reader,1),BindingAt(reader,10));
    }
}
