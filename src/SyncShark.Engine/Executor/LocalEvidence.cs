namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Captures source applicability once and reads only actual acquired evidence for an observed Local subject.</summary>
internal sealed class LocalEvidence(IStateStore store, PairConfig pair, long contextGeneration)
{
    private readonly object _gate = new();
    private (long Epoch, string Domain)? _source;

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
