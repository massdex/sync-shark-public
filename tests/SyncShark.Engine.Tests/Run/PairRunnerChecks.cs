using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>Only the original pair of roots can clear a binding refusal.</summary>
public static class PairRunnerChecks
{
    public static async Task RunAsync()
    {
        using var store = new InMemoryStateStore();
        var pair = ItemChecks.Pair();
        var roots = new RootIdentity(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,2,null),new("account","root","home","folder"));
        pair = pair with {Roots=roots,Drive=new(1,2,3)};
        await store.SavePairAsync(pair,default);
        var runner = new PairRunner();
        var changed = roots with {Dropbox=roots.Dropbox with {FolderId="other"}};
        Check.Require(!await runner.CheckRootsAsync(pair,changed,pair.Drive,store,default),"Different folder admitted at the saved path");
        Check.Require(store.OpenNotices.Single().Cause == TextKey.RootDropboxFolderChanged && (await store.PairAsync(pair.PairId,default))!.Roots==roots,"Mismatch replaced the binding or lost its refusal");
        Check.Require(await runner.CheckRootsAsync(pair,roots,pair.Drive,store,default) && !store.OpenNotices.Any(),"Matching identities did not clear the root refusal");
        Check.Require(!await runner.CheckRootsAsync(pair,changed,new(2,2,3),store,default) && store.OpenNotices.Single().Kind==NoticeKind.DriveChange,"Drive change did not outrank folder mismatch");
        var wrong=new List<string>();
        using var db=new InMemoryStateStore(); var calls=new List<PairConfig>();
        var local=new FakeSide(Side.Local){Root=(roots.Local,null,pair.Drive,DriveType.Fixed)};
        var remote=new FakeSide(Side.Dropbox){Root=(null,roots.Dropbox,null,null)};
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(p,_,_,_)=>{calls.Add(p);return(local,remote);},
            ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        await host.AddPairAsync(pair with {Enabled=false,Roots=null},default);
        if(calls.Count<2 || calls[^1].Roots!=roots) wrong.Add("first add retained its unbound adapters");
        try {await host.RepairAsync(pair.PairId,default); wrong.Add("disabled pair ran Repair");}
        catch(EngineRequestException e) when(e.Key==TextKey.PairDisabled) { }
        catch(NotImplementedException) {wrong.Add("disabled pair reached Repair");}
        var configured=await db.PairAsync(pair.PairId,default); await db.SavePairAsync(configured! with {Enabled=true},default);
        try {await host.RepairAsync(pair.PairId,default);} catch(NotImplementedException) { /* The request must already be durable before execution. */ }
        if((await db.ReadRepairAsync(pair.PairId,Side.Dropbox,default)).Reason==null) wrong.Add("requested Repair not durable");
        var cloudFailure=System.Reflection.DispatchProxy.Create<ISide,HostChecks.JournalProbe>(); var probe=(HostChecks.JournalProbe)(object)cloudFailure;
        probe.Local=remote; probe.FailRoot=true; local.Root=(roots.Local,null,new(2,5,9),DriveType.Fixed);
        var gated=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>(local,cloudFailure),ItemChecks.Settings,null!,TimeProvider.System,
            _=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        try {await gated.BindAsync(pair.PairId,default);} catch(EngineRequestException) { }
        if(!db.OpenNotices.Any(n=>n.Kind==NoticeKind.DriveChange)) wrong.Add("Dropbox root failure hid the observed local drive change");
        Check.Require(wrong.Count==0,string.Join("; ",wrong));
    }
    /// <summary>Returned scope uses the actual executor parent mapping.</summary>
    public static async Task ReturnedScopeAsync()
    {
        using var db=new InMemoryStateStore();var pair=ItemChecks.Pair();
        var host=new Host((_,_)=>Task.FromResult<IStore?>(db),(_,_,_,_)=>throw new InvalidOperationException(),ItemChecks.Settings,null!,TimeProvider.System,_=>Task.FromResult(""),(_,_)=>Task.CompletedTask);
        var owner=pair with {PairId="outer",LocalRoot=@"T:\Outer",DropboxRoot="/Outer"};
        var inner=pair with {PairId="inner",LocalRoot=@"T:\Outer\Parent\Inner",DropboxRoot="/Elsewhere"};
        await db.SavePairAsync(owner,default); await db.SavePairAsync(inner,default);
        var pc=ItemChecks.File(Side.Local,"Parent",0,"local-parent",true);var cloud=ItemChecks.File(Side.Dropbox,"Mapped",0,"db-parent",true);
        await db.CommitAsync(owner.PairId,null,SyncedEntries.From(pc,cloud),null,null,default);
        await host.RemovePairAsync(inner.PairId,default);
        Check.Require((await db.PairAsync(owner.PairId,default))?.OwedScopes?.Contains(new("Parent/Inner","Mapped/Inner"))==true,"returned scope ignored saved parent mapping");
        await host.StopAsync(default);
    }
    /// <summary>Changing a root or drive diagnosis replaces its exact persistent slot.</summary>
    public static async Task DiagnosisSlotAsync()
    {
        var directory=Directory.CreateTempSubdirectory();
        try
        {
            var image=Path.Combine(directory.FullName,"fixture.sqlite");
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder{DataSource=image,Pooling=false}.ToString()))
            {
                db.Open();db.CreateFunction("pathkey",(string path)=>PathNames.Key(path),isDeterministic:true);
                using var create=db.CreateCommand();create.CommandText=Engine.Storage.Schema.Create+$"PRAGMA user_version={Engine.Storage.Schema.Version}";create.ExecuteNonQuery();
            }
            using var store=new Engine.Storage.Store(image,false,ItemChecks.Settings);var pair=ItemChecks.Pair();
            var roots=new RootIdentity(pair.LocalRoot,pair.DropboxRoot,pair.ConnectionId,new(1,2,null),new("account","root","home","folder"));
            pair=pair with {Roots=roots,Drive=new(1,2,30)};await store.SavePairAsync(pair,default);
            var runner=new PairRunner();
            async Task<List<Notice>> Read(){var rows=new List<Notice>();await foreach(var n in store.NoticesAtAsync(pair.PairId,string.Empty,default)) rows.Add(n);return rows;}
            async Task<Notice> Refused(RootIdentity? observed,DriveEvidence? drive,NoticeKind kind,TextKey cause)
            {
                Check.Require(!await runner.CheckRootsAsync(pair,observed,drive,store,default),"Changed root or drive was admitted");
                var rows=await Read();Check.Require(rows.Count==1 && rows[0].Kind==kind && rows[0].Cause==cause,"Expected exactly one row with the current diagnosis");return rows[0];
            }
            var first=await Refused(null,pair.Drive,NoticeKind.RootIdentity,TextKey.RootUnavailable);
            var next=await Refused(roots with {Local=new(1,3,null)},pair.Drive,NoticeKind.RootIdentity,TextKey.RootLocalIdentityChanged);
            Check.Require(first.Id==next.Id && await runner.CheckRootsAsync(pair,roots,pair.Drive,store,default) && (await Read()).Count==0,"Root diagnosis was not updated in place and cleared by one successful bind");
            first=await Refused(roots,new(2,2,30),NoticeKind.DriveChange,TextKey.DriveChanged);
            next=await Refused(roots,new(1,2,20),NoticeKind.DriveChange,TextKey.DriveRestored);
            Check.Require(first.Id==next.Id && await store.ResolveDriveAsync(pair with {Drive=new(1,2,20)},next,ResolutionChoice.SyncNormally,default) && (await Read()).Count==0,"One exact drive resolution did not clear the replacement diagnosis");
        }
        finally {directory.Delete(true);}
    }
}
