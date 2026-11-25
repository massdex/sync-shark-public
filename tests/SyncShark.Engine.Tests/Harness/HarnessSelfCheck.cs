using System.Net.Http;
using Massdex.SyncShark.Engine.Dropbox;

namespace Massdex.SyncShark.Engine.Tests.Harness;

/// <summary>
/// Self-checks the two harness seams this lane owns, on real code: the SQLite statement/row observer across the
/// connections a phase uses, and the synthetic loopback browser against the public account-connect flow. A
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
            using var start = new Barrier(connections.Count);
            await Task.WhenAll(connections.Select(db => Task.Run(() => { start.SignalAndWait(ct); Burst(db, 50); }, ct)));
            Check.Require(trace.Statements == 103, $"Concurrent windows saw {trace.Statements} statements since Attach, expected 103");
            Check.Require(trace.TotalChanges == 104, $"Concurrent windows counted {trace.TotalChanges} rows since Attach, expected 104");
            trace.Detach();
            foreach (var db in connections) await ExecAsync(db, "INSERT INTO t(v) VALUES('after');", ct);
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
        var browser = new LoopbackBrowser(wrongState: true);
        using var http = new HttpClient(new Dropbox.DropboxSideChecks.ReplyWire((_, _) => throw new InvalidOperationException("Wrong state reached token exchange")));
        var api = new DropboxApi(http, TimeProvider.System, SettingsParser.Load(), _ => { });
        try { await new SignIn().ConnectAccountAsync(api, ct, browser.Open); }
        catch (InvalidDataException e) when (e.Message == Text.Get(TextKey.SignInReturnRetry))
        {
            Check.Require(browser.Receipt is {} receipt && (await receipt.WaitAsync(ct)).EndsWith(SignInCallback.AcknowledgementPage(false), StringComparison.Ordinal),
                "Rejected browser return was not acknowledged by the listener");
            return;
        }
        throw new InvalidDataException("Sign-in accepted a return with the wrong state");
    }
}
