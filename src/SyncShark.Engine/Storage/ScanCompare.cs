using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Projection = (Massdex.SyncShark.Engine.Notice Notice, Massdex.SyncShark.Engine.NoticeItem? Member, bool Covered, bool Touched);

using NoticeMap = System.Collections.Generic.Dictionary<(string Key,bool Bound),System.Collections.Generic.List<(Massdex.SyncShark.Engine.Notice Notice,Massdex.SyncShark.Engine.NoticeItem? Member)>>;

namespace Massdex.SyncShark.Engine.Storage;
internal sealed class ScanCompare(ScanTable scan)
{
    private delegate (bool Covered,bool Touched) ProofFor(Side side,string path,bool descendants);
    private const string EntryFields = "x.path,x.is_folder,x.size,x.mtime,x.id,x.rev,x.hash,x.skip,x.shared";
    private const string SavedFields = "b.local_path,b.dropbox_key_delta,b.dropbox_path,b.is_folder,b.local_id,b.local_size,b.local_mtime,b.dropbox_id,b.dropbox_hash,b.local_published_journal,b.local_published_usn";
    private static bool InScope(Scope? scope, string local, string cloud) => scope == null || scope.LocalPath.Length==0 || scope.DropboxPath.Length==0 || PathNames.IsSelfOrUnder(local, scope.LocalPath) || PathNames.IsSelfOrUnder(cloud, scope.DropboxPath);
    // Pull at most one page from each indexed key range. A mapped cloud observation joins its saved
    // binding at the PC key; an unrelated cloud file reusing that PC spelling remains a separate free row.
    // One ordered cursor over physical ranges. The projected pages never rescan the entire pair.
    private static SqliteCommand KeyStream(SqliteConnection db,string pairId,Guid scanId,Scope? scope,string? after)
    {
        var ranges=new List<string>();
        void Add(string table,string column,string predicate,string? root,string? projection=null)
        {
            var select="SELECT "+(projection??column)+" AS k FROM "+table+" WHERE pair_id="+ScanTable.Pair+predicate;
            if(after!=null) select+=" AND "+(projection??column)+">$a";
            if(root==null)ranges.Add(select);
            else {ranges.Add(select+" AND "+column+"="+root);ranges.Add(select+" AND "+column+">="+root+"||'/' AND "+column+"<"+root+"||'0'");}
        }
        string? l=scope==null||scope.LocalPath.Length==0?null:"$l",d=scope==null||scope.DropboxPath.Length==0?null:"$d";
        Add("synced_entry INDEXED BY synced_local_key","pathkey(local_path)","",l);
        if(scope!=null && l!=d){Add("synced_entry INDEXED BY synced_local_key","pathkey(local_path)"," AND dropbox_key_delta IS NULL",d);Add("synced_entry INDEXED BY synced_dropbox_key","dropbox_key_delta"," AND dropbox_key_delta IS NOT NULL",d,"pathkey(local_path)");}
        Add("scan_state.scan INDEXED BY scan_own","pathkey(path)"," AND scan_id=$s AND side=0",l);
        Add("scan_state.scan INDEXED BY scan_own","pathkey(path)"," AND scan_id=$s AND side=1 AND key_delta IS NULL",d);
        Add("scan_state.scan INDEXED BY scan_key","key_delta"," AND scan_id=$s AND side=1 AND key_delta IS NOT NULL",d);
        Add("issue_member INDEXED BY issue_member_scope","path_key"," AND side=0",l);Add("issue_member INDEXED BY issue_member_scope","path_key"," AND side=1",d);
        Add("issue INDEXED BY issue_path_key","pathkey(path)","",l);if(scope!=null && l!=d)Add("issue INDEXED BY issue_path_key","pathkey(path)","",d);
        var sql=string.Join(" UNION ",ranges)+" ORDER BY k";
        return StorageCommand.Create(db,null,sql,("$p",pairId),("$s",scanId.ToByteArray()),("$a",after),("$l",scope==null?null:PathNames.Key(scope.LocalPath)),("$d",scope==null?null:PathNames.DropboxKey(scope.DropboxPath)));
    }
    private const string PagePrefix="""
        WITH keys AS MATERIALIZED (SELECT value k FROM json_each($keys)),
        bindings AS MATERIALIZED (SELECT k.k,b.rowid binding_id,b.dropbox_key dk FROM keys k LEFT JOIN synced_entry b INDEXED BY synced_local_key ON b.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND pathkey(b.local_path)=+k.k)
        """;
    private const string MemberRows="""
        ,footprints AS MATERIALIZED (
          SELECT b.k,m.issue_id,m.side,m.path_key,m.identity,m.subject_json,b.binding_id IS NOT NULL owner_bound FROM bindings b CROSS JOIN issue_member m INDEXED BY issue_member_scope WHERE m.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND m.side=0 AND m.path_key=b.k
          UNION ALL SELECT b.k,m.issue_id,m.side,m.path_key,m.identity,m.subject_json,b.binding_id IS NOT NULL owner_bound FROM bindings b CROSS JOIN issue_member m INDEXED BY issue_member_scope WHERE m.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND m.side=1 AND m.path_key=coalesce(b.dk,b.k)
            AND (b.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry d INDEXED BY synced_dropbox_key WHERE d.pair_id=m.pair_id AND d.dropbox_key_delta=m.path_key))
          UNION ALL SELECT b.k,m.issue_id,m.side,m.path_key,m.identity,m.subject_json,0 FROM bindings b CROSS JOIN issue_member m INDEXED BY issue_member_scope WHERE b.dk<>b.k AND m.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND m.side=1 AND m.path_key=b.k AND NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=m.pair_id AND d.dropbox_key_delta=m.path_key)
          UNION ALL SELECT k.k,i.issue_id,NULL,NULL,NULL,NULL,k.binding_id IS NOT NULL FROM bindings k CROSS JOIN issue i INDEXED BY issue_path_key WHERE i.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND pathkey(i.path)=+k.k AND NOT EXISTS(SELECT 1 FROM issue_member m WHERE m.issue_id=i.issue_id))
        SELECT m.k,i.issue_id,i.kind,i.cause,i.path,i.subject_json AS subject,i.at_ticks,i.dismissed,i.ignored,m.side AS member_side,m.subject_json AS member_subject,m.path_key,m.identity,m.owner_bound AS bound FROM footprints m CROSS JOIN issue i ON i.issue_id=m.issue_id ORDER BY m.issue_id
        """;
    private const string CompareRows = """
        ,groups AS (
          SELECT k,binding_id,dk FROM bindings
          UNION ALL SELECT k,NULL,NULL FROM bindings b WHERE dk<>k AND NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND d.dropbox_key_delta=b.k))
        SELECT g.k AS k,
        """ + SavedFields + ",x.side AS scan_side," + EntryFields + """
        ,(SELECT first_repair_done FROM pair WHERE pair_id=$p) AS first_done,g.binding_id IS NOT NULL AS bound FROM groups g
        LEFT JOIN synced_entry b ON b.rowid=g.binding_id
        LEFT JOIN scan_state.scan x ON x.rowid IN (
          SELECT rowid FROM scan_state.scan INDEXED BY scan_own WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=0 AND pathkey(path)=+g.k AND (g.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry l INDEXED BY synced_local_key WHERE l.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND pathkey(l.local_path)=+g.k))
          UNION ALL SELECT rowid FROM scan_state.scan INDEXED BY scan_own WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=1 AND key_delta IS NULL AND pathkey(path)=+coalesce(g.dk,g.k) AND (g.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND d.dropbox_key_delta=g.k))
          UNION ALL SELECT rowid FROM scan_state.scan INDEXED BY scan_key WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=1 AND key_delta=coalesce(g.dk,g.k) AND (g.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND d.dropbox_key_delta=g.k)))
        ORDER BY g.k,g.binding_id IS NOT NULL DESC,x.side,x.path
        """;
    /// <summary>Read pages of complete bindings, including notice-only rows and independently retained put-back notices.</summary>
    internal async IAsyncEnumerable<(Row Row, IReadOnlyList<Projection> Notices)> CompareAsync(SqliteConnection db, string pairId, Guid scanId, Scope? scope, Func<Side, string, (bool Covered, bool Touched)> evidence, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        string? after = null;
        while (true)
        {
            var (last, page) = await ReadPage(db, pairId, scanId, scope, evidence, after, pageSize, ct);
            if (last == null) yield break;
            after = last;
            foreach (var result in page) yield return result;
        }
    }

