using System.Text.Json;
using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Adds, removes and reconfigures pairs under the host's commands gate, publishing each change to overlapping pairs first.</summary>
internal sealed class HostPairs(Host host)
{
    internal async Task AddPairAsync(PairConfig pair,CancellationToken ct)
    {
        if (!PathNames.IsDropboxRoot(pair.DropboxRoot)) throw new EngineRequestException(TextKey.DropboxPathInvalid,(TextArg.Path,pair.DropboxRoot));
        var (m,db,token)=await RegisterPairAsync(pair,ct);
        try
        {
            await m.Setup; await host.Commands.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested(); var all=await host.Configurations(token); all.Add(m.Pair);
                m.Paths=new(m.Pair,all,m.Type,host.Settings.DefaultIgnoreRules);
                await PublishParents(pair,all,async ()=>{await db.SavePairAsync(m.Pair,token); return true;},token);
                // Register the bound adapters only after this pair exists in the store's published membership.
                m.Local=null!; m.Dropbox=null!; await host.Life.Adapters(m,token);
                m.Admitted=true; await host.Life.Membership(m,false,token); host.Life.Start(m);
            }
            finally {host.Commands.Release();}
        }
        catch
        {
            await host.Commands.WaitAsync(CancellationToken.None);
            try
            {
                if(host.Members.TryGetValue(pair.PairId,out var current) && ReferenceEquals(m,current) && !m.Admitted)
                { host.Members.TryRemove(pair.PairId,out _); await host.Life.Membership(m,true,CancellationToken.None); m.Opening?.Dispose(); }
            }
            finally {host.Commands.Release();}
            throw;
        }
    }
    // Under the gate: open or create storage, refuse a known pair id, attach the member and start reading its actual roots.
    private async Task<(HostMember Member,IStore Store,CancellationToken Token)> RegisterPairAsync(PairConfig pair,CancellationToken ct)
    {
        await host.Commands.WaitAsync(ct);
        try
        {
            var db=await host.Open(false,ct) ?? await host.Open(true,ct) ?? throw new EngineRequestException(TextKey.StatusUnavailable);
            if(host.Members.ContainsKey(pair.PairId) || await db.PairAsync(pair.PairId,ct)!=null) throw new EngineRequestException(TextKey.ResolveStale);
            var m=host.Life.Attach(pair); m.Opening=CancellationTokenSource.CreateLinkedTokenSource(ct); var token=m.Opening.Token;
            m.Setup=Task.Run(async ()=>
            {
                var (roots,drive,type)=await host.Life.ReadRoots(m,token);
                if(roots?.IsValid()!=true || type==DriveType.Unknown) throw new EngineRequestException(TextKey.RootUnavailable);
                m.Pair=pair with {Roots=roots,Drive=drive}; m.Type=type;
            });
            return (m,db,token);
        }
        finally {host.Commands.Release();}
    }
    private Task PublishParents(PairConfig child,IReadOnlyList<PairConfig> pairs,Func<Task<bool>> commit,CancellationToken ct)
    {
        var parents=host.Members.Values.Where(x=>x.Pair.PairId!=child.PairId && (Under(x.Pair,child,Side.Local)!=null || Under(x.Pair,child,Side.Dropbox)!=null))
            .OrderBy(x=>x.Pair.PairId,StringComparer.Ordinal).ToArray();
        var before=host.PublishedPairs;
        return host.OutsideCommands(()=>Publish(0));
        async Task Publish(int index)
        {
            if(index<parents.Length)
            {
                var outer=parents[index];var paths=new List<string>();
                foreach(var side in new[]{Side.Local,Side.Dropbox}) if(Under(outer.Pair,child,side) is {} rel)
                {var mapped=await Mapped(outer.Pair,side,rel,ct);paths.Add(mapped.LocalPath);paths.Add(mapped.DropboxPath);}
                await outer.Queue.PublishAsync(paths,()=>Publish(index+1),ct); return;
            }
            await host.Command(async ()=>
            {
                if(host.Stopped.IsCancellationRequested || !ReferenceEquals(before,host.PublishedPairs)) throw new EngineRequestException(TextKey.ResolveStale);
                if(!await commit()) return;
                lock(host.Members) {host.PublishedPairs=pairs; foreach(var outer in parents) outer.Paths=new(outer.Pair,pairs,outer.Type,host.Settings.DefaultIgnoreRules);}
            },ct);
        }
    }
    private static string? Under(PairConfig parent,PairConfig child,Side side) => side == Side.Local ? PathNames.RelativeUnderLocalRoot(parent.LocalRoot,child.LocalRoot) :
        parent.ConnectionId==child.ConnectionId ? PathNames.RelativeUnderDropboxRoot(PathNames.TrimDropboxRoot(parent.DropboxRoot),PathNames.TrimDropboxRoot(child.DropboxRoot)) : null;
    internal async Task<TextKey?> RemovePairAsync(string pairId,CancellationToken ct)
    {
        TextKey? cause=null;
        await host.Command(async () =>
        {
            PairConfig p;
            try { p=await host.Required(pairId,ct); }
            // An undecodable definition never launched: no member, reservation or owner scope exists, so remove the row only.
            catch(Exception e) when(e is InvalidDataException or JsonException && host.Store!=null && !host.Members.ContainsKey(pairId))
            { cause=await host.Store!.RemovePairAsync(pairId,ct); return; }
            var all=await host.Configurations(ct); var owed=new HashSet<(string,Scope)>();
            foreach (var side in new[]{Side.Local,Side.Dropbox})
            {
                var owner=all.Where(q => q.Enabled && q.PairId!=pairId).Select(q => (q,rel:Under(q,p,side))).Where(x => !string.IsNullOrEmpty(x.rel)).OrderBy(x => PathNames.Depth(x.rel!)).FirstOrDefault();
                if (owner.q==null || !p.Enabled) continue;
                owed.Add((owner.q.PairId,await Mapped(owner.q,side,owner.rel!,ct)));
            }
            var m=host.Members.TryGetValue(pairId,out var member)?member:host.Life.Attach(p);
            if(Interlocked.Increment(ref m.Closing)!=1) {Interlocked.Decrement(ref m.Closing);throw new EngineRequestException(TextKey.ResolveStale);}
            bool committed=false;
            try {
                await HostLifecycle.Drain(m);
                await PublishParents(p,all.Where(x=>x.PairId!=pairId).ToArray(),async ()=>
                {cause=await host.Store!.RemovePairAsync(pairId,ct,owed.ToArray());return committed=cause==null;},ct);
            } finally {
                if(committed) {
                    try {
                        if((m.Local==null || p.Roots==null) && host.ReleaseReservation!=null) await host.ReleaseReservation(p,CancellationToken.None);
                        else await host.Life.Membership(m,true,CancellationToken.None);
                    } finally {
                        host.Members.TryRemove(pairId,out _);host.Paused.TryRemove(pairId,out _);host.Life.ReleaseTrim(m,true);
                        foreach(var id in owed.Select(x=>x.Item1).Distinct()) if(host.Members.TryGetValue(id,out var owner)) host.Life.WakeRepair(owner);
                    }
                } else {Interlocked.Decrement(ref m.Closing);if(!host.Stopped.IsCancellationRequested && !host.IsPaused(pairId)) host.Life.Launch(m,all,CancellationToken.None);}
            }
        },ct); return cause;
    }
    internal async Task<Scope> Mapped(PairConfig pair,Side side,string path,CancellationToken ct)
    {
        var binding=await host.Store!.FindAsync(pair.PairId,side,PathNames.KeyFor(side,path),ct);
        if(binding!=null) return new(binding.LocalPath,binding.DropboxPath);
        var destination=await ItemSync.CounterpartAsync(pair,side,path,host.Store,ct);
        return side==Side.Local ? new(path,destination) : new(destination,path);
    }
    internal async Task<(ExclusionOutcome? Outcome,long CopyFiles,long CopyBytes,long RemoveFiles,long RemoveBytes,string? RecoveryPlace,TextKey? Cause)> SetExclusionAsync(string pairId,Exclusion exclusion,bool include,bool confirmed,CancellationToken ct)
    {
        try { if (Path.IsPathRooted(exclusion.Path)) throw new ArgumentException(); exclusion=exclusion with {Path=PathNames.NormalizeRelative(exclusion.Path)}; }
        catch (ArgumentException e) { throw new EngineRequestException(TextKey.ExclusionPathInvalid,(TextArg.Path,exclusion.Path),(TextArg.Message,e.Message)); }
        await host.Commands.WaitAsync(ct);
        try
        {
            var p=await host.Required(pairId,ct);
            HostMember m;
            if (!include && !exclusion.Keep && !confirmed)
            {
                m=host.Members.TryGetValue(pairId,out var existing) ? existing : host.Life.Attach(p);
                if (m.Paths==null)
                {
                    await host.Life.Adapters(m,ct);
                    m.Type=(await m.Local.RootAsync(ct)).LocalDriveType ?? DriveType.Unknown;
                    m.Paths=new(p,await host.Configurations(ct),m.Type,host.Settings.DefaultIgnoreRules);
                }
            }
            else m=await host.Life.Ready(p,ct);
            var list=p.Exclusions.Where(e => e.Side!=exclusion.Side || !PathNames.Same(e.Path,exclusion.Path)).ToList(); if (!include) list.Add(exclusion);
            var next=p with {Exclusions=list};
            if (!include && !exclusion.Keep) return await Exclude(m,next,exclusion,confirmed,ct);
            var mapped=await Mapped(p,exclusion.Side,exclusion.Path,ct);
            await Change(m,next,include ? [mapped] : [],ct,mapped); return (ExclusionOutcome.Applied,0,0,0,0,null,null);
        }
        finally { host.Commands.Release(); }
    }
    private async Task Change(HostMember m,PairConfig next,IReadOnlyList<Scope> returned,CancellationToken ct,Scope? footprint=null,bool excluding=false)
    {
        var scope=new PathScope(next,await host.Configurations(ct),m.Type,host.Settings.DefaultIgnoreRules);
        var owed=returned.Where(s => excluding || !scope.Excludes(s.LocalPath,s.DropboxPath,true)).Select(s => (next.PairId,s)).Distinct().ToArray();
        var expected=m.Pair; var before=host.PublishedPairs;
        await host.OutsideCommands(()=>m.Queue.PublishAsync(footprint==null ? [string.Empty] : [footprint.LocalPath,footprint.DropboxPath],()=>host.Command(async ()=>
        {
            if(!host.Members.TryGetValue(next.PairId,out var current) || current!=m || !ReferenceEquals(m.Pair,expected) || !ReferenceEquals(before,host.PublishedPairs))
                throw new EngineRequestException(TextKey.ResolveStale);
            await host.Store!.SavePairAsync(next,ct,owed); lock(host.Members){m.Pair=next; m.Paths=scope;}
        },ct),ct));
        if(owed.Length!=0) host.Life.WakeRepair(m);
    }
    internal Task SetIgnoresAsync(string pairId,IReadOnlyList<string> rules,CancellationToken ct) => host.Command(async () =>
    {
        var p=await host.Required(pairId,ct); var m=await host.Life.Ready(p,ct);
        var edited=IgnoreRule.Normalize(rules.SelectMany(IgnoreRule.ParseForEdit).ToArray()).Select(IgnoreRule.Format).ToArray();
        bool broadened=IgnoreRule.Normalize(p.IgnoreRules.SelectMany(IgnoreRule.Parse).ToArray()).Select(IgnoreRule.Format).Except(edited).Any();
        await Change(m,p with {IgnoreRules=edited},broadened ? [new("","")] : [],ct);
    },ct);
    private async Task<(ExclusionOutcome?,long,long,long,long,string?,TextKey?)> Exclude(HostMember m,PairConfig next,Exclusion exclusion,bool confirmed,CancellationToken ct)
    {
        var executor=new Exclusions(); var v=await executor.PreviewAsync(m.Pair,exclusion,m.Local,m.Dropbox,host.Store!,m.Paths,ct);
        var place=v.RemoveFiles==0 ? null : exclusion.Side==Side.Local && m.Type==DriveType.Removable ? Path.Combine(Path.GetPathRoot(m.Pair.LocalRoot)!,ResourceNames.RootBinFolder) : Text.Get(v.RecoveryPlace);
        ExclusionOutcome? outcome=null;TextKey? cause=v.BlockedByProtected ? TextKey.FailExcludeHoldsProtected : null;
        if (confirmed)
        {
            var mapped=await Mapped(m.Pair,exclusion.Side,exclusion.Path,ct);
            await Change(m,next,[mapped],ct,mapped,true);
            (outcome,cause)=await executor.ApplyAsync(await host.Required(next.PairId,ct),host.Settings,m.Event,m.Paths,exclusion,m.Local,m.Dropbox,host.Store!,
                (s,side,f,token)=>host.Populate(m,s,side,f,token),(b,before,after,token)=>host.Compare(m,b,before,after,token),ct,m.Queue.Held,m.Queue.CoversHop);
        }
        return (outcome,v.CopyFiles,v.CopyBytes,v.RemoveFiles,v.RemoveBytes,place,cause);
    }
}
