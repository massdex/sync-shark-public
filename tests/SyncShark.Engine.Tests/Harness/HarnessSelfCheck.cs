using System.Net;
using System.Net.Sockets;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>
/// Self-checks the two harness seams this lane owns, on real code: the SQLite statement/row observer across the
/// connections a phase uses, and the synthetic loopback browser against the real ReceiveCallbackAsync verification. A
/// new check cannot be registered from this lane, so it runs inside the registered harness entry point that calls it.
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
        var paths = new List<string>();
        var connections = new List<Microsoft.Data.Sqlite.SqliteConnection>();
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var path = Path.Combine(Path.GetTempPath(), "syncshark-harness-sql-" + Guid.NewGuid().ToString("N") + ".db");
                paths.Add(path);
                var db = new Microsoft.Data.Sqlite.SqliteConnection(
                    new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                connections.Add(db);
                await db.OpenAsync(ct);
                await ExecAsync(db, "CREATE TABLE t(id INTEGER PRIMARY KEY, v TEXT);", ct);
            }
            var first = connections[0];
            var second = connections[1];
            // An earlier write precedes Attach, so its row must not be counted after Attach.
            await ExecAsync(first, "INSERT INTO t(v) VALUES('pre');", ct);
            var trace = SqlTrace.Attach(first, second);
            var statementsBeforeRead = trace.Statements;
            var baselineRows = trace.TotalChanges;
            Check.Require(trace.Statements == statementsBeforeRead, "Reading the row count added a SQL statement");
            await ExecAsync(first, "INSERT INTO t(v) VALUES('a'),('b');", ct);   // window 1: two rows on the writer
            await ExecAsync(second, "INSERT INTO t(v) VALUES('c');", ct);        // window 1: one row on the reader
            var afterWindow1 = trace.TotalChanges;
            await ExecAsync(first, "UPDATE t SET v='z' WHERE id=1;", ct);        // window 2: one row
            var afterWindow2 = trace.TotalChanges;
            Check.Require(baselineRows == 0, $"SQL observer counted {baselineRows} rows before the first window, expected the earlier write excluded");
            Check.Require(afterWindow1 - baselineRows == 3, $"First window counted {afterWindow1 - baselineRows} rows, expected 3");
            Check.Require(afterWindow2 - afterWindow1 == 1, $"Second window counted {afterWindow2 - afterWindow1} rows, expected 1");
            Check.Require(afterWindow2 == 4, $"SQL observer counted {afterWindow2} rows since Attach, expected 4");
            Check.Require(trace.Statements == 3, $"SQL observer saw {trace.Statements} statements since Attach, expected 3");
            // Statements on two connections running on two threads must all be counted (the counter is shared across handles).
            await Task.WhenAll(Task.Run(() => Burst(first, 50)), Task.Run(() => Burst(second, 50)));
            Check.Require(trace.Statements == 103, $"Concurrent windows saw {trace.Statements} statements since Attach, expected 103");
            Check.Require(trace.TotalChanges == 104, $"Concurrent windows counted {trace.TotalChanges} rows since Attach, expected 104");
            trace.Detach();
            await ExecAsync(first, "INSERT INTO t(v) VALUES('after');", ct);
            Check.Require(trace.Statements == 103, "SQL observer kept counting after Detach");
        }
        finally
        {
            foreach (var db in connections) await db.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var path in paths)
            {
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static async Task ExecAsync(Microsoft.Data.Sqlite.SqliteConnection db, string sql, CancellationToken ct)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    // A run of statements on one connection, so two runs on two connections execute simultaneously on separate threads
    // (Microsoft.Data.Sqlite's async methods run synchronously, so this must be a real thread to exercise the shared counter).
    private static void Burst(Microsoft.Data.Sqlite.SqliteConnection db, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO t(v) VALUES('burst');";
            command.ExecuteNonQuery();
        }
    }

    private static async Task LoopbackSeamAsync(CancellationToken ct)
    {
        var redirect = new Uri("http://127.0.0.1:" + FreePort() + "/oauth/dropbox/callback/");
        var browser = LoopbackBrowser.For(redirect, "code-1", "state-a");
        var code = await SignIn.ReceiveCallbackAsync(redirect, redirect.AbsolutePath, "state-a", ct, browser.Action);
        await browser.Drain;
        Check.Require(browser.Started && browser.Sent, "Synthetic loopback exchange never sent its callback");
        Check.Require(code == "code-1", "Synthetic loopback exchange did not return the verified code");

        var wrongRedirect = new Uri("http://127.0.0.1:" + FreePort() + "/oauth/dropbox/callback/");
        var wrong = LoopbackBrowser.For(wrongRedirect, "code-2", "state-wrong");
        var refused = false;
        try { await SignIn.ReceiveCallbackAsync(wrongRedirect, wrongRedirect.AbsolutePath, "state-a", ct, wrong.Action); }
        catch (InvalidDataException) { refused = true; }
        await wrong.Drain;
        // A listener-start failure throws InvalidOperationException before invoking the browser; it must not count as rejection.
        Check.Require(wrong.Started && wrong.Sent, "Wrong-state loopback return was rejected before its callback was sent and received");
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
