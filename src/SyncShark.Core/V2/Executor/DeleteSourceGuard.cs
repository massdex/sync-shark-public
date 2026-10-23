namespace Massdex.SyncShark.V2;

/// <summary>One bounded absence observation. A null observation never authorizes deletion; Reason is diagnostic only.</summary>
public readonly record struct AbsenceObservation(bool? Absent, string? Reason)
{
    /// <summary>A short, non-sensitive explanation for an inconclusive observation.</summary>
    internal static string Cause(Exception error) => error switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => "rate limited",
        HttpRequestException => "network error",
        TimeoutException => "timeout",
        OperationCanceledException => "timeout",
        InvalidDataException or ArgumentException => "the source identity or path is malformed",
        _ => error.Message,
    };
}

/// <summary>Fresh identity AND path absence evidence; null means absence could not be proved.</summary>
public interface ISourceAbsenceProbe
{
    /// <summary>True only when the historical identity (if supplied) and exact path are both absent.</summary>
    Task<bool?> IsSourceAbsentAsync(ItemState? historical, string path, CancellationToken ct);

    /// <summary>Bounded fresh evidence in request order; a null observation never authorizes deletion.</summary>
    async Task<IReadOnlyList<AbsenceObservation>> IsAbsentBatchAsync(
        IReadOnlyList<(ItemState Historical, string Path)> requests, CancellationToken ct)
    {
        var results = new AbsenceObservation[requests.Count];
        await Parallel.ForEachAsync(Enumerable.Range(0, requests.Count), new ParallelOptions
        { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (index, token) =>
        {
            try { results[index] = new(await IsSourceAbsentAsync(requests[index].Historical, requests[index].Path, token).ConfigureAwait(false), null); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item) { results[index] = new(null, AbsenceObservation.Cause(error)); }
        }).ConfigureAwait(false);
        return results;
    }

    /// <summary>Fresh metadata-only source observation for a planned replacement.</summary>
    Task<ItemState?> ObserveSourceMetadataAsync(string path, CancellationToken ct);

    /// <summary>True only when the same historical object still exists; null means unknown.</summary>
    Task<bool?> IsHistoricalSourcePresentAsync(ItemState historical, CancellationToken ct) => Task.FromResult<bool?>(null);
}

// A definite pre-submit refusal, carrying the guard owner's existing result across the provider queue.
internal sealed class DeleteSourceUnconfirmedException(ActionResult result) : Exception(result.Reason)
{
    internal ActionResult Result { get; } = result;
}

/// <summary>Checks the missing side before any absence-propagating delete or Recovery move.</summary>
internal static class DeleteSourceGuard
{
    internal const string ReasonKey = "delete-source-unconfirmed";
    internal static string NoticeId(string pairId, string path) => ShortId.Make("n", pairId, "0", PathKey.Of(path), ReasonKey);

    // Explicit decisions authorize conditional removal of their frozen victim, not propagation of absence.
    internal static bool ExplicitRemoval(SyncAction action) => action.ChoiceRemove || action.RebuildableRemove ||
        action.RemovalReviewChoice || action.FrozenChoiceId is not null ||
        (action.SuppressReconciledBaseline && action.ActionId.StartsWith("nch", StringComparison.Ordinal));

    internal static bool Applies(SyncAction action) => action.Kind == ActionKind.Delete && !ExplicitRemoval(action);

    internal static bool RequiresAbsence(SyncAction action) => Applies(action) && action.Rule is not (4 or 12 or 26 or 32);

    internal static bool ReviewedRemoval(SyncAction action) => action.Kind == ActionKind.Delete &&
        (action.FirstSyncChoice || action.ChoiceRemove || action.RebuildableRemove || action.RemovalReviewChoice);

    internal static Notice ReviewedRemovalNotice(string pairId, string path, IReadOnlyList<SyncAction> actions) =>
        new(pairId, 40, NoticeKind.UserDecision, path,
            "The reviewed removal no longer matches its saved item or contents, or a complete fresh read could not prove it. Nothing was removed or copied back." + NoticeChoices.RemovalAttentionHelp,
            actions.Any(action => action.ExpectedTarget?.Kind == ItemKind.Folder) ? "reviewed-folder-grew" : "reviewed-removal-skipped",
            RemovalSides: actions.Select(action => action.Target).Distinct().ToArray());

