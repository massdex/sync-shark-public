namespace Massdex.SyncShark.Engine.Executor;

/// <summary>One pair's saved jobs and their coalesced check opportunities. No polling.</summary>
public sealed class Unfinished
{
    private readonly Dictionary<(string Route,string Id),AcceptedJob> jobs=[];
    private readonly HashSet<(string Route,string Id)> checking=[], again=[], invalid=[];
    private readonly SemaphoreSlim refresh=new(1);
    private bool loaded;

    /// <summary>After saving the token, match its actual launch paths against already admitted news.</summary>
    public async Task InstalledAsync(AcceptedJob job,Func<Scope,IEnumerable<Change>> heldInput,Func<Change,Task> wake)
    {
        lock(jobs) jobs[(job.Route,job.JobId)]=job;
        foreach(var member in job.Launch)
            foreach(var path in new[]{member.Path,member.Destination})
                if(path!=null && heldInput(new(PathNames.LocalSpelling(path),path)).FirstOrDefault(c=>Matches(job,c)) is {} change) {await wake(change);return;}
    }

    private static bool Matches(AcceptedJob job,Change c) => c.Side==Side.Dropbox && job.Launch.Any(m=>Overlaps(m.Path,c.Path)||Overlaps(m.Path,c.OldPath)||Overlaps(m.Destination,c.Path)||Overlaps(m.Destination,c.OldPath));
    private static bool Overlaps(string? a,string? b)=>a!=null && b!=null && (PathNames.IsSelfOrUnder(a,b)||PathNames.IsSelfOrUnder(b,a));

