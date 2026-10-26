namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private const string RetiredWalkPrefix = "reclaim:";
    private readonly CancellationTokenSource _walkReclaimStop = new();
    private readonly Dictionary<string, WalkReclaim> _walkReclaims = new(StringComparer.Ordinal);
    private sealed class WalkReclaim(string epoch)
    {
        internal string Epoch = epoch;
        internal Task Work = Task.CompletedTask;
    }

    // Caller owns LocalWalkGate. The cursor is no longer a replay boundary once binding
    // is empty: it holds a unique retirement epoch, without a schema or payload rewrite.
    internal Task RetireLocalWalk(string root)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var epoch = RetiredWalkPrefix + Guid.NewGuid().ToString("N");
        var retired = InStore(() =>
        {
            using var tx = _connection.BeginTransaction();
            StoreSchema.Execute(_connection, tx,
                "UPDATE local_walk SET binding='',cursor=$e WHERE root_id=$r;", ("$e", epoch), ("$r", root));
            var changed = StoreSchema.Changes(_connection, tx) != 0;
            tx.Commit();
            return changed;
        });
        TraceWalkReclaim("foreground", watch.ElapsedMilliseconds, 0);
        if (!retired) return Task.CompletedTask;
        lock (_walkReclaims)
        {
            if (_walkReclaimStop.IsCancellationRequested) return Task.CompletedTask;
            if (_walkReclaims.TryGetValue(root, out var pending)) { pending.Epoch = epoch; return pending.Work; }
            var state = new WalkReclaim(epoch);
            _walkReclaims.Add(root, state);
            return state.Work = StartWalkReclaim(root, state);
        }
    }

    private Task StartWalkReclaim(string root, WalkReclaim state)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(() => ReclaimWalkAsync(root, state));
        using (ExecutionContext.SuppressFlow()) return Task.Run(() => ReclaimWalkAsync(root, state));
    }

    private async Task ReclaimWalkAsync(string root, WalkReclaim state)
    {
        using var admission = FairStoreGate.WithCancellation(_walkReclaimStop.Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        long committed = 0;
        try
        {
            while (true)
            {
                string epoch;
                lock (_walkReclaims) epoch = state.Epoch;
                var gate = LocalWalkGate(root);
                await gate.WaitAsync(_walkReclaimStop.Token).ConfigureAwait(false);
                bool more;
                try { more = ReclaimRetiredWalkPage(root, epoch); }
                finally { gate.Release(); }
                if (more) committed++;
                lock (_walkReclaims)
                {
                    if (more || state.Epoch != epoch) continue;
                    _walkReclaims.Remove(root);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (_walkReclaimStop.IsCancellationRequested) { }
        catch (StateDamagedException error) { RunOutput.Observe(error); throw; }
        catch (Exception error) { System.Diagnostics.Trace.WriteLine("walk-reclaim deferred: " + error.GetType().Name); }
        finally
        {
            lock (_walkReclaims)
                if (_walkReclaims.TryGetValue(root, out var current) && ReferenceEquals(current, state)) _walkReclaims.Remove(root);
            TraceWalkReclaim("background", watch.ElapsedMilliseconds, committed);
        }
    }

    // The epoch check and bounded delete share a transaction. An old queued worker can
    // never reclaim a newer walk, even when it obtains the root gate after Begin/Save.
    internal bool ReclaimRetiredWalkPage(string root, string epoch) => InStore(() =>
    {
        if (_disposed || _walkReclaimStop.IsCancellationRequested) return false;
        using var tx = _connection.BeginTransaction();
        if (StoreSchema.Scalar(_connection, tx,
            "SELECT 1 FROM local_walk WHERE root_id=$r AND binding='' AND cursor=$e;",
            ("$r", root), ("$e", epoch)) is null) return false;
        var removed = ReclaimPayloadSlice(root, tx);
        if (removed == 0)
        {
            StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk_page WHERE root_id=$r AND ordinal=-1;", ("$r", root));
            StoreSchema.Execute(_connection, tx, "DELETE FROM local_walk WHERE root_id=$r AND binding='' AND cursor=$e;",
                ("$r", root), ("$e", epoch));
        }
        tx.Commit();
        return removed != 0;
    });

    private long ReclaimPayloadSlice(string root, Microsoft.Data.Sqlite.SqliteTransaction tx)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bound = Math.Max(V2Settings.MinimumStoreWalkRows, _settings.StoreWalkReclaimRows);
        long removed = 0;
        // A payload deletion and commit are indivisible. Stop admitting more payloads
        // when the slice expires; never hold a transaction for another full row batch.
        do
        {
            StoreSchema.Execute(_connection, tx, LocalWalkCleanupPageSql,
                ("$r", root), ("$n", V2Settings.MinimumStoreWalkRows));
            var changed = StoreSchema.Changes(_connection, tx);
            if (changed == 0) break;
            removed += changed;
        } while (removed < bound && watch.Elapsed < _settings.StoreWalkReclaimSlice &&
            !_walkReclaimStop.IsCancellationRequested);
        return removed;
    }

    private static void TraceWalkReclaim(string lane, long elapsed, long pages) =>
        System.Diagnostics.Trace.WriteLine($"walk-reclaim {lane} elapsed-ms={elapsed} pages={pages}");
}
