using Microsoft.Data.Sqlite;
using Projection = (Massdex.SyncShark.Engine.Notice Notice, Massdex.SyncShark.Engine.NoticeItem? Member, bool Covered, bool Touched);

namespace Massdex.SyncShark.Engine.Storage;
/// <summary>Stores each Repair listing and compares its observed endpoints with saved bindings.</summary>
public sealed class ScanTable
{
    /// <summary>The deletion history and completed cursors belonging to this store, across its read and write connections.</summary>
    public DeletedNames History { get; } = new();

    private readonly Dictionary<(string Pair, Guid Scan), (SortedSet<string> Exact, PrefixSet Trees)[]> active = new();
    /// <summary>Begin tracking commits before taking this acquisition's listing boundary.</summary>
    public void Begin(string pairId, Guid scanId)
    {
        lock (active)
            if (!active.ContainsKey((pairId, scanId)))
                active.Add((pairId, scanId), [(new(StringComparer.Ordinal), new()), (new(StringComparer.Ordinal), new())]);
    }

    /// <summary>Whether a committed notice replacement needs its original footprints for an active scan.</summary>
    public bool HasActive(string pairId)
    {
        lock (active)
            return active.Keys.Any(key => key.Pair == pairId);
    }

    /// <summary>Mark an observed change during acquisition. Key is an already-canonical side key, so this must not call PathNames.KeyFor again.</summary>
    public void Touch(string pairId, Side side, string key, bool subtree)
    {
        lock (active)
            foreach (var(owner, marks)in active)
            {
                if (owner.Pair != pairId)
                    continue;
                marks[(int)side].Exact.Add(key);
                if (subtree)
                    marks[(int)side].Trees.Add(key);
            }
    }

    /// <summary>Test exact and covering commits, plus descendant commits when a folder is being settled.</summary>
    public bool Touched(string pairId, Guid scanId, Side side, string path, bool descendants)
    {
        var key = PathNames.KeyFor(side, path);
        lock (active)
        {
            if (!active.TryGetValue((pairId, scanId), out var marks))
                return true;
            var(exact, trees) = marks[(int)side];
            return exact.Contains(key) || trees.ResponsibilityOverlaps(key, false) || descendants && (key.Length == 0 ? exact.Count != 0 : PathNames.HasDescendantKey(exact, key));
        }
    }