    // The frozen signature is authority; current sync inventory and cursors are NOT fresh proof.
    // Listing is targeted even when ordinary sync excludes this path or consumed its change while sleeping.
    internal static async Task<bool> ReviewedRemovalUnchangedAsync(SyncAction action, PairDef pair,
        IStateStore store, IInventorySource inventory, CancellationToken ct)
    {
        var observed = await ObserveReviewedRemovalAsync(action, pair, store, inventory, ct).ConfigureAwait(false);
        if (observed.Signature is null) return false;
        if (action.ExpectedTarget?.Kind != ItemKind.Folder) return true;
        return action.FrozenRemovalSignature is { } all
            ? all == observed.Signature
            : !observed.HasCarried && action.FrozenVictimSignature == observed.Signature;
    }

    internal static async Task<string?> CaptureReviewedRemovalAsync(SyncAction action, PairDef pair,
        IStateStore store, IInventorySource inventory, CancellationToken ct) =>
        (await ObserveReviewedRemovalAsync(action, pair, store, inventory, ct).ConfigureAwait(false)).Signature;

    private static async Task<(string? Signature, bool HasCarried)> ObserveReviewedRemovalAsync(SyncAction action,
        PairDef pair, IStateStore store, IInventorySource inventory, CancellationToken ct)
    {
        if (action.ExpectedTarget is not { } expected) return (null, false);
        var folder = expected.Kind == ItemKind.Folder;
        var hasCarried = false;
        var session = "delete-check-" + Guid.NewGuid().ToString("N");
        var db = store as SqliteStateStore;
        var memory = db is null ? new List<ItemState>() : null;
        var rootSeen = false;
        var valid = true;
        var damaged = false;
        var rules = PairInclude.IgnoreRules(pair);
        var root = action.Target == Side.A ? pair.A : pair.B;
        var refused = ProviderRefusedPaths.Load(store, pair, root.RootId);
        try
        {
            await inventory.ReadSubtreeAsync(action.Path, page =>
            {
                var children = new List<ItemState>(page.Count);
                foreach (var item in page)
                {
                    if (item.IncompleteReason is not null || item.Flags != ItemFlags.None) valid = false;
                    if (PairPath.Same(item.Path, action.Path))
                    {
                        if (rootSeen || item.ObjectId != expected.ObjectId || item.Kind != expected.Kind ||
                            (item.Kind == ItemKind.File && (item.Size != expected.Size ||
                                Content.SameHash(item.Hash, expected.Hash) != true ||
                                Content.SideStamp(item) != Content.SideStamp(expected)))) valid = false;
                        rootSeen = true;
                    }
                    else if (!folder || !PairPath.IsUnder(item.Path, action.Path)) valid = false;
                    else
                    {
                        hasCarried |= refused.Contains(item) || SyncIgnorePolicy.IsIgnored(item.Path, item.Kind == ItemKind.Folder, rules);
                        children.Add(item);
                    }
                }
                if (db is not null) db.StageRemovalItems(session, action.Target, children, ordinalPaths: true);
                else memory!.AddRange(children);
                return Task.CompletedTask;
            }, ct).ConfigureAwait(false);
            return (valid && rootSeen ? FolderDelete.SignatureOrdered(db is not null
                ? db.RemovalObservation(session, action.Target)
                : memory!.OrderBy(item => item.Path, StringComparer.Ordinal)) : null, hasCarried);
        }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
        { return (null, hasCarried); }
        catch (Exception error)
        {
            damaged = FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.State;
            throw;
        }
        finally { if (!damaged) db?.ClearRemovalObservation(session); }
    }

    internal static async Task<bool?> ObserveAbsenceAsync(IEndpointWriter source, ItemState? historical,
        string path, CancellationToken ct)
    {
        if (historical is null || string.IsNullOrWhiteSpace(historical.ObjectId) || source is not ISourceAbsenceProbe probe)
            return null;
        return await probe.IsSourceAbsentAsync(historical, path, ct).ConfigureAwait(false);
    }

