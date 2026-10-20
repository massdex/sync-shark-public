using System.Collections;

namespace Massdex.SyncShark.V2;

/// <summary>Only our attributed interrupted delete is missing-copy evidence, never user-delete evidence.</summary>
internal static class InterruptedDeleteRepair
{
    internal static IReadOnlyList<PlanRow> Protect(IReadOnlyList<PlanRow> rows, IEnumerable<SyncRetry> retries)
    {
        var repairs = retries.Where(retry => retry.Kind == SqliteStateStore.InterruptedDeleteRestoreKind)
            .SelectMany(retry => retry.ProposedActions ?? []).Where(action => action.Source is not null)
            .GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return repairs.Count == 0 ? rows : new ProtectedRows(rows, repairs);
    }

    internal static IReadOnlySet<string> SourcePaths(RunScope.Scope scope, IReadOnlyList<SyncRetry> retries)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { Side.A, Side.B })
        {
            var proposals = retries.Where(retry => retry.Kind == SqliteStateStore.InterruptedDeleteRestoreKind)
                .SelectMany(retry => retry.ProposedActions ?? []).Where(action => action.Target == target && action.Source is not null)
                .GroupBy(action => action.Path, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Source!, StringComparer.OrdinalIgnoreCase);
            if (proposals.Count == 0) continue;
            var occupied = (target == Side.A ? scope.A : scope.B).Where(item => proposals.ContainsKey(item.Path))
                .Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var source in target == Side.A ? scope.B : scope.A)
                if (!occupied.Contains(source.Path) && proposals.TryGetValue(source.Path, out var expected) &&
                    source.ObjectId == expected.ObjectId) paths.Add(source.Path);
        }
        return paths;
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
