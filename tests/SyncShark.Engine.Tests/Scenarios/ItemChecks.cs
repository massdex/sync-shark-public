using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Scenarios;

/// <summary>Compares final state on both endpoints, stored bindings and notices through the engine entry point.</summary>
public static class ItemChecks
{
    internal static EngineSettings Settings { get; }=SettingsParser.Load();
    public static async Task IdenticalAsync()
    {
        var pair = new PairConfig("pair", "Pair A", @"T:\Pair A", "/Pair A", "test", [], [], FirstRunPolicy.NewestWins, true, null);
        var local = new FakeSide(Side.Local); var remote = new FakeSide(Side.Dropbox);
        var bytes = new byte[] { 1, 2, 3 };
        using var digest = new ContentHash(); digest.Append(bytes); var hash = digest.Finish();
        var a = new Entry("file", false, bytes.Length, DateTimeOffset.UnixEpoch, "local", "", hash, null);
        var b = a with { Id = "remote", Revision = "rev" }; local.Seed(a, bytes); remote.Seed(b, bytes);
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store = new InMemoryStateStore();
        var queue=await StartQueueAsync(pair,paths,local,remote,store);
        await ItemSync.ApplyAsync(pair,ItemChecks.Settings,_=>{},paths, new Change(Side.Dropbox, ChangeKind.Changed, b.Path, null, b.Id, b, false, false), local, remote, store,Population(pair,paths,local,remote,store),Hop(pair,paths,queue,local,remote,store),CancellationToken.None);
        Check.Require(local.Items.Count == 1 && remote.Items.Count == 1 && local.Items[a.Path].Bytes.SequenceEqual(bytes) && remote.Items[b.Path].Bytes.SequenceEqual(bytes), "Final paths and bytes");
        Check.Require(store.Records.Values.Single() == SyncedEntries.From(a, b) && store.OpenNotices.Count == 0, "Binding and notices");
        Check.Require(local.Requests[nameof(ISide.OpenReadAsync)] == 0 && remote.Requests[nameof(ISide.OpenReadAsync)] == 0, "Unexpected transfer");
    }

    internal static async Task<WorkQueue> StartQueueAsync(PairConfig pair, PathScope paths, ISide local, ISide cloud, IStore store, PairRunner? runner = null)
    {
        var queue = new WorkQueue();
        runner ??= new PairRunner();
        var localEnd = await local.BoundaryAsync(default);
        var cloudEnd = await cloud.BoundaryAsync(default);
        await runner.CatchUpAsync(pair, (localEnd.Position, cloudEnd.Cursor), Settings, _ => { }, paths, queue,
            local, cloud, store, static () => { }, default);
        Check.Require(ReferenceEquals(queue.Unfinished,runner.Unfinished) && queue.Unfinished != null,"Finite admission did not bind the pair's job owner");
        return queue;
    }

