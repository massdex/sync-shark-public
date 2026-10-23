namespace Massdex.SyncShark;

/// <summary>
/// The complete global context one planning invocation classifies every saved synchronization point
/// against. Every member is the exact retained observation cut: nothing here is reduced to the reviewed
/// selection, so a sparse reviewed run still classifies against unrelated aliases, parent directories,
/// overlapping-pair participants and coordinated reservations.
/// </summary>
internal readonly record struct SyncAuthorityPlanningClassificationInputs(
    IReadOnlyDictionary<string, SyncAuthorityObservedEntry[]> LocalByObject,
    IReadOnlyDictionary<string, SyncAuthorityObservedEntry[]> RemoteByObject,
    IReadOnlySet<string> InitialUncertainObjectIds,
    IReadOnlySet<string> CoordinatedObjectReservations,
    IReadOnlySet<string>? EffectPathFilter,
    SyncAuthorityReconciliationPlanner.AncestorPathIndex ExcludedPaths,
    SyncAuthorityReconciliationPlanner.AncestorPathIndex LocalUncertainPaths,
    SyncAuthorityReconciliationPlanner.AncestorPathIndex RemoteUncertainPaths,
    SyncAuthorityReconciliationPlanner.AncestorPathIndex TypeConflictAncestors);

/// <summary>
/// One immutable classification of one saved synchronization point, computed exactly once in the single
/// cancellable preparation pass and reused afterwards by the move/baseline decision loop and by the
/// batch dependency-boundary construction. It holds that exact baseline, its already-resolved local and
/// remote membership arrays and every pure predicate the planning loop previously recomputed per
/// baseline. It is derived state for one invocation only and is discarded with that invocation.
/// </summary>
internal sealed class SyncAuthorityPlanningBaselineContext
{
    internal SyncAuthorityPlanningBaselineContext(SyncAuthoritySynchronizationPoint baseline,
        SyncAuthorityObservedEntry[] localMemberships, SyncAuthorityObservedEntry[] remoteMemberships,
        bool isExcluded, bool isCoordinated, bool isUncertain, bool isAncestorUncertain, bool isRelocated,
        bool identityMoved, bool touchesFilter)
    {
        Baseline = baseline;
        LocalMemberships = localMemberships;
        RemoteMemberships = remoteMemberships;
        IsExcluded = isExcluded;
        IsCoordinated = isCoordinated;
        IsUncertain = isUncertain;
        IsAncestorUncertain = isAncestorUncertain;
        IsRelocated = isRelocated;
        IdentityMoved = identityMoved;
        TouchesFilter = touchesFilter;
    }

    internal SyncAuthoritySynchronizationPoint Baseline { get; }

    /// <summary>The complete local memberships of this exact object, resolved once.</summary>
    internal SyncAuthorityObservedEntry[] LocalMemberships { get; }

    /// <summary>The complete remote memberships of this exact object, resolved once.</summary>
    internal SyncAuthorityObservedEntry[] RemoteMemberships { get; }

    internal bool IsExcluded { get; }

    internal bool IsCoordinated { get; }

    /// <summary>Uncertainty of this exact object, including the indexed ancestor and type-conflict checks.</summary>
    internal bool IsUncertain { get; }

    /// <summary>True when the indexed ancestor or type-conflict check alone classified this baseline uncertain.</summary>
    internal bool IsAncestorUncertain { get; }

    /// <summary>The relocation/alias boundary flag, with the batch-boundary ordinal-ignore-case comparison.</summary>
    internal bool IsRelocated { get; }

    /// <summary>The decision-loop relocation flag, with the retained exact ordinal path comparison.</summary>
    internal bool IdentityMoved { get; }

    /// <summary>The exact filter membership result, or true when no reviewed filter is bound.</summary>
    internal bool TouchesFilter { get; }

    /// <summary>
    /// A descriptor that may enter the reviewed baseline decision loop. Excluded, coordinated and
    /// non-touching descriptors still contribute their complete move/alias paths to the handled set.
    /// </summary>
    internal bool IsEligible => !IsExcluded && !IsCoordinated && TouchesFilter;
}

