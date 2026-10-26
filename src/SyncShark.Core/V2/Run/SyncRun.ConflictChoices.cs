using System.Text;

namespace Massdex.SyncShark.V2;

public sealed partial class SyncRun
{
    private const int CompareLimit = 8 * 1024 * 1024;

    public Task<NoticeComparison> CompareNoticeAsync(string pairId, string noticeId, CancellationToken ct) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            var (pair, notice) = ConflictNotice(pairId, noticeId);
            await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
            return await ReadComparisonAsync(pair, notice, token).ConfigureAwait(false);
        }, ct, admissionPairId: pairId);

    private (PairDef Pair, Notice Notice) ConflictNotice(string pairId, string noticeId)
    {
        var saved = _store.NoticeById(pairId, noticeId);
        if (saved is null || saved.Value.Closed || saved.Value.Notice.ConflictVersions is null)
            throw new SyncRunRefusedException("This notice is no longer open. Refresh notices; no files changed.");
        var pair = _store.Pair(pairId) ?? throw new SyncRunRefusedException("The pair is unavailable; no files changed.");
        RefuseLayout(pair);
        return (pair, saved.Value.Notice);
    }

    private async Task<NoticeComparison> ReadComparisonAsync(PairDef pair, Notice notice, CancellationToken ct)
    {
        var versions = notice.ConflictVersions!;
        if (versions.A.Kind != ItemKind.File || versions.B.Kind != ItemKind.File)
            throw new SyncRunRefusedException("These are folders. Open both locations to compare their contents; no files changed.");
        var a = _endpoints.Writer(pair.A);
        var b = _endpoints.Writer(pair.B);
        if (versions.A.Size > CompareLimit || versions.B.Size > CompareLimit)
            return await ExportComparisonAsync(pair, versions, ct).ConfigureAwait(false);
        var reads = await Task.WhenAll(ReadCurrentAsync(a, pair.A, versions.A, ct),
            ReadCurrentAsync(b, pair.B, versions.B, ct)).ConfigureAwait(false);
        var textA = ConflictText.Decode(reads[0]);
        var textB = ConflictText.Decode(reads[1]);
        if (textA is null || textB is null)
        {
            var exported = await ExportComparisonAsync(pair, versions, ct).ConfigureAwait(false);
            return exported with { A = reads[0], B = reads[1] };
        }
        string? basis = null;
        if (versions.Base is { } baseline)
        {
            foreach (var (writer, state) in new[] { (b, baseline.B), (a, baseline.A) })
            {
                if (writer is not IRetainedNoticeReader historical || string.IsNullOrEmpty(state.VersionId)) continue;
                try
                {
                    await using var stream = await historical.OpenRetainedAsync(state, ct).ConfigureAwait(false);
                    basis = ConflictText.Decode(await ReadVerifiedAsync(stream, state, ct).ConfigureAwait(false));
                    if (basis is not null) break;
                }
                catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or SyncRunRefusedException) { /* Try the other baseline revision. */ }
            }
            if (basis is null)
                foreach (var (side, root, writer, state) in new[] { (Side.A, pair.A, a, baseline.A), (Side.B, pair.B, b, baseline.B) })
                {
                    var recovery = _store.RecoveryForVersion(pair.PairId, side, state);
                    if (recovery is null || recovery.Expires <= _time.GetUtcNow()) continue;
                    try
                    {
                        await using var stream = await OpenRetainedNoticeBytesAsync(root, writer, recovery, state, ct).ConfigureAwait(false);
                        basis = ConflictText.Decode(await ReadVerifiedAsync(stream, state, ct).ConfigureAwait(false));
                        if (basis is not null) break;
                    }
                    catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or SyncRunRefusedException) { /* Keep originals when an archive is unavailable. */ }
                }
        }
        var merged = basis is null ? null : ConflictText.Merge(basis, textA, textB);
        return new(reads[0], reads[1], basis, merged, basis is null
            ? "The shared baseline revision is unavailable. Compare both versions; originals remain unchanged."
            : merged is null ? "Overlapping edits need manual resolution. Both originals remain unchanged."
            : "Clean three-way merge available. Saving it creates a new file and keeps every original.");
    }

    private async Task<NoticeComparison> ExportComparisonAsync(PairDef pair, ConflictNoticeVersions versions, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetTempPath(), "SyncShark-compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var available = new DriveInfo(Path.GetPathRoot(directory)!).AvailableFreeSpace;
        if (versions.A.Size < 0 || versions.B.Size < 0 || versions.A.Size > available ||
            versions.B.Size > available - versions.A.Size)
            throw new SyncRunRefusedException("There is not enough temporary disk space for both verified snapshots. Free space or use the original locations; originals were not modified.");
        async Task<string> Export(Side side, RootDef root, ItemState expected)
        {
            var writer = _endpoints.Writer(root);
            var current = await writer.ObserveAsync(expected.Path, ct).ConfigureAwait(false);
            if (current is null || !SameRetainedWinner(current, expected, root))
                throw new SyncRunRefusedException("A comparison version changed. Originals were not modified.");
            var extension = Path.GetExtension(expected.Path);
            if (extension.Length > 20 || extension.Any(ch => ch != '.' && !char.IsAsciiLetterOrDigit(ch))) extension = ".bin";
            var path = Path.Combine(directory, side + extension);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await using var input = await writer.OpenReadAsync(expected, ct).ConfigureAwait(false);
                var block = new byte[64 * 1024];
                int count;
                while ((count = await input.ReadAsync(block, ct).ConfigureAwait(false)) != 0)
                {
                    if (output.Length + count > expected.Size)
                        throw new SyncRunRefusedException("Comparison snapshot grew during reading; originals remain unchanged.");
                    await output.WriteAsync(block.AsMemory(0, count), ct).ConfigureAwait(false);
                }
                output.Position = 0;
                var hash = await DropboxContentHash.ComputeAsync(output, ct).ConfigureAwait(false);
                if (Content.SameContent(output.Length, hash, expected.Size, expected.Hash) != true)
                    throw new SyncRunRefusedException("Comparison snapshot verification failed; originals remain unchanged.");
            }
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return path;
        }
        var paths = await Task.WhenAll(Export(Side.A, pair.A, versions.A), Export(Side.B, pair.B, versions.B)).ConfigureAwait(false);
        return new([], [], null, null,
            "Verified read-only snapshots are available for external comparison. Originals remain unchanged; inline merge requires UTF-8 text within 8 MiB. Snapshots are retained for you to remove when finished: " + directory,
            paths[0], paths[1]);
    }

    private static async Task<byte[]> ReadCurrentAsync(IEndpointWriter writer, RootDef root, ItemState expected, CancellationToken ct)
    {
        var observed = await writer.ObserveAsync(expected.Path, ct).ConfigureAwait(false);
        if (observed is null || !SameRetainedWinner(observed, expected, root))
            throw new SyncRunRefusedException("A version changed since this notice. Refresh or open the current files; nothing was changed.");
        await using var stream = await writer.OpenReadAsync(expected, ct).ConfigureAwait(false);
        return await ReadVerifiedAsync(stream, expected, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadVerifiedAsync(Stream stream, ItemState expected, CancellationToken ct)
    {
        if (expected.Size > CompareLimit)
            throw new SyncRunRefusedException("This file exceeds the inline comparison limit. Open both files in an external comparison tool; nothing was changed.");
        using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        int count;
        while ((count = await stream.ReadAsync(block, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > CompareLimit) throw new SyncRunRefusedException("The file grew; open it externally instead.");
            buffer.Write(block, 0, count);
        }
        var bytes = buffer.ToArray();
        if (Content.SameContent(bytes.LongLength, DropboxContentHash.Compute(bytes), expected.Size, expected.Hash) != true)
            throw new SyncRunRefusedException("The version failed verification. Nothing was changed.");
        return bytes;
    }

    /// <summary>Explicit file choices only; every removed or replaced file goes through the recovery-first executor.</summary>
    public Task<ActionResult> ResolveConflictNoticeAsync(string pairId, string noticeId, string choice,
        string? destination, CancellationToken ct) => RunExclusiveAsync(null, async (_, token) =>
    {
        var id = ShortId.Make("nch", pairId, noticeId);
        ActionResult Result(Outcome outcome, string reason) => new(id, outcome, null, null, null, reason);
        var (pair, notice) = ConflictNotice(pairId, noticeId);
        if (!NoticeChoices.IsAdmissible(notice, choice))
            return Result(Outcome.Skipped, "Choose an action offered by this notice. No files changed.");
        if (notice.ConflictOperation is { } checkpoint)
        {
            // A finished replace waits for the incoming item; it can be restored or closed, never re-applied.
            if (NoticeChoices.AwaitsArrival(notice))
            {
                if (choice is NoticeChoices.RestoreExisting or NoticeChoices.RestoreAsCopy)
                {
                    await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
                    var restored = await RestoreReplacedAsync(pair, notice, choice == NoticeChoices.RestoreAsCopy, token).ConfigureAwait(false);
                    if (restored.Outcome != Outcome.Done && restored.Reason is { } reason)
                        _store.RecordNoticeChoiceFailure(notice, reason);
                    return restored;
                }
                AbandonRestoreIntents(pair, checkpoint);
                CloseConflictNotice(pair.PairId, noticeId, checkpoint, choice);
                return Result(Outcome.Done, checkpoint.Restoring
                    ? "Closed. Already restored items remain in place; all other removed files remain in Recovery."
                    : "Closed. The replaced item stays in Recovery; the incoming item arrives with the next sync.");
            }
            // Keep both/dismiss stop an unfinished choice without further effects. Completed steps are NOT undone.
            if (choice is NoticeChoices.KeepBoth or NoticeChoices.Dismiss && checkpoint.Choice != choice)
            {
                if (ConflictChoicePending(pair, checkpoint, token))
                    return Result(Outcome.Skipped, "An earlier write is still unverified. Retry the saved choice to reconcile it before stopping; no checkpoint was discarded.");
                var removed = checkpoint.Actions.Where(FolderDelete.Expands)
                    .Sum(action => _store.CountChoiceVictims(action.ActionId, ChoiceVictimState.Removed));
                CloseConflictNotice(pair.PairId, noticeId, checkpoint, choice);
                return Result(Outcome.Done,
                    $"Stopped the earlier choice. {checkpoint.Completed.Count} of {checkpoint.Actions.Count} steps" +
                    (removed > 0 ? $" and {removed} item(s) inside removed folders" : "") +
                    " had already completed and stay removed or replaced; nothing was put back. " +
                    "Their previous versions are in Recovery (or Dropbox version history) if you want to restore them.");
            }
            if (checkpoint.Choice != choice || checkpoint.Destination != destination)
                return Result(Outcome.Skipped, "A previous choice is partly complete. Retry that same choice to reconcile it before selecting another.");
            await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
            return await ApplyConflictOperationAsync(pair, noticeId, checkpoint, token).ConfigureAwait(false);
        }
        if (choice is NoticeChoices.KeepBoth or NoticeChoices.Dismiss)
        {
            _store.CloseNotice(noticeId, choice);
            return Result(Outcome.Done, "Both items remain unchanged.");
        }
        if (!pair.Enabled) return Result(Outcome.Skipped, "Enable this pair to apply the choice, or dismiss to leave both items unchanged.");
        await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
        var versions = notice.ConflictVersions!;
        if (versions.PendingOccupantSource is { } pendingSource && versions.Incoming is { } pendingIncoming)
        {
            var sourceSide = pendingIncoming.Target == Side.A ? Side.B : Side.A;
            var sourceRoot = sourceSide == Side.A ? pair.A : pair.B;
            var link = _store.BaselinesTouching(pairId, sourceSide, [pendingSource.ObjectId], [])
                .SingleOrDefault(basis => SameConflictItem(sourceSide == Side.A ? basis.A : basis.B, pendingSource, sourceRoot) &&
                    PairPath.Same((pendingIncoming.Target == Side.A ? basis.A : basis.B).Path, pendingIncoming.Path));
            if (link is null) return Result(Outcome.Skipped, "The first incoming item has not completed at the destination. Retry after sync, or dismiss; no files changed.");
            versions = pendingIncoming.Target == Side.A ? versions with { A = link.A } : versions with { B = link.B };
        }
        var a = _endpoints.Writer(pair.A);
        var b = _endpoints.Writer(pair.B);
        if (await CheckConflictOriginalsAsync(pair, notice, versions, noticeId, choice, token).ConfigureAwait(false) is { } checkedResult)
            return checkedResult;
        var actions = new List<SyncAction>();
        var guards = ActionPredicates(pair, LeftAlone(pair));
        SyncAction Action(ActionKind kind, Side side, string path, ItemState? source, ItemState? victim) =>
            new(ShortId.Make("nch", pairId, noticeId, side.ToString(), path, kind.ToString()), notice.Rule,
                kind, side, path, null, source, victim, victim?.Kind == ItemKind.File, null)
            { SuppressReconciledBaseline = true, CallerOwnsNotice = true };
        if (choice == NoticeChoices.Merge)
        {
            var compare = await ReadComparisonAsync(pair, notice, token).ConfigureAwait(false);
            if (compare.MergedText is null) return Result(Outcome.Skipped, compare.Note);
            if (string.IsNullOrWhiteSpace(destination))
                return Result(Outcome.Skipped, "Choose an unused relative path for the clean merged file. Both originals will be kept.");
            ValidateChoicePath(destination);
            if (await a.ObserveAsync(destination, token) is not null)
                return Result(Outcome.Skipped, "That merge destination exists. Choose another name; nothing was changed.");
            var bytes = Encoding.UTF8.GetBytes(compare.MergedText);
            var source = versions.A with { Size = bytes.LongLength, Hash = DropboxContentHash.Compute(bytes), VersionId = null };
            var action = Action(ActionKind.CopyNew, Side.A, destination, source, null);
            if (!Allowed(guards, action)) return Result(Outcome.Skipped, "The merge destination is excluded or protected. Choose an included path.");
            if (bytes.Length > CompareLimit)
                return Result(Outcome.Skipped, "The merged output exceeds 8 MiB. Save it with an external editor; originals remain unchanged.");
            var merge = new ConflictNoticeOperation(choice, destination, [action], []) { MergeBytes = bytes };
            _store.SaveConflictOperation(pairId, noticeId, merge);
            return await ApplyConflictOperationAsync(pair, noticeId, merge, token).ConfigureAwait(false);
        }
        // Folder removals reuse the executor's frozen, recovery-first descendant expansion (decisions 71/73);
        // only rule 27's content versions are file-only.
        if (notice.Rule == 27 && (versions.A.Kind == ItemKind.Folder || versions.B.Kind == ItemKind.Folder))
            return Result(Outcome.Skipped, "Folders have no content versions to keep. Keep both or dismiss; nothing was changed.");
        if (versions.Incoming is { } incoming)
        {
            var source = incoming.Target == Side.A ? versions.B : versions.A;
            var victim = incoming.Target == Side.A ? versions.A : versions.B;
            if (choice == NoticeChoices.RenameIncoming)
            {
                if (string.IsNullOrWhiteSpace(destination)) return Result(Outcome.Skipped, "Provide an unused relative destination name; no files changed.");
                ValidateChoicePath(destination);
                var sourceSide = incoming.Target == Side.A ? Side.B : Side.A;
                var writer = sourceSide == Side.A ? a : b;
                if (await writer.ObserveAsync(destination, token) is not null)
                    return Result(Outcome.Skipped, "That name is already occupied. Choose another name; no files changed.");
                if (PairPath.IsSelfOrUnder(destination, source.Path))
                    return Result(Outcome.Skipped, "Choose a name outside the incoming item's own subtree.");
                actions.Add(Action(ActionKind.Move, sourceSide, destination, null, source) with
                    { FromPath = source.Path, RecoveryFirst = false });
            }
            else if (choice == NoticeChoices.ReplaceExisting)
            {
                // The occupant's own sync link must never tie it to the incoming entity, or its removal could
                // later be read as a deletion of the incoming item.
                if (_store.BaselinesTouching(pairId, incoming.Target, [victim.ObjectId], []).Any(basis =>
                        (incoming.Target == Side.A ? basis.B : basis.A).ObjectId == source.ObjectId))
                    return Result(Outcome.Skipped, "These are linked versions of one item, not a name clash. Refresh notices; nothing was changed.");
                // Two files: one conditional recovery-first update. Any folder: remove the occupant (each file
                // preserved first); the held incoming item then arrives on the next sync into the free path.
                actions.Add(source.Kind == ItemKind.File && victim.Kind == ItemKind.File
                    ? Action(ActionKind.Update, incoming.Target, victim.Path, source, victim)
                    : Action(ActionKind.Delete, incoming.Target, victim.Path, null, victim));
            }
        }
        else if (choice is NoticeChoices.KeepA or NoticeChoices.KeepB)
        {
            var keepA = choice == NoticeChoices.KeepA;
            if (notice.Rule == 27)
                actions.Add(Action(ActionKind.Update, keepA ? Side.B : Side.A,
                    keepA ? versions.B.Path : versions.A.Path, keepA ? versions.A : versions.B,
                    keepA ? versions.B : versions.A));
            else
                actions.Add(Action(ActionKind.Delete, keepA ? Side.B : Side.A,
                    keepA ? versions.B.Path : versions.A.Path, null, keepA ? versions.B : versions.A));
            // A labelled/two-location copy is only removed when its durable identity link proves it
            // was produced from this exact original, never merely because its filename looks right.
            foreach (var (side, path, original, originalSide) in new[] {
                (Side.A, versions.CopyOnA, versions.B, Side.B),
                (Side.B, versions.CopyOnB, versions.A, Side.A) })
            {
                if (notice.Rule != 27 && (keepA ? side == Side.B : side == Side.A)) continue;
                if (path is null) return Result(Outcome.Skipped, "The copy provenance is missing; keep both or dismiss instead.");
                var link = _store.BaselinesAt(pairId, side, [path]).SingleOrDefault(basis =>
                    SameConflictItem(originalSide == Side.A ? basis.A : basis.B, original, originalSide == Side.A ? pair.A : pair.B));
                if (link is null) return Result(Outcome.Skipped, "A copy's identity link changed. Keep both or refresh before choosing again.");
                if (notice.Rule == 27)
                    actions[0] = BindConflictCorrespondence(actions[0], link.ObjectKey, originalSide == (keepA ? Side.A : Side.B));
                var saved = side == Side.A ? link.A : link.B;
                var (current, copyFailure) = await CheckConflictCopyAsync(pair, notice, noticeId, side, path, saved, token).ConfigureAwait(false);
                if (copyFailure is not null) return copyFailure;
                actions.Add(Action(ActionKind.Delete, side, path, null, current));
            }
        }
        if (actions.Count == 0) return Result(Outcome.Skipped, "No file action selected. Compare or keep both instead.");
        if (actions.Any(action => !Allowed(guards, action)))
            return Result(Outcome.Skipped, "A selected path is excluded or protected. Include it or keep both; no files changed.");
        var operation = new ConflictNoticeOperation(choice, destination, actions, []);
        _store.SaveConflictOperation(pairId, noticeId, operation);
        return await ApplyConflictOperationAsync(pair, noticeId, operation, token).ConfigureAwait(false);
    }, ct, admissionPairId: pairId);

    private async Task<ActionResult> ApplyConflictOperationAsync(PairDef pair, string noticeId,
        ConflictNoticeOperation operation, CancellationToken ct)
    {
        ActionResult Result(Outcome outcome, string reason) =>
            new(ShortId.Make("nch", pair.PairId, noticeId), outcome, null, null, null, reason);
        if (!pair.Enabled) return Result(Outcome.Skipped, "Enable the pair to finish this choice; no further files changed.");
        var a = _endpoints.Writer(pair.A);
        var b = _endpoints.Writer(pair.B);
        var gen = _store.BeginGeneration(pair.A.RootId, RunKind);
        var completed = operation.Completed.ToHashSet(StringComparer.Ordinal);
        var remaining = operation.ReadyActions();
        var guards = ActionPredicates(pair, LeftAlone(pair));
        foreach (var folder in remaining.Where(FolderDelete.Expands))
            RevokeProtectedCarriedChoice(folder, guards with { IgnoreRules = null });
        if (remaining.Any(action => !Allowed(guards, action)))
            return Result(Outcome.Skipped, "A remaining path is protected. Restore its inclusion to finish this saved choice.");
        // Observe all independent effects concurrently, including the commit/checkpoint crash window.
        var resolved = await Task.WhenAll(remaining.Select(async action =>
        {
            var intent = _store.IntentById(pair.PairId, action.ActionId) ?? action;
            var result = await EffectResolver.ResolveAsync(intent, intent.Target == Side.A ? a : b,
                intent.Target == Side.A ? b : a, ct, isStateDamage: StorageFailureClassifier.IsStateDamage,
                preserved: RecoverySafety.PreservedReceipt(_store, pair.PairId, intent)).ConfigureAwait(false);
            return (Action: intent, Result: result);
        })).ConfigureAwait(false);
        var retry = new List<SyncAction>();
        foreach (var item in resolved)
        {
            if (item.Result.Outcome == Outcome.Done)
            {
                _store.CommitResult(pair.PairId, gen, item.Action, item.Result);
                completed.Add(item.Action.ActionId);
            }
            else if (item.Result.Outcome == Outcome.Skipped && item.Result.Reason == EffectResolver.NotApplied)
            {
                // The resolver compares folders by kind only; a resumed removal must still target the same folder.
                if (item.Action.ExpectedTarget is { Kind: ItemKind.Folder } folder &&
                    await (item.Action.Target == Side.A ? a : b).ObserveAsync(folder.Path, ct).ConfigureAwait(false) is { } now &&
                    !string.Equals(now.ObjectId, folder.ObjectId, StringComparison.Ordinal))
                    return Result(Outcome.Skipped, "A different folder now occupies " + folder.Path + ". Nothing further was removed; earlier removals remain recoverable.");
                retry.Add(item.Action with { ProviderEffect = null, CallerOwnsNotice = true });
            }
            else return RecordConflictChoiceFailure(pair.PairId, noticeId, item.Result);
        }
        void Save()
        {
            if (operation.Choice == NoticeChoices.ReplaceExisting && operation.Actions.Any(action => action.Kind == ActionKind.Delete) &&
                operation.Actions.All(action => completed.Contains(action.ActionId)) &&
                !completed.Any(entry => entry.StartsWith("replaced-at-gen:", StringComparison.Ordinal)))
                completed.Add(ReplacedAtMarker(gen));
            _store.SaveConflictOperation(pair.PairId, noticeId, operation with { Completed = completed.ToArray() });
        }
        Save();
        // A folder's contents leave first, from the snapshot frozen when the choice was made, in bounded pages; the
        // folder itself is then removed only if it is empty. Nothing added or changed since the freeze is removed.
        for (var index = 0; index < retry.Count; index++)
        {
            if (!operation.Actions.Any(original => original.ActionId == retry[index].ActionId && FolderDelete.Expands(original))) continue;
            if (await RemoveFrozenContentsAsync(pair, retry[index], gen, a, b, ct).ConfigureAwait(false) is { } stopped)
                return RecordConflictChoiceFailure(pair.PairId, noticeId, stopped);
            retry[index] = retry[index] with { DescendantsPlanned = true, ContentsSettled = true, FrozenChoiceId = retry[index].ActionId };
        }
        ActionResult? failure = null;
        var owned = operation.Actions.Select(action => action.ActionId).ToHashSet(StringComparer.Ordinal);
        var merged = operation.MergeBytes ?? (operation.MergeContentId is { } contentId ? _store.ConflictMergeBytes(contentId) : null);
        using var mergedStream = merged is not null ? new MemoryStream(merged, false) : null;
        if (operation.Choice == NoticeChoices.Merge && (merged is not { Length: <= CompareLimit } bytes ||
            operation.Actions.Count != 1 || operation.Actions[0].Source is not { } mergeSource ||
            Content.SameContent(mergeSource.Size, mergeSource.Hash, bytes.LongLength, DropboxContentHash.Compute(bytes)) != true))
            return Result(Outcome.Skipped, "The saved merge checkpoint failed verification; originals remain unchanged.");
        var sourceWriter = mergedStream is null ? b : new RetainedBytesWriter(operation.Actions[0].Source!, mergedStream);
        await foreach (var result in ApplyAdmittedAsync(pair, new Plan(pair.PairId, gen, retry, []), a, sourceWriter, ct))
        {
            if (result.Outcome != Outcome.Done) failure ??= result;
            else if (owned.Contains(result.ActionId)) { completed.Add(result.ActionId); Save(); }
        }
        if (failure is not null)
            return RecordConflictChoiceFailure(pair.PairId, noticeId, failure);
        if (!owned.All(completed.Contains) && remaining.All(action => completed.Contains(action.ActionId)))
            return await ApplyConflictOperationAsync(pair, noticeId,
                operation with { Completed = completed.ToArray() }, ct).ConfigureAwait(false);
        if (!owned.All(completed.Contains))
            return Result(Outcome.Skipped, "The saved choice is unfinished. Retry the same choice to resume safely.");
        // Decision 88 / owner: a removed occupant is only "replaced" once the incoming item has arrived. Until then
        // the notice stays open (no longer holding the path) and offers to restore the removed item.
        if (operation.Choice == NoticeChoices.ReplaceExisting && operation.Actions.Any(action => action.Kind == ActionKind.Delete))
        {
            // Only a sync link written after this generation can prove the incoming item's arrival.
            Save();
            return Result(Outcome.Done, "The existing item was removed after its content was preserved. The incoming item " +
                "arrives with the next sync; this notice stays open until it does and offers to restore the removed item.");
        }
        CloseConflictNotice(pair.PairId, noticeId, operation, operation.Choice);
        return Result(Outcome.Done, "Choice completed. Removed and replaced files remain recoverable.");
    }

    private static bool SameConflictItem(ItemState current, ItemState saved, RootDef root) =>
        PairPath.Same(current.Path, saved.Path) && (saved.Kind == ItemKind.File
            ? SameRetainedWinner(current, saved, root)
            : current.Kind == saved.Kind && current.ObjectId == saved.ObjectId && current.VersionId == saved.VersionId);

    private static void ValidateChoicePath(string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(part => part is "" or "." or ".."))
            throw new SyncRunRefusedException("Choose a relative path inside the pair, without parent segments.");
    }
}
