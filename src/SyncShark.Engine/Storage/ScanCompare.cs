using Microsoft.Data.Sqlite;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Projection = (Massdex.SyncShark.Engine.Notice Notice, Massdex.SyncShark.Engine.NoticeItem? Member, bool Covered, bool Touched);

using NoticeMap = System.Collections.Generic.Dictionary<(string Key,bool Bound),System.Collections.Generic.List<(Massdex.SyncShark.Engine.Notice Notice,Massdex.SyncShark.Engine.NoticeItem? Member)>>;

namespace Massdex.SyncShark.Engine.Storage;
internal sealed class ScanCompare(ScanTable scan)
{
    private delegate (bool Covered,bool Touched) ProofFor(Side side,string path,bool descendants);
    private static bool InScope(Scope? scope, string local, string cloud) => scope == null || scope.LocalPath.Length==0 || scope.DropboxPath.Length==0 || PathNames.IsSelfOrUnder(local, scope.LocalPath) || PathNames.IsSelfOrUnder(cloud, scope.DropboxPath);
    // Pull at most one page from each indexed key range. A mapped cloud observation joins its saved
    // binding at the PC key; an unrelated cloud file reusing that PC spelling remains a separate free row.
    // One ordered cursor over physical ranges. The projected pages never rescan the entire pair.
    private static SqliteCommand KeyStream(SqliteConnection db,string pairId,Guid scanId,Scope? scope,string? after)
    {
        var ranges=new List<string>();
        void Add(string table,string column,string predicate,string? root,string? projection=null)
        {
            var select=Sql.Get(SqlKey.ScanCompareAdd1)+(projection??column)+Sql.Get(SqlKey.ScanCompareAdd2)+table+Sql.Get(SqlKey.ScanCompareAdd3)+predicate;
            if(after!=null) select+=Sql.Get(SqlKey.ScanCompareAdd4)+(projection??column)+Sql.Get(SqlKey.ScanCompareAdd5);
            if(root==null)ranges.Add(select);
            else {ranges.Add(select+Sql.Get(SqlKey.ScanCompareAdd6)+column+Sql.Get(SqlKey.ScanCompareAdd7)+root);ranges.Add(select+Sql.Get(SqlKey.ScanCompareAdd8)+column+Sql.Get(SqlKey.ScanCompareAdd9)+root+Sql.Get(SqlKey.ScanCompareAdd10)+column+Sql.Get(SqlKey.ScanCompareAdd11)+root+Sql.Get(SqlKey.ScanCompareAdd12));}
        }
        string? l=scope==null||scope.LocalPath.Length==0?null:"$l",d=scope==null||scope.DropboxPath.Length==0?null:"$d";
        Add(Sql.Get(SqlKey.ScanCompareKeyStream1),Sql.Get(SqlKey.ScanCompareKeyStream2),"",l);
        if(scope!=null && l!=d){Add(Sql.Get(SqlKey.ScanCompareKeyStream3),Sql.Get(SqlKey.ScanCompareKeyStream4),Sql.Get(SqlKey.ScanCompareKeyStream5),d);Add(Sql.Get(SqlKey.ScanCompareKeyStream6),Sql.Get(SqlKey.ScanCompareKeyStream7),Sql.Get(SqlKey.ScanCompareKeyStream8),d,Sql.Get(SqlKey.ScanCompareKeyStream9));}
        Add(Sql.Get(SqlKey.ScanCompareKeyStream10),Sql.Get(SqlKey.ScanCompareKeyStream11),Sql.Get(SqlKey.ScanCompareKeyStream12),l);
        Add(Sql.Get(SqlKey.ScanCompareKeyStream13),Sql.Get(SqlKey.ScanCompareKeyStream14),Sql.Get(SqlKey.ScanCompareKeyStream15),d);
        Add(Sql.Get(SqlKey.ScanCompareKeyStream16),Sql.Get(SqlKey.ScanCompareKeyStream17),Sql.Get(SqlKey.ScanCompareKeyStream18),d);
        Add(Sql.Get(SqlKey.ScanCompareKeyStream19),Sql.Get(SqlKey.ScanCompareKeyStream20),Sql.Get(SqlKey.ScanCompareKeyStream21),l);Add(Sql.Get(SqlKey.ScanCompareKeyStream22),Sql.Get(SqlKey.ScanCompareKeyStream23),Sql.Get(SqlKey.ScanCompareKeyStream24),d);
        Add(Sql.Get(SqlKey.ScanCompareKeyStream25),Sql.Get(SqlKey.ScanCompareKeyStream26),"",l);if(scope!=null && l!=d)Add(Sql.Get(SqlKey.ScanCompareKeyStream27),Sql.Get(SqlKey.ScanCompareKeyStream28),"",d);
        var sql=string.Join(Sql.Get(SqlKey.ScanCompareKeyStream29),ranges)+Sql.Get(SqlKey.ScanCompareKeyStream30);
        return StorageCommand.Create(db,null,sql,("$p",pairId),("$s",scanId.ToByteArray()),("$a",after),("$l",scope==null?null:PathNames.Key(scope.LocalPath)),("$d",scope==null?null:PathNames.DropboxKey(scope.DropboxPath)));
    }
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
        SqliteCommand Page(string sql) => StorageCommand.Create(db,tx,Sql.Get(SqlKey.ScanComparePagePrefix)+sql,("$p",pairId),("$s",scanId.ToByteArray()),("$keys",json));
        using var members=Page(Sql.Get(SqlKey.ScanCompareMemberRows));
        var notices=await ReadNotices(members,ct);
        using var entries=Page(Sql.Get(SqlKey.ScanCompareCompareRows));
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
            var index = local ? Sql.Get(SqlKey.ScanCompareCandidates1) : Sql.Get(SqlKey.ScanCompareCandidates2);
            var id = local ? Sql.Get(SqlKey.ScanCompareCandidates3) : Sql.Get(SqlKey.ScanCompareCandidates4);
            var path = local ? Sql.Get(SqlKey.ScanCompareCandidates5) : Sql.Get(SqlKey.ScanCompareCandidates6);
            string ExactSaved(string idx,string match)=>Sql.Get(SqlKey.ScanCompareExactSaved1)+idx+Sql.Get(SqlKey.ScanCompareExactSaved2)+match+Sql.Get(SqlKey.ScanCompareExactSaved3)+path+Sql.Get(SqlKey.ScanCompareExactSaved4)+id+Sql.Get(SqlKey.ScanCompareExactSaved5);
            var exact=ExactSaved(Sql.Get(SqlKey.ScanCompareCandidates7),Sql.Get(SqlKey.ScanCompareCandidates8)+(local?"":Sql.Get(SqlKey.ScanCompareCandidates9)));
            if(!local)exact+=Sql.Get(SqlKey.ScanCompareCandidates10)+ExactSaved(Sql.Get(SqlKey.ScanCompareCandidates11),Sql.Get(SqlKey.ScanCompareCandidates12));
            // OFFSET 0 prevents SQLite flattening the exact-name filter into the identity join.
            // Unchanged aliases are discarded before there is a candidate fan-out.
            var unmatched=Sql.Get(SqlKey.ScanCompareCandidates13)+(int)side+Sql.Get(SqlKey.ScanCompareCandidates14)+exact+Sql.Get(SqlKey.ScanCompareCandidates15);
            return Sql.Get(SqlKey.ScanCompareCandidates16)+unmatched+Sql.Get(SqlKey.ScanCompareCandidates17)+index+
                Sql.Get(SqlKey.ScanCompareCandidates18)+id+Sql.Get(SqlKey.ScanCompareCandidates19)+path+
                Sql.Get(SqlKey.ScanCompareCandidates20)+path+Sql.Get(SqlKey.ScanCompareCandidates21)+path+Sql.Get(SqlKey.ScanCompareCandidates22)+(local?"$l":"$d")+Sql.Get(SqlKey.ScanCompareCandidates23);
        }
        var sql = Sql.Get(SqlKey.ScanCompareRenames1) + Candidates(Side.Local) + Sql.Get(SqlKey.ScanCompareRenames2) + Candidates(Side.Dropbox) + Sql.Get(SqlKey.ScanCompareRenames3);
        using (var temp = StorageCommand.Create(db,null,Sql.Get(SqlKey.ScanCompareRenames4)))
            await temp.ExecuteNonQueryAsync(ct);
        var all=scope==null || scope.LocalPath.Length==0 || scope.DropboxPath.Length==0; // The empty component means whole pair here too (InScope/KeyStream).
        using (var fill = StorageCommand.Create(db, null, Sql.Get(SqlKey.ScanCompareRenames5) + sql, ("$p",pairId),("$s",scanId.ToByteArray()),("$l",all?null:scope!.LocalPath),("$d",all?null:scope!.DropboxPath)))
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
        finally { using var drop=StorageCommand.Create(db,null,Sql.Get(SqlKey.ScanCompareRenames6)); drop.ExecuteNonQuery(); }
    }

    private static async Task<(List<RenameCandidate> Page,long Last)> ReadRenamePage(SqliteConnection db,long after,int size,CancellationToken ct)
    {
        var page=new List<RenameCandidate>();
        using var cmd=StorageCommand.Create(db,null,Sql.Get(SqlKey.ScanCompareReadRenamePage1),("$after",after),("$size",size));
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
