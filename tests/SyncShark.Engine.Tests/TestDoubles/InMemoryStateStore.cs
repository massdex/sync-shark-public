namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>In-memory records and notices used for observable settlement assertions.</summary>
public sealed class InMemoryStateStore : IStore
{
    public Dictionary<(string Pair, string LocalKey), SyncedEntry> Records { get; } = new();
    public RequestCounters Requests { get; } = new();
    public List<Notice> OpenNotices { get; } = [];
    /// <summary>Pairs operation on the supplied facts.</summary>
    public IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Pair operation on the supplied facts.</summary>
    public Task<PairConfig?> PairAsync(string pairId, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>SavePair operation on the supplied facts.</summary>
    public Task SavePairAsync(PairConfig pair, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>RemovePair operation on the supplied facts.</summary>
    public Task<bool> RemovePairAsync(string pairId, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
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
        return Enumerate(OpenNotices,ct);
    }
    /// <summary>SaveNotice operation on the supplied facts.</summary>
    public Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct) { ct.ThrowIfCancellationRequested(); OpenNotices.Add(notice); return Task.CompletedTask; }
    /// <summary>CloseNotice operation on the supplied facts.</summary>
    public Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); OpenNotices.Remove(expected); return Task.CompletedTask;
    }
    /// <summary>Position operation on the supplied facts.</summary>
    public Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>SavePosition operation on the supplied facts.</summary>
    public Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Cursor operation on the supplied facts.</summary>
    public Task<string?> CursorAsync(string pairId, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>SaveCursor operation on the supplied facts.</summary>
    public Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>OweRepair operation on the supplied facts.</summary>
    public Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>FinishRepair operation on the supplied facts.</summary>
    public Task FinishRepairAsync(string pairId, Side side, long revision, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>SaveJob operation on the supplied facts.</summary>
    public Task SaveJobAsync(AcceptedJob job, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Jobs operation on the supplied facts.</summary>
    public IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>SettleJob operation on the supplied facts.</summary>
    public Task SettleJobAsync(AcceptedJob job, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Arm operation on the supplied facts.</summary>
    public Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Armed operation on the supplied facts.</summary>
    public Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Disarm operation on the supplied facts.</summary>
    public Task DisarmAsync(string pairId, RemovalSelection expected, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>AddScan operation on the supplied facts.</summary>
    public Task AddScanAsync(string pairId, Side side, IAsyncEnumerable<Listed> rows, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Compare operation on the supplied facts.</summary>
    public IAsyncEnumerable<Row> CompareAsync(string pairId, Scope? scope, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>ClearScan operation on the supplied facts.</summary>
    public Task ClearScanAsync(string pairId, CancellationToken ct) => throw new InvalidOperationException("Unscripted store operation");
    /// <summary>Dispose operation on the supplied facts.</summary>
    private static async IAsyncEnumerable<T> Enumerate<T>(IEnumerable<T> values, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        foreach (var value in values) { ct.ThrowIfCancellationRequested(); yield return value; }
    }
    public void Dispose() { }
}
