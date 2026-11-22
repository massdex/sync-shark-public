namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>In-memory records and notices used for observable settlement assertions.</summary>
public sealed class InMemoryStateStore : IStore
{
    public Dictionary<(string Pair, string LocalKey), SyncedEntry> Records { get; } = new();
    public RequestCounters Requests { get; } = new();
    private readonly Dictionary<(string Pair,long Id),Notice> notices = [];
    public IReadOnlyCollection<Notice> OpenNotices => notices.Values;
    private readonly Dictionary<string,PairConfig> pairs = [];
    private readonly Dictionary<string,JournalPosition> positions = [];
    private readonly Dictionary<string,string> cursors = [];
    private readonly Dictionary<(string, Side),(long Revision,string? Reason)> repairs = [];
    private readonly Dictionary<(string Pair,string Route,string Token),AcceptedJob> jobs = [];
    private readonly Dictionary<string,(RemovalSelection Victim, StageProof? Stage)> armed = [];
    private readonly Dictionary<(string Pair,Side Side,string Key),Entry> scan = [];
    private readonly HashSet<string> firstRepairDone = [];
    public void MarkFirstRepairDone(string pair) => firstRepairDone.Add(pair);
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
    public Task SavePairAsync(PairConfig pair, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        pairs[pair.PairId] = pair; return Task.CompletedTask;
    }
    /// <summary>RemovePair operation on the supplied facts.</summary>
    public Task<bool> RemovePairAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (armed.ContainsKey(pairId) || jobs.Keys.Any(k=>k.Pair==pairId) || notices.Any(x=>x.Key.Pair==pairId && x.Value.Kind==NoticeKind.Failure)) return Task.FromResult(false); return Task.FromResult(pairs.Remove(pairId));
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
    public Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Requests.Count(nameof(CommitAsync));
        if (before != null && !Records.Remove((pairId, before.LocalKey))) throw new InvalidDataException("Missing previous binding");
        if (after != null) Records[(pairId, after.LocalKey)] = after;
        return Task.CompletedTask;
    }
    /// <summary>Notices operation on the supplied facts.</summary>
    public IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct)
    {
        return Enumerate(notices.Where(x=>x.Key.Pair == pairId).Select(x=>x.Value),ct);
    }
    /// <summary>SaveNotice operation on the supplied facts.</summary>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) { ct.ThrowIfCancellationRequested(); notices[(pairId,notice.Id)] = notice; return Task.CompletedTask; }
    /// <summary>CloseNotice operation on the supplied facts.</summary>
    public Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); if (notices.TryGetValue((pairId,expected.Id),out var current) && current.Matches(expected)) notices.Remove((pairId,expected.Id)); return Task.CompletedTask;
    }
    /// <summary>Position operation on the supplied facts.</summary>
    public Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(positions.GetValueOrDefault(volume));
    }
    /// <summary>SavePosition operation on the supplied facts.</summary>
    public Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        positions[volume] = position; return Task.CompletedTask;
    }
    /// <summary>Cursor operation on the supplied facts.</summary>
    public Task<string?> CursorAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(cursors.GetValueOrDefault(pairId));
    }
    /// <summary>SaveCursor operation on the supplied facts.</summary>
    public Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        cursors[pairId]=cursor; return Task.CompletedTask;
    }
    /// <summary>OweRepair operation on the supplied facts.</summary>
    public Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var revision=repairs.GetValueOrDefault((pairId,side)).Revision+1; repairs[(pairId,side)]=(revision,reason); return Task.FromResult(revision);
    }
    /// <summary>FinishRepair operation on the supplied facts.</summary>
    public Task FinishRepairAsync(string pairId, Side side, long revision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(repairs.TryGetValue((pairId,side),out var owed) && owed.Revision==revision) repairs[(pairId,side)]=(revision,null); return Task.CompletedTask;
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
    public Task DisarmAsync(string pairId, RemovalSelection expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(armed.TryGetValue(pairId,out var note) && note.Victim==expected) armed.Remove(pairId); return Task.CompletedTask;
    }
    /// <summary>AddScan operation on the supplied facts.</summary>
    public async Task AddScanAsync(string pairId, Side side, IAsyncEnumerable<Listed> rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await foreach(var row in rows.WithCancellation(ct)) if(row.Entry is { } entry) scan[(pairId,side,PathNames.KeyFor(side,row.Path))]=entry;
    }
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<Row> CompareAsync(string pairId, Scope? scope, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var records = Records.Where(k=>k.Key.Pair==pairId).Select(k=>k.Value).ToArray();
        var keys = scan.Keys.Where(k=>k.Pair==pairId).Select(k=>k.Key).Concat(records.Select(v=>v.LocalKey)).Distinct(PathNames.Comparer).Order(StringComparer.Ordinal);
        return Enumerate(Rows(),ct);
        IEnumerable<Row> Rows()
        {
            foreach(var key in keys)
            {
                var saved = records.FirstOrDefault(v=>v.LocalKey==key || v.DropboxKey==key);
                var local=scan.GetValueOrDefault((pairId,Side.Local,saved?.LocalKey??key));
                var cloud=scan.GetValueOrDefault((pairId,Side.Dropbox,saved?.DropboxKey??key));
                if(scope!=null && !PathNames.IsSelfOrUnder(local?.Path??saved?.LocalPath??key,scope.LocalPath) && !PathNames.IsSelfOrUnder(cloud?.Path??saved?.DropboxPath??key,scope.DropboxPath)) continue;
                yield return new Row(key,local,cloud,saved,[],null,RowContext.Neutral with { FirstRun=!firstRepairDone.Contains(pairId), CompleteInventory=true });
            }
        }
    }
    /// <summary>ClearScan operation on the supplied facts.</summary>
    public Task ClearScanAsync(string pairId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach(var key in scan.Keys.Where(k=>k.Pair==pairId).ToArray()) scan.Remove(key); return Task.CompletedTask;
    }
    /// <summary>Dispose operation on the supplied facts.</summary>
    private static async IAsyncEnumerable<T> Enumerate<T>(IEnumerable<T> values, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var value in values) { ct.ThrowIfCancellationRequested(); yield return value; }
    }
    public void Dispose() { }
}
