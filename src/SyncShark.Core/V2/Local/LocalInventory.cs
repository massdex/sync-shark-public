using System.ComponentModel;

namespace Massdex.SyncShark.V2;

/// <summary>Reads one local NTFS root's complete or incremental inventory and its content hashes.</summary>
public sealed class LocalInventory : IInventorySource
{
    private readonly RootDef _root;
    private readonly Side _side;
    private readonly IStateStore _known;
    private readonly IReadOnlySet<string> _productOwnedPaths;
    private readonly IReadOnlySet<string> _productOwnedRelative;
    private readonly V2Settings _settings;
    private readonly TimeProvider _clock;
    private readonly uint _volume;
    private readonly ulong _rootFileId;
    private readonly PilotDirectoryIdentity _rootIdentity;

    /// <summary>Binds one local root; the saved root object id must parse into its NTFS identity.</summary>
    public LocalInventory(RootDef root, Side side, IStateStore known,
        IReadOnlySet<string> productOwnedPaths, V2Settings settings, TimeProvider clock)
    {
        _root = root ?? throw new ArgumentNullException(nameof(root));
        _side = side;
        _known = known ?? throw new ArgumentNullException(nameof(known));
        _productOwnedPaths = productOwnedPaths ?? throw new ArgumentNullException(nameof(productOwnedPaths));
        // The relative form the shared exclusion test needs; the scanner keeps the absolute set for enumeration.
        _productOwnedRelative = LocalItems.ProductOwnedRelative(_root.RootPath, productOwnedPaths);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        // Refused before any work: zero workers would hash nothing, a zero read chunk would read nothing.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.LocalHashWorkers, nameof(settings.LocalHashWorkers));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(settings.LocalHashBufferBytes,
            nameof(settings.LocalHashBufferBytes));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        (_volume, _rootFileId) = LocalItems.ParseObjectId(root.RootObjectId);
        _rootIdentity = new PilotDirectoryIdentity(_volume, _rootFileId);
    }

    /// <inheritdoc/>
    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p, CancellationToken ct)
    {
        var rootId = _root.RootId;
        var cursor = NtfsUsnJournal.TryReadCheckpoint(_root.RootPath, out var checkpoint) ? checkpoint!.Cursor : string.Empty;
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return WholeIncomplete(gen, rootId);
        }
        using (lease)
        {
            PilotLocalScan scan;
            try
            {
                scan = await ScanAsync(lease, ct, include, refreshPaths: null, p).ConfigureAwait(false);
            }
            catch (Exception error) when (IsRootUnavailable(error))
            {
                return WholeIncomplete(gen, rootId);
            }
            return BuildSnapshot(gen, rootId, cursor, scan, include);
        }
    }

    /// <inheritdoc/>
    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct)
    {
        var rootId = _root.RootId;
        if (!NtfsUsnCheckpoint.TryParseCursor(cursor, out var journalId, out var next)) return Invalid(gen, rootId, cursor, "saved cursor cannot be parsed");
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return Invalid(gen, rootId, cursor, "root lease failed: " + error.Message);
        }
        using (lease)
        {
            return await DeltaAsync(lease, cursor, gen, rootId, journalId, next, include, ct).ConfigureAwait(false);
        }
    }

    private async Task<Delta> DeltaAsync(PilotDirectoryLease lease, string cursor, long gen, string rootId,
        ulong journalId, long next, Func<string, bool> include, CancellationToken ct)
    {
        if (!NtfsUsnJournal.TryReadCheckpoint(_root.RootPath, out var through) || through is null)
            return Invalid(gen, rootId, cursor, "journal checkpoint unavailable");
        var from = new NtfsUsnCheckpoint(journalId, through.FirstUsn, next, through.LowestValidUsn);
        if (through.RequiresFullReconciliation(cursor)) return Invalid(gen, rootId, cursor,
            journalId != through.JournalId ? "journal replaced" : next > through.NextUsn ? "cursor ahead of journal" : "journal gap: cursor precedes retained range");
        if (!NtfsUsnJournal.TryReadChanges(_root.RootPath, from, through, out var changes, out var readFailure, ct))
            return Invalid(gen, rootId, cursor, readFailure ?? "journal read failed");
        var names = new LocalJournalNames(_root, _known, changes, _settings.LocalDeltaMaxPaths);
        var fallbackReason = "resolved path limit exceeded";
        var context = new ResolveContext(_rootFileId, _volume,
            id => _known.ByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, id)),
            _settings.LocalDeltaMaxPaths, names.Find, names.Ambiguous, reason => fallbackReason = names.LastFailure ?? reason);
        var paths = await LocalDeltaResolver.ResolveAsync(changes, context, ct).ConfigureAwait(false);
        if (paths is null || paths.Count > _settings.LocalDeltaMaxPaths)
        {
            // Unlike a gap, this batch contains authoritative write evidence. Keep it through fallback.
            PreserveWrites();
            return Invalid(gen, rootId, cursor, fallbackReason);
        }
        var delta = paths.Count == 0
            ? new Delta(rootId, _side, gen, cursor, through.Cursor, Array.Empty<Change>(), false, [])
            : await RefreshAsync(lease, gen, cursor, through.Cursor, include, paths, ct).ConfigureAwait(false);
        // A moved-in directory may expose files with no individual journal record in this interval.
        // Validate those refreshed identities too, before upserts can mistake aliases for old move positions.
        AfterRefreshScan?.Invoke();
        foreach (var change in delta.Changes)
        {
            if (change.State is not { Kind: ItemKind.File } state) continue;
            ct.ThrowIfCancellationRequested();
            if (!names.Ambiguous(LocalItems.ParseObjectId(state.ObjectId).FileId, state, requirePresent: true)) continue;
            PreserveWrites();
            return Invalid(gen, rootId, cursor, names.LastFailure ?? "refreshed file has ambiguous hard-link identity");
        }
        var written = changes.Where(change => (change.Reason & 0x7) != 0)
            .Select(change => SyncLocalEndpointStore.ObjectId(_volume, change.FileId)).ToHashSet(StringComparer.Ordinal);
        return await RehashAsync(lease, delta, written, ct).ConfigureAwait(false);

        void PreserveWrites()
        {
            var writtenKnown = changes.Where(record => (record.Reason & 0x7) != 0).DistinctBy(record => record.FileId)
                .Select(record => _known.ByObject(rootId, SyncLocalEndpointStore.ObjectId(_volume, record.FileId)))
                .Where(item => item is { Kind: ItemKind.File }).Select(item => item!);
            _known.RequireRehashBatch(rootId, writtenKnown, "journal data write requires full reconciliation before hashing");
        }
    }

    private async Task<Delta> RehashAsync(PilotDirectoryLease lease, Delta delta, HashSet<string> written,
        CancellationToken ct)
    {
        // Metadata-only cloud-aware scanning emits hydrated files as ordinary items (CloudAvailability null).
        // Placeholder states from this path are online-only: never hydrate them just to verify content (decision 57).
        const ItemFlags blocked = ItemFlags.Locked | ItemFlags.Unreadable | ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder;
        var candidates = delta.Changes.Where(change => change.State is { Kind: ItemKind.File } state &&
            (state.Flags & blocked) == 0 && _known.ByObject(_root.RootId, state.ObjectId) is { } prior &&
            ((prior.Flags & ItemFlags.NeedsRehash) != 0 ||
             written.Contains(state.ObjectId) && prior.Size == state.Size && prior.Mtime == state.Mtime))
            .Select(change => change.State!).ToArray();
        if (candidates.Length == 0) return delta;
        // Persist before opening; even cancellation/crash must not discard authoritative journal evidence.
        _known.RequireRehashBatch(_root.RootId, candidates, "content verification pending after journal write");
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scan = await HashScanAsync(lease, candidates, ct).ConfigureAwait(false);
        Accept(candidates, scan, hashes);
        var completed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in candidates)
        {
            if (hashes.TryGetValue(item.Path, out var hash) && _known.CompleteRehash(_root.RootId, item, hash))
                completed[item.Path] = hash;
            else
            {
                var reason = scan.Decisions.FirstOrDefault(decision => decision.RelativePath == item.Path)?.Reason
                    ?? "content or stored identity changed during verification; retry on next sync";
                _known.RequireRehash(_root.RootId, item, reason);
                RunLog.Trace($"local rehash pending: {item.Path}: {reason}");
            }
        }
        var attempted = candidates.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return delta with { Changes = delta.Changes.Select(change => change.State is { } state && attempted.Contains(state.Path)
            ? change with { State = completed.TryGetValue(state.Path, out var hash)
                ? state with { Hash = hash, Flags = state.Flags & ~ItemFlags.NeedsRehash }
                : state with { Hash = null, Flags = state.Flags | ItemFlags.NeedsRehash } } : change).ToArray() };
    }

    /// <inheritdoc/>
    public async Task VerifyPendingAsync(Func<string, bool> include, Action<string> touched, CancellationToken ct)
    {
        var through = _known.RehashWatermark(_root.RootId);
        if (through == 0) return;
        PilotDirectoryLease lease;
        try { lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity); }
        catch (Exception error) when (IsRootUnavailable(error)) { return; }
        using (lease)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var page = _known.RehashCandidates(_root.RootId, 128, through);
                if (page.Count == 0) break;
                // Rotation occurs before filtering: excluded/blocked entries keep their reason and cannot spin.
                // Bit128 is the parallel quarantine lane's reserved item hold; it never auto-retries.
                var candidates = page.Where(item => item.Kind == ItemKind.File && include(item.Path) &&
                    !LocalItems.IsExcluded(item.Path, _productOwnedRelative) &&
                    (item.Flags & (ItemFlags.Ignored | ItemFlags.Link | ItemFlags.Placeholder | (ItemFlags)128)) == 0).ToArray();
                if (candidates.Length == 0) continue;
                PilotLocalScan scan;
                try { scan = await HashScanAsync(lease, candidates, ct).ConfigureAwait(false); }
                catch (Exception error) when (IsRootUnavailable(error)) { return; }
                var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Accept(candidates, scan, hashes);
                foreach (var item in candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    if (hashes.TryGetValue(item.Path, out var hash) && _known.CompleteRehash(_root.RootId, item, hash))
                    {
                        // Completion unholds an entity, not just its selected name. Report every alias in pages.
                        for (var offset = 0; ; offset += 128)
                        {
                            ct.ThrowIfCancellationRequested();
                            var aliases = _known.ObjectPaths(_root.RootId, item.ObjectId, offset, 128);
                            foreach (var path in aliases) touched(path);
                            if (aliases.Count < 128) break;
                        }
                    }
                    else
                    {
                        var reason = scan.Decisions.FirstOrDefault(decision => decision.RelativePath == item.Path)?.Reason
                            ?? "content or stored identity changed during verification; retry on next sync";
                        _known.RequireRehash(_root.RootId, item, reason);
                    }
                }
            }
        }
    }

    /// <summary>Runs the refresh and delta construction for explicit paths; exposed for checks without a live journal.</summary>
    internal async Task<Delta> ChangesForPathsAsync(IReadOnlyList<string> paths, string cursor, long gen,
        Func<string, bool> include, CancellationToken ct)
    {
        using var lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        return await RefreshAsync(lease, gen, cursor, cursor, include, paths, ct).ConfigureAwait(false);
    }

    private async Task<Delta> RefreshAsync(PilotDirectoryLease lease, long gen, string cursor, string throughCursor,
        Func<string, bool> include, IReadOnlyList<string> paths, CancellationToken ct)
    {
        paths = paths.Where(path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative)).ToArray();
        // The ancestors stay held until the listing completes, so none can be swapped after it was validated.
        using var guard = LocalItems.GuardRefreshPaths(_root.RootPath, _root.RootId, _known, paths);
        // The deletion time is the scan time it is first seen missing, so it is sampled before the scan.
        var scanTime = _clock.GetUtcNow();
        BeforeRefreshScan?.Invoke();
        PilotLocalScan scan;
        try
        {
            scan = await ScanAsync(lease, ct, include, guard.Safe, null).ConfigureAwait(false);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return Invalid(gen, _root.RootId, cursor, "root unavailable during guarded refresh");
        }
        return BuildDelta(gen, _root.RootId, cursor, throughCursor, scan, paths, include, scanTime, guard.Incomplete);
    }

    /// <summary>Check seam: runs after the refresh ancestors are held and the scan time is sampled, before listing.
    /// Its only reader is LocalDeltaChecks (ancestor-swap refusal and deletion-time sampling order); null in production.</summary>
    internal Action? BeforeRefreshScan { get; set; }
    internal Action? AfterRefreshScan { get; set; }

    /// <summary>
    /// Always empty: a local deletion time is the scan time it was first seen missing (decision 4), which needs an
    /// earlier scan that a first-sync preview never persists; an unknown time keeps and restores the survivor.
    /// </summary>
    // Backlog: first-sync local deletion evidence (pending owner decision).
    public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        var rows = _known.Live(_root.RootId, paths);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rows.Count == 0) return result;
        PilotDirectoryLease lease;
        try
        {
            lease = PilotDirectoryLease.Open(_root.RootPath, _rootIdentity);
        }
        catch (Exception error) when (IsRootUnavailable(error))
        {
            return result;
        }
        using (lease)
        {
            try
            {
                var refused = await HashSharesAsync(lease, rows, result, ct).ConfigureAwait(false);
                // Two names of one physical file in different shares can collide on the exclusive open. Like the
                // serial scan's single sharing retry, a row refused in the parallel pass is retried once, alone.
                if (refused.Count > 0)
                    Accept(refused, await HashScanAsync(lease, refused, ct).ConfigureAwait(false), result);
            }
            catch (Exception error) when (IsRootUnavailable(error))
            {
                return result;
            }
        }
        return result;
    }

    /// <summary>
    /// Hashes the rows with a bounded number of workers, each a serial verified scan over an interleaved share,
    /// accepts every unchanged result, and returns the rows the scans refused (blocked) without a hash.
    /// </summary>
    private async Task<IReadOnlyList<ItemState>> HashSharesAsync(PilotDirectoryLease lease, IReadOnlyList<ItemState> rows,
        Dictionary<string, string> result, CancellationToken ct)
    {
        var workers = Math.Clamp(_settings.LocalHashWorkers, 1, rows.Count);
        var shares = Enumerable.Range(0, workers)
            .Select(worker => rows.Where((_, index) => index % workers == worker).ToArray()).ToArray();
        var scans = await Task.WhenAll(shares.Select(share => Task.Run(() => HashScanAsync(lease, share, ct), ct)))
            .ConfigureAwait(false);
        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var worker = 0; worker < workers; worker++)
        {
            Accept(shares[worker], scans[worker], result);
            foreach (var decision in scans[worker].Decisions) blocked.Add(decision.RelativePath);
        }
        if (workers == 1) return Array.Empty<ItemState>(); // a lone serial scan already made its own retry
        return rows.Where(row => !result.ContainsKey(row.Path) && blocked.Contains(row.Path)).ToArray();
    }

    private Task<PilotLocalScan> HashScanAsync(PilotDirectoryLease lease, IReadOnlyList<ItemState> rows,
        CancellationToken ct) =>
        new PilotLocalScanner { HashBufferBytes = _settings.LocalHashBufferBytes, CloudFilesAware = true,
            ReplaceOnlineOnlyStubs = _settings.ReplaceOnlineOnlyStubs }
            .ScanAsync(lease, _productOwnedPaths, ct, refreshPaths: rows.Select(row => row.Path).ToArray());

    /// <summary>Accepts a scanned hash only for a file whose identity, size and mtime still match its row.</summary>
    private static void Accept(IReadOnlyList<ItemState> rows, PilotLocalScan scan, Dictionary<string, string> result)
    {
        var byPath = new Dictionary<string, PilotLocalEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in scan.Entries.Values) byPath[entry.RelativePath] = entry;
        foreach (var row in rows)
        {
            if (!byPath.TryGetValue(row.Path, out var entry) || entry.Kind != PilotEntryKind.File ||
                entry.ContentHash is null)
                continue;
            var state = LocalItems.FromScan(entry);
            if (state.ObjectId == row.ObjectId && state.Size == row.Size && state.Mtime.UtcTicks == row.Mtime.UtcTicks)
                result[row.Path] = entry.ContentHash;
        }
    }

    private Task<PilotLocalScan> ScanAsync(PilotDirectoryLease lease, CancellationToken ct, Func<string, bool> include,
        IReadOnlyList<string>? refreshPaths, IProgress<string>? p) =>
        new PilotLocalScanner { MetadataOnly = true, CloudFilesAware = true,
            ReplaceOnlineOnlyStubs = _settings.ReplaceOnlineOnlyStubs }.ScanAsync(lease, _productOwnedPaths, ct,
            includePath: path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative),
            traverseDirectory: path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative),
            onInventory: p is null
                ? null
                : (path, found) => { p.Report($"{found:N0} items: {path}"); return Task.CompletedTask; },
            refreshPaths: refreshPaths);

    private Snapshot BuildSnapshot(long gen, string rootId, string cursor, PilotLocalScan scan, Func<string, bool> include)
    {
        var items = new List<ItemState>();
        foreach (var entry in scan.Entries.Values)
            if (include(entry.RelativePath) && !LocalItems.IsExcluded(entry.RelativePath, _productOwnedRelative))
                items.Add(LocalItems.FromScan(entry));
        items.Sort((left, right) => string.CompareOrdinal(left.Path, right.Path));
        return new Snapshot(rootId, _side, gen, cursor, items, DecisionPaths(scan, include)) { IncludesPath = include };
    }

    private Delta BuildDelta(long gen, string rootId, string cursor, string throughCursor, PilotLocalScan scan,
        IReadOnlyList<string> paths, Func<string, bool> include, DateTimeOffset scanTime, IReadOnlyList<string> extraIncomplete)
    {
        var decisionPaths = extraIncomplete.Count == 0
            ? DecisionPaths(scan, include)
            : DecisionPaths(scan, include).Concat(extraIncomplete.Where(path => path.Length == 0 || include(path))).Distinct(StringComparer.Ordinal).ToArray();
        var changes = new List<Change>();
        foreach (var entry in scan.Entries.Values)
        {
            if (!include(entry.RelativePath) || LocalItems.IsExcluded(entry.RelativePath, _productOwnedRelative)) continue;
            var state = LocalItems.FromScan(entry);
            changes.Add(new Change(ChangeKind.Upsert, state.ObjectId, state.Path, state, null));
        }
        changes.AddRange(LocalDeletes.Build(_known, rootId, paths, scan,
            path => include(path) && !LocalItems.IsExcluded(path, _productOwnedRelative) &&
                !LocalItems.InsideAny(path, decisionPaths),
            scanTime));
        return new Delta(rootId, _side, gen, cursor, throughCursor, changes, false, decisionPaths) { IncludesPath = include };
    }

    private static IReadOnlyList<string> DecisionPaths(PilotLocalScan scan, Func<string, bool> include) =>
        scan.Decisions.Select(decision => decision.RelativePath).Where(path => path.Length == 0 || include(path)).Distinct(StringComparer.Ordinal).ToArray();

    private Snapshot WholeIncomplete(long gen, string rootId) =>
        new(rootId, _side, gen, string.Empty, Array.Empty<ItemState>(), new[] { string.Empty });

    internal string? LastFallbackReason { get; private set; }

    private Delta Invalid(long gen, string rootId, string cursor, string reason)
    {
        LastFallbackReason = reason;
        RunLog.Trace($"local full reconciliation ({rootId}): {reason}");
        return new(rootId, _side, gen, cursor, cursor, Array.Empty<Change>(), true, Array.Empty<string>());
    }

    private static bool IsRootUnavailable(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception;
}
