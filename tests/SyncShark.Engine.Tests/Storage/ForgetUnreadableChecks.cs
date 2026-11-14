using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace Massdex.SyncShark.Engine.Tests.Storage;

/// <summary>Checks forget guard mapping, statement-snapshot freshness and constant lookup counts.</summary>
internal static class ForgetUnreadableChecks
{
    private const string Pair = "forget-pair";
    private const string BelowPrefix = "SELECT path_key FROM issue_member INDEXED BY issue_member_skip WHERE pair_id=";

    internal static void Run(int count = 4, bool budget = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            using var store = StateStore.Open(Path.Combine(directory, "state.db"));
            store.SavePair(new PairConfig(Pair, Pair, directory, "/Parent", "account", [], [],
                FirstRunPolicy.NewestWins, true, null));
            var root = Saved("Local", "Remote", true);
            var children = Enumerable.Range(0, count).Select(index =>
                Saved("Local/Child" + index, "Remote/Child" + index, true)).ToArray();
            store.Commit(Pair, StateChange.Empty with { Record = [root, .. children] });
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(directory, "state.db"), Pooling = false }.ToString());
            StateStore.OpenConnection(connection);
            (Side Side, string Key)[][] orders =
            [
                [(Side.Dropbox, root.DropboxKey)],
                [(Side.Local, root.LocalKey), (Side.Dropbox, root.DropboxKey)],
                [(Side.Dropbox, root.DropboxKey), (Side.Local, root.LocalKey)]
            ];
            foreach (var crossLink in new[] { false, true })
            {
                if (crossLink)
                    store.Commit(Pair, StateChange.Empty with { Record =
                        [Saved("Outside/Branch", "Remote/Linked", true), Saved("Outside/Branch/Leaf", "Remote/Linked/Leaf", false)] });
                for (var order = 0; order < orders.Length; order++)
                {
                    var calls = 0;
                    var begins = 0;
                    raw.sqlite3_trace(connection.Handle, (strdelegate_trace)((_, sql) =>
                    {
                        if (sql.StartsWith("SELECT 1 FROM issue_member", StringComparison.Ordinal)) calls++;
                        if (sql.TrimStart().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)) begins++;
                    }), null);
                    try
                    {
                        Check.Require(!StateStore.ForgetTouchesUnreadable(connection, Pair, orders[order], default),
                            "clear mapped and cross-linked subtrees are allowed");
                    }
                    finally { raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null); }
                    var expected = new[] { 2, 1, 3 }[order] + (crossLink ? 1 : 0);
                    Check.Require(calls == expected && begins == 0,
                        $"forget guard size={count} order={order} crossLink={crossLink}: calls={calls}/{expected}, BEGIN={begins}");
                    Console.WriteLine($"forget guard size={count} order={order} crossLink={crossLink}: calls={calls}, BEGIN={begins}");
                    if (budget) continue;
                    foreach (var member in crossLink ? new[] { "Local/Child0/Blocked", "Outside" } : new[] { "Local/Child0/Blocked" })
                    {
                        UnreadableMember.Record(store, Pair, member);
                        Check.Require(StateStore.ForgetTouchesUnreadable(connection, Pair, orders[order], default),
                            "mapped descendants and cross-linked ancestors protect every root order");
                        UnreadableMember.Release(store, Pair, member);
                    }
                }
            }
            if (budget) return;
            foreach (var mapped in new[] { true, false })
            {
                var member = mapped ? "Local/Child0/Blocked" : "Local/Unmapped";
                var fired = false;
                raw.sqlite3_trace(connection.Handle, (strdelegate_trace)((_, sql) =>
                {
                    if (fired || !sql.StartsWith(BelowPrefix, StringComparison.Ordinal)) return;
                    fired = true;
                    UnreadableMember.Record(store, Pair, member);
                }), null);
                try
                {
                    Check.Require(StateStore.ForgetTouchesUnreadable(connection, Pair, orders[0], default) == mapped && fired,
                        "descendant snapshot sees a later commit, but an unmapped cover hit alone does not refuse");
                }
                finally { raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null); }
                UnreadableMember.Release(store, Pair, member);
            }
            Check.Require(StateStore.ForgetTouchesUnreadable(connection, Pair, Roots(), default),
                "a later disjoint root sees a commit after the earlier root's reader ends");
            IEnumerable<(Side Side, string Key)> Roots()
            {
                yield return (Side.Local, PathNames.Key("First"));
                UnreadableMember.Record(store, Pair, "Second/Blocked");
                yield return (Side.Local, PathNames.Key("Second"));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static SyncedEntry Saved(string local, string remote, bool folder) =>
        new(PathNames.Key(local), local, PathNames.DropboxKey(remote), remote, folder,
            "local-" + local, 0, DateTimeOffset.UnixEpoch.UtcTicks, "remote-" + remote, "");
}
