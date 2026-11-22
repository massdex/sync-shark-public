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
        cloud.Recoverable[(last.Id,last.Revision,last.ContentHash)]=new Recovery.File(last,new MemoryStream([2]));
        var notice = new Notice(1, NoticeKind.PutBack, TextKey.FailLocalInUse, path,
            new([new(Side.Dropbox, path, last.Id)], Side.Dropbox), DateTimeOffset.UnixEpoch, false);
        await store.SaveNoticeAsync(pair.PairId, notice, default);
        var consumed = new List<string>();
        Change Held(Entry e) => new Change(Side.Dropbox, ChangeKind.Changed,path,null,e.Id,e,false,false)
            { ConsumeHeld = () => { Check.Require(pc.Items.TryGetValue(path,out var at) && at.Bytes.SequenceEqual(new byte[]{2}),"MF6 settled held version before publishing final bytes"); consumed.Add(e.Revision); } };
        Change[] input = [Held(first), Held(last)];
        var defects=new List<string>();
        try { input[0].ConsumeHeld!(); defects.Add("MF6 ordering assertion accepted acknowledgment before publication"); } catch(InvalidDataException) { }
        consumed.Clear();
        var debt=new Notice(2,NoticeKind.Failure,TextKey.ReadLocalFailed,"debt",new([new(Side.Local,"debt","pc-debt")],Side.Local,IssueSubject.FirstRunDeletionKind.Item),DateTimeOffset.UnixEpoch,false);
        await store.SaveNoticeAsync(pair.PairId,debt,default); pc.Seed(ItemChecks.File(Side.Local,"debt",1,"pc-debt"),[1]);
        Task Ordinary(Retries retries,Notice n)=>retries.RetryAsync(pair,ItemChecks.Settings,_=>{},paths,n,pc,cloud,store,_=>[],(_,_,_,_)=>throw new InvalidOperationException(),(_,_,_,_)=>throw new InvalidOperationException(),default);
        bool plannerStub=false;
        try { await Ordinary(new Retries(),debt); } catch(NotImplementedException) { plannerStub=true; } catch(ItemFailedException) { }
        if((!plannerStub && (!cloud.Items.TryGetValue("debt",out var syncedDebt) || !syncedDebt.Bytes.SequenceEqual(new byte[]{1}) || store.OpenNotices.Any(n=>n.Kind==NoticeKind.Failure && n.Path==debt.Path))) || cloud.Requests[nameof(ISide.DeletedAsync)]==0) defects.Add("MF5 reviewed historical retry neither probed history nor settled debt");
        var renamed=debt with {Path="caption",Subject=new([new(Side.Local,"renamed","moved-id")],Side.Local)};
        await store.SaveNoticeAsync(pair.PairId,renamed,default);renamed=store.OpenNotices.Single(n=>n.Path=="caption");
        pc.ReadFailures["caption"]=new InvalidDataException("unrelated replacement"); pc.Seed(ItemChecks.File(Side.Local,"renamed",1,"moved-id"),[1]);
        var probes=pc.Requests[nameof(ISide.ReadByIdAsync)];
        try { await Ordinary(new Retries(),renamed); } catch(InvalidDataException){defects.Add("R2-8 ordinary Retry followed caption instead of reviewed subject");} catch(NotImplementedException) { }
        if(pc.Requests[nameof(ISide.ReadByIdAsync)]<=probes) defects.Add("R2-8 did not validate the reviewed moved identity");
        pc.ReadFailures.Remove("caption"); await store.CloseNoticeAsync(pair.PairId,renamed,default);
        var unknown=renamed with {Path="source",Subject=new([new(Side.Dropbox,"source","cloud-move")],Side.Dropbox){Move=new("cloud-move","source","destination",null,false)}};
        await store.SaveNoticeAsync(pair.PairId,unknown,default);unknown=store.OpenNotices.Single(n=>n.Path=="source");
        long beforeId=cloud.Requests[nameof(ISide.ReadByIdAsync)];
        try { await Ordinary(new Retries(),unknown); } catch(NotImplementedException) { }
        if(cloud.Requests[nameof(ISide.ReadByIdAsync)]==beforeId) defects.Add("R3-6 unaccepted unknown move was not observed by id");
        Check.Require(defects.Count==0,string.Join("; ",defects));
        // Both checks use other groups. Run the custody check even when the notice owner is still a stub.
        NotImplementedException? missing=null;
        try { await ItemSyncChecks.UnreadableAsync(); } catch(NotImplementedException e) { missing=e; }
        Task Retry() => new Retries().RetryAsync(pair,ItemChecks.Settings,_=>{},paths,notice,pc,cloud,store,
            scope => scope == new Scope(path,path) ? input : [], (_,_,_,_)=>throw new InvalidOperationException(),
            (_,_,_,_)=>throw new InvalidOperationException(),default);
        pc.Seed(ItemChecks.File(Side.Local,path,3,"occupant"),[3]);
        try { await Retry(); } catch (ItemFailedException) { } catch (PathOccupiedException) { }
        Check.Require(pc.Items[path].Bytes.SequenceEqual(new byte[]{3}) && consumed.Count==0 && store.OpenNotices.Count(n=>n.Kind==NoticeKind.PutBack)==1,"Occupied put-back consumed its evidence or replaced the occupant");
        notice=store.OpenNotices.Single(n=>n.Kind==NoticeKind.PutBack);
        pc.Items.Remove(path);
        await Retry();
        Check.Require(pc.Items.TryGetValue(path,out var copy) && copy.Bytes.SequenceEqual(new byte[]{2}) && consumed.SequenceEqual(new[]{first.Revision,last.Revision}) && store.OpenNotices.All(n=>n.Kind!=NoticeKind.PutBack),"Retry did not publish the last held revision before settling ordered input");
        if(missing!=null) throw missing;
        if(plannerStub) throw new NotImplementedException(nameof(ItemSync));
    }
}
