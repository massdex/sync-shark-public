namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private IReadOnlyList<(string RootId, string ObjectId)> FoldExecutionProofs(PairDef pair, BothScan scan, Tally tally)
    {
        if (_store is not SqliteStateStore sql) return [];
        var folded = new List<(string RootId, string ObjectId)>();
        foreach (var (rootId, objectId, hash) in tally.ExecutionProofs())
        {
            var order = sql.RehashProofOrder(rootId, objectId, hash);
            if (order == 0) continue;
            var side = rootId == pair.A.RootId ? scan.A : scan.B;
            side.RehashThrough = Math.Max(side.RehashThrough, order);
            folded.Add((rootId, objectId));
        }
        return folded;
    }

    private void TraceAcknowledgedExecutionProofs(IReadOnlyList<(string RootId, string ObjectId)> proofs) =>
        RunTrace.Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("execution-proofs-acknowledged"),
            proofs.Count(proof => _store.RehashRevision(proof.RootId, proof.ObjectId) == 0)));

    internal sealed partial class Tally
    {
        private readonly Dictionary<(string RootId, string ObjectId), string> _executionProofs = [];

        private void RecordExecutionProof(PairDef pair, SyncAction? action, ActionResult result)
        {
            if (result.Outcome != Outcome.Done || action is null ||
                action.Kind is not (ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth)) return;
            var root = action.Target == Side.B ? pair.A : pair.B;
            var source = action.Target == Side.B ? result.NewA : result.NewB;
            if (root.Provider != SyncProviders.Local || source is not { Kind: ItemKind.File, Hash: { } hash } ||
                source.ObjectId != action.Source?.ObjectId) return;
            lock (_actionGate) _executionProofs[(root.RootId, source.ObjectId)] = hash;
        }

        internal IReadOnlyList<(string RootId, string ObjectId, string Hash)> ExecutionProofs()
        {
            lock (_actionGate) return _executionProofs.Select(entry =>
                (entry.Key.RootId, entry.Key.ObjectId, entry.Value)).ToArray();
        }
    }
}
