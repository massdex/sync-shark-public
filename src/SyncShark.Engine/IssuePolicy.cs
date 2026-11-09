using System.Runtime.CompilerServices;

namespace Massdex.SyncShark.Engine;

/// <summary>Pure issue policy for holds, inherited kinds, unreadability and retirement from configured scope or
/// trusted scan facts, shared by Storage, Run and the Executor.</summary>
public static class IssuePolicy
{
    /// <summary>The safety consequence of hiding a notice; visibility never changes this policy.</summary>
    public static TextKey? IgnoreConsequence(IssueKind kind, TextKey cause, IssueSubject subject) => kind switch
    {
        IssueKind.DriveChange or IssueKind.RootIdentity => TextKey.ResolutionIgnorePairConsequence,
        _ when Holds(kind, cause, subject) => TextKey.ResolutionIgnoreHeldConsequence,
        _ => null
    };

    // shortcut: transitional for the cross-lane seam, removed at 0.3.82 integration
    public static TextKey? IgnoreConsequence(IssueKind kind, TextKey cause) => kind switch
    {
        IssueKind.DriveChange or IssueKind.RootIdentity => TextKey.ResolutionIgnorePairConsequence,
        _ when Holds(kind, cause) => TextKey.ResolutionIgnoreHeldConsequence,
        _ => null
    };

    /// <summary>Recognizes only the current actionable occupied-name clash for one acquired incoming identity.</summary>
    public static bool RenameClashMatches(Issue issue, Side side, string path, string identity) =>
        issue is { Kind: IssueKind.NameClash, Cause: TextKey.ClashNameTaken, Ignored: false, Dismissed: false } &&
        issue.Subject.Incoming == side && issue.Subject.Items.Any(item => item.Side == side &&
            item.Id == identity && PathNames.KeyFor(side, item.Path) == PathNames.KeyFor(side, path));
    /// <summary>An incoming clash needs a binding to its incoming identity without the captured occupant, or real
    /// two-sided absence; a result at the same name alone has no closure authority (decisions 84/88).</summary>
    public static bool ScopedCloseAllowed(Issue issue, SyncedEntry? binding, bool absent)
    {
        if (issue is not { Kind: IssueKind.NameClash, Subject.Incoming: { } side } || absent) return true;
        var items = issue.Subject.Items;
        var incoming = items.Count;
        for (var index = 0; index < items.Count && incoming == items.Count; index++)
            if (items[index].Side == side) incoming = index;
        if (incoming == items.Count) return true;
        if (binding is null || binding.IdOn(side) != items[incoming].Id) return false;
        var other = PathNames.Other(side);
        for (var index = 0; index < items.Count; index++)
            // Pairing the incoming item with the occupant is the clash itself, not its resolution.
            if (items[index].Side == other && items[index].Id == binding.IdOn(other) &&
                PathNames.Same(items[index].Path, issue.Path)) return false;
        return true;
    }

    /// <summary>Identifies the explicit Retry choices whose scope is a full-pair Repair.</summary>
    public static bool RetryRunsRepair(Issue? issue, ResolutionChoice choice) =>
        choice == ResolutionChoice.Retry && issue?.Cause == TextKey.FailFolderDependencyCycle;

    public static bool UnbaselinedLocal(Row row) =>
        row.Synced is null && row.Dropbox is null && row.Local is { Skip: null };

    public static bool SameFirstRunLocal(Entry? original, Entry? current) =>
        original is not null && MatchesFirstRunLocal(original.Path, original.Id, current);

    private static bool MatchesFirstRunLocal(string path, string id, Entry? current) =>
        current is not null && string.Equals(path, current.Path, StringComparison.Ordinal) &&
        string.Equals(id, current.Id, StringComparison.Ordinal);

    /// <summary>Indexes retained failure subjects separately from conflict hash proofs.</summary>
    public static void AddFirstRunSubject(IDictionary<string, IssueSubject> subjects, Issue issue)
    {
        if (issue.Kind != IssueKind.Failure ||
            issue.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None) return;
        foreach (var item in issue.Subject.Items)
            if (item.Side == Side.Local) subjects[PathNames.Key(item.Path)] = issue.Subject;
    }

    /// <summary>Finds only subtree obligations; an individual item never enrolls descendants.</summary>
    public static IEnumerable<IssueSubject> CoveringSubtrees(IReadOnlyDictionary<string, IssueSubject> subjects,
        string key)
    {
        if (subjects.TryGetValue(key, out var own) &&
            own.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.UnscannedSubtree) yield return own;
        foreach (var ancestor in PathNames.Ancestors(key))
            if (subjects.TryGetValue(ancestor, out var subject) &&
                subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.UnscannedSubtree) yield return subject;
    }

