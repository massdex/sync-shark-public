using Massdex.SyncShark.Engine.Planner;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Prepares observed rows and their current content evidence for the existing planner.</summary>
internal readonly struct ItemPreparation(ExecutorContext _context, Parents _parents, KeepBothRunner _keepBoth, ItemSync items)
{
    private RowContexts Rows => items.Rows;

    /// <summary>Prepares a live row and returns the label set its action needs, so no caller chooses labels.
    /// The replacement phase is the owner's answer too; the caller only sequences live admission.</summary>
    internal async ValueTask<ItemSync.LivePreparation> PrepareLiveAsync(Row row, CancellationToken ct, LocalEvidence.DirtyProof? evidence = null,
        ItemSync.LivePreparation? previous = null, bool retireReplaced = false)
    {
        // The held episode's own file id, not the binding's, decides a replacement (B-177).
        if (retireReplaced && row.Local?.Id != row.Synced?.LocalId) await RetireReplacedAsync(row, ct);
        var retried = previous?.Retried == true;
        var unchanged = previous?.Unchanged == true;
        var reobserve = false;
        var owned = _context.Evidence.CaptureIfHashed(row);
        while (true)
        {
            try
            {
                if (reobserve)
                {
                    (row, owned, evidence, unchanged, _) = await RefreshAsync(
                        row, owned, new Observer(_context, _parents), ct);
                }
                var action = await PrepareAsync(row, ct, evidence);
                row = action.Row;
                owned = _context.Evidence.CaptureIfHashed(row);
                var labels = action.Kind == ActionKind.KeepBoth ? await _keepBoth.ChooseLabelsAsync(action.Row, ct) : null;
                return new(action, labels, ItemReplacement.RequiresFolderReplacement(action), retried, unchanged);
            }
            catch (ChangedUnderneathException error) when (IssuePolicy.CanRetryChanged(error))
            {
                _context.Evidence.Release(row, owned);
                if (IssuePolicy.StaleRetryWaits(retried, liveSelection: true)) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                _context.Events(RunEvent.RetryChanged(Row.Path(row)));
                retried = reobserve = true;
            }
            catch
            {
                _context.Evidence.Release(row, owned);
                throw;
            }
        }
    }

    // Preparation transfers one proof to its returned action; failed preparation must revoke any digest it owned.
    internal async ValueTask<SyncAction> PrepareAsync(Row row, CancellationToken ct, LocalEvidence.DirtyProof? evidence = null)
    {
        var owned = _context.Evidence.CaptureIfHashed(row);
        try
        {
            ct.ThrowIfCancellationRequested();
            row = Rows.RefreshDeletionEligibility(row);
            if (Decide.WithoutContent(row) is { } settled) return AdmitInBatch(settled);
            if (row.Synced is null && row.Local is { } local && row.Dropbox is null)
                row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Dropbox, local.Path) } };
            else if (row.Synced is null && row.Dropbox is { } remote && row.Local is null)
                row = row with { Context = row.Context with { Destination = _parents.Destination(Side.Local, remote.Path) } };
            if (evidence is null && LocalEvidence.AppliesTo(row)) evidence = ReadDirty(row);
            var currentHash = _context.Evidence.CurrentHash(row, evidence);
            if (currentHash is null && (evidence?.Dirty == true || owned?.Hash is not null))
            {
                _context.Evidence.Release(row, owned);
                owned = null;
                row = row with { LocalHash = null };
            }
            var dirty = _context.Evidence.ContentUnproven(row, evidence, currentHash, hashKnown: true);
            var action = AdmitInBatch(Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), dirty));
            if (action.Kind == ActionKind.NeedDeletionEvidence)
            {
                row = row with { Deletion = await _context.ReadDeletionEvidenceAsync(action.Target!, row.IsFolder(), ct) };
                if (row.Deletion is DeletionEvidence.LiveAtTarget live)
                    row = IssuePolicy.ReobserveFirstRun(row, row with { Dropbox = live.Location.Entry, Deletion = null });
                else if (row.Deletion is DeletionEvidence.LiveElsewhere)
                    throw new DeletionEvidenceRefusedException(action.Target!);
                action = Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(), dirty);
            }
            // A held row settles without a hash; a validated current digest needs no second read.
            if (action.Kind == ActionKind.NeedLocalHash ||
                evidence?.Dirty == true && currentHash is null && action.None != NoneReason.Held)
            {
                evidence ??= ReadDirty(row);
                var hashed = await _context.Evidence.HashAsync(_context.Local, row.Local!, row.Synced,
                    (evidence.Dirty, evidence.Boundary, evidence.Version), ct);
                row = row with { LocalHash = hashed.Hash };
                evidence = new LocalEvidence.DirtyProof(hashed.Proof.Dirty, hashed.Proof.Boundary, hashed.Proof.Version);
                owned = _context.Evidence.Remember(row, evidence, hashed.Hash);
                action = Decide.For(row, _context.Pair.FirstRun, _context.Time.GetUtcNow(),
                    _context.Evidence.ContentUnproven(row, evidence));
            }
            else if (currentHash is null && evidence is not null && action.WritesLocal() && LocalEvidence.AppliesTo(row))
                owned = _context.Evidence.Remember(row, evidence);
            return AdmitInBatch(action);
        }
        catch
        {
            _context.Evidence.Release(row, owned);
            throw;
        }
    }

    internal SyncAction AdmitInBatch(SyncAction action) => _context.BatchScope is null ? action :
        Decide.AdmitInBatch(action, action.Row.Synced is null && BoundById(action.Row),
            _context.BatchUnlisted, _context.BatchSettlesFirstRun);

    internal bool BoundById(Row row)
    {
        if (row.Local is { } local && _context.Store.SyncedById(_context.Pair.PairId, Side.Local, local.Id) is not null ||
            row.Dropbox is { } remote && _context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, remote.Id) is not null)
            return true;
        foreach (var entry in row.Extra)
            if (_context.Store.SyncedById(_context.Pair.PairId, Side.Dropbox, entry.Id) is not null) return true;
        return false;
    }

    internal async ValueTask<(Row Row, LocalEvidence.DirtyProof? Owned, LocalEvidence.DirtyProof? Evidence,
        bool Unchanged, string? Hash)> RefreshAsync(Row row, LocalEvidence.DirtyProof? owned,
        Observer observer, CancellationToken ct, Entry? observed = null, bool compare = true)
    {
        // Adoption compares the pre-observation authority; ordinary reobservation retains its original read order.
        var previousHash = observed is null ? null : _context.Evidence.CurrentHash(row);
        var fresh = await ReobserveAsync(row, observer, ct);
        return _context.Evidence.Adopt(row, owned, fresh,
            observed is not null ? previousHash : compare ? _context.Evidence.CurrentHash(row) : null,
            observed, compare);
    }

    // B-177: the episode is read before this Local read, so another file id found there is newer evidence that the
    // episode's file is gone.
    internal async Task RetireReplacedAsync(Row row, CancellationToken ct)
    {
        if (row.Local is { IsFolder: false } listed && _context.Store.ReadReplacedItemRetry(_context.Pair.PairId,
                _context.ExpectedGeneration, listed.Path, listed.Id) is { } held &&
            await _context.Local.ReadOneAsync(listed.Path, ct) is { IsFolder: false } fresh)
            _context.Store.RetireReplacedItemRetry(held, listed.Path, fresh.Id);
    }

    internal async Task<Row> ReobserveAsync(Row row, Observer observer, CancellationToken ct)
    {
        var paths = Row.Paths(row);
        var side = Decide.CrossedFar(row.Local, row.Dropbox, row.Synced) ??
            (row.Local is not null || row.Dropbox is null ? Side.Local : Side.Dropbox);
        var fresh = await observer.ObserveAsync(side, side == Side.Local ? paths.Local : paths.Dropbox, ct);
        fresh = fresh with { Context = row.Context with
        {
            Destination = fresh.Context.Destination,
            Skipped = row.Context.Skipped || fresh.Context.Skipped
        } };
        if (row.Synced is null && Decide.CrossedFar(fresh.Local, fresh.Dropbox, fresh.Synced) is null)
        {
            var local = row.Local is null ? null : fresh.Local;
            var dropbox = row.Dropbox is null ? null : fresh.Dropbox;
            var localBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, PathNames.KeyFor(Side.Local, paths.Local));
            var dropboxBinding = _context.Store.SyncedByKey(_context.Pair.PairId, Side.Dropbox, PathNames.KeyFor(Side.Dropbox, paths.Dropbox));
            if (Decide.IsFreeIncoming(local, dropbox, localBinding, dropboxBinding))
                fresh = fresh with { Local = local, Dropbox = dropbox, Synced = null };
        }
        return Rows.RefreshDeletionEligibility(IssuePolicy.ReobserveFirstRun(row, fresh));
    }

    // A content answer exists only for a saved same-kind file; a kind change decides before content is read.
    internal LocalEvidence.DirtyProof? ReadDirtyIfApplicable(Row row) =>
        LocalEvidence.AppliesTo(row) ? ReadDirty(row) : null;

    /// <summary>The one stored dirty answer for this row, or null when no saved same-kind content answer applies.
    /// The owner is the store's LocalEvidence; callers never supply a literal.</summary>
    internal bool? LocalContentDirty(Row row) => ReadDirtyIfApplicable(row)?.Dirty;

    private LocalEvidence.DirtyProof ReadDirty(Row row)
    {
        var evidence = _context.Evidence.Read(row.Local ?? throw new ArgumentException(nameof(row)), row.Synced);
        return new LocalEvidence.DirtyProof(evidence.Dirty, evidence.Boundary, evidence.Version);
    }

}
