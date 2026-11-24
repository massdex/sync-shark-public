using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Massdex.SyncShark.Engine.Storage;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks the published store contract and its open-existing boundary.</summary>
public static class StoreChecks
{
    /// <summary>Every Store operation documents its behavior, and probing an absent image cannot create it.</summary>
    public static async Task RunAsync()
    {
        var root = new DirectoryInfo(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "SyncShark.sln"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Source checkout not found.");
        var storage = Path.Combine(root.FullName,"src","SyncShark.Engine","Storage");
        var violations = new List<string>();
        var options = CSharpParseOptions.Default.WithDocumentationMode(Microsoft.CodeAnalysis.DocumentationMode.Diagnose);
        var storeTree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(storage,"Store.cs")),options);
        var source=storeTree.GetText().ToString();
        var api=CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root.FullName,"src","SyncShark.Engine","IStore.cs")),options);
        static string Signature(MethodDeclarationSyntax m) => m.Identifier.ValueText + "(" + string.Join(",",m.ParameterList.Parameters.Select(p=>string.Concat(p.Type!.DescendantTokens().Select(t=>t.Text)))) + ")";
        var contracts=api.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m=>Summary(m)!=null)
            .ToDictionary(Signature,m=>Summary(m)!);
        foreach(var owner in new[]{"Store","Committer","Encoding"})
        {
            var tree=owner=="Store"?storeTree:CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(storage,owner+".cs")),options);
            foreach(var declaration in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>().Where(d=>d.Modifiers.Any(t=>t.RawKind==(int)SyntaxKind.PublicKeyword)))
            {
                var name=declaration switch { MethodDeclarationSyntax m=>m.Identifier.ValueText, ConstructorDeclarationSyntax=>".ctor",
                    TypeDeclarationSyntax t=>t.Identifier.ValueText, FieldDeclarationSyntax f=>f.Declaration.Variables.Single().Identifier.ValueText, _=>declaration.Kind().ToString() };
                // Read IStore's own rule for each documented overload, rather than maintaining a second copy.
                var contract=owner=="Store" && declaration is MethodDeclarationSyntax mth ? contracts.GetValueOrDefault(Signature(mth)) : null;
                var required=contract ?? MemberRule(owner,name);
                var text=Summary(declaration);
                if(text==null || required==null || (contract!=null ? text!=contract : !text.Contains(required,StringComparison.OrdinalIgnoreCase)))
                    violations.Add(owner+"."+name+" missing or incorrect behavior summary; expected: "+required);
            }
        }
        var methods=storeTree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>();
        foreach(var example in new[]{"// <summary>not documentation</summary>","/// <summary>  </summary>","/// <summary>TODO</summary>","/// <summary>Has words <b>but mismatches</summary>","/// <summary><!-- comment only --></summary>","/// <summary>Not implemented.</summary>","/// <summary>X operation on the supplied facts.</summary>"})
        {
            var fixture=CSharpSyntaxTree.ParseText(string.Join(Environment.NewLine,"class Probe {",example,"public void Run() {} }"),options).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().Single();
            if (Summary(fixture)!=null) violations.Add("#6 rejected-documentation fixture accepted");
        }
        if(new[]{source, File.ReadAllText(Path.Combine(storage,"ScanScratch.cs"))}.Any(text => text.Contains("ForeignKeys = true",StringComparison.Ordinal))) violations.Add("#5 mutable connection configuration before version acceptance");
        var ingest=methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="IngestAsync");
        var add=ingest.DescendantNodes().OfType<InvocationExpressionSyntax>().Single(x=>x.Expression.ToString()=="scan.AddScanAsync");
        if(!add.ArgumentList.Arguments.Any(a=>a.Expression.ToString()=="gate")) violations.Add("#2 no shared writer admission for scan batches");
        var reset = methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText==nameof(IStore.ResetJournalAsync));
        var visibility=methods.OfType<MethodDeclarationSyntax>().SingleOrDefault(m=>m.Identifier.ValueText==nameof(IStore.UpdateNoticeVisibilityAsync));
        if(visibility==null || !visibility.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(call=>call.Expression.ToString()=="notices.UpdateNoticeVisibilityAsync" &&
            call.Ancestors().OfType<InvocationExpressionSyntax>().Any(outer=>outer.Expression.ToString()=="Locked")))
            violations.Add("Reviewed visibility change did not reach the notice owner under the Store writer gate");
        // Source wiring only: PREP table bodies cannot establish persisted behavior. Public scenarios run separately.
        string Body(string name) => methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText==name).ToString();
        if(source.Contains("PriorNoticeAsync",StringComparison.Ordinal)) violations.Add("Store guesses a prior slot outside the writer");
        if(!Body(nameof(IStore.CloseNoticeAsync)).Contains("if (matched) TouchNotice",StringComparison.Ordinal)) violations.Add("Close touch is not conditional on its match");
        var linkWrite=methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText==nameof(IStore.UpdateLinkMemberAsync));
        if (!linkWrite.DescendantNodes().OfType<IfStatementSyntax>().Any(branch=>branch.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(call=>call.Expression.ToString()=="notices.UpdateLinkMemberAsync") && branch.Statement.ToString().Contains("scan.Touch",StringComparison.Ordinal)))
            violations.Add("Link freshness is not conditional on the committed membership write");
        var commit=methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText==nameof(IStore.CommitAsync));
        if(commit.ExpressionBody?.Expression is not InvocationExpressionSyntax queue || queue.Expression.ToString()!="committer.QueueAsync" ||
            !queue.ArgumentList.Arguments.Any(a=>a.Expression is ParenthesizedLambdaExpressionSyntax callback && callback.Body.ToString()=="TouchChange(pairId, before, after)"))
            violations.Add("Public commit must queue the write and its scan notification together");
        var change=methods.OfType<MethodDeclarationSyntax>().Single(m=>m.Identifier.ValueText=="TouchChange");
        var marks=change.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c=>c.Expression.ToString()=="scan.Touch").ToArray();
        foreach (var (key,guard) in new[]{("old","old != null && old != first && old != last"),("first","first != null && first != last"),("last","last != null")})
        {
            var calls=marks.Where(c=>c.ArgumentList.Arguments[2].ToString()==key).ToArray();
            if(calls.Length!=1 || calls[0].ArgumentList.Arguments[0].ToString()!="pairId" || calls[0].ArgumentList.Arguments[1].ToString()!="side" ||
                calls[0].Ancestors().OfType<IfStatementSyntax>().FirstOrDefault()?.Condition.ToString()!=guard)
                violations.Add("Expected one guarded binding scan mark for " + key + "; found " + calls.Length);
        }
        // Each counted mark must carry this side's canonical key, never a second side's key.
        var mark=change.DescendantNodes().OfType<LocalFunctionStatementSyntax>().Single(l=>l.Identifier.ValueText=="Mark");
        foreach (var (name,owner) in new[]{("old","before"),("first","after"),("last","second")})
        {
            var init=mark.DescendantNodes().OfType<VariableDeclaratorSyntax>().Single(v=>v.Identifier.ValueText==name).Initializer?.Value.ToString();
            if(init!=owner+"?.KeyOn(side)") violations.Add("Binding mark "+name+" used a key other than this side's "+owner+": "+init);
        }
        if(marks.Length!=3 || !change.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c=>c.Expression.ToString()=="Mark")
            .Select(c=>c.ArgumentList.Arguments[0].ToString()).SequenceEqual(new[]{"Side.Local","Side.Dropbox"}) ||
            !Body("KeepBothAsync").Contains("TouchChange(pairId, before, first, second)",StringComparison.Ordinal))
            violations.Add("Binding publication must mark both sides of the same change");
        var resetText=reset.ToString();
        var begin=resetText.IndexOf("Connection.BeginTransaction(deferred:false)",StringComparison.Ordinal);
        var write=resetText.IndexOf("await pairs.ResetJournalAsync(tx,",StringComparison.Ordinal);
        if(!resetText.Contains("Locked(async",StringComparison.Ordinal) || begin<0 || write<begin || resetText.IndexOf("tx.Commit()",StringComparison.Ordinal)<write) violations.Add("Reset is not one gated immediate transaction committed after its write");
        var visibilityText=visibility?.ToString() ?? string.Empty;
        if(!visibilityText.Contains("var matched = await notices.UpdateNoticeVisibilityAsync(Connection, pairId, expected, ignored, ct)",StringComparison.Ordinal) || !visibilityText.Contains("if (matched) TouchNotice(pairId, expected.Path, expected.Subject)",StringComparison.Ordinal) || !visibilityText.Contains("return matched;",StringComparison.Ordinal)) violations.Add("Visibility discards the match or omits conditional freshness");
        // PREP table stubs cannot establish a durable outcome. They must not be reached on cancelled admission.
        await WithImage(async (store,_) =>
        {
            using var stop=new CancellationTokenSource(); stop.Cancel();
            var notice=new Notice(1,NoticeKind.Info,TextKey.InfoMovedEdited,"x",new([],null),DateTimeOffset.UnixEpoch,false);
            foreach(var call in new Func<Task>[] { ()=>store.ResetJournalAsync("42",new(1,2),"gap",stop.Token),
                ()=>store.UpdateNoticeVisibilityAsync("p",notice,true,stop.Token) })
            {
                try { await call(); violations.Add("Cancelled writer admission returned success"); }
                catch(OperationCanceledException e) when(e.CancellationToken==stop.Token) { }
                catch(NotImplementedException) { violations.Add("Cancelled writer admission reached the table"); }
            }
        });
        if(violations.Count!=0) throw new InvalidDataException(string.Join("; ",violations));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        foreach (var readOnly in new[] { true, false })
        {
            try { using var store = new Store(path, readOnly, null!); }
            catch (SqliteException) when (!File.Exists(path)) { continue; }
            throw new InvalidDataException("Opening an absent store did not fail without creating it.");
        }
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            if (await Store.OpenAsync(directory.FullName, false, null!, default) != null || directory.EnumerateFileSystemInfos().Any())
                throw new InvalidDataException("An ordinary open created store files.");
            var image = Path.Combine(directory.FullName, "sample.sqlite");
            var cs = new SqliteConnectionStringBuilder { DataSource = image, Pooling = false }.ToString();
            foreach (var other in new[] { Schema.Version - 1, Schema.Version + 1 })
            {
                using (var original=new SqliteConnection(cs)) { original.Open(); using var seed=original.CreateCommand(); seed.CommandText=$"PRAGMA journal_mode=DELETE; PRAGMA user_version={other};"; seed.ExecuteNonQuery(); }
                var bytes = File.ReadAllBytes(image);
                foreach (var ro in new[]{false,true})
                {
                    try { using var wrong = new Store(image,ro,null!); throw new Exception("Accepted incompatible version"); } catch(InvalidDataException) { }
                    if (!File.ReadAllBytes(image).SequenceEqual(bytes) || File.Exists(image+"-wal")) throw new InvalidDataException("Rejected store changed");
                }
                using var read = new SqliteConnection(cs); read.Open(); using var mode=read.CreateCommand(); mode.CommandText="PRAGMA journal_mode";
                if (!Equals(mode.ExecuteScalar(),"delete")) throw new InvalidDataException("Changed rejected journal mode");
            }
            using (var db = new SqliteConnection(cs))
            {
                db.Open();
                db.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic:true);
                using var sql = db.CreateCommand();
                sql.CommandText = Schema.Create + $"PRAGMA user_version={Schema.Version};INSERT INTO pair(pair_id,def_json) VALUES('p','{{}}');";
                sql.ExecuteNonQuery();
            }
            using (var store = new Store(image, false, SettingsParser.Load()))
                await Task.WhenAll(new[] { "A", "B" }.Select(key => Task.Run(() => store.CommitAsync("p",null,null,null,key,default))));
            await TraceStoreConnectionsAsync(image);
            using var inspect = new SqliteConnection(cs);
            inspect.Open();
            inspect.CreateFunction("pathkey", (string path) => PathNames.Key(path), isDeterministic:true);
            using var count = inspect.CreateCommand();
            count.CommandText = "SELECT count(*) FROM discovery_deleted_delta WHERE spent=1";
            if ((long)count.ExecuteScalar()! != 2) throw new InvalidDataException("Concurrent acknowledgements lost.");
        }
        finally { directory.Delete(recursive:true); }
        await MalformedPairIsolatedAsync();
    }
    private static async Task TraceStoreConnectionsAsync(string image)
    {
        var trace = Harness.SqlTrace.Attach();
        using (var store = new Store(image, false, SettingsParser.Load(), trace.Observe))
        {
            await store.SaveCursorAsync("p", "cursor", default);
            Check.Require(trace.TotalChanges == 1, "Writer change was not counted");
            await store.AddScanAsync("p", Guid.NewGuid(), Side.Local, OneRow(), null, default);
            Check.Require(trace.TotalChanges == 2, "Closed ingestion connection lost its row delta");
            var statements = trace.Statements;
            await foreach (var _ in store.NoticesAsync("p", default)) { }
            Check.Require(trace.Statements > statements && trace.TotalChanges == 2, "Read-only stream was not counted independently");
        }
        Check.Require(trace.TotalChanges == 2, "Closing the Store lost or duplicated writer changes");
        trace.Detach();

        static async IAsyncEnumerable<Listed> OneRow()
        {
            await Task.CompletedTask;
            yield return new("a", Scenarios.ItemChecks.File(Side.Local, "a", 1, "42"));
        }
    }

    // Only members without a documented interface rule need a local behavior phrase.
    private static string? MemberRule(string owner,string member) => (owner,member) switch
    {
        ("Store","Store")=>"transactional and streaming operations", ("Store",".ctor")=>"read-only access never creates one",
        ("Store","OpenAsync")=>"only explicit first-add may request staged creation", ("Store","Dispose")=>"Close the store connection",
        ("Store","PairsAsync")=>"Stream configured pairs", ("Store","PairAsync")=>"Read one pair by identifier",
        ("Store","ByIdAsync")=>"bindings sharing an endpoint identity", ("Store","DescendantsAsync")=>"Stream recorded descendants",
        ("Store","NoticesAsync")=>"Stream notices for a pair", ("Store","CloseNoticeAsync")=>"Close only the reviewed notice",
        ("Store","PositionAsync")=>"Read the journal boundary", ("Store","SavePositionAsync")=>"Save a settled volume boundary",
        ("Store","CursorAsync")=>"Read the ordinary change cursor", ("Store","SaveCursorAsync")=>"Save the settled ordinary change cursor",
        ("Store","OweRepairAsync")=>"Advance the Repair revision with its reason", ("Store","SaveJobAsync")=>"Persist the accepted job",
        ("Store","JobsAsync")=>"Read unsettled jobs", ("Store","SettleJobAsync")=>"Retire a settled job",
        ("Store","ArmAsync")=>"Persist the selected victim and stage proof", ("Store","ArmedAsync")=>"Read the armed victim and stage proof",
        ("Store","DisarmAsync")=>"Clear only the matching victim and proof", ("Store","ClearScanAsync")=>"Discard the scan and its unpublished cursor",
        ("Committer","Committer")=>"atomic updates to synced bindings", ("Committer","CommitAsync")=>"exact spent historical name in one transaction",
        ("Committer","CommitKeepBothAsync")=>"insert both surviving links, and publish their notice together", ("Committer","ProjectSubtreeAsync")=>"both mapped descendant ranges from the same frozen old rows",
        ("Encoding","Encoding")=>"compact column formats and rejects malformed stored objects",
        ("Encoding","DecodePair")=>"Reject malformed pair JSON", ("Encoding","DecodeSubject")=>"numeric-enum subject stored on an issue row",
        ("Encoding","DecodeMemberSubject")=>"v16 Version/Value issue-member subject", ("Encoding","Envelope")=>"v16 member envelope with stable enum names",
        ("Encoding","LocalId")=>"canonical integer ids without changing opaque identity strings", ("Encoding","IdFor")=>"Dropbox ids stay opaque",
        ("Encoding","Hash")=>"canonical lowercase hashes and preserves opaque strings", ("Encoding","ReadHash")=>"Decodes a packed hash",
        ("Encoding","ReadBool")=>"Persisted booleans are nonzero", ("Encoding","ReadTime")=>"UTC ticks as the zero-offset instant",
        ("Encoding","ReadSynced")=>"adjacent nullable journal/USN publication columns", ("Encoding","ReadEntry")=>"local projection has no stored revision, hash or shared-root flag",
        _=>null
    };
    private static string? Summary(MemberDeclarationSyntax method)
    {
        var docs=method.GetLeadingTrivia().Select(t=>t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>();
        if (docs.Any(d=>d.ContainsDiagnostics)) return null;
        var summaries=docs.SelectMany(d=>d.Content.OfType<XmlElementSyntax>()).Where(x=>x.StartTag.Name.ToString()=="summary").ToArray();
        if (summaries.Length!=1) return null;
        var words=string.Concat(summaries[0].DescendantNodes().OfType<XmlTextSyntax>().SelectMany(n=>n.TextTokens).Select(t=>t.ValueText)).Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        var text=string.Join(" ",words);
        return words.Length<2 ||
            text.TrimEnd('.').Equals("Not implemented",StringComparison.OrdinalIgnoreCase) ||
            text.Contains("operation on the supplied facts",StringComparison.OrdinalIgnoreCase) ||
            text.Contains("selected Store operation",StringComparison.OrdinalIgnoreCase) ? null : text;
    }
    /// <summary>Exercise folder freshness through the public binding commit; requires the real scan owner.</summary>
    public static Task RootCommitTouchesAsync() => WithImage(async (store,_) =>
    {
        var folder=new SyncedEntry("A","a","B","b",true,"pc",0,0,"cloud",string.Empty);
        await store.CommitAsync("p",null,folder,null,null,default);
        async Task CheckMove(SyncedEntry before,SyncedEntry? after,bool localTree,bool remoteTree)
        {
            var id=Guid.NewGuid(); store.BeginScan("p",id);
            await store.CommitAsync("p",before,after,null,null,default);
            foreach (var side in new[]{Side.Local,Side.Dropbox})
            {
                if (after==null || before.KeyOn(side)!=after.KeyOn(side))
                    Check.Require(await store.FindAsync("p",side,before.KeyOn(side),default)==null,"Root commit left the prior binding at its old key");
                if (after!=null) Check.Require(await store.FindAsync("p",side,after.KeyOn(side),default)==after,"Root commit did not persist the new binding");
            }
            foreach(var row in new[]{before,after}.OfType<SyncedEntry>())
                foreach(var side in new[]{Side.Local,Side.Dropbox})
                {
                    var path=row.PathOn(side);
                    Check.Require(store.ScanTouched("p",id,side,path,false),"Public commit missed a root");
                    Check.Require(store.ScanTouched("p",id,side,PathNames.Child(path,"child"),false)==(side==Side.Local?localTree:remoteTree),"Public commit marked the wrong descendants");
                }
        }
        var cased=folder with {LocalPath="A"}; await CheckMove(folder,cased,true,false);
        var moved=cased with {DropboxKey="C",DropboxPath="c"}; await CheckMove(cased,moved,false,true);
        var replaced=moved with {LocalId="replacement",LocalPath="a"}; await CheckMove(moved,replaced,true,true);
        await CheckMove(replaced,null,true,true);
    });

    /// <summary>Applied notice writes mark replaced and new members, never stale or unrelated slots; a paused notice reader does not block a checkpoint.</summary>
    public static async Task NoticeFreshnessAsync()
    {
        await NoticeWritesAsync();
        await PausedNoticeReaderAllowsCheckpointAsync();
    }

    private static Task NoticeWritesAsync() => WithImage(async (store,sql) =>
    {
        Notice At(NoticeKind kind,TextKey cause,string member,int tick,string path="file") => new(0,kind,cause,path,
            new([new(Side.Local,member,member)],null),DateTimeOffset.UnixEpoch.AddTicks(tick),false);
        var unrelated=At(NoticeKind.Info,TextKey.InfoMovedEdited,"c",0); var target=At(NoticeKind.Info,TextKey.InfoMovedTwice,"a",0);
        await store.SaveNoticeAsync("p",unrelated,default); await store.SaveNoticeAsync("p",target,default);
        async Task<Notice> Slot(Notice expected)
        {
            await foreach(var n in store.NoticesAtAsync("p",PathNames.Key(expected.Path),default))
                if(n.Cause==expected.Cause && n.Kind==expected.Kind) return n;
            throw new InvalidDataException("Missing expected notice slot");
        }
        var reviewed=await Slot(target); var otherSlot=await Slot(unrelated);
        var id=Guid.NewGuid(); store.BeginScan("p",id);
        var next=At(target.Kind,target.Cause,"b",1);
        await store.SaveNoticeAsync("p",next,default);
        var updated=await store.NoticeAsync("p",reviewed.Id,default);
        Check.Require(updated!=null && updated.Matches(next with {Id=reviewed.Id}) &&
            (await store.NoticeAsync("p",otherSlot.Id,default))?.Matches(otherSlot)==true,"Info replacement did not preserve both exact slots");
        bool Hit(string member)=>store.ScanTouched("p",id,Side.Local,member,false);
        Check.Require(Hit("a") && Hit("b") && !Hit("c"),"Upsert marked the wrong cause's members");
        id=Guid.NewGuid(); store.BeginScan("p",id);
        Check.Require(!await store.CloseNoticeAsync("p",reviewed,default),"Stale close reported a change");
        Check.Require(!Hit("a") && !Hit("b") && !Hit("c"),"Stale close published a phantom change");
        var current=await store.NoticeAsync("p",reviewed.Id,default) ?? throw new InvalidDataException("Stale close removed current notice");
        Check.Require(await store.CloseNoticeAsync("p",current,default),"Exact close reported no change"); Check.Require(Hit("b") && !Hit("c"),"Matching close did not mark exact subject");
        Check.Require(await store.NoticeAsync("p",current.Id,default)==null,"Exact close did not delete the notice");
        foreach (var kind in new[]{NoticeKind.RootIdentity, NoticeKind.DriveChange, NoticeKind.Failure})
        {
            var path=kind.ToString();
            var first=At(kind,TextKey.FailChanged,"before",2,path);
            var second=At(kind,TextKey.InfoMovedEdited,"after",3,path);
            await store.SaveNoticeAsync("p",first,default);
            var oldId=0L; await foreach(var item in store.NoticesAtAsync("p",PathNames.Key(path),default)) oldId=item.Id;
            id=Guid.NewGuid(); store.BeginScan("p",id);
            await store.SaveNoticeAsync("p",second,default);
            var count=0; await foreach(var item in store.NoticesAtAsync("p",PathNames.Key(path),default))
            {
                count++; Check.Require(item.Id==oldId && item.Cause==second.Cause,"Changed diagnosis must retain slot identity");
            }
            Check.Require(count==1 && Hit("before") && Hit("after") && !Hit("c"),"Changed diagnosis must touch exactly the displaced and written members");
        }
        var old=At(NoticeKind.PutBack,TextKey.FailChanged,"original",2); await store.SaveNoticeAsync("p",old,default);
        id=Guid.NewGuid(); store.BeginScan("p",id);
        await store.SaveNoticeAsync("p",At(NoticeKind.PutBack,TextKey.InfoMovedEdited,"new",3,"FILE"),default);
        Check.Require(Hit("original") && Hit("new"),"Cause-changing PutBack missed its displaced subject");
        var winner=await Slot(At(NoticeKind.PutBack,TextKey.InfoMovedEdited,"new",3,"FILE"));
        id=Guid.NewGuid(); store.BeginScan("p",id); await store.SaveNoticeAsync("p",old,default);
        async Task<bool> WinnerIntact()=> (await store.NoticeAsync("p",winner.Id,default))?.Matches(winner)==true;
        Check.Require(await WinnerIntact(),"Older PutBack overwrote the committed winner");
        Check.Require(!Hit("original") && !Hit("new"),"Older PutBack marked an unchanged slot");

        foreach (bool two in new[]{false,true})
        {
            string tag=two?"two":"one";
            SyncedEntry Binding(string name)=>new(PathNames.Key(name),name,PathNames.Key(name),name,false,name,1,2,name,"hash");
            async Task Keep(string operation,Notice n,bool abort=false)
            {
                var first=Binding(tag+operation); var second=Binding(first.LocalPath+"-peer");
                var prior=Binding(first.LocalPath+"-old") with {LocalPublished=new JournalPosition(12,34)};
                await store.CommitAsync("p",null,prior,prior.LocalPublished,null,default);
                id=Guid.NewGuid(); store.BeginScan("p",id);
                var legs=two?new[]{first,second}:new[]{first};
                if(abort)
                {
                    // Distinguish a notice failure after the binding writes from an earlier failure.
                    sql.CommandText=$"""
                        CREATE TRIGGER fail_keep_notice BEFORE INSERT ON issue WHEN new.path='abort-notice' BEGIN
                        SELECT CASE WHEN NOT EXISTS(SELECT 1 FROM synced_entry WHERE local_path='{prior.LocalPath}')
                          AND (SELECT count(*) FROM synced_entry WHERE local_path IN ('{first.LocalPath}','{second.LocalPath}'))={legs.Length}
                          THEN RAISE(ABORT,'failed after replacement') ELSE RAISE(ABORT,'wrong binding preimage') END;
                        END;
                        """;
                    sql.ExecuteNonQuery();
                }
                try
                {
                    if(two) await store.CommitKeepBothAsync("p",prior,first,second,n,default);
                    else await store.CommitKeepBothAsync("p",prior,first,n,default);
                    Check.Require(!abort,"Keep-both did not propagate the notice failure");
                }
                catch(SqliteException e) when(abort && e.SqliteErrorCode==19 && e.Message.Contains("failed after replacement",StringComparison.Ordinal)) { }
                finally { if(abort) { sql.CommandText="DROP TRIGGER fail_keep_notice"; sql.ExecuteNonQuery(); } }
                foreach(var side in new[]{Side.Local,Side.Dropbox})
                {
                    var oldRow=await store.FindAsync("p",side,prior.KeyOn(side),default);
                    if(abort) Check.Require(oldRow==prior,"Notice failure did not restore the old binding");
                    else Check.Require(oldRow==null,"Keep-both did not retire the old binding");
                    Check.Require(store.ScanTouched("p",id,side,prior.PathOn(side),false)==!abort,"Keep-both old binding touch did not match its commit");
                    foreach(var row in legs)
                        Check.Require(await store.FindAsync("p",side,row.KeyOn(side),default)==(abort?null:row) && store.ScanTouched("p",id,side,row.PathOn(side),false)==!abort,"Keep-both lost an independent binding or its touch");
                }
                if(abort)
                {
                    await foreach(var ignored in store.NoticesAtAsync("p",PathNames.Key(n.Path),default)) throw new InvalidDataException("Failed keep-both published a notice");
                    Check.Require(!Hit(n.Subject.Items.Single().Path),"Failed keep-both published notice freshness");
                }
            }
            var previous=At(NoticeKind.Info,TextKey.InfoMovedEdited,tag+"-before",4,tag);
            var replacement=At(previous.Kind,previous.Cause,tag+"-after",5,tag);
            await store.SaveNoticeAsync("p",previous,default); var priorId=(await Slot(previous)).Id;
            await Keep("write",replacement);
            Check.Require((await store.NoticeAsync("p",priorId,default))?.Matches(replacement with {Id=priorId})==true && Hit(tag+"-before") && Hit(tag+"-after"),tag+" keep-both lost the replaced notice or its touches");
            await Keep("refused",old);
            Check.Require(await WinnerIntact() && !Hit("original") && !Hit("new"),tag+" keep-both marked or replaced a refused notice");
            await Keep("abort",At(NoticeKind.Info,TextKey.InfoMovedEdited,tag+"-abort-subject",1,"abort-notice"),true);
        }
        var link=new NoticeItem(Side.Local,"link","id"); await store.UpdateLinkMemberAsync("p",link,true,default);
        id=Guid.NewGuid(); store.BeginScan("p",id); await store.UpdateLinkMemberAsync("p",link with {Id="wrong"},false,default);
        async Task<int> LinkCount() { await foreach(var n in store.NoticesAtAsync("p",string.Empty,default)) if(n.Cause==TextKey.InfoLinks) return n.LinkCount ?? 0; return 0; }
        Check.Require(!Hit(link.Path) && await LinkCount()==1,"Stale link retirement marked or removed the live member");
        await store.UpdateLinkMemberAsync("p",link,false,default); Check.Require(Hit(link.Path) && await LinkCount()==0,"Exact link retirement did not remove and mark the member");
        id=Guid.NewGuid(); store.BeginScan("p",id); await store.UpdateLinkMemberAsync("p",link,false,default);
        Check.Require(!Hit(link.Path) && await LinkCount()==0,"Missing link retirement marked a phantom change");
    });
    /// <summary>Public reset and visibility operations must publish only their committed outcomes.</summary>
    public static Task ResetVisibilityAsync() => WithImage(async (store,sql) =>
    {
        sql.CommandText="""
            UPDATE pair SET def_json='{"Roots":{"Local":{"VolumeSerialNumber":42}}}' WHERE pair_id='p';
            INSERT INTO pair(pair_id,def_json) VALUES('late','{"Roots":{"Local":{"VolumeSerialNumber":42}}}'),('outside','{"Roots":{"Local":{"VolumeSerialNumber":7}}}');
            """; sql.ExecuteNonQuery();
        var pos=new JournalPosition(3,7); await store.ResetJournalAsync("42",pos,"gap",default);
        Check.Require(await store.PositionAsync("42",default)==pos && await store.ReadRepairAsync("p",Side.Local,default)==(1L,"gap") &&
            await store.ReadRepairAsync("late",Side.Local,default)==(1L,"gap") && await store.ReadRepairAsync("outside",Side.Local,default)==(0L,null),"Cursor and durable debts did not commit together");
        sql.CommandText="CREATE TRIGGER fail_gap BEFORE UPDATE ON source_state BEGIN SELECT RAISE(ABORT,'synthetic gap failure'); END;"; sql.ExecuteNonQuery();
        try { await store.ResetJournalAsync("42",new(8,9),"next",default); throw new InvalidDataException("Reset did not propagate debt failure"); }
        catch(SqliteException e) when (e.SqliteErrorCode==19) { }
        Check.Require(await store.PositionAsync("42",default)==pos && await store.ReadRepairAsync("p",Side.Local,default)==(1L,"gap") && await store.ReadRepairAsync("late",Side.Local,default)==(1L,"gap"),"Failed reset leaked cursor or debt");
        var notice=new Notice(0,NoticeKind.Info,TextKey.InfoMovedEdited,"file",new([new(Side.Local,"member","id")],null),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync("p",notice,default);
        var saved=await store.NoticeAsync("p",1,default) ?? throw new InvalidDataException();
        var id=Guid.NewGuid(); store.BeginScan("p",id);
        Check.Require(!await store.UpdateNoticeVisibilityAsync("p",saved with {At=saved.At.AddTicks(1)},true,default) && !store.ScanTouched("p",id,Side.Local,"member",false),"Stale visibility marked a member or returned true");
        Check.Require((await store.NoticeAsync("p",saved.Id,default))?.Ignored==false,"Stale visibility modified persisted state");
        Check.Require(await store.UpdateNoticeVisibilityAsync("p",saved,true,default) && store.ScanTouched("p",id,Side.Local,"member",false) && (await store.NoticeAsync("p",saved.Id,default))?.Ignored==true,"Matched visibility lost its result, write or touch");
        id=Guid.NewGuid(); store.BeginScan("p",id);
        Check.Require(await store.UpdateNoticeVisibilityAsync("p",saved,false,default) && store.ScanTouched("p",id,Side.Local,"member",false) && (await store.NoticeAsync("p",saved.Id,default))?.Ignored==false,"Unignore did not restore visibility and publish its mark");
    });

    private static Task MalformedPairIsolatedAsync() => WithImage(async (store, sql) =>
    {
        var valid = new PairConfig("healthy", "Example", "left", "/right", "connection", [], [], FirstRunPolicy.NewestWins, true, null);
        await store.SavePairAsync(valid, default);
        sql.CommandText = """UPDATE pair SET def_json='{"x":1}' WHERE pair_id='p'""";
        sql.ExecuteNonQuery();
        for (var pass = 0; pass < 2; pass++)
        {
            var ids = new List<string>();
            await foreach (var pair in store.PairsAsync(default)) ids.Add(pair.PairId);
            Check.Require(ids.SequenceEqual([valid.PairId]), "Malformed pair prevented healthy pair enumeration");
            var failures = new List<Notice>();
            await foreach (var notice in store.NoticesAsync("p", default)) failures.Add(notice);
            Check.Require(failures is [{ Kind: NoticeKind.Failure, Cause: TextKey.StateRootBindingInvalid }], "Malformed pair needs exactly one failure notice");
        }
        Check.Require(await store.RemovePairAsync("p", default) == null, "Malformed pair could not be removed");
        sql.CommandText = "SELECT count(*) FROM pair WHERE pair_id='p'";
        Check.Require((long)sql.ExecuteScalar()! == 0, "Malformed pair row survived removal");
    });

    private static Task PausedNoticeReaderAllowsCheckpointAsync() => WithImage(async (store, sql) =>
    {
        foreach (var path in new[] { "first", "second" })
            await store.SaveNoticeAsync("p", new(0, NoticeKind.Info, TextKey.InfoMovedEdited, path, new([], null), DateTimeOffset.UnixEpoch, false), default);
        await using var notices = store.NoticesAsync("p", default).GetAsyncEnumerator();
        Check.Require(await notices.MoveNextAsync() && notices.Current.Path == "first", "Expected first notice before checkpoint");
        var entry = new SyncedEntry("A", "a", "A", "a", false, "42", 0, 0, "remote", "opaque");
        await store.CommitAsync("p", null, entry, null, null, default);
        sql.CommandText = "PRAGMA wal_checkpoint(PASSIVE)";
        using (var result = sql.ExecuteReader())
        {
            Check.Require(result.Read(), "No checkpoint result");
            var frames = result.GetInt32(result.GetOrdinal("log"));
            var copied = result.GetInt32(result.GetOrdinal("checkpointed"));
            Check.Require(frames > 0 && copied == frames, $"Paused notice reader checkpointed {copied} of {frames} WAL frames");
        }
        Check.Require(await notices.MoveNextAsync() && notices.Current.Path == "second" && !await notices.MoveNextAsync(), "Paused notice reader lost or repeated its remaining row");
    });

    private static async Task WithImage(Func<IStore,SqliteCommand,Task> run)
    {
        var dir=Directory.CreateTempSubdirectory();
        try
        {
            var path=Path.Combine(dir.FullName,"fixture.sqlite");
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path,Pooling=false }.ToString()); db.Open();
            db.CreateFunction("pathkey",(string path)=>PathNames.Key(path),isDeterministic:true);
            using var cmd=db.CreateCommand(); cmd.CommandText=Schema.Create+$"PRAGMA user_version={Schema.Version};INSERT INTO pair(pair_id,def_json) VALUES('p','{{}}');"; cmd.ExecuteNonQuery();
            using var store=new Store(path,false,SettingsParser.Load()); await run(store,cmd);
        }
        finally { dir.Delete(recursive:true); }
    }
}
