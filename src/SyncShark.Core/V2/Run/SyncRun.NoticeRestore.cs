namespace Massdex.SyncShark.V2;

/// <summary>Reads an immutable retained provider revision, not the current path's contents.</summary>
internal interface IRetainedNoticeReader
{
    Task<Stream> OpenRetainedAsync(ItemState retained, CancellationToken ct);
}

internal interface ILocalRetainedNoticeReader
{
    Task<Stream> OpenRecoveryAsync(RecoveryRecord recovery, ItemState retained, CancellationToken ct);
}

public sealed partial class SyncRun
{
    /// <summary>Restores only the exact retained file named by a notice, conditionally in place or as a new copy.</summary>
    public Task<ActionResult> RestoreNoticeVersionAsync(string pairId, string noticeId, bool asCopy, CancellationToken ct) =>
        RunExclusiveAsync(null, async (_, token) =>
        {
            var actionId = NoticeRestoreActionId(pairId, noticeId);
            ActionResult Skipped(string reason) => new(actionId, Outcome.Skipped, null, null, null, reason);
            var saved = _store.NoticeById(pairId, noticeId);
            if (saved is null) return Skipped("That notice is unavailable; nothing was changed.");
            if (saved.Value.Closed) return new ActionResult(actionId, Outcome.Done, null, null, null,
                "That notice is already closed; nothing was changed.");
            var notice = saved.Value.Notice;
            if (notice.RetainedVersion is not { } retained || retained.Loser.Kind != ItemKind.File ||
                retained.Winner.Kind != ItemKind.File)
                return Skipped("This notice has no retained file version. Folder trees are not restored automatically.");
            var pair = _store.Pair(pairId);
            if (pair is null || !pair.Enabled) return Skipped("Enable the folder pair before restoring this version.");
            RefuseLayout(pair);
            await _endpoints.PrepareAsync(pair, token).ConfigureAwait(false);
            var targetRoot = retained.Side == Side.A ? pair.A : pair.B;
            var target = _endpoints.Writer(targetRoot);
            var a = _endpoints.Writer(pair.A);
            var b = _endpoints.Writer(pair.B);
            // One durable operation identity per notice, across both choices. A retry observes the old effect
            // instead of creating another copy or switching choices while an upload may still complete.
            if (_store.IntentById(pairId, actionId) is { } pending)
            {
                if (pending.ResolvedNoticeId != noticeId)
                    return Skipped("The retained-version operation identity is occupied; nothing was changed.");
                var results = await _executor.ReconcileAsync([pending], _store.BeginGeneration(targetRoot.RootId, RunKind),
                    a, b, token).ConfigureAwait(false);
                return results.Single();
            }
            var recovery = _store.RecoveryForAction(pairId, retained.ActionId);
            if (recovery is null || recovery.Side != retained.Side || recovery.PairId != pairId ||
                !PairPath.Same(recovery.Path, retained.Loser.Path) || Content.SameHash(recovery.Hash, retained.Loser.Hash) != true ||
                recovery.Size != retained.Loser.Size || recovery.VersionId != retained.Loser.VersionId &&
                !string.Equals(targetRoot.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
                return Skipped("The exact retained version is unavailable. The current file was left unchanged.");
            if (recovery.Expires <= _time.GetUtcNow())
                return Skipped("The retained version has expired. The current file was left unchanged.");
            var path = notice.Path;
            ItemState? expected = null;
            if (asCopy)
            {
                var extension = Path.GetExtension(path);
                if (extension.Length > 20 || extension.Any(ch => ch != '.' && !char.IsAsciiLetterOrDigit(ch))) extension = "";
                var slash = path.LastIndexOf('/');
                var directory = slash < 0 ? "" : path[..(slash + 1)];
                var found = false;
                for (var attempt = 1; attempt <= _settings.MaxLabelAttempts; attempt++)
                {
                    var candidate = directory + "Retained-" + ShortId.Hex16(pairId, noticeId) +
                        (attempt == 1 ? "" : "-" + attempt) + extension;
                    if (await target.ObserveAsync(candidate, token).ConfigureAwait(false) is not null) continue;
                    path = candidate;
                    found = true;
                    break;
                }
                if (!found) return Skipped("No unused copy name was found. Rename an existing copy and try again.");
            }
            else
            {
                expected = await target.ObserveAsync(path, token).ConfigureAwait(false);
                if (expected is null || !SameRetainedWinner(expected, retained.Winner, targetRoot))
                    return Skipped("The current file changed since this notice. Save the retained version as a copy instead.");
                var baselines = _store.BaselinesTouching(pairId, retained.Side,
                    new[] { expected.ObjectId, retained.Loser.ObjectId }.Distinct(StringComparer.Ordinal), []);
                var baseline = baselines.SingleOrDefault(item =>
                {
                    var targetState = retained.Side == Side.A ? item.A : item.B;
                    // A successor notice can be used before ordinary sync observes the preceding restore.
                    // Its displaced loser is then still the baseline; the opposite side must remain unchanged.
                    return SameRetainedWinner(targetState, expected, targetRoot) ||
                        notice.ReasonKey == "retained-version-restored" &&
                        retained.ActionId.StartsWith("nrs", StringComparison.Ordinal) &&
                        SameRetainedWinner(targetState, retained.Loser, targetRoot);
                });
                if (baseline is null)
                    return Skipped("The sync history for this version changed. Save the retained version as a copy instead.");
                var otherState = retained.Side == Side.A ? baseline.B : baseline.A;
                var other = retained.Side == Side.A ? b : a;
                var observedOther = await other.ObserveAsync(otherState.Path, token).ConfigureAwait(false);
                if (observedOther is null || !SameRetainedWinner(observedOther, otherState, retained.Side == Side.A ? pair.B : pair.A))
                    return Skipped("The other side changed since this notice. Save the retained version as a copy instead.");
            }
            var source = retained.Loser with { Hash = recovery.Hash, Size = recovery.Size, VersionId = recovery.VersionId };
            var action = new SyncAction(actionId, notice.Rule, asCopy ? ActionKind.CopyNew : ActionKind.Update,
                retained.Side, path, null, source, expected, !asCopy, null)
            {
                SuppressReconciledBaseline = true,
                ResolvedNoticeId = noticeId,
                CallerOwnsNotice = true,
            };
            // Archived bytes are not a live opposite-side source. Guard the actual destination, the notice path and
            // each identity-linked counterpart's actual paths; never an unrelated entity's synthetic spelling.
            var guard = ActionPredicates(pair, LeftAlone(pair));
            var linked = _store.BaselinesTouching(pairId, retained.Side,
                new[] { retained.Winner.ObjectId, retained.Loser.ObjectId }.Distinct(StringComparer.Ordinal), []);
            var otherSide = retained.Side == Side.A ? Side.B : Side.A;
            var otherRoot = retained.Side == Side.A ? pair.B : pair.A;
            IEnumerable<SyncAction> RestoreGuards()
            {
                yield return action with { Target = retained.Side, Path = path, Source = null };
                yield return action with { Target = retained.Side, Path = notice.Path, Source = null };
                foreach (var basis in linked)
                {
                    var counterpart = retained.Side == Side.A ? basis.B : basis.A;
                    yield return action with { Target = otherSide, Path = counterpart.Path, Source = null };
                    if (_store.ByObject(otherRoot.RootId, counterpart.ObjectId) is { } current)
                        yield return action with { Target = otherSide, Path = current.Path, Source = null };
                }
            }
            if (GuardedActions(RestoreGuards(), guard).Any(entry => !Allowed(entry.Guard, entry.Action)))
                return Skipped("This item or its linked counterpart is protected by exclusions, ignore rules or a nested pair. Include it before restoring.");
            Stream retainedBytes;
            try
            {
                retainedBytes = await OpenRetainedNoticeBytesAsync(targetRoot, target, recovery, source, token)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                return Skipped("The retained version could not be read or verified; the current file was left unchanged. " +
                    "Retry if the provider is temporarily unavailable. " + error.Message);
            }
            await using var bytes = retainedBytes;
            var reader = new RetainedBytesWriter(source, bytes);
            var plan = new Plan(pairId, _store.BeginGeneration(targetRoot.RootId, RunKind), [action], []);
            await foreach (var result in _executor.ApplyAsync(plan,
                retained.Side == Side.A ? target : reader, retained.Side == Side.B ? target : reader, token))
            {
                if (result.Outcome != Outcome.Done && result.Reason is { } reason)
                    _store.RecordNoticeChoiceFailure(notice, reason);
                return result;
            }
            return Skipped("The retained-version operation stopped before completion. Retry to check its outcome.");
        }, ct, admissionPairId: pairId);

    internal static string NoticeRestoreActionId(string pairId, string noticeId) =>
        ShortId.Make("nrs", pairId, noticeId);

    private static bool SameRetainedWinner(ItemState current, ItemState saved, RootDef root) =>
        current.Kind == ItemKind.File && current.ObjectId == saved.ObjectId && current.VersionId == saved.VersionId &&
        current.Size == saved.Size && (current.Mtime == saved.Mtime ||
            string.Equals(root.Provider, SyncProviders.Dropbox, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(current.ObjectId) && !string.IsNullOrEmpty(current.VersionId)) && current.Hash is not null &&
        Content.SameContent(current, saved) == true;

    private static async Task<Stream> OpenRetainedNoticeBytesAsync(RootDef root, IEndpointWriter writer,
        RecoveryRecord recovery, ItemState source, CancellationToken ct)
    {
        Stream bytes;
        if (string.Equals(root.Provider, SyncProviders.Local, StringComparison.OrdinalIgnoreCase))
            bytes = writer is ILocalRetainedNoticeReader local
                ? await local.OpenRecoveryAsync(recovery, source, ct).ConfigureAwait(false)
                : throw new SyncRunRefusedException("This local writer cannot verify Recovery ownership.");
        else if (writer is IRetainedNoticeReader historical)
            bytes = await historical.OpenRetainedAsync(source, ct).ConfigureAwait(false);
        else throw new SyncRunRefusedException("This provider cannot read the retained revision yet; the current file was left unchanged.");
        try
        {
            if (!bytes.CanSeek || bytes.Length != recovery.Size ||
                Content.SameHash(await DropboxContentHash.ComputeAsync(bytes, ct).ConfigureAwait(false), recovery.Hash) != true)
                throw new SyncRunRefusedException("The retained bytes failed verification; the current file was left unchanged.");
            bytes.Position = 0;
            return bytes;
        }
        catch
        {
            await bytes.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class RetainedBytesWriter(ItemState source, Stream bytes) : IEndpointWriter
    {
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (state != source) throw new InvalidOperationException("Unexpected retained source.");
            bytes.Position = 0;
            return Task.FromResult(bytes);
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => Task.FromResult<ItemState?>(source);
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => throw new NotSupportedException();
    }
}
