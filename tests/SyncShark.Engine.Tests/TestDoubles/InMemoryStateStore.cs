using ScanMarks = global::Massdex.SyncShark.Engine.Storage.ScanTable;
namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>In-memory records and notices used for observable settlement assertions.</summary>
public sealed class InMemoryStateStore : IStore
{
    public Dictionary<(string Pair, string LocalKey), SyncedEntry> Records { get; } = new();
    public RequestCounters Requests { get; } = new();
    private readonly Dictionary<(string Pair,long Id),Notice> notices = [];
    private long lastNoticeId;
    public IReadOnlyCollection<Notice> OpenNotices => notices.Values;
    private readonly Dictionary<string,PairConfig> pairs = [];
    private readonly Dictionary<string,JournalPosition> positions = [];
    private readonly Dictionary<string,string> cursors = [];
    private readonly Dictionary<(string, Side),(long Revision,string? Reason)> repairs = [];
    private readonly Dictionary<(string Pair,string Route,string Token),AcceptedJob> jobs = [];
    private readonly Dictionary<string,(RemovalSelection Victim, StageProof? Stage)> armed = [];
    private readonly Dictionary<(string Pair,Guid Scan,Side Side,string Key),Entry> scan = [];
    private readonly HashSet<string> firstRepairDone = [];
    private readonly Dictionary<string,string> summaries=[];
    private readonly Dictionary<string,string> historyCursor=[];
    private readonly HashSet<(string Pair,Guid Scan,string Key)> scratchDeleted=[];
    private readonly Dictionary<(string Pair,Guid Scan),(string Cursor,string Origin)> completedHistory=[];
    private readonly Dictionary<(string Pair,string Key),bool> spentDeleted=[];
    private readonly Dictionary<(string Pair,Guid Scan),ScanMarks> tracking=[];
    public void BeginScan(string pairId,Guid scanId)
    {
        lock(pairs) if(!tracking.ContainsKey((pairId,scanId)))
        {
            var marks=new ScanMarks();marks.Begin(pairId,scanId);tracking.Add((pairId,scanId),marks);
        }
    }
    public bool ScanTouched(string pairId,Guid scanId,Side side,string path,bool descendants)
    {
        lock(pairs) return !tracking.TryGetValue((pairId,scanId),out var marks) || marks.Touched(pairId,scanId,side,path,descendants);
    }
    private void Touch(string pairId,Side side,string key,bool subtree)
    {
        lock(pairs) foreach(var item in tracking) if(item.Key.Pair==pairId) item.Value.Touch(pairId,side,key,subtree);
    }
    private void TouchChange(string pairId, SyncedEntry? before, SyncedEntry? after, SyncedEntry? second = null)
    {
        bool sameFolder = before is {IsFolder:true, LocalId.Length:>0, DropboxId.Length:>0} && after is {IsFolder:true} &&
            before.LocalId==after.LocalId && before.DropboxId==after.DropboxId;
        bool localMoved=before?.LocalPath!=after?.LocalPath, remoteMoved=before?.DropboxPath!=after?.DropboxPath;
        Mark(Side.Local, !(sameFolder && remoteMoved && !localMoved));
        Mark(Side.Dropbox, !(sameFolder && localMoved && !remoteMoved));
        void Mark(Side side, bool descendants)
        {
            var old = before?.KeyOn(side);
            var first = after?.KeyOn(side);
            var last = second?.KeyOn(side);
            if (old != null && old != first && old != last) Touch(pairId, side, old, descendants && before!.IsFolder);
            if (first != null && first != last) Touch(pairId, side, first, descendants && (after!.IsFolder || (first == old && before!.IsFolder)));
            if (last != null) Touch(pairId, side, last, second!.IsFolder || (descendants && ((last == old && before!.IsFolder) || (last == first && after!.IsFolder))));
        }
    }

    private void TouchNotice(string pairId, string path, IssueSubject subject)
    {
        foreach (var member in subject.Items) Touch(pairId, member.Side, PathNames.KeyFor(member.Side, member.Path), false);
        if (subject.Items.Count != 0) return;
        Touch(pairId, Side.Local, PathNames.Key(path), false);
        Touch(pairId, Side.Dropbox, PathNames.DropboxKey(path), false);
    }