    private async Task<(string? Last,List<(Row,IReadOnlyList<Projection>)> Page)> ReadPage(SqliteConnection db,string pairId,Guid scanId,Scope? scope,Func<Side,string,(bool Covered,bool Touched)> evidence,string? after,int size,CancellationToken ct)
    {
        using var tx = db.BeginTransaction(deferred:true);
        var keys = new List<string>();
        using(var command=KeyStream(db,pairId,scanId,scope,after))
        {
            command.Transaction=tx;
            using var reader=await command.ExecuteReaderAsync(ct);
            var k=reader.GetOrdinal("k");
            while(keys.Count<size && await reader.ReadAsync(ct)) keys.Add(reader.GetString(k));
        }
        if(keys.Count==0) return (null,[]);
        var json=JsonSerializer.Serialize(keys);
        SqliteCommand Page(string sql) => StorageCommand.Create(db,tx,PagePrefix+sql,("$p",pairId),("$s",scanId.ToByteArray()),("$keys",json));
        using var members=Page(MemberRows);
        var notices=await ReadNotices(members,ct);
        using var entries=Page(CompareRows);
        var rows=await CompareRowsAsync(entries,scope,notices,(side,path,folder)=>Proof(pairId,scanId,evidence,side,path,folder),ct);
        return (keys[^1],rows);
    }

