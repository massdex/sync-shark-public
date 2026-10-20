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
