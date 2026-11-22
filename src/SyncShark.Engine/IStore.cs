namespace Massdex.SyncShark.Engine;

/// <summary>Pair-scoped durable facts and streamed Repair rows. Source boundaries advance only on settlement.</summary>
public interface IStore : IDisposable
{
    IAsyncEnumerable<PairConfig> PairsAsync(CancellationToken ct);
    Task<PairConfig?> PairAsync(string pairId, CancellationToken ct);
    Task SavePairAsync(PairConfig pair, CancellationToken ct);
    Task<bool> RemovePairAsync(string pairId, CancellationToken ct);
    Task<SyncedEntry?> FindAsync(string pairId, Side side, string key, CancellationToken ct);
    IAsyncEnumerable<SyncedEntry> ByIdAsync(string pairId, Side side, string id, CancellationToken ct);
    IAsyncEnumerable<SyncedEntry> DescendantsAsync(string pairId, Side side, string key, CancellationToken ct);
    Task CommitAsync(string pairId, SyncedEntry? before, SyncedEntry? after, JournalPosition? publication, CancellationToken ct);
    IAsyncEnumerable<Notice> NoticesAsync(string pairId, CancellationToken ct);
    Task SaveNoticeAsync(string pairId, Notice notice, CancellationToken ct);
    Task CloseNoticeAsync(string pairId, Notice expected, CancellationToken ct);
    Task<JournalPosition?> PositionAsync(string volume, CancellationToken ct);
    Task SavePositionAsync(string volume, JournalPosition position, CancellationToken ct);
    Task<string?> CursorAsync(string pairId, CancellationToken ct);
    Task SaveCursorAsync(string pairId, string cursor, CancellationToken ct);
    Task<long> OweRepairAsync(string pairId, Side side, string reason, CancellationToken ct);
    Task FinishRepairAsync(string pairId, Side side, long revision, CancellationToken ct);
    Task SaveJobAsync(AcceptedJob job, CancellationToken ct);
    IAsyncEnumerable<AcceptedJob> JobsAsync(string pairId, CancellationToken ct);
    Task SettleJobAsync(AcceptedJob job, CancellationToken ct);
    Task ArmAsync(string pairId, RemovalSelection victim, StageProof? stage, CancellationToken ct);
    Task<(RemovalSelection Victim, StageProof? Stage)?> ArmedAsync(string pairId, CancellationToken ct);
    Task DisarmAsync(string pairId, RemovalSelection expected, CancellationToken ct);
    Task AddScanAsync(string pairId, Side side, IAsyncEnumerable<Listed> rows, CancellationToken ct);
    IAsyncEnumerable<Row> CompareAsync(string pairId, Scope? scope, CancellationToken ct);
    Task ClearScanAsync(string pairId, CancellationToken ct);
}
