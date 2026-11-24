using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Projection = (Massdex.SyncShark.Engine.Notice Notice, Massdex.SyncShark.Engine.NoticeItem? Member, bool Covered, bool Touched);

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Stores each Repair listing and compares its observed endpoints with saved bindings.</summary>
public sealed class ScanTable
{
    /// <summary>The deletion history and completed cursors belonging to this store, across its read and write connections.</summary>
    public DeletedNames History { get; } = new();

    private readonly Dictionary<(string Pair, Guid Scan), (SortedSet<string> Exact, PrefixSet Trees)[]> active = new();
    /// <summary>Begin tracking commits before taking this acquisition's listing boundary.</summary>
    public void Begin(string pairId, Guid scanId)
    {
        lock (active)
            if (!active.ContainsKey((pairId, scanId)))
                active.Add((pairId, scanId), [(new(StringComparer.Ordinal), new()), (new(StringComparer.Ordinal), new())]);
    }

    /// <summary>Whether a committed notice replacement needs its original footprints for an active scan.</summary>
    public bool HasActive(string pairId)
    {
        lock (active)
            return active.Keys.Any(key => key.Pair == pairId);
    }

    /// <summary>Mark an observed change during acquisition. Key is an already-canonical side key, so this must not call PathNames.KeyFor again.</summary>
    public void Touch(string pairId, Side side, string key, bool subtree)
    {
        lock (active)
            foreach (var(owner, marks)in active)
            {
                if (owner.Pair != pairId)
                    continue;
                marks[(int)side].Exact.Add(key);
                if (subtree)
                    marks[(int)side].Trees.Add(key);
            }
    }

    /// <summary>Test exact and covering commits, plus descendant commits when a folder is being settled.</summary>
    public bool Touched(string pairId, Guid scanId, Side side, string path, bool descendants)
    {
        var key = PathNames.KeyFor(side, path);
        lock (active)
        {
            if (!active.TryGetValue((pairId, scanId), out var marks))
                return true;
            var(exact, trees) = marks[(int)side];
            return exact.Contains(key) || trees.ResponsibilityOverlaps(key, false) || descendants && (key.Length == 0 ? exact.Count != 0 : PathNames.HasDescendantKey(exact, key));
        }
    }

