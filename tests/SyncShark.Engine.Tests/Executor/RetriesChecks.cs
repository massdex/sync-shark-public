using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.Scenarios;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Retry takes the last held revision; an occupied destination retains the notice and input.</summary>
public static class RetriesChecks
{
    public static async Task RunAsync()
    {
        var pair = ItemChecks.Pair(); var paths = new PathScope(pair, [pair], DriveType.Fixed);
        var pc = new FakeSide(Side.Local); var cloud = new FakeSide(Side.Dropbox);
        using var store = new InMemoryStateStore(); const string path = "parent/item";
        var first = ItemChecks.File(Side.Dropbox, path, 1, "child");
        var last = ItemChecks.File(Side.Dropbox, path, 2, first.Id); cloud.Seed(last, [2]);
        var notice = new Notice(1, NoticeKind.PutBack, TextKey.FailLocalInUse, path,
            new([new(Side.Dropbox, path, last.Id)], Side.Dropbox), DateTimeOffset.UnixEpoch, false);
        await store.SaveNoticeAsync(pair.PairId, notice, default);
        var consumed = new List<string>();
        Change Held(Entry e) => new Change(Side.Dropbox, ChangeKind.Changed,path,null,e.Id,e,false,false)
            { ConsumeHeld = () => consumed.Add(e.Revision) };
        Change[] input = [Held(first), Held(last)];
        Task Retry() => new Retries().RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,cloud,store,
            scope => scope == new Scope(path,path) ? input : [], (_,_,_,_)=>throw new InvalidOperationException(),
            (_,_,_,_)=>throw new InvalidOperationException(),default);
        pc.Seed(ItemChecks.File(Side.Local,path,3,"occupant"),[3]);
        try { await Retry(); } catch (ItemFailedException) { } catch (PathOccupiedException) { }
        catch (NotImplementedException) { /* Assert observable state below, including the second attempt. */ }
        Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{3}) && consumed.Count==0 && store.OpenNotices.Count==1,"Occupied put-back consumed its evidence or replaced the occupant");
        pc.Items.Remove(path);
        try { await Retry(); } catch (NotImplementedException) { }
        Check.Require(pc.Items.TryGetValue(path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{2}) && consumed.SequenceEqual(new[]{first.Revision,last.Revision}) && store.OpenNotices.Count==0,"Retry did not publish the last held revision before settling ordered input");
    }
}