    private (bool Covered,bool Touched) Proof(string pairId,Guid scanId,Func<Side,string,(bool Covered,bool Touched)> evidence,Side side,string path,bool folder)
    {
        var result=evidence(side,path);
        return (result.Covered,result.Touched || scan.Touched(pairId,scanId,side,path,folder));
    }
    private static async Task<NoticeMap> ReadNotices(SqliteCommand command,CancellationToken ct)
    {
        var notices=new NoticeMap(); Notice? notice=null;
        using var rd=await command.ExecuteReaderAsync(ct);
        int key=rd.GetOrdinal("k"), bound=rd.GetOrdinal("bound"), id=rd.GetOrdinal("issue_id"), kind=rd.GetOrdinal("kind"), cause=rd.GetOrdinal("cause"), path=rd.GetOrdinal("path"), subject=rd.GetOrdinal("subject"), at=rd.GetOrdinal("at_ticks"), dismissed=rd.GetOrdinal("dismissed"), ignored=rd.GetOrdinal("ignored");
        int memberSide=rd.GetOrdinal("member_side"), memberSubject=rd.GetOrdinal("member_subject"), memberKey=rd.GetOrdinal("path_key"), memberId=rd.GetOrdinal("identity");
        while(await rd.ReadAsync(ct))
        {
            var issue=rd.GetInt64(id);
            if(notice==null || notice.Id!=issue)
                notice=new Notice(issue,(NoticeKind)rd.GetInt32(kind),Enum.Parse<TextKey>(rd.GetString(cause)),rd.GetString(path),Encoding.DecodeSubject(rd.GetString(subject)),new(rd.GetInt64(at),TimeSpan.Zero),rd.GetBoolean(dismissed),rd.GetBoolean(ignored));
            var owner=(rd.GetString(key),rd.GetBoolean(bound));
            if(!notices.TryGetValue(owner,out var list)) notices[owner]=list=[];
            var member=rd.IsDBNull(memberSubject)?null:ReadMember(notice,(Side)rd.GetInt32(memberSide),rd.GetString(memberKey),rd.IsDBNull(memberId)?string.Empty:rd.GetString(memberId),rd.GetString(memberSubject));
            list.Add((notice,member));
        }
        return notices;
    }

