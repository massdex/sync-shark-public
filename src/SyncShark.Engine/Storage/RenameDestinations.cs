using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Finds other saved bindings at a rename's exact destination keys inside one caller-owned snapshot.</summary>
internal static class RenameDestinations
{
    internal static IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> All(
        SqliteConnection connection, SqliteTransaction transaction, string pairId, SyncedEntry? before,
        SyncedEntry after)
    {
        var owners = new Dictionary<string, (SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)>();
        foreach (var (binding, side) in Matches(connection, transaction, pairId, before, after))
        {
            owners.TryGetValue(binding.LocalKey, out var prior);
            owners[binding.LocalKey] = (binding, prior.LocalKeyMatched || side == Side.Local,
                prior.DropboxKeyMatched || side == Side.Dropbox);
        }
        return owners.Values.ToArray();
    }

    internal static Side? FirstOther(SqliteConnection connection, SqliteTransaction transaction,
        string pairId, SyncedEntry? before, SyncedEntry after)
    {
        foreach (var (_, side) in Matches(connection, transaction, pairId, before, after)) return side;
        return null;
    }

    private static IEnumerable<(SyncedEntry Binding, Side Side)> Matches(SqliteConnection connection,
        SqliteTransaction transaction, string pairId, SyncedEntry? before, SyncedEntry after)
    {
        foreach (var side in new[] { Side.Local, Side.Dropbox })
        {
            using var command = StorageCommand.Create(connection, transaction,
                side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey,
                (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, after.KeyOn(side)));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var binding = StateEncoding.ReadSynced(reader,
                    reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath);
                // A binding's key, not its file identity, distinguishes a separate hard-link owner.
                if (binding.LocalKey != before?.LocalKey) yield return (binding, side);
            }
        }
    }
}