    /// <summary>Pairs operation on the supplied facts.</summary>
    public IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Enumerate(pairs.Values,ct);
    }
    /// <summary>Pair operation on the supplied facts.</summary>
    public Task<PairConfig?> PairAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(pairs.GetValueOrDefault(pairId));
    }
    /// <summary>SavePair operation on the supplied facts.</summary>
    public Task SavePairAsync(PairConfig pair, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (pairs)
        {
            ValidateOwners(returnedScopes, pair.PairId, null);
            pairs[pair.PairId] = pair with { OwedScopes = pairs.GetValueOrDefault(pair.PairId)?.OwedScopes };
            PublishScopes(returnedScopes);
        }
        return Task.CompletedTask;
    }
    /// <summary>RemovePair operation on the supplied facts.</summary>
    public Task<TextKey?> RemovePairAsync(string pairId, CancellationToken ct, IReadOnlyList<(string Owner, Scope Scope)>? returnedScopes = null)
    {
        ct.ThrowIfCancellationRequested();
        lock (pairs)
        {
            if (jobs.Keys.Any(k=>k.Pair==pairId)) return Task.FromResult<TextKey?>(TextKey.PairJobOutstanding);
            if (armed.ContainsKey(pairId)) return Task.FromResult<TextKey?>(TextKey.PairPcNoteOutstanding);
            if (notices.Any(x=>x.Key.Pair==pairId && x.Value.Kind==NoticeKind.PutBack)) return Task.FromResult<TextKey?>(TextKey.PairPutBackOutstanding);
            ValidateOwners(returnedScopes, null, pairId);
            if (!pairs.Remove(pairId)) return Task.FromResult<TextKey?>(TextKey.PairUnknown);
            PublishScopes(returnedScopes);
            return Task.FromResult<TextKey?>(null);
        }
    }
    private void ValidateOwners(IReadOnlyList<(string Owner, Scope Scope)>? scopes,string? adding,string? removing)
    {
        if (scopes?.Any(x=>x.Owner==removing || x.Owner!=adding && !pairs.ContainsKey(x.Owner))==true) throw new InvalidDataException("Unknown returned scope owner");
    }
    private void PublishScopes(IReadOnlyList<(string Owner, Scope Scope)>? scopes)
    {
        if (scopes==null) return;
        foreach(var group in scopes.GroupBy(x=>x.Owner))
        {
            var pair=pairs[group.Key];pairs[group.Key]=pair with { OwedScopes=(pair.OwedScopes??[]).Union(group.Select(x=>x.Scope)).ToArray() };
            var key=(group.Key,Side.Dropbox);var old=repairs.GetValueOrDefault(key);repairs[key]=(old.Revision+1,old.Reason);
        }
    }
    /// <summary>Find operation on the supplied facts.</summary>
    public Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); Requests.Count(nameof(FindAsync));
        return Task.FromResult(Records.Where(v => v.Key.Pair == pairId).Select(v => v.Value).SingleOrDefault(v => PathNames.Same(v.KeyOn(side),key)));
    }
    /// <summary>ById operation on the supplied facts.</summary>
    public IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct)
    {
        return Enumerate(Records.Where(v => v.Key.Pair == pairId && v.Value.IdOn(side) == id).Select(v => v.Value),ct);
    }
    /// <summary>Descendants operation on the supplied facts.</summary>
    public IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct)
    {
        return Enumerate(Records.Where(v => v.Key.Pair == pairId && PathNames.IsUnderKey(v.Value.KeyOn(side),key)).Select(v => v.Value),ct);
    }
    /// <summary>Commit operation on the supplied facts.</summary>
    public Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, string? spentDeletedKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Count(nameof(CommitAsync));
        if (before != null && !Records.Remove((pairId, before.LocalKey))) throw new InvalidDataException("Missing previous binding");
        if (after != null) Records[(pairId, after.LocalKey)] = after with { LocalPublished = publication ?? (before?.LocalId == after.LocalId ? after.LocalPublished : null) };
        TouchChange(pairId,before,after);
        if (spentDeletedKey!=null) spentDeleted[(pairId,spentDeletedKey)]=true;
        return Task.CompletedTask;
    }
    /// <summary>Notices operation on the supplied facts.</summary>
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct)
    {
        return Enumerate(notices.Where(x=>x.Key.Pair == pairId).Select(x=>x.Value),ct);
    }
    /// <summary>SaveNotice operation on the supplied facts.</summary>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        lock (pairs)
        {
            var slot=notices.Where(x=>x.Key.Pair==pairId).Select(x=>x.Value).FirstOrDefault(n=>n.Path==notice.Path && n.Kind==notice.Kind);
            if(slot?.Kind==NoticeKind.PutBack && slot.At>notice.At) return Task.CompletedTask;
            if(slot!=null) TouchNotice(pairId,slot.Path,slot.Subject);
            TouchNotice(pairId,notice.Path,notice.Subject);
            var id=slot?.Id ?? checked(++lastNoticeId);
            notices[(pairId,id)]=notice with { Id=id };
        }
        return Task.CompletedTask;
    }
    /// <summary>CloseNotice operation on the supplied facts.</summary>
    public Task<bool> CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (pairs)
        {
            var key = (pairId, expected.Id);
            var closed=notices.TryGetValue(key, out var current) && current.Matches(expected) && notices.Remove(key);
            if(closed) TouchNotice(pairId,expected.Path,expected.Subject);
            return Task.FromResult(closed);
        }
    }
    /// <summary>Position operation on the supplied facts.</summary>
    public Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(pairs) return Task.FromResult(positions.GetValueOrDefault(volume));
    }
    /// <summary>SavePosition operation on the supplied facts.</summary>
    public Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(pairs) positions[volume] = position; return Task.CompletedTask;
    }
    /// <summary>Cursor operation on the supplied facts.</summary>
    public Task<string?> CursorAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(pairs) return Task.FromResult(cursors.GetValueOrDefault(pairId));
    }
    /// <summary>SaveCursor operation on the supplied facts.</summary>
    public Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(pairs) cursors[pairId]=cursor; return Task.CompletedTask;
    }
    /// <summary>OweRepair operation on the supplied facts.</summary>
    public Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock(pairs) { return Task.FromResult(Owe(pairId,side,reason)); }
    }
    /// <summary>FinishRepair operation on the supplied facts.</summary>
    public Task<(long Revision,string? Reason)> ReadRepairAsync(string pairId, Side side, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); lock(pairs) return Task.FromResult(repairs.GetValueOrDefault((pairId,side)));
    }
    public Task<bool> FinishRepairAsync(string pairId, (long Local,long Dropbox) revisions, string completedRepairJson, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ArgumentException.ThrowIfNullOrWhiteSpace(completedRepairJson);
        lock(pairs)
        {
            if(repairs.GetValueOrDefault((pairId,Side.Local)).Revision!=revisions.Local || repairs.GetValueOrDefault((pairId,Side.Dropbox)).Revision!=revisions.Dropbox) return Task.FromResult(false);
            repairs[(pairId,Side.Local)]=(revisions.Local,null);repairs[(pairId,Side.Dropbox)]=(revisions.Dropbox,null);
            firstRepairDone.Add(pairId);summaries[pairId]=completedRepairJson;return Task.FromResult(true);
        }
    }
    public Task<bool> FinishRepairAsync(string pairId, Scope scope, long revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ArgumentNullException.ThrowIfNull(scope);
        lock(pairs)
        {
            if(repairs.GetValueOrDefault((pairId,Side.Dropbox)).Revision!=revision || !pairs.TryGetValue(pairId,out var pair) || pair.OwedScopes?.Contains(scope)!=true) return Task.FromResult(false);
            pairs[pairId]=pair with { OwedScopes=pair.OwedScopes.Where(x=>x!=scope).ToArray() };return Task.FromResult(true);
        }
    }
    /// <summary>SaveJob operation on the supplied facts.</summary>
    public Task SaveJobAsync(AcceptedJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        jobs.Add((job.PairId,job.Route,job.JobId),job); return Task.CompletedTask;
    }
    /// <summary>Jobs operation on the supplied facts.</summary>
    public IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Enumerate(jobs.Where(x=>x.Key.Pair==pairId).Select(x=>x.Value),ct);
    }
    /// <summary>SettleJob operation on the supplied facts.</summary>
    public Task SettleJobAsync(AcceptedJob job, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(jobs.GetValueOrDefault((job.PairId,job.Route,job.JobId))==job) jobs.Remove((job.PairId,job.Route,job.JobId)); return Task.CompletedTask;
    }
    /// <summary>Arm operation on the supplied facts.</summary>
    public Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        armed.Add(pairId,(victim,stage)); return Task.CompletedTask;
    }
    /// <summary>Armed operation on the supplied facts.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<(RemovalSelection Victim, StageProof? Stage)?>(armed.TryGetValue(pairId,out var value) ? value : null);
    }
    /// <summary>Disarm operation on the supplied facts.</summary>
    public Task DisarmAsync(string pairId, RemovalSelection expected, StageProof? expectedStage, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(armed.TryGetValue(pairId,out var note) && note.Victim==expected && note.Stage==expectedStage) armed.Remove(pairId); return Task.CompletedTask;
    }
    /// <summary>AddScan operation on the supplied facts.</summary>
    public async Task AddScanAsync(string pairId, Guid scanId, Side side, IAsyncEnumerable<Listed> rows, Func<string>? terminalHistoryCursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(terminalHistoryCursor!=null && side!=Side.Dropbox) throw new ArgumentException(nameof(terminalHistoryCursor));
        BeginScan(pairId,scanId);
        var origin=HistoryOrigin(pairId);
        if(terminalHistoryCursor!=null){completedHistory.Remove((pairId,scanId));scratchDeleted.RemoveWhere(k=>k.Pair==pairId && k.Scan==scanId);}
        await foreach(var row in rows.WithCancellation(ct))
        {
            if(row.Entry is { } entry) scan[(pairId,scanId,side,PathNames.KeyFor(side,row.Path))]=entry;
            if(terminalHistoryCursor!=null){var key=(pairId,scanId,PathNames.Key(row.Path));if(row.Entry==null) scratchDeleted.Add(key);else scratchDeleted.Remove(key); }
        }
        if(terminalHistoryCursor!=null)
        {
            var cursor=terminalHistoryCursor();ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
            completedHistory[(pairId,scanId)]=(cursor,origin);
        }
    }
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<(Notice Notice, NoticeItem? Member, bool Covered, bool Touched)> Notices)> CompareAsync(string pairId, Guid scanId, Scope? scope, Func<Side,string,(bool Covered,bool Touched)> evidence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var remoteKeys=Records.Where(x=>x.Key.Pair==pairId).Select(x=>x.Value).ToDictionary(v=>v.DropboxKey,v=>v.LocalKey,PathNames.Comparer);
        string Canonical(Side side,string path) {var key=PathNames.KeyFor(side,path);return side==Side.Dropbox?remoteKeys.GetValueOrDefault(key,key):key;}
        var members=notices.Where(v=>v.Key.Pair==pairId).SelectMany(v=>v.Value.Subject.Items.Count==0 ?
            new[]{(Key:PathNames.Key(v.Value.Path),Notice:v.Value,Member:(NoticeItem?)null)} :
            v.Value.Subject.Items.Select(m=>(Key:Canonical(m.Side,m.Path),Notice:v.Value,Member:(NoticeItem?)m))).ToLookup(x=>x.Key,PathNames.Comparer);
        var keys=scan.Where(x=>x.Key.Pair==pairId && x.Key.Scan==scanId).Select(x=>Canonical(x.Key.Side,x.Value.Path)).Concat(Records.Keys.Where(k=>k.Pair==pairId).Select(k=>k.LocalKey)).Concat(members.Select(x=>x.Key)).Distinct(PathNames.Comparer).Order(StringComparer.Ordinal);
        return Enumerate(Rows(),ct);
        IEnumerable<(Row,IReadOnlyList<(Notice,NoticeItem?,bool,bool)>)> Rows()
        {
            foreach(var key in keys)
            {
                var saved=Records.GetValueOrDefault((pairId,key));
                var local=scan.GetValueOrDefault((pairId,scanId,Side.Local,saved?.LocalKey??key));var cloud=scan.GetValueOrDefault((pairId,scanId,Side.Dropbox,saved?.DropboxKey??key));
                var lp=local?.Path??saved?.LocalPath??key;var dp=cloud?.Path??saved?.DropboxPath??key;
                if(scope!=null && !PathNames.IsSelfOrUnder(lp,scope.LocalPath) && !PathNames.IsSelfOrUnder(dp,scope.DropboxPath)) continue;
                var l=evidence(Side.Local,lp);var d=evidence(Side.Dropbox,dp);
                var row=new Row(key,local,cloud,saved,[],null,RowContext.Neutral with { FirstRun=!firstRepairDone.Contains(pairId),CompleteInventory=l.Covered&&d.Covered });
                yield return (row,members[key].Select(x=>{ var proof=x.Member is {} m?evidence(m.Side,m.Path):(Covered:l.Covered&&d.Covered,Touched:l.Touched||d.Touched);return (x.Notice,x.Member,proof.Covered,proof.Touched); }).ToArray());
            }
        }
    }
    /// <summary>ClearScan operation on the supplied facts.</summary>
    public Task ClearScanAsync(string pairId, Guid scanId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach(var key in scan.Keys.Where(k=>k.Pair==pairId && k.Scan==scanId).ToArray()) scan.Remove(key);
        lock(pairs) tracking.Remove((pairId,scanId));
        scratchDeleted.RemoveWhere(k=>k.Pair==pairId && k.Scan==scanId); completedHistory.Remove((pairId,scanId));return Task.CompletedTask;
    }
    /// <summary>Dispose operation on the supplied facts.</summary>
    private static async IAsyncEnumerable<T> Enumerate<T>(IEnumerable<T> values, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var value in values) { ct.ThrowIfCancellationRequested(); yield return value; }
    }
    public void Dispose() { }
    public Task<(bool FirstRepairDone,string? LastRepairJson)> RepairStateAsync(string pairId,CancellationToken ct) => Task.FromResult((firstRepairDone.Contains(pairId),summaries.GetValueOrDefault(pairId)));
    public Task<(string? Cursor,bool Owed)> DeletedHistoryAsync(string pairId,CancellationToken ct) => Task.FromResult((historyCursor.GetValueOrDefault(pairId),!historyCursor.ContainsKey(pairId)));
    private string HistoryOrigin(string pairId)
    {
        var pair=pairs.GetValueOrDefault(pairId);
        return System.Text.Json.JsonSerializer.Serialize(new {LocalRoot=pair?.LocalRoot,DropboxRoot=pair?.DropboxRoot,ConnectionId=pair?.ConnectionId,Roots=pair?.Roots,Cursor=historyCursor.GetValueOrDefault(pairId)});
    }
    public Task<(string? Cursor,bool Owed)> DeletedHistoryAsync(string pairId,Guid scanId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!pairs.ContainsKey(pairId)) throw new InvalidOperationException(Text.Get(TextKey.PairUnknown,(TextArg.Pair,pairId)));
        // Published names are stored directly; there are no encoded blocks to restore or verify.
        return DeletedHistoryAsync(pairId,ct);
    }
    public Task PublishDeletedHistoryAsync(string pairId,Guid scanId,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!completedHistory.TryGetValue((pairId,scanId),out var done) || done.Origin!=HistoryOrigin(pairId)) throw new InvalidOperationException("Not a current completed discovery acquisition");
        foreach(var row in scratchDeleted.Where(x=>x.Pair==pairId && x.Scan==scanId)) spentDeleted.TryAdd((pairId,row.Key),false);
        historyCursor[pairId]=done.Cursor;completedHistory.Remove((pairId,scanId));return Task.CompletedTask;
    }
    public Task<bool> DeletedNameAsync(string pairId,Guid? scanId,string key,CancellationToken ct) => DeletedNameAsync(pairId,scanId,key,false,ct);
    public Task<bool> DeletedNameAsync(string pairId,Guid? scanId,string key,bool isFolder,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        bool completed=completedHistory.TryGetValue((pairId,scanId.GetValueOrDefault()),out var done) && scanId.HasValue;
        if(completed ? done.Origin!=HistoryOrigin(pairId) : !historyCursor.ContainsKey(pairId)) return Task.FromResult(false);
        for(var candidate=key;candidate.Length!=0;candidate=PathNames.Parent(candidate))
        {
            if(spentDeleted.TryGetValue((pairId,candidate),out var spent)){if(!spent || candidate==key) return Task.FromResult(!spent);}
            else if(completed && scratchDeleted.Contains((pairId,scanId!.Value,candidate))) return Task.FromResult(true);
            if(!isFolder) break;
        }
        return Task.FromResult(false);
    }
    public IAsyncEnumerable<Notice> NoticesAtAsync(string pairId,string pathKey,CancellationToken ct) => Enumerate(notices.Where(n=>n.Key.Pair==pairId && PathNames.Same(n.Value.Path,pathKey)).Select(n=>n.Value),ct);
    public Task ProjectSubtreeAsync(string pairId,Scope from,Scope to,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var projected=new List<(SyncedEntry Before,SyncedEntry After)>();
        foreach(var item in Records.Where(k=>k.Key.Pair==pairId).Select(k=>k.Value))
        {
            // Strict descendants only: the real store's RebaseDescendantsBoth excludes the moved binding itself,
            // which the caller then settles with its own Commit.
            bool Under(string path,string before)=>path.Length>before.Length && PathNames.IsSelfOrUnder(path,before);
            string Rewrite(string path,string before,string after)=>Under(path,before)?after+path[before.Length..]:path;
            var l=Rewrite(item.LocalPath,from.LocalPath,to.LocalPath);var d=Rewrite(item.DropboxPath,from.DropboxPath,to.DropboxPath);
            if(l!=item.LocalPath || d!=item.DropboxPath) projected.Add((item,item with { LocalPath=l,LocalKey=PathNames.Key(l),DropboxPath=d,DropboxKey=PathNames.DropboxKey(d) }));
        }
        foreach(var x in projected) Records.Remove((pairId,x.Before.LocalKey));
        foreach(var side in new[]{Side.Local,Side.Dropbox})
        {
            var old=side==Side.Local?from.LocalPath:from.DropboxPath;var next=side==Side.Local?to.LocalPath:to.DropboxPath;
            if(old!=next){Touch(pairId,side,PathNames.KeyFor(side,old),true);Touch(pairId,side,PathNames.KeyFor(side,next),true);}
        }
        foreach(var x in projected) Records.Add((pairId,x.After.LocalKey),x.After);
        return Task.CompletedTask;
    }
    private long Owe(string pairId,Side side,string reason)
    {
        var revision=repairs.GetValueOrDefault((pairId,side)).Revision+1;repairs[(pairId,side)]=(revision,reason);return revision;
    }
    public Task ResetJournalAsync(string volume, JournalPosition fresh, IReadOnlyList<string> affectedPairs, string reason, CancellationToken ct)
    {
        lock (pairs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var pair in pairs.Values)
                if (pair.Roots?.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) == volume)
                    Owe(pair.PairId, Side.Local, reason);
            positions[volume] = fresh;
        }
        return Task.CompletedTask;
    }
    public Task ResetCursorAsync(string pairId, string fresh, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();lock(pairs) { if(!pairs.ContainsKey(pairId)) throw new InvalidDataException("Unknown pair");
            Owe(pairId,Side.Dropbox,reason);cursors[pairId]=fresh; } return Task.CompletedTask;
    }
    public IAsyncEnumerable<RenameCandidate> RenamesAsync(string pairId, Guid scanId, Scope? scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var observed=scan.Where(x=>x.Key.Pair==pairId && x.Key.Scan==scanId).Select(x=>(x.Key.Side,Entry:x.Value));
        var sides=Records.Where(x=>x.Key.Pair==pairId).SelectMany(x=>new[]{(Side:Side.Local,Saved:x.Value),(Side:Side.Dropbox,Saved:x.Value)});
        var matched=sides.Where(x=>scan.TryGetValue((pairId,scanId,x.Side,x.Saved.KeyOn(x.Side)),out var entry) &&
                entry.Id==x.Saved.IdOn(x.Side) && entry.IsFolder==x.Saved.IsFolder && entry.Path==x.Saved.PathOn(x.Side))
            .Select(x=>(x.Side,x.Saved.KeyOn(x.Side))).ToHashSet();
        var bindings=sides.Where(x=>!matched.Contains((x.Side,x.Saved.KeyOn(x.Side))))
            .ToLookup(x=>(x.Side,x.Saved.IdOn(x.Side),x.Saved.IsFolder),x=>x.Saved);
        var candidates=from row in observed where row.Entry.Id.Length!=0 && !matched.Contains((row.Side,PathNames.KeyFor(row.Side,row.Entry.Path)))
                       from saved in bindings[(row.Side,row.Entry.Id,row.Entry.IsFolder)]
                       where (scope==null || PathNames.IsSelfOrUnder(saved.LocalPath,scope.LocalPath) || PathNames.IsSelfOrUnder(saved.DropboxPath,scope.DropboxPath) || PathNames.IsSelfOrUnder(row.Entry.Path,row.Side==Side.Local?scope.LocalPath:scope.DropboxPath))
                       select new RenameCandidate(row.Side,row.Entry,saved);
        return Enumerate(candidates.GroupBy(x=>x.Synced).OrderBy(group=>group.Min(x=>PathNames.Depth(x.Moved.Path))).ThenBy(group=>group.Key.LocalKey,StringComparer.Ordinal).SelectMany(group=>group.OrderBy(x=>x.Side)),ct);
    }
    /// <inheritdoc/>
    public Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, Notice notice, CancellationToken ct) =>
        KeepBothAsync(pairId, before, first, null, notice, ct);
    /// <inheritdoc/>
    public Task CommitKeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry second, Notice notice, CancellationToken ct) =>
        KeepBothAsync(pairId, before, first, second, notice, ct);
    private Task KeepBothAsync(string pairId, SyncedEntry? before, SyncedEntry first, SyncedEntry? second, Notice notice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();lock(pairs)
        {
            if(first.LocalKey==second?.LocalKey || first.DropboxKey==second?.DropboxKey || before!=null && Records.GetValueOrDefault((pairId,before.LocalKey))!=before || Records.Any(x=>x.Key.Pair==pairId && x.Value!=before && (x.Value.LocalKey==first.LocalKey || x.Value.LocalKey==second?.LocalKey || x.Value.DropboxKey==first.DropboxKey || x.Value.DropboxKey==second?.DropboxKey))) throw new InvalidDataException("Kept bindings no longer match");
            SaveNoticeAsync(pairId,notice,ct).GetAwaiter().GetResult();
            if(before!=null) Records.Remove((pairId,before.LocalKey));
            Records.Add((pairId,first.LocalKey),first);
            if(second!=null) Records.Add((pairId,second.LocalKey),second);
            TouchChange(pairId,before,first,second);
            return Task.CompletedTask;
        }
    }
    /// <summary>In one transaction match the reviewed drive notice and publish the chosen pair; only FirstSync resets bindings and first Repair.</summary>
    public Task<bool> ResolveDriveAsync(PairConfig pair, Notice reviewed, ResolutionChoice choice, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();lock(pairs)
        {
            if(choice is not (ResolutionChoice.FirstSync or ResolutionChoice.SyncNormally) || reviewed.Kind!=NoticeKind.DriveChange || !notices.TryGetValue((pair.PairId,reviewed.Id),out var open) || !open.Matches(reviewed)) return Task.FromResult(false);
            if(choice==ResolutionChoice.FirstSync){foreach(var k in Records.Keys.Where(k=>k.Pair==pair.PairId).ToArray()) Records.Remove(k);firstRepairDone.Remove(pair.PairId);}
            pairs[pair.PairId]=pair;notices.Remove((pair.PairId,reviewed.Id));return Task.FromResult(true);
        }
    }
    /// <summary>Stream notices whose members overlap the mapped footprint on their own side, including covering ancestors.</summary>
    public IAsyncEnumerable<Notice> NoticesUnderAsync(string pairId, Scope footprint, CancellationToken ct) => Enumerate(notices.Where(x=>x.Key.Pair==pairId).Select(x=>x.Value).Where(n=>n.Subject.Items.Any(m=>Overlaps(m.Path,m.Side==Side.Local?footprint.LocalPath:footprint.DropboxPath)) || n.Subject.Items.Count==0 && (Overlaps(n.Path,footprint.LocalPath) || Overlaps(n.Path,footprint.DropboxPath))),ct);
    private static bool Overlaps(string path,string root)=>PathNames.IsSelfOrUnder(path,root)||PathNames.IsSelfOrUnder(root,path);
    /// <summary>Add or retire only this observed link member; close the pair-wide link notice only when no members remain.</summary>
    public Task UpdateLinkMemberAsync(string pairId, NoticeItem member, bool present, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var link=notices.Where(x=>x.Key.Pair==pairId && x.Value.Cause==TextKey.InfoLinks).Select(x=>x.Value).SingleOrDefault();
        if(link==null && !present) return Task.CompletedTask;
        link??=new Notice(0,NoticeKind.Info,TextKey.InfoLinks,string.Empty,new([],null),DateTimeOffset.UnixEpoch,false);
        var items=(present?link.Subject.Items.Append(member).Distinct():link.Subject.Items.Where(x=>x!=member)).ToArray();
        return items.Length==0 ? CloseNoticeAsync(pairId,link,ct) : SaveNoticeAsync(pairId,link with {Subject=link.Subject with {Items=items}},ct);
    }
    /// <summary>Read one indexed notice by pair and ID; the caller must still validate its reviewed token.</summary>
    public Task<Notice?> NoticeAsync(string pairId, long id, CancellationToken ct) { ct.ThrowIfCancellationRequested();return Task.FromResult(notices.GetValueOrDefault((pairId,id))); }
}
