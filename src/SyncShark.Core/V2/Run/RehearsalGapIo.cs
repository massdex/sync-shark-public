namespace Massdex.SyncShark.V2;

/// <summary>Gap verification reads the same virtual metadata as the foreground rehearsal inventory.</summary>
internal sealed class RehearsalGapIo(ILocalGapIo real, RehearsalOverlay overlay, IStateStore store) : ILocalGapIo
{
    public bool Paused => real.Paused;
    public string PauseReason => real.PauseReason;
    public bool Excluded(RootDef root, ItemState item) => real.Excluded(root, item);
    private bool Virtual(RootDef root, ItemState item, out LocalGapProof? proof)
    {
        proof = null;
        if (!overlay.Try(root.RootId, item.Path, out var state)) return false;
        if (state is null || state.ObjectId != item.ObjectId || state.Size != item.Size || state.Mtime != item.Mtime) return true;
        var cursor = store.Cursor(root.RootId) ?? (store as ILocalGapStore)?.GapEpoch(root.RootId)?.JournalCursor;
        if (cursor is null || !NtfsUsnCheckpoint.TryParseCursor(cursor, out _, out _)) return true;
        var identity = LocalItems.ParseObjectId(state.ObjectId);
        proof = new LocalGapProof(cursor, new NtfsFileUsn(identity.Volume, identity.FileId, 0,
            state.Size, state.Mtime.UtcDateTime.ToFileTimeUtc()));
        return true;
    }
    public LocalGapProof? Probe(RootDef root, ItemState item) => Virtual(root, item, out var proof) ? proof : real.Probe(root, item);
    public ILocalGapProbe OpenProbes(RootDef root) => new Probes(this, root, real.OpenProbes(root));
    public Task<LocalGapHashResult> HashAsync(RootDef root, ItemState item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Virtual(root, item, out var proof)) return real.HashAsync(root, item, ct);
        if (proof is null || !overlay.Try(root.RootId, item.Path, out var state) || state?.Hash is not { } hash)
            return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Unverifiable));
        return Task.FromResult(new LocalGapHashResult(LocalGapHashOutcome.Verified, hash, proof));
    }
    private sealed class Probes(RehearsalGapIo owner, RootDef root, ILocalGapProbe real) : ILocalGapProbe
    {
        public LocalGapProof? Probe(ItemState item) => owner.Virtual(root, item, out var proof) ? proof : real.Probe(item);
        public string? UnavailableReason => real.UnavailableReason;
        public string? LastFailureReason => real.LastFailureReason;
        public void Dispose() => real.Dispose();
    }
}
