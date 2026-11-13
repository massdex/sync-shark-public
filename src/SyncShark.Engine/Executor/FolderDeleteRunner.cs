using System.Text.Json;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Executor;

/// <summary>Coordinates durable selected folder effects, receipt reconciliation and admitted recovery.</summary>
public sealed class FolderDeleteRunner
{
    private const long BeforeFirstOrdinal = 0 - 1;
    private const long RootOrdinal = 0;

    /// <summary>The provider's most effects in one submission; the caller streams to this bound.</summary>
    internal const int MaxEffectsPerSubmission = DropboxApiLimits.BatchMaxEntries;

    private readonly ExecutorContext _context;
    private readonly ActionExecutor _actions;
    private readonly IRecoveryExecutionBoundary _recovery;
    private readonly LocalFolderEffects _local;

    /// <summary>Uses the shared action owner and recovery admission boundary without invoking either during construction.</summary>
    internal FolderDeleteRunner(ExecutorContext context, ActionExecutor actions,
        IRecoveryExecutionBoundary recovery)
    {
        _context = context;
        _actions = actions;
        _recovery = recovery;
        _local = new LocalFolderEffects(context, actions, Read, IsFrozen, LateAsync, path => Report(Side.Local, path));
    }

    private string PairId => _context.Pair.PairId;

    /// <summary>Submits eligible effects with durable member attribution and joins their jobs before guarded continuation.
    /// Only a Complete aggregate permits whole-root closure or replacement.</summary>
    public async Task<IReadOnlyList<FolderRemovalResult>> RunGroupAsync(
        IReadOnlyList<FolderRemovalEffect> eligibleEffects, CancellationToken ct)
    {
        var operations = eligibleEffects.Select(effect => effect.OperationId).Distinct(StringComparer.Ordinal)
            .Select(Read).ToArray();
        // A submission has one deleting side; independent roots of that side share one provider batch.
        foreach (var group in operations.GroupBy(operation => operation.DeletingSide))
        {
            await _recovery.ExecuteAsync<bool>(group.ToArray(), async (execution, token) =>
            {
                var eligible = new List<FolderRemovalEffect>();
                foreach (var operation in group)
                    eligible.AddRange(await EligibleAsync(Read(operation.OperationId), eligibleEffects
                        .Where(effect => effect.OperationId == operation.OperationId), execution, token));
                return await SendAndJoinAsync(eligible, execution, token);
            }, ct);
        }
        return operations.Select(operation => Read(operation.OperationId)).Select(current =>
            new FolderRemovalResult(current.OperationId, current.RootEntry.Path, Status(current.Phase))).ToArray();
    }

    /// <summary>Sends each accumulated batch in turn; the batch owner applies the provider's bound across pages.</summary>
    internal async Task RunPreparedAsync(IEnumerable<IReadOnlyList<FolderRemovalEffect>> pages, CancellationToken ct)
    {
        foreach (var batch in Batches(pages)) await RunGroupAsync(batch, ct);
    }

    /// <summary>Streams pages into provider-legal batches; a full batch crosses page and operation boundaries.</summary>
    internal static IEnumerable<IReadOnlyList<FolderRemovalEffect>> Batches(
        IEnumerable<IReadOnlyList<FolderRemovalEffect>> pages)
    {
        var ready = new List<FolderRemovalEffect>(MaxEffectsPerSubmission);
        foreach (var page in pages)
        foreach (var effect in page)
        {
            ready.Add(effect);
            if (ready.Count < MaxEffectsPerSubmission) continue;
            yield return ready;
            ready = new List<FolderRemovalEffect>(MaxEffectsPerSubmission);
        }
        if (ready.Count != 0) yield return ready;
    }

    /// <summary>Whether the parent's effects reached follow-up eligibility: every effect verified or absent, nothing
    /// unknown, in flight, unsent, refused or in custody, and the post-window evidence closed.</summary>
    internal bool FollowUpsReady(FolderRemovalState operation, CancellationToken ct)
    {
        var progress = new Progress();
        foreach (var item in Members(operation.OperationId, ct)) progress.Observe(item);
        return operation.Active && operation.Cursor is null && !progress.Blocked && !progress.Ready &&
            !progress.Failed;
    }

    /// <summary>Reconciles receipts; Recovery owns transient retries. An optional terminal continuation runs after claim release.</summary>
    public async Task ReconcileFolderRemovalsAsync(CancellationToken ct, bool progressOnly = false,
        Func<string, CancellationToken, Task>? finish = null)
    {
        var finished = finish is null ? null : new HashSet<string>(StringComparer.Ordinal);
        foreach (var submission in _context.Store.PendingFolderSubmissions(PairId, ct, progressOnly))
        {
            ct.ThrowIfCancellationRequested();
            if (!PairRuntimes.TryClaimFolderJob(submission.SubmissionId)) continue;
            IReadOnlyList<FolderRemovalEffect>? completed = null;
            try
            {
                var current = _context.Store.FolderSubmission(submission.SubmissionId);
                if (current.Phase != FolderSubmission.SubmissionPhase.Accepted ||
                    progressOnly && current.Cause is not null) continue;
                var effects = _context.Store.FolderSubmissionEffects(current.SubmissionId);
                var episode = new object();
                while (true)
                {
                    var step = await ObserveAsync(current, effects.Count, episode, false, ct);
                    if (!step.Retry)
                    {
                        if (finish is not null && step.Terminal) completed = effects;
                        break;
                    }
                    current = _context.Store.FolderSubmission(current.SubmissionId);
                    if (current.Phase != FolderSubmission.SubmissionPhase.Accepted) break;
                }
            }
            finally
            {
                PairRuntimes.ReleaseFolderJob(submission.SubmissionId);
            }
            if (completed is not null)
                foreach (var effect in completed)
                    if (finished!.Add(effect.OperationId)) await finish!(effect.OperationId, ct);
        }
    }

    /// <summary>Continues the durable operation through fresh recovery admission without replaying uncertain effects.</summary>
    public async Task ResumeAsync(FolderRemovalState operation, WorkSelection? selection, CancellationToken ct)
    {
        var operationId = operation.OperationId;
        var more = operation.Active;
        while (more)
        {
            // Jobs finish outside admission; every settlement and later send reacquires root and operation authority.
            await JoinAsync([operationId], ct);
            var current = Read(operationId);
            more = current.Active && await _recovery.ExecuteAsync(current,
                (execution, token) => ContinueAsync(operationId, execution, selection, token), ct);
        }
    }

    private FolderRemovalState Read(string operationId) => _context.Store.FolderRemoval(PairId, operationId);