/// <summary>
/// The one classification pass for one planning invocation. Each pure per-baseline predicate is
/// evaluated exactly once against the complete observation cut, and every evaluation is counted in the
/// invocation's work counters so a focused check can prove the classification is not repeated per use.
/// </summary>
internal static class SyncAuthorityPlanningClassification
{
    internal static SyncAuthorityPlanningBaselineContext Classify(SyncAuthoritySynchronizationPoint point,
        in SyncAuthorityPlanningClassificationInputs inputs, SyncAuthorityPlanningWorkCounters counters)
    {
        SyncAuthorityObservedEntry[] localMemberships =
            inputs.LocalByObject.TryGetValue(point.ObjectId, out var local) ? local : [];
        SyncAuthorityObservedEntry[] remoteMemberships =
            inputs.RemoteByObject.TryGetValue(point.ObjectId, out var remote) ? remote : [];
        counters.BaselineClassifications++;
        counters.LocalMembershipLookups++;
        counters.RemoteMembershipLookups++;
        // CS1628: capture the index once so the per-membership predicates never close over the in-parameter.
        var excludedPaths = inputs.ExcludedPaths;
        var isExcluded = AtOrUnder(point.LocalRelativePath, excludedPaths) ||
                         AtOrUnder(point.DropboxRelativePath, excludedPaths) ||
                         localMemberships.Any(item => AtOrUnder(item.RelativePath, excludedPaths)) ||
                         remoteMemberships.Any(item => AtOrUnder(item.RelativePath, excludedPaths));
        counters.ExclusionClassifications++;
        var isCoordinated = inputs.CoordinatedObjectReservations.Contains(point.ObjectId);
        counters.CoordinationClassifications++;
        var isAncestorUncertain = AtOrUnder(point.LocalRelativePath, inputs.LocalUncertainPaths) ||
                                  AtOrUnder(point.DropboxRelativePath, inputs.RemoteUncertainPaths) ||
                                  StrictUnder(point.LocalRelativePath, inputs.TypeConflictAncestors) ||
                                  StrictUnder(point.DropboxRelativePath, inputs.TypeConflictAncestors);
        counters.UncertaintyClassifications++;
        var isUncertain = inputs.InitialUncertainObjectIds.Contains(point.ObjectId) || isAncestorUncertain;
        var isRelocated = localMemberships.Length > 1 || remoteMemberships.Length > 1 ||
                          localMemberships.Any(item => !string.Equals(item.RelativePath,
                              point.LocalRelativePath, StringComparison.OrdinalIgnoreCase)) ||
                          remoteMemberships.Any(item => !string.Equals(item.RelativePath,
                              point.DropboxRelativePath, StringComparison.OrdinalIgnoreCase));
        counters.RelocationClassifications++;
        var identityMoved = localMemberships.Any(item => !string.Equals(item.RelativePath,
                                point.LocalRelativePath, StringComparison.Ordinal)) ||
                            remoteMemberships.Any(item => !string.Equals(item.RelativePath,
                                point.DropboxRelativePath, StringComparison.Ordinal));
        counters.IdentityMoveClassifications++;
        var filter = inputs.EffectPathFilter;
        var touchesFilter = filter is null ||
                            TouchesReviewedFilter(point, localMemberships, remoteMemberships, filter);
        counters.FilterClassifications++;
        return new SyncAuthorityPlanningBaselineContext(point, localMemberships, remoteMemberships,
            isExcluded, isCoordinated, isUncertain, isAncestorUncertain, isRelocated, identityMoved,
            touchesFilter);
    }

    /// <summary>
    /// The exact reviewed-selection membership test: the saved membership paths of this one baseline and
    /// every resolved membership alias of its object, against the normalized reviewed filter only.
    /// </summary>
    private static bool TouchesReviewedFilter(SyncAuthoritySynchronizationPoint point,
        SyncAuthorityObservedEntry[] localMemberships, SyncAuthorityObservedEntry[] remoteMemberships,
        IReadOnlySet<string> filter) =>
        point.LocalRelativePath is not null && filter.Contains(point.LocalRelativePath) ||
        point.DropboxRelativePath is not null && filter.Contains(point.DropboxRelativePath) ||
        localMemberships.Any(item => filter.Contains(item.RelativePath)) ||
        remoteMemberships.Any(item => filter.Contains(item.RelativePath));

    private static bool AtOrUnder(string? path,
        SyncAuthorityReconciliationPlanner.AncestorPathIndex ancestors) => ancestors.Contains(path, strict: false);

    private static bool StrictUnder(string? path,
        SyncAuthorityReconciliationPlanner.AncestorPathIndex ancestors) => ancestors.Contains(path, strict: true);
}

/// <summary>
/// The complete comparison universe of one planning invocation and the sparse reviewed selection over
/// it. The universe keeps the retained membership semantics (distinct, ordinal-ignore-case) of the full
/// local, remote, saved-baseline and uncertain path sets. A reviewed filter narrows only which universe
/// paths are sorted and decided: a selected path that is absent from the universe stays absent, and no
/// unselected path is dropped from the descendant, ancestor, alias or membership context.
/// </summary>
internal sealed class SyncAuthorityPlanningComparisonContext
{
    private readonly SyncAuthorityDescendantPathIndex _descendants;

