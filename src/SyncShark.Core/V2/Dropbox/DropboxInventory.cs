using System.Net;

namespace Massdex.SyncShark.V2;

/// <summary>Reads one Dropbox root's complete or incremental inventory through the provider client.</summary>
public sealed class DropboxInventory : IInventorySource, IDeferredDeletionEvidence
{
    private static readonly IReadOnlyDictionary<string, SyncRemoteDeletionEvidence> NoEvidence =
        new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase);

    private readonly RootDef _root;
    private readonly Side _side;
    private readonly DropboxPilotClient _client;
    private readonly IStateStore _known;
    private readonly string _pairId;
    // Complete first-sync tombstones come from a full listing or its persisted cursor-bound classification.
    // The ancestor set also keeps tombstones for re-created folders; exact matches exclude live replacements.
    // Only tombstone-matched file candidates require revision-history evidence.
    private IReadOnlySet<string>? _deletedExactFromFull;
    private IReadOnlySet<string>? _deletedAncestorsFromFull;
    private string? _evidenceBasis;

    public DropboxInventory(RootDef root, Side side, DropboxPilotClient client, IStateStore known, string pairId)
    {
        _root = root;
        _side = side;
        _client = client;
        _known = known;
        _pairId = pairId;
    }

    public Task ReadSubtreeAsync(string path, Func<IReadOnlyList<ItemState>, Task> accept, CancellationToken ct,
        IProgress<string>? progress = null) =>
        _client.HydratePrefixAsync(path, rows =>
        {
            ProgressGuard.Report(progress, $"Review listing {path}: {rows.Count:N0} entries in page", ct);
            return accept(rows.Where(r => r.Entry is not null && !ArtifactPaths.IsArtifactPath(r.Path)).Select(r => DropboxItems.ToItem(r.Entry!)).ToArray());
        }, ct);

    internal async Task<IReadOnlyDictionary<string, string>> HydratePrefixesAsync(IReadOnlyList<string> prefixes,
        Func<IReadOnlyList<DropboxChange>, Task> accept, CancellationToken ct, Action<string>? listingBasis = null)
    {
        var requested = accept;
        accept = changes => requested(changes.Where(change => !ArtifactPaths.IsArtifactPath(change.Path)).ToArray());
        if (prefixes.Contains(string.Empty))
        {
            // Root scope is "", never "id:/". A required full re-list uses the durable
            // inventory page journal; it is not an uncheckpointed prefix hydration.
            var listing = await ListCheckpointedAsync(null, ct);
            listingBasis?.Invoke(listing.BasisCursor);
            foreach (var batch in listing.Entries.Select(entry => new DropboxChange(entry.RelativePath, entry)).Chunk(256))
                await accept(batch);
            return listing.ScopedFailures;
        }
        await Parallel.ForEachAsync(prefixes, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (prefix, token) => await _client.HydratePrefixAsync(prefix, accept, token));
        return new Dictionary<string, string>();
    }

    /// <summary>A complete listing; a moved or unreachable root yields a whole-root-incomplete snapshot, never a delete.</summary>
    public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
        CancellationToken ct)
    {
        string cursor;
        IReadOnlyList<PilotRemoteEntry> entries;
        IReadOnlyDictionary<string, string> scoped;
        _deletedExactFromFull = null;
        _deletedAncestorsFromFull = null;
        _evidenceBasis = null;
        _evidenceValidatedThisRun = false;
        try
        {
            var listing = await ListCheckpointedAsync(p, ct, include, _client.ListingTransientBudget);
            entries = listing.Entries;
            scoped = listing.ScopedFailures;
            _deletedExactFromFull = listing.DeletedPaths;
            _deletedAncestorsFromFull = listing.DeletedAncestors;
            cursor = _evidenceBasis = listing.BasisCursor;
            _known.ReplaceDeletionClassification(_root, cursor, listing.DeletedPaths, listing.DeletedAncestors);
        }
        catch (InvalidDataException error)
        {
            _known?.ForgetListing(_root.RootId);
            return WholeIncomplete(gen,
                "Dropbox returned inventory data that could not be verified safely: " + error.Message +
                " No item was inferred absent. Retry; if it persists, report the folder that cannot be listed.");
        }
        catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            _known?.ForgetListing(_root.RootId);
            return WholeIncomplete(gen,
                "Dropbox rejected the saved listing checkpoint after the folder changed. A fresh complete listing is required; nothing was inferred absent.");
        }
        catch (Exception exception) when (DropboxItems.MapAccessError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
        var items = entries.Where(entry => include(entry.RelativePath)).Select(DropboxItems.ToItem)
            .OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        // A known malformed or colliding entry is scoped debt with its real reason: every healthy
        // branch still syncs, and the held path is never inferred absent (RQ-032/034, decision 85).
        var reasons = scoped.Where(entry => include(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        return new Snapshot(_root.RootId, _side, gen, cursor, items, reasons.Keys.ToArray())
            { IncludesPath = include, IncompleteReasons = reasons };
    }

    /// <summary>
    /// An incremental delta since a cursor. An expired or moved feed yields a cursor-invalid delta; deletes
    /// carry the stored spelling, id and server deletion time when Dropbox can still prove them.
    /// </summary>
    public async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
        CancellationToken ct) => await ChangesSinceAsync(cursor, gen, include, ct, false);

    internal async Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include,
        CancellationToken ct, bool firstSync)
    {
        var invalid = new Delta(_root.RootId, _side, gen, cursor, cursor, Array.Empty<Change>(), true,
            Array.Empty<string>());
        // Drop run-local state at this inventory boundary. Replay the saved feed over complete
        // persisted classification, then validate evidence against that same feed and basis.
        _deletedExactFromFull = null;
        _deletedAncestorsFromFull = null;
        _evidenceBasis = null;
        _evidenceValidatedThisRun = false;
        if (string.IsNullOrEmpty(cursor) || _known.HasFullListing(_root)) return invalid;
        var classification = _known.ReadDeletionClassification(_root);
        if (firstSync && classification?.Cursor != cursor)
        {
            RunTrace.Emit("first-sync cursor bootstrap: durable tombstone classification missing or basis mismatch; one complete listing required");
            return invalid;
        }
        var (valid, current, feed) = await ReadFeedAsync(cursor, ct);
        if (!valid) return invalid;
        _evidenceBasis = current;
        if (classification?.Cursor == cursor)
        {
            _known.ReplayDeletionClassification(_root, current, feed.Select(change => (change.Path, change.Entry is null)));
            classification = _known.ReadDeletionClassification(_root)!;
            _deletedExactFromFull = classification.Exact;
            _deletedAncestorsFromFull = classification.Ancestors;
        }
        else
        {
            // Ordinary incremental deletes need only the exact tombstones in this feed. A first-sync
            // classification is never inferred from this partial set (the bootstrap guard above).
            var exact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ancestors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var change in feed)
                if (change.Entry is null) { exact.Add(change.Path); ancestors.Add(change.Path); }
                else exact.Remove(change.Path);
            _deletedExactFromFull = exact;
            _deletedAncestorsFromFull = ancestors;
        }
        ReuseEvidence(cursor, current, feed);
        _evidenceValidatedThisRun = true;
        var (changes, pending, released, incomplete) = await BuildChangesAsync(feed, gen, include, ct, firstSync);
        if (firstSync)
        {
            // Historical released rows may predate this cursor and still have stale live inventory.
            // Prune only after a targeted provider observation, never from the failure record itself.
            var repaired = new System.Collections.Concurrent.ConcurrentQueue<Change>();
            var feedPaths = feed.Select(change => change.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(EvidencePages(failuresOnly: true)
                .Where(row => row.Failure!.Released && include(row.Path)).Select(row => row.Path), new ParallelOptions
                { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (path, token) =>
            {
                if (feedPaths.Contains(path) || _known.LiveSpellings(_root.RootId, path).Count == 0) return;
                try
                {
                    var entry = await _client.FindEntryAtPathAsync(path, token);
                    if (entry is not null)
                    {
                        var item = DropboxItems.ToItem(entry);
                        repaired.Enqueue(new(ChangeKind.Upsert, item.ObjectId, item.Path, item, null));
                        return;
                    }
                    var rows = await DeletedRowsAsync([path], token);
                    foreach (var row in rows[path]) repaired.Enqueue(new(ChangeKind.Delete, row.ObjectId, row.Path, null, null));
                }
                catch (Exception error) when (error is not EndpointAccessException &&
                    !(error is OperationCanceledException && token.IsCancellationRequested) &&
                    ReferenceEquals(DropboxItems.MapAccessError(error), error))
                {
                    // Preserve this row and flag it unreadable, rather than turning a complete feed
                    // into a failed root scan. The next targeted repair retries it; no missing
                    // metadata becomes absence or invalidates unrelated cursor coverage (RQ-032).
                    foreach (var row in _known.LiveSpellings(_root.RootId, path))
                    {
                        var held = row with { Flags = row.Flags | ItemFlags.Unreadable };
                        repaired.Enqueue(new(ChangeKind.Upsert, held.ObjectId, held.Path, held, null));
                    }
                    RunTrace.Emit($"released deletion row remains held: {error.GetType().Name}");
                }
            });
            changes = changes.Concat(repaired.OrderBy(change => change.Path, StringComparer.Ordinal)).ToArray();
            // First-sync absence is inventory, not deletion authority: held classification below
            // obtains the revision/hash proof (D78c); matched-at-prepare paths use ordinary rules.
            pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        return new Delta(_root.RootId, _side, gen, cursor, current, changes, false, incomplete.Keys.ToArray())
            { PendingDeletionEvidence = pending, ReleasedDeletionEvidence = released, IncludesPath = include,
                IncompleteReasons = incomplete };
    }

    private async Task<(IReadOnlyList<PilotRemoteEntry> Entries, IReadOnlySet<string> DeletedPaths,
        IReadOnlySet<string> DeletedAncestors, string BasisCursor, IReadOnlyDictionary<string, string> ScopedFailures)>
        ListCheckpointedAsync(
        IProgress<string>? p, CancellationToken ct, Func<string, bool>? include = null, TimeSpan? readBudget = null)
    {
        var checkpoint = OpenListing("full");
        async Task<(IReadOnlyList<PilotRemoteEntry> Entries, IReadOnlySet<string> DeletedPaths,
            IReadOnlySet<string> DeletedAncestors, string BasisCursor, IReadOnlyDictionary<string, string> ScopedFailures)>
            ListAsync() =>
            await _client.ListEntriesWithDeletedPathsAsync(progress =>
            {
                ProgressGuard.Report(p, $"{progress.ObservedCount:N0} items: {progress.Detail}", ct);
                return Task.CompletedTask;
            }, ct, include, checkpoint, readBudget);
        (IReadOnlyList<PilotRemoteEntry> Entries, IReadOnlySet<string> DeletedPaths,
            IReadOnlySet<string> DeletedAncestors, string BasisCursor, IReadOnlyDictionary<string, string> ScopedFailures) listing;
        try
        {
            try { listing = await ListAsync(); }
            catch (DropboxApiException error) when (error.StatusCode == HttpStatusCode.Conflict && checkpoint is not null)
            {
                checkpoint.Complete();
                RunTrace.Emit("Dropbox unfinished listing rejected (cursor or path changed); discarded partial pages and starting a fresh listing.");
                checkpoint = OpenListing("full");
                listing = await ListAsync();
            }
            checkpoint?.Complete();
            return listing;
        }
        catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException ||
            error is DropboxApiException { StatusCode: HttpStatusCode.Conflict })
        {
            // A bad saved response must not poison every retry. Transport failures and Pause retain pages.
            checkpoint?.Complete();
            throw;
        }
    }

    private ListingCheckpoint? OpenListing(string mode) => _known?.SupportsListingCheckpoint == true
        ? new ListingCheckpoint(_known, _root, _pairId, mode) : null;

    // Reads every change page until the feed is exhausted; a reset (409) or a moved root is not a delta.
    private async Task<(bool Valid, string Cursor, List<DropboxChange> Feed)> ReadFeedAsync(string cursor,
        CancellationToken ct)
    {
        var feed = new List<DropboxChange>();
        var checkpoint = OpenListing("delta:" + cursor);
        var current = cursor;
        try
        {
            // Same bound as the provider's recursive listing. Remember every cursor, not just the
            // previous one: a multi-cursor cycle must not grow this feed without bound either.
            const int maximumPages = 100_000;
            var seen = new HashSet<string>(StringComparer.Ordinal) { cursor };
            var pages = 0;
            bool hasMore;
            do
            {
                if (++pages > maximumPages)
                    throw new InvalidDataException($"Dropbox exceeded the bounded change-feed limit of {maximumPages:N0} pages.");
                var page = await _client.ReadChangePageAsync(current, ct, checkpoint, _client.ListingTransientBudget);
                feed.AddRange(page.Changes);
                current = page.Cursor;
                hasMore = page.HasMore;
                if (hasMore && !seen.Add(current))
                    throw new InvalidDataException("Dropbox cycled a change-feed cursor without completing the feed.");
            } while (hasMore);
        }
        catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            checkpoint?.Complete();
            RunTrace.Emit("Dropbox unfinished delta rejected/reset or malformed; discarded partial pages; a full listing is required.");
            return (false, cursor, feed);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or
            KeyNotFoundException or InvalidOperationException or FormatException)
        {
            // Missing/wrong-shaped JSON fields are bad feed data too, not a transient transport failure.
            checkpoint?.Complete();
            RunTrace.Emit("Dropbox unfinished delta rejected/reset or malformed; discarded partial pages; a full listing is required.");
            return (false, cursor, feed);
        }
        catch (Exception exception) when (DropboxItems.MapAccessError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
        checkpoint?.Complete();
        return (true, current, feed);
    }

    private async Task<(IReadOnlyList<Change> Changes, IReadOnlyDictionary<string, string> Pending, IReadOnlySet<string> Released,
        IReadOnlyDictionary<string, string> Incomplete)>
        BuildChangesAsync(IReadOnlyList<DropboxChange> feed, long gen, Func<string, bool> include, CancellationToken ct,
            bool firstSync = false)
    {
        var incomplete = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collapsed = Collapse(feed).Where(change => include(change.Path)).ToList();
        var deleted = await DeletedRowsAsync(collapsed.Where(change => change.Entry is null)
            .Select(change => change.Path).Distinct(StringComparer.Ordinal).ToArray(), ct, incomplete);
        // Unknown membership is per-item read debt, not a deletion and not a root failure (RQ-032/034).
        var unreadable = incomplete.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        collapsed.RemoveAll(change => unreadable.Contains(change.Path) || PairPath.Ancestors(change.Path).Any(unreadable.Contains));
        foreach (var path in incomplete.Keys) deleted.Remove(path);
        // DeletedMetadata has neither kind nor server time. Known folders use the ordinary delta deletion
        // and descendant rules, not file revision history (decision 68). A replay after ingestion can have
        // no live row; its baseline still identifies the folder until its effects finish.
        var folders = deleted.Where(entry => entry.Value.Any(row => row.Kind == ItemKind.Folder))
            .Select(entry => entry.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = deleted.Where(entry => entry.Value.Count == 0).Select(entry => entry.Key).ToArray();
        var baselinePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (unknown.Length > 0)
            foreach (var baseline in _known.BaselinesTouching(_pairId, _side, [], unknown))
            {
                var row = _side == Side.A ? baseline.A : baseline.B;
                baselinePaths.Add(row.Path);
                if (row.Kind == ItemKind.Folder && unknown.Contains(row.Path, StringComparer.OrdinalIgnoreCase))
                    folders.Add(row.Path);
            }
        // Once an earlier pass finished a folder, both its row and baseline can be gone while an
        // unrelated pending file retains the cursor. No remaining item on either side means no victim
        // needs revision evidence. An opposite folder likewise has no file revision history to probe.
        var noFileVictim = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_known.Pair(_pairId) is { } pair)
        {
            var oppositeRoot = _side == Side.A ? pair.B.RootId : pair.A.RootId;
            foreach (var path in unknown)
                if (!baselinePaths.Contains(path) && _known.LiveSpellings(_root.RootId, path).Count == 0 &&
                    _known.LiveSpellings(oppositeRoot, path).All(row => row.Kind == ItemKind.Folder))
                    noFileVictim.Add(path);
        }
        var failures = _known.DeletionEvidenceRows(_pairId, _root.RootId,
            feed.Select(change => change.Path).Concat(deleted.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            .Where(row => row.Failure is not null)
            .ToDictionary(row => row.Path, StringComparer.OrdinalIgnoreCase);
        // A later incarnation retires obsolete evidence and its decision, including evidence whose
        // successful probe already replaced the failure row. Query only observed upserts, never all retries.
        var recreated = feed.GroupBy(change => change.Path, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Last().Entry is not null && (failures.ContainsKey(group.Key) ||
                _known.SyncRetryAt(_pairId, group.Key) is { Kind: "deletion-evidence" or "decision", Held: not null }))
            .Select(group => group.Key).ToArray();
        _known.RemoveDeletionEvidence(_pairId, _root.RootId, recreated);
        foreach (var path in recreated)
        {
            failures.Remove(path);
            if (_known.SyncRetryAt(_pairId, path) is { Kind: "deletion-evidence" or "decision", Held: not null })
                _known.ClearSyncRetry(_pairId, path);
        }
        if (recreated.Length > 0)
        {
            foreach (var path in recreated)
                foreach (var reason in new[] { "deletion-review", "deletion-history-unavailable", "deletion-evidence-pending" })
                    _known.CloseUnresolvedNotices(_pairId, reason, "remote-recreated", path);
        }
        // Upgrade previously released failures into the same ordinary retry queue, including
        // empty feeds. Respect Ignore/exclusion just as for fresh feed entries: its cached failure
        // must not recreate the retry and reopen a decision the user explicitly dismissed.
        // Recreating a file above resets evidence but never authorizes a stale deletion.
        // A legacy failure is not itself a surviving victim. Inventing a hold without one
        // blocks ordinary safe downloads. Existing holds are never cleared by this lookup.
        var consumer = _known.Pair(_pairId);
        var survivorRoot = consumer is null ? null : (_side == Side.A ? consumer.B.RootId : consumer.A.RootId);
        foreach (var failure in EvidencePages(failuresOnly: true).Where(row => row.Failure!.Released && include(row.Path)))
            if (_known.SyncRetryAt(_pairId, failure.Path) is null &&
                survivorRoot is not null && _known.Live(survivorRoot, [failure.Path]).Count > 0)
                _known.SaveSyncRetry(_pairId, new SyncRetry(failure.Path, "deletion-evidence",
                    failure.Failure!.Attempts, "Deletion history was unavailable.", DateTimeOffset.MinValue,
                    Held: new HeldDeletion(failure.Path, _side == Side.A ? Side.B : Side.A)));
        var released = failures.Values.Where(row => row.Failure!.Released).Select(row => row.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletePaths = deleted.Keys.Where(path => !folders.Contains(path) && !noFileVictim.Contains(path) &&
            !released.Contains(path)).ToArray();
        var evidence = await DeletionEvidenceAsync(deletePaths, ct);
        ct.ThrowIfCancellationRequested();
        var pending = evidence.Where(entry => entry.Value.LookupFailed)
            .ToDictionary(entry => entry.Key, entry => entry.Value.Reason, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;
        foreach (var (path, result) in evidence)
        {
            if (!result.LookupFailed)
            {
                // Also reset failures for a confirmed no-history result, which need not create a cache row.
                if (failures.ContainsKey(path))
                    _known.StoreDeletionEvidence(_pairId, _root.RootId,
                        [new DeletionEvidenceRecord(path, result.DeletedUtc, _evidenceBasis, result.ContentHash)]);
                continue;
            }
            var previous = failures.GetValueOrDefault(path)?.Failure;
            // RQ-052 owner decision (2026-10-20): a transient deletion-history failure keeps its automatic
            // retry. The item is released from the feed (its durable hold protects the local survivor) and
            // enqueued as a due-now Held/ReprobeEvidence row; the existing held-evidence lane in SyncRun
            // owns the answered-run count and backoff and probes it again. A permanent malformed/refused
            // answer skips that budget and goes straight to the authorized review flow. No second budget
            // or backoff implementation lives here.
            // Historical fact counter only (the answered-run budget is Run-owned). One increment per
            // generation avoids drift when the same feed is processed twice in one run.
            var attempts = (previous?.Attempts ?? 0) + (previous?.Generation == gen ? 0 : 1);
            var first = previous?.FirstUtc ?? now;
            // Persist item-owned retry input before releasing this cursor.
            var oldRetry = _known.SyncRetryAt(_pairId, path);
            if (oldRetry is null)
                // Attempts stays 0: the answered-run budget belongs to the Run owner, which counts the
                // re-probes it actually makes. Seeding it here with an unanswered outage would spend it.
                _known.SaveSyncRetry(_pairId, new SyncRetry(path, "deletion-evidence", 0, result.Reason,
                    DateTimeOffset.MinValue, Held: new HeldDeletion(path, _side == Side.A ? Side.B : Side.A))
                    { ReprobeEvidence = true, EvidenceReleased = result.PermanentFailure });
            _known.StoreDeletionEvidence(_pairId, _root.RootId,
                [new DeletionEvidenceRecord(path, null, Failure: new(attempts, first, gen, true))]);
            released.Add(path);
        }
        var changes = new List<Change>();
        foreach (var change in collapsed)
        {
            // Released tombstones are observations, not local-delete authority. Their durable retry row
            // protects the local victim in both directions while the remote absence is ingested and the cursor advances.
            // Keeping a phantom remote row would make a later explicit choice fail its OppositeAbsent guard.
            if (!firstSync && pending.Keys.Where(path => !released.Contains(path)).Any(path => PairPath.IsSelfOrUnder(change.Path, path) ||
                PairPath.IsUnder(path, change.Path))) continue;
            if (change.Entry is null)
            {
                var deletedUtc = evidence.TryGetValue(change.Path, out var item) ? item.DeletedUtc : null;
                var rows = deleted[change.Path];
                if (rows.Count == 0) changes.Add(new Change(ChangeKind.Delete, "", change.Path, null, deletedUtc));
                foreach (var row in rows.Where(row => include(row.Path)))
                    changes.Add(new Change(ChangeKind.Delete, row.ObjectId, row.Path, null, deletedUtc));
            }
            else
            {
                var item = DropboxItems.ToItem(change.Entry);
                changes.Add(new Change(ChangeKind.Upsert, item.ObjectId, item.Path, item, null));
            }
        }
        return (changes, pending, released, incomplete);
    }

    // B-126, RQ-039: a deleted path's identity comes from its exact stored spelling only (the feed's path_display,
    // or path_lower when Dropbox sends no display path). Another stored spelling is named only on evidence that
    // its object left that path: Dropbox no longer holds it, holds it outside this root, or holds it elsewhere in
    // it. An object still at that path (a surviving spelling) is kept, and the delete then names no object, so
    // Storage applies it to the exact spelling alone and a replay after a crash never reaches a survivor. A failed
    // membership read protects its path as incomplete, never becoming absence. Candidates come from one keyed
    // lookup per path; their memberships are read once per distinct object, after one root check.
    private async Task<Dictionary<string, List<ItemState>>> DeletedRowsAsync(IReadOnlyList<string> paths,
        CancellationToken ct, IDictionary<string, string>? incomplete = null)
    {
        var rows = new Dictionary<string, List<ItemState>>(StringComparer.Ordinal);
        var others = new Dictionary<string, List<ItemState>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var spellings = _known.LiveSpellings(_root.RootId, path);
            var exact = spellings.Where(row => string.Equals(row.Path, path, StringComparison.Ordinal)).ToList();
            rows[path] = exact;
            if (exact.Count == 0 && spellings.Count > 0) others[path] = spellings.ToList();
        }
        if (others.Count == 0) return rows;
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var places = await _client.CurrentRelativePathsAsync(
            others.Values.SelectMany(list => list).Select(row => row.ObjectId), ct,
            incomplete is null ? null : (id, error) => failures[id] = error.Message);
        foreach (var (path, candidates) in others)
        {
            if (candidates.Any(row => failures.ContainsKey(row.ObjectId)))
            {
                incomplete![path] = "Dropbox metadata could not be read: " +
                    failures[candidates.First(row => failures.ContainsKey(row.ObjectId)).ObjectId];
                continue;
            }
            rows[path] = candidates.Where(row => places[row.ObjectId] is not { } now || !PairPath.Same(now, row.Path))
                .ToList();
        }
        return rows;
    }

    /// <summary>
    /// Stored hashes for the requested paths. A stored row without a hash falls back to one exact provider read,
    /// accepted only while the observed item is still the stored id and revision (a newer version stays unresolved).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var row in _known.Live(_root.RootId, paths))
            {
                if (row.Hash is not null)
                {
                    result[row.Path] = row.Hash;
                    continue;
                }
                var entry = await _client.FindEntryAtPathAsync(row.Path, ct);
                if (entry is { Kind: PilotEntryKind.File, ContentHash: not null } &&
                    string.Equals(entry.Id, row.ObjectId, StringComparison.Ordinal) &&
                    string.Equals(entry.Revision, row.VersionId, StringComparison.Ordinal))
                    result[row.Path] = entry.ContentHash;
            }
        }
        catch (Exception exception) when (DropboxItems.MapAccessError(exception) is var mapped && mapped != exception)
        {
            throw mapped;
        }
        return result;
    }

    /// <summary>Server deletion times from revision history; an unproven path or a failed lookup is left out.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var times = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var results = await DeletionEvidenceAsync(paths, ct);
        if (results.Values.Any(evidence => evidence.LookupFailed))
            throw new IOException("Dropbox deletion evidence is unresolved; absence must not authorize a sync effect.");
        foreach (var (path, evidence) in results)
            if (evidence.DeletedUtc is { } deletedUtc) times[path] = deletedUtc;
        return times;
    }

    private Snapshot WholeIncomplete(long gen, string? reason = null) =>
        new(_root.RootId, _side, gen, "", Array.Empty<ItemState>(), new[] { "" })
        {
            IncompleteReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { [""] = reason ?? "The Dropbox folder could not be read completely. Nothing was inferred absent." }
        };

    // Sequential change semantics in feed order: a delete closes everything at or under its path, a later
    // upsert replaces a pending upsert at the same path while pending deletes stay. A file replacing a folder
    // at p arrives as one file entry, so every descendant under p/ (pending and stored) is invalidated here.
    //
    // The collapse is keyed: one dictionary of the live upsert slot per path, one sorted key set for the
    // prefix/descendant operations and one tombstone set. Every pending upsert is removed at most once, so a
    // feed of N changes does O(N log N) work, not the previous O(N^2) list scans. This feed is ordered, so a
    // later observation at one path is its last observation: the same object (a case-only rename or a new
    // revision) or a different id (a sequential delete+recreate, or a move onto the path) replaces the pending
    // slot. A different id is never a collision and the prior id is never inferred deleted. Distinct live ids
    // reported by the unordered full-listing walk remain collisions at ReadPage/MergeLocal.
    private List<DropboxChange> Collapse(IReadOnlyList<DropboxChange> feed)
    {
        var slots = new List<DropboxChange?>(feed.Count);
        var upsertSlot = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var upsertKeys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tombstones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void DropUpsert(string path)
        {
            if (!upsertSlot.Remove(path, out var index)) return;
            slots[index] = null;
            upsertKeys.Remove(path);
        }

        // Pending live observations strictly under (optionally at) this path. One range read of the key set
        // instead of a scan of the whole pending list for every change.
        void DropUpserts(string path, bool self)
        {
            if (self && upsertSlot.ContainsKey(path)) DropUpsert(path);
            if (path.Length == 0)
            {
                foreach (var key in upsertKeys.ToArray()) DropUpsert(key);
                return;
            }
            var prefix = path + "/";
            // '0' is the ordinal successor of '/'; unlike a trailing U+FFFF sentinel,
            // this includes descendants whose first character is U+FFFF as well.
            var view = upsertKeys.GetViewBetween(prefix, path + "0");
            foreach (var key in view.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray())
                DropUpsert(key);
        }

        void AddDelete(string path)
        {
            slots.Add(new DropboxChange(path, null));
            tombstones.Add(path);
        }

        foreach (var change in feed)
        {
            if (change.Entry is null)
            {
                DropUpserts(change.Path, self: true);
                AddDelete(change.Path);
                continue;
            }
            if (change.Entry.Kind == PilotEntryKind.File)
            {
                DropUpserts(change.Path, self: false);
                foreach (var descendant in _known.LiveUnder(_root.RootId, new[] { change.Path })
                    .Where(row => PairPath.IsUnder(row.Path, change.Path)))
                    // A pending tombstone already owns this path; do not duplicate it. A pending live
                    // observation for a descendant was just removed above.
                    if (tombstones.Add(descendant.Path))
                        slots.Add(new DropboxChange(descendant.Path, null));
            }
            if (upsertSlot.TryGetValue(change.Path, out var existing))
            {
                // The ordered feed's last observation at this path wins. Same id is a case-only rename or
                // newer revision; a different id is a replacement (delete+recreate or a move onto the path).
                slots[existing] = change;
                continue;
            }
            slots.Add(change);
            upsertSlot[change.Path] = slots.Count - 1;
            upsertKeys.Add(change.Path);
        }
        return slots.Where(slot => slot is not null).Select(slot => slot!).ToList();
    }

    private IEnumerable<DeletionEvidenceRecord> EvidencePages(bool failuresOnly)
    {
        string? after = null;
        while (true)
        {
            var page = _known.DeletionEvidencePage(_pairId, _root.RootId, after, 256, failuresOnly);
            if (page.Count == 0) yield break;
            after = page[^1].Path;
            foreach (var row in page) yield return row;
        }
    }

    private IReadOnlyDictionary<string, DeletionEvidenceRecord> ReuseEvidence(string input, string current,
        IReadOnlyList<DropboxChange> feed)
    {
        var changed = feed.Select(change => change.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ancestors = changed.SelectMany(PairPath.Ancestors).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var page in EvidencePages(failuresOnly: false).Chunk(256))
        {
            var valid = new List<DeletionEvidenceRecord>();
            var invalid = new List<string>();
            foreach (var row in page)
                if (row.BasisCursor != input || changed.Contains(row.Path) || ancestors.Contains(row.Path) ||
                    PairPath.Ancestors(row.Path).Any(changed.Contains))
                    invalid.Add(row.Path);
                else valid.Add(row with { BasisCursor = current });
            // Mutating a page cannot shift the path-key continuation. Release the store gate between pages.
            _known.RemoveDeletionEvidence(_pairId, _root.RootId, invalid);
            _known.StoreDeletionEvidence(_pairId, _root.RootId, valid);
        }
        // Validated results stay in SQLite; callers load only the paths they actually probe.
        return new Dictionary<string, DeletionEvidenceRecord>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyDictionary<string, SyncRemoteDeletionEvidence>> DeletionEvidenceAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return NoEvidence;
        try
        {
            var cached = await PrepareEvidenceCacheAsync(paths, ct);
            return await _client.LookupDeletionEvidenceForV2Async(paths,
                _deletedExactFromFull,
                _deletedAncestorsFromFull, cached,
                StoreProbedEvidence, ct);
        }
        catch (Exception exception) when (exception is not (EndpointAccessException or PairPausedException) &&
            !(exception is OperationCanceledException && ct.IsCancellationRequested))
        {
            if (DropboxItems.MapAccessError(exception) is var mapped && mapped != exception) throw mapped;
            // Batch-wide fallback: never permanent; one bad listing cannot release every path.
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path,
                _ => new SyncRemoteDeletionEvidence(null, $"Dropbox deletion evidence lookup failed: {exception.Message}")
                    { LookupFailed = true }, StringComparer.OrdinalIgnoreCase);
        }
    }

    // The cache validated once in this run (by held classification) is reused by the deferred evidence lane: re-validating
    // replays the Dropbox change feed from the oldest checkpoint, which during a first-sync apply includes every upload
    // the run itself made, and stalled the lane for 10+ minutes before failing. The lane's paths are exactly the ones
    // that cache did not answer, so reusing it within the run reuses no stale evidence.
    private bool _evidenceValidatedThisRun;

    /// <summary>
    /// Takes over a previous inventory's tombstone classification and validated evidence cache for the same root
    /// within one run. The run reopens its endpoints after the preview, and without this the deferred evidence lane
    /// started from an empty classification and replayed the whole change feed before its first lookup.
    /// </summary>
    internal void AdoptClassificationFrom(DropboxInventory previous)
    {
        if (previous._evidenceBasis is null) return;
        _deletedExactFromFull = previous._deletedExactFromFull;
        _deletedAncestorsFromFull = previous._deletedAncestorsFromFull;
        _evidenceBasis = previous._evidenceBasis;
        _evidenceValidatedThisRun = previous._evidenceValidatedThisRun;
    }

    private async Task<IReadOnlyDictionary<string, DeletionEvidenceRecord>> PrepareEvidenceCacheAsync(
        IReadOnlyList<string> paths, CancellationToken ct)
    {
        await PrepareEvidenceCacheAsync(ct);
        var cached = new Dictionary<string, DeletionEvidenceRecord>(StringComparer.OrdinalIgnoreCase);
        // Classification owns probe eligibility. Never create a SQLite reader per never-existed
        // candidate merely to discover there is no cached evidence (100k candidates exceed the
        // allocation budget that way). Keep selection/gate batches bounded even for many matches.
        foreach (var batch in paths.Where(path => DropboxPilotClient.IsDeletionProbe(path,
            _deletedExactFromFull!, _deletedAncestorsFromFull!)).Chunk(256))
            foreach (var row in _known.DeletionEvidenceRows(_pairId, _root.RootId, batch))
                if (row.Failure is null && row.BasisCursor == _evidenceBasis) cached[row.Path] = row;
        return cached;
    }

    private async Task<IReadOnlyDictionary<string, DeletionEvidenceRecord>> PrepareEvidenceCacheAsync(CancellationToken ct)
    {
        if (_evidenceValidatedThisRun && _evidenceBasis is not null)
            return new Dictionary<string, DeletionEvidenceRecord>(StringComparer.OrdinalIgnoreCase);
        async Task ReadClassificationAsync()
        {
            var listing = await ListCheckpointedAsync(null, ct);
            _deletedExactFromFull = listing.DeletedPaths;
            _deletedAncestorsFromFull = listing.DeletedAncestors;
            _evidenceBasis = listing.BasisCursor;
            _known.ReplaceDeletionClassification(_root, listing.BasisCursor, listing.DeletedPaths, listing.DeletedAncestors);
        }
        if (_evidenceBasis is null) await ReadClassificationAsync();
        var cached = ReuseEvidence(_evidenceBasis!, _evidenceBasis!, []);
        _evidenceValidatedThisRun = true;
        return cached;
    }

    public bool IsAtOrUnderExactTombstone(string path) => _deletedExactFromFull is { } exact &&
        (exact.Contains(path) || PairPath.Ancestors(path).Any(exact.Contains));

    public IReadOnlyList<string> FolderDeletionCandidates(IReadOnlyList<string> paths)
    {
        if (_deletedExactFromFull is null || _deletedAncestorsFromFull is null) return [];
        return paths.Where(path => DropboxPilotClient.IsDeletionProbe(path,
            _deletedExactFromFull, _deletedAncestorsFromFull)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<DeletionEvidencePreparation> PrepareDeletionEvidenceAsync(IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        if (paths.Count == 0)
            return new(new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase), []);
        var names = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        try
        {
            var cached = await PrepareEvidenceCacheAsync(paths, ct);
            var resolved = new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<string>();
            var matched = 0;
            foreach (var path in names)
            {
                if (!DropboxPilotClient.IsDeletionProbe(path, _deletedExactFromFull!, _deletedAncestorsFromFull!))
                    resolved[path] = new SyncRemoteDeletionEvidence(null,
                        "Dropbox returned no retained deleted entry at this exact path; remote absence alone does not prove deletion.");
                else
                {
                    matched++;
                    if (cached.TryGetValue(path, out var stored) && stored.HasRequiredContentHash)
                        resolved[path] = new SyncRemoteDeletionEvidence(stored.DeletedUtc,
                            "Stored Dropbox deletion evidence from an earlier run.", ContentHash: stored.ContentHash);
                    else pending.Add(path);
                }
            }
            RunTrace.Emit($"held classification: requested {names.Length:N0}, tombstone-matched {matched:N0}, held {pending.Count:N0}, classification ok (complete cursor-bound tombstones)");
            return new(resolved, pending);
        }
        catch (Exception exception) when (exception is not (EndpointAccessException or PairPausedException) &&
            !(exception is OperationCanceledException && ct.IsCancellationRequested))
        {
            if (DropboxItems.MapAccessError(exception) is var mapped && mapped != exception) throw mapped;
            // Failed classification is not proof of no evidence; all candidates remain held.
            var matched = _deletedExactFromFull is not null && _deletedAncestorsFromFull is not null
                ? names.Count(path => DropboxPilotClient.IsDeletionProbe(path, _deletedExactFromFull, _deletedAncestorsFromFull)).ToString("N0")
                : "unknown";
            RunTrace.Emit($"held classification: requested {names.Length:N0}, tombstone-matched {matched}, held {names.Length:N0}, classification failed ({exception.GetType().Name}: {exception.Message})");
            return new(new Dictionary<string, SyncRemoteDeletionEvidence>(StringComparer.OrdinalIgnoreCase), names);
        }
    }

    public async Task ResolveDeletionEvidenceAsync(IReadOnlyList<string> paths,
        Action<string, SyncRemoteDeletionEvidence> resolved, CancellationToken ct)
    {
        if (paths.Count == 0) return;
        RunTrace.Emit($"deletion evidence lane: {paths.Count:N0} path(s) to resolve");
        var cached = await PrepareEvidenceCacheAsync(paths, ct);
        await _client.LookupDeletionEvidenceForV2Async(paths,
            _deletedExactFromFull,
            _deletedAncestorsFromFull, cached, StoreProbedEvidence, ct, resolved);
    }

    private void StoreProbedEvidence(IReadOnlyList<DeletionEvidenceRecord> batch)
    {
        var stored = batch.Select(row => row with { BasisCursor = _evidenceBasis }).ToArray();
        _known.StoreDeletionEvidence(_pairId, _root.RootId, stored);
        // Fresh probes stay in the store and share this run's validated classification basis.
        // Subsequent requests select only their own paths, without accumulating the run's history.
    }
}
