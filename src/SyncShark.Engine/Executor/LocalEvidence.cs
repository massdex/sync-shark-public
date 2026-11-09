namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Captures source applicability once and reads only actual acquired evidence for an observed Local subject.</summary>
internal sealed class LocalEvidence(IStateStore store, PairConfig pair, long contextGeneration)
{
    private readonly object _gate = new();
    private (long Epoch, string Domain)? _source;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Entry, DirtyProof> _proofs = new();

    internal sealed record DirtyProof(bool Dirty, JournalPosition? Boundary, long Version,
        SyncedEntry? Saved = null, string? Hash = null);

    internal DirtyProof Remember(Row row, DirtyProof evidence, string? hash = null)
    {
        var proof = evidence with { Saved = row.Synced, Hash = hash };
        lock (_gate)
        {
            _proofs.Remove(row.Local!);
            _proofs.Add(row.Local!, proof);
        }
        return proof;
    }

    internal DirtyProof? Capture(Row row)
    {
        if (row.Local is null) return null;
        lock (_gate) return _proofs.TryGetValue(row.Local, out var proof) ? proof : null;
    }

    internal void Release(Row row, DirtyProof? owned)
    {
        if (owned?.Hash is null || row.Local is null) return;
        lock (_gate)
        {
            if (_proofs.TryGetValue(row.Local, out var current) && ReferenceEquals(current, owned))
                _proofs.Remove(row.Local);
        }
    }

    internal string? CurrentHash(Row row, DirtyProof? evidence)
    {
        if (row.LocalHash is null || evidence is null) return null;
        var proof = Capture(row);
        return proof?.Hash == row.LocalHash && proof.Saved == row.Synced &&
            SameFile((proof.Dirty, proof.Boundary, proof.Version),
                (evidence.Dirty, evidence.Boundary, evidence.Version)) ? proof.Hash : null;
    }

    internal string? CurrentHash(Row row)
    {
        if (row.LocalHash is null) return null;
        var proof = Capture(row);
        return proof?.Hash == row.LocalHash && proof.Saved == row.Synced ? proof.Hash : null;
    }

    internal (Row Row, DirtyProof? Evidence) Rebind(Row prepared, Row fresh, Entry observed)
    {
        DirtyProof? before;
        DirtyProof? target;
        lock (_gate)
        {
            before = Capture(prepared);
            target = Capture(fresh);
        }
        if (before is null || fresh.Local is null) return (fresh, null);
        var reading = Read(fresh.Local, fresh.Synced);
        var evidence = new DirtyProof(reading.Dirty, reading.Boundary, reading.Version);
        if (string.IsNullOrEmpty(observed.ContentHash) || !AppliesTo(prepared) || !AppliesTo(fresh) ||
            before.Saved != prepared.Synced || prepared.Synced != fresh.Synced ||
            !SameObservation(prepared.Local!, observed) || !SameObservation(fresh.Local, observed) ||
            !SameFile((before.Dirty, before.Boundary, before.Version), reading)) return (fresh, evidence);
        var rebound = fresh with { LocalHash = observed.ContentHash };
        lock (_gate)
        {
            // Validation may race a newer preparation; it cannot replace that preparation's authority.
            if (!ReferenceEquals(before, Capture(prepared)) || !ReferenceEquals(target, Capture(fresh)))
                return (fresh, evidence);
            Remember(rebound, evidence, observed.ContentHash);
        }
        return (rebound, evidence);
    }

    private static bool SameObservation(Entry selected, Entry observed) =>
        selected.Id == observed.Id && selected.Path == observed.Path && selected.IsFolder == observed.IsFolder &&
        selected.Size == observed.Size && selected.Modified == observed.Modified;

    internal static bool SameFile((bool Dirty, JournalPosition? Boundary, long Version) before,
        (bool Dirty, JournalPosition? Boundary, long Version) after) =>
        before.Dirty == after.Dirty && before.Version == after.Version;

    internal static bool AppliesTo(Row row) =>
        row.Local is { IsFolder: false, Skip: null } && row.Synced is { IsFolder: false };

    internal async Task<(string Hash, (bool Dirty, JournalPosition? Boundary, long Version) Proof)> HashAsync(
        ISide localSide, Entry entry, SyncedEntry? saved, (bool Dirty, JournalPosition? Boundary, long Version) before,
        CancellationToken ct)
    {
        var hash = await localSide.ContentHashAsync(entry, ct).ConfigureAwait(false);
        var after = Read(entry, saved);
        if (!SameFile(before, after)) throw new ChangedUnderneathException(entry.Path);
        return (hash, after);
    }

    internal long ReadEpoch() => (store.ReadSource(pair.PairId, Side.Local) ??
        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged)).Epoch;

    internal (bool Dirty, JournalPosition? Boundary, long Version) Read(Entry local, SyncedEntry? saved)
    {
        (long Epoch, string Domain) source;
        lock (_gate)
        {
            if (_source is null)
            {
                var epoch = ReadEpoch();
                var roots = pair.Roots ??
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
                _source = (epoch, roots.LocalVolumeKey());
            }
            source = _source.Value;
        }
        var result = store.ReadLocalDirty(pair.PairId, contextGeneration,
            source.Epoch, source.Domain, local.Id, PathNames.Key(local.Path), saved?.LocalKey);
        if (result.SourceBoundary is null)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        return (result.Dirty, result.SourceBoundary, result.EvidenceVersion);
    }
}
