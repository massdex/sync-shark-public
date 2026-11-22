using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Exercise label selection and the atomic settlement with two different file versions.</summary>
public static class KeepBothChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair();
        var settings = ItemChecks.Settings with { ComputerName = "Test-PC" };
        var local = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        var a = ItemChecks.File(Side.Local, "report.txt", 1, "a");
        var b = ItemChecks.File(Side.Dropbox, a.Path, 2, "b");
        local.Seed(a, [1]); cloud.Seed(b, [2]);
        using var store = new InMemoryStateStore();
        var previous=SyncedEntries.From(a,b);
        await store.CommitAsync(pair.PairId,null,previous,null,null,default);
        var row = new Row(a.Path, a, b, previous, [], a.ContentHash, RowContext.Neutral);
        var outcome = await new KeepBoth().ApplyAsync(pair, settings, _ => {}, new(pair, [pair], DriveType.Fixed), row,
            local, cloud, store, (_,_,_,_) => throw new InvalidOperationException(), default);
        const string pcName="report [Dropbox version 1 Jan 00.00].txt", dbName="report [Local version Test-PC 1 Jan 00.00].txt";
        Check.Require(local.Items.Count==2 && cloud.Items.Count==2 && local.Items[a.Path].Entry==a && cloud.Items[b.Path].Entry==b && local.Items[a.Path].Bytes.SequenceEqual(new byte[]{1}) && cloud.Items[b.Path].Bytes.SequenceEqual(new byte[]{2}),"Originals changed during keep-both");
        var pcLabel=local.Items[pcName].Entry; var cloudLabel=cloud.Items[dbName].Entry;
        Check.Require(pcLabel.Path==pcName && cloudLabel.Path==dbName && local.Items[pcName].Bytes.SequenceEqual(new byte[]{2}) && cloud.Items[dbName].Bytes.SequenceEqual(new byte[]{1}) && !store.Records.Values.Contains(previous),"Wrong label spelling, bytes, or stale original link");
        var links = new[]{SyncedEntries.From(a,cloudLabel),SyncedEntries.From(pcLabel,b)};
        var notice = store.OpenNotices.Single();
        var members = new HashSet<NoticeItem>{new(Side.Local,a.Path,a.Id),new(Side.Dropbox,b.Path,b.Id),new(Side.Local,pcLabel.Path,pcLabel.Id),new(Side.Dropbox,cloudLabel.Path,cloudLabel.Id)};
        Check.Require(outcome==EffectOutcome.Applied && links.ToHashSet().SetEquals(store.Records.Values) && store.Records.Count==2,"Kept endpoints linked to the wrong versions");
        Check.Require(notice.Kind==NoticeKind.Info && notice.Cause==TextKey.InfoKeptBoth && notice.Path==a.Path && notice.Subject.Items.Count==4 && members.SetEquals(notice.Subject.Items),"Kept-both notice did not name the four actual endpoints");
        foreach (var receiving in new[]{Side.Local,Side.Dropbox})
        {
            var onlyPc=new FakeSide(Side.Local); var onlyDb=new FakeSide(Side.Dropbox); using var onlyStore=new InMemoryStateStore();
            onlyPc.Seed(a,[1]);onlyDb.Seed(b,[2]); await onlyStore.CommitAsync(pair.PairId,null,previous,null,null,default);
            var legEvents=new List<RunEvent>();
            var one=await new KeepBoth().ApplyAsync(pair,settings,legEvents.Add,new(pair,[pair],DriveType.Fixed),row,onlyPc,onlyDb,onlyStore,(_,_,_,_)=>throw new InvalidOperationException(),default,receiving);
            var receivingEndpoint=receiving==Side.Local?onlyPc:onlyDb;
            var label=receivingEndpoint.Items.Values.Single(x=>x.Entry.Path!=a.Path).Entry;
            var excluded=receiving==Side.Local?onlyDb:onlyPc;
            var name=receiving==Side.Local?pcName:dbName;
            Check.Require(label.Path==name && receivingEndpoint.Items.Count==2 && excluded.Items.Count==1 && receivingEndpoint.Items[name].Bytes.SequenceEqual(new byte[]{receiving==Side.Local?(byte)2:(byte)1}),"Wrong one-sided label, extra label or bytes");
            Check.Require(onlyPc.Items[a.Path].Entry==a && onlyDb.Items[b.Path].Entry==b && onlyPc.Items[a.Path].Bytes.SequenceEqual(new byte[]{1}) && onlyDb.Items[b.Path].Bytes.SequenceEqual(new byte[]{2}) && !onlyStore.Records.Values.Contains(previous),"One-sided conflict changed an original or retained old link");
            Check.Require(excluded.Requests[nameof(ISide.DeleteAsync)]==0 && excluded.Requests[nameof(ISide.MoveAsync)]==0 && excluded.Requests[nameof(ISide.EnsureFolderAsync)]==0 && excluded.Requests[nameof(ISide.StageAsync)]==0 && excluded.Requests[nameof(ISide.PublishAsync)]==0 && excluded.Requests[nameof(ISide.CreateFolderExclusiveAsync)]==0 && excluded.Requests[nameof(ISide.ReadOneAsync)]==1 && receivingEndpoint.Requests[nameof(ISide.OpenReadAsync)]==0,"An omitted conflict leg still queried or transferred a label");
            Check.Require(legEvents.Count==1 && legEvents[0].Kind==RunEventKind.Effect && legEvents[0].Side==receiving && legEvents[0].Path==name && legEvents[0].Detail==a.Path,"Conflict emitted an extra event or attributed the omitted leg");
            var expected=receiving==Side.Local?SyncedEntries.From(label,b):SyncedEntries.From(a,label);
            var three=new HashSet<NoticeItem>{new(Side.Local,a.Path,a.Id),new(Side.Dropbox,b.Path,b.Id),new(receiving,label.Path,label.Id)};
            var oneNotice=onlyStore.OpenNotices.Single();
            Check.Require(one==EffectOutcome.Applied && onlyStore.Records.Values.Single()==expected && oneNotice.Path==a.Path && oneNotice.Kind==NoticeKind.Info && oneNotice.Cause==TextKey.InfoKeptBoth && oneNotice.Subject.Items.Count==3 && three.SetEquals(oneNotice.Subject.Items),"Single receiving side persisted the wrong link or notice members");
        }
    }
}
