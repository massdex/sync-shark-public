using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>Owns consumption of exact historical deletion eligibility inside the state mutation transaction.</summary>
internal static class DeletedHistory
{
    internal static void Spend(SqliteConnection connection, SqliteTransaction transaction, string pairId, string key)
    {
        using var command = StorageCommand.Create(connection, transaction, StateSchema.SpendDeletedKey,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamPath, key));
        command.ExecuteNonQuery();
    }
}