    internal SyncAuthorityPlanningComparisonContext(IEnumerable<string> localPaths,
        IEnumerable<string> remotePaths, IEnumerable<string> baselinePaths,
        IEnumerable<string> localUncertainPaths, IEnumerable<string> remoteUncertainPaths,
        IReadOnlySet<string>? effectPathFilter, SyncAuthorityPlanningWorkCounters counters,
        CancellationToken cancellationToken)
    {
        var universe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        universe.UnionWith(localPaths);
        universe.UnionWith(remotePaths);
        universe.UnionWith(baselinePaths);
        universe.UnionWith(localUncertainPaths);
        universe.UnionWith(remoteUncertainPaths);
        Universe = universe;
        _descendants = new SyncAuthorityDescendantPathIndex(universe, cancellationToken);
        if (effectPathFilter is null)
        {
            Selected = universe.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        else
        {
            var selected = new List<string>(universe.Count);
            foreach (var path in universe)
            {
                counters.FilterMembershipTests++;
                if (effectPathFilter.Contains(path)) selected.Add(path);
            }
            selected.Sort(StringComparer.OrdinalIgnoreCase);
            Selected = selected.ToArray();
        }
        counters.ComparisonUniversePaths += universe.Count;
        counters.SelectedComparisonPaths += Selected.Count;
        counters.LexicalSortEntries += Selected.Count;
        counters.UnselectedComparisonPaths += universe.Count - Selected.Count;
    }

    /// <summary>The complete comparison universe, never reduced to the reviewed selection.</summary>
    internal IReadOnlySet<string> Universe { get; }

    /// <summary>The ordinal-ignore-case sorted paths this invocation decides.</summary>
    internal IReadOnlyList<string> Selected { get; }

    /// <summary>Universe paths outside the reviewed selection, accounted without being visited.</summary>
    internal int UnselectedPathCount => Universe.Count - Selected.Count;

    /// <summary>
    /// True when a strict descendant of this exact path exists in the complete universe. The batch
    /// dependency boundary is answered from the indexed universe, so a sparse run never needs a full
    /// lexical sort of unselected paths merely to skip them.
    /// </summary>
    internal bool HasDescendant(string? path) => _descendants.Contains(path);
}

/// <summary>
/// Exact strict-descendant membership for one path set, with the retained whole-slash-boundary and
/// ordinal-ignore-case semantics. It records the proper slash-delimited ancestors of every path once,
/// so a lookup depends on path depth rather than on the size of the set.
/// </summary>
internal sealed class SyncAuthorityDescendantPathIndex
{
    private readonly HashSet<string> _ancestors = new(StringComparer.OrdinalIgnoreCase);

    internal SyncAuthorityDescendantPathIndex(IEnumerable<string> paths,
        CancellationToken cancellationToken = default)
    {
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(path)) continue;
            for (var slash = path.LastIndexOf('/'); slash >= 0; slash = path.LastIndexOf('/', slash - 1))
            {
                _ancestors.Add(path[..slash]);
                if (slash == 0) break;
            }
        }
    }

    /// <summary>True when some indexed path is a strict slash-boundary descendant of this exact path.</summary>
    internal bool Contains(string? path) => path is not null && _ancestors.Contains(path);
}

/// <summary>
/// Bounded, secret-free work counters for exactly one planning invocation. Production code only
/// increments them; no counter is ever read to choose a decision, an identity, an ordering or an
/// effect. A focused check observes them to prove each baseline classification runs once and that the
/// sparse reviewed selection bounds the lexical and decision work.
/// </summary>
internal sealed class SyncAuthorityPlanningWorkCounters
{
    /// <summary>Saved synchronization points classified in the single preparation pass.</summary>
    internal int BaselineClassifications { get; set; }

    internal int LocalMembershipLookups { get; set; }

    internal int RemoteMembershipLookups { get; set; }

    internal int ExclusionClassifications { get; set; }

    internal int CoordinationClassifications { get; set; }

    internal int UncertaintyClassifications { get; set; }

    internal int RelocationClassifications { get; set; }

    internal int IdentityMoveClassifications { get; set; }

    internal int FilterClassifications { get; set; }

    /// <summary>Descriptors ordered for the baseline decision loop.</summary>
    internal int BaselineSortEntries { get; set; }

    /// <summary>Baselines handled for their complete move paths without entering the decision loop.</summary>
    internal int EligibilitySkips { get; set; }

    /// <summary>Baselines that entered the expensive baseline decision loop.</summary>
    internal int EligibleDecisionEntries { get; set; }

    /// <summary>Baselines whose relocation boundary was reused from the classified descriptor.</summary>
    internal int RelocatedBoundaryReuses { get; set; }

    /// <summary>Complete comparison universe paths of this invocation.</summary>
    internal int ComparisonUniversePaths { get; set; }

    /// <summary>Universe paths the reviewed selection decided.</summary>
    internal int SelectedComparisonPaths { get; set; }

    /// <summary>Paths passed to a lexical sort of the comparison selection.</summary>
    internal int LexicalSortEntries { get; set; }

    /// <summary>Universe paths outside the reviewed selection.</summary>
    internal int UnselectedComparisonPaths { get; set; }

    /// <summary>Reviewed filter membership tests performed over the complete universe.</summary>
    internal int FilterMembershipTests { get; set; }

    /// <summary>Unselected paths accounted for progress in one step instead of a per-path skip.</summary>
    internal int UnselectedProgressAdvances { get; set; }

    internal int SelectedPathsVisited { get; set; }

    internal int SelectedPathsSkipped { get; set; }

    internal int ComparisonPathsDecided { get; set; }

    internal int BatchDrains { get; set; }

    internal int BatchedPaths { get; set; }
}

/// <summary>
/// The one test-only observation of a completed planning context. Production installs no callback, and
/// the counters it reports never influence a decision, an identity, an effect or an ordering.
/// </summary>
internal static class SyncAuthorityPlanningContextObservation
{
    internal static Action<SyncAuthorityPlanningWorkCounters>? CompletedForTest { get; set; }
}
