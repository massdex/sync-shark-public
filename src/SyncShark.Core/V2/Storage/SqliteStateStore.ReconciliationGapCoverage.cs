using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private static readonly AsyncLocal<ReconciliationGapCoverage?> BatchGapCoverage = new();

    internal sealed class ReconciliationGapCoverage : IDisposable
    {
        private readonly ReconciliationGapCoverage? _previous;
        internal readonly SqliteStateStore Owner;
        internal readonly IReadOnlyDictionary<long, string> Epochs;
        internal IReadOnlySet<string> Observed { get; private set; } = new HashSet<string>();
        internal string PathsJson { get; private set; } = "[]";
        internal bool Active { get; private set; } = true;

        internal ReconciliationGapCoverage(SqliteStateStore owner, IReadOnlyDictionary<long, string> epochs)
        {
            Owner = owner; Epochs = epochs; _previous = BatchGapCoverage.Value; BatchGapCoverage.Value = this;
        }

        internal void Observe(IEnumerable<string> paths, IReadOnlyCollection<string> selectedIds)
        {
            Observed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            PathsJson = JsonSerializer.Serialize(Observed.Select(path =>
            {
                var key = RowCodec.Split(RowCodec.Key(path));
                return new { dir = key.Parent, name = key.Name };
            }));
            Owner.StageObservedGapPlanning(this, selectedIds);
        }

        public void Dispose() { Active = false; BatchGapCoverage.Value = _previous; }
    }

    internal ReconciliationGapCoverage BeginReconciliationGapCoverage(string pairId)
    {
        var epochs = InStore(() =>
        {
            var result = new Dictionary<long, string>();
            if (Pair(pairId) is not { } pair) return result;
            foreach (var root in new[] { pair.A, pair.B })
                if (RootNo(root.RootId) is { } number && ReadGap(number) is { } epoch)
                    result[number] = epoch.Epoch;
            return result;
        });
        return new(this, epochs);
    }

    private static string GapObservedPaths(SqliteConnection connection, SqliteTransaction? tx, long root)
    {
        var scope = BatchGapCoverage.Value;
        if (scope is not { Active: true } || connection.DataSource != scope.Owner._connection.DataSource ||
            !scope.Epochs.TryGetValue(root, out var epoch)) return "[]";
        return StoreSchema.Scalar(connection, tx, "SELECT 1 FROM main.local_gap WHERE root_no=$r AND epoch=$e;",
            ("$r", root), ("$e", epoch)) is null ? "[]" : scope.PathsJson;
    }

    private void StageObservedGapPlanning(ReconciliationGapCoverage scope, IReadOnlyCollection<string> selectedIds) => InStore(() =>
    {
        var epochs = scope.Epochs.Where(entry => ReadGap(entry.Key)?.Epoch == entry.Value).ToArray();
        using var tx = _connection.BeginTransaction();
        foreach (var (root, epoch) in epochs)
        {
            var items = scope.Observed.SelectMany(path => _reads.AtKey(root, path, tx).Select(row => row.Item)).ToList();
            using (var command = StoreSchema.Command(_connection, tx,
                "SELECT expected_json FROM json_each($ids) j CROSS JOIN local_gap_item i " +
                "WHERE i.root_no=$r AND i.epoch=$e AND i.object_id=j.value;"))
            {
                command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(selectedIds));
                command.Parameters.AddWithValue("$r", root); command.Parameters.AddWithValue("$e", epoch);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (JsonSerializer.Deserialize<ItemState>(reader.GetString(0)) is { } item && scope.Observed.Contains(item.Path))
                        items.Add(item);
            }
            MarkPlanningDirty(tx, root, items.Where(item => scope.Observed.Contains(item.Path))
                .Select(item => (item.ObjectId, item.Path)).Distinct().ToArray());
        }
        tx.Commit();
    });

    private void AcknowledgeReconciliationGap(SqliteTransaction tx, DeferredPlanning row)
    {
        var scope = BatchGapCoverage.Value;
        if (scope is not { Active: true } || scope.Owner != this || !scope.Epochs.TryGetValue(row.Root, out var epoch) ||
            row.Paths.Count == 0 || !row.Paths.All(scope.Observed.Contains)) return;
        // Called only after the ordinary all-alias, settled, version-guarded planning acknowledgement succeeds.
        StoreSchema.Execute(_connection, tx,
            "DELETE FROM local_gap_item WHERE root_no=$r AND object_id=$o AND epoch=$e AND EXISTS(" +
            "SELECT 1 FROM local_gap WHERE root_no=$r AND epoch=$e);",
            ("$r", row.Root), ("$o", row.ObjectId), ("$e", epoch));
    }
}
