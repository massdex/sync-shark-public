namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private DateTimeOffset _nextPrune = DateTimeOffset.MinValue;
    internal int PrunePasses { get; private set; }

    public void Prune()
    {
        if (!StartPrune()) return;
        PruneHistory();
        PruneDirectories();
    }

    private bool StartPrune()
    {
        InStore(() => TryResultMaintenance());
        if (Volatile.Read(ref _resultMaintenanceHalt) is not null) return false;
        return InStore(() =>
        {
            var now = _clock.GetUtcNow();
            if (now < _nextPrune) return false;
            _nextPrune = now + V2Settings.StorePruneInterval;
            PrunePasses++;
            return true;
        });
    }

    private void PruneHistory()
    {
        var cutoff = NowMs() - (long)_settings.HistoryRetention.TotalMilliseconds;
        PrunePages("generation", "finished IS NOT NULL AND finished < $cutoff AND gen < $pin",
            transaction => [("$cutoff", cutoff), ("$pin", Pin(transaction))]);
        while (InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            var changed = _notices.PrunePage(transaction, cutoff, 512);
            transaction.Commit();
            return changed != 0;
        }, nameof(Prune))) { }
        InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            // Only fully resolved deferrals age out: proceeded, released, or the entity is gone on both sides (no live row
            // in any root and no baseline). Pending and Keep-held rows of a live entity are never pruned, on any root type.
            StoreSchema.Execute(_connection, transaction,
                "DELETE FROM defender_deferral WHERE object_id IN (SELECT q.object_id FROM defender_deferral q " +
                "WHERE q.updated<$cutoff AND (q.consent IN ($proceed,$released) OR (" +
                "NOT EXISTS (SELECT 1 FROM root r CROSS JOIN file_state f INDEXED BY fs_obj ON f.root_no=r.root_no AND f.object_id=q.object_id) AND " +
                "NOT EXISTS (SELECT 1 FROM pair p CROSS JOIN sync_point s INDEXED BY sp_a_obj ON s.pair_no=p.pair_no AND s.a_object_id=q.object_id) AND " +
                "NOT EXISTS (SELECT 1 FROM pair p CROSS JOIN sync_point s INDEXED BY sp_b_obj ON s.pair_no=p.pair_no AND s.b_object_id=q.object_id))) " +
                "ORDER BY q.updated LIMIT 512);",
                ("$cutoff", (_clock.GetUtcNow() - _settings.HistoryRetention).UtcTicks),
                ("$proceed", (long)DefenderConsent.Proceed), ("$released", (long)DefenderConsent.Released));
            transaction.Commit();
        });
        PrunePages("choice_merge", "NOT EXISTS (SELECT 1 FROM notice n WHERE n.notice_id=choice_merge.notice_id AND " + NoticeTable.ActionableSql("n.state") + ")", _ => []);
        InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            _runHistory.Prune(transaction, NowMs(), _settings.HistoryRetention, _settings.RunSummaryMaximumRows);
            transaction.Commit();
        });
    }

    private void PruneDirectories()
    {
        // Directory liveness calculation and removal are ONE atomic unit: releasing between
        // them could delete a directory newly referenced by a concurrent publication.
        InStore(() =>
        {
            using var transaction = _connection.BeginTransaction();
            StoreSchema.Execute(_connection, transaction, "DROP TABLE IF EXISTS used;");
            StoreSchema.Execute(_connection, transaction,
                "CREATE TEMP TABLE used AS SELECT DISTINCT dir_no FROM file_state " +
                "UNION SELECT a_dir FROM sync_point UNION SELECT b_dir FROM sync_point WHERE b_dir IS NOT NULL " +
                "UNION SELECT dir_no FROM tombstone;");
            StoreSchema.Execute(_connection, transaction, "DELETE FROM dir WHERE dir_no NOT IN (SELECT dir_no FROM used);");
            StoreSchema.Execute(_connection, transaction, "DROP TABLE used;");
            transaction.Commit();
            _dir.ClearCache();
        });
    }
}
