using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Notice recovery asks about the entity's own verification obligation, not the root's
    // unpublished discovery frontier. Accepted results leave planning debt, not read failure.
    IReadOnlySet<string> ILocalGapStore.GapObligations(string rootId, IReadOnlyList<string> objectIds) => InStore(() =>
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var root = RootNo(rootId);
        if (root is null || objectIds.Count == 0) return result;
        using var command = StoreSchema.Command(_connection, null,
            "SELECT object_id FROM local_gap_item WHERE root_no=$r " +
            "AND object_id IN (SELECT value FROM json_each($ids));");
        command.Parameters.AddWithValue("$r", root.Value);
        command.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(objectIds));
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    });
}
