using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises notice ownership, replacement, membership and mapped side footprints.</summary>
public static class NoticeTableChecks
{
    /// <summary>A stale close cannot settle the replacement, and another kind cannot erase a hidden PutBack.</summary>
    public static async Task RunAsync()
    {
        await RepeatAndHeader();
        await ReviewLinks();
        using var db=PairTableChecks.Open(); var notices=new NoticeTable();
        var first=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,"same",new([new(Side.Dropbox,"cloud/parent","original")],null),DateTimeOffset.UnixEpoch,false,true);
        await notices.SaveNoticeAsync(db,"p",first,default);
        var old=await At(NoticeKind.PutBack);
        await notices.SaveNoticeAsync(db,"p",first with {Kind=NoticeKind.Failure,Ignored=false},default);
        if(!(await At(NoticeKind.PutBack)).Ignored) throw new InvalidOperationException("Failure displaced hidden PutBack");
        var replacement=first with {Subject=new([new(Side.Dropbox,"cloud/parent","replacement")],null)};
        await notices.SaveNoticeAsync(db,"p",replacement,default);
        await notices.CloseNoticeAsync(db,"p",old,default);
        var current=await At(NoticeKind.PutBack);
        if(current.Subject.Items.Single().Id!="replacement") throw new InvalidOperationException("Stale close changed replacement");
        int hits=0; await foreach(var n in notices.NoticesUnderAsync(db,"p",new("unrelated","cloud/parent/child"),default)) hits++;
        if(hits!=2) throw new InvalidOperationException("Missing covering ancestor");
        await foreach(var n in notices.NoticesUnderAsync(db,"p",new("cloud/parent","unrelated"),default)) throw new InvalidOperationException("Wrong side matched mapped footprint");
        await notices.CloseNoticeAsync(db,"p",current,default);
        if(await notices.NoticeAsync(db,"p",current.Id,default)!=null) throw new InvalidOperationException("Exact close did not settle slot");
        var link=new NoticeItem(Side.Local,"dir/link","7"); var next=link with {Id="8"};
        await notices.UpdateLinkMemberAsync(db,"p",link,true,default);
        await notices.UpdateLinkMemberAsync(db,"p",next,true,default);
        await notices.UpdateLinkMemberAsync(db,"p",link,false,default);
        var survivors=new List<NoticeItem>();
        await foreach(var n in notices.NoticesUnderAsync(db,"p",new("dir","unused"),default)) survivors.AddRange(n.Subject.Items);
        if(!survivors.SequenceEqual(new[]{next})) throw new InvalidOperationException("Old link retirement removed the replacement");
        await notices.UpdateLinkMemberAsync(db,"p",next,false,default);
        await foreach(var n in notices.NoticesAtAsync(db,"p","",default)) throw new InvalidOperationException("Empty link summary retained");
        async Task<Notice> At(NoticeKind kind)
        {
            await foreach(var n in notices.NoticesAtAsync(db,"p",PathNames.Key("same"),default)) if(n.Kind==kind) return n;
            throw new InvalidOperationException("Missing notice kind");
        }
    }
    internal static async Task PagePlan()
    {
        using var db=PairTableChecks.Open();var table=new NoticeTable();string? query=null;
        SQLitePCL.strdelegate_trace capture=(_,sql)=>{if(sql.Contains("ORDER BY",StringComparison.OrdinalIgnoreCase)) query=sql;};
        SQLitePCL.raw.sqlite3_trace(db.Handle,capture,null);
        try { await foreach(var n in table.ListAsync(db,"p",1,default)) throw new InvalidDataException("Expected empty notice list"); }
        finally { SQLitePCL.raw.sqlite3_trace(db.Handle,(SQLitePCL.strdelegate_trace)null!,null); }
        using var cmd=db.CreateCommand();cmd.CommandText="EXPLAIN QUERY PLAN "+(query??throw new InvalidDataException("No notice page statement"));
        using var rd=cmd.ExecuteReader();var plan=new List<string>();while(rd.Read()) plan.Add(rd.GetString(3));
        Check.Require(plan.Any(x=>x.Contains("SEARCH i ",StringComparison.Ordinal) && x.Contains("pair_id=? AND ",StringComparison.Ordinal)) && !plan.Any(x=>x.Contains("TEMP B-TREE",StringComparison.Ordinal)),"Notice page has no ordered pair+cursor seek: "+string.Join("; ",plan));
    }
    private static async Task RepeatAndHeader()
    {
        using var db=PairTableChecks.Open();var table=new NoticeTable();var errors=new List<string>();
        var original=new Notice(0,NoticeKind.Failure,TextKey.FailChanged,"name",new([],null),DateTimeOffset.UnixEpoch,false);
        await table.SaveNoticeAsync(db,"p",original,default);
        var reviewed=await table.NoticeAsync(db,"p",1,default) ?? throw new InvalidDataException();
        await table.SaveNoticeAsync(db,"p",reviewed with {Ignored=true},default);
        await table.SaveNoticeAsync(db,"p",original with {At=original.At.AddSeconds(1)},default);
        var continued=await table.NoticeAsync(db,"p",1,default);
        if(continued is not {Ignored:true} || !continued.Matches(reviewed)) errors.Add("Repeated ordinary failure lost Ignore or reviewed identity");
        using var cmd=db.CreateCommand(); cmd.CommandText="UPDATE issue SET subject_json=$subject WHERE issue_id=1";
        var subject=cmd.Parameters.AddWithValue("$subject","""{"Items":[{"Path":"name","Id":"a"}],"Incoming":null}""");cmd.ExecuteNonQuery();
        try {await table.NoticeAsync(db,"p",1,default);errors.Add("Header silently defaulted missing Side to Local");} catch(InvalidDataException) { }
        subject.Value="""{"Items":[{"Side":1,"Path":"name","Id":null}],"Incoming":null}""";cmd.ExecuteNonQuery();
        try {if((await table.NoticeAsync(db,"p",1,default))?.Subject.Items.Single()!=new NoticeItem(Side.Dropbox,"name","")) errors.Add("Wrong legacy unknown identity");} catch(InvalidDataException){errors.Add("Rejected explicitly null legacy identity");}
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }
    internal static async Task PutBackAndScopePlan()
    {
        using var db=PairTableChecks.Open(); var table=new NoticeTable(); var errors=new List<string>();
        var older=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,"Dir/File",new([],null),DateTimeOffset.UnixEpoch,false);
        await table.SaveNoticeAsync(db,"p",older,default);
        var reviewed=await table.NoticeAsync(db,"p",1,default) ?? throw new InvalidDataException();
        var newer=older with { Path="dir/file",At=older.At.AddTicks(2) };
        await table.SaveNoticeAsync(db,"p",newer,default);
        await table.SaveNoticeAsync(db,"p",older with {Path=newer.Path},default);
        await table.SaveNoticeAsync(db,"p",reviewed with {Ignored=true},default);
        var slots=new List<Notice>(); await foreach(var n in table.NoticesAtAsync(db,"p","DIR/FILE",default)) slots.Add(n);
        if(slots.Count!=1 || slots[0].At!=newer.At || slots[0].Ignored || slots[0].Path!=newer.Path) errors.Add("R1 canonical PutBack or stale visibility");
        var traces=new List<string>(); SQLitePCL.strdelegate_trace trace=(_,sql)=>traces.Add(sql);
        SQLitePCL.raw.sqlite3_trace(db.Handle,trace,null);
        try { await foreach(var n in table.NoticesUnderAsync(db,"p",new("a","z"),default)){} }
        finally { SQLitePCL.raw.sqlite3_trace(db.Handle,(SQLitePCL.strdelegate_trace)null!,null); }
        using var cmd=db.CreateCommand();
        var sqls=traces.Where(q=>q.TrimStart().StartsWith("SELECT",StringComparison.OrdinalIgnoreCase)||q.TrimStart().StartsWith("WITH",StringComparison.OrdinalIgnoreCase)).ToArray();
        Check.Require(sqls.Length!=0,"No production scoped query captured");
        var memberSeek=false; var noticeSeek=false;
        foreach(var sql in sqls)
        {
            cmd.CommandText="EXPLAIN QUERY PLAN "+sql; using var plan=cmd.ExecuteReader();
            while(plan.Read())
            {
                var detail=plan.GetString(3);
                memberSeek|=detail.Contains("pair_id=? AND side=? AND path_key>",StringComparison.Ordinal);
                noticeSeek|=detail.Contains("issue_path_key (pair_id=? AND <expr>>",StringComparison.Ordinal);
                if(detail.Contains("SCAN issue_member",StringComparison.Ordinal) || detail.Contains("issue_path_key (pair_id=?)",StringComparison.Ordinal)) errors.Add("R2 unbounded scope plan: "+detail);
            }
        }
        Check.Require(memberSeek && noticeSeek,"No indexed member or notice descendant range in actual no-statistics plan: "+string.Join("; ",errors));
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }
    private static async Task ReviewLinks()
    {
        using var db=PairTableChecks.Open();var table=new NoticeTable();var errors=new List<string>();
        using var cmd=db.CreateCommand();
        cmd.CommandText="""
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES(10,1,3,'InfoLinks','','{"Items":[],"Incoming":null}',0);
            INSERT INTO issue_member(issue_id,pair_id,side,path_key,identity,subject_json,skip_cause) VALUES
            (10,1,0,'A/ONE',1,'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"a/one","Id":"1"}],"Incoming":null}}','SkipLink'),
            (10,1,0,'B/TWO',2,'{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"b/two","Id":"2"}],"Incoming":null}}','SkipLink');
            """; cmd.ExecuteNonQuery();
        var members=new List<NoticeItem>();
        await foreach(var n in table.NoticesUnderAsync(db,"p",new("a","z"),default)) if(n.Cause==TextKey.InfoLinks) members.AddRange(n.Subject.Items);
        if(!members.SequenceEqual(new[]{new NoticeItem(Side.Local,"a/one","1")})) errors.Add("R3 scoped read returned unrelated links");
        var display=await table.NoticeAsync(db,"p",10,default);
        Check.Require(display?.LinkCount==2 && display.LinkFolders!.SequenceEqual(new[]{"a","b"}) && display.Subject.Items.Count==0,"Link display hydrated the inventory or lost top folders");
        cmd.CommandText="UPDATE issue_member SET subject_json=replace(subject_json,'\"Side\":\"Local\",','') WHERE identity=1";cmd.ExecuteNonQuery();
        try { await foreach(var n in table.NoticesUnderAsync(db,"p",new("a","z"),default)){} errors.Add("R5 accepted member without Side"); }
        catch(InvalidDataException) { } catch(System.Text.Json.JsonException) { }
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }
}
