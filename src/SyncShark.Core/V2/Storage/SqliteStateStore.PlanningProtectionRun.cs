namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    private long _protectionVersion;
    private long _protectionChanges = -1;
    private long _protectionExternalVersion = -1;

    // Conservative invalidation belongs to Storage, not an incomplete list of Run write sites.
    // total_changes includes every writer-connection table mutation (including WITHOUT ROWID and
    // trigger writes); data_version also catches commits through another connection. TEMP-only
    // snapshot materialization uses its own connection and cannot invalidate this counter.
    private long PlanningProtectionVersion() => InStore(() =>
    {
        if (SQLitePCL.raw.sqlite3_get_autocommit(_connection.Handle) == 0) return checked(++_protectionVersion);
        var changes = Convert.ToInt64(StoreSchema.Scalar(_connection, null, "SELECT total_changes();"));
        var external = Convert.ToInt64(StoreSchema.Scalar(_connection, null, "PRAGMA data_version;"));
        if (changes != _protectionChanges || external != _protectionExternalVersion)
        {
            _protectionVersion = checked(_protectionVersion + 1);
            _protectionChanges = changes;
            _protectionExternalVersion = external;
        }
        return _protectionVersion;
    });

    internal PlanningProtectionRun OpenPlanningProtectionRun(PairDef pair) => new(this, pair);

    /// <summary>One planning run's disk-backed protection snapshot, refreshed after a store write.</summary>
    internal sealed class PlanningProtectionRun(SqliteStateStore store, PairDef pair) : IDisposable
    {
        private long _version = -1;
        private PlanningProtection? _snapshot;
        private ProtectionStream? _stream;
        internal int Builds { get; private set; }

        internal PlanningProtection Snapshot()
        {
            var version = store.PlanningProtectionVersion();
            if (_snapshot is not null && version == _version) return _snapshot;
            ReleaseSnapshot();
            // Capture the version BEFORE materialization: an intervening write forces the next
            // scope to rebuild, never labels a pre-write snapshot with a post-write version.
            _snapshot = store.OpenPlanningProtection(pair);
            _version = version;
            Builds++;
            return _snapshot;
        }

        internal ProtectionStream Stream(PlanningProtection snapshot)
        {
            if (!ReferenceEquals(snapshot, _snapshot)) throw new InvalidOperationException("Protection snapshot changed within a scope.");
            return _stream ??= snapshot.Stream();
        }

        public void Dispose()
        {
            RunLog.Trace($"Planning protection snapshots: builds={Builds}");
            ReleaseSnapshot();
        }

        private void ReleaseSnapshot()
        {
            var stream = _stream;
            var snapshot = _snapshot;
            _stream = null;
            _snapshot = null;
            DisposePreserving(null, stream, snapshot);
        }
    }
}