    private const string Pair = "(SELECT id FROM pair WHERE pair_id=$p)";
    private const string Slice = "pair_id=" + Pair + " AND scan_id=$s";
    private const string EntryFields = "x.path,x.is_folder,x.size,x.mtime,x.id,x.rev,x.hash,x.skip,x.shared";
    private const string SavedFields = "b.local_path,b.dropbox_key_delta,b.dropbox_path,b.is_folder,b.local_id,b.local_size,b.local_mtime,b.dropbox_id,b.dropbox_hash,b.local_published_journal,b.local_published_usn";
    /// <summary>Append ordered events in bounded transactions; capture the history cursor only after successful enumeration.</summary>
    public Task AddScanAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,CancellationToken ct) => IngestAsync(db,pairId,scanId,side,rows,terminalHistoryCursor,batchRows,null,ct);
    /// <summary>Serialize each scratch write with the store, without holding its gate while waiting for listing events.</summary>
    public Task AddScanAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,SemaphoreSlim writerGate,CancellationToken ct) => IngestAsync(db,pairId,scanId,side,rows,terminalHistoryCursor,batchRows,writerGate,ct);
    private async Task IngestAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,SemaphoreSlim? gate,CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchRows);
        if(terminalHistoryCursor!=null && side!=Side.Dropbox)throw new ArgumentException(nameof(side));
        Begin(pairId,scanId);string? origin=null;
        if(terminalHistoryCursor!=null)await WriteAsync(()=>{
            History.Forget(pairId,scanId);origin=DeletedNames.State(db,pairId).Origin;
            using var reset=StorageCommand.Create(db,null,"DELETE FROM scan_state.deleted_name WHERE scan_id=$n","$n",DeletedNames.Token(pairId,scanId));reset.ExecuteNonQuery();
        });
        async Task WriteAsync(Action write)
        {
            if(gate!=null)await gate.WaitAsync(ct);try{write();}finally{gate?.Release();}
        }
        var token = DeletedNames.Token(pairId, scanId);
        var batch = new List<Listed>();
        await foreach (var row in rows.WithCancellation(ct))
        {
            batch.Add(row);
            if (batch.Count == batchRows)
                await WriteAsync(Flush);
        }

        if(batch.Count>0)await WriteAsync(Flush);
        if (terminalHistoryCursor != null)
            await WriteAsync(()=>History.Completed(pairId, scanId, terminalHistoryCursor(), origin!));
        void Flush()
        {
            if (batch.Count == 0)
                return;
            using var tx = db.BeginTransaction();
            const string deletePrefix="DELETE FROM scan_state.scan INDEXED BY scan_own WHERE "+Slice+" AND side=$side AND ";
            using var exact=StorageCommand.Create(db,tx,deletePrefix+"pathkey(path)=$k",("$p",pairId),("$s",scanId.ToByteArray()),("$side",(int)side),("$k",""));
            using var clear=StorageCommand.Create(db,tx,deletePrefix+"pathkey(path)>=$lo AND pathkey(path)<$hi",("$p",pairId),("$s",scanId.ToByteArray()),("$side",(int)side),("$lo",""),("$hi",""));
            using var put = StorageCommand.Create(db, tx, "INSERT INTO scan_state.scan(pair_id,scan_id,side,key_delta,path,is_folder,size,mtime,id,rev,hash,skip,shared) VALUES(" + Pair + ",$s,$side,$delta,$path,$folder,$size,$time,$id,$rev,$hash,$skip,$shared) ON CONFLICT DO UPDATE SET key_delta=excluded.key_delta,path=excluded.path,is_folder=excluded.is_folder,size=excluded.size,mtime=excluded.mtime,id=excluded.id,rev=excluded.rev,hash=excluded.hash,skip=excluded.skip,shared=excluded.shared", ("$p", pairId), ("$s", scanId.ToByteArray()), ("$side", (int)side));
            foreach (var name in new[]
            {
                "$delta",
                "$path",
                "$folder",
                "$size",
                "$time",
                "$id",
                "$rev",
                "$hash",
                "$skip",
                "$shared"
            }

            )
                put.Parameters.AddWithValue(name, DBNull.Value);
            foreach (var listed in batch)
            {
                ct.ThrowIfCancellationRequested();
                var key = PathNames.Key(listed.Path);
                var entry = listed.Entry;
                if (entry?.IsFolder != true)
                {
                    var(lo, hi) = PathNames.DescendantBounds(key);
                    if(entry==null){exact.Parameters["$k"].Value=key;exact.ExecuteNonQuery();}
                    clear.Parameters["$lo"].Value = lo;
                    clear.Parameters["$hi"].Value = hi;
                    clear.ExecuteNonQuery();
                }

                if (terminalHistoryCursor != null)
                {
                    using var history = StorageCommand.Create(db, tx, entry == null ? "INSERT OR IGNORE INTO scan_state.deleted_name VALUES($s,$k)" : "DELETE FROM scan_state.deleted_name WHERE scan_id=$s AND path_key=$k", ("$s", token), ("$k", key));
                    history.ExecuteNonQuery();
                }

                if (entry == null)
                    continue;
                var join = PathNames.KeyFor(side, listed.Path, key);
                var args = put.Parameters;
                args["$delta"].Value = join == key ? DBNull.Value : join;
                args["$path"].Value = listed.Path;
                args["$folder"].Value = entry.IsFolder;
                args["$size"].Value = entry.Size;
                args["$time"].Value = entry.Modified.UtcTicks;
                args["$id"].Value = Encoding.IdFor(side,entry.Id)!;
                args["$rev"].Value = entry.Revision;
                args["$hash"].Value = Encoding.Hash(entry.ContentHash)!;
                args["$skip"].Value = (object? )entry.Skip ?? DBNull.Value;
                args["$shared"].Value = entry.SharedRoot;
                put.ExecuteNonQuery();
            }

            tx.Commit();
            batch.Clear();
        }
    }

    /// <summary>Clear only this pair's acquisition rows, scratch names and pending terminal cursor.</summary>
    public async Task ClearScanAsync(SqliteConnection db, string pairId, Guid scanId, CancellationToken ct)
    {
        using var tx = db.BeginTransaction();
        using var cmd = StorageCommand.Create(db, tx, "DELETE FROM scan_state.scan WHERE " + Slice + "; DELETE FROM scan_state.deleted_name WHERE scan_id IN ($n,$r)", ("$p", pairId), ("$s", scanId.ToByteArray()), ("$n", DeletedNames.Token(pairId, scanId)), ("$r", DeletedNames.RestoredToken(pairId,scanId)));
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        History.Cleared(pairId, scanId);
        lock (active)
            active.Remove((pairId, scanId));
    }

    private static bool InScope(Scope? scope, string local, string cloud) => scope == null || scope.LocalPath.Length==0 || scope.DropboxPath.Length==0 || PathNames.IsSelfOrUnder(local, scope.LocalPath) || PathNames.IsSelfOrUnder(cloud, scope.DropboxPath);
    // Pull at most one page from each indexed key range. A mapped cloud observation joins its saved
    // binding at the PC key; an unrelated cloud file reusing that PC spelling remains a separate free row.
    // One ordered cursor over physical ranges. The projected pages never rescan the entire pair.
    private static SqliteCommand KeyStream(SqliteConnection db,string pairId,Guid scanId,Scope? scope)
    {
        var ranges=new List<string>();
        void Add(string table,string column,string predicate,string? root,string? projection=null)
        {
            var select="SELECT "+(projection??column)+" AS k FROM "+table+" WHERE pair_id="+Pair+predicate;
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
        return StorageCommand.Create(db,null,sql,("$p",pairId),("$s",scanId.ToByteArray()),("$l",scope==null?null:PathNames.Key(scope.LocalPath)),("$d",scope==null?null:PathNames.DropboxKey(scope.DropboxPath)));
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
        SELECT m.k,i.issue_id,i.kind,i.cause,i.path,i.subject_json,i.at_ticks,i.dismissed,i.ignored,m.side,m.subject_json,m.path_key,m.identity,m.owner_bound FROM footprints m CROSS JOIN issue i ON i.issue_id=m.issue_id ORDER BY m.issue_id
        """;
    private const string CompareRows = """
        ,groups AS (
          SELECT k,binding_id,dk FROM bindings
          UNION ALL SELECT k,NULL,NULL FROM bindings b WHERE dk<>k AND NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND d.dropbox_key_delta=b.k))
        SELECT g.k,
        """ + SavedFields + ",x.side," + EntryFields + """
        ,(SELECT first_repair_done FROM pair WHERE pair_id=$p),g.binding_id IS NOT NULL FROM groups g
        LEFT JOIN synced_entry b ON b.rowid=g.binding_id
        LEFT JOIN scan_state.scan x ON x.rowid IN (
          SELECT rowid FROM scan_state.scan INDEXED BY scan_own WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=0 AND pathkey(path)=+g.k AND (g.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry l INDEXED BY synced_local_key WHERE l.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND pathkey(l.local_path)=+g.k))
          UNION ALL SELECT rowid FROM scan_state.scan INDEXED BY scan_own WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=1 AND key_delta IS NULL AND pathkey(path)=+coalesce(g.dk,g.k) AND (g.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND d.dropbox_key_delta=g.k))
          UNION ALL SELECT rowid FROM scan_state.scan INDEXED BY scan_key WHERE pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND scan_id=$s AND side=1 AND key_delta=coalesce(g.dk,g.k) AND (g.binding_id IS NOT NULL OR NOT EXISTS(SELECT 1 FROM synced_entry d WHERE d.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND d.dropbox_key_delta=g.k)))
        ORDER BY g.k,g.binding_id IS NOT NULL DESC,x.side,x.path
        """;
    /// <summary>Read pages of complete bindings, including notice-only rows and independently retained put-back notices.</summary>
    public async IAsyncEnumerable<(Row Row, IReadOnlyList<Projection> Notices)> CompareAsync(SqliteConnection db, string pairId, Guid scanId, Scope? scope, Func<Side, string, (bool Covered, bool Touched)> evidence, int pageSize, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        using var keyCommand=KeyStream(db,pairId,scanId,scope);
        using var keyReader=await keyCommand.ExecuteReaderAsync(ct);
        (bool Covered, bool Touched) Proof(Side side, string path, bool descendants)
        {
            var result = evidence(side, path);
            return (result.Covered, result.Touched || Touched(pairId, scanId, side, path, descendants));
        }

        while (true)
        {
            var keys=new List<string>();while(keys.Count<pageSize && await keyReader.ReadAsync(ct))keys.Add(keyReader.GetString(0));
            if(keys.Count==0)yield break;
            var page = new List<(Row, IReadOnlyList<Projection>)>();
            {
                var jsonKeys=JsonSerializer.Serialize(keys);
                SqliteCommand Page(string sql) => StorageCommand.Create(db,null,PagePrefix+sql,("$p",pairId),("$s",scanId.ToByteArray()),("$keys",jsonKeys));
                var notices = new Dictionary<(string Key,bool Bound), List<(Notice Notice, NoticeItem? Member)>>();
                Notice? notice=null;
                using (var cmd = Page(MemberRows))
                using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct))
                    {
                        var key = (rd.GetString(0),rd.GetBoolean(13));var noticeId=rd.GetInt64(1);
                        if(notice==null || notice.Id!=noticeId)
                        {
                            var header=Encoding.DecodeSubject(rd.GetString(5));
                            notice=new Notice(noticeId,(NoticeKind)rd.GetInt32(2),Enum.Parse<TextKey>(rd.GetString(3)),rd.GetString(4),header,new(rd.GetInt64(6),TimeSpan.Zero),rd.GetBoolean(7),rd.GetBoolean(8));
                        }
                        var subject=notice.Subject;
                        NoticeItem? member = null;
                        if (!rd.IsDBNull(10))
                        {
                            var side=(Side)rd.GetInt32(9);var memberKey=rd.GetString(11);
                            var identity=rd.IsDBNull(12)?string.Empty:rd.GetString(12);
                            // A v16 work row may point at a descendant not present in the enclosing
                            // notice. The indexed physical side, key and identity are authoritative.
                            member=subject.Items.FirstOrDefault(i=>i.Side==side && i.Id==identity && PathNames.KeyFor(side,i.Path)==memberKey);
                            if(member==null && notice.Cause==TextKey.InfoLinks)
                                member=Encoding.DecodeMemberSubject(rd.GetString(10)).Items.FirstOrDefault(i=>i.Side==side && i.Id==identity && PathNames.KeyFor(side,i.Path)==memberKey);
                            member??=new NoticeItem(side,memberKey,identity);
                        }



                        if (!notices.TryGetValue(key, out var list))
                            notices[key] = list = [];
                        list.Add((notice, member));
                    }

                string? last = null;
                bool bound = false;
                Entry? local = null, cloud = null;
                SyncedEntry? saved = null;
                List<Entry> extra = [];
                bool first = false;
                using (var cmd = Page(CompareRows))
                using (var rd = await cmd.ExecuteReaderAsync(ct))
                    while (await rd.ReadAsync(ct))
                    {
                        var key = rd.GetString(0);
                        if (last != key || bound != rd.GetBoolean(23))
                        {
                            Finish();
                            last = key;
                            bound = rd.GetBoolean(23);
                            saved = rd.IsDBNull(1) ? null : Encoding.ReadSynced(rd,key,1);
                            local = null;
                            cloud = null;
                            extra = [];
                            first = !rd.GetBoolean(22);
                        }

                        if (!rd.IsDBNull(12))
                        {
                            var entry = Encoding.ReadEntry(rd,13,false);
                            if (rd.GetInt32(12) == 0)
                                local = entry;
                            else if (cloud == null)
                                cloud = entry;
                            else
                                extra.Add(entry);
                        }
                    }

                Finish();

                void Finish()
                {
                    if (last == null)
                        return;
                    var lp = local?.Path ?? saved?.LocalPath ?? last;
                    var dp = cloud?.Path ?? saved?.DropboxPath ?? last;
                    if(local==null && cloud==null && saved==null && !notices.ContainsKey((last,bound)))return;
                    if (!InScope(scope, lp, dp))
                        return;
                    var folder = local?.IsFolder == true || cloud?.IsFolder == true || saved?.IsFolder == true;
                    var l = Proof(Side.Local, lp, folder);
                    var d = Proof(Side.Dropbox, dp, folder);
                    var projection = new List<Projection>();
                    if (notices.TryGetValue((last,bound), out var list))
                        foreach (var(n, m)in list)
                        {
                            var proof = m == null ? (l.Covered && d.Covered, l.Touched || d.Touched) : Proof(m.Side, m.Path, folder);
                            projection.Add((n, m, proof.Item1, proof.Item2));
                        }

                    page.Add((new Row(last, local, cloud, saved, extra, null, RowContext.Neutral with { FirstRun = first, CompleteInventory = l.Covered && d.Covered && !l.Touched && !d.Touched }), projection));
                }
            }

            foreach (var result in page)
                yield return result;
        }
    }

    /// <summary>Match unchanged physical names first; stream unmatched identities ordered by the binding's shallowest move.</summary>
    public async IAsyncEnumerable<RenameCandidate> RenamesAsync(SqliteConnection db, string pairId, Guid scanId, Scope? scope, int pageSize, [EnumeratorCancellation] CancellationToken ct)
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
            var unmatched="SELECT * FROM scan_state.scan x WHERE x.pair_id="+Pair+" AND x.scan_id=$s AND x.side="+(int)side+" AND x.id<>'' AND NOT EXISTS("+exact+")";
            return "SELECT x.side,"+EntryFields+","+SavedFields+",pathkey(b.local_path) lk FROM ("+unmatched+" LIMIT -1 OFFSET 0) x CROSS JOIN synced_entry b INDEXED BY "+index+
                " WHERE b.pair_id=x.pair_id AND b."+id+"=x.id AND b.is_folder=x.is_folder AND x.path<>b."+path+
                " AND NOT EXISTS(SELECT 1 FROM scan_state.scan same INDEXED BY scan_own WHERE same.pair_id=x.pair_id AND same.scan_id=x.scan_id AND same.side=x.side AND pathkey(same.path)=+pathkey(b."+path+") AND same.path=b."+path+" AND same.id=x.id AND same.is_folder=x.is_folder)"+
                " AND ($l IS NULL OR scansubtree(b.local_path,$l) OR scansubtree(b.dropbox_path,$d) OR scansubtree(x.path,"+(local?"$l":"$d")+"))";
        }

        var sql = "WITH candidates AS (" + Candidates(Side.Local) + " UNION ALL " + Candidates(Side.Dropbox) + "), ordered AS (SELECT *,min(plandepth(path)) OVER(PARTITION BY lk) depth FROM candidates) SELECT * FROM ordered ORDER BY depth,lk,side,path";
        using var cmd = StorageCommand.Create(db, null, sql, ("$p", pairId), ("$s", scanId.ToByteArray()), ("$l", scope?.LocalPath), ("$d", scope?.DropboxPath));
        using var reader = await cmd.ExecuteReaderAsync(ct);
        var page = new List<RenameCandidate>();
        while (await reader.ReadAsync(ct))
        {
            page.Add(new((Side)reader.GetInt32(0), Encoding.ReadEntry(reader,1,false), Encoding.ReadSynced(reader,reader.GetString(21),10)));
            if (page.Count != pageSize)
                continue;
            foreach (var candidate in page)
                yield return candidate;
            page.Clear();
        }

        foreach (var candidate in page)
            yield return candidate;
    }
}
