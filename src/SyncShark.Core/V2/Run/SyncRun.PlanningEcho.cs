namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    internal Action? PlanningAcknowledgementForTest { get; set; }
    internal Action<SqliteStateStore.DeferredPlanning, long?, bool, string>? PlanningEchoForTest { get; set; }

    private void WriteHashObservation(PairDef pair, SideScan scan, Delta observation)
    {
        if (_store is SqliteStateStore observedStore)
            foreach (var (id, version) in observedStore.WriteHashObservation(pair.PairId, observation))
                scan.PlanningObservationVersions[id] = version;
        else _store.WriteDelta(observation);
    }

    private void AcknowledgePlanningObservations(PairDef pair, BothScan scan,
        IEnumerable<SqliteStateStore.DeferredPlanning>? deferredPlanning, Func<string, bool> settled,
        Func<string, bool> emptySettled, Action<SqliteStateStore.DeferredPlanning>? acknowledged,
        Action<SqliteStateStore.DeferredPlanning, string>? blocked, CancellationToken ct)
    {
        PlanningAcknowledgementForTest?.Invoke();
        var echoesRetired = 0;
        if (deferredPlanning is not null && _store is SqliteStateStore deferredStore)
            foreach (var page in deferredPlanning.Chunk(256))
            {
                ct.ThrowIfCancellationRequested();
                var folded = page.Select(row => FoldPlanningObservation(pair, scan, row)).ToArray();
                var echoes = folded.Where((row, index) => row.Version != page[index].Version).ToHashSet();
                var echoPaths = echoes.SelectMany(row => row.Paths).ToHashSet(StringComparer.OrdinalIgnoreCase);
                deferredStore.AcknowledgeDeferredPlanning(pair.PairId, folded,
                    path => settled(path) && (!echoPaths.Contains(path) || PlanningEchoVerified(pair, path)),
                    path => !echoPaths.Contains(path) && emptySettled(path),
                    row => { if (echoes.Contains(row)) echoesRetired++; acknowledged?.Invoke(row); },
                    count => { if (count > 0) RunTrace.Emit($"Deferred planning re-spell decisions: count={count}"); },
                    (row, reason) =>
                    {
                        PlanningEchoForTest?.Invoke(row, PlanningObservationVersion(pair, scan, row), row.Paths.All(settled), reason);
                        blocked?.Invoke(row, reason);
                    });
            }
        RunTrace.Emit(string.Format(System.Globalization.CultureInfo.InvariantCulture,
            MessageCatalog.Default.Template("deferred-echoes-retired"), echoesRetired));
    }

    private static SqliteStateStore.DeferredPlanning FoldPlanningObservation(
        PairDef pair, BothScan scan, SqliteStateStore.DeferredPlanning row)
    {
        var version = PlanningObservationVersion(pair, scan, row);
        return version is { } observed && observed > row.Version ? row with { Version = observed } : row;
    }

    private static long? PlanningObservationVersion(PairDef pair, BothScan scan, SqliteStateStore.DeferredPlanning row)
    {
        var versions = row.RootId == pair.A.RootId ? scan.A.PlanningObservationVersions :
            row.RootId == pair.B.RootId ? scan.B.PlanningObservationVersions : null;
        return versions is not null && versions.TryGetValue(row.ObjectId, out var version) ? version : null;
    }

    private bool PlanningEchoVerified(PairDef pair, string path)
    {
        var a = _store.Live(pair.A.RootId, [path]);
        var b = _store.Live(pair.B.RootId, [path]);
        return a.Count == 1 && b.Count == 1 &&
            VerificationPolicy.CanAcknowledge(a[0].Hash, a[0].Flags) &&
            VerificationPolicy.CanAcknowledge(b[0].Hash, b[0].Flags);
    }
}