    /// <summary>Projects exact retained identity, or a child of an already validated subtree.</summary>
    public static IssueSubject.FirstRunDeletionKind FirstRunKind(Row row,
        IReadOnlyDictionary<string, IssueSubject> subjects, bool underRetainedSubtree)
    {
        if (subjects.TryGetValue(row.Key, out var subject))
            foreach (var item in subject.Items)
                if (item.Side == Side.Local && MatchesFirstRunLocal(item.Path, item.Id, row.Local))
                    return subject.FirstRunDeletion;
        return underRetainedSubtree && UnbaselinedLocal(row)
            ? IssueSubject.FirstRunDeletionKind.Item : IssueSubject.FirstRunDeletionKind.None;
    }

    public static bool InFirstRunScope(Row row, bool firstRepairPending, IssueSubject.FirstRunDeletionKind kept) =>
        UnbaselinedLocal(row) && kept != IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
        (firstRepairPending || kept != IssueSubject.FirstRunDeletionKind.None);

    /// <summary>Invalidates prepared history on reobservation without lending an old subject's authority to a replacement.</summary>
    public static Row ReobserveFirstRun(Row original, Row observed)
    {
        var same = SameFirstRunLocal(original.Local, observed.Local);
        var kept = same ? original.Context.FirstRunDeletion : IssueSubject.FirstRunDeletionKind.None;
        var eligible = UnbaselinedLocal(observed) &&
            kept != IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice &&
            (same ? original.Context.DeletionEligible : original.Context.FirstRun);
        return observed with
        {
            Deletion = null,
            Context = observed.Context with
            {
                FirstRunDeletion = kept,
                DeletionEligible = eligible,
                CompleteInventory = same && observed.Context.CompleteInventory
            }
        };
    }

    /// <summary>The kind and cause of a failure that blocks an exclusion holding protected items.</summary>
    public static (IssueKind Kind, TextKey Cause) ProtectedExclusion { get; } =
        (IssueKind.Failure, TextKey.FailExcludeHoldsProtected);

    /// <summary>The kind of the notice holding a row (2.4): the row's key, then its saved Local key, then its saved
    /// Dropbox key, then observed and missing-side mapped locations. Earlier coverage takes precedence.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IssueKind? HeldKind(Row row, PrefixSet heldKeys, IReadOnlyDictionary<string, IssueKind> heldKinds)
    {
        var key = CoveredKey(row, heldKeys);
        return key is null ? null : InheritedKind(heldKinds, key);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string? CoveredKey(Row row, PrefixSet keys) =>
        FirstCovered(row, keys, static (set, key) => set.Covers(key) ? key : null);

    internal static TResult? FirstCovered<T, TResult>(Row row, T state, Func<T, string, TResult?> covering)
        where TResult : class
    {
        var keys = HoldKeys(row);
        return covering(state, keys.Key) ??
            (keys.Local is { } local ? covering(state, local) : null) ??
            (keys.Dropbox is { } dropbox ? covering(state, dropbox) : null) ??
            (keys.ObservedLocal is { } observedLocal ? covering(state, observedLocal) : null) ??
            (keys.ObservedDropbox is { } observedDropbox ? covering(state, observedDropbox) : null) ??
            (keys.DestinationLocal is { } destinationLocal ? covering(state, destinationLocal) : null) ??
            (keys.DestinationDropbox is { } destinationDropbox ? covering(state, destinationDropbox) : null);
    }

    internal static (string Key, string? Local, string? Dropbox, string? ObservedLocal,
        string? ObservedDropbox, string? DestinationLocal, string? DestinationDropbox) HoldKeys(Row row) =>
        (row.Key, row.Synced?.LocalKey, row.Synced?.DropboxKey,
            AdditionalHoldKey(row, Side.Local, row.Local?.Path),
            AdditionalHoldKey(row, Side.Dropbox, row.Dropbox?.Path),
            row.Local is null ? AdditionalHoldKey(row, Side.Local, row.Context?.Destination) : null,
            row.Dropbox is null ? AdditionalHoldKey(row, Side.Dropbox, row.Context?.Destination) : null);

    private static string? AdditionalHoldKey(Row row, Side side, string? path) =>
        path is null || PathNames.Same(row.Key, path) || PathNames.Same(row.Synced?.LocalKey, path) ||
        PathNames.Same(row.Synced?.DropboxKey, path) ? null : PathNames.KeyFor(side, path);

    /// <summary>The subject keys held by an issue; subject-less Failures name both sides.</summary>
    public static IEnumerable<string> HeldKeys(Issue issue)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        AddHeldKeys(issue.Kind, issue.Path, issue.Subject, keys, static (set, key) => set.Add(key));
        return keys;
    }

