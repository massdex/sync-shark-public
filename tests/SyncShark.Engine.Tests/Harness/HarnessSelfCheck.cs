using System.Net;
using System.Net.Sockets;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>
/// Self-checks the two harness seams this lane owns, on real code: the SQLite statement/row observer on a real
/// connection, and the synthetic loopback browser against the real ReceiveCallbackAsync verification. A new check
/// cannot be registered from this lane, so it runs inside the registered harness entry point that calls it.
/// </summary>
internal static class HarnessSelfCheck
{
    internal static async Task RunAsync(CancellationToken ct)
    {
        await SqlObserverAsync(ct);
        await LoopbackSeamAsync(ct);
    }

    private static async Task SqlObserverAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), "syncshark-harness-sql-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var db = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            await db.OpenAsync(ct);
            using (var create = db.CreateCommand())
            {
                create.CommandText = "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);";
                await create.ExecuteNonQueryAsync(ct);
            }
            var trace = SqlTrace.Attach(db);
            using (var insert = db.CreateCommand())
            {
                insert.CommandText = "INSERT INTO t(v) VALUES('a'),('b');";
                await insert.ExecuteNonQueryAsync(ct);
            }
            using (var update = db.CreateCommand())
            {
                update.CommandText = "UPDATE t SET v='c' WHERE id<=1;";
                await update.ExecuteNonQueryAsync(ct);
            }
            var statements = trace.Statements;
            trace.Detach();
            var rows = trace.TotalChanges();
            using (var after = db.CreateCommand())
            {
                after.CommandText = "DELETE FROM t;";
                await after.ExecuteNonQueryAsync(ct);
            }
            Check.Require(statements >= 2, $"SQL observer did not see the phase statements ({statements})");
            Check.Require(rows == 3, $"SQL observer saw {rows} row changes, expected 3");
            Check.Require(trace.Statements == statements, "SQL observer kept counting after Detach");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task LoopbackSeamAsync(CancellationToken ct)
    {
        var redirect = new Uri("http://127.0.0.1:" + FreePort() + "/oauth/dropbox/callback/");
        var browser = LoopbackBrowser.For(redirect, "code-1", "state-a");
        var code = await SignIn.ReceiveCallbackAsync(redirect, redirect.AbsolutePath, "state-a", ct, browser.Action);
        await browser.Drain;
        Check.Require(code == "code-1", "Synthetic loopback exchange did not return the verified code");

        var wrongRedirect = new Uri("http://127.0.0.1:" + FreePort() + "/oauth/dropbox/callback/");
        var wrong = LoopbackBrowser.For(wrongRedirect, "code-2", "state-wrong");
        var refused = false;
        try { await SignIn.ReceiveCallbackAsync(wrongRedirect, wrongRedirect.AbsolutePath, "state-a", ct, wrong.Action); }
        catch (InvalidDataException) { refused = true; }
        catch (InvalidOperationException) { refused = true; }
        await wrong.Drain;
        Check.Require(refused, "Real state validation accepted a wrong-state loopback return");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
