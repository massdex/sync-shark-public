namespace Massdex.SyncShark.V2;

/// <summary>Test-only writer modelling the batched-upload path (executor.md sections 4.3 and 8) over a fake.</summary>
/// <remarks>Each create frees its admission lease once its bytes are "sent", then waits for its batch commit; a batch
/// commits when it holds <c>capacity</c> creates or the last expected create arrives.</remarks>
internal sealed class BatchingWriter : IEndpointWriter, ISourceAbsenceProbe
{
    private readonly FakeEndpoint _inner;
    private readonly SyncTransferResourceController _admission;
    private readonly int _capacity;
    private readonly int _expected;
    private readonly object _gate = new();
    private readonly List<IReadOnlyList<string>> _batches = new();
    private readonly List<int> _activeAtCommit = new();
    private readonly List<(bool File, long Bytes)> _leases = new();
    private List<string> _pending = new();
    private TaskCompletionSource _commit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrived;

    internal BatchingWriter(FakeEndpoint inner, SyncTransferResourceController admission, int capacity, int expected)
    {
        _inner = inner;
        _admission = admission;
        _capacity = capacity;
        _expected = expected;
    }

    /// <summary>The paths of each committed batch, in commit order.</summary>
    internal IReadOnlyList<IReadOnlyList<string>> Batches { get { lock (_gate) return _batches.ToArray(); } }

    /// <summary>Admitted file transfers still holding a slot at each batch commit.</summary>
    internal IReadOnlyList<int> ActiveAtCommit { get { lock (_gate) return _activeAtCommit.ToArray(); } }

    /// <summary>The admission lease each create ran under (File=false, Bytes=-1 when there was none).</summary>
    internal IReadOnlyList<(bool File, long Bytes)> Leases { get { lock (_gate) return _leases.ToArray(); } }

    public async Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct)
    {
        await Arrive(a.Path).WaitAsync(ct);
        return await _inner.PutAsync(a, content, ct);
    }

    public Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct) => _inner.MoveAsync(a, ct);
    public Task DeleteAsync(SyncAction a, CancellationToken ct) => _inner.DeleteAsync(a, ct);
    public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => _inner.MkDirAsync(path, ct);
    public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
        _inner.PreserveAsync(victim, actionId, ct);
    public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => _inner.ObserveAsync(path, ct);
    public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => _inner.ObserveAsync(path, ct);
    public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct) =>
        _inner.IsSourceAbsentAsync(historical, path, ct);
    public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) =>
        _inner.IsHistoricalSourcePresentAsync(historical, ct);
    public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct) => _inner.OpenReadAsync(s, ct);

    // The bytes are sent: release the admission slot now, as the Dropbox batched path does, and join the open batch.
    private Task Arrive(string path)
    {
        var lease = SyncTransferResourceController.Lease.Current.Value;
        lock (_gate)
        {
            _leases.Add(lease is null ? (false, -1) : (lease.File, lease.Bytes));
            lease?.Complete(true);
            _pending.Add(path);
            var commit = _commit.Task;
            _arrived++;
            if (_pending.Count == _capacity || _arrived == _expected) CommitBatch();
            return commit;
        }
    }

    private void CommitBatch()
    {
        _batches.Add(_pending);
        _activeAtCommit.Add(_admission.Snapshot().ActiveTransfers);
        _pending = new List<string>();
        var full = _commit;
        _commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        full.SetResult();
    }
}