    internal static void AddHeldKeys<T>(IssueKind kind, string path, IssueSubject subject,
        T state, Action<T, string> add)
    {
        var unknownMove = UnknownMove(kind, subject);
        if (kind == IssueKind.Failure && (subject.Items.Count == 0 || unknownMove))
        {
            add(state, PathNames.KeyFor(Side.Local, path));
            add(state, PathNames.KeyFor(Side.Dropbox, path));
        }
        if (unknownMove)
        {
            var move = subject.Move!;
            add(state, PathNames.KeyFor(Side.Dropbox, move.Source));
            add(state, PathNames.KeyFor(Side.Dropbox, move.Destination));
            if (move.Intermediate is { } intermediate) add(state, PathNames.KeyFor(Side.Dropbox, intermediate));
        }
        foreach (var item in subject.Items) add(state, PathNames.KeyFor(item.Side, item.Path));
    }

    /// <summary>Recognizes the stored move responsibility whose footprint stays held until by-ID settlement.</summary>
    internal static bool UnknownMove(IssueKind kind, IssueSubject subject) =>
        kind == IssueKind.Failure && subject.Move is not null &&
        subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None;

    /// <summary>Includes unknown-move responsibility in the existing cause-based holding policy.</summary>
    public static bool Holds(IssueKind kind, TextKey cause, IssueSubject subject) =>
        UnknownMove(kind, subject) || Holds(kind, cause);

    /// <summary>Only settled move responsibility can release its footprint, even for an explicit Retry.</summary>
    internal static bool Exempt(IssueKind kind, IssueSubject subject, long id,
        IReadOnlySet<long>? retryIssues, long? selectedIssueId = null) =>
        (id == selectedIssueId || Retried(kind, id, retryIssues));

    /// <summary>Names only an open actionable issue by identity, independently of its current path.</summary>
    internal static bool OwnsItem(Issue issue, Side side, string id) =>
        !issue.Dismissed && issue.Kind is IssueKind.Failure or IssueKind.NameClash or IssueKind.Tie &&
        issue.Subject.Items.Any(item => item.Side == side && item.Id == id);

    /// <summary>Classifies holds centrally so storage and in-memory callers use the same cause rules.</summary>
    public static bool Holds(IssueKind kind, TextKey cause) => kind switch
    {
        IssueKind.Failure => cause is TextKey.FailDropboxNameRejected or TextKey.FailDropboxNoPermission,
        IssueKind.Tie or IssueKind.NameClash =>
            cause is not (TextKey.ClashMappedNames or TextKey.ClashMappedDiffers),
        _ => false
    };

    /// <summary>Kinds admitted to the stored projection, derived once from the holding policy.</summary>
    public static IReadOnlyList<IssueKind> HoldingKinds { get; } = Array.AsReadOnly(
        Enum.GetValues<IssueKind>().Where(kind => Enum.GetValues<TextKey>().Any(cause => Holds(kind, cause))).ToArray());

    /// <summary>An explicit Retry omits only its selected Failure, never another notice kind.</summary>
    // shortcut: transitional for the cross-lane seam, removed at 0.3.82 integration
    public static bool Retried(IssueKind kind, long issueId, IReadOnlySet<long>? retryIssues) =>
        Retried(kind, issueId, static id => id, retryIssues);

    // shortcut: transitional for the cross-lane seam, removed at 0.3.82 integration
    internal static bool Retried<T>(IssueKind kind, T state, Func<T, long> issueId,
        IReadOnlySet<long>? retryIssues) =>
        kind == IssueKind.Failure && retryIssues is not null && retryIssues.Contains(issueId(state));

    /// <summary>Ordinary races permit diagnostic replay; deliberate deletion-evidence refusals do not.</summary>
    internal static bool CanRetryChanged(ChangedUnderneathException error) => error is not DeletionEvidenceRefusedException;

    /// <summary>Fresh provider observations and the saved binding determine whether diagnostic evidence changed.</summary>
    internal static bool SameStaleEvidence(Row previous, Row current) =>
        previous.Local == current.Local && previous.Dropbox == current.Dropbox && previous.Synced == current.Synced;

    /// <summary>A live item that changes again after its diagnostic retry waits for its next acquisition.</summary>
    internal static bool StaleRetryWaits(bool retried, bool liveSelection) => retried && liveSelection;

    /// <summary>A stale retry fails when its evidence is unchanged; only a live selection may wait on further changes.</summary>
    public static bool StaleRetryFailed(bool reobserved, bool unchanged, bool liveSelection) =>
        unchanged || reobserved && !liveSelection;

    /// <summary>The local access diagnoses eligible for opportunity-driven item retry, not permanent holds.</summary>
    public static bool IsLocalAccessRetryCause(TextKey cause) =>
        cause is TextKey.FailLocalInUse or TextKey.FailLocalInUseOrAccessDenied;

