using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Massdex.SyncShark.V2;

/// <summary>In-memory endpoint implementing inventory reads and conditional writes.</summary>
internal sealed class FakeEndpoint : IInventorySource, IEndpointWriter, ISourceAbsenceProbe
{
    private sealed record Entry(ItemState State, byte[] Bytes);

    private readonly Side _side;
    private readonly string _rootId;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _items;
    private readonly List<Change> _log = new();
    private readonly Dictionary<string, byte[]> _recoveryBytes = new(StringComparer.Ordinal);
    private long _cursor;
    private long _version;
    private long _idCounter;

    public FakeEndpoint(Side side, string rootId, bool caseSensitive = false)
    {
        _side = side;
        _rootId = rootId;
        _items = new(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Fail this many upcoming writes without applying them.</summary>
    public int FailNextWrites { get; set; }

    /// <summary>Write attempts reaching the mutation guard, including put, delete, move, mkdir and preserve.</summary>
    public int WriteCalls { get; private set; }

    /// <summary>Like the real writers (decision 16): refuse to delete a folder that still has children.</summary>
    public bool RefuseNonEmptyFolderDelete { get; set; }

    /// <summary>Apply the next write, then throw UncertainEffectException.</summary>
    public bool NextWriteUncertain { get; set; }

    /// <summary>Optional delay applied before each write.</summary>
    public TimeSpan Latency { get; set; }

    /// <summary>How many whole-root scans this fake served; a probe for the forced full-rescan path.</summary>
    public int FullCalls { get; private set; }

    /// <summary>How many cursor (incremental) reads this fake served; a probe next to <see cref="FullCalls"/>.</summary>
    public int DeltaCalls { get; private set; }

    /// <summary>Report files without a content hash from inventory, as the real local scan does; HashAsync has them.</summary>
    public bool InventoryWithoutHashes { get; set; }

    /// <summary>Model LocalWriter's unhashed observations for explicit conflict content-proof checks.</summary>
    public bool ObserveWithoutHashes { get; set; }

    /// <summary>Use the real Dropbox hash when paired with the scripted Dropbox wire.</summary>
    public bool DropboxHashes { get; set; }

    /// <summary>Model local files: identity and hash are available, but no provider revision exists.</summary>
    public bool OmitVersionIds { get; set; }

    /// <summary>Paths HashAsync cannot hash (unreadable, or changed while hashed).</summary>
    public HashSet<string> Unhashable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuse a put whose source, or a preserve whose victim, carries no content hash, as the real local and Dropbox
    /// writers do.
    /// </summary>
    public bool RequireHashes { get; set; }

    /// <summary>Every path HashAsync was asked for, in order.</summary>
    public List<string> HashedPaths { get; } = new();

    /// <summary>Adds one item as if it had always been present, and logs an upsert.</summary>
    public ItemState Seed(string path, string content, ItemKind kind = ItemKind.File,
        string? versionId = null, DateTimeOffset? mtime = null) =>
        Seed(path, Encoding.UTF8.GetBytes(content), kind, versionId, mtime);

    /// <summary>Adds one item as if it had always been present, and logs an upsert.</summary>
    public ItemState Seed(string path, byte[] bytes, ItemKind kind = ItemKind.File,
        string? versionId = null, DateTimeOffset? mtime = null)
    {
        lock (_gate)
        {
            var state = Store(path, bytes, kind, null, versionId, mtime);
            LogUpsert(state);
            return state;
        }
    }

    public Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct) => ObserveAsync(path, ct);

    public Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate) return Task.FromResult<bool?>(_items.Values.Any(entry => entry.State.ObjectId == historical.ObjectId));
    }

    internal System.Collections.Concurrent.ConcurrentQueue<(string Path, string? ObjectId, bool Absent)>? AbsenceDiagnostics { get; set; }

    public Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var absent = !_items.ContainsKey(path) &&
                (historical is null || (!_items.ContainsKey(historical.Path) &&
                    !_items.Values.Any(entry => entry.State.ObjectId == historical.ObjectId)));
            AbsenceDiagnostics?.Enqueue((path, historical?.ObjectId, absent));
            return Task.FromResult<bool?>(absent);
        }
    }

    public async Task<ItemState> PutAsync(SyncAction a, Stream content, CancellationToken ct)
    {
        await DelayIfNeededAsync(ct);
        if (RequireHashes && a.Source is { Kind: ItemKind.File, Hash: null })
            throw new ArgumentException("A put requires a file source with a content hash.", nameof(a));
        var bytes = await ReadAllAsync(content, ct);
        lock (_gate)
        {
            ThrowIfInjectedFailure();
            if (a.ExpectedTarget is null) EnsureFree(a.Path, null, "put");
            else EnsureExpected(a.Path, a.ExpectedTarget, "put");
            var state = Store(a.Path, bytes, a.Source?.Kind ?? ItemKind.File, a.Source?.IdentityJson);
            LogUpsert(state);
            if (NextWriteUncertain)
            {
                NextWriteUncertain = false;
                throw new UncertainEffectException($"FakeEndpoint put '{a.Path}' may have applied.");
            }
            return state;
        }
    }

    public async Task<ItemState> MoveAsync(SyncAction a, CancellationToken ct)
    {
        await DelayIfNeededAsync(ct);
        lock (_gate)
        {
            ThrowIfInjectedFailure();
            var source = a.FromPath ?? a.Path;
            EnsureExpected(source, a.ExpectedTarget, "move");
            if (!_items.TryGetValue(source, out var entry))
                throw new InvalidOperationException($"FakeEndpoint move: '{source}' does not exist.");
            EnsureFree(a.Path, source, "move");
            _items.Remove(source);
            var moved = entry.State with { Path = a.Path };
            _items[a.Path] = entry with { State = moved };
            LogDelete(source, entry.State.ObjectId);
            LogUpsert(moved);
            // A folder move carries its subtree, as on a real file system (identities kept, paths rebased).
            if (entry.State.Kind == ItemKind.Folder)
                foreach (var (childPath, child) in _items.Where(item => PairPath.IsUnder(item.Key, source)).ToArray())
                {
                    _items.Remove(childPath);
                    var rebased = child.State with { Path = PairPath.Rebase(childPath, source, a.Path) };
                    _items[rebased.Path] = child with { State = rebased };
                    LogDelete(childPath, child.State.ObjectId);
                    LogUpsert(rebased);
                }
            if (NextWriteUncertain)
            {
                NextWriteUncertain = false;
                throw new UncertainEffectException($"FakeEndpoint move '{source}' to '{a.Path}' may have applied.");
            }
            return moved;
        }
    }

    public async Task DeleteAsync(SyncAction a, CancellationToken ct)
    {
        await DelayIfNeededAsync(ct);
        lock (_gate)
        {
            ThrowIfInjectedFailure();
            EnsureExpected(a.Path, a.ExpectedTarget, "delete");
            if (RefuseNonEmptyFolderDelete && _items.Keys.Any(path => path.StartsWith(a.Path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"FakeEndpoint: folder '{a.Path}' is not empty.");
            if (a.PreserveIgnoredChildren && a.ExpectedTarget?.Kind == ItemKind.Folder &&
                _items.Keys.Any(path => PairPath.IsUnder(path, a.Path)))
                throw new IOException("Reviewed folder is no longer empty.");
            if (!_items.Remove(a.Path, out var entry)) return;
            LogDelete(a.Path, entry.State.ObjectId);
            if (NextWriteUncertain)
            {
                NextWriteUncertain = false;
                throw new UncertainEffectException($"FakeEndpoint delete '{a.Path}' may have applied.");
            }
        }
    }

    public async Task<ItemState> MkDirAsync(string path, CancellationToken ct)
    {
        await DelayIfNeededAsync(ct);
        lock (_gate)
        {
            ThrowIfInjectedFailure();
            var state = Store(path, Array.Empty<byte>(), ItemKind.Folder, null);
            LogUpsert(state);
            if (NextWriteUncertain)
            {
                NextWriteUncertain = false;
                throw new UncertainEffectException($"FakeEndpoint mkdir '{path}' may have applied.");
            }
            return state;
        }
    }

    public async Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct)
    {
        await DelayIfNeededAsync(ct);
        lock (_gate)
        {
            ThrowIfInjectedFailure();
            if (RequireHashes && victim.Kind == ItemKind.File && victim.Hash is null)
                throw new ArgumentException("A preserved victim must carry its content hash.", nameof(victim));
            var bytes = _items.TryGetValue(victim.Path, out var entry) ? entry.Bytes : Array.Empty<byte>();
            if (!_recoveryBytes.ContainsKey(actionId)) _recoveryBytes[actionId] = (byte[])bytes.Clone();
            return new RecoveryRef(_side, "memory://recovery/" + actionId, victim.VersionId, victim.Hash ?? string.Empty, victim.Size);
        }
    }

    public Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(path, out var entry)
                ? ObserveWithoutHashes && entry.State.Kind == ItemKind.File ? entry.State with { Hash = null } : entry.State
                : null);
        }
    }

    public Task<Stream> OpenReadAsync(ItemState s, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_items.TryGetValue(s.Path, out var entry))
                throw new FileNotFoundException($"FakeEndpoint has no '{s.Path}'.", s.Path);
            if (!string.Equals(entry.State.VersionId, s.VersionId, StringComparison.Ordinal) ||
                !string.Equals(entry.State.Hash, s.Hash, StringComparison.OrdinalIgnoreCase))
                throw new ExpectedTargetMismatchException(
                    $"FakeEndpoint open-read '{s.Path}': expected VersionId='{s.VersionId}' Hash='{s.Hash}'.");
            return Task.FromResult<Stream>(new MemoryStream(entry.Bytes, writable: false));
        }
    }

    public async Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null)
    {
        SubtreeEntered?.TrySetResult();
        var snapshot = await FullAsync(0, p => PairPath.IsSelfOrUnder(p, path), new Progress<string>(), ct);
        foreach (var page in snapshot.Items.Where(i => PairPath.IsSelfOrUnder(i.Path, path)).Chunk(256))
        {
            var hashes = await HashAsync(page.Where(i => i.Kind == ItemKind.File && i.Hash is null && i.Flags == ItemFlags.None)
                .Select(i => i.Path).ToArray(), ct);
            await accept(page.Select(i => i.Kind == ItemKind.File && i.Hash is null
                ? i with { Hash = hashes.GetValueOrDefault(i.Path) } : i).Select(i => SubtreeTransform?.Invoke(i) ?? i).ToArray());
            if (SubtreeReadFailure is { } failure) throw failure; // Partial pages are never complete authority.
        }
        if (SubtreeReadFailure is { } emptyFailure) throw emptyFailure;
    }

    internal Exception? SubtreeReadFailure { get; set; }
    internal Func<ItemState, ItemState>? SubtreeTransform { get; set; }

    internal TaskCompletionSource? SubtreeEntered { get; set; }

    public Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            FullCalls++;
            var items = _items.Values.Select(entry => Inventoried(entry.State))
                .Where(state => include(state.Path))
                .OrderBy(state => state.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            p?.Report("full:" + items.Length);
            return Task.FromResult(new Snapshot(_rootId, _side, gen, CursorText(), items, Array.Empty<string>()));
        }
    }

    public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DeltaCalls++;
            if (!long.TryParse(cursor, NumberStyles.Integer, CultureInfo.InvariantCulture, out var from) || from < 0 || from > _cursor)
                return Task.FromResult(new Delta(_rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), true, Array.Empty<string>()));
            // Inventory deltas are normalized before storage's delete-then-upsert pass, as in
            // DropboxInventory.Collapse. Retain tombstones for moves/recreations, but never replay
            // an earlier upsert after a later deletion of that path or its enclosing folder.
            var pending = new List<Change>();
            foreach (var change in _log.Skip((int)from))
            {
                if (change.Kind == ChangeKind.Delete)
                {
                    pending.RemoveAll(item => item.Kind == ChangeKind.Upsert &&
                        PairPath.IsSelfOrUnder(item.Path, change.Path));
                    pending.Add(change);
                    continue;
                }
                if (change.State?.Kind == ItemKind.File)
                    pending.RemoveAll(item => item.Kind == ChangeKind.Upsert &&
                        PairPath.IsUnder(item.Path, change.Path));
                var existing = pending.FindIndex(item => item.Kind == ChangeKind.Upsert &&
                    _items.Comparer.Equals(item.Path, change.Path));
                if (existing >= 0) pending[existing] = change;
                else pending.Add(change);
            }
            var changes = pending.Where(change => include(change.Path))
                .Select(change => change.State is null ? change : change with { State = Inventoried(change.State) })
                .ToArray();
            return Task.FromResult(new Delta(_rootId, _side, gen, cursor, CursorText(), changes, false, Array.Empty<string>()));
        }
    }

    // What an inventory reports for one item: without its hash when InventoryWithoutHashes is set.
    private ItemState Inventoried(ItemState state) =>
        InventoryWithoutHashes && state.Kind == ItemKind.File ? state with { Hash = null } : state;

    public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            HashedPaths.AddRange(paths);
            foreach (var path in paths)
                if (!Unhashable.Contains(path) && _items.TryGetValue(path, out var entry) && entry.State.Hash is string hash)
                    result[path] = hash;
            return Task.FromResult<IReadOnlyDictionary<string, string>>(result);
        }
    }

    public bool ReportsDeletionHistory { get; set; } = true;

    /// <summary>The last logged delete of each requested path that is still absent, keyed by the logged spelling.</summary>
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var path in paths.Where(path => ReportsDeletionHistory && !_items.ContainsKey(path)))
                if (_log.LastOrDefault(change => change.Kind == ChangeKind.Delete && PairPath.Same(change.Path, path))
                    is { DeletedUtc: { } deletedUtc } deleted)
                    result[deleted.Path] = deletedUtc;
            return Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(result);
        }
    }

    private async Task DelayIfNeededAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Latency > TimeSpan.Zero) await Task.Delay(Latency, ct);
    }

    private void ThrowIfInjectedFailure()
    {
        WriteCalls++;
        if (FailNextWrites <= 0) return;
        FailNextWrites--;
        throw new IOException("FakeEndpoint: injected write failure.");
    }

    private void EnsureExpected(string path, ItemState? expected, string operation)
    {
        if (expected is null) return;
        var current = _items.TryGetValue(path, out var entry) ? entry.State : null;
        // Like the real writers: identity/version always, content hash only when the plan carries one.
        if (current is null ||
            !string.Equals(current.VersionId, expected.VersionId, StringComparison.Ordinal) ||
            (expected.Hash is not null && !string.Equals(current.Hash, expected.Hash, StringComparison.Ordinal)))
            throw new ExpectedTargetMismatchException(
                $"FakeEndpoint {operation} on '{path}': expected VersionId='{expected.VersionId}' Hash='{expected.Hash}'.");
    }

    // A create or move destination must be free (writer contract); a case-only rename of the moved item is allowed.
    private void EnsureFree(string path, string? movedFrom, string operation)
    {
        if (movedFrom is not null && PairPath.Same(path, movedFrom)) return;
        if (_items.ContainsKey(path))
            throw new ExpectedTargetMismatchException($"FakeEndpoint {operation} to '{path}': the destination is occupied.");
    }

    private ItemState Store(string path, byte[] bytes, ItemKind kind, string? identityJson,
        string? versionId = null, DateTimeOffset? mtime = null)
    {
        var hash = DropboxHashes ? DropboxContentHash.Compute(bytes)
            : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var objectId = _items.TryGetValue(path, out var existing)
            ? existing.State.ObjectId
            : "id:" + (++_idCounter).ToString(CultureInfo.InvariantCulture);
        var state = new ItemState(path, kind, objectId, OmitVersionIds ? null : versionId ?? "v" + (++_version), hash, bytes.Length,
            mtime ?? DateTimeOffset.UnixEpoch, ItemFlags.None, identityJson);
        _items[path] = new Entry(state, bytes);
        return state;
    }

    private static async Task<byte[]> ReadAllAsync(Stream content, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private void LogUpsert(ItemState state)
    {
        _cursor++;
        _log.Add(new Change(ChangeKind.Upsert, state.ObjectId, state.Path, state, null));
    }

    private void LogDelete(string path, string objectId)
    {
        _cursor++;
        _log.Add(new Change(ChangeKind.Delete, objectId, path, null, DateTimeOffset.UtcNow));
    }

    private string CursorText() => _cursor.ToString(CultureInfo.InvariantCulture);
}
