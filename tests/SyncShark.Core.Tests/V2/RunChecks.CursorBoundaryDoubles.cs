using System.Collections.Concurrent;

namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private sealed class CursorBoundaryIo(LocalGapIo native, string unchanged, long boundary,
        NtfsUsnCheckpoint through) : ILocalGapIo
    {
        private readonly ConcurrentDictionary<string, int> _reads = new();
        internal int Probes;
        internal int ReadsFor(string path) => _reads.GetValueOrDefault(path);
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Paused => false;
        public LocalGapProof? Probe(RootDef root, ItemState item)
        {
            Interlocked.Increment(ref Probes);
            var identity = LocalItems.ParseObjectId(item.ObjectId);
            return new(through.Cursor, new NtfsFileUsn(identity.Volume, identity.FileId,
                item.Path == unchanged ? boundary - 1 : boundary, item.Size, item.Mtime.UtcDateTime.ToFileTimeUtc()));
        }
        public async Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct)
        {
            _reads.AddOrUpdate(item.Path, 1, (_, count) => count + 1);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return await native.HashAsync(root, item, ct);
        }
    }

    private sealed class BoundaryTraceCapture : RunTrace
    {
        internal ConcurrentQueue<string> Lines { get; } = new();
        internal override void WriteTrace(string line) => Lines.Enqueue(line);
    }

    private sealed class BoundaryPlanCapture(IExecutor inner) : IExecutor
    {
        internal ConcurrentQueue<SyncAction> Actions { get; } = new();
        public IAsyncEnumerable<ActionResult> ApplyAsync(Plan plan, IEndpointWriter a, IEndpointWriter b, CancellationToken ct)
        {
            foreach (var action in plan.Actions) Actions.Enqueue(action);
            return inner.ApplyAsync(plan, a, b, ct);
        }
        public Task<IReadOnlyList<ActionResult>> ReconcileAsync(IReadOnlyList<SyncAction> intents, long gen,
            IEndpointWriter a, IEndpointWriter b, CancellationToken ct, IProgress<int>? progress = null) =>
            inner.ReconcileAsync(intents, gen, a, b, ct, progress);
    }
}