    private static NoticeItem ReadMember(Notice notice,Side side,string key,string id,string json)
    {
        bool Matches(NoticeItem item)=>item.Side==side && item.Id==id && PathNames.KeyFor(side,item.Path)==key;
        return notice.Subject.Items.FirstOrDefault(Matches) ?? (notice.Cause==TextKey.InfoLinks ? Encoding.DecodeMemberSubject(json).Items.FirstOrDefault(Matches) : null) ?? new NoticeItem(side,key,id);
    }

    private static async Task<List<(Row,IReadOnlyList<Projection>)>> CompareRowsAsync(SqliteCommand command,Scope? scope,NoticeMap notices,ProofFor proof,CancellationToken ct)
    {
        using var rd=await command.ExecuteReaderAsync(ct);
        int key=rd.GetOrdinal("k"), bound=rd.GetOrdinal("bound"), local=rd.GetOrdinal("local_path"), first=rd.GetOrdinal("first_done"), side=rd.GetOrdinal("scan_side"), path=rd.GetOrdinal("path");
        var page=new List<(Row,IReadOnlyList<Projection>)>();
        (string Key,bool Bound)? group=null;
        SyncedEntry? saved=null; Entry? pc=null,cloud=null; List<Entry> extras=[]; bool firstRun=false;
        while(await rd.ReadAsync(ct))
        {
            var owner=(rd.GetString(key),rd.GetBoolean(bound));
            if(group!=owner)
            {
                Finish(); group=owner;
                saved=rd.IsDBNull(local)?null:Encoding.ReadSynced(rd,owner.Item1,local);
                pc=cloud=null; extras=[]; firstRun=!rd.GetBoolean(first);
            }
            if(rd.IsDBNull(side)) continue;
            var entry=Encoding.ReadEntry(rd,path,false);
            if((Side)rd.GetInt32(side)==Side.Local) pc=entry;
            else if(cloud==null) cloud=entry;
            else extras.Add(entry);
        }
        Finish(); return page;
        void Finish()
        {
            if(group is {} owner && CompareRow(owner,pc,cloud,saved,extras,firstRun,scope,notices,proof) is {} row) page.Add(row);
        }
    }

    private static (Row,IReadOnlyList<Projection>)? CompareRow((string Key,bool Bound) owner,Entry? pc,Entry? cloud,SyncedEntry? saved,List<Entry> extras,bool firstRun,Scope? scope,NoticeMap notices,ProofFor proof)
    {
        notices.TryGetValue(owner,out var list);
        if(pc==null && cloud==null && saved==null && list==null) return null;
        var lp=pc?.Path ?? saved?.LocalPath ?? owner.Key;
        var dp=cloud?.Path ?? saved?.DropboxPath ?? owner.Key;
        if(!InScope(scope,lp,dp)) return null;
        var folder=pc?.IsFolder==true || cloud?.IsFolder==true || saved?.IsFolder==true;
        var l=proof(Side.Local,lp,folder); var d=proof(Side.Dropbox,dp,folder);
        var projection=new List<Projection>();
        if(list!=null) foreach(var (n,m) in list)
        {
            var p=m==null?(l.Covered && d.Covered,l.Touched || d.Touched):proof(m.Side,m.Path,folder);
            projection.Add((n,m,p.Item1,p.Item2));
        }
        return (new Row(owner.Key,pc,cloud,saved,extras,null,RowContext.Neutral with { FirstRun=firstRun, CompleteInventory=l.Covered && d.Covered && !l.Touched && !d.Touched }),projection);
    }