    internal static async Task<ActionResult?> CheckReconcileAsync(SyncAction action, IEndpointWriter source,
        ItemState? historical, bool restorePending, CancellationToken ct)
    {
        // Already-submitted explicit victims and replacements retain their durable non-absence authority.
        if (ExplicitRemoval(action) || action.Rule is 26 or 32) return null;
        if (!restorePending && historical is not null && !string.IsNullOrWhiteSpace(historical.ObjectId) &&
            source is ISourceAbsenceProbe probe &&
            await probe.IsHistoricalSourcePresentAsync(historical, ct).ConfigureAwait(false) == true)
        {
            var current = await probe.ObserveSourceMetadataAsync(action.Path, ct).ConfigureAwait(false);
            if (current is not null && string.Equals(current.ObjectId, historical.ObjectId, StringComparison.Ordinal))
                return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, EffectResolver.NotApplied)
                {
                    ForgetUnsafeDeleteBaseline = true,
                    NewA = action.Target == Side.B ? current : null,
                    NewB = action.Target == Side.A ? current : null
                };
        }
        // A paired move's already-submitted effect is resolved by identity absence, not plan-local admission.
        var result = await CheckEvidenceAsync(action with { Rule = 6 }, source, historical, restorePending, ct)
            .ConfigureAwait(false);
        return result is null ? null : result with { Outcome = Outcome.Uncertain,
            DeleteSourceUnconfirmed = false, DeleteSourceIdentityMissing = false,
            RetryAfterSeconds = ProviderTransientFailure.VerificationRetrySeconds };
    }