    internal static FolderRemovalStatus Status(FolderRemovalPhase phase) => phase switch
    {
        FolderRemovalPhase.Complete => FolderRemovalStatus.Complete,
        FolderRemovalPhase.Unknown => FolderRemovalStatus.Unknown,
        FolderRemovalPhase.Retained => FolderRemovalStatus.Retained,
        _ => FolderRemovalStatus.Partial
    };

    // True when another send happened, so its receipts must be joined outside this lease before settling.
    private async Task<bool> ContinueAsync(string operationId, IAdmittedExecution execution, WorkSelection? selection, CancellationToken ct)
    {
        var operation = Read(operationId);
        var progress = ReadProgress(operation, ct);
        if (progress.Unknown || progress.Accepted)
        {
            // A user's terminal discard touches only its own stage, so it never waits on another member's effect.
            await RecoverMembersAsync(operation, progress, ct, terminalOnly: true);
            await ConcludeAsync(Read(operationId), progress, selection, ct);
            return false;
        }
        var after = BeforeFirstOrdinal;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = _context.Store.ReadyFolderEffects(PairId, operationId, after,
                _context.Settings.StoragePageSize).ToArray();
            if (page.Length == 0) break;
            after = page[^1].ItemOrdinal;
            var eligible = await EligibleAsync(operation, page, execution, ct);
            if (eligible.Count == 0) continue;
            if (await SendAndJoinAsync(eligible, execution, ct)) return true;
        }
        if (progress.Ready) progress.Refused(TextKey.FailChanged);
        await CloseWindowAsync(operation, progress, ct);
        await RecoverMembersAsync(operation, progress, ct);
        await ConcludeAsync(Read(operationId), progress, selection, ct);
        return false;
    }

    private IEnumerable<FolderRemovalItem> Members(string operationId, CancellationToken ct)
    {
        var after = BeforeFirstOrdinal;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = _context.Store.FolderRemovalItems(PairId, operationId, after,
                _context.Settings.StoragePageSize).ToArray();
            if (page.Length == 0) yield break;
            after = page[^1].Ordinal;
            foreach (var item in page) yield return item;
        }
    }

    // Reports what prevents completion; inside the footprint no send of this operation is running,
    // so an armed effect has lost its receipt and can only become Unknown.
    private Progress ReadProgress(FolderRemovalState operation, CancellationToken ct)
    {
        var progress = new Progress();
        var armed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in Members(operation.OperationId, ct))
        {
            progress.Observe(item);
            if (item.EffectState == FolderRemovalItem.EffectStatus.Armed) armed.Add(item.SubmissionId!);
        }
        foreach (var id in armed)
        {
            var submission = _context.Store.FolderSubmission(id);
            Advance(submission, submission with
            {
                Generation = checked(submission.Generation + 1),
                Phase = FolderSubmission.SubmissionPhase.Unknown
            }, []);
            progress.LostReceipt();
        }
        return progress;
    }

    // One settlement pass over one frozen operation: verified members are settled before the cursor retires, so a crash re-reads the same unsettled set.
    // Mappings settle only after the post-window feed agrees with the frozen set; the cursor retires last, so a
    // crash re-reads it and a failed or mismatching read leaves every verified member unsettled and visible.
    private async Task CloseWindowAsync(FolderRemovalState operation, Progress progress, CancellationToken ct)
    {
        if (operation.Cursor is { } cursor)
        {
            try
            {
                if (!await WindowCleanAsync(operation, cursor, progress, ct)) return;
            }
            catch (Exception error) when (IsProviderFailure(error))
            {
                progress.Refused(Cause(error, operation.RootEntry.Path, operation.DeletingSide) ?? TextKey.DropboxRefused);
                return;
            }
        }
        // Settlement reports the effect lines the completed log keeps: deepest first, then the frozen key, the
        // same order the submission uses (engine.md 6.x), so the log records the deletion order.
        var settleable = Members(operation.OperationId, ct)
            .Where(item => !item.IsTerminal && (item.EffectState == FolderRemovalItem.EffectStatus.Verified ||
                item is { EffectKind: FolderEffectKind.StateOnly, EffectState: FolderRemovalItem.EffectStatus.Ready }))
            .OrderByDescending(item => PathNames.Depth(item.Path))
            .ThenBy(item => PathNames.Key(item.Path), StringComparer.Ordinal)
            .ToArray();
        var verified = new List<FolderRemovalItem>();
        foreach (var item in settleable)
        {
            if (!await ReadableAsync(operation, item.Path, item.Saved, item.EffectKind, ct))
            {
                progress.SettleRefused();
                continue;
            }
            if (item.EffectState == FolderRemovalItem.EffectStatus.Verified)
            {
                if (IsRecovering(item)) continue;
                // A verified Dropbox effect without its cursor has no post-window evidence, so it cannot settle.
                if (operation.DeletingSide == Side.Dropbox && operation.Cursor is null) progress.SettleRefused();
                else verified.Add(item);
            }
            else if (item is { EffectKind: FolderEffectKind.StateOnly, EffectState: FolderRemovalItem.EffectStatus.Ready })
            {
                if (await SavedOnlyAbsentAsync(operation, item, ct)) verified.Add(item);
                else progress.Refused(TextKey.FailChanged);
            }
        }
        var completed = new HashSet<long>();
        foreach (var submission in verified.GroupBy(item => item.SubmissionId))
        foreach (var batch in submission.Chunk(MaxEffectsPerSubmission))
        {
            ct.ThrowIfCancellationRequested();
            var settled = Settle(operation, batch);
            for (var index = 0; index < batch.Length; index++)
            {
                if (!settled[index]) progress.SettleRefused();
                else
                {
                    progress.Settled();
                    completed.Add(batch[index].Ordinal);
                }
            }
        }
        foreach (var item in verified)
            if (completed.Contains(item.Ordinal) && item.Ordinal != RootOrdinal && item.Expected is not null)
                Report(operation, item.Path);
        if (operation.Cursor is not null && !progress.Unsettled)
            Advance(operation, FolderRemovalPhase.Checking, operation.Cause, null);
    }

    private static bool IsRecovering(FolderRemovalItem item) => item.Stage is not null ||
        item.Phase is not (FolderRemovalItem.RecoveryPhase.Planned or FolderRemovalItem.RecoveryPhase.Deleted or
            FolderRemovalItem.RecoveryPhase.Unknown or FolderRemovalItem.RecoveryPhase.Settled);

    // Compares the whole window, reporting every known path outside the frozen removal. The selected root's own entry
    // is its member row, so it never counts as unexpected, and paths outside selected branches are ignored.
    private async Task<bool> WindowCleanAsync(FolderRemovalState operation, string cursor, Progress progress,
        CancellationToken ct)
    {
        var read = _context.ReadFolderChangesAsync ?? throw new InvalidOperationException();
        var side = operation.DeletingSide;
        var rootKey = PathNames.KeyFor(side, operation.RootEntry.Path);
        var whole = Member(operation, rootKey)?.EffectKind == FolderEffectKind.RecursiveFolder;
        var clean = true;
        await foreach (var listed in read(cursor, ct).WithCancellation(ct))
        {
            var key = PathNames.KeyFor(side, listed.Path);
            if (!PathNames.IsSelfOrUnderKey(key, rootKey)) continue;
            var item = Member(operation, key);
            if (!whole && !InSelectedBranch(operation, item, listed.Path)) continue;
            if (item is not null && (listed.Entry is null || Same(listed.Entry, item.Expected) || IsRecovering(item) ||
                item.IsTerminal)) continue;
            clean &= await SelectRecoveryAsync(operation, item, listed, progress, ct);
        }
        return clean;
    }

    // Only substantiated deletion of the exact observed version selects bytes. A live or moved identity is preserved
    // and never copied back; anything unproved stays an exact-path notice, never a guessed restoration.
    private async Task<bool> SelectRecoveryAsync(FolderRemovalState operation, FolderRemovalItem? item, Listed listed,
        Progress progress, CancellationToken ct)
    {
        TextKey cause = TextKey.FailChanged;
        if (listed.Entry is { IsFolder: false, Skip: null } live)
        {
            var evidence = await _context.ReadDeletionEvidenceAsync(listed.Path, false, ct);
            if (evidence is DeletionEvidence.DeletedFile file && file.RemoteId == live.Id &&
                file.Revision == live.Revision && file.ContentHash == live.ContentHash &&
                (item is not null ? SelectMember(item, live) : Record(operation,
                    FolderRemovalItem.ProvenanceKind.Unexpected, live.Path, live, live)))
                return true;
            if (evidence is DeletionEvidence.Unknown unknown) cause = unknown.Cause;
        }
        if (item is null)
            Record(operation, FolderRemovalItem.ProvenanceKind.Unexpected, listed.Entry?.Path ?? listed.Path,
                listed.Entry, null);
        await NotifyAsync(operation, listed.Path, cause, progress, ct);
        return false;
    }

    private bool SelectMember(FolderRemovalItem item, Entry live)
    {
        AdvanceMember(item, item with { Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, Selected = live });
        return true;
    }

    // Makes a known path durable with its provenance. Storage refuses a conflicting re-observation rather than
    // overwriting progress, so the earlier row stands and the caller's exact-path notice still reports this one.
    private bool Record(FolderRemovalState operation, FolderRemovalItem.ProvenanceKind provenance, string path,
        Entry? observed, Entry? selected)
    {
        try
        {
            _context.Store.InsertFolderMember(PairId, operation.OperationId, Read(operation.OperationId).Generation,
                provenance, path, observed, selected);
            return true;
        }
        catch (LiveAdmissionStaleException)
        {
            return false;
        }
    }

    // A recycled entry outside the frozen inventory is kept only as a durable warning, because nothing restores out of
    // the bin.
    private async Task LateAsync(FolderRemovalState operation, Listed listed, CancellationToken ct)
    {
        Record(operation, FolderRemovalItem.ProvenanceKind.Unexpected, listed.Path, listed.Entry, null);
        await NotifyAsync(operation, listed.Path, TextKey.FailRecycledLate, null, ct, listed.Entry);
    }

    private async Task NotifyAsync(FolderRemovalState operation, string path, TextKey cause, Progress? progress,
        CancellationToken ct, Entry? observed = null)
    {
        await _actions.Failures.FailAsync(Row.ForOneSide(operation.DeletingSide, path, observed, null, [], null,
            operation.RootContext), null, new ItemFailedException(path, cause, null), ct);
        progress?.Noticed(cause);
    }

    private FolderRemovalItem AdvanceMember(FolderRemovalItem item, FolderRemovalItem change)
    {
        var next = change with { Generation = checked(item.Generation + 1) };
        _context.Store.SettleFolderRemovalItem(PairId, item.OperationId, item.Ordinal, item.Generation, next,
            StateChange.Empty);
        return next;
    }

    private async Task RecoverMembersAsync(FolderRemovalState operation, Progress progress, CancellationToken ct,
        bool terminalOnly = false)
    {
        foreach (var item in Members(operation.OperationId, ct))
        {
            if (terminalOnly ? !item.IsTerminalDiscard : !IsRecovering(item)) continue;
            progress.Recovering();
            try
            {
                var current = item;
                while (await StepRecoveryAsync(operation, current, progress, ct) is { } next) current = next;
            }
            catch (Exception error) when (IsProviderFailure(error))
            {
                // The durable phase already says what is known, so the next opportunity resumes from it.
                await NotifyAsync(operation, item.Path, Cause(error, item.Path, operation.DeletingSide) ?? TextKey.FailChanged,
                    progress, ct);
            }
        }
    }

    // One durable recovery transition; null stops this member until the next opportunity or an exact user action.
    private async Task<FolderRemovalItem?> StepRecoveryAsync(FolderRemovalState operation, FolderRemovalItem item,
        Progress progress, CancellationToken ct)
    {
        switch (item.Phase)
        {
            case FolderRemovalItem.RecoveryPhase.RecoverySelected:
                return await StageSelectedAsync(item, ct);
            case FolderRemovalItem.RecoveryPhase.AllocateArmed:
                return await InspectAllocationAsync(operation, item, progress, ct);
            case FolderRemovalItem.RecoveryPhase.StageArmed:
            case FolderRemovalItem.RecoveryPhase.Published:
                return AdvanceMember(item, item with { Phase = FolderRemovalItem.RecoveryPhase.DiscardArmed });
            case FolderRemovalItem.RecoveryPhase.DiscardArmed:
                // An arming row is the only authority to discard; absence after it means the discard already ran.
                await _context.Recovery.DiscardRecoveryStageAsync(item.Stage!, ct);
                return FinishDiscard(item);
            case FolderRemovalItem.RecoveryPhase.Staged:
                return await PublishAsync(operation, item, progress, ct);
            case FolderRemovalItem.RecoveryPhase.PublishArmed:
                // A publication that may have happened is never resent, even if its target is later absent.
                AdvanceMember(item, item with { Phase = FolderRemovalItem.RecoveryPhase.Unknown });
                await NotifyAsync(operation, item.Path, TextKey.FailChanged, progress, ct);
                return null;
            case FolderRemovalItem.RecoveryPhase.Settled:
                return null;
            default:
                await NotifyAsync(operation, item.Path, item.Cause ?? TextKey.FailChanged, progress, ct);
                return null;
        }
    }

    private async Task<FolderRemovalItem?> StageSelectedAsync(FolderRemovalItem item, CancellationToken ct)
    {
        var allocation = _context.Recovery.PlanRecoveryStage(item.OperationId, item.Ordinal);
        // The exact locator is durable before any file exists, so nothing is created without a recorded owner.
        var armed = AdvanceMember(item, item with
        {
            Phase = FolderRemovalItem.RecoveryPhase.AllocateArmed, StageAllocation = allocation
        });
        var selected = armed.Selected!;
        await using var content = await _context.Dropbox.OpenReadAsync(selected,
            new RequestAttempt(_context.Opportunity, AttemptOwner.Recovery), ct);
        await using var stage = await _context.Recovery.StageRecoveryAsync(allocation, selected, content, ct);
        return Reread(armed);
    }

    private FolderRemovalItem Reread(FolderRemovalItem item) => _context.Store.FolderRemovalItems(PairId,
        item.OperationId, checked(item.Ordinal - 1), StateSchema.OneRow).Single();

    private async Task<FolderRemovalItem?> InspectAllocationAsync(FolderRemovalState operation, FolderRemovalItem item,
        Progress progress, CancellationToken ct)
    {
        var occupancy = await _context.Recovery.InspectRecoveryAllocationAsync(item.StageAllocation!, ct);
        if (occupancy == ILocalRecovery.AllocationOccupancy.Absent)
            return AdvanceMember(item, item with
            {
                Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, StageAllocation = null
            });
        // An occupant is never adopted, reallocated around or cleaned; the user handles it.
        await NotifyAsync(operation, item.Path, TextKey.FailChanged, progress, ct);
        return null;
    }

    // A verified publication settles with no mapping change; anything else returns for a fresh known-no-effect attempt.
    private FolderRemovalItem? FinishDiscard(FolderRemovalItem item)
    {
        if (!item.IsTerminalDiscard && item.Result is null)
            return AdvanceMember(item, item with
            {
                Phase = FolderRemovalItem.RecoveryPhase.RecoverySelected, Stage = null, StageAllocation = null
            });
        AdvanceMember(item, item.SettledRecovery());
        return null;
    }

    // One create-only publication: the armed phase, staged upload and verification result are one guarded durable sequence.
    // Create-only publication of the selected bytes: expected null keeps any occupant, and no target is ever removed.
    private async Task<FolderRemovalItem?> PublishAsync(FolderRemovalState operation, FolderRemovalItem item,
        Progress progress, CancellationToken ct)
    {
        var selected = item.Selected!;
        await using var content = await _context.Recovery.OpenRecoveryStageAsync(item.Stage!, ct);
        var parent = PathNames.Parent(item.Path);
        FolderRemovalItem armed;
        IStaged upload;
        try
        {
            if (parent.Length != 0) await _context.Dropbox.EnsureFolderAsync(parent, ct);
            armed = AdvanceMember(item, item with { Phase = FolderRemovalItem.RecoveryPhase.PublishArmed });
        }
        catch (PathOccupiedException occupied)
        {
            var occupant = await _context.Dropbox.ReadOneAsync(occupied.Path, ct);
            Record(operation, FolderRemovalItem.ProvenanceKind.BlockingParent, occupant?.Path ?? occupied.Path,
                occupant, null);
            return await ConflictAsync(operation, item, progress, ct);
        }
        try
        {
            upload = await _context.Dropbox.StageAsync(item.Path, content, selected, ct);
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            // An upload session publishes nothing, so this attempt is known to have had no effect.
            AdvanceMember(armed, armed with { Phase = FolderRemovalItem.RecoveryPhase.Staged });
            throw;
        }
        await using (upload)
        {
            RemovalResult published;
            try
            {
                published = await _context.Dropbox.PublishAsync(upload, null, null, ct);
            }
            catch (PathOccupiedException)
            {
                return await ConflictAsync(operation, armed, progress, ct);
            }
            catch (Exception error) when (IsProviderFailure(error))
            {
                AdvanceMember(armed, armed with { Phase = FolderRemovalItem.RecoveryPhase.Unknown });
                await NotifyAsync(operation, item.Path, TextKey.FailChanged, progress, ct);
                return null;
            }
            if (published is { Outcome: RemovalOutcome.Published, Final: { } final } &&
                final.Size == selected.Size && final.ContentHash == selected.ContentHash)
                return AdvanceMember(armed, armed with { Phase = FolderRemovalItem.RecoveryPhase.Published, Result = final });
            AdvanceMember(armed, armed with { Phase = FolderRemovalItem.RecoveryPhase.Unknown });
            await NotifyAsync(operation, item.Path, published.Cause ?? TextKey.FailChanged, progress, ct);
            return null;
        }
    }

    private async Task<FolderRemovalItem?> ConflictAsync(FolderRemovalState operation, FolderRemovalItem item,
        Progress progress, CancellationToken ct)
    {
        AdvanceMember(item, item with
        {
            Phase = FolderRemovalItem.RecoveryPhase.Conflict, Cause = TextKey.ClashNameTaken
        });
        await NotifyAsync(operation, item.Path, TextKey.ClashNameTaken, progress, ct);
        return null;
    }

    private FolderRemovalItem? Member(FolderRemovalState operation, string key) =>
        _context.Store.FolderRemovalItemByKey(PairId, operation.OperationId, key);

    private bool InSelectedBranch(FolderRemovalState operation, FolderRemovalItem? item, string path) =>
        item?.EffectKind is FolderEffectKind.RecursiveFolder or FolderEffectKind.ConditionalFile ||
        PathNames.Ancestors(path).Any(ancestor => Member(operation,
            PathNames.KeyFor(operation.DeletingSide, ancestor))?.EffectKind == FolderEffectKind.RecursiveFolder);

    private static bool Same(Entry current, Entry? expected) => expected is not null &&
        current.Id == expected.Id && current.IsFolder == expected.IsFolder && current.Revision == expected.Revision &&
        current.SharedRoot == expected.SharedRoot;

    internal static bool IsProviderFailure(Exception error) => error is HttpRequestException or IOException or
        JsonException or ItemFailedException or ChangedUnderneathException or InventoryIncompleteException;

    // A provider failure may arrive wrapped by an inventory read; its advertised cause must survive either shape.
    private static TextKey? Cause(Exception error, string path, Side side) =>
        (error is InventoryIncompleteException incomplete
            ? ItemFailures.InventoryFailure(incomplete, path, side)
            : error as ItemFailedException)?.Cause;

    private async Task<bool> SavedOnlyAbsentAsync(FolderRemovalState operation, FolderRemovalItem item,
        CancellationToken ct)
    {
        var other = PathNames.Other(operation.DeletingSide);
        return await MatchesAsync(operation.DeletingSide, item.Path, null, ct) &&
            await MatchesAsync(other, item.Saved?.PathOn(other) ?? item.Path, null, ct);
    }

    private IReadOnlyList<bool> Settle(FolderRemovalState operation, IReadOnlyList<FolderRemovalItem> items)
    {
        var changes = items.Select(item => (item.Generation, item with
        {
            Generation = checked(item.Generation + 1),
            EffectGeneration = checked(item.EffectGeneration + 1),
            EffectState = FolderRemovalItem.EffectStatus.Settled,
            Phase = FolderRemovalItem.RecoveryPhase.Settled,
            Cause = null
        }, item.Ordinal == RootOrdinal ? StateChange.Empty :
            Forget(operation.DeletingSide, item.Path, item.Saved, item.EffectKind))).ToArray();
        return _context.Store.SettleFolderRemovalItems(PairId, operation.OperationId, changes);
    }

    private async Task<bool> ReadableAsync(FolderRemovalState operation, string path, SyncedEntry? saved,
        FolderEffectKind kind, CancellationToken ct)
    {
        var change = Forget(operation.DeletingSide, path, saved, kind);
        try
        {
            ActionExecutor.RequireReadable(_context.Store, PairId, path,
                kind == FolderEffectKind.ConditionalFile ? change.ForgetSelf : change.ForgetSubtrees,
                kind != FolderEffectKind.ConditionalFile, ct);
            return true;
        }
        catch (ItemFailedException error)
        {
            await _actions.Failures.FailAsync(Row.ForOneSide(operation.DeletingSide, path, null, saved, [], null,
                operation.RootContext), null, error, ct);
            return false;
        }
    }

    private static StateChange Forget(Side side, string path, SyncedEntry? saved, FolderEffectKind kind)
    {
        IReadOnlyList<(Side Side, string Key)> keys = saved is null
            ? [(side, PathNames.KeyFor(side, path))]
            : [(Side.Local, saved.LocalKey), (Side.Dropbox, saved.DropboxKey)];
        IReadOnlyList<(string Path, IReadOnlyList<IssueKind> Kinds)> close = [(path, IssueClosure.AfterEffect)];
        return kind == FolderEffectKind.ConditionalFile
            ? StateChange.Empty with { ForgetSelf = keys, Close = close }
            : StateChange.Empty with { ForgetSubtrees = keys, Close = close };
    }

    private void Report(Side side, string path) =>
        _context.Events(RunEvent.Effect(EffectKinds.Deleted(side), path, string.Empty, side));

    // Dropbox members report at settlement; Local folder effects report after their checked receipt.
    private void Report(FolderRemovalState operation, string path)
    {
        if (operation.DeletingSide == Side.Dropbox) Report(Side.Dropbox, path);
    }

    // One conclusion pass: retirement, advance and completion are mutually exclusive outcomes of the same progress accumulator.
    private async Task ConcludeAsync(FolderRemovalState operation, Progress progress, WorkSelection? selection, CancellationToken ct)
    {
        var root = operation.RootEntry;
        if (progress.Failed && !progress.Reported)
            await _actions.Failures.FailAsync(Row.ForOneSide(operation.DeletingSide,
                PathNames.KeyFor(operation.DeletingSide, root.Path), root, operation.SavedRoot, [], null,
                operation.RootContext), null, new ItemFailedException(root.Path,
                    progress.Cause ?? TextKey.FailChanged, null), ct);
        if (progress.Failed && operation.Cause != (progress.Cause ?? TextKey.FailChanged))
            operation = Advance(operation, operation.Phase, progress.Cause ?? TextKey.FailChanged);
        // Recovery may have added a follow-up after this pass captured its progress.
        var followUp = progress.FollowUp || progress.Unfinished &&
            _context.Store.PendingFolderFollowUps(PairId, operation.OperationId, BeforeFirstOrdinal, StateSchema.OneRow).Any();
        if (progress.Unknown)
            Advance(operation, FolderRemovalPhase.Unknown, progress.Cause);
        else if (progress.Accepted)
            Advance(operation, FolderRemovalPhase.Submitted, operation.Cause);
        else if (progress.ProvedUnsent && !followUp)
            _context.Store.RetireFolderRemoval(PairId, operation.OperationId, operation.Generation,
                FolderRetirementDisposition.NoEffect, StateChange.Empty);
        // A refused no-effect branch invalidates this claim, but durable receipts and custody must first be settled.
        else if (progress.Unfinished && operation.Cursor is null && (progress.Ready || progress.Failed || !followUp))
        {
            var retirement = FolderRetirement.Evaluate(Members(operation.OperationId, ct));
            if (retirement.CanAcknowledge)
                _context.Store.RetireFolderRemoval(PairId, operation.OperationId, operation.Generation,
                    retirement.Disposition, StateChange.Empty);
            else Advance(operation, FolderRemovalPhase.Retained, progress.Cause);
        }
        else if (progress.Unfinished && (!followUp || progress.Failed))
            Advance(operation, FolderRemovalPhase.Retained, progress.Cause);
        else
        {
            var checking = Advance(operation, FolderRemovalPhase.Checking, null);
            // An operation that never sent (a stored terminal submission seen after restart) leaves the root
            // completion to the next admitted pass, after its post-window and follow-up gates.
            var sent = operation.Phase is not (FolderRemovalPhase.Prepared or FolderRemovalPhase.Observed);
            if (sent && !followUp && !progress.Unfinished && checking.Cursor is null)
            {
                if (await ReadableAsync(checking, root.Path, operation.SavedRoot,
                    FolderEffectKind.RecursiveFolder, ct)) Complete(checking, selection);
                else Advance(checking, FolderRemovalPhase.Retained, Text.ReadFailedOn(Side.Local));
            }
        }
    }

    private void Complete(FolderRemovalState operation, WorkSelection? selection)
    {
        var root = operation.RootEntry;
        try
        {
            var change = Forget(operation.DeletingSide, root.Path, operation.SavedRoot, FolderEffectKind.RecursiveFolder);
            // A Repair removal settles X's obligation with its forget; the handoff names only where X was seen (-6 3.5.5).
            if (operation is { Authority: FolderRemovalAuthority.Repair, SavedRoot: { } victim })
            {
                var shared = PathNames.Other(operation.DeletingSide);
                (Side Side, string Path, string Id)? handoff =
                    operation.SourceEvidence is { } seen ? (shared, seen.Path, seen.Id) : null;
                change = change with { SettleRepair = (shared, victim.IdOn(shared), victim, operation.OperationId, handoff) };
            }
            _context.Store.CompleteFolderRemoval(PairId, operation.OperationId, operation.Generation, change, selection);
        }
        catch (FolderStateRefusedException)
        {
            // A rebound or newly saved descendant refuses the forget; the root stays retained, never reported removed.
            Advance(operation, FolderRemovalPhase.Retained, TextKey.FailChanged);
            return;
        }
        Report(operation, root.Path);
    }

    private FolderRemovalState Advance(FolderRemovalState operation, FolderRemovalPhase phase, TextKey? cause) =>
        Advance(operation, phase, cause, operation.Cursor);

    private FolderRemovalState Advance(FolderRemovalState operation, FolderRemovalPhase phase, TextKey? cause,
        string? cursor)
    {
        if (operation.Phase == phase && operation.Cause == cause && operation.Cursor == cursor) return operation;
        var next = operation with
        {
            Generation = checked(operation.Generation + 1), Phase = phase, Cause = cause, Cursor = cursor
        };
        _context.Store.AdvanceFolderRemoval(PairId, operation.OperationId, operation.Generation, operation.Phase, next);
        return next;
    }

    // Fresh checks narrow observed mismatches: a refused effect stays Ready and unsent for honest disposition.
    private async Task<IReadOnlyList<FolderRemovalEffect>> EligibleAsync(FolderRemovalState operation,
        IEnumerable<FolderRemovalEffect> effects, IAdmittedExecution execution, CancellationToken ct)
    {
        var side = operation.DeletingSide;
        var other = PathNames.Other(side);
        var root = operation.RootEntry;
        var eligible = new List<FolderRemovalEffect>();
        if (!await MatchesAsync(side, root.Path, root, ct) ||
            !await MatchesAsync(other, operation.SourceEvidence?.Path ?? operation.SavedRoot?.PathOn(other) ?? root.Path,
                operation.SourceEvidence, ct)) return eligible;
        foreach (var effect in effects)
        {
            if (effect is not { Kind: FolderEffectKind.RecursiveFolder or FolderEffectKind.ConditionalFile,
                Expected: { } expected }) continue;
            execution.Require(PairId, _context.ExpectedGeneration,
                LockKeys.ForPaths(_context.Pair, [(side, expected.Path)]).ToArray());
            if (effect.ItemOrdinal == RootOrdinal || await MatchesAsync(side, expected.Path, expected, ct))
                eligible.Add(effect);
        }
        return eligible;
    }

    // An unreadable answer is not a match, so a failed read never authorizes an effect or a forget.
    private async Task<bool> MatchesAsync(Side side, string path, Entry? expected, CancellationToken ct)
    {
        Entry? current;
        try
        {
            current = await _context.For(side).ReadOneAsync(path, ct);
        }
        catch (Exception error) when (ItemFailures.IsItemError(error))
        {
            return false;
        }
        return expected is null ? current is null : current is not null && Same(current, expected);
    }

    // One send-and-join pass: the recorded cursor, re-read membership and joined jobs must use the same committed ordinals.
    // Membership is read back after arming so every send uses committed wire ordinals, never the caller's list.
    private async Task<bool> SendAndJoinAsync(IReadOnlyList<FolderRemovalEffect> eligible, IAdmittedExecution execution,
        CancellationToken ct)
    {
        var (operations, effects) = await PrepareSendAsync(eligible, ct);
        if (effects.Count == 0) return false;
        // A caller's page may exceed the provider bound (a raised StoragePageSize), so the batch owner splits it.
        var deletingSide = Read(operations[0]).DeletingSide;
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var sent = false;
        foreach (var chunk in effects.Chunk(MaxEffectsPerSubmission))
        {
            // Rechecked under this lease's permit and footprint; a refused operation keeps its Ready effects (B-218).
            var batch = await AuthorizedEffectsAsync(chunk, refused, ct);
            if (batch.Count == 0) continue;
            // A Local effect makes no account call, so its recycle and receipt check hold only the footprint.
            if (deletingSide == Side.Local) await execution.ReleaseAdmissionAsync();
            var batchOperations = batch.Select(effect => effect.OperationId).Distinct(StringComparer.Ordinal).ToArray();
            var submissionId = _context.Store.ArmFolderSubmission(PairId, batch);
            foreach (var id in batchOperations) AdvancePhase(id, FolderRemovalPhase.SubmitArmed);
            var submission = _context.Store.FolderSubmission(submissionId);
            var members = _context.Store.FolderSubmissionEffects(submissionId);
            if (deletingSide == Side.Local) await _local.RemoveAsync(submission, members, ct);
            else await SubmitAsync(submission, members, ct);
            foreach (var id in batchOperations) AdvancePhase(id, FolderRemovalPhase.Submitted);
            sent = true;
            // The next lease re-prepares and re-authorizes the rest under its own permit.
            if (deletingSide == Side.Local) break;
        }
        if (!sent) return false;
        ct.ThrowIfCancellationRequested();
        // Waits hold the footprint but no namespace permit; settlement takes a fresh admission afterwards.
        await execution.ReleaseAdmissionAsync();
        await JoinAsync(operations, ct);
        return true;
    }

    // Candidate preparation: the recorded cursor, unchanged members and readable effects of each operation.
    private async Task<(List<string> Operations, List<FolderRemovalEffect> Effects)> PrepareSendAsync(
        IReadOnlyList<FolderRemovalEffect> eligible, CancellationToken ct)
    {
        var operations = new List<string>();
        var effects = new List<FolderRemovalEffect>(eligible.Count);
        foreach (var group in eligible.GroupBy(effect => effect.OperationId, StringComparer.Ordinal))
        {
            if (!await RecordCursorAsync(Read(group.Key), ct)) continue;
            var unchanged = await UnchangedAsync(Read(group.Key), group.ToArray(), ct);
            if (unchanged.Count == 0) continue;
            var operation = Read(group.Key);
            var readable = new List<FolderRemovalEffect>();
            foreach (var effect in unchanged)
            {
                var item = _context.Store.FolderRemovalItems(PairId, operation.OperationId,
                    checked(effect.ItemOrdinal - 1), StateSchema.OneRow).Single();
                if (await ReadableAsync(operation, item.Path, item.Saved, effect.Kind, ct)) readable.Add(effect);
            }
            if (readable.Count == 0) continue;
            operations.Add(group.Key);
            effects.AddRange(readable);
        }
        return (operations, effects);
    }

    // Each distinct operation's durable authority, once per chunk (2.3): an automatic removal is refused when its saved
    // identity is kept elsewhere; a user's choice is not rechecked. A refusal holds for every later chunk, and an
    // unreadable answer never authorizes.
    private async Task<IReadOnlyList<FolderRemovalEffect>> AuthorizedEffectsAsync(FolderRemovalEffect[] chunk,
        HashSet<string> refused, CancellationToken ct)
    {
        foreach (var id in chunk.Select(effect => effect.OperationId).Distinct(StringComparer.Ordinal))
        {
            if (refused.Contains(id)) continue;
            var operation = Read(id);
            if (operation.Authority == FolderRemovalAuthority.Repair)
            {
                if (!RepairAuthorized(operation)) refused.Add(id);
                continue;
            }
            if (operation.Authority is not (FolderRemovalAuthority.Inferred or FolderRemovalAuthority.Vacate or
                FolderRemovalAuthority.Legacy)) continue;
            try
            {
                if (await FolderIdentity.KeptElsewhereAsync(_context, operation.SavedRoot, operation.DeletingSide, ct))
                    refused.Add(id);
            }
            catch (Exception error) when (ItemFailures.IsItemError(error))
            {
                refused.Add(id);
            }
        }
        return chunk.Where(effect => !refused.Contains(effect.OperationId)).ToArray();
    }

    // The Repair arm (-6 2.3), Storage probes only: the victim is unchanged by key, and V1-V4 apart from this
    // operation's own custody and P2 still hold.
    private bool RepairAuthorized(FolderRemovalState operation)
    {
        if (operation.SavedRoot is not { } victim) return false;
        var shared = PathNames.Other(operation.DeletingSide);
        return _context.Store.SyncedByKey(_context.Pair.PairId, Side.Local, victim.LocalKey) == victim &&
            _context.Store.CheckDuplicateRepair(_context.Pair.PairId, new DuplicateRepairKey(shared, victim.IdOn(shared)),
                victim, operation.OperationId) == DuplicateRepairProof.Valid;
    }

    // A recursive effect deletes whatever is there, so each footprint's complete inventory must equal its frozen members.
    // The cursor is already durable: a change before it refuses here, one after it reaches the post-window feed.
    // A mismatch leaves the effect unsent; the victims are never refreshed.
    private async Task<IReadOnlyList<FolderRemovalEffect>> UnchangedAsync(FolderRemovalState operation,
        IReadOnlyList<FolderRemovalEffect> effects, CancellationToken ct)
    {
        var side = operation.DeletingSide;
        var unmatched = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var effect in effects)
            if (effect is { Kind: FolderEffectKind.RecursiveFolder, Expected: { } expected } &&
                await MatchingInventoryAsync(operation, expected.Path, ct) is { } listed)
                unmatched[PathNames.KeyFor(side, expected.Path)] = listed;
        if (unmatched.Count != 0)
            foreach (var item in Members(operation.OperationId, ct))
                if (item is { Provenance: FolderRemovalItem.ProvenanceKind.Planned, Expected: not null } &&
                    PathNames.Ancestors(item.Path).Select(ancestor => PathNames.KeyFor(side, ancestor))
                        .FirstOrDefault(unmatched.ContainsKey) is { } covering)
                    unmatched[covering]--;
        return effects.Where(effect => effect.Kind != FolderEffectKind.RecursiveFolder ||
            unmatched.TryGetValue(PathNames.KeyFor(side, effect.Expected!.Path), out var left) && left == 0).ToArray();
    }

    // The count of live descendants when each matches its frozen member, or null when any does not or the read failed.
    private async Task<long?> MatchingInventoryAsync(FolderRemovalState operation, string path, CancellationToken ct)
    {
        var side = operation.DeletingSide;
        long count = 0;
        try
        {
            await foreach (var listed in _context.For(side).ReadAllAsync(path,
                new ListOptions(Recursive: true, IncludeOutOfScope: true), ct).WithCancellation(ct))
            {
                if (!IsFrozen(operation, listed)) return null;
                count = checked(count + 1);
            }
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return null;
        }
        return count;
    }

    // A live entry is frozen when it is a planned removed member at the same version.
    private bool IsFrozen(FolderRemovalState operation, Listed listed) =>
        listed.Entry is { } live && Member(operation, PathNames.KeyFor(operation.DeletingSide, listed.Path)) is
            { Provenance: FolderRemovalItem.ProvenanceKind.Planned, Verdict: FolderVerdict.Removed,
              Expected: { } frozen } && SameVersion(live, frozen);

    // Local files keep their ID and revision across an edit, so a file also compares its size and time.
    private static bool SameVersion(Entry current, Entry frozen) => Same(current, frozen) &&
        (current.IsFolder || current.Size == frozen.Size && current.Modified == frozen.Modified);

    private void AdvancePhase(string operationId, FolderRemovalPhase phase)
    {
        var operation = Read(operationId);
        Advance(operation, phase, operation.Cause);
    }

    // The cursor is durable before the first effect, so no window can open without its evidence owner.
    private async Task<bool> RecordCursorAsync(FolderRemovalState operation, CancellationToken ct)
    {
        if (operation.DeletingSide != Side.Dropbox) return true;
        // Both ports are required before any arm: a sent effect must always have an owner able to close its window.
        if (_context.CaptureFolderCursorAsync is not { } capture || _context.ReadFolderChangesAsync is null)
            throw new InvalidOperationException();
        if (operation.Cursor is not null) return true;
        try
        {
            Advance(operation, FolderRemovalPhase.Observed, operation.Cause, await capture(ct));
            return true;
        }
        catch (Exception error) when (IsProviderFailure(error))
        {
            return false;
        }
    }

    // One submission: the request attempt, result classification and exactly one durable phase transition belong together.
    private async Task SubmitAsync(FolderSubmission submission, IReadOnlyList<FolderRemovalEffect> members,
        CancellationToken ct)
    {
        var attempt = new RequestAttempt(_context.Opportunity, AttemptOwner.Recovery);
        var success = false;
        FolderSubmitResult result;
        try
        {
            result = await _context.Dropbox.SubmitFolderRemovalAsync(submission, members, attempt, ct);
            success = result is FolderSubmitResult.Accepted or FolderSubmitResult.Terminal;
        }
        catch (Exception error) when (attempt.Completion is not null)
        {
            var cause = Cause(error, members.Count == 0 ? string.Empty : members[0].Expected?.Path ?? string.Empty,
                Side.Dropbox) ?? TextKey.DropboxRefused;
            result = attempt.Completion.Value.NoEffectProven
                ? new FolderSubmitResult.KnownNoEffect(cause) : new FolderSubmitResult.Unknown(cause);
        }
        finally
        {
            _context.Admission.ActionCompleted(success);
        }
        var next = submission with { Generation = checked(submission.Generation + 1) };
        switch (result)
        {
            case FolderSubmitResult.Accepted accepted:
                Advance(submission, next with { Phase = FolderSubmission.SubmissionPhase.Accepted, JobId = accepted.JobId }, []);
                break;
            case FolderSubmitResult.Terminal terminal:
                Advance(submission, next with { Phase = FolderSubmission.SubmissionPhase.Terminal }, terminal.Results);
                break;
            case FolderSubmitResult.KnownNoEffect none:
                Advance(submission, next with { Phase = FolderSubmission.SubmissionPhase.Terminal, Cause = none.Cause },
                    members.Select(FolderMemberResult (_, ordinal) =>
                        new FolderMemberResult.KnownNoEffect(ordinal, none.Cause)).ToArray());
                break;
            case FolderSubmitResult.Unknown unknown:
                Advance(submission, next with { Phase = FolderSubmission.SubmissionPhase.Unknown, Cause = unknown.Cause }, []);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(result));
        }
    }

    private void Advance(FolderSubmission expected, FolderSubmission next, IReadOnlyList<FolderMemberResult> results) =>
        _context.Store.AdvanceFolderSubmission(expected, next, results);


    // One join per accepted job of these operations; its reads hold no namespace admission.
    private async Task JoinAsync(IReadOnlyList<string> operationIds, CancellationToken ct)
    {
        foreach (var submission in _context.Store.PendingFolderSubmissions(PairId, ct))
        {
            var effects = _context.Store.FolderSubmissionEffects(submission.SubmissionId);
            if (effects.Any(effect => operationIds.Contains(effect.OperationId)))
                await JoinAsync(submission, effects.Count, ct);
        }
    }

    private async Task JoinAsync(FolderSubmission accepted, int count, CancellationToken ct)
    {
        var current = accepted;
        var interval = _context.Settings.FolderDeletePollInterval;
        var episode = new object();
        var wait = true;
        var claimed = false;
        try
        {
            while (current.Phase == FolderSubmission.SubmissionPhase.Accepted)
            {
                if (wait) await Task.Delay(interval, _context.Time, ct);
                if (!claimed)
                {
                    claimed = PairRuntimes.TryClaimFolderJob(current.SubmissionId);
                    current = _context.Store.FolderSubmission(current.SubmissionId);
                    if (!claimed || current.Phase != FolderSubmission.SubmissionPhase.Accepted) continue;
                }
                var step = await ObserveAsync(current, count, episode, true, ct);
                if (!step.Observed && !step.Retry) return;
                // A selected retry already waited, so the cadence is not stacked on top of it.
                wait = step.Observed;
                current = _context.Store.FolderSubmission(current.SubmissionId);
            }
        }
        finally
        {
            if (claimed) PairRuntimes.ReleaseFolderJob(accepted.SubmissionId);
        }
    }

    // One observation: the single call, its store record and the retry and failure fan-out share the same attempt and episode objects.
    // Records one observation; neither flag set ends the join, leaving the accepted job for the next opportunity.
    private async Task<(bool Observed, bool Retry, bool Terminal)> ObserveAsync(FolderSubmission expected, int count, object episode,
        bool joined, CancellationToken ct)
    {
        var attempt = new RequestAttempt(episode, AttemptOwner.Recovery);
        FolderJobResult? observation = null;
        TextKey? cause = null;
        try
        {
            observation = await _context.Dropbox.ReadFolderRemovalAsync(expected.JobId!, count, attempt, ct);
        }
        catch (ItemFailedException error)
        {
            cause = error.Cause;
        }
        catch (Exception) when ((!joined || attempt.Completion is not null) && !ct.IsCancellationRequested)
        {
            cause = TextKey.DropboxRefused;
        }
        try
        {
            _context.Store.RecordFolderJobObservation(expected, observation, cause);
        }
        catch (FolderStateRefusedException)
        {
            return (true, false, false);
        }
        var retry = cause != TextKey.FailDropboxDeleteJobInvalid &&
            _context.CompleteRecoveryAttemptAsync is { } complete && await complete(attempt, ct);
        if (cause is not null && cause != TextKey.FailDropboxDeleteJobInvalid && !retry)
        {
            foreach (var operationId in _context.Store.FolderSubmissionEffects(expected.SubmissionId)
                .Select(effect => effect.OperationId).Distinct(StringComparer.Ordinal))
            {
                var operation = Read(operationId);
                await _actions.Failures.FailAsync(Row.ForOneSide(operation.DeletingSide, operation.RootEntry.Path,
                    operation.RootEntry, operation.SavedRoot, [], null, operation.RootContext), null,
                    new ItemFailedException(operation.RootEntry.Path, cause.Value, null), ct);
            }
        }
        return (observation is not null, retry, observation is FolderJobResult.Terminal);
    }

    private sealed class Progress
    {
        private bool _sent;
        private bool _settled;
        private bool _armed;
        private bool _savedOnly;

        internal bool Unknown { get; private set; }
        internal bool Ready { get; private set; }
        internal bool Unsettled { get; private set; }
        internal bool Reported { get; private set; }
        internal bool Accepted { get; private set; }
        internal bool Custody { get; private set; }
        internal bool FollowUp { get; private set; }
        internal bool Failed { get; private set; }
        internal bool Unfinished { get; private set; }
        internal TextKey? Cause { get; private set; }

        internal bool ProvedUnsent => Unfinished && !_sent && !_settled && !Custody;

        internal bool Blocked => Unknown || Accepted || Custody || _armed || _savedOnly;

        internal void Observe(FolderRemovalItem item)
        {
            FollowUp |= item.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending;
            // A recovered survivor means the removal is not whole, even after its custody settled.
            Unfinished |= item.Verdict == FolderVerdict.Survives || item.Result is not null;
            Custody |= item.StageAllocation is not null || IsRecovering(item);
            switch (item.EffectState)
            {
                case FolderRemovalItem.EffectStatus.Armed:
                    _sent = true;
                    _armed = true;
                    break;
                case FolderRemovalItem.EffectStatus.Accepted:
                    _sent = true;
                    Accepted = true;
                    break;
                case FolderRemovalItem.EffectStatus.Unknown:
                    LostReceipt();
                    Cause ??= item.Cause;
                    break;
                case FolderRemovalItem.EffectStatus.NoEffect:
                    Refused(item.Cause);
                    break;
                case FolderRemovalItem.EffectStatus.Ready when item.IsTerminal:
                    Unfinished = true;
                    break;
                case FolderRemovalItem.EffectStatus.Ready when item.EffectKind != FolderEffectKind.StateOnly:
                    Ready = true;
                    Unfinished = true;
                    break;
                case FolderRemovalItem.EffectStatus.Ready:
                    _savedOnly = true;
                    break;
                case FolderRemovalItem.EffectStatus.Verified:
                case FolderRemovalItem.EffectStatus.Settled:
                    _sent = true;
                    break;
            }
        }

        internal void LostReceipt()
        {
            _sent = true;
            Unknown = true;
            Failed = true;
        }

        internal void Refused(TextKey? cause)
        {
            Failed = true;
            Unfinished = true;
            Cause ??= cause;
        }

        internal void Noticed(TextKey cause)
        {
            Reported = true;
            Refused(cause);
        }

        internal void Recovering() => Unfinished = true;

        internal void SettleRefused()
        {
            Unsettled = true;
            Refused(TextKey.FailChanged);
        }

        // A settled saved-only member needs no provider effect, yet it changed state and forbids NoEffect retirement.
        internal void Settled() => _settled = true;
    }
}
