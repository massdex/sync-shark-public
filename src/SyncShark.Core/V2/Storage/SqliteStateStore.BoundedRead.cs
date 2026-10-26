using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    /// <summary>
    /// Runs one caller-supplied read under the store gate with a hard deadline. SQLite's progress handler
    /// aborts a statement that is still executing when the token is cancelled, and the token is checked
    /// before and after the read, so the read itself is bounded rather than only its gate wait. The read
    /// runs on the caller's thread: expiry can never leave an abandoned task holding the gate.
    /// </summary>
    internal T BoundedRead<T>(CancellationToken token, Func<CancellationToken, T> read)
    {
        using var admission = FairStoreGate.WithCancellation(token);
        return InStore(() =>
        {
            token.ThrowIfCancellationRequested();
            var handle = _connection.Handle;
            SQLitePCL.delegate_progress progress = _ => token.IsCancellationRequested ? 1 : 0;
            SQLitePCL.raw.sqlite3_progress_handler(handle, 1000, progress, progress);
            try
            {
                var result = read(token);
                token.ThrowIfCancellationRequested();
                return result;
            }
            catch (SqliteException error) when (error.SqliteErrorCode == SQLitePCL.raw.SQLITE_INTERRUPT)
            {
                throw new OperationCanceledException("The bounded store read was aborted at its deadline.", error, token);
            }
            finally
            {
                SQLitePCL.raw.sqlite3_progress_handler(handle, 0, progress, progress);
                GC.KeepAlive(progress);
            }
        }, "bounded read");
    }
}
