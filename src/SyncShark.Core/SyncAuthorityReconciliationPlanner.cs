using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityReconciliationPlanner(
    SyncAuthorityDatabase authority,
    SyncAuthorityPlannerOptions options,
    SyncAuthorityCommandAdmission admission,
    IReadOnlySet<string>? effectPathFilter = null,
    string? operationGroupId = null,
    IReadOnlyDictionary<string, string>? membershipObjectFences = null,
    IReadOnlyDictionary<string, string>? membershipPathFences = null,
    IReadOnlyDictionary<string, SyncAuthorityOperationKind>? expectedOperationKinds = null,
    Func<SyncComparisonProgress, Task>? onComparison = null,
    ISyncAuthorityContentComparison? contentComparison = null,
    SyncEndpointContext? endpointContext = null,
    IReadOnlyDictionary<string, SyncBootstrapMoveReview>? expectedMoveReviews = null,
    int planningConcurrency = 1)
{
    // Existing call sites that omit the optional comparer keep the legacy single-algorithm compatibility
    // policy. Cloud mixed-provider production wiring supplies the explicit provider-aware instance.
    private readonly ISyncAuthorityContentComparison _contentComparison =
        contentComparison ?? SyncAuthorityLegacyContentComparison.Instance;

    // Optional retained endpoint context captured by the caller from the planning pair. It is
    // descriptive presentation evidence only: it never changes a decision, an identity or an effect.
    private readonly SyncEndpointContext? _endpointContext = endpointContext;

    private readonly IReadOnlySet<string>? _effectPathFilter = effectPathFilter is null
        ? null
        : effectPathFilter.Select(PilotPath.NormalizeRelative)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, string> _membershipObjectFences =
        membershipObjectFences ?? new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly IReadOnlyList<KeyValuePair<string, string>> _membershipPathFences =
        (membershipPathFences ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
        .Select(item => new KeyValuePair<string, string>(PilotPath.NormalizeRelative(item.Key), item.Value))
        .OrderByDescending(item => item.Key.Count(character => character == '/'))
        .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    private readonly IReadOnlyDictionary<string, SyncAuthorityOperationKind>? _expectedOperationKinds =
        expectedOperationKinds?.ToDictionary(item => PilotPath.NormalizeRelative(item.Key), item => item.Value,
            StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<string, SyncBootstrapMoveReview> _expectedMoveReviews =
        (expectedMoveReviews ?? new Dictionary<string, SyncBootstrapMoveReview>())
        .ToDictionary(item => PilotPath.NormalizeRelative(item.Key), item => item.Value,
            StringComparer.OrdinalIgnoreCase);
    private IReadOnlySet<string> _coordinatedObjectReservations = new HashSet<string>(StringComparer.Ordinal);
    private SyncAuthorityPairObservationSet? _observationSet;
    private string? _observationSetId;
    private IReadOnlyList<SyncAuthorityPairObservationSet> _participantObservationSets = [];
    private IReadOnlyDictionary<string, string> _synchronizationPointIds =
        new Dictionary<string, string>(StringComparer.Ordinal);
    // The owning runner supplies this. Each invocation is awaited after
    // all planning writes drain; false stops planning without dispatching more.
    internal Func<IReadOnlyList<SyncAuthorityPlannerOutcome>, Task<bool>>? OnPlanWindow { get; init; }
    internal bool WorkWindowFull { get; private set; }
    internal bool RequiresFreshObservation { get; private set; }
    private sealed class PlanningWindowStoppedException : Exception { }
    // Ordered (structural/dependent) effects are handed over in windows of this size.
    private const int OrderedWindowOperations = 16;
    // A streamed window of independent file effects is sealed and executed as one batch, so it is sized to
    // fill a provider batch commit, not to the comparison width. It shares the durable work window with at
    // most one undrained ordered window, so its own planning never reaches capacity.
    private const int StreamedWindowFiles =
        SyncAuthorityDatabase.MaximumOrdinaryWorkWindow - OrderedWindowOperations;

    public async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> PlanAsync(string pairId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pairId);
        if (_endpointContext is not null) SyncEndpointContext.RequireRetainable(_endpointContext, pairId);
        if (options.ConfigurationRevision < 1)
            throw new InvalidOperationException("The planner requires a retained configuration revision.");
        foreach (var value in new[]
                 {
                     options.CapabilityProfileId, options.LocalControlNamespaceId,
                     options.DropboxControlNamespaceId
                 })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (options.DeletionMinimumCompleteObservations < 1)
            throw new InvalidOperationException("Deletion authority requires at least one complete observation.");
        var deletionMinimumInterval = options.DeletionMinimumInterval ?? TimeSpan.Zero;
        if (deletionMinimumInterval < TimeSpan.Zero)
            throw new InvalidOperationException("The deletion grace interval cannot be negative.");

        var localScanId = await authority.LoadLatestCompleteScanIdAsync(pairId, "local", cancellationToken);
        var dropboxScanId = await authority.LoadLatestCompleteScanIdAsync(pairId, "dropbox", cancellationToken);
        var localRoot = await authority.LoadActiveRootBindingIdAsync(pairId, "local", cancellationToken);
        var dropboxRoot = await authority.LoadActiveRootBindingIdAsync(pairId, "dropbox", cancellationToken);
        var observationSet = await authority.LoadPlanningObservationSetAsync(pairId,
            options.ConfigurationRevision, localRoot, dropboxRoot, localScanId, dropboxScanId,
            cancellationToken);
        var observationSetId = ObservationSetIdentity(observationSet);
        return await PlanAsync(observationSet, observationSetId, null, null, cancellationToken);
    }

    internal static string ObservationSetIdentity(SyncAuthorityPairObservationSet observationSet) =>
        Id("observation-set", observationSet.PairId, observationSet.LocalScanGenerationId,
            observationSet.DropboxScanGenerationId, observationSet.ContentFingerprint);

    public async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> PlanAsync(
        SyncAuthorityPairObservationSet observationSet, string observationSetId,
        IReadOnlySet<string>? coordinatedObjectReservations = null,
        IReadOnlyList<SyncAuthorityPairObservationSet>? participantObservationSets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observationSet);
        if (_endpointContext is not null)
            SyncEndpointContext.RequireRetainable(_endpointContext, observationSet.PairId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observationSetId);
        if (options.ConfigurationRevision < 1)
            throw new InvalidOperationException("The planner requires a retained configuration revision.");
        foreach (var value in new[]
                 {
                     options.CapabilityProfileId, options.LocalControlNamespaceId,
                     options.DropboxControlNamespaceId
                 })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (options.DeletionMinimumCompleteObservations < 1)
            throw new InvalidOperationException("Deletion authority requires at least one complete observation.");
        if ((options.DeletionMinimumInterval ?? TimeSpan.Zero) < TimeSpan.Zero)
            throw new InvalidOperationException("The deletion grace interval cannot be negative.");
        var pairId = observationSet.PairId;
        if (observationSet.ConfigurationRevision != options.ConfigurationRevision)
            throw new InvalidOperationException("The observation set has a different retained configuration revision.");
        var participants = participantObservationSets ?? [observationSet];
        if (participants.All(item => !string.Equals(item.PairId, observationSet.PairId,
                StringComparison.Ordinal)) ||
            participants.Select(item => item.PairId).Distinct(StringComparer.Ordinal).Count() != participants.Count)
            throw new InvalidOperationException("The coordinated planning participants are incomplete or duplicated.");
        cancellationToken.ThrowIfCancellationRequested();
        var preparationStarted = DateTimeOffset.UtcNow;
        if (onComparison is not null)
            await onComparison(new SyncComparisonProgress("Preparing Comparison", pairId,
                0, observationSet.SynchronizationPoints.Count, preparationStarted, DateTimeOffset.UtcNow,
                "Rechecking saved inventory authority, then indexing exclusions and uncertain folders before comparing files; no file transfer is running.", "saved baselines"));
        foreach (var participant in participants)
            if (!await authority.IsPlanningAuthorityCurrentAsync(participant.Bind(observationSetId),
                    cancellationToken))
                throw new InvalidOperationException(
                    "The observation set changed before reconciliation planning began.");
        _observationSet = observationSet;
        _observationSetId = observationSetId;
        _participantObservationSets = participants;
        _coordinatedObjectReservations = coordinatedObjectReservations ??
            new HashSet<string>(StringComparer.Ordinal);
        var localScanId = observationSet.LocalScanGenerationId;
        var dropboxScanId = observationSet.DropboxScanGenerationId;
        var local = observationSet.Local.ToDictionary(item => item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var dropbox = observationSet.Dropbox.ToDictionary(item => item.RelativePath,
            StringComparer.OrdinalIgnoreCase);
        var localUncertain = observationSet.LocalUncertainPaths;
        var dropboxUncertain = observationSet.DropboxUncertainPaths;
        // Index once. Scanning every excluded/uncertain ancestor for every saved
        // file is quadratic on real inventories, and used to run without a
        // cancellation check or progress report before the first comparison.
        var localUncertainIndex = new AncestorPathIndex(localUncertain, cancellationToken: cancellationToken);
        var dropboxUncertainIndex = new AncestorPathIndex(dropboxUncertain, cancellationToken: cancellationToken);
        var excludedPaths = new AncestorPathIndex(observationSet.ExcludedPaths, cancellationToken: cancellationToken);
        var points = observationSet.SynchronizationPoints;
        _synchronizationPointIds = points.ToDictionary(item => item.ObjectId,
            item => item.SynchronizationPointId, StringComparer.Ordinal);
        var baselines = points.ToDictionary(
            item => item.LocalRelativePath ?? item.DropboxRelativePath
                ?? throw new InvalidDataException("A synchronization point has no retained membership path."),
            StringComparer.OrdinalIgnoreCase);
        var localRoot = observationSet.LocalRootBindingId;
        var dropboxRoot = observationSet.DropboxRootBindingId;
        var outcomes = new List<SyncAuthorityPlannerOutcome>();
        var handledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var uncertainObjectIds = points.Where(item =>
                item.LocalRelativePath is not null && localUncertain.Contains(item.LocalRelativePath) ||
                item.DropboxRelativePath is not null && dropboxUncertain.Contains(item.DropboxRelativePath))
            .Select(item => item.ObjectId).ToHashSet(StringComparer.Ordinal);

        var localByObject = local.Values.GroupBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var dropboxByObject = dropbox.Values.GroupBy(item => item.ObjectId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var typeConflictAncestors = local.Keys.Intersect(dropbox.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(path => !string.Equals(local[path].ObjectKind, dropbox[path].ObjectKind,
                StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var typeConflictIndex = new AncestorPathIndex(typeConflictAncestors,
            windowsPaths: true, cancellationToken: cancellationToken);
        var work = new SyncAuthorityPlanningWorkCounters();
        var classificationInputs = new SyncAuthorityPlanningClassificationInputs(localByObject, dropboxByObject,
            uncertainObjectIds, _coordinatedObjectReservations, _effectPathFilter, excludedPaths,
            localUncertainIndex, dropboxUncertainIndex, typeConflictIndex);
        var baselineContexts = new SyncAuthorityPlanningBaselineContext[points.Count];
        var preparedBaselines = 0;
        var lastPreparationReport = DateTimeOffset.UtcNow;
        foreach (var item in points)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Classify this saved baseline once against the complete cut, then reuse that exact
            // classification for the move handling, the batch dependency boundaries and the eligibility
            // of the reviewed decision loop. No baseline is re-derived from the object indexes again.
            var context = SyncAuthorityPlanningClassification.Classify(item, classificationInputs, work);
            if (context.IsAncestorUncertain) uncertainObjectIds.Add(item.ObjectId);
            baselineContexts[preparedBaselines] = context;
            preparedBaselines++;
            var now = DateTimeOffset.UtcNow;
            if (onComparison is not null && (preparedBaselines == points.Count ||
                    now - lastPreparationReport >= TimeSpan.FromSeconds(1)))
            {
                await onComparison(new SyncComparisonProgress("Preparing Comparison",
                    item.LocalRelativePath ?? item.DropboxRelativePath ?? "",
                    preparedBaselines, points.Count, preparationStarted, now,
                    "Checking this saved file against excluded, uncertain and type-conflicting folders; no file transfer is running.", "saved baselines"));
                lastPreparationReport = now;
            }
        }
        // The complete comparison universe is built once from the local, remote, saved-baseline and
        // uncertain paths. A reviewed selection never becomes the universe: it only selects which of
        // those complete universe paths this invocation sorts and decides.
        var planningComparison = new SyncAuthorityPlanningComparisonContext(local.Keys, dropbox.Keys,
            baselines.Keys, localUncertain, dropboxUncertain, _effectPathFilter, work, cancellationToken);
        var comparisonStarted = DateTimeOffset.UtcNow;
        var completedChecks = 0;
        var totalChecks = points.Count + planningComparison.Universe.Count;
        async Task Report(string phase, string path, bool force = false)
        {
            var now = DateTimeOffset.UtcNow;
            if (onComparison is null) return;
            // Item reports precede that item's decision; eligibility-only skips still
            // advance the full-context counter, and the terminal report includes all of it.
            var completed = Volatile.Read(ref completedChecks);
            await onComparison(new SyncComparisonProgress(phase, path, force ? completed : completed - 1, totalChecks, comparisonStarted, now,
                phase == "Saving Comparison Results" ? "Saving the proposed actions and conflict decisions to Sync Shark's database; no files are being copied." :
                phase == "Checking Saved Baselines" ? "Comparing the saved synchronized version with the current endpoint identities." :
                "Comparing this item's local and remote versions to decide whether an action is needed."));
        }
        var orderedBaselines = baselineContexts.OrderBy(item => item.Baseline.ObjectId,
            StringComparer.Ordinal).ToArray();
        work.BaselineSortEntries += orderedBaselines.Length;
        // A reviewed run still marks the complete original move paths of every excluded, coordinated or
        // non-touching baseline, so those paths keep fencing the selected universe through the complete
        // identity membership and the ancestor/alias indexes. Only touching eligible descriptors enter
        // the expensive baseline decision loop, in the retained ObjectId order.
        if (_effectPathFilter is not null)
            foreach (var skip in orderedBaselines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (skip.IsEligible) continue;
                completedChecks++;
                work.EligibilitySkips++;
                MarkMovePathsHandled(handledPaths, skip.Baseline, skip.LocalMemberships, skip.RemoteMemberships);
            }
        WorkWindowFull = false;
        RequiresFreshObservation = false;
        var orderedWindow = new List<SyncAuthorityPlannerOutcome>(OrderedWindowOperations);
        var examinedOutcomes = 0;
        var structuralPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool BelowStructuralEffect(string path)
        {
            for(var slash=path.LastIndexOf('/');slash>=0;slash=path.LastIndexOf('/',slash-1))
            {
                if(structuralPaths.Contains(path[..slash]))return true;
                if(slash==0)break;
            }
            return false;
        }
        async Task DrainOrderedWindowAsync(bool force = false)
        {
            if(OnPlanWindow is null || participants.Count!=1)return;
            // Move only newly produced operation outcomes into the bounded journal window.
            // No-effect explanations stay process-local; previously drained IDs are not retained.
            var end=outcomes.Count;
            var retained=new List<SyncAuthorityPlannerOutcome>(end-examinedOutcomes);
            for(var index=examinedOutcomes;index<end;index++)
            {
                var item=outcomes[index];
                if(item.OperationId is null){retained.Add(item);continue;}
                orderedWindow.Add(item);
                if(local.TryGetValue(item.RelativePath,out var left) && left.ObjectKind=="directory" ||
                    dropbox.TryGetValue(item.RelativePath,out var right) && right.ObjectKind=="directory")
                    structuralPaths.Add(item.RelativePath);
            }
            if(end>examinedOutcomes)
            {
                outcomes.RemoveRange(examinedOutcomes,end-examinedOutcomes);
                outcomes.AddRange(retained);
            }
            examinedOutcomes=outcomes.Count;
            if(orderedWindow.Count==0 || !force && orderedWindow.Count<OrderedWindowOperations)return;
            var batch=orderedWindow.ToArray();orderedWindow.Clear();
            if(!await OnPlanWindow(batch))throw new PlanningWindowStoppedException();
        }
        try
        {
        foreach (var context in _effectPathFilter is null
                     ? orderedBaselines
                     : orderedBaselines.Where(item => item.IsEligible).ToArray())
        {
            try
            {
            cancellationToken.ThrowIfCancellationRequested();
            completedChecks++;
            var baseline = context.Baseline;
            var candidateLocalObjects = context.LocalMemberships;
            var candidateDropboxObjects = context.RemoteMemberships;
            if (context.IsExcluded || context.IsCoordinated || !context.TouchesFilter)
            {
                work.EligibilitySkips++;
                MarkMovePathsHandled(handledPaths, baseline, candidateLocalObjects, candidateDropboxObjects);
                continue;
            }
            work.EligibleDecisionEntries++;
            // The complete observation set remains the authority/context. A reviewed run
            // does not compare unapproved objects: do not serialize durable/presentation
            // callbacks for those eligibility-only skips before reaching approved work.
            await Report("Checking Saved Baselines", baseline.LocalRelativePath ?? baseline.DropboxRelativePath ?? "");
            var membershipPaths = new[] { baseline.LocalRelativePath, baseline.DropboxRelativePath }
                .Where(path => path is not null).Select(path => path!)
                .Concat(candidateLocalObjects.Select(item => item.RelativePath))
                .Concat(candidateDropboxObjects.Select(item => item.RelativePath));
            string? membershipPathFence = null;
            string? membershipPathFenceReason = null;
            foreach (var membershipPath in membershipPaths)
            {
                if (!TryGetMembershipPathFence(membershipPath, out var retainedReason)) continue;
                membershipPathFence = membershipPath;
                membershipPathFenceReason = retainedReason;
                break;
            }
            if (membershipPathFence is not null)
            {
                outcomes.Add(await BlockAsync(pairId, membershipPathFence, baseline.ObjectId, localScanId,
                    dropboxScanId, baseline, membershipPathFenceReason!, cancellationToken));
                MarkMovePathsHandled(handledPaths, baseline, candidateLocalObjects, candidateDropboxObjects);
                continue;
            }
            if (_membershipObjectFences.TryGetValue(baseline.ObjectId, out var membershipFenceReason))
            {
                var fencedPath = candidateLocalObjects.FirstOrDefault()?.RelativePath ??
                                 candidateDropboxObjects.FirstOrDefault()?.RelativePath ??
                                 baseline.LocalRelativePath ?? baseline.DropboxRelativePath ??
                                 throw new InvalidDataException("A membership-fenced object has no retained path.");
                outcomes.Add(await BlockAsync(pairId, fencedPath, baseline.ObjectId, localScanId,
                    dropboxScanId, baseline, membershipFenceReason, cancellationToken));
                MarkMovePathsHandled(handledPaths, baseline, candidateLocalObjects, candidateDropboxObjects);
                continue;
            }
            if (context.IsUncertain)
            {
                var uncertainPath = baseline.LocalRelativePath ?? baseline.DropboxRelativePath
                    ?? throw new InvalidDataException("An uncertain synchronization point has no retained path.");
                outcomes.Add(await BlockAsync(pairId, uncertainPath, baseline.ObjectId, localScanId,
                    dropboxScanId, baseline,
                    IsStrictDescendantOfAny(uncertainPath, typeConflictIndex)
                        ? "An ancestor has a file/folder conflict; its descendants are uncertain and no effect was planned."
                        : "At least one latest scan could not observe this path or an ancestor; uncertainty is not absence and no effect was planned.",
                    cancellationToken));
                MarkMovePathsHandled(handledPaths, baseline, candidateLocalObjects, candidateDropboxObjects);
                continue;
            }
            if (baseline.LocalObjectVersionId is null || baseline.DropboxObjectVersionId is null ||
                baseline.LocalRelativePath is null || baseline.DropboxRelativePath is null ||
                candidateLocalObjects.Length == 0 || candidateDropboxObjects.Length == 0)
                continue;
            var localObjects = candidateLocalObjects;
            var dropboxObjects = candidateDropboxObjects;
            var identityMoved = context.IdentityMoved;
            if ((localObjects.Length != 1 || dropboxObjects.Length != 1) && identityMoved)
            {
                var ambiguousPath = dropboxObjects.FirstOrDefault(item => !string.Equals(item.RelativePath,
                                        baseline.DropboxRelativePath, StringComparison.Ordinal))?.RelativePath ??
                                    localObjects.FirstOrDefault(item => !string.Equals(item.RelativePath,
                                        baseline.LocalRelativePath, StringComparison.Ordinal))?.RelativePath ??
                                    baseline.DropboxRelativePath;
                outcomes.Add(await BlockAsync(pairId, ambiguousPath, baseline.ObjectId, localScanId,
                    dropboxScanId, baseline,
                    "A stable physical identity appears at multiple or ambiguous paths; no move effect was planned.",
                    cancellationToken));
                MarkMovePathsHandled(handledPaths, baseline, localObjects, dropboxObjects);
                continue;
            }
            if (localObjects.Length != 1 || dropboxObjects.Length != 1) continue;
            var currentLocal = localObjects[0];
            var currentDropbox = dropboxObjects[0];
            var localSameVersion = await MatchesLocalVersionAsync(currentLocal,
                baseline.LocalObjectVersionId, cancellationToken);
            var dropboxSameVersion = string.Equals(currentDropbox.ObjectVersionId, baseline.DropboxObjectVersionId,
                StringComparison.Ordinal);
            var localMoved = !string.Equals(currentLocal.RelativePath, baseline.LocalRelativePath,
                StringComparison.Ordinal);
            var dropboxMoved = !string.Equals(currentDropbox.RelativePath, baseline.DropboxRelativePath,
                StringComparison.Ordinal);
            if (localMoved && dropboxMoved)
            {
                if (localSameVersion && dropboxSameVersion && string.Equals(currentLocal.RelativePath,
                        currentDropbox.RelativePath, StringComparison.Ordinal))
                {
                    var pointId = Id("syncpoint-converged-move", pairId, baseline.ObjectId,
                        currentLocal.ObjectVersionId, currentDropbox.ObjectVersionId,
                        currentLocal.MembershipId, currentDropbox.MembershipId);
                    await authority.AcceptSynchronizationPointAsync(pointId, pairId, baseline.ObjectId,
                        currentLocal.ObjectVersionId, currentDropbox.ObjectVersionId,
                        currentLocal.MembershipId, currentDropbox.MembershipId, cancellationToken);
                    outcomes.Add(await RecordAsync(pairId, currentLocal.RelativePath, baseline.ObjectId,
                        localScanId, dropboxScanId, pointId, SyncAuthorityPlannerOutcomeKind.Adopted,
                        null, null, null,
                        "Both stable identities already converged on the same renamed membership; the new path was adopted without an effect.",
                        cancellationToken));
                }
                else
                {
                    outcomes.Add(await BlockAsync(pairId, currentDropbox.RelativePath, baseline.ObjectId,
                        localScanId, dropboxScanId, baseline,
                        "Both stable identities moved to different paths or moved with changed content; no effect was planned.",
                        cancellationToken));
                }
                MarkMovePathsHandled(handledPaths, baseline, localObjects, dropboxObjects);
                continue;
            }
            if (!localMoved && !dropboxMoved) continue;
            // A one-sided file move may also carry an edit on the moving side. The move is planned here;
            // its committed point keeps the moving side's baseline version, so the next comparison sees
            // the edit and plans the ordinary update. The other side must still match the baseline.
            if (localMoved && (!dropboxSameVersion || !localSameVersion && currentLocal.ObjectKind != "file"))
                continue;
            if (dropboxMoved && !local.ContainsKey(currentDropbox.RelativePath) &&
                _expectedOperationKinds is not null &&
                _expectedOperationKinds.TryGetValue(currentDropbox.RelativePath, out var reviewedKind) &&
                reviewedKind == SyncAuthorityOperationKind.CreateLocal)
            {
                // A reviewed copy to a new absent path is not consent to relocate
                // or delete the predecessor. Stable cloud identity reuse during
                // a rename must not substitute automatic move inference for
                // that exact copy approval. Leave both paths to the ordinary
                // per-path planner and its unchanged observation/reservation guards.
                continue;
            }
            if (dropboxMoved)
            {
                if (!localSameVersion)
                {
                    outcomes.Add(await BlockAsync(pairId, currentDropbox.RelativePath, baseline.ObjectId,
                        localScanId, dropboxScanId, baseline,
                        "The local predecessor diverged while the stable Dropbox identity moved; no move effect was planned.",
                        cancellationToken));
                    MarkMovePathsHandled(handledPaths, baseline, localObjects, dropboxObjects);
                    continue;
                }
                var baselineDropboxVersion = await authority.LoadObjectVersionAsync(
                    baseline.DropboxObjectVersionId, cancellationToken);
                if (string.IsNullOrWhiteSpace(baselineDropboxVersion.ContentHash) ||
                    string.IsNullOrWhiteSpace(currentDropbox.ContentHash) || baselineDropboxVersion.Size is null ||
                    currentDropbox.Size is null)
                {
                    outcomes.Add(await BlockAsync(pairId, currentDropbox.RelativePath, baseline.ObjectId,
                        localScanId, dropboxScanId, baseline,
                        "The stable-ID Dropbox move lacks complete content hash or size evidence; no move effect was planned.",
                        cancellationToken));
                    MarkMovePathsHandled(handledPaths, baseline, localObjects, dropboxObjects);
                    continue;
                }
                // Changed content no longer blocks the move: the move is planned now and the next
                // comparison plans the ordinary update from the moved Dropbox version.
                var oldOccupier = dropbox.Values.SingleOrDefault(item => string.Equals(item.RelativePath,
                    baseline.DropboxRelativePath, StringComparison.Ordinal));
                if (oldOccupier is not null &&
                    !await IsDistinctRemoteReplacementAsync(oldOccupier, currentDropbox,
                        baselineDropboxVersion, cancellationToken))
                {
                    outcomes.Add(await BlockAsync(pairId, currentDropbox.RelativePath, baseline.ObjectId,
                        localScanId, dropboxScanId, baseline,
                        "The baseline Dropbox source path is still occupied; the move identity is ambiguous and no effect was planned.",
                        cancellationToken));
                    MarkMovePathsHandled(handledPaths, baseline, localObjects, dropboxObjects);
                    continue;
                }
            }
            var desiredPath = localMoved ? currentLocal.RelativePath : currentDropbox.RelativePath;
            var priorPath = localMoved ? currentDropbox.RelativePath : currentLocal.RelativePath;
            var destinationView = localMoved ? dropbox : local;
            if (destinationView.TryGetValue(desiredPath, out var collision) &&
                !string.Equals(collision.ObjectId, baseline.ObjectId, StringComparison.Ordinal))
            {
                // An occupant with the moved file's exact content, whose own copy on the moving side is
                // already gone, is retired through its ordinary recoverable deletion; the destination is
                // then free and the next comparison plans this same move. Anything else stays blocked.
                var movingView = localMoved ? localByObject : dropboxByObject;
                if (!baselines.TryGetValue(desiredPath, out SyncAuthoritySynchronizationPoint? occupantBaseline) ||
                    !string.Equals(occupantBaseline.ObjectId, collision.ObjectId, StringComparison.Ordinal) ||
                    occupantBaseline.LocalObjectVersionId is null || occupantBaseline.DropboxObjectVersionId is null ||
                    collision.ObjectKind != "file" || (localMoved ? currentLocal : currentDropbox).ObjectKind != "file" ||
                    movingView.ContainsKey(collision.ObjectId))
                    occupantBaseline = null;
                var equalOccupant = occupantBaseline is not null &&
                    (await _contentComparison.CompareAsync(localMoved ? currentLocal : collision,
                        localMoved ? collision : currentDropbox, cancellationToken)).Kind ==
                    SyncContentComparisonKind.Equal;
                outcomes.Add(equalOccupant
                    ? await TombstoneOrDeleteAsync(pairId, desiredPath, collision.ObjectId,
                        localMoved ? "local" : "dropbox", collision, occupantBaseline!, localRoot, dropboxRoot,
                        localScanId, dropboxScanId, cancellationToken)
                    : await BlockAsync(pairId, desiredPath, baseline.ObjectId, localScanId, dropboxScanId,
                        baseline, "The move destination is occupied by a different logical object with different " +
                            "or unconfirmed content; keeping both under a '(conflict)' name is not available yet, " +
                            "so no effect was planned.", cancellationToken));
                handledPaths.Add(priorPath);
                handledPaths.Add(desiredPath);
                continue;
            }
            var kind = localMoved ? SyncAuthorityOperationKind.MoveRemote : SyncAuthorityOperationKind.MoveLocal;
            if (!OperationKindMatchesReview(desiredPath, kind) ||
                !MoveIdentityMatchesReview(desiredPath, priorPath, kind, currentLocal, currentDropbox))
            {
                outcomes.Add(await BlockAsync(pairId, desiredPath, baseline.ObjectId, localScanId, dropboxScanId,
                    baseline, $"Moving '{priorPath}' to '{desiredPath}' requires a matching move approval. " +
                        "The observed change no longer matches the reviewed first-sync action or identity; review this file again. No effect was planned.",
                    cancellationToken));
                handledPaths.Add(priorPath);
                handledPaths.Add(desiredPath);
                continue;
            }
            outcomes.Add(await PlanMoveAsync(pairId, baseline.ObjectId, kind,
                localMoved ? currentLocal : currentDropbox, localMoved ? currentDropbox : currentLocal,
                baseline, localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken));
            handledPaths.Add(priorPath);
            handledPaths.Add(desiredPath);
            }
            finally { await DrainOrderedWindowAsync(); }
        }
        await DrainOrderedWindowAsync(force:true);

        // Only independent reviewed file creates/updates overlap. Move discovery and
        // folder/deletion/conflict decisions retain their original ordered path. Each
        // write still passes the same SQL guards and is acknowledged only after commit.
        var batchPlanning = planningConcurrency > 1 &&
            (OnPlanWindow is not null || _effectPathFilter is not null &&
                _expectedOperationKinds is not null && operationGroupId is not null);
        var planningWidth = Math.Clamp(planningConcurrency, 1, 16);
        var pending = new List<string>(planningWidth);
        var pendingObjects = new HashSet<string>(StringComparer.Ordinal);
        var streamPending = false;
        var streamStopped = false;
        // Full-context move/alias and ancestor decisions above remain mandatory.
        // An unexecuted directory effect is NOT an available parent for a file.
        // handledPaths also contains unchanged, unapproved ancestors; those are
        // not pending effects. Only actual identity relocations/aliases and
        // structurally relevant reviewed actions form batch boundaries.
        var relocatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var context in baselineContexts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            work.RelocatedBoundaryReuses++;
            // The relocation/alias boundary reuses the exact classification already computed for this
            // baseline: the membership indexes are never scanned a second time for the same baseline.
            if (context.IsRelocated)
                MarkMovePathsHandled(relocatedPaths, context.Baseline, context.LocalMemberships,
                    context.RemoteMemberships);
        }
        var effectBoundaries = relocatedPaths.Concat((_expectedOperationKinds ??
                new Dictionary<string, SyncAuthorityOperationKind>()).Where(item =>
                item.Value is not (SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal or
                    SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote) ||
                local.TryGetValue(item.Key, out var l) && l.ObjectKind != "file" ||
                dropbox.TryGetValue(item.Key, out var r) && r.ObjectKind != "file")
            .Select(item => item.Key)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var effectAncestors = new AncestorPathIndex(effectBoundaries, windowsPaths: true,
            cancellationToken: cancellationToken);

        static bool HasDescendant(string path, string[] sorted)
        {
            var prefix = path + "/";
            var index = Array.BinarySearch(sorted, prefix, StringComparer.OrdinalIgnoreCase);
            if (index < 0) index = ~index;
            return index < sorted.Length && sorted[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        bool CanStreamFile(string path, string objectId)
        {
            if (OnPlanWindow is null || participants.Count != 1 ||
                effectAncestors.Contains(path) || HasDescendant(path, effectBoundaries) ||
                planningComparison.HasDescendant(path) ||
                localByObject.TryGetValue(objectId, out var ls) && ls.Length != 1 ||
                dropboxByObject.TryGetValue(objectId, out var rs) && rs.Length != 1)
                return false;
            if (local.TryGetValue(path, out var l) && l.ObjectId != objectId ||
                dropbox.TryGetValue(path, out var r) && r.ObjectId != objectId ||
                baselines.TryGetValue(path, out var b) && (b.ObjectId != objectId ||
                    b.LocalRelativePath is { } lp && !string.Equals(lp, path, StringComparison.OrdinalIgnoreCase) ||
                    b.DropboxRelativePath is { } rp && !string.Equals(rp, path, StringComparison.OrdinalIgnoreCase)))
                return false;
            // Every destination ancestor already exists as a directory in the
            // complete cut. Missing/uncertain/type-conflicting ancestors stay on
            // the existing fully ordered path, never inferred from a subset.
            var destinations = _expectedOperationKinds is not null
                ? new[] { _expectedOperationKinds[path] is SyncAuthorityOperationKind.CreateLocal or
                    SyncAuthorityOperationKind.UpdateLocal ? local : dropbox }
                : new[] { local, dropbox };
            foreach (var destination in destinations)
                for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
                {
                    if (!destination.TryGetValue(path[..slash], out var parent) || parent.ObjectKind != "directory")
                        return false;
                    if (slash == 0) break;
                }
            return true;
        }

        async Task DrainComparisonsAsync()
        {
            if (pending.Count == 0) return;
            var admitted = pending.ToArray();
            pending.Clear();
            pendingObjects.Clear();
            work.BatchDrains++;
            work.BatchedPaths += admitted.Length;
            // At most planningWidth comparisons run at once. WhenAll drains EVERY started
            // path even when one refuses or cancellation arrives; no later chunk starts
            // then. The caller cannot seal/dispatch while metadata writes are pending.
            var results = new List<IReadOnlyList<SyncAuthorityPlannerOutcome>>(admitted.Length);
            var capacityReached = false;
            await using (var writes = batchPlanning ? authority.BeginExecutionConnectionScope(planningReservations: true) : null)
            {
                for (var start = 0; start < admitted.Length && !capacityReached; start += planningWidth)
                {
                    var comparisons = admitted.Skip(start).Take(planningWidth).Select(CompareAndTrackAsync).ToArray();
                    try { results.AddRange(await Task.WhenAll(comparisons)); }
                    catch (SyncAuthorityWorkWindowFullException) when (comparisons.All(task =>
                        !task.IsCanceled && (!task.IsFaulted || task.Exception!.Flatten().InnerExceptions.All(
                            error => error is SyncAuthorityWorkWindowFullException))))
                    {
                        // Every started comparison and its writes have drained. Preserve the exact
                        // registered prefix; rejected and unstarted paths retain no operation or spent ticket.
                        results.AddRange(comparisons.Where(task => task.IsCompletedSuccessfully)
                            .Select(task => task.Result));
                        capacityReached = true;
                    }
                }
            }
            var batch = results.SelectMany(item => item).ToArray();
            // A no-effect outcome stays entirely in memory. Only a real authority mutation is serialized, so the
            // independent-batch ordering and its cancellation boundary are exactly as before.
            if (streamPending && !streamStopped)
            {
                await DrainOrderedWindowAsync(force:true);
                streamStopped = !await OnPlanWindow!(batch);
            }
            else
            {
                outcomes.AddRange(batch);
                await DrainOrderedWindowAsync();
            }
            if (capacityReached) throw new SyncAuthorityWorkWindowFullException();
        }

        bool CanBatchFile(string path, out string? objectId)
        {
            local.TryGetValue(path, out var localItem);
            dropbox.TryGetValue(path, out var remoteItem);
            baselines.TryGetValue(path, out var point);
            objectId = localItem?.ObjectId ?? remoteItem?.ObjectId ?? point?.ObjectId;
            return batchPlanning && objectId is not null &&
                   (localItem is null || localItem.ObjectKind == "file") &&
                   (remoteItem is null || remoteItem.ObjectKind == "file") &&
                   (localItem is not null || remoteItem is not null) &&
                   !_expectedMoveReviews.ContainsKey(path) &&
                   (_expectedOperationKinds is not null
                       ? _expectedOperationKinds.TryGetValue(path, out var kind) &&
                           kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.UpdateLocal or
                               SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote
                       // A missing side with a baseline is a deletion decision and retains
                       // participant-wide authority. Only creates/replacements use this path.
                       : point is null || localItem is not null && remoteItem is not null);
        }

        async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> CompareAndTrackAsync(string path)
        {
            work.ComparisonPathsDecided++;
            var result = await ComparePathAsync(path);
            if (batchPlanning) Interlocked.Increment(ref completedChecks);
            return result;
        }

        // The reviewed selection is a strict subset of the complete comparison universe. The unselected
        // remainder is accounted for once here instead of being visited path by path, so the retained
        // full-context total stays exact without a per-skip callback; an unselected path is never
        // reported as compared or effected.
        if (planningComparison.UnselectedPathCount != 0)
        {
            completedChecks += planningComparison.UnselectedPathCount;
            work.UnselectedProgressAdvances += planningComparison.UnselectedPathCount;
        }
        try
        {
            foreach (var path in planningComparison.Selected)
            {
                if (streamStopped) break;
                cancellationToken.ThrowIfCancellationRequested();
                work.SelectedPathsVisited++;
                if(OnPlanWindow is not null && participants.Count==1 && BelowStructuralEffect(path))
                {
                    // A planned parent change cannot license a child against the old cut.
                    // Its exact outcome is drained first; the caller then reobserves.
                    RequiresFreshObservation=true;
                    work.SelectedPathsSkipped++;
                    continue;
                }
                if (handledPaths.Contains(path) || IsAtOrUnderAny(path, excludedPaths))
                {
                    work.SelectedPathsSkipped++;
                    Interlocked.Increment(ref completedChecks);
                    continue;
                }
                if (!batchPlanning) completedChecks++;
                // A directory already observed on both sides has no file effect:
                // comparison can only adopt its exact current metadata or return a
                // no-effect explanation. It must not split disjoint child transfers
                // into one-file windows merely because paths are lexically adjacent.
                if (local.TryGetValue(path,out var localDirectory) && localDirectory.ObjectKind=="directory" &&
                    dropbox.TryGetValue(path,out var remoteDirectory) && remoteDirectory.ObjectKind=="directory")
                {
                    outcomes.AddRange(await CompareAndTrackAsync(path));
                    await DrainOrderedWindowAsync();
                    continue;
                }
                if (CanBatchFile(path, out var objectId))
                {
                    var stream = CanStreamFile(path, objectId!);
                    if (pending.Count > 0 && stream != streamPending) await DrainComparisonsAsync();
                    if (streamStopped) break;
                    // Two memberships of the same logical object never run together.
                    if (pendingObjects.Contains(objectId!)) await DrainComparisonsAsync();
                    if (streamStopped) break;
                    streamPending = stream;
                    pendingObjects.Add(objectId!);
                    pending.Add(path);
                    if (pending.Count == (streamPending ? StreamedWindowFiles : planningWidth))
                        await DrainComparisonsAsync();
                }
                else
                {
                    await DrainComparisonsAsync();
                    if (streamStopped) break;
                    outcomes.AddRange(await CompareAndTrackAsync(path));
                    await DrainOrderedWindowAsync();
                }
            }
            await DrainComparisonsAsync();
            await DrainOrderedWindowAsync(force:true);
        }
        finally
        {
            // Cancellation during eligibility checks must not strand admitted writes.
            if (pending.Count != 0) await DrainComparisonsAsync();
        }

        async Task<IReadOnlyList<SyncAuthorityPlannerOutcome>> ComparePathAsync(string path)
        {
            var pathOutcomes = new List<SyncAuthorityPlannerOutcome>(1);
            await Report("Comparing Files and Folders", path, force: batchPlanning);
            local.TryGetValue(path, out var localEntry);
            dropbox.TryGetValue(path, out var dropboxEntry);
            baselines.TryGetValue(path, out var baseline);
            var objectId = localEntry?.ObjectId ?? dropboxEntry?.ObjectId ?? baseline?.ObjectId;
            if (objectId is not null && _coordinatedObjectReservations.Contains(objectId)) return pathOutcomes;
            var typeConflictAncestor = IsStrictDescendantOfAny(path, typeConflictIndex);
            var uncertain = IsAtOrUnderAny(path, localUncertainIndex) || IsAtOrUnderAny(path, dropboxUncertainIndex) ||
                            typeConflictAncestor ||
                            objectId is not null && uncertainObjectIds.Contains(objectId);
            if (uncertain)
            {
                if (objectId is null) return pathOutcomes;
                pathOutcomes.Add(await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId,
                    baseline,
                    typeConflictAncestor
                        ? "An ancestor has a file/folder conflict; its descendants are uncertain and no effect was planned."
                        : "At least one latest scan could not observe this path or an ancestor; uncertainty is not absence and no effect was planned.",
                    cancellationToken));
                return pathOutcomes;
            }
            if (objectId is null) return pathOutcomes;
            if (TryGetMembershipPathFence(path, out var membershipPathFenceReason))
            {
                pathOutcomes.Add(await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId,
                    baseline, membershipPathFenceReason, cancellationToken));
                return pathOutcomes;
            }
            if (_membershipObjectFences.TryGetValue(objectId, out var membershipFenceReason))
            {
                pathOutcomes.Add(await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId,
                    baseline, membershipFenceReason, cancellationToken));
                return pathOutcomes;
            }
            if (localEntry is not null && dropboxEntry is not null &&
                !string.Equals(localEntry.ObjectKind, dropboxEntry.ObjectKind, StringComparison.Ordinal))
            {
                pathOutcomes.Add(await ConflictAsync(pairId, path, objectId, localEntry, dropboxEntry, baseline,
                    localScanId, dropboxScanId,
                    "The same included path is a file on one side and a folder on the other.",
                    cancellationToken));
                return pathOutcomes;
            }

            if (localEntry is not null && dropboxEntry is not null &&
                !string.Equals(localEntry.ObjectId, dropboxEntry.ObjectId, StringComparison.Ordinal))
            {
                pathOutcomes.Add(await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId,
                    baseline, "Same-path observations do not share one logical-object authority.", cancellationToken));
                return pathOutcomes;
            }

            // A reviewed first-sync removal is only valid while the object still has no synchronization
            // point anywhere; otherwise the review is stale and must not become an ordinary deletion.
            if (_expectedOperationKinds is not null &&
                _expectedOperationKinds.TryGetValue(path, out var reviewedKind) &&
                reviewedKind == SyncAuthorityOperationKind.DeleteLocal &&
                (baseline is not null || _synchronizationPointIds.ContainsKey(objectId)))
            {
                pathOutcomes.Add(await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline,
                    "This file was synced after the first-sync review; review it again before removing it.",
                    cancellationToken));
                return pathOutcomes;
            }

            if (baseline is null)
            {
                pathOutcomes.Add(await PlanNewPathAsync(pairId, path, objectId, localEntry, dropboxEntry,
                    localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken));
                return pathOutcomes;
            }

            if (localEntry is { ObjectKind: "directory" } && dropboxEntry is { ObjectKind: "directory" })
            {
                var localDirectoryMatches = Matches(localEntry, baseline.LocalObjectVersionId,
                    baseline.LocalRelativePath);
                var dropboxDirectoryMatches = Matches(dropboxEntry, baseline.DropboxObjectVersionId,
                    baseline.DropboxRelativePath);
                if (localDirectoryMatches && dropboxDirectoryMatches)
                {
                    pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
                        baseline.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Unchanged, null, null, null,
                        "The folder membership matches the accepted synchronization point.", cancellationToken));
                }
                else
                {
                    var pointId = Id("syncpoint", pairId, objectId, localEntry.ObjectVersionId,
                        dropboxEntry.ObjectVersionId, localScanId, dropboxScanId);
                    await authority.AcceptSynchronizationPointAsync(pointId, pairId, objectId,
                        localEntry.ObjectVersionId, dropboxEntry.ObjectVersionId, localEntry.MembershipId,
                        dropboxEntry.MembershipId, cancellationToken);
                    pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId, pointId,
                        SyncAuthorityPlannerOutcomeKind.Adopted, null, null, null,
                        "Folder metadata changed while both sides retained the same folder membership; descendants remain independently reconciled.",
                        cancellationToken));
                }
                return pathOutcomes;
            }

            var localChanged = localEntry is null ? baseline.LocalObjectVersionId is not null :
                !string.Equals(localEntry.RelativePath, baseline.LocalRelativePath, StringComparison.Ordinal) ||
                !await MatchesLocalVersionAsync(localEntry, baseline.LocalObjectVersionId, cancellationToken);
            var dropboxChanged = !Matches(dropboxEntry, baseline.DropboxObjectVersionId,
                baseline.DropboxRelativePath);
            if (!localChanged && !dropboxChanged)
            {
                pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
                    baseline.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Unchanged, null, null, null,
                    "Both complete observations match the last accepted synchronization point.", cancellationToken));
                return pathOutcomes;
            }

            // A provider revision (or a local file identity) may change without changing
            // content. Use the same authoritative comparer as the two-sided convergence
            // path before scheduling a one-sided replacement. Unknown equality must not
            // establish a baseline: the existing guarded transfer path still handles it.
            if (localChanged != dropboxChanged && localEntry is not null && dropboxEntry is not null)
            {
                var comparison = await _contentComparison.CompareAsync(localEntry, dropboxEntry,
                    cancellationToken);
                if (comparison.Kind == SyncContentComparisonKind.Equal)
                {
                    var pointId = Id("syncpoint", pairId, objectId, localEntry.ObjectVersionId,
                        dropboxEntry.ObjectVersionId, localScanId, dropboxScanId);
                    await authority.AcceptSynchronizationPointAsync(pointId, pairId, objectId,
                        localEntry.ObjectVersionId, dropboxEntry.ObjectVersionId, localEntry.MembershipId,
                        dropboxEntry.MembershipId, cancellationToken);
                    pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
                        pointId, SyncAuthorityPlannerOutcomeKind.Adopted, null, null, null,
                        "One side's version changed while both sides retain equivalent content; the new point was adopted without a transfer.",
                        cancellationToken));
                    return pathOutcomes;
                }
            }

            if (localChanged && dropboxChanged)
            {
                if (localEntry is null && dropboxEntry is null)
                {
                    // Absence alone cannot prove a deletion succeeded. A completed,
                    // receipted cross-pair move of these exact baseline memberships
                    // does prove why the source is absent, including on a later run.
                    if (await authority.HasVerifiedCrossPairRemovalAsync(baseline,
                            localRoot, dropboxRoot, cancellationToken))
                    {
                        pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
                            baseline.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Adopted,
                            null, null, null,
                            "The exact baseline object was verified as moved to another pair; no source deletion or transfer is required.",
                            cancellationToken));
                        return pathOutcomes;
                    }
                    // One retained absence episode per accepted baseline and exact
                    // roots/policy. Later complete scans confirm the same episode;
                    // they must not append another tombstone on every no-effect run.
                    var tombstoneId = Id("tombstone", pairId, objectId, "both",
                        baseline.SynchronizationPointId, localRoot, dropboxRoot,
                        options.ConfigurationRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await authority.RecordTombstoneAsync(tombstoneId, pairId, objectId, "both",
                        baseline.LocalMembershipId ?? baseline.DropboxMembershipId,
                        baseline.LocalObjectVersionId ?? baseline.DropboxObjectVersionId, 1, cancellationToken);
                    await authority.BindBothAbsentClosureAsync(tombstoneId, baseline, localRoot, dropboxRoot,
                        localScanId, dropboxScanId, path, options.ConfigurationRevision, cancellationToken);
                    pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
                        baseline.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Tombstone, null, null,
                        tombstoneId, "Both complete observations prove absence; no delete effect is required.",
                        cancellationToken, isBothAbsentConfirmation: true));
                }
                else if (localEntry is not null && dropboxEntry is not null)
                {
                    var comparison = await _contentComparison.CompareAsync(localEntry, dropboxEntry,
                        cancellationToken);
                    if (comparison.Kind == SyncContentComparisonKind.Unknown)
                    {
                        pathOutcomes.Add(await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId,
                            baseline,
                            "Both sides changed from the accepted synchronization point, but cross-side " +
                            "content equality is unknown (" + comparison.Reason + "); no point was adopted " +
                            "and no effect was planned.", cancellationToken));
                    }
                    else if (comparison.Kind == SyncContentComparisonKind.Different)
                    {
                        pathOutcomes.Add(await NewerOrConflictAsync(pairId, path, objectId, localEntry, dropboxEntry,
                            baseline, localRoot, dropboxRoot, localScanId, dropboxScanId,
                            "Both sides changed from the accepted synchronization point.", cancellationToken));
                    }
                    else
                    {
                        var pointId = Id("syncpoint", pairId, objectId, localEntry.ObjectVersionId,
                            dropboxEntry.ObjectVersionId, localScanId, dropboxScanId);
                        await authority.AcceptSynchronizationPointAsync(pointId, pairId, objectId,
                            localEntry.ObjectVersionId, dropboxEntry.ObjectVersionId, localEntry.MembershipId,
                            dropboxEntry.MembershipId, cancellationToken);
                        pathOutcomes.Add(await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId, pointId,
                            SyncAuthorityPlannerOutcomeKind.Adopted, null, null, null,
                            "Both sides independently converged to equivalent content; the new point was adopted without an effect.",
                            cancellationToken));
                    }
                }
                else if ((localEntry ?? dropboxEntry)!.ObjectKind == "file" &&
                         !(localEntry is null ? localByObject : dropboxByObject).ContainsKey(objectId))
                {
                    // Edited on one side, deleted (not moved) on the other: the edit survives and is
                    // restored to the deleting side by the ordinary create; never a delete of the edit.
                    pathOutcomes.Add(await OperationOrBlockAsync(pairId, path, objectId,
                        localEntry is null ? SyncAuthorityOperationKind.CreateLocal : SyncAuthorityOperationKind.CreateRemote,
                        (localEntry ?? dropboxEntry)!, null, baseline, localRoot, dropboxRoot, localScanId,
                        dropboxScanId, cancellationToken, notice: "Edited on one side and deleted on the other; " +
                            "the edited file was kept and is restored to the side that deleted it."));
                }
                else
                {
                    pathOutcomes.Add(await ConflictAsync(pairId, path, objectId, localEntry, dropboxEntry, baseline,
                        localScanId, dropboxScanId, "Both sides changed from the accepted synchronization point.",
                        cancellationToken));
                }
                return pathOutcomes;
            }

            if (localChanged)
            {
                if (localEntry is null)
                {
                    pathOutcomes.Add(await TombstoneOrDeleteAsync(pairId, path, objectId, "local", dropboxEntry,
                        baseline, localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken));
                    return pathOutcomes;
                }
                if (dropboxEntry is null && baseline.DropboxObjectVersionId is not null)
                {
                    pathOutcomes.Add(await ConflictAsync(pairId, path, objectId, localEntry, null, baseline,
                        localScanId, dropboxScanId,
                        "The local version changed while Dropbox is absent; the retained predecessor prevents resurrection.",
                        cancellationToken));
                    return pathOutcomes;
                }
                var kind = dropboxEntry is null ? SyncAuthorityOperationKind.CreateRemote :
                    SyncAuthorityOperationKind.UpdateRemote;
                pathOutcomes.Add(await OperationOrBlockAsync(pairId, path, objectId, kind, localEntry, dropboxEntry,
                    baseline, localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken));
                return pathOutcomes;
            }

            if (dropboxEntry is null)
            {
                pathOutcomes.Add(await TombstoneOrDeleteAsync(pairId, path, objectId, "dropbox", localEntry,
                    baseline, localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken));
                return pathOutcomes;
            }
            if (localEntry is null && baseline.LocalObjectVersionId is not null)
            {
                pathOutcomes.Add(await ConflictAsync(pairId, path, objectId, null, dropboxEntry, baseline,
                    localScanId, dropboxScanId,
                    "The Dropbox version changed while local is absent; the retained predecessor prevents resurrection.",
                    cancellationToken));
                return pathOutcomes;
            }
            var remoteKind = localEntry is null ? SyncAuthorityOperationKind.CreateLocal :
                SyncAuthorityOperationKind.UpdateLocal;
            pathOutcomes.Add(await OperationOrBlockAsync(pairId, path, objectId, remoteKind, dropboxEntry, localEntry,
                baseline, localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken));
            return pathOutcomes;
        }
        // Unchanged and skipped outcomes describe no effect. Their complete outcome is still returned to the
        // caller, but they are neither serialized nor appended, so a repeated complete no-op scan adds no
        // durable record at all and cannot grow the retained history or change its safety state.
        }
        catch (PlanningWindowStoppedException)
        {
            // The sink already retained every admitted outcome before stopping this cut.
        }
        catch (SyncAuthorityWorkWindowFullException)
        {
            // Return already-registered dependency predecessors for the existing
            // dispatcher to drain. Capacity is never converted to a failed file.
            WorkWindowFull = true;
        }
        // Capacity can interrupt comparison before a partial ordered frontier reaches the sink.
        // Return exactly that registered prefix for the same dispatcher to drain.
        outcomes.AddRange(orderedWindow);
        SyncAuthorityPlanningContextObservation.CompletedForTest?.Invoke(work);
        await Report("Saving Comparison Results", "", true);
        return outcomes;
    }

    private static bool IsAtOrUnderAny(string? path, AncestorPathIndex ancestors) =>
        ancestors.Contains(path, strict: false);

    private static bool IsStrictDescendantOfAny(string? path, AncestorPathIndex ancestors) =>
        ancestors.Contains(path, strict: true);

    internal sealed class AncestorPathIndex
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

        internal AncestorPathIndex(IEnumerable<string> paths, bool windowsPaths = false,
            CancellationToken cancellationToken = default)
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _paths.Add(windowsPaths ? PilotPath.NormalizeRelative(path) : PilotPath.NormalizeInventoryPath(path));
            }
        }

        internal bool Contains(string? path, bool strict = false)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            var normalized = PilotPath.NormalizeInventoryPath(path);
            if (!strict && _paths.Contains(normalized)) return true;
            // Whole slash-delimited ancestors only: "folder" must not match
            // "folder-copy". Cost depends on path depth, never inventory size.
            for (var slash = normalized.LastIndexOf('/'); slash >= 0;
                 slash = normalized.LastIndexOf('/', slash - 1))
            {
                if (_paths.Contains(normalized[..slash])) return true;
                if (slash == 0) break;
            }
            return false;
        }
    }

    private static bool IsSameOrDescendant(string path, string ancestor)
    {
        var normalizedAncestor = PilotPath.NormalizeInventoryPath(ancestor);
        return string.Equals(path, normalizedAncestor, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedAncestor + "/", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetMembershipPathFence(string path, out string reason)
    {
        var normalized = PilotPath.NormalizeRelative(path);
        foreach (var fence in _membershipPathFences)
        {
            if (!IsSameOrDescendant(normalized, fence.Key)) continue;
            reason = fence.Value;
            return true;
        }
        reason = string.Empty;
        return false;
    }

    private static void MarkMovePathsHandled(ISet<string> handledPaths,
        SyncAuthoritySynchronizationPoint baseline, IEnumerable<SyncAuthorityObservedEntry> localObjects,
        IEnumerable<SyncAuthorityObservedEntry> dropboxObjects)
    {
        if (baseline.LocalRelativePath is not null) handledPaths.Add(baseline.LocalRelativePath);
        if (baseline.DropboxRelativePath is not null) handledPaths.Add(baseline.DropboxRelativePath);
        foreach (var item in localObjects) handledPaths.Add(item.RelativePath);
        foreach (var item in dropboxObjects) handledPaths.Add(item.RelativePath);
    }

    private async Task<SyncAuthorityPlannerOutcome> PlanMoveAsync(string pairId, string objectId,
        SyncAuthorityOperationKind kind, SyncAuthorityObservedEntry source, SyncAuthorityObservedEntry destination,
        SyncAuthoritySynchronizationPoint baseline, string localRoot, string dropboxRoot, string localScanId,
        string dropboxScanId, CancellationToken cancellationToken)
    {
        if (!SyncModePolicy.Allows(options.Direction, kind))
            return await SkipDirectionAsync(pairId, source.RelativePath, objectId, localScanId, dropboxScanId, baseline, cancellationToken);
        if (!options.EnableMoveEffects)
            return await BlockAsync(pairId, source.RelativePath, objectId, localScanId, dropboxScanId, baseline,
                "A stable-identity move was detected, but move effects are disabled by the retained capability profile.",
                cancellationToken);
        // A fresh approved group/build must not impersonate an immutable plan left
        // by an interrupted run. Exact replay within the same authority stays stable.
        var logicalOperationId = Id("operation", pairId, objectId, kind.ToString(), localScanId, dropboxScanId,
            baseline.SynchronizationPointId, options.CapabilityProfileId, operationGroupId ?? "none");
        var idempotencyKey = Id("idempotency", logicalOperationId);
        var registration = await authority.ReserveActionAsync(idempotencyKey, admission, cancellationToken);
        var operationId = registration.OperationId;
        var sourceRoot = kind == SyncAuthorityOperationKind.MoveRemote ? localRoot : dropboxRoot;
        var destinationRoot = kind == SyncAuthorityOperationKind.MoveRemote ? dropboxRoot : localRoot;
        var preconditions = JsonSerializer.Serialize(new
        {
            observationSetId = _observationSetId!,
            pairId,
            kind = SyncAuthorityNames.OperationKind(kind),
            sourcePath = source.RelativePath,
            priorPath = destination.RelativePath,
            configurationRevision = options.ConfigurationRevision,
            localScanGenerationId = localScanId,
            dropboxScanGenerationId = dropboxScanId,
            synchronizationPointId = baseline.SynchronizationPointId,
            source = EntryState(source),
            destination = EntryState(destination)
        });
        var members = new[]
        {
            Member(operationId, "source", pairId, objectId, sourceRoot, source),
            Member(operationId, "destination", pairId, objectId, destinationRoot, destination),
            new SyncAuthorityMember(Id("member", operationId, "lock"), pairId, objectId, "lock",
                destinationRoot, null, null, null, source.RelativePath, "{\"present\":false}")
        };
        var allocations = BuildAllocations(operationId, kind, source.RelativePath, localRoot, dropboxRoot,
            source.ObjectKind, source.Size);
        try
        {
            await authority.CreateOperationAsync(new SyncAuthorityOperationPlan(operationId,
                idempotencyKey, kind, pairId, objectId, options.ConfigurationRevision,
                options.CapabilityProfileId, preconditions, 80, members, [], allocations, operationGroupId,
                PlanningAuthorities(objectId, includeParticipants: false)),
                admission, cancellationToken, reservation: registration.Reservation);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains(
                   "nonterminal operation reservation", StringComparison.Ordinal))
        {
            return await BlockAsync(pairId, source.RelativePath, objectId, localScanId, dropboxScanId,
                baseline, "A prior nonterminal operation already reserves this root/path or containing subtree.",
                cancellationToken);
        }
        return await RecordAsync(pairId, source.RelativePath, objectId, localScanId, dropboxScanId,
            baseline.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Move, operationId, null, null,
            $"A stable physical identity moved from {destination.RelativePath} to {source.RelativePath}; one immutable {SyncAuthorityNames.OperationKind(kind)} operation was planned.",
            cancellationToken);
    }

    private async Task<SyncAuthorityPlannerOutcome> PlanNewPathAsync(string pairId, string path, string objectId,
        SyncAuthorityObservedEntry? local, SyncAuthorityObservedEntry? dropbox, string localRoot,
        string dropboxRoot, string localScanId, string dropboxScanId, CancellationToken cancellationToken)
    {
        // The reviewed expected-operation-kind map is the only consent: a first-sync local file
        // whose Dropbox path was deleted is removed with the ordinary archive-then-delete effect.
        if (_expectedOperationKinds is not null &&
            _expectedOperationKinds.TryGetValue(path, out var reviewedKind) &&
            reviewedKind == SyncAuthorityOperationKind.DeleteLocal &&
            local is not null && dropbox is null && local.ObjectKind == "file")
        {
            if (!options.EnableDeletionEffects || !SyncModePolicy.Allows(options.Direction,
                    SyncAuthorityOperationKind.DeleteLocal))
                return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, null,
                    "The reviewed first-sync deletion is disabled by the retained direction or capability profile.",
                    cancellationToken);
            // The ordinary tombstone records the absence; the reviewed choice replaces the confirmation wait.
            var tombstoneId = Id("tombstone", pairId, objectId, "dropbox", "first-sync", local.ObjectVersionId);
            await authority.RecordTombstoneObservationAsync(tombstoneId, pairId, objectId, "dropbox", null, null,
                localScanId, dropboxScanId, cancellationToken);
            return await OperationOrBlockAsync(pairId, path, objectId, SyncAuthorityOperationKind.DeleteLocal,
                local, null, null, localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken, tombstoneId);
        }
        if (local is not null && dropbox is not null)
        {
            var comparison = await _contentComparison.CompareAsync(local, dropbox, cancellationToken);
            if (comparison.Kind == SyncContentComparisonKind.Unknown)
                return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, null,
                    "A new path appeared on both sides, but cross-side content equality is unknown (" +
                    comparison.Reason + "); no point was adopted and no effect was planned.", cancellationToken);
            if (comparison.Kind == SyncContentComparisonKind.Different)
                return await NewerOrConflictAsync(pairId, path, objectId, local, dropbox, null, localRoot,
                    dropboxRoot, localScanId, dropboxScanId, "A new path appeared on both sides with different content.",
                    cancellationToken);
            var pointId = Id("syncpoint", pairId, objectId, local.ObjectVersionId, dropbox.ObjectVersionId,
                localScanId, dropboxScanId);
            await authority.AcceptSynchronizationPointAsync(pointId, pairId, objectId, local.ObjectVersionId,
                dropbox.ObjectVersionId, local.MembershipId, dropbox.MembershipId, cancellationToken);
            return await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId, pointId,
                SyncAuthorityPlannerOutcomeKind.Adopted, null, null, null,
                "Independently created equivalent content was adopted without an effect.", cancellationToken);
        }
        var kind = local is not null ? SyncAuthorityOperationKind.CreateRemote : SyncAuthorityOperationKind.CreateLocal;
        return await OperationOrBlockAsync(pairId, path, objectId, kind, local ?? dropbox!, null, null,
            localRoot, dropboxRoot, localScanId, dropboxScanId, cancellationToken);
    }

    private async Task<SyncAuthorityPlannerOutcome> TombstoneOrDeleteAsync(string pairId, string path,
        string objectId, string absentSide, SyncAuthorityObservedEntry? peer,
        SyncAuthoritySynchronizationPoint baseline, string localRoot, string dropboxRoot,
        string localScanId, string dropboxScanId, CancellationToken cancellationToken)
    {
        var kind = absentSide == "local" ? SyncAuthorityOperationKind.DeleteRemote : SyncAuthorityOperationKind.DeleteLocal;
        if (!SyncModePolicy.Allows(options.Direction, kind))
            return await SkipDirectionAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline, cancellationToken);
        var priorMembership = absentSide == "local" ? baseline.LocalMembershipId : baseline.DropboxMembershipId;
        var priorVersion = absentSide == "local" ? baseline.LocalObjectVersionId : baseline.DropboxObjectVersionId;
        var tombstoneId = Id("tombstone", pairId, objectId, absentSide, baseline.SynchronizationPointId,
            priorMembership ?? "none", priorVersion ?? "none");
        var observation = await authority.RecordTombstoneObservationAsync(tombstoneId, pairId, objectId,
            absentSide, priorMembership, priorVersion, localScanId, dropboxScanId, cancellationToken);
        var deletionMinimumInterval = options.DeletionMinimumInterval ?? TimeSpan.Zero;
        var graceSatisfied = observation.CompleteObservationCount >= options.DeletionMinimumCompleteObservations &&
                             observation.LastObservedUtc - observation.FirstObservedUtc >= deletionMinimumInterval;
        if (!options.EnableDeletionEffects || peer is null || !graceSatisfied)
            return await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
                baseline.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Tombstone, null, null,
                tombstoneId, !options.EnableDeletionEffects
                    ? "Complete observation recorded absence as a tombstone; deletion remains capability-gated."
                    : $"Deletion confirmation is pending: {observation.CompleteObservationCount}/{options.DeletionMinimumCompleteObservations} complete observations.",
                cancellationToken);
        return await OperationOrBlockAsync(pairId, path, objectId, kind, peer, null, baseline, localRoot,
            dropboxRoot, localScanId, dropboxScanId, cancellationToken, tombstoneId);
    }

    private async Task<SyncAuthorityPlannerOutcome> OperationOrBlockAsync(string pairId, string path,
        string objectId, SyncAuthorityOperationKind kind, SyncAuthorityObservedEntry source,
        SyncAuthorityObservedEntry? destination, SyncAuthoritySynchronizationPoint? baseline,
        string localRoot, string dropboxRoot, string localScanId, string dropboxScanId,
        CancellationToken cancellationToken, string? tombstoneId = null, string? notice = null)
    {
        if (!SyncModePolicy.Allows(options.Direction, kind))
            return await SkipDirectionAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline, cancellationToken);
        if (!OperationKindMatchesReview(path, kind))
            return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline,
                "The observed change no longer matches the reviewed first-sync action; no effect was planned.",
                cancellationToken);
        var isCreate = kind is SyncAuthorityOperationKind.CreateLocal or SyncAuthorityOperationKind.CreateRemote;
        var isDelete = kind is SyncAuthorityOperationKind.DeleteLocal or SyncAuthorityOperationKind.DeleteRemote;
        var enabled = isCreate ? options.EnableCreateEffects : isDelete ? options.EnableDeletionEffects :
            options.EnableReplacementEffects;
        if (!enabled)
            return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline,
                "The required effect is disabled by the retained capability profile.", cancellationToken);
        if (source.ContentHash is null && source.ObjectKind != "directory")
            return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline,
                "The exact source content is unavailable; no effect was planned.", cancellationToken);

        // Keep run and build authority in the identity, rather than overwriting an
        // old plan's group/profile when the same reviewed inventories are retried.
        var logicalOperationId = Id("operation", pairId, objectId, kind.ToString(), localScanId, dropboxScanId,
            baseline?.SynchronizationPointId ?? "none", options.CapabilityProfileId,
            operationGroupId ?? "none");
        var idempotencyKey = Id("idempotency", logicalOperationId);
        var registration = await authority.ReserveActionAsync(idempotencyKey, admission, cancellationToken);
        var operationId = registration.OperationId;
        var sourceSide = kind is SyncAuthorityOperationKind.CreateRemote or SyncAuthorityOperationKind.UpdateRemote or
            SyncAuthorityOperationKind.DeleteLocal ? "local" : "dropbox";
        var destinationSide = sourceSide == "local" ? "dropbox" : "local";
        var sourceRoot = sourceSide == "local" ? localRoot : dropboxRoot;
        var destinationRoot = destinationSide == "local" ? localRoot : dropboxRoot;
        var preconditions = JsonSerializer.Serialize(new
        {
            observationSetId = _observationSetId!,
            pairId,
            path,
            kind = SyncAuthorityNames.OperationKind(kind),
            configurationRevision = options.ConfigurationRevision,
            localScanGenerationId = localScanId,
            dropboxScanGenerationId = dropboxScanId,
            synchronizationPointId = baseline?.SynchronizationPointId,
            tombstoneId,
            source = EntryState(source),
            destination = destination is null ? new { present = false } : EntryState(destination)
        });
        var members = new List<SyncAuthorityMember>
        {
            Member(operationId, "source", pairId, objectId, sourceRoot, source)
        };
        if (destination is null)
            members.Add(new SyncAuthorityMember(Id("member", operationId, "destination"), pairId, objectId,
                "destination", destinationRoot, null, null, null, path, "{\"present\":false}"));
        else
            members.Add(Member(operationId, "destination", pairId, objectId, destinationRoot, destination));

        var allocations = BuildAllocations(operationId, kind, path, localRoot, dropboxRoot, source.ObjectKind,
            source.Size);
        var plan = new SyncAuthorityOperationPlan(operationId, idempotencyKey, kind, pairId,
            objectId, options.ConfigurationRevision, options.CapabilityProfileId, preconditions, isDelete ? 90 : 50,
            members, [], allocations, operationGroupId,
            PlanningAuthorities(objectId, includeParticipants: isDelete));
        try
        {
            await authority.CreateOperationAsync(plan, admission, cancellationToken, reservation: registration.Reservation);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains(
                   "nonterminal operation reservation", StringComparison.Ordinal))
        {
            return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline,
                "A prior nonterminal operation already reserves this root/path or containing subtree.",
                cancellationToken);
        }
        return await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
            baseline?.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Operation, operationId, null,
            tombstoneId, (notice is null ? "" : notice + " ") +
                $"An immutable {SyncAuthorityNames.OperationKind(kind)} operation was planned from three-view authority.",
            cancellationToken);
    }

    private Task<SyncAuthorityPlannerOutcome> SkipDirectionAsync(string pairId, string path, string objectId,
        string localScanId, string dropboxScanId, SyncAuthoritySynchronizationPoint? baseline, CancellationToken cancellationToken) =>
        RecordAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline?.SynchronizationPointId,
            SyncAuthorityPlannerOutcomeKind.Skipped, null, null, null,
            "Not transferred: this pair uses " + SyncModePolicy.Label(options.Direction) +
            ". The opposite side is unchanged; no synchronization point was accepted.", cancellationToken);

    private bool OperationKindMatchesReview(string path, SyncAuthorityOperationKind kind) =>
        _expectedOperationKinds is null ||
        _expectedOperationKinds.TryGetValue(PilotPath.NormalizeRelative(path), out var expected) &&
        expected == kind;

    private bool MoveIdentityMatchesReview(string path, string priorPath, SyncAuthorityOperationKind kind,
        SyncAuthorityObservedEntry local, SyncAuthorityObservedEntry remote) =>
        _expectedOperationKinds is null ||
        _expectedMoveReviews.TryGetValue(PilotPath.NormalizeRelative(path), out var review) &&
        review.Kind == kind && string.Equals(review.FromPath, priorPath, StringComparison.Ordinal) &&
        string.Equals(review.ObjectId, local.ObjectId, StringComparison.Ordinal) &&
        string.Equals(review.ObjectId, remote.ObjectId, StringComparison.Ordinal) &&
        string.Equals(review.LocalObjectVersionId, local.ObjectVersionId, StringComparison.Ordinal) &&
        string.Equals(review.RemoteObjectVersionId, remote.ObjectVersionId, StringComparison.Ordinal);

    private async Task<bool> IsDistinctRemoteReplacementAsync(SyncAuthorityObservedEntry oldOccupier,
        SyncAuthorityObservedEntry moved, SyncAuthorityObjectVersion baseline, CancellationToken token)
    {
        if (string.Equals(oldOccupier.ObjectId, moved.ObjectId, StringComparison.Ordinal)) return false;
        var oldVersion = await authority.LoadObjectVersionAsync(oldOccupier.ObjectVersionId, token);
        var movedVersion = await authority.LoadObjectVersionAsync(moved.ObjectVersionId, token);
        static string? FileId(string identity)
        {
            using var json = JsonDocument.Parse(identity);
            return json.RootElement.TryGetProperty("fileId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null;
        }
        var oldId = FileId(oldVersion.IdentityJson);
        var movedId = FileId(movedVersion.IdentityJson);
        var baselineId = FileId(baseline.IdentityJson);
        // A new object may reuse the old name. That is distinct from an alias of
        // the moved object, and the replacement is left for fresh observation.
        return !string.IsNullOrWhiteSpace(oldId) && !string.IsNullOrWhiteSpace(movedId) &&
               string.Equals(movedId, baselineId, StringComparison.Ordinal) &&
               !string.Equals(oldId, movedId, StringComparison.Ordinal);
    }

    private IReadOnlyList<SyncAuthorityPlanningAuthority> PlanningAuthorities(string objectId,
        bool includeParticipants)
    {
        var own = _observationSet ??
                  throw new InvalidOperationException("The planner has no bound observation set.");
        var observationSetId = _observationSetId ??
                               throw new InvalidOperationException("The planner has no bound observation-set ID.");
        // This index belongs to this exact validated planning cut. Binding each
        // file must not rescan every saved synchronization point. Reservation and
        // dispatch still validate its exact point against the current database.
        var ownAuthority = own.Bind(observationSetId) with
        {
            ObjectId = objectId,
            SynchronizationPointId = _synchronizationPointIds.GetValueOrDefault(objectId)
        };
        if (!includeParticipants) return [ownAuthority];
        return _participantObservationSets.Select(item =>
                string.Equals(item.PairId, own.PairId, StringComparison.Ordinal)
                    ? item.Bind(observationSetId, objectId)
                    : item.Bind(observationSetId))
            .OrderBy(item => item.PairId, StringComparer.Ordinal)
            .ToArray();
    }

    private IReadOnlyList<SyncAuthorityAllocation> BuildAllocations(string operationId,
        SyncAuthorityOperationKind kind, string path, string localRoot, string dropboxRoot,
        string objectKind = "file", long? sourceSize = null) =>
        // CC3B-1: the allocation truth lives once in the shared policy. With no cloud configuration the
        // returned rows and their order are byte-equivalent to the former in-planner implementation.
        SyncAuthorityOperationAllocations.Build(operationId, kind, path, localRoot, dropboxRoot, objectKind,
            sourceSize, options);

    private static SyncAuthorityMember Member(string operationId, string role, string pairId, string objectId,
        string rootBindingId, SyncAuthorityObservedEntry entry) =>
        new(Id("member", operationId, role), pairId, objectId, role, rootBindingId, entry.MembershipId,
            entry.ObjectVersionId, entry.MembershipGeneration, entry.RelativePath,
            JsonSerializer.Serialize(EntryState(entry)));

    /// <summary>
    /// Newest wins, decided where a two-sided content difference is detected. The newer side replaces the older
    /// through the same Update operation and executors a KeepLocal/KeepDropbox choice uses, so the older version
    /// is archived to Recovery first; an earlier open conflict for this object is then no longer the user's to
    /// decide. A tie, a missing time or a replacement this pair's direction forbids stays a conflict.
    /// </summary>
    private async Task<SyncAuthorityPlannerOutcome> NewerOrConflictAsync(string pairId, string path,
        string objectId, SyncAuthorityObservedEntry local, SyncAuthorityObservedEntry dropbox,
        SyncAuthoritySynchronizationPoint? baseline, string localRoot, string dropboxRoot, string localScanId,
        string dropboxScanId, string reason, CancellationToken cancellationToken)
    {
        var (localModified, dropboxModified) = SyncBootstrapActions.ModifiedUtc(local, dropbox);
        var newer = SyncBootstrapActions.NewerPort(localModified, dropboxModified);
        var kind = newer == "local" ? SyncAuthorityOperationKind.UpdateRemote : SyncAuthorityOperationKind.UpdateLocal;
        if (newer is null || !SyncModePolicy.Allows(options.Direction, kind))
            return await ConflictAsync(pairId, path, objectId, local, dropbox, baseline, localScanId, dropboxScanId,
                newer is null ? SyncBootstrapActions.ManualReason(localModified, dropboxModified) : reason,
                cancellationToken);
        var outcome = await OperationOrBlockAsync(pairId, path, objectId, kind, newer == "local" ? local : dropbox,
            newer == "local" ? dropbox : local, baseline, localRoot, dropboxRoot, localScanId, dropboxScanId,
            cancellationToken);
        if (outcome.Kind == SyncAuthorityPlannerOutcomeKind.Operation)
            await authority.SupersedeOpenConflictsAsync(pairId, objectId, cancellationToken);
        return outcome;
    }

    private async Task<SyncAuthorityPlannerOutcome> ConflictAsync(string pairId, string path, string objectId,
        SyncAuthorityObservedEntry? local, SyncAuthorityObservedEntry? dropbox,
        SyncAuthoritySynchronizationPoint? baseline, string localScanId, string dropboxScanId, string reason,
        CancellationToken cancellationToken)
    {
        var localVersion = local?.ObjectVersionId ?? baseline?.LocalObjectVersionId;
        var remoteVersion = dropbox?.ObjectVersionId ?? baseline?.DropboxObjectVersionId;
        if (localVersion is null || remoteVersion is null)
            return await BlockAsync(pairId, path, objectId, localScanId, dropboxScanId, baseline,
                reason + " Exact versions for both conflict sides are not retained.", cancellationToken);
        var conflictId = Id("conflict", pairId, objectId, localVersion, remoteVersion);
        await authority.RecordConflictAsync(conflictId, pairId, objectId, reason,
            localVersion, local?.MembershipId ?? baseline?.LocalMembershipId,
            remoteVersion, dropbox?.MembershipId ?? baseline?.DropboxMembershipId, cancellationToken);
        return await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
            baseline?.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Conflict, null, conflictId, null,
            reason + " Both versions were preserved and no effect was planned.", cancellationToken);
    }

    private async Task<SyncAuthorityPlannerOutcome> BlockAsync(string pairId, string path, string objectId,
        string localScanId, string dropboxScanId, SyncAuthoritySynchronizationPoint? baseline, string reason,
        CancellationToken cancellationToken) =>
        await RecordAsync(pairId, path, objectId, localScanId, dropboxScanId,
            baseline?.SynchronizationPointId, SyncAuthorityPlannerOutcomeKind.Blocked, null, null, null, reason,
            cancellationToken);

    private async Task<SyncAuthorityPlannerOutcome> RecordAsync(string pairId, string path, string objectId,
        string localScanId, string dropboxScanId, string? synchronizationPointId,
        SyncAuthorityPlannerOutcomeKind kind, string? operationId, string? conflictId, string? tombstoneId,
        string reason, CancellationToken cancellationToken, bool isBothAbsentConfirmation = false)
    {
        // An Unchanged or Skipped outcome records no effect, so it is returned complete - path, object, kind,
        // operation/conflict/tombstone identity, exact reason and the both-absence confirmation - without being
        // serialized or appended. Every other kind keeps its exact decision identity and durable record.
        if (kind is SyncAuthorityPlannerOutcomeKind.Unchanged or SyncAuthorityPlannerOutcomeKind.Skipped)
            return new SyncAuthorityPlannerOutcome(path, objectId, kind, operationId, conflictId, tombstoneId,
                reason, isBothAbsentConfirmation);
        // Repeated both-absence returns its current full outcome but keeps the
        // first immutable decision/cuts of that stable episode. A new accepted
        // baseline, root or policy has a different tombstone and decision identity.
        var id = isBothAbsentConfirmation
            ? Id("decision", pairId, objectId, "both", tombstoneId!, synchronizationPointId!)
            : Id("decision", pairId, objectId, localScanId, dropboxScanId, kind.ToString(),
                operationId ?? conflictId ?? tombstoneId ?? "none");
        var json = JsonSerializer.Serialize(new
        {
            path,
            kind = kind.ToString(),
            operationId,
            conflictId,
            tombstoneId,
            reason,
            endpointContext = _endpointContext,
            isBothAbsentConfirmation
        });
        await authority.RecordReconciliationDecisionAsync(id, pairId, objectId, localScanId, dropboxScanId,
            synchronizationPointId, DecisionName(kind), json, cancellationToken);
        return new SyncAuthorityPlannerOutcome(path, objectId, kind, operationId, conflictId, tombstoneId, reason,
            isBothAbsentConfirmation);
    }

    private async Task<bool> MatchesLocalVersionAsync(SyncAuthorityObservedEntry current,
        string? expectedVersion, CancellationToken token)
    {
        if (expectedVersion is null) return false;
        if (string.Equals(current.ObjectVersionId, expectedVersion, StringComparison.Ordinal)) return true;
        if (current.ObjectKind != "file" || current.ContentHash is null || current.Size is null) return false;
        var baseline = await authority.LoadObjectVersionAsync(expectedVersion, token);
        var observed = await authority.LoadObjectVersionAsync(current.ObjectVersionId, token);
        // Adding redundant verified byte fields to a legacy identity is not an edit.
        // Only this exact same-object/same-side/known-content representation difference
        // is equivalent. Physical/provider/algorithm/time fields remain exact; equal
        // hashes alone never authorize an identity replacement, move or overwrite.
        return baseline.Side == "local" && observed.Side == "local" &&
               string.Equals(baseline.ObjectId, current.ObjectId, StringComparison.Ordinal) &&
               string.Equals(observed.ObjectId, current.ObjectId, StringComparison.Ordinal) &&
               baseline.Size == current.Size && observed.Size == current.Size &&
               string.Equals(baseline.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(observed.ContentHash, current.ContentHash, StringComparison.OrdinalIgnoreCase) &&
               SyncAuthorityLocalVersionIdentity.EquivalentWithKnownContent(baseline.IdentityJson,
                   observed.IdentityJson, current.ContentHash, current.Size);
    }

    private static bool Matches(SyncAuthorityObservedEntry? current, string? expectedVersion,
        string? expectedPath) => current is null ? expectedVersion is null :
        expectedVersion is not null && string.Equals(current.ObjectVersionId, expectedVersion, StringComparison.Ordinal) &&
        string.Equals(current.RelativePath, expectedPath, StringComparison.Ordinal);

    private static object EntryState(SyncAuthorityObservedEntry entry) => new
    {
        present = true,
        entry.RelativePath,
        entry.ObjectVersionId,
        entry.MembershipId,
        entry.MembershipGeneration,
        entry.ContentHash,
        entry.Size,
        entry.ObjectKind
    };

    private static string DecisionName(SyncAuthorityPlannerOutcomeKind kind) => kind switch
    {
        SyncAuthorityPlannerOutcomeKind.Unchanged => "unchanged",
        SyncAuthorityPlannerOutcomeKind.Adopted => "adopted",
        SyncAuthorityPlannerOutcomeKind.Operation => "operation",
        SyncAuthorityPlannerOutcomeKind.Conflict => "conflict",
        SyncAuthorityPlannerOutcomeKind.Tombstone => "tombstone",
        SyncAuthorityPlannerOutcomeKind.Move => "move",
        SyncAuthorityPlannerOutcomeKind.Blocked => "blocked",
        SyncAuthorityPlannerOutcomeKind.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string Id(string role, params string[] parts) => role + "-" + Hash(role, parts)[..32];

    private static string Hash(string role, params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(role + "\n" + string.Join("\n", parts))))
            .ToLowerInvariant();
}
