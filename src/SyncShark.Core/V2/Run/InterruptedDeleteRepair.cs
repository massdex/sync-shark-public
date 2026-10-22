using System.Collections;

namespace Massdex.SyncShark.V2;

/// <summary>Only our attributed interrupted delete is missing-copy evidence, never user-delete evidence.</summary>
internal static class InterruptedDeleteRepair
{
    // Fetch only this page's attribution, including backed-off repairs beyond the due-work page.
    internal static IReadOnlyList<PlanRow> Protect(IReadOnlyList<PlanRow> rows, IStateStore store, string pairId) =>
        new BatchedRows(rows, store, pairId);

    internal static IReadOnlySet<string> SourcePaths(RunScope.Scope scope, IStateStore store, string pairId)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { Side.A, Side.B })
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in (target == Side.A ? scope.B : scope.A).Chunk(256))
            {
                var proposals = RepairProposals(store, pairId, batch.Select(item => item.Path).ToArray())
                    .Where(action => action.Target == target && action.Source is not null)
                    .GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var item in batch)
                    if (proposals.TryGetValue(item.Path, out var proposal) && item.ObjectId == proposal.Source!.ObjectId)
                        candidates.Add(item.Path);
            }
            // Retain only matching source path strings, not another inventory or all retry payloads.
            foreach (var item in target == Side.A ? scope.A : scope.B) candidates.Remove(item.Path);
            paths.UnionWith(candidates);
        }
        return paths;
    }

    private sealed class BatchedRows(IReadOnlyList<PlanRow> rows, IStateStore store, string pairId) : IReadOnlyList<PlanRow>
    {
        private int _start = -1;
        private IReadOnlyList<PlanRow> _batch = [];
        public int Count => rows.Count;
        public PlanRow this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                var start = index / 256 * 256;
                if (_start != start)
                {
                    var page = Enumerable.Range(start, Math.Min(256, Count - start)).Select(i => rows[i]).ToArray();
                    var paths = page.Where(row => row.Base is null && (row.A is null) != (row.B is null))
                        .Select(row => (row.A ?? row.B)!.Path).ToArray();
                    _batch = ProtectProposals(page, RepairProposals(store, pairId, paths));
                    _start = start;
                }
                return _batch[index - start];
            }
        }
        public IEnumerator<PlanRow> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    // Both mechanisms attribute a missing copy to an explicit repair, not a new user deletion.
    // Recovery ready notices bind Source to the VERIFIED restored object, never the archive object's identity.
    private static IEnumerable<SyncAction> RepairProposals(IStateStore store, string pairId, IReadOnlyCollection<string> paths) =>
        store.RecoveryRestoreReadyAt(pairId, paths)
            .SelectMany(notice => notice.ConflictOperation?.Actions ?? [])
            .Where(SyncRun.IsRecoveryRestoreIntent)
            .Select(action => action with { Target = action.Target == Side.A ? Side.B : Side.A })
            .Concat(store.InterruptedDeleteRepairsAt(pairId, paths).SelectMany(retry => retry.ProposedActions ?? []));

    internal static IReadOnlyList<PlanRow> Protect(IReadOnlyList<PlanRow> rows, IEnumerable<SyncRetry> retries) =>
        ProtectProposals(rows, retries.Where(retry => retry.Kind == SqliteStateStore.InterruptedDeleteRestoreKind)
            .SelectMany(retry => retry.ProposedActions ?? []));

    private static IReadOnlyList<PlanRow> ProtectProposals(IReadOnlyList<PlanRow> rows, IEnumerable<SyncAction> proposals)
    {
        var repairs = proposals.Where(action => action.Source is not null)
            .GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return repairs.Count == 0 ? rows : new ProtectedRows(rows, repairs);
    }

    internal static IReadOnlyDictionary<string, PrepareMatch>? WithoutMatches(
        IReadOnlyDictionary<string, PrepareMatch>? matches, IReadOnlySet<string> repairs) =>
        matches is null || repairs.Count == 0 ? matches : new FilteredMatches(matches, repairs);

    private sealed class FilteredMatches(IReadOnlyDictionary<string, PrepareMatch> inner,
        IReadOnlySet<string> repairs) : IReadOnlyDictionary<string, PrepareMatch>
    {
        public PrepareMatch this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);
        public bool ContainsKey(string key) => !repairs.Contains(key) && inner.ContainsKey(key);
        public bool TryGetValue(string key, out PrepareMatch value)
        {
            value = null!;
            return !repairs.Contains(key) && inner.TryGetValue(key, out value!);
        }
        public IEnumerable<string> Keys => inner.Keys.Where(key => !repairs.Contains(key));
        public IEnumerable<PrepareMatch> Values => this.Select(entry => entry.Value);
        public int Count => Keys.Count();
        public IEnumerator<KeyValuePair<string, PrepareMatch>> GetEnumerator() =>
            inner.Where(entry => !repairs.Contains(entry.Key)).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ProtectedRows(IReadOnlyList<PlanRow> rows, Dictionary<string, SyncAction> repairs) : IReadOnlyList<PlanRow>
    {
        public int Count => rows.Count;
        public PlanRow this[int index]
        {
            get
            {
                var row = rows[index];
                var present = row.A ?? row.B;
                if (row.Base is not null || present is null || (row.A is null) == (row.B is null) ||
                    !repairs.TryGetValue(present.Path, out var repair) ||
                    (repair.Target == Side.A ? row.A : row.B) is not null ||
                    !string.Equals(present.ObjectId, repair.Source?.ObjectId, StringComparison.Ordinal)) return row;
                return row with { Ev = repair.Target == Side.A
                    ? row.Ev with { DeletedA = false, DeletedAtA = null, DeletedHashA = null }
                    : row.Ev with { DeletedB = false, DeletedAtB = null, DeletedHashB = null } };
            }
        }
        public IEnumerator<PlanRow> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