    /// <summary>Distinguishes unreadability from ordinary skips that retain enclosing-delete semantics.</summary>
    public static bool ProtectsSubtree(string? skip) => skip == ProtectedSkipCause();

    /// <summary>The one skip cause that protects a subtree, passed to SQL as a value so no statement restates it.</summary>
    public static string ProtectedSkipCause() => nameof(TextKey.SkipUnreadable);

    // One retirement decision: configured scope, applied exclusions and the subject walk answer one question from one set of scan facts.
    /// <summary>Retires an old Failure, Tie or NameClash only on configured scope or trusted scan facts;
    /// untrusted absence proves nothing. The configured scope ignores a pending exclusion, which retires nothing
    /// until it is applied (2.4), and is derived from <paramref name="scope"/> once for that scope's lifetime;
    /// the caller supplies each side's original path through <paramref name="scanEntry"/>.
    /// </summary>
    public static bool Obsolete(
        Issue issue,
        Func<Side, string, Entry?> scanEntry,
        PrefixSet localSkipped,
        PrefixSet dropboxSkipped,
        PrefixSet appliedExclusions,
        PrefixSet pendingExclusions,
        PathScope scope,
        bool retainedByOwner)
    {
        if (retainedByOwner || !IssueClosure.AfterHeldInSync.Contains(issue.Kind)) return false;
        // Asked only for an issue that can retire; the scope derives it once and keeps it.
        var configuredScope = scope.Configured;
        if (Covered(Outside) || Covered(AppliedExclusion)) return true;
        if (UnknownMove(issue.Kind, issue.Subject)) return false;
        var localOnly = issue.Kind == IssueKind.Failure &&
            issue.Subject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None;

        foreach (var item in issue.Subject.Items)
        {
            if (localOnly && item.Side == Side.Dropbox) continue;
            if (Untrusted(item.Side, item.Path)) continue;
            var entry = scanEntry(item.Side, item.Path);
            if (entry?.Skip is not null) continue;
            if (entry is null || entry.Path != item.Path || entry.Id != item.Id) return true;
        }

        return issue.Kind == IssueKind.Failure && issue.Subject.Items.Count == 0 &&
            !Untrusted(Side.Local, issue.Path) && !Untrusted(Side.Dropbox, issue.Path) &&
            scanEntry(Side.Local, issue.Path) is null && scanEntry(Side.Dropbox, issue.Path) is null;

        bool Skipped(Side side, string path) =>
            (side == Side.Local ? localSkipped : dropboxSkipped).Covers(PathNames.KeyFor(side, path));

        bool Excluded(PathScope at, Side side, string path, bool folder) =>
            at.Excludes(side == Side.Local ? path : null, side == Side.Dropbox ? path : null, folder);

        bool ExcludedAt(PathScope at, Side side, string path)
        {
            // A missing counterpart does not make a known folder eligible for file-only ignores (2.4).
            var entry = scanEntry(side, path) ??
                scanEntry(side == Side.Local ? Side.Dropbox : Side.Local, path);
            return entry is not null ? Excluded(at, side, path, entry.IsFolder)
                : Excluded(at, side, path, false) || Excluded(at, side, path, true);
        }

        bool Outside(Side side, string path) => !Skipped(Side.Local, path) && !Skipped(Side.Dropbox, path) &&
            !pendingExclusions.Covers(PathNames.KeyFor(side, path)) &&
            ExcludedAt(configuredScope, side, path);

        bool Untrusted(Side side, string path) => Skipped(side, path) || ExcludedAt(scope, side, path);

        bool AppliedExclusion(Side side, string path) => appliedExclusions.Covers(PathNames.KeyFor(side, path));

        bool Covered(Func<Side, string, bool> contains) =>
            contains(Side.Local, issue.Path) || contains(Side.Dropbox, issue.Path) ||
            issue.Subject.Items.Count != 0 && issue.Subject.Items.All(item => contains(item.Side, item.Path));
    }

    /// <summary>The key's own recorded kind, else its nearest recorded ancestor's: O(depth) rather than a scan of
    /// every held key per row. A prefix with no recorded kind counts as Tie (2.4).</summary>
    public static IssueKind InheritedKind(IReadOnlyDictionary<string, IssueKind> kinds, string key)
    {
        if (kinds.TryGetValue(key, out var kind)) return kind;
        _ = key.Length; // a null key must throw NullReferenceException, as the old string search did
        // Walk up at path boundaries; never test the empty root key.
        for (var boundary = PathNames.LastSeparator(key.AsSpan()); boundary > 0;
             boundary = PathNames.LastSeparator(key.AsSpan()[..boundary]))
        {
            if (kinds.TryGetValue(key[..boundary], out kind)) return kind;
        }
        return IssueKind.Tie;
    }
}
