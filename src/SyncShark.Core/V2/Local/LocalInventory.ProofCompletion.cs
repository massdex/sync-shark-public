namespace Massdex.SyncShark.V2;

public sealed partial class LocalInventory
{
    private long ReflagReread(ItemState item)
    {
        _known.RequireRehash(_root.RootId, item, MessageCatalog.Default.Message("proof-unavailable-changed"));
        return _known.RehashRevision(_root.RootId, item.ObjectId);
    }

    private static bool SameContentProof(ItemState item, LocalGapHashResult result) =>
        result is { Outcome: LocalGapHashOutcome.Verified, Hash: { } hash, Proof: { } proof } &&
        item.Hash == hash && LocalGapWorker.Matches(item, proof.File);

    private bool RetireExcludedProof(ILocalGapIo io, ItemState item, long revision)
    {
        if (!io.Excluded(_root, item)) return false;
        _known.RetireIndexingRehash(_root.RootId, item, revision);
        return true;
    }

    private bool CompleteContentProof(ItemState item, long revision, LocalGapHashResult result)
    {
        if (result.Outcome == LocalGapHashOutcome.NotPairWork)
        {
            _known.RetireIndexingRehash(_root.RootId, item, revision);
            return false;
        }
        if (result.Failure is { RootScoped: true } rootFailure)
        {
            if (_known.IndexRetryDue(_root.RootId)) _known.RecordIndexFailure(_root.RootId, rootFailure.Encode());
            return false;
        }
        if (result.QuarantineCode is { } code)
            PersistQuarantine(new PilotDecision(item.Path, PilotDecisionKind.Blocked,
                result.Reason ?? MessageCatalog.Default.Message("proof-required")) { QuarantineCode = code, QuarantineFile = true });
        if (result is { Outcome: LocalGapHashOutcome.Verified, Hash: { } hash, Proof: { } proof } &&
            LocalGapWorker.Matches(item, proof.File) && _known.CompleteIndexingRehash(_root.RootId, item, hash, revision))
        {
            CompletePointerVerification(item);
            return true;
        }
        if (result.Outcome is LocalGapHashOutcome.Verified or LocalGapHashOutcome.Unverifiable or LocalGapHashOutcome.ChangedDuringRead)
        {
            var reason = result.Diagnostic ?? new ContentProofFailure(ContentProofFailure.StageKind.AfterRead,
                MessageCatalog.Default.Message("proof-unavailable-changed")).Encode();
            _known.FailIndexingRehash(_root.RootId, item, revision, reason);
            RunTrace.Emit(reason);
        }
        return false;
    }
}