    internal const string Pair = "(SELECT id FROM pair WHERE pair_id=$p)";
    private const string Slice = "pair_id=" + Pair + " AND scan_id=$s";
    /// <summary>Append ordered events in bounded transactions; capture the history cursor only after successful enumeration.</summary>
    public Task AddScanAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,CancellationToken ct) => IngestAsync(db,pairId,scanId,side,rows,terminalHistoryCursor,batchRows,null,ct);
    /// <summary>Serialize each scratch write with the store, without holding its gate while waiting for listing events.</summary>
    public Task AddScanAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,SemaphoreSlim writerGate,CancellationToken ct) => IngestAsync(db,pairId,scanId,side,rows,terminalHistoryCursor,batchRows,writerGate,ct);
    private async Task IngestAsync(SqliteConnection db,string pairId,Guid scanId,Side side,IAsyncEnumerable<Listed> rows,Func<string>? terminalHistoryCursor,int batchRows,SemaphoreSlim? gate,CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchRows);
        if(terminalHistoryCursor!=null && side!=Side.Dropbox)throw new ArgumentException(nameof(side));
        Begin(pairId,scanId);string? origin=null;
        if(terminalHistoryCursor!=null)await WriteAsync(gate, ct, ()=>{
            History.Forget(pairId,scanId);origin=DeletedNames.State(db,pairId).Origin;
            using var reset=StorageCommand.Create(db,null,"DELETE FROM scan_state.deleted_name WHERE scan_id=$n","$n",DeletedNames.Token(pairId,scanId));reset.ExecuteNonQuery();
        });
        var token = DeletedNames.Token(pairId, scanId);
        var batch = new List<Listed>();
        await foreach (var row in rows.WithCancellation(ct))
        {
            batch.Add(row);
            if (batch.Count == batchRows)
                await WriteAsync(gate, ct, () => Flush(db, pairId, scanId, side, token, batch, terminalHistoryCursor != null, ct));
        }

        if(batch.Count>0)await WriteAsync(gate, ct, () => Flush(db, pairId, scanId, side, token, batch, terminalHistoryCursor != null, ct));
        if (terminalHistoryCursor != null)
            await WriteAsync(gate, ct, ()=>History.Completed(pairId, scanId, terminalHistoryCursor(), origin!));
    }

    private static async Task WriteAsync(SemaphoreSlim? gate, CancellationToken ct, Action write)
    {
        if (gate != null) await gate.WaitAsync(ct);
        try { write(); } finally { gate?.Release(); }
    }

    private static void Flush(SqliteConnection db, string pairId, Guid scanId, Side side, byte[] token, List<Listed> batch, bool history, CancellationToken ct)
    {
        using var tx = db.BeginTransaction();
        WriteRows(db, tx, pairId, scanId, side, token, batch, history, ct);
        tx.Commit();
        batch.Clear();
    }

    private static void WriteRows(SqliteConnection db, SqliteTransaction tx, string pairId, Guid scanId, Side side, byte[] token, List<Listed> batch, bool history, CancellationToken ct)
    {
        const string deletePrefix="DELETE FROM scan_state.scan INDEXED BY scan_own WHERE "+Slice+" AND side=$side AND ";
        using var exact=StorageCommand.Create(db,tx,deletePrefix+"pathkey(path)=$k",("$p",pairId),("$s",scanId.ToByteArray()),("$side",(int)side),("$k",""));
        using var clear=StorageCommand.Create(db,tx,deletePrefix+"pathkey(path)>=$lo AND pathkey(path)<$hi",("$p",pairId),("$s",scanId.ToByteArray()),("$side",(int)side),("$lo",""),("$hi",""));
        using var put = StorageCommand.Create(db, tx, "INSERT INTO scan_state.scan(pair_id,scan_id,side,key_delta,path,is_folder,size,mtime,id,rev,hash,skip,shared) VALUES(" + Pair + ",$s,$side,$delta,$path,$folder,$size,$time,$id,$rev,$hash,$skip,$shared) ON CONFLICT DO UPDATE SET key_delta=excluded.key_delta,path=excluded.path,is_folder=excluded.is_folder,size=excluded.size,mtime=excluded.mtime,id=excluded.id,rev=excluded.rev,hash=excluded.hash,skip=excluded.skip,shared=excluded.shared", ("$p", pairId), ("$s", scanId.ToByteArray()), ("$side", (int)side));
        foreach (var name in new[]{"$delta","$path","$folder","$size","$time","$id","$rev","$hash","$skip","$shared"})
            put.Parameters.AddWithValue(name, DBNull.Value);
        foreach (var listed in batch)
        {
            ct.ThrowIfCancellationRequested();
            var key = PathNames.Key(listed.Path);
            var entry = listed.Entry;
            if (entry?.IsFolder != true)
            {
                var(lo, hi) = PathNames.DescendantBounds(key);
                if(entry==null){exact.Parameters["$k"].Value=key;exact.ExecuteNonQuery();}
                clear.Parameters["$lo"].Value = lo;
                clear.Parameters["$hi"].Value = hi;
                clear.ExecuteNonQuery();
            }

            if (history) WriteDeletedName(db, tx, token, key, entry == null);
            if (entry == null)
                continue;
            PutEntry(put, side, listed.Path, key, entry);
        }

    }

    private static void WriteDeletedName(SqliteConnection db, SqliteTransaction tx, byte[] token, string key, bool deleted)
    {
        using var history = StorageCommand.Create(db, tx, deleted ? "INSERT OR IGNORE INTO scan_state.deleted_name VALUES($s,$k)" : "DELETE FROM scan_state.deleted_name WHERE scan_id=$s AND path_key=$k", ("$s", token), ("$k", key));
        history.ExecuteNonQuery();
    }

    private static void PutEntry(SqliteCommand put, Side side, string path, string key, Entry entry)
    {
        var join = PathNames.KeyFor(side, path, key);
        var args = put.Parameters;
        args["$delta"].Value = join == key ? DBNull.Value : join;
        args["$path"].Value = path;
        args["$folder"].Value = entry.IsFolder;
        args["$size"].Value = entry.Size;
        args["$time"].Value = entry.Modified.UtcTicks;
        args["$id"].Value = Encoding.IdFor(side,entry.Id)!;
        args["$rev"].Value = entry.Revision;
        args["$hash"].Value = Encoding.Hash(entry.ContentHash)!;
        args["$skip"].Value = (object? )entry.Skip ?? DBNull.Value;
        args["$shared"].Value = entry.SharedRoot;
        put.ExecuteNonQuery();
        put.ExecuteNonQuery();
    }

    /// <summary>Clear only this pair's acquisition rows, scratch names and pending terminal cursor.</summary>
    public async Task ClearScanAsync(SqliteConnection db, string pairId, Guid scanId, CancellationToken ct)
    {
        using var tx = db.BeginTransaction();
        using var cmd = StorageCommand.Create(db, tx, "DELETE FROM scan_state.scan WHERE " + Slice + "; DELETE FROM scan_state.deleted_name WHERE scan_id IN ($n,$r)", ("$p", pairId), ("$s", scanId.ToByteArray()), ("$n", DeletedNames.Token(pairId, scanId)), ("$r", DeletedNames.RestoredToken(pairId,scanId)));
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        History.Cleared(pairId, scanId);
        lock (active)
            active.Remove((pairId, scanId));
    }

    /// <summary>Read pages of complete bindings, including notice-only rows and independently retained put-back notices.</summary>
    public IAsyncEnumerable<(Row Row, IReadOnlyList<Projection> Notices)> CompareAsync(SqliteConnection db, string pairId, Guid scanId, Scope? scope, Func<Side, string, (bool Covered, bool Touched)> evidence, int pageSize, CancellationToken ct) =>
        new ScanCompare(this).CompareAsync(db, pairId, scanId, scope, evidence, pageSize, ct);

    /// <summary>Match unchanged physical names first; stream unmatched identities ordered by the binding's shallowest move.</summary>
    public IAsyncEnumerable<RenameCandidate> RenamesAsync(SqliteConnection db, string pairId, Guid scanId, Scope? scope, int pageSize, CancellationToken ct) =>
        ScanCompare.RenamesAsync(db, pairId, scanId, scope, pageSize, ct);
}
