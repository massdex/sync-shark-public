using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>One Windows file bound at a second name owes every name's file scope in that binding's transaction and signals once after commit.</summary>
public static class BindingNameChecks
{
    private static SyncedEntry Bind(string path,string localId,string dropboxId,bool folder=false) =>
        new(PathNames.Key(path),path,PathNames.DropboxKey(path),path,folder,localId,1,1,dropboxId,folder?"":"hash");

    private static Scope[] Names(params string[] paths) => paths.Select(p=>new Scope(p,p)).ToArray();

    private static bool SameSet(IReadOnlyList<Scope>? owed,Scope[] expected) => (owed ?? []).ToHashSet().SetEquals(expected) && (owed?.Count ?? 0)==expected.Length;

    // A second connection proves what is durable, independently of the store's own connection and caches.
    private static (long Bound,IReadOnlyList<Scope>? Owed) Durable(string root,string pairId,params string[] localIds)
    {
        var image=Path.Combine(root,ResourceNames.EngineFolder,ResourceNames.DatabaseFile);
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=image,Pooling=false}.ToString());
        db.Open();
        using var cmd=db.CreateCommand();
        long bound=0;
        foreach(var id in localIds)
        {
            cmd.CommandText="SELECT count(*) FROM synced_entry WHERE local_id=$id"; cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$id",Massdex.SyncShark.Engine.Storage.Encoding.LocalId(id)!);
            bound+=(long)cmd.ExecuteScalar()!;
        }
        cmd.CommandText="SELECT def_json FROM pair WHERE pair_id=$p"; cmd.Parameters.Clear(); cmd.Parameters.AddWithValue("$p",pairId);
        return (bound,Massdex.SyncShark.Engine.Storage.Encoding.DecodePair((string)cmd.ExecuteScalar()!).OwedScopes);
    }

    private static async Task WithStoreAsync(Func<string,PairConfig,Store,Task> run,Func<PairConfig,Store,Task>? reopened=null)
    {
        var root=Directory.CreateTempSubdirectory();
        try
        {
            var pair=ItemChecks.Pair();
            using(var store=await Store.OpenAsync(root.FullName,true,ItemChecks.Settings,default) ?? throw new InvalidDataException("Could not open the disposable Store"))
            {
                await store.SavePairAsync(pair,default);
                await store.FinishRepairAsync(pair.PairId,(0,await store.OweRepairAsync(pair.PairId,Side.Dropbox,"requested",default)),"{}",default);
                await run(root.FullName,pair,store);
            }
            if(reopened==null) return;
            using var again=await Store.OpenAsync(root.FullName,false,ItemChecks.Settings,default) ?? throw new InvalidDataException("Could not reopen the disposable Store");
            await reopened(pair,again);
        }
        finally { SqliteConnection.ClearAllPools(); root.Delete(true); }
    }

    /// <summary>S1: a second name of one file owes both names once, in either commit order, and survives a reopen.</summary>
    public static async Task SecondNameOwesBothAsync()
    {
        foreach(var (first,second) in new[]{("a","b"),("b","a")})
            await WithStoreAsync(async (root,pair,store)=>
            {
                var signals=new List<(string Pair,long Bound,IReadOnlyList<Scope>? Owed)>();
                ((IStore)store).ScopesOwed=id=>{ var (bound,owed)=Durable(root,pair.PairId,"7"); signals.Add((id,bound,owed)); };
                await store.CommitAsync(pair.PairId,null,Bind(first,"7","d-"+first),null,null,default);
                Check.Require(signals.Count==0,"A file's only name owed or signalled");
                var before=(await store.PairAsync(pair.PairId,default))!;
                var raw=(await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Revision; var net=(await store.ReadFullRepairAsync(pair.PairId,default)).Revision;
                await store.CommitAsync(pair.PairId,null,Bind(second,"7","d-"+second),null,null,default);
                var after=(await store.PairAsync(pair.PairId,default))!;
                Check.Require(SameSet(after.OwedScopes,Names("a","b")) && after.OwedScopesRevision==before.OwedScopesRevision+1,$"Order {first},{second}: the second name did not owe both names with one owed-scope step");
                Check.Require((await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Revision==raw+1 && (await store.ReadFullRepairAsync(pair.PairId,default)).Revision==net,$"Order {first},{second}: the raw Dropbox revision did not step once or the net full revision moved");
                Check.Require(signals.Count==1 && signals[0].Pair==pair.PairId && signals[0].Bound==2 && SameSet(signals[0].Owed,Names("a","b")),$"Order {first},{second}: not exactly one signal after the binding and its debt were durable");
            },async (pair,store)=>
            {
                Check.Require(SameSet((await store.PairAsync(pair.PairId,default))!.OwedScopes,Names("a","b")),"The owed names did not survive a reopen");
                Check.Require(await store.FindAsync(pair.PairId,Side.Local,PathNames.Key(second),default) is {LocalId:"7"},"The second binding did not survive a reopen");
            });
    }

    /// <summary>S1t: a throwing subscriber reaches the caller after the binding, its debt and the scan touch are durable.</summary>
    public static Task ThrowingSubscriberAsync() => WithStoreAsync(async (root,pair,store)=>
    {
        await store.CommitAsync(pair.PairId,null,Bind("a","7","d-a"),null,null,default);
        var scan=Guid.NewGuid(); store.BeginScan(pair.PairId,scan);
        ((IStore)store).ScopesOwed=_=>throw new InvalidDataException("subscriber fault");
        try { await store.CommitAsync(pair.PairId,null,Bind("b","7","d-b"),null,null,default); throw new InvalidOperationException("The subscriber's fault was swallowed"); }
        catch(InvalidDataException e) when(e.Message=="subscriber fault") { }
        var (bound,owed)=Durable(root,pair.PairId,"7");
        Check.Require(bound==2 && SameSet(owed,Names("a","b")),"A subscriber fault lost the binding or its debt");
        Check.Require(store.ScanTouched(pair.PairId,scan,Side.Local,"b",false) && !store.ScanTouched(pair.PairId,scan,Side.Local,"untouched",false),"A subscriber fault skipped the scan touch");
    });

    /// <summary>S4: same-id edits and moves, folders, unknown ids and different ids owe nothing and signal nothing.</summary>
    public static Task NoSecondNameAsync() => WithStoreAsync(async (root,pair,store)=>
    {
        var signals=0; ((IStore)store).ScopesOwed=_=>signals++;
        var before=(await store.PairAsync(pair.PairId,default))!;
        var raw=(await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Revision;
        var a=Bind("a","7","d-a"); await store.CommitAsync(pair.PairId,null,a,null,null,default);
        var edited=a with {LocalSize=2}; await store.CommitAsync(pair.PairId,a,edited,null,null,default);
        await store.CommitAsync(pair.PairId,edited,edited with {LocalPath="c",LocalKey=PathNames.Key("c")},null,null,default);
        await store.CommitAsync(pair.PairId,null,Bind("f","8","d-f",true),null,null,default);
        await store.CommitAsync(pair.PairId,null,Bind("g","8","d-g",true),null,null,default);
        foreach(var unknown in new[]{"","0"})
        {
            await store.CommitAsync(pair.PairId,null,Bind("u1"+unknown,unknown,"d-u1"+unknown),null,null,default);
            await store.CommitAsync(pair.PairId,null,Bind("u2"+unknown,unknown,"d-u2"+unknown),null,null,default);
        }
        await store.CommitAsync(pair.PairId,null,Bind("x","9","d-x"),null,null,default);
        await store.CommitAsync(pair.PairId,null,Bind("y","10","d-y"),null,null,default);
        var after=(await store.PairAsync(pair.PairId,default))!;
        Check.Require(signals==0 && (after.OwedScopes?.Count ?? 0)==0 && after.OwedScopesRevision==before.OwedScopesRevision && (await store.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Revision==raw,
            "An install without a second name of one known file owed, signalled or stepped a revision");
    });

    /// <summary>S6: a keep-both second link sharing another binding's file signals once after commit; a kept relink signals nothing.</summary>
    public static Task KeepBothTwinAsync() => WithStoreAsync(async (root,pair,store)=>
    {
        var signals=new List<(long Bound,IReadOnlyList<Scope>? Owed)>();
        ((IStore)store).ScopesOwed=_=>signals.Add(Durable(root,pair.PairId,"5","7"));
        await store.CommitAsync(pair.PairId,null,Bind("z","7","d-z"),null,null,default);
        var a=Bind("a","5","d-a"); await store.CommitAsync(pair.PairId,null,a,null,null,default);
        var copy=Bind("a copy","7","d-copy");
        var notice=new Notice(0,NoticeKind.Info,TextKey.InfoKeptBoth,"a",new([],null),DateTimeOffset.UnixEpoch,false);
        await store.CommitKeepBothAsync(pair.PairId,a,a,copy,notice,default);
        Check.Require(signals.Count==1 && signals[0].Bound==3 && SameSet(signals[0].Owed,Names("z","a copy")),"A keep-both twin link did not signal exactly once after both links and the debt were durable");
        await store.FollowKeepAsync(pair.PairId,copy,copy with {LocalPath="a copy 2",LocalKey=PathNames.Key("a copy 2")},null,default);
        Check.Require(signals.Count==1,"A kept relink of the same file signalled");
    });
}
