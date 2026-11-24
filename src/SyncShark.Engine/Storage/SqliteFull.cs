using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>SQLite FULL is Storage's disk-full fact; it leaves Storage as the shared normalized exception (#36),
/// with the SQLite error kept inner. The mapping reads no ambient or caller state.</summary>
internal static class SqliteFull
{
    internal static Exception Normalize(Exception error) =>
        error is SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_FULL } ? new InsufficientSpaceException(error) : error;
}