    internal static PairConfig Pair() => new("pair", "Pair A", @"T:\Pair A", "/Pair A", "test", [], [], FirstRunPolicy.NewestWins, true, null);
    // The endpoint still advertises its high-water mark after the reader drains the page.
    internal static void Script(FakeSide side,ChangePage page)
    {
        side.IdleBoundary=page;
        side.Pages.Enqueue(page);
    }
    internal static async Task<ChangePage> ReadPrefixAsync(FakeSide side,ChangePage script,CancellationToken ct)
    {
        Script(side,script);
        var end=await side.BoundaryAsync(ct);
        var read=await side.ReadChangesAsync(null,null,end,ct);
        Check.Require(!read.ContinuityLost && read.CaughtUp && read.Changes.SequenceEqual(script.Changes) &&
            (side.Side==Side.Local ? read.ProjectedThrough!=null && read.ProjectedThrough==read.Position && read.Position==end.Position :
             !string.IsNullOrEmpty(read.Cursor) && read.Cursor==end.Cursor),"Did not acquire the scripted finite prefix");
        return read;
    }
    internal static ChangePage Journal(IReadOnlyList<Change> changes,ulong next)
    {
        var end=new JournalPosition(1,next);
        return new(changes,null,end){ProjectedThrough=end};
    }
    internal static async Task CloudChangeAsync(PairConfig pair,PathScope paths,WorkQueue queue,Change change,FakeSide pc,FakeSide cloud,IStore store,bool expectEffect=true)
    {
        var local=await ReadPrefixAsync(pc,Journal([],1),default);
        var remote=await ReadPrefixAsync(cloud,new([change],"before",null),default);
        ChangePage? after=null;
        bool verified=false;
        void Effect(Entry entry)
        {
            var at=local.Position!;
            var deleted=change.Kind==ChangeKind.Deleted;
            var echo=new Change(Side.Local,change.Kind,entry.Path,null,entry.Id,deleted?null:entry,!deleted,deleted,at.NextUsn,JournalId:at.JournalId);
            after=Journal([echo],at.NextUsn+1);
            Script(pc,after);
        }
        if(change.Kind==ChangeKind.Deleted) pc.AfterDelete=Effect; else pc.AfterPublish=Effect;
        bool Covered((JournalPosition? Local,string? Dropbox) fence)
        {
            if(after==null) return fence==(local.ProjectedThrough,remote.Cursor);
            if(fence!=(after.Position,remote.Cursor)) return false;
            if(verified) return true;
            var read=pc.ReadChangesAsync(null,local.Position,(fence.Dropbox,fence.Local),default).GetAwaiter().GetResult();
            return verified=read.CaughtUp && !read.ContinuityLost && read.Position==fence.Local && read.ProjectedThrough==fence.Local && read.Changes.SequenceEqual(after.Changes);
        }
        await queue.AdmitAsync(pair,local,(_,_)=>throw new InvalidOperationException("Unexpected local input"),pc.SettleJournalAsync,default);
        await queue.AdmitAsync(pair,remote,(input,ct)=>ItemSync.ApplyAsync(pair,Settings,_=>{},paths,input,pc,cloud,store,
            Population(pair,paths,pc,cloud,store),Hop(pair,paths,queue,pc,cloud,store),ct,heldInput:queue.Held,
            coversThrough:Covered),null,default);
        Check.Require(expectEffect?verified:after==null,"Unexpected effect or missing post-effect acquisition");
    }
    internal static Entry File(Side side, string path, byte value, string id, bool folder = false)
    {
        using var hash = new ContentHash(); if (!folder) hash.Append([value]);
        return new(path, folder, folder ? 0 : 1, DateTimeOffset.UnixEpoch.AddSeconds(value), id,
            side == Side.Dropbox ? $"rev-{value}" : "", folder ? "" : hash.Finish(), null);
    }
    /// <summary>A saved pair of versions, one incoming change, and all four final projections.</summary>
    public static async Task ChangeAsync(bool baseline, byte? pc, byte? cloud, Side source, byte? expected, TextKey? notice = null, bool folder = false)
    {
        var pair = Pair(); var local = new FakeSide(Side.Local); var remote = new FakeSide(Side.Dropbox);
        var paths=new PathScope(pair,[pair],DriveType.Fixed);
        using var store = new InMemoryStateStore();
        var queue=await StartQueueAsync(pair,paths,local,remote,store); const string path = "item";
        var oldA = File(Side.Local, path, 1, "local", folder); var oldB = File(Side.Dropbox, path, 1, "cloud", folder);
        if (baseline) await store.CommitAsync(pair.PairId, null, SyncedEntries.From(oldA,oldB),null,null,default);
        if (pc is { } a) local.Seed(File(Side.Local,path,a,oldA.Id,folder),folder ? [] : [a]);
        if (cloud is { } b) remote.Seed(File(Side.Dropbox,path,b,oldB.Id,folder),folder ? [] : [b]);
        var from = source == Side.Local ? local : remote; var observed = from.Items.GetValueOrDefault(path).Entry;
        var change=new Change(source,observed==null?ChangeKind.Deleted:ChangeKind.Changed,path,null,
            source==Side.Local?oldA.Id:oldB.Id,observed,source==Side.Local && !folder,observed==null);
        if(source==Side.Dropbox && baseline && pc!=null)
            await CloudChangeAsync(pair,paths,queue,change,local,remote,store);
        else
            await ItemSync.ApplyAsync(pair,Settings,_=>{},paths,change,local,remote,store,Population(pair,paths,local,remote,store),Hop(pair,paths,queue,local,remote,store),default,heldInput:queue.Held);
        foreach (var side in new[] { local,remote })
        {
            Check.Require(side.Items.Count == (expected.HasValue ? 1 : 0), $"Unexpected {side.Side} paths");
            if (expected.HasValue) Check.Require(side.Items.TryGetValue(path,out var value) && value.Entry.IsFolder == folder && value.Bytes.SequenceEqual(folder ? Array.Empty<byte>() : new[]{expected.Value}), $"Unexpected {side.Side} bytes or kind");
        }
        Check.Require(store.Records.Count == (expected.HasValue ? 1 : 0), "Record count");
        if (expected.HasValue) Check.Require(store.Records.Values.Single() == SyncedEntries.From(local.Items[path].Entry,remote.Items[path].Entry), "Stale saved binding");
        Check.Require(store.OpenNotices.Select(n => n.Cause).SequenceEqual(notice.HasValue ? new[]{notice.Value} : Array.Empty<TextKey>()), "Wrong notices");
        if (baseline && expected != 1 && (pc == 1 || cloud == 1))
            Check.Require((pc == 1 ? local : remote).History.Any(v => v.Entry.Path == path && (folder || v.Bytes.SequenceEqual(new byte[]{1}))), "Displaced version not recoverable");
    }
    internal static Func<Scope,Side,Recovery.Folder,CancellationToken,Task> Population(PairConfig pair,PathScope paths,ISide local,ISide cloud,IStore store) =>
        (scope,side,folder,ct)=>global::Massdex.SyncShark.Engine.Run.AutoSync.PopulateAsync(pair,ItemChecks.Settings,_=>{},paths,scope,side,folder,local,cloud,store,ct);
    internal static Func<SyncedEntry?,Scope,Scope?,CancellationToken,Task> Hop(PairConfig pair,PathScope paths,WorkQueue queue,ISide local,ISide cloud,IStore store) =>
        (binding,before,after,ct)=>global::Massdex.SyncShark.Engine.Run.Repair.RunAsync(pair,ItemChecks.Settings,_=>{},paths,queue,local,cloud,store,before,(binding,before,after),ct);
}
