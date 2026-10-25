using ProofStage = Massdex.SyncShark.V2.ContentProofFailure.StageKind;

namespace Massdex.SyncShark.V2;

internal sealed partial class LocalGapIo
{
    // Read only parent-listing metadata, including ancestors before opening their directories.
    // The scanner owns cloud/link classification; no content or hydration occurs here.
    private async Task<LocalGapHashResult?> RefreshClassificationAsync(RootDef root, ItemState item,
        PilotDirectoryLease lease, CancellationToken ct)
    {
        foreach (var path in PairPath.Ancestors(item.Path).Reverse().Append(item.Path))
        {
            var cache = path != item.Path && _proofPass.Value?.RootId == root.RootId ? _proofPass.Value.Ancestors : null;
            if (cache is null || !cache.TryGetValue(path, out var classified))
            {
                classified = await ReadClassificationAsync(root, item, path, lease, ct).ConfigureAwait(false);
                if (cache is not null) cache[path] = classified;
            }
            var (observed, failure) = classified;
            if (failure is not null) return failure;
            if (path != item.Path) continue;
            if (observed!.ObjectId != item.ObjectId || observed.Kind != item.Kind || observed.Size != item.Size || observed.Mtime != item.Mtime)
            {
                CachedIdentityRefresh.TryQueue(known, root, item, 87, Time.GetUtcNow());
                return new(LocalGapHashOutcome.IdentityChanged, Reason: CachedIdentityRefresh.PendingReason);
            }
            if ((observed.Flags & ItemFlags.Unreadable) != 0)
                return new(LocalGapHashOutcome.Unverifiable, Reason: "proof:classification-unreadable");
        }
        return null;
    }

    private async Task<(ItemState? Item, LocalGapHashResult? Failure)> ReadClassificationAsync(RootDef root, ItemState item,
        string path, PilotDirectoryLease lease, CancellationToken ct)
    {
        using var guard = LocalItems.GuardRefreshPaths(root.RootPath, root.RootId, known, [path]);
        if (guard.Safe.Count == 0) return (null, new(LocalGapHashOutcome.Unverifiable, Reason: "proof:parent-identity-unavailable")
        { Failure = guard.Failure ?? new(ProofStage.ParentIdentity, MessageCatalog.Default.Message("proof-parent-unsafe")) });
        var scan = await new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true }
            .ScanAsync(lease, owned, ct, refreshPaths: [path], traverseDirectory: _ => false).ConfigureAwait(false);
        if (scan.Decisions.Any(decision => decision.Kind == PilotDecisionKind.OnlineOnly))
            return (null, new(LocalGapHashOutcome.NotPairWork, Reason: "stub"));
        if (!scan.Entries.TryGetValue(path, out var entry))
        {
            if (scan.Decisions.FirstOrDefault() is { } decision)
                return (null, LocalGapHashResult.Failed(ProofStage.Classification, decision.Reason, decision.NativeError));
            if (CachedIdentityRefresh.TryQueue(known, root, item, 87, Time.GetUtcNow()))
                return (null, new(LocalGapHashOutcome.IdentityChanged, Reason: CachedIdentityRefresh.PendingReason));
            return (null, LocalGapHashResult.Failed(ProofStage.Classification, MessageCatalog.Default.Message("proof-path-unknown")));
        }
        var observed = LocalItems.FromScan(entry);
        var classification = Eligibility(root, observed, checkHolds: false);
        return classification.Eligible ? (observed, null) :
            (null, new(classification.NotPairWork ? LocalGapHashOutcome.NotPairWork : LocalGapHashOutcome.Unverifiable,
                Reason: classification.Reason));
    }
}