    /// <summary>Match unchanged physical names first; stream unmatched identities ordered by the binding's shallowest move.</summary>
    internal static async IAsyncEnumerable<RenameCandidate> RenamesAsync(SqliteConnection db, string pairId, Guid scanId, Scope? scope, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        db.CreateFunction<string, int>("plandepth", PathNames.Depth, isDeterministic: true);
        db.CreateFunction<string, string, bool>("scansubtree", PathNames.IsSelfOrUnder, isDeterministic: true);
        string Candidates(Side side)
        {
            bool local = side == Side.Local;
            var index = local ? "synced_local_id" : "synced_dropbox_id";
            var id = local ? "local_id" : "dropbox_id";
            var path = local ? "local_path" : "dropbox_path";
            string ExactSaved(string idx,string match)=>"SELECT 1 FROM synced_entry same INDEXED BY "+idx+" WHERE same.pair_id=x.pair_id AND "+match+" AND same."+path+"=x.path AND same."+id+"=x.id AND same.is_folder=x.is_folder";
            var exact=ExactSaved("synced_local_key","pathkey(same.local_path)=+pathkey(x.path)"+(local?"":" AND same.dropbox_key_delta IS NULL"));
            if(!local)exact+=" UNION ALL "+ExactSaved("synced_dropbox_key","same.dropbox_key_delta=x.key");
            // OFFSET 0 prevents SQLite flattening the exact-name filter into the identity join.
            // Unchanged aliases are discarded before there is a candidate fan-out.
            var unmatched="SELECT * FROM scan_state.scan x WHERE x.pair_id="+ScanTable.Pair+" AND x.scan_id=$s AND x.side="+(int)side+" AND x.id<>'' AND NOT EXISTS("+exact+")";
            return "SELECT x.side,"+EntryFields+","+SavedFields+",pathkey(b.local_path) lk FROM ("+unmatched+" LIMIT -1 OFFSET 0) x CROSS JOIN synced_entry b INDEXED BY "+index+
                " WHERE b.pair_id=x.pair_id AND b."+id+"=x.id AND b.is_folder=x.is_folder AND x.path<>b."+path+
                " AND NOT EXISTS(SELECT 1 FROM scan_state.scan same INDEXED BY scan_own WHERE same.pair_id=x.pair_id AND same.scan_id=x.scan_id AND same.side=x.side AND pathkey(same.path)=+pathkey(b."+path+") AND same.path=b."+path+" AND same.id=x.id AND same.is_folder=x.is_folder)"+
                " AND ($l IS NULL OR scansubtree(b.local_path,$l) OR scansubtree(b.dropbox_path,$d) OR scansubtree(x.path,"+(local?"$l":"$d")+"))";
        }
        var sql = "WITH candidates AS (" + Candidates(Side.Local) + " UNION ALL " + Candidates(Side.Dropbox) + "), ordered AS (SELECT *,min(plandepth(path)) OVER(PARTITION BY lk) depth FROM candidates) SELECT * FROM ordered ORDER BY depth,lk,side,path";
        using (var temp = StorageCommand.Create(db,null,"PRAGMA temp_store=FILE"))
            await temp.ExecuteNonQueryAsync(ct);
        using (var fill = StorageCommand.Create(db, null, "CREATE TEMP TABLE rename_order AS " + sql, ("$p",pairId),("$s",scanId.ToByteArray()),("$l",scope?.LocalPath),("$d",scope?.DropboxPath)))
            await fill.ExecuteNonQueryAsync(ct);
        try
        {
            long after=0;
            while (true)
            {
                var (page,last)=await ReadRenamePage(db,after,pageSize,ct);
                after=last;
                if(page.Count==0) yield break;
                foreach(var candidate in page) yield return candidate;
            }
        }
        finally { using var drop=StorageCommand.Create(db,null,"DROP TABLE temp.rename_order"); drop.ExecuteNonQuery(); }
    }

    private static async Task<(List<RenameCandidate> Page,long Last)> ReadRenamePage(SqliteConnection db,long after,int size,CancellationToken ct)
    {
        var page=new List<RenameCandidate>();
        using var cmd=StorageCommand.Create(db,null,"SELECT *,rowid AS r FROM temp.rename_order WHERE rowid>$after ORDER BY rowid LIMIT $size",("$after",after),("$size",size));
        using var reader=await cmd.ExecuteReaderAsync(ct);
        int row=reader.GetOrdinal("r"), side=reader.GetOrdinal("side"), path=reader.GetOrdinal("path"), key=reader.GetOrdinal("lk"), saved=reader.GetOrdinal("local_path");
        while (await reader.ReadAsync(ct))
        {
            after=reader.GetInt64(row);
            page.Add(new((Side)reader.GetInt32(side),Encoding.ReadEntry(reader,path,false),Encoding.ReadSynced(reader,reader.GetString(key),saved)));
        }
        return (page,after);
    }
}
