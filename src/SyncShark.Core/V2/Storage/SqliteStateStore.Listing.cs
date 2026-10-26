using System.Text.Json;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    bool IStateStore.SupportsListingCheckpoint => true;
    bool IStateStore.CachedPairPaused(string pairId) => CachedPairPaused(pairId);
    void IStateStore.BeginListing(RootDef root, string mode) => BeginListing(root, mode);
    string? IStateStore.ListingPage(string root, string request) => ListingPage(root, request);
    void IStateStore.SaveListingPage(string root, string request, string cursor, string payload) =>
        SaveListingPage(root, request, cursor, payload);
    void IStateStore.ForgetListing(string root) => ForgetListing(root);
    bool IStateStore.HasFullListing(RootDef root) => HasFullListing(root);

    // Partial provider pages are deliberately separate from observation_checkpoint and live inventory.
    // One page contains both its rows and continuation cursor: they can never commit independently.
    internal void BeginListing(RootDef root, string mode) => InStore(() =>
    {
        var binding = JsonSerializer.Serialize(root);
        using var tx = _connection.BeginTransaction();
        var previous = StoreSchema.Scalar(_connection, tx,
            "SELECT binding||char(10)||mode FROM listing_checkpoint WHERE root_id=$r;", ("$r", root.RootId)) as string;
        if (previous != binding + "\n" + mode)
        {
            StoreSchema.Execute(_connection, tx, "DELETE FROM listing_page WHERE root_id=$r;", ("$r", root.RootId));
            StoreSchema.Execute(_connection, tx,
                "INSERT INTO listing_checkpoint VALUES($r,$b,$m) ON CONFLICT(root_id) DO UPDATE SET binding=$b,mode=$m;",
                ("$r", root.RootId), ("$b", binding), ("$m", mode));
        }
        tx.Commit();
    });

    internal bool HasFullListing(RootDef root) => InStore(() =>
        (long)StoreSchema.Scalar(_connection, null,
            "SELECT EXISTS(SELECT 1 FROM listing_checkpoint WHERE root_id=$r AND binding=$b AND mode='full');",
            ("$r", root.RootId), ("$b", JsonSerializer.Serialize(root)))! != 0);

    internal string? ListingPage(string root, string request) => InStore(() =>
        StoreSchema.Scalar(_connection, null,
            "SELECT payload FROM listing_page WHERE root_id=$r AND request_key=$q;", ("$r", root), ("$q", request)) as string);

    internal void SaveListingPage(string root, string request, string cursor, string payload) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx,
            "INSERT INTO listing_page VALUES($r,$q,$c,$p) ON CONFLICT(root_id,request_key) DO UPDATE SET cursor=$c,payload=$p;",
            ("$r", root), ("$q", request), ("$c", cursor), ("$p", payload));
        tx.Commit();
    });

    internal void ForgetListing(string root) => InStore(() =>
    {
        using var tx = _connection.BeginTransaction();
        StoreSchema.Execute(_connection, tx, "DELETE FROM listing_page WHERE root_id=$r;", ("$r", root));
        StoreSchema.Execute(_connection, tx, "DELETE FROM listing_checkpoint WHERE root_id=$r;", ("$r", root));
        tx.Commit();
    });

    /// <summary>Durable explicit per-pair pause, independent of Enabled and global/account preferences.</summary>
    public bool PairPaused(string pairId) => InStore(() =>
        (long)StoreSchema.Scalar(_connection, null, "SELECT EXISTS(SELECT 1 FROM pair_pause WHERE pair_id=$p);", ("$p", pairId))! != 0);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PauseCache> _pauseCache = new(StringComparer.Ordinal);
    private sealed class PauseCache
    {
        internal bool Loaded, Paused;
        internal long ReadAt;
    }

    // Local setters update immediately; another process is sampled at most once per two seconds.
    // The cache lock is outside InStore, both readers and setters take it before the store lock.
    internal bool CachedPairPaused(string pairId)
    {
        var cache = _pauseCache.GetOrAdd(pairId, _ => new PauseCache());
        lock (cache)
        {
            var now = _clock.GetTimestamp();
            if (!cache.Loaded || _clock.GetElapsedTime(cache.ReadAt, now) >= TimeSpan.FromSeconds(2))
            {
                cache.Paused = PairPaused(pairId);
                cache.ReadAt = now;
                cache.Loaded = true;
            }
            return cache.Paused;
        }
    }

    /// <summary>Requests a checkpoint-safe pause, or allows the next run to resume.</summary>
    public void SetPairPaused(string pairId, bool paused)
    {
        var cache = _pauseCache.GetOrAdd(pairId, _ => new PauseCache());
        lock (cache)
        {
            InStore(() => StoreSchema.Execute(_connection, null, paused
                ? "INSERT OR IGNORE INTO pair_pause VALUES($p);"
                : "DELETE FROM pair_pause WHERE pair_id=$p;", ("$p", pairId)));
            cache.Paused = paused;
            cache.ReadAt = _clock.GetTimestamp();
            cache.Loaded = true;
        }
        IndexingWorkSignal.Publish(this);
    }
}
