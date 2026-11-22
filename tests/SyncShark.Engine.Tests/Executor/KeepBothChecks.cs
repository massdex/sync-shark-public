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
        var row = new Row(a.Path, a, b, null, [], a.ContentHash, RowContext.Neutral);
        var outcome = await new KeepBoth().ApplyAsync(pair, settings, _ => {}, new(pair, [pair], DriveType.Fixed), row,
            local, cloud, store, (_,_,_,_) => throw new InvalidOperationException(), default);
        Check.Require(cloud.Items.Any(x => x.Key.Contains("Test-PC", StringComparison.Ordinal) && x.Value.Bytes.SequenceEqual(new byte[]{1})), "Local version must carry the observed PC name at the Dropbox destination");
        Check.Require(local.Items.Any(x => x.Key.Contains("Dropbox", StringComparison.Ordinal) && x.Value.Bytes.SequenceEqual(new byte[]{2})), "Dropbox version must keep its provider label");
        var pcLabel = local.Items.Values.Single(x=>x.Entry.Id!=a.Id).Entry;
        var cloudLabel = cloud.Items.Values.Single(x=>x.Entry.Id!=b.Id).Entry;
        var links = new[]{SyncedEntries.From(a,cloudLabel),SyncedEntries.From(pcLabel,b)};
        var notice = store.OpenNotices.Single();
        var members = new HashSet<NoticeItem>{new(Side.Local,a.Path,a.Id),new(Side.Dropbox,b.Path,b.Id),new(Side.Local,pcLabel.Path,pcLabel.Id),new(Side.Dropbox,cloudLabel.Path,cloudLabel.Id)};
        Check.Require(outcome==EffectOutcome.Applied && links.ToHashSet().SetEquals(store.Records.Values) && store.Records.Count==2,"Kept endpoints linked to the wrong versions");
        Check.Require(notice.Kind==NoticeKind.Info && notice.Cause==TextKey.InfoKeptBoth && notice.Path==a.Path && notice.Subject.Items.Count==4 && members.SetEquals(notice.Subject.Items),"Kept-both notice did not name the four actual endpoints");
        foreach (var receiving in new[]{Side.Local,Side.Dropbox})
        {
            var onlyPc=new FakeSide(Side.Local); var onlyDb=new FakeSide(Side.Dropbox); using var onlyStore=new InMemoryStateStore();
            onlyPc.Seed(a,[1]);onlyDb.Seed(b,[2]);
            var one=await new KeepBoth().ApplyAsync(pair,settings,_=>{},new(pair,[pair],DriveType.Fixed),row,onlyPc,onlyDb,onlyStore,(_,_,_,_)=>throw new InvalidOperationException(),default,receiving);
            var receivingEndpoint=receiving==Side.Local?onlyPc:onlyDb;
            var label=receivingEndpoint.Items.Values.Single(x=>x.Entry.Path!=a.Path).Entry;
            var expected=receiving==Side.Local?SyncedEntries.From(label,b):SyncedEntries.From(a,label);
            var three=new HashSet<NoticeItem>{new(Side.Local,a.Path,a.Id),new(Side.Dropbox,b.Path,b.Id),new(receiving,label.Path,label.Id)};
            var oneNotice=onlyStore.OpenNotices.Single();
            Check.Require(one==EffectOutcome.Applied && onlyStore.Records.Values.Single()==expected && oneNotice.Cause==TextKey.InfoKeptBoth && oneNotice.Subject.Items.Count==3 && three.SetEquals(oneNotice.Subject.Items),"Single receiving side persisted the wrong link or notice members");
        }
    }
}
