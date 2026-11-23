using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Exercises notice ownership, replacement, membership and mapped side footprints.</summary>
public static class NoticeTableChecks
{
    /// <summary>A stale close cannot settle the replacement, and another kind cannot erase a hidden PutBack.</summary>
    public static async Task RunAsync()
    {
        await SignInNotice(true);
        await DiagnosisUpdate(NoticeKind.Failure,TextKey.FailChanged,TextKey.PublishFailed);
        await ChangedResponsibilities();
        await SkipChangesKind();
        await StoreVisibility();
        await PutBackContinuity();
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
        int hits=0; await foreach(var n in notices.NoticesUnderAsync(db,"p",new("unrelated","cloud/parent/child"),1,default)) hits++;
        if(hits!=2) throw new InvalidOperationException("Missing covering ancestor");
        await foreach(var n in notices.NoticesUnderAsync(db,"p",new("cloud/parent","unrelated"),1,default)) throw new InvalidOperationException("Wrong side matched mapped footprint");
        await notices.CloseNoticeAsync(db,"p",current,default);
        if(await notices.NoticeAsync(db,"p",current.Id,default)!=null) throw new InvalidOperationException("Exact close did not settle slot");
        var link=new NoticeItem(Side.Local,"dir/link","7"); var next=link with {Id="8"};
        Check.Require(await notices.UpdateLinkMemberAsync(db,"p",link,true,default),"First observed link was not added");
        var respelled=link with {Path="dir/Link"};
        Check.Require(await notices.UpdateLinkMemberAsync(db,"p",respelled,true,default),"Same-key link spelling change reported no change");
        var spelled=new List<NoticeItem>();await foreach(var n in notices.NoticesUnderAsync(db,"p",new("dir","unused"),1,default)) spelled.AddRange(n.Subject.Items);
        Check.Require(spelled.SequenceEqual(new[]{respelled}),"Changed link spelling was not persisted");
        Check.Require(await notices.UpdateLinkMemberAsync(db,"p",next,true,default),"Changed link identity was not updated");
        await notices.UpdateLinkMemberAsync(db,"p",link,false,default);
        var survivors=new List<NoticeItem>();
        await foreach(var n in notices.NoticesUnderAsync(db,"p",new("dir","unused"),1,default)) survivors.AddRange(n.Subject.Items);
        if(!survivors.SequenceEqual(new[]{next})) throw new InvalidOperationException("Old link retirement removed the replacement");
        await notices.UpdateLinkMemberAsync(db,"p",next,false,default);
        await foreach(var n in notices.NoticesAtAsync(db,"p","",default)) throw new InvalidOperationException("Empty link summary retained");
        async Task<Notice> At(NoticeKind kind)
        {
            await foreach(var n in notices.NoticesAtAsync(db,"p",PathNames.Key("same"),default)) if(n.Kind==kind) return n;
            throw new InvalidOperationException("Missing notice kind");
        }
    }
    private static async Task ChangedResponsibilities()
    {
        using var db=PairTableChecks.Open();var notices=new NoticeTable();var item=new NoticeItem(Side.Local,"original","1");
        var source=new Notice(0,NoticeKind.Failure,TextKey.FailChanged,item.Path,new([item],Side.Local,IssueSubject.FirstRunDeletionKind.Item),DateTimeOffset.UnixEpoch,false);
        await notices.SaveNoticeAsync(db,"p",source,default);
        var changed=source with {Cause=TextKey.PublishFailed,Subject=new([item,new(Side.Dropbox,item.Path,"cloud")],null)};
        await notices.SaveNoticeAsync(db,"p",changed,default);
        Check.Require((await notices.NoticeAsync(db,"p",1,default))?.Subject.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.Item,"Failure turnover discarded first-sync deletion evidence");
        var fresh=source with {Subject=new([item with {Id="2"}],Side.Local)};
        await notices.SaveNoticeAsync(db,"p",fresh,default);
        Check.Require((await notices.NoticeAsync(db,"p",1,default))?.Subject.FirstRunDeletion==IssueSubject.FirstRunDeletionKind.None,"Replacement identity inherited old evidence");
        var move=new IssueSubject.UnknownMove("cloud","source","destination",null,false);
        var moving=changed with {Subject=changed.Subject with {Move=move}};
        await notices.SaveNoticeAsync(db,"p",moving,default);
        await notices.SaveNoticeAsync(db,"p",source,default);
        Check.Require((await notices.NoticeAsync(db,"p",1,default))?.Subject.Move==move,"New diagnostic erased an unconfirmed move");
    }
    private static async Task SkipChangesKind()
    {
        using var db=PairTableChecks.Open();var table=new NoticeTable();var a=new NoticeItem(Side.Local,"x","1");var b=new NoticeItem(Side.Dropbox,"x","cloud");
        var skip=new Notice(0,NoticeKind.Skipped,TextKey.SkipOnlineOnly,"x",new([a,b],null),DateTimeOffset.UnixEpoch,false);
        await table.SaveNoticeAsync(db,"p",skip,default);await table.UpdateLinkMemberAsync(db,"p",a,true,default);
        Check.Require((await table.NoticeAsync(db,"p",1,default))?.Subject.Items.SequenceEqual(new[]{b})==true,"Reclassification retained stale skipped member or lost survivor");
        await table.SaveNoticeAsync(db,"p",skip,default);
        Check.Require(await table.NoticeAsync(db,"p",2,default)==null && (await table.NoticeAsync(db,"p",1,default))!.Subject.HasSameItems(skip.Subject),"Reverse reclassification retained empty links or lost skip");
    }
    private static async Task StoreVisibility()
    {
        var root=Directory.CreateTempSubdirectory();
        try
        {
            using IStore store=await Store.OpenAsync(root.FullName,true,Scenarios.ItemChecks.Settings,default) ?? throw new InvalidDataException();
            var pair=Scenarios.ItemChecks.Pair();await store.SavePairAsync(pair,default);
            foreach(var kind in new[]{NoticeKind.RootIdentity,NoticeKind.DriveChange})
            {
                var admission=new Notice(0,kind,TextKey.DriveChanged,"",new([],null),DateTimeOffset.UnixEpoch,false);
                await store.SaveNoticeAsync(pair.PairId,admission,default);await store.SaveNoticeAsync(pair.PairId,admission with {Cause=TextKey.DriveRestored},default);
                var active=new List<Notice>();await foreach(var n in store.NoticesAtAsync(pair.PairId,"",default)) if(n.Kind==kind) active.Add(n);
                Check.Require(active.Count==1 && active[0].Cause==TextKey.DriveRestored && await store.CloseNoticeAsync(pair.PairId,active[0],default),"Changed admission diagnosis did not close once");
                await foreach(var n in store.NoticesAtAsync(pair.PairId,"",default)) Check.Require(n.Kind!=kind,"A stale admission notice still blocks the pair");
            }
            var put=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,"file",new([],null),DateTimeOffset.UnixEpoch,false);
            await store.SaveNoticeAsync(pair.PairId,put,default);var atPath=new List<Notice>();await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.Key(put.Path),default)) atPath.Add(n);var old=atPath.Single();
            Check.Require(await store.UpdateNoticeVisibilityAsync(pair.PairId,old,true,default) && (await store.NoticeAsync(pair.PairId,old.Id,default))!.Ignored,"Real Store ignore not persisted");
            Check.Require(await store.UpdateNoticeVisibilityAsync(pair.PairId,old,false,default) && !(await store.NoticeAsync(pair.PairId,old.Id,default))!.Ignored,"Real Store unignore not persisted");
            await store.SaveNoticeAsync(pair.PairId,put with {At=put.At.AddSeconds(1)},default);
            Check.Require(!await store.UpdateNoticeVisibilityAsync(pair.PairId,old,true,default),"Stale reviewed visibility rewound replacement");
            var current=await store.NoticeAsync(pair.PairId,old.Id,default) ?? throw new InvalidDataException();
            await store.CloseNoticeAsync(pair.PairId,current,default);
            Check.Require(!await store.UpdateNoticeVisibilityAsync(pair.PairId,current,true,default) && await store.NoticeAsync(pair.PairId,current.Id,default)==null,"Closed notice was recreated by visibility update");
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();root.Delete(true);}
    }
    internal static async Task RootInfoSlots()
    {
        await SignInNotice(false);
        using var db=PairTableChecks.Open();using var cmd=db.CreateCommand();var table=new NoticeTable();
        cmd.CommandText="""
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks,dismissed) VALUES(25,1,3,'DeletedHistoryCorrupt','','{"Items":[],"Incoming":null}',17,1);
            INSERT INTO issue_member(issue_id,pair_id,side,path_key,subject_json) VALUES(25,1,1,'HISTORY','{"Version":1,"Value":{"Items":[],"Incoming":null}}');
            """;cmd.ExecuteNonQuery();
        var links=new Notice(0,NoticeKind.Info,TextKey.InfoLinks,"",new([],null),DateTimeOffset.UnixEpoch,false);
        var wrong=new List<string>();
        await table.SaveNoticeAsync(db,"p",links,default);
        cmd.CommandText="SELECT issue_id FROM issue WHERE cause='InfoLinks'";var linkId=(long)cmd.ExecuteScalar()!;
        cmd.CommandText="""
            INSERT INTO issue_member(issue_id,pair_id,side,path_key,subject_json,skip_cause)
            SELECT issue_id,1,0,'LINK','{"Version":1,"Value":{"Items":[{"Side":"Local","Path":"link","Id":""}],"Incoming":null}}','SkipLink' FROM issue WHERE cause='InfoLinks';
            """;cmd.ExecuteNonQuery();
        cmd.CommandText="SELECT member_id FROM issue_member WHERE issue_id=$lid";cmd.Parameters.AddWithValue("$lid",linkId);var memberId=(long)cmd.ExecuteScalar()!;
        var refresh=links with {At=links.At.AddSeconds(1)};await table.SaveNoticeAsync(db,"p",refresh,default);
        cmd.CommandText="SELECT count(*) FROM issue i JOIN issue_member m USING(issue_id) WHERE i.issue_id=$lid AND m.member_id=$mid AND i.at_ticks=$at AND m.path_key='LINK'";
        cmd.Parameters.AddWithValue("$mid",memberId);cmd.Parameters.AddWithValue("$at",refresh.At.UtcTicks);
        if((long)cmd.ExecuteScalar()! != 1) wrong.Add("InfoLinks refresh did not retain id/member and publish new At");
        cmd.CommandText="""
            SELECT count(*) FROM issue i JOIN issue_member m USING(issue_id) WHERE i.pair_id=1 AND i.path='' AND i.kind=3 AND
            ((i.cause='DeletedHistoryCorrupt' AND i.issue_id=25 AND i.at_ticks=17 AND i.dismissed=1 AND m.path_key='HISTORY') OR
             (i.cause='InfoLinks' AND m.path_key='LINK'));
            """;
        Check.Require((long)cmd.ExecuteScalar()! == 2,"Pair-level Info upsert replaced another cause or lost its members");
        await CheckReplayAsync(db,table,memberId,wrong);
        var old=links with {Kind=NoticeKind.PutBack,Cause=TextKey.FailChanged,Path="Dir/File"};
        var winner=old with {Cause=TextKey.PublishFailed,Path="dir/file",At=old.At.AddTicks(2)};
        Check.Require(await table.SaveNoticeAsync(db,"p",old,default) is {Applied:true,Prior:null},"New PutBack unexpectedly displaced a slot");
        cmd.CommandText="SELECT issue_id FROM issue WHERE pair_id=1 AND kind=$oldKind";cmd.Parameters.AddWithValue("$oldKind",(int)old.Kind);
        var oldId=(long)cmd.ExecuteScalar()!;
        const string oldSubject="""{"Items":[{"Side":1,"Path":"former/location","Id":"previous"}],"Incoming":1}""";
        cmd.CommandText="UPDATE issue SET subject_json=$oldSubject WHERE issue_id=$oldId";
        cmd.Parameters.AddWithValue("$oldSubject",oldSubject);cmd.Parameters.AddWithValue("$oldId",oldId);cmd.ExecuteNonQuery();
        var (applied,displaced)=await table.SaveNoticeAsync(db,"p",winner,default);
        if(!applied || displaced!=(oldId,old.Path,old.Kind,old.Cause,oldSubject)) wrong.Add("Changed-cause PutBack did not displace the previous cause");
        if(await table.SaveNoticeAsync(db,"p",old with {At=old.At.AddTicks(1)},default) is not {Applied:false,Prior:null}) wrong.Add("Stale PutBack reported a displacement");
        cmd.CommandText="SELECT cause,path,at_ticks,issue_id FROM issue WHERE pair_id=1 AND pathkey(path)=pathkey($path) AND kind=$kind";
        cmd.Parameters.AddWithValue("$path",old.Path);cmd.Parameters.AddWithValue("$kind",(int)old.Kind);
        using(var rows=cmd.ExecuteReader())
        {
            if(!rows.Read() || rows.GetString(0)!=winner.Cause.ToString() || rows.GetString(1)!=winner.Path || rows.GetInt64(2)!=winner.At.UtcTicks) wrong.Add("Widened cause key kept or restored an older PutBack");
            else if(rows.GetInt64(3)!=oldId) wrong.Add("Changed-cause PutBack did not retain its persisted issue_id");
            if(rows.Read()) wrong.Add("Multiple PutBack winners remained");
        }
        foreach(var (kind,oldCause,newCause) in new[]{(NoticeKind.RootIdentity,TextKey.RootUnbound,TextKey.RootChanged),(NoticeKind.DriveChange,TextKey.DriveChanged,TextKey.DriveRestored)})
        {
            try { await DiagnosisUpdate(kind,oldCause,newCause); }
            catch(InvalidDataException failure) { wrong.Add(failure.Message); }
        }
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }
    private static async Task CheckReplayAsync(Microsoft.Data.Sqlite.SqliteConnection db,NoticeTable table,long memberId,List<string> wrong)
    {
        using var cmd=db.CreateCommand();cmd.Parameters.AddWithValue("$mid",memberId);
        object[] Snapshot()
        {
            // Compare stored bytes, not deserialized JSON; keep NULL distinct from empty and INTEGER distinct from TEXT.
            cmd.CommandText="SELECT member_id,issue_id,pair_id,side,hex(path_key),typeof(identity),hex(identity),hex(subject_json),typeof(skip_cause),hex(skip_cause) FROM issue_member WHERE member_id=$mid";
            using var row=cmd.ExecuteReader();if(!row.Read()) return [];
            var values=new object[row.FieldCount];row.GetValues(values);return values;
        }
        var member=new NoticeItem(Side.Local,"link",string.Empty);var before=Snapshot();Check.Require(before.Length!=0,"Replay member was not seeded");
        cmd.CommandText="CREATE TEMP TRIGGER replay_guard BEFORE DELETE ON issue_member WHEN OLD.member_id="+memberId+" BEGIN SELECT RAISE(ABORT,'Identical link replay deleted its member'); END";cmd.ExecuteNonQuery();
        if(await table.UpdateLinkMemberAsync(db,"p",member,true,default)) wrong.Add("Identical link replay reported a change");
        if(!before.SequenceEqual(Snapshot())) wrong.Add("False-returning link replay changed the persisted member row");
        cmd.CommandText="DROP TRIGGER replay_guard";cmd.ExecuteNonQuery();
        if(await table.UpdateLinkMemberAsync(db,"p",member with {Path="missing"},false,default)) wrong.Add("Zero-row link removal reported a change");
        if(!await table.UpdateLinkMemberAsync(db,"p",member,false,default)) wrong.Add("Actual link removal reported no change");
        if(Snapshot().Length!=0) wrong.Add("Successful removal left the link member in storage");
    }
    private static async Task SignInNotice(bool repeatCredentialFailure)
    {
        using var db=PairTableChecks.Open();var table=new NoticeTable();
        var signIn=new Notice(0,NoticeKind.Failure,TextKey.SignInReturnRetry,"",new([],null),DateTimeOffset.UnixEpoch,false);
        await table.SaveNoticeAsync(db,"p",signIn,default);
        await table.SaveNoticeAsync(db,"p",signIn with {Cause=TextKey.RootUnavailable},default);
        await table.SaveNoticeAsync(db,"p",signIn with {Cause=TextKey.ReadDropboxFailed},default);
        using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT cause,issue_id FROM issue WHERE pair_id=1 AND path='' AND kind=$kind AND NOT ignored AND NOT dismissed ORDER BY cause";
        cmd.Parameters.AddWithValue("$kind",(int)NoticeKind.Failure);long id;
        using(var rows=cmd.ExecuteReader())
        {
            Check.Require(rows.Read() && rows.GetString(0)==nameof(TextKey.ReadDropboxFailed) && rows.Read() && rows.GetString(0)==nameof(TextKey.RootUnavailable) && rows.Read() && rows.GetString(0)==nameof(TextKey.SignInReturnRetry),
                "Distinct root failure causes did not coexist before the credential retry");
            id=rows.GetInt64(1);Check.Require(!rows.Read(),"Duplicate root failure notice");
        }
        if(!repeatCredentialFailure) return;
        Check.Require(await table.UpdateNoticeVisibilityAsync(db,"p",signIn with {Id=id},true,default),"Could not ignore reviewed credential notice");
        var repeat=await table.SaveNoticeAsync(db,"p",signIn with {At=signIn.At.AddTicks(1)},default);
        var stored=await table.NoticeAsync(db,"p",id,default);
        Check.Require(!repeat.Applied && stored is {Ignored:true,Dismissed:false,Cause:TextKey.SignInReturnRetry} && stored.Id==id && stored.At==signIn.At,
            "Repeated root failure changed its reviewed row, time or Ignore choice");
    }
    private static async Task DiagnosisUpdate(NoticeKind kind,TextKey oldCause,TextKey newCause)
    {
        using var db=PairTableChecks.Open();using var cmd=db.CreateCommand();var table=new NoticeTable();
        var original=new Notice(0,kind,oldCause,kind==NoticeKind.Failure?"item":"",new([],Side.Local),DateTimeOffset.UnixEpoch,false);
        await table.SaveNoticeAsync(db,"p",original,default);
        cmd.CommandText="SELECT issue_id FROM issue WHERE pair_id=1 AND path=$path AND kind=$kind";cmd.Parameters.AddWithValue("$kind",(int)kind);cmd.Parameters.AddWithValue("$path",original.Path);
        var originalId=(long)cmd.ExecuteScalar()!;
        var changed=original with {Cause=newCause,Subject=original.Subject with {Incoming=Side.Dropbox}};
        var result=await table.SaveNoticeAsync(db,"p",changed,default);
        cmd.CommandText="SELECT issue_id,cause,json_extract(subject_json,'$.Incoming') FROM issue WHERE pair_id=1 AND path=$path AND kind=$kind";
        using var rows=cmd.ExecuteReader();
        Check.Require(result.Applied && result.Prior?.Id==originalId && rows.Read() && rows.GetInt64(0)==originalId &&
            rows.GetString(1)==newCause.ToString() && rows.GetInt32(2)==(int)Side.Dropbox && !rows.Read(),
            $"{kind} diagnosis change did not apply in place with the new cause and subject");
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
        Check.Require(await table.UpdateNoticeVisibilityAsync(db,"p",reviewed,true,default),"Reviewed failure refused visibility change");
        await table.SaveNoticeAsync(db,"p",original with {At=original.At.AddSeconds(1)},default);
        var continued=await table.NoticeAsync(db,"p",1,default);
        if(continued is not {Ignored:true} || !continued.Matches(reviewed)) errors.Add("Repeated ordinary failure lost Ignore or reviewed identity");
        await table.SaveNoticeAsync(db,"p",original with {Path="",Kind=NoticeKind.Info},default);
        await table.SaveNoticeAsync(db,"p",original with {Kind=NoticeKind.Info},default);
        await table.SaveNoticeAsync(db,"p",original with {Path="",Kind=NoticeKind.Info,Cause=TextKey.DeletedHistoryCorrupt},default);
        var ordered=new List<(string,NoticeKind,TextKey)>();
        await foreach(var n in table.ListAsync(db,"p",1,default)) ordered.Add((n.Path,n.Kind,n.Cause));
        Check.Require(ordered.SequenceEqual(new[]{("",NoticeKind.Info,TextKey.DeletedHistoryCorrupt),("",NoticeKind.Info,TextKey.FailChanged),("name",NoticeKind.Failure,TextKey.FailChanged),("name",NoticeKind.Info,TextKey.FailChanged)}),"Notice page skipped a cause or kind sharing the same path");
        using var cmd=db.CreateCommand(); cmd.CommandText="UPDATE issue SET subject_json=$subject WHERE issue_id=1";
        var subject=cmd.Parameters.AddWithValue("$subject","""{"Items":[{"Path":"name","Id":"a"}],"Incoming":null}""");cmd.ExecuteNonQuery();
        try {await table.NoticeAsync(db,"p",1,default);errors.Add("Header silently defaulted missing Side to Local");} catch(InvalidDataException) { }
        subject.Value="""{"Items":[{"Side":1,"Path":"name","Id":null}],"Incoming":null}""";cmd.ExecuteNonQuery();
        try {if((await table.NoticeAsync(db,"p",1,default))?.Subject.Items.Single()!=new NoticeItem(Side.Dropbox,"name","")) errors.Add("Wrong legacy unknown identity");} catch(InvalidDataException){errors.Add("Rejected explicitly null legacy identity");}
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }
    private static async Task PutBackContinuity()
    {
        using var db=PairTableChecks.Open(); var table=new NoticeTable(); var errors=new List<string>();
        var older=new Notice(0,NoticeKind.PutBack,TextKey.FailChanged,"Dir/File",new([],null),DateTimeOffset.UnixEpoch,false);
        await table.SaveNoticeAsync(db,"p",older,default);
        var reviewed=await table.NoticeAsync(db,"p",1,default) ?? throw new InvalidDataException();
        var newer=older with { Path="dir/file",At=older.At.AddTicks(2) };
        await table.SaveNoticeAsync(db,"p",newer,default);
        await table.SaveNoticeAsync(db,"p",older with {Path=newer.Path},default);
        Check.Require(!await table.UpdateNoticeVisibilityAsync(db,"p",reviewed,true,default),"Superseded PutBack accepted visibility change");
        var slots=new List<Notice>(); await foreach(var n in table.NoticesAtAsync(db,"p","DIR/FILE",default)) slots.Add(n);
        if(slots.Count!=1 || slots[0].At!=newer.At || slots[0].Ignored || slots[0].Path!=newer.Path) errors.Add("R1 canonical PutBack or stale visibility");
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }
    internal static async Task ScopePlan()
    {
        using var db=PairTableChecks.Open();var table=new NoticeTable();var errors=new List<string>();
        var traces=new List<string>(); SQLitePCL.strdelegate_trace trace=(_,sql)=>traces.Add(sql);
        SQLitePCL.raw.sqlite3_trace(db.Handle,trace,null);
        try { await foreach(var n in table.NoticesUnderAsync(db,"p",new("a","z"),1,default)){} }
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
        // Replay the actual bounded statement before JSON decoding so this selection also runs in the isolated storage lane.
        cmd.CommandText="""
            INSERT INTO issue(issue_id,pair_id,kind,cause,path,subject_json,at_ticks) VALUES
            (1,1,0,'FailChanged','A/legacy','{"Items":[{"Side":0,"Path":"other","Id":"1"}],"Incoming":null}',0),
            (2,1,0,'FailChanged','A/modern','{"Items":[],"Incoming":null}',0),
            (3,1,0,'FailChanged','outside','{"Items":[{"Side":0,"Path":"a","Id":"3"}],"Incoming":null}',0);
            INSERT INTO issue_member(issue_id,pair_id,side,path_key,subject_json) VALUES(2,1,0,'OUTSIDE','{"Version":1,"Value":{"Items":[]}}');
            """;cmd.ExecuteNonQuery();
        cmd.CommandText=sqls.Single(q=>q.TrimStart().StartsWith("WITH",StringComparison.OrdinalIgnoreCase));
        var hits=new List<long>();using(var rows=cmd.ExecuteReader()) while(rows.Read()) hits.Add(rows.GetInt64(0));
        Check.Require(hits.SequenceEqual(new long[]{1}),"Missing-member fallback must seek the stored notice path only, never embedded JSON or a member-backed notice's unrelated header path");
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
        await foreach(var n in table.NoticesUnderAsync(db,"p",new("a","z"),1,default)) if(n.Cause==TextKey.InfoLinks) members.AddRange(n.Subject.Items);
        if(!members.SequenceEqual(new[]{new NoticeItem(Side.Local,"a/one","1")})) errors.Add("R3 scoped read returned unrelated links");
        var display=await table.NoticeAsync(db,"p",10,default);
        Check.Require(display?.LinkCount==2 && display.LinkFolders!.SequenceEqual(new[]{"a","b"}) && display.Subject.Items.Count==0,"Link display hydrated the inventory or lost top folders");
        // A consumer stopping at the first hit must not even decode the malformed later page.
        cmd.CommandText="UPDATE issue_member SET subject_json=json_remove(subject_json,'$.Value.Items[0].Side') WHERE identity=2";cmd.ExecuteNonQuery();
        await using(var stream=table.NoticesUnderAsync(db,"p",new("",""),1,default).GetAsyncEnumerator())
        {
            Check.Require(await stream.MoveNextAsync() && stream.Current.Subject.Items.SequenceEqual(new[]{new NoticeItem(Side.Local,"a/one","1")}),"Late link decoded before first result");
            try { await stream.MoveNextAsync();errors.Add("Malformed later page accepted"); } catch(InvalidDataException) { }
        }
        cmd.CommandText="UPDATE issue_member SET subject_json=replace(subject_json,'\"Side\":\"Local\",','') WHERE identity=1";cmd.ExecuteNonQuery();
        try { await foreach(var n in table.NoticesUnderAsync(db,"p",new("a","z"),1,default)){} errors.Add("R5 accepted member without Side"); }
        catch(InvalidDataException) { } catch(System.Text.Json.JsonException) { }
        Check.Require(errors.Count==0,string.Join("; ",errors));
    }
}