    internal static IReadOnlyDictionary<(Side, int, string), ItemState> Replacements(IReadOnlyList<SyncAction> actions) =>
        actions.Where(next => next.Rule is 26 or 32 && next.Source is not null &&
            next.Kind is ActionKind.CopyNew or ActionKind.MkDir)
            .GroupBy(next => (next.Target, next.Rule, PathKey.Of(next.Path)))
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Source!);

    internal static ItemState? Replacement(SyncAction action, IReadOnlyDictionary<(Side, int, string), ItemState> replacements)
    {
        if (action.Kind != ActionKind.Delete || action.Rule is not (26 or 32)) return null;
        for (var path = PathKey.Of(action.Path); path.Length > 0;)
        {
            if (replacements.TryGetValue((action.Target, action.Rule, path), out var source)) return source;
            var slash = path.LastIndexOf('/');
            path = slash < 0 ? "" : path[..slash];
        }
        return null;
    }

    internal static async Task RevalidateCurrentAsync(SyncAction? action)
    {
        if (action?.Kind != ActionKind.Delete) return;
        var context = ProviderEffectContext.Current.Value;
        var matches = context?.Action?.ActionId == action.ActionId;
        var observation = matches && context?.RevalidateDelete is { } check
            ? await check().ConfigureAwait(false) : new AbsenceObservation(null, "No delete recheck supplied.");
        if (observation.Absent == true) return;
        var refusal = matches && context?.RefuseDelete is { } refuse ? await refuse(observation).ConfigureAwait(false) : null;
        throw new DeleteSourceUnconfirmedException(refusal ?? new ActionResult(action.ActionId,
            Outcome.Skipped, null, null, null, observation.Reason) { DeleteSourceUnconfirmed = true });
    }

    internal static Task<ActionResult?> CheckAsync(SyncAction action, IEndpointWriter source,
        StoreCommitter committer, string pairId, CancellationToken ct, ItemState? replacement = null,
        bool pairedAdmitted = false, bool preprobed = false, bool? absence = null,
        AdmissionAbsenceBatch? admissionBatch = null, string? absenceReason = null) =>
        CheckEvidenceAsync(action, source, RequiresAbsence(action) ? committer.DeleteSource(action) : null,
            Applies(action) && committer.DeleteRestorePending(action), ct, replacement, pairedAdmitted,
            preprobed, absence, admissionBatch, absenceReason);

    // Captured store evidence keeps SQLite reads outside provider namespace gates. Both initial
    // admission and dispatch use this decision; callers never infer authority from a missing callback.
    internal static async Task<ActionResult?> CheckEvidenceAsync(SyncAction action, IEndpointWriter source,
        ItemState? historical, bool restorePending, CancellationToken ct, ItemState? replacement = null,
        bool pairedAdmitted = false, bool preprobed = false, bool? absence = null,
        AdmissionAbsenceBatch? admissionBatch = null, string? absenceReason = null)
    {
        if (!Applies(action)) return null;
        string? detail;
        var retryLater = preprobed && absence is null;
        var identityMissing = false;
        try
        {
            if (action.Rule is 4 or 12)
            {
                if (pairedAdmitted) return null;
                detail = action.Rule == 12
                    ? "The move-completion delete has no verified surviving destination: the destination changed or disappeared, could not be read, or the matching Adopt did not complete. Nothing was deleted; Retry replans the move."
                    : "The move-completion delete has no admitted or successful matching effect in this plan. Nothing was deleted; Retry replans the move.";
            }
            else if (action.Rule is 26 or 32)
            {
                var current = replacement is not null && source is ISourceAbsenceProbe metadata
                    ? await metadata.ObserveSourceMetadataAsync(replacement.Path, ct).ConfigureAwait(false) : null;
                if (current is not null && replacement is not null && current.Kind == replacement.Kind &&
                    string.Equals(current.ObjectId, replacement.ObjectId, StringComparison.Ordinal)) return null;
                detail = "The expected replacement source could not be verified with its planned identity and kind. Nothing was deleted; Retry checks it again next run.";
            }
            else if (restorePending)
            {
                detail = "This item's missing copy is queued for restoration after an interrupted delete. Nothing was deleted; Retry restores the missing copy instead.";
            }
            else
            {
                identityMissing = historical is null || string.IsNullOrWhiteSpace(historical.ObjectId);
                // A missing path alone cannot exclude an original that moved elsewhere. This also
                // holds expanded children without a baseline until trusted per-child identity arrives.
                bool? absent = null;
                if (historical is not null && !string.IsNullOrWhiteSpace(historical.ObjectId) &&
                    source is ISourceAbsenceProbe probe)
                {
                    if (preprobed) absent = absence;
                    else if (admissionBatch is not null)
                    {
                        var observation = await admissionBatch.ProbeAsync(probe, historical, action.Path, ct).ConfigureAwait(false);
                        absent = observation.Absent;
                        absenceReason = observation.Reason;
                    }
                    else absent = await ObserveAbsenceAsync(source, historical, action.Path, ct).ConfigureAwait(false);
                }
                if (absent == true) return null;
                retryLater |= absent is null;
                detail = identityMissing
                    ? "The original source identity is unavailable, so absence cannot authorize deletion. Nothing was deleted; review this item to keep, restore, ignore, or explicitly remove its frozen survivor."
                    : absent == false
                        ? "Deletion was expected, but the source item still exists. Nothing was deleted; the item will be checked and replanned next run."
                        : "Deletion was expected, but the source item's absence could not be verified" +
                          (string.IsNullOrWhiteSpace(absenceReason) ? "" : ": " + absenceReason) +
                          ". Nothing was deleted; Retry checks it again next run.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (FailureRetryOwner.ScopeOf(error, ct) == FailureRetryOwner.FailureScope.Item)
        {
            retryLater = true;
            detail = "Deletion was expected, but checking the source failed. Nothing was deleted; Retry checks it again next run. " + error.Message;
        }
        detail = $"Source '{action.Path}' on side {(action.Target == Side.A ? Side.B : Side.A)}: " + detail;
        // The result commit owns retry detail and thresholded notices atomically; never emit one
        // notice per provider failure here before the aggregation transaction sees the whole debt.
        return new ActionResult(action.ActionId, Outcome.Skipped, null, null, null, detail)
            { DeleteSourceUnconfirmed = true, DeleteSourceIdentityMissing = identityMissing,
                RetryAfterSeconds = retryLater && !identityMissing ? 30 : null };
    }
}