    /// <summary>Refresh before first use and at Repair; feed input checks only overlapping jobs.</summary>
    public async Task CheckAsync(PairConfig pair, EngineSettings settings, Action<RunEvent> events, PathScope paths, Change? received, ISide local, ISide dropbox, IStore store, Func<Scope,IEnumerable<Change>> heldInput, Func<Scope,Side,Recovery.Folder,CancellationToken,Task> populate, Func<SyncedEntry,Scope,Scope,CancellationToken,Task> compareOldBinding, CancellationToken ct)
    {
        if(received!=null && received.Side!=Side.Dropbox) return;
        await refresh.WaitAsync(ct);
        try
        {
            if(!loaded || received==null)
            {
                await foreach(var job in store.JobsAsync(pair.PairId,ct)) lock(jobs) jobs[(job.Route,job.JobId)]=job;
                loaded=true;
            }
        }
        finally {refresh.Release();}
        AcceptedJob[] selected;
        lock(jobs) selected=jobs.Values.Where(j=>j.PairId==pair.PairId && (received==null || Matches(j,received))).ToArray();
        await Task.WhenAll(selected.Select(Check));
        if(received==null) await RecoverStageAsync(pair,local,dropbox,store,ct);

        async Task Check(AcceptedJob job)
        {
            var key=(job.Route,job.JobId);
            lock(jobs)
            {
                if(!jobs.ContainsKey(key)) return;
                if(!checking.Add(key)) {again.Add(key);return;}
            }
            try
            {
                while(true)
                {
                    var reply=await dropbox.ReadJobAsync(job,ct);
                    bool done=reply is JobReply.Terminal terminal && await SettleMembers(job,terminal);
                    if(reply is JobReply.Invalid)
                    {
                        lock(jobs) invalid.Add(key);
                        foreach(var m in job.Launch)
                        {
                            if(m.Id!=null) await dropbox.ReadByIdAsync(m.Id,ct);
                            bool masked=false;
                            await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.DropboxKey(m.Path),ct)) if(n.Kind==NoticeKind.Failure && n.Cause!=TextKey.FailDropboxDeleteJobInvalid) masked=true;
                            if(!masked) await store.SaveNoticeAsync(pair.PairId,new(0,NoticeKind.Failure,TextKey.FailDropboxDeleteJobInvalid,m.Path,new([new(Side.Dropbox,m.Path,m.Id??string.Empty)],Side.Dropbox),DateTimeOffset.UtcNow,false),ct);
                        }
                    }
                    else
                    {
                        bool wasInvalid;lock(jobs) wasInvalid=invalid.Remove(key);
                        if(wasInvalid) foreach(var m in job.Launch)
                        {
                            bool stillInvalid; lock(jobs) stillInvalid=invalid.Any(k=>jobs[k].Launch.Any(x=>PathNames.Same(x.Path,m.Path)));
                            if(!stillInvalid) await foreach(var n in store.NoticesAtAsync(pair.PairId,PathNames.DropboxKey(m.Path),ct)) if(n.Cause==TextKey.FailDropboxDeleteJobInvalid) await store.CloseNoticeAsync(pair.PairId,n,ct);
                        }
                    }
                    if(done) await store.SettleJobAsync(job,ct);
                    lock(jobs)
                    {
                        if(done) jobs.Remove(key);
                        if(again.Remove(key) && !done) continue;
                        checking.Remove(key); return;
                    }
                }
            }
            catch {lock(jobs) {checking.Remove(key);again.Remove(key);} throw;}
        }

        async Task<bool> SettleMembers(AcceptedJob job,JobReply.Terminal reply)
        {
            if(reply.Members.Count!=job.Launch.Count) return false;
            bool all=true;
            for(int i=0;i<job.Launch.Count;i++)
            {
                var member=job.Launch[i]; var receipt=reply.Members[i];
                if(receipt.Entry==null || receipt.Cause!=null || receipt.Failure!=null || member.Id!=null && member.Id!=receipt.Entry.Id || !PathNames.Same(member.Destination??member.Path,receipt.Entry.Path)) {all=false;continue;}
                var binding=await store.FindAsync(pair.PairId,Side.Dropbox,PathNames.DropboxKey(member.Path),ct);
                var scope=new Scope(binding?.LocalPath??PathNames.LocalSpelling(member.Path),member.Path);
                if(job.Route.Contains("delete",StringComparison.Ordinal))
                {
                    if(!await FolderDelete.RecoverHeldAsync(pair,scope,Side.Dropbox,local,dropbox,store,heldInput,populate,ct)) {all=false;continue;}
                    if(binding!=null && binding.DropboxId==member.Id) await compareOldBinding(binding,scope,scope,ct);
                }
                else if(binding!=null && member.Destination!=null && binding.DropboxId==member.Id) await compareOldBinding(binding,scope,new(scope.LocalPath,member.Destination),ct);
            }
            return all;
        }
    }

    private static async Task RecoverStageAsync(PairConfig pair,ISide local,ISide dropbox,IStore store,CancellationToken ct)
    {
        if(await store.ArmedAsync(pair.PairId,ct) is not {Stage:{} stage} note) return;
        var victim=note.Victim; var proof=victim.Proof;
        var location=await local.ReadByIdAsync(proof.Expected.Id,ct);
        if(location is not {InScope:false} actual || actual.Location.Domain!=proof.VolumeId || actual.Location.Entry.Id!=proof.Expected.Id) return;
        if(!proof.Expected.IsFolder && (actual.Location.Entry.Size!=proof.Expected.Size || actual.Location.Entry.ContentHash!=proof.ContentHash)) return;
        var current=await local.ReadOneAsync(proof.Expected.Path,ct);
        if(current!=null && (current.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash)) return;
        try
        {
            if(current==null)
            {
                await using var reopened=await local.ReopenStageAsync(victim,stage,ct);
                var result=await local.PublishAsync(reopened,null,null,ct); current=result.Final;
                if(result.Outcome!=RemovalOutcome.Published || current?.Id!=stage.FileId || current.Size!=stage.Size || await local.ContentHashAsync(current,ct)!=stage.ContentHash) return;
            }
            if(victim.Saved is {} saved && await dropbox.ReadOneAsync(saved.DropboxPath,ct) is {} other && other.Id==saved.DropboxId && other.ContentHash==stage.ContentHash)
            {
                await store.CommitAsync(pair.PairId,saved,SyncedEntries.From(current,other),null,null,ct);
                await store.DisarmAsync(pair.PairId,victim,stage,ct);
            }
        }
        catch(ChangedUnderneathException) { /* An unverified stage stays armed. */ }
    }
}
