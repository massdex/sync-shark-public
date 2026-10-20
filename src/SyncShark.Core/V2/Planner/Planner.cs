using Draft = System.Collections.Generic.List<(Massdex.SyncShark.V2.PlanRow Row, Massdex.SyncShark.V2.SyncAction Action)>;

namespace Massdex.SyncShark.V2;

/// <summary>Pure decision-table planner orchestration, shared emissions and small helpers (no I/O, no clock).</summary>
public static partial class Planner
{
    // Failure-injection seam, isolated to the caller's async flow; null in production.
    internal static readonly AsyncLocal<Action?> PostPassFault = new();
    /// <summary>Decides the actions and notices for one pair generation from its joined rows.</summary>
    public static Plan Decide(string pairId, long gen, IReadOnlyList<PlanRow> rows, PlanOptions o)
    {
        if (o.TieWindow < TimeSpan.Zero)
            throw new ArgumentException("Planner: the tie window cannot be negative.", nameof(o));
        if (o.Label is null)
            throw new ArgumentException("Planner: a label rule is required.", nameof(o));

        var ctx = BuildDraft(pairId, rows, o);
        ctx.Draft.Sort((left, right) => CompareActions(left.Action, right.Action));
        var actions = new List<SyncAction>(ctx.Draft.Count);
        foreach (var (row, action) in ctx.Draft)
        {
            // Draft-owned actions have not escaped. Finalize only after all id-indexed post-passes,
            // without allocating a second record for every effect in a million-item plan.
            action.FinalizeDraft(PlanIds.ActionId(pairId, gen, action.Rule, action.Kind, action.Target, action.Path),
                action.Kind == ActionKind.Delete && Cur(row, Opposite(action.Target)) is null);
            actions.Add(action);
        }
        // Link only surviving effects, after post-passes and final action IDs. Recovery is keyed
        // to this exact effect, never to whichever version happens to occupy the path later.
        var retained = new Dictionary<string, RetainedNoticeVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in actions)
            if (action.Rule == 4 && action.Kind == ActionKind.Update &&
                action.ExpectedTarget is { } loser && action.Source is { } winner)
                retained[action.Path] = new RetainedNoticeVersion(action.ActionId, action.Target, loser, winner);
        for (var i = 0; i < ctx.Notices.Count; i++)
        {
            var notice = ctx.Notices[i];
            if (notice.Rule == 4 && notice.Kind == NoticeKind.AutoConflict &&
                retained.TryGetValue(notice.Path, out var version))
                ctx.Notices[i] = notice with { RetainedVersion = version };
        }
        return new Plan(pairId, gen, actions, ctx.Notices) { DeferredPlanningActions = ctx.DeferredPlanning };
    }

    /// <summary>The table plus post-passes B, A and C and the held-folder drop; the context keeps notices and hash needs.</summary>
    private static Context BuildDraft(string pairId, IReadOnlyList<PlanRow> rows, PlanOptions o)
    {
        var ordered = OrderRows(rows);
        var ctx = new Context(pairId, o, ordered, Occupancy(ordered, Side.A), Occupancy(ordered, Side.B));
        foreach (var row in ordered)
        {
            try
            {
                var start = ctx.Draft.Count;
                Dispatch(ctx, row);
                if (ctx.Draft.Skip(start).Any(entry => ReadsOwnedPointer(entry.Action)))
                {
                    ctx.TruncateDraft(start);
                    ctx.Notices.RemoveAll(notice => PairPath.Same(notice.Path, PrimaryPath(row)) &&
                        notice.Kind is NoticeKind.KeepBoth or NoticeKind.AutoConflict or NoticeKind.EditBeatsDelete);
                    HoldPlanningFailure(ctx, row, new InvalidOperationException(PointerReadPending));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { HoldPlanningFailure(ctx, row, error); }
        }
        var original = ctx.Draft.ToArray();
        try
        {
            ctx.Draft = PostFolderDeletes(ctx, ordered);   // on the rows' own paths, before any rebase
            PostPassFault.Value?.Invoke();
            ctx.Draft = PostMoves(ctx);
            ctx.Draft = PostCollisions(ctx);
            ctx.Draft = DropHeld(ctx);
            ctx.Draft = AppendRebuildableDeletes(ctx);
            ctx.Draft = BlockHeldFolderDeletes(ctx);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // Discard every post-pass output, including partial rebase/collapse state. Only
            // ordinary file creates into an observed empty target can stand independently.
            bool Safe((PlanRow Row, SyncAction Action) entry) => entry.Action is
                { Kind: ActionKind.CopyNew, ExpectedTarget: null, FromPath: null, RecoveryFirst: false, Source.Kind: ItemKind.File } &&
                Cur(entry.Row, entry.Action.Target) is null &&
                !(entry.Action.Target == Side.A ? ctx.OccA : ctx.OccB).ContainsKey(entry.Action.Path) &&
                PairPath.Same(entry.Action.Path, entry.Action.Source!.Path) && !ctx.Mapping.Protects(entry.Action);
            ctx.DeferredPlanning.AddRange(original.Where(entry => !Safe(entry)).Select(entry => entry.Action));
            var held = LinkExclusions.None.WithPending(ctx.DeferredPlanning.Select(action => action.Path));
            ctx.Draft = original.Where(entry => Safe(entry) && !held.Covers(entry.Action.Path) && !held.Contains(entry.Action.Path)).ToList();
            var kept = ctx.Draft.Select(entry => entry.Action).ToHashSet();
            ctx.DeferredPlanning.Clear();
            ctx.DeferredPlanning.AddRange(original.Where(entry => !kept.Contains(entry.Action)).Select(entry => entry.Action));
            ctx.Notices.RemoveAll(notice => notice.Kind is NoticeKind.KeepBoth or NoticeKind.AutoConflict or NoticeKind.EditBeatsDelete);
            ctx.Notices.Add(new(pairId, 40, NoticeKind.Info, "",
                "Planning issue: deletions and moves paused this run; Sync Shark will retry. Details: " + error.Message,
                "planning-degraded"));
        }
        for (var i = 0; i < ctx.Draft.Count; i++)
        {
            var (row, action) = ctx.Draft[i];
            if (!ctx.Mapping.Protects(action)) continue;
            ctx.Draft[i] = (row, action with { Kind = ActionKind.Hold, RecoveryFirst = false });
            AddNotice(ctx, 30, NoticeKind.UserDecision, action.Path, MappingAmbiguous);
        }
        // Collision post-passes can turn a metadata-only move into a copy. Hold the whole
        // row (including Forget/Delete siblings), not just its unsafe half.
        var pointerReads = ctx.Draft.Where(entry => ReadsOwnedPointer(entry.Action))
            .Select(entry => entry.Row).ToHashSet();
        foreach (var row in pointerReads)
            HoldPlanningFailure(ctx, row, new InvalidOperationException(PointerReadPending));
        var failures = LinkExclusions.None.WithPending(ctx.FailedPaths);
        bool touches(string path) => failures.Covers(path) || failures.Contains(path);
        ctx.Draft = ctx.Draft.Where(entry => !touches(entry.Action.Path) &&
            (entry.Action.FromPath is null || !touches(entry.Action.FromPath)) &&
            (entry.Action.Source is null || !touches(entry.Action.Source.Path))).ToList();
        return ctx;
    }

    private const string PointerReadPending =
        "This operation needs online-only content; open the file while connected to download it, then retry";

    private static bool IsOwnedPointer(ItemState? item) => item?.Flags.HasFlag(ItemFlags.OwnedPointer) == true;

    private static bool ReadsOwnedPointer(SyncAction action) =>
        !OwnedPointerUpdates.IsMetadataUpdate(action) &&
        (action.Kind is ActionKind.CopyNew or ActionKind.Update or ActionKind.KeepBoth &&
            (IsOwnedPointer(action.Source) || IsOwnedPointer(action.ExpectedTarget)) ||
        action.RecoveryFirst && IsOwnedPointer(action.ExpectedTarget));

    private static void HoldPlanningFailure(Context ctx, PlanRow row, Exception error)
    {
        foreach (var path in new[] { row.A?.Path, row.B?.Path, row.Base?.A.Path, row.Base?.B.Path }
            .Where(path => path is not null).Select(path => path!).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty(string.Empty))
        {
            ctx.FailedPaths.Add(path);
            var notice = new Notice(ctx.PairId, 40, NoticeKind.Info, path,
                "Planning could not complete: " + error.Message + ". Retry after correcting this item, or Ignore to exclude it from sync.", "sync-retry");
            if (ctx.NoticeKeys.Add(notice)) ctx.Notices.Add(notice);
        }
    }

    /// <summary>Classifies one row and applies the pre-table checks (rules 30, 31, 32 and no-evidence).</summary>
    private static void Dispatch(Context ctx, PlanRow row)
    {
        if (((row.A?.Flags ?? ItemFlags.None) | (row.B?.Flags ?? ItemFlags.None)).HasFlag(ItemFlags.Quarantined))
        {
            var target = row.A?.Flags.HasFlag(ItemFlags.Quarantined) == true ? Side.A : Side.B;
            var notice = new Notice(ctx.PairId, 30, NoticeKind.UserDecision, PrimaryPath(row),
                "Antivirus hold; explicit retry required.", QuarantineFailure.NoticeKey,
                Quarantine: new(target, target == Side.A ? row.B : row.A, target == Side.A ? row.A : row.B, true));
            Emit(ctx, row, 30, ActionKind.Hold, target, notice.Path, null, null, null, false, null);
            if (ctx.NoticeKeys.Add(notice)) ctx.Notices.Add(notice);
            return;
        }
        // A rebuildable folder the user chose to stop syncing (docs/design/rebuildables.md) is inert on both sides;
        // only the folder's single cloud delete (AppendRebuildableDeletes) touches it.
        if (ctx.UnderRebuildable(row.A?.Path) || ctx.UnderRebuildable(row.B?.Path))
            return;
        var sA = ClassifySide(row.A, row.Base?.A, row.Ev.DeletedA, row.Ev.KnownA);
        var sB = ClassifySide(row.B, row.Base?.B, row.Ev.DeletedB, row.Ev.KnownB);
        if (sA == RowState.Ignored || sB == RowState.Ignored)
            return;                                                                       // rule 31
        if (row.NameMappingCollision)
        {
            Hold(ctx, row, 30, MappingAmbiguous);
            foreach (var item in new[] { row.A, row.B, row.Base?.A, row.Base?.B })
                if (item is not null)
                {
                    AddNotice(ctx, 30, NoticeKind.UserDecision, item.Path, MappingAmbiguous);
                    AddNotice(ctx, 30, NoticeKind.UserDecision, DropboxNameMapping.MatchPath(item), MappingAmbiguous);
                }
            return;
        }
        // Owned pointers participate in identity/content comparison and structural sync.
        // Only foreign placeholders use rule 35; content-reading effects are guarded separately.
        if (!IsOwnedPointer(row.A) && !IsOwnedPointer(row.B) && TryStub(ctx, row))
            return;                                                                       // rule 35 (before choices)
        if (sA == RowState.Unreadable || sB == RowState.Unreadable)
        {
            AddNotice(ctx, 30, NoticeKind.Info, PrimaryPath(row), PlanNotices.CannotRead); // rule 30
            return;
        }
        // a path the reviewed prepare saw on both sides is ordinary ongoing sync when it becomes
        // one-sided after the prepare, so it is compared BEFORE the first-sync winners and one-sided choices. Safety
        // and rebuildable exclusions already returned above.
        if (TryPreparedMatch(ctx, row))
            return;
        if (ctx.Winners.Count > 0 && ctx.Winners.TryGetValue(PrimaryPath(row), out var winner))
        {
            ApplyWinner(ctx, row, row.Base?.ObjectKey, winner);                           // rule 32
            return;
        }
        if (TryOneSided(ctx, row))
            return;                                                                       // rule 32 (one-sided choice)
        if (row.Base is not null && (sA == RowState.Unknown || sB == RowState.Unknown))
            return;                                                                       // no evidence -> nothing
        if (row.Base is not null)
            FromBaseline(ctx, row, sA, sB);
        else
            WithoutBaseline(ctx, row, sA, sB);
    }

    /// <summary>Drops the actions of rows under a rule-29 held folder (step 8).</summary>
    private static Draft DropHeld(Context ctx) => ctx.Held.Count == 0
        ? ctx.Draft
        : ctx.Draft.Where(d => !ctx.Held.Any(folder => TouchesUnder(d.Row, folder))).ToList();

    /// <summary>
    /// (third-review finding 1): a held/pending path is hidden from the planner's rows, but the
    /// Executor expands a folder Delete over the live store, so an ordinary folder Delete would remove the hidden
    /// child without review. While any held path is under the folder (or an ancestor), the delete becomes a Hold with
    /// a pending-evidence notice; the folder stays until the held child is resolved and a later run handles it.
    /// Reviewed choice deletes are excluded: their expansion is already checked against the prepared victim set.
    /// </summary>
    private static Draft BlockHeldFolderDeletes(Context ctx)
    {
        if (ctx.Options.HeldPaths is not { Count: > 0 } && ctx.Options.ProtectedDescendants is null) return ctx.Draft;
        for (var index = 0; index < ctx.Draft.Count; index++)
        {
            var (row, action) = ctx.Draft[index];
            if (action.Kind != ActionKind.Delete || action.ExpectedTarget?.Kind != ItemKind.Folder ||
                !HeldUnder(ctx, action))
                continue;
            ctx.Draft[index] = (row, action with { Kind = ActionKind.Hold, RecoveryFirst = false });
            AddNotice(ctx, action.Rule, NoticeKind.Info, action.Path, PlanNotices.DeletionEvidencePending);
        }
        return ctx.Draft;
    }

    /// <summary>
    /// True when a non-choice Delete has a held/pending path under it. Shared by the rule-23 collapse (such a folder is
    /// never collapsed over, so its ordinary child deletes are kept) and <see cref="BlockHeldFolderDeletes"/>.
    /// </summary>
    private static bool HeldUnder(Context ctx, SyncAction delete) =>
        !delete.FirstSyncChoice &&
        (ctx.Options.ProtectedDescendants?.Invoke(delete.Path) == true || ctx.HeldParents.Contains(delete.Path));

    // ---- shared emissions ----------------------------------------------------

    /// <summary>A new action with no id yet.</summary>
    private static SyncAction NewAction(int rule, ActionKind kind, Side target, string path, string? from,
        ItemState? source, ItemState? expected, bool recoveryFirst, string? key) =>
        new(string.Empty, rule, kind, target, path, from, source, expected, recoveryFirst, key);

    private static void Emit(Context ctx, PlanRow row, int rule, ActionKind kind, Side target, string path,
        string? from, ItemState? source, ItemState? expected, bool recoveryFirst, string? key,
        bool choiceRemove = false, bool rebuildableRemove = false, bool firstSyncChoice = false,
        bool needsReview = false) =>
        Append(ctx, ctx.Draft, row, new SyncAction("s" + ctx.Draft.Count, rule, kind, target,
            kind is ActionKind.CopyNew or ActionKind.MkDir or ActionKind.Move or ActionKind.KeepBoth
                ? ctx.Mapping.TargetPath(target, path) : path, from, source, expected,
            recoveryFirst, key)
        {
            ChoiceRemove = choiceRemove,
            RebuildableRemove = rebuildableRemove,
            FirstSyncChoice = firstSyncChoice || choiceRemove || rebuildableRemove,
            NeedsReview = needsReview,
        });   // draft-unique id: post-pass A and C refer to actions by it

    /// <summary>Adds an action to the draft and to the draft index.</summary>
    private static void Append(Context ctx, Draft draft, PlanRow row, SyncAction action)
    {
        draft.Add((row, action));
        ctx.BuiltIndex?.Added(draft.Count - 1, row, action);
    }

    /// <summary>Emits MkDir for a folder source and CopyNew for a file source.</summary>
    private static void CreateOn(Context ctx, PlanRow row, int rule, Side target, string path, ItemState source, string? key) =>
        Emit(ctx, row, rule, source.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
            target, path, null, source, null, false, key);

    /// <summary>Records an identity as synced: the target holds the source's content.</summary>
    private static void AdoptAction(Context ctx, PlanRow row, int rule, Side target, string path,
        ItemState source, ItemState expected, string? key) =>
        Emit(ctx, row, rule, ActionKind.Adopt, target, path, null, source, expected, false, key);

    /// <summary>Holds one row and raises the matching UserDecision notice.</summary>
    private static void Hold(Context ctx, PlanRow row, int rule, NoticeReason reason)
    {
        var path = PrimaryPath(row);
        Emit(ctx, row, rule, ActionKind.Hold, Side.B, path, null, null, null, false, null);
        AddNotice(ctx, rule, NoticeKind.UserDecision, path, reason);
    }

    /// <summary>Adds a notice once (both sides of one collision may raise the same one).</summary>
    private static void AddNotice(Context ctx, int rule, NoticeKind kind, string path, NoticeReason reason)
    {
        var notice = new Notice(ctx.PairId, rule, kind, path, reason.Detail, reason.Key);
        if (ctx.NoticeKeys.Add(notice))
            ctx.Notices.Add(notice);
    }

    /// <summary>Withdraws the row's two-place-copy notice (rules 10/11) once the row is no longer a keep-both outcome.</summary>
    private static void WithdrawTwoPlaceNotice(Context ctx, PlanRow row)
    {
        var path = PrimaryPath(row);
        bool TwoPlace(Notice n) => n.Rule is 10 or 11 && PairPath.Same(n.Path, path);
        ctx.Notices.RemoveAll(TwoPlace);
        ctx.NoticeKeys.RemoveWhere(TwoPlace);
    }

    /// <summary>The smallest attempt whose case-insensitive path is free on the target side.</summary>
    private static string FreeLabel(Context ctx, Side target, string path, string sideName, DateTimeOffset mtime)
    {
        for (var attempt = 1; ; attempt++)
        {
            var candidate = KeepBothLabel.For(path, sideName, mtime, ctx.Options.Label, attempt);
            var taken = ctx.Occupancy(target).ContainsKey(candidate) || ctx.Index.AnyCreateAt(ctx.Draft, target, candidate);
            if (!taken)
                return candidate;
            if (attempt > ctx.MaxLabelAttempts)
                throw new InvalidOperationException("Planner: no free keep-both label for " + path);
        }
    }

    /// <summary>Emits the keep-both pair for a clash (rules 17 and 27).</summary>
    private static void KeepBothPair(Context ctx, PlanRow row, string? key, ItemState a, ItemState b, bool split)
    {
        var rule = split ? 27 : 17;
        if (split)
            Emit(ctx, row, 27, ActionKind.Forget, Side.B, row.Base!.A.Path, null, null, null, false, key); // rule 27
        var keyA = split ? PlanIds.Split(key!, Side.A) : PlanIds.ObjectKey(ctx.PairId, Side.A, a.ObjectId);
        var keyB = split ? PlanIds.Split(key!, Side.B) : PlanIds.ObjectKey(ctx.PairId, Side.B, b.ObjectId);
        var labelA = FreeLabel(ctx, Side.B, a.Path, ctx.Options.Label.NameA, a.Mtime);
        var labelB = FreeLabel(ctx, Side.A, a.Path, ctx.Options.Label.NameB, b.Mtime);
        Emit(ctx, row, rule, ActionKind.KeepBoth, Side.B, labelA, null, a, null, false, keyA);
        Emit(ctx, row, rule, ActionKind.KeepBoth, Side.A, labelB, null, b, null, false, keyB);
        AddNotice(ctx, rule, NoticeKind.KeepBoth, PrimaryPath(row), PlanNotices.KeepBoth);
        AttachConflictVersions(ctx, rule, PrimaryPath(row), new(a, b, row.Base, labelB, labelA));
    }

    private static void AttachConflictVersions(Context ctx, int rule, string path, ConflictNoticeVersions versions)
    {
        var index = ctx.Notices.FindLastIndex(n => n.Rule == rule && PairPath.Same(n.Path, path));
        if (index >= 0) ctx.Notices[index] = ctx.Notices[index] with { ConflictVersions = versions };
    }

    // ---- small helpers -------------------------------------------------------

    private static Dictionary<string, (int Row, ItemState Item)> Occupancy(IReadOnlyList<PlanRow> rows, Side side)
    {
        var map = new Dictionary<string, (int Row, ItemState Item)>(rows.Count, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
        {
            var item = side == Side.A ? rows[i].A : rows[i].B;
            if (item is not null && !map.ContainsKey(item.Path))
                map[item.Path] = (i, item);
        }
        return map;
    }

    /// <summary>The user's winners by case-insensitive path; of two keys differing only by case the first listed wins.</summary>
    private static Dictionary<string, Side?> WinnersByPath(IReadOnlyDictionary<string, Side?>? winners)
    {
        var map = new Dictionary<string, Side?>(StringComparer.OrdinalIgnoreCase);
        if (winners is not null)
            foreach (var pair in winners)
                map.TryAdd(pair.Key, pair.Value);
        return map;
    }

    private static bool TouchesUnder(PlanRow row, string folder) =>
        UnderPath(row.Base?.A.Path, folder) || UnderPath(row.Base?.B.Path, folder) ||
        UnderPath(row.A?.Path, folder) || UnderPath(row.B?.Path, folder);

    private static bool UnderPath(string? path, string folder) => path is not null && PairPath.IsUnder(path, folder);

    private static string PrimaryPath(PlanRow row) => row.A?.Path ?? row.B?.Path ?? row.Base?.A.Path ?? string.Empty;

    private static Side Opposite(Side side) => side == Side.A ? Side.B : Side.A;

    private static ItemState? Cur(PlanRow row, Side side) => side == Side.A ? row.A : row.B;

    private static string SourceSideName(LabelRule label, Side target) => target == Side.A ? label.NameB : label.NameA;

    private static int CompareActions(SyncAction x, SyncAction y)
    {
        var bySide = x.Target.CompareTo(y.Target);
        if (bySide != 0)
            return bySide;
        var byName = string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
        if (byName != 0)
            return byName;
        var byOrdinal = string.Compare(x.Path, y.Path, StringComparison.Ordinal);
        return byOrdinal != 0 ? byOrdinal : ((int)x.Kind).CompareTo((int)y.Kind);
    }

    /// <summary>Immutable run inputs plus the running draft, notices, held folders and post-pass A's rebase record.</summary>
    private sealed class Context
    {
        public Context(string pairId, PlanOptions options, IReadOnlyList<PlanRow> rows,
            IReadOnlyDictionary<string, (int Row, ItemState Item)> occA,
            IReadOnlyDictionary<string, (int Row, ItemState Item)> occB)
        {
            PairId = pairId;
            Options = options;
            HeldParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (options.HeldPaths is { } heldPaths)
                foreach (var path in heldPaths)
                    foreach (var parent in PairPath.Ancestors(path)) HeldParents.Add(parent);
            Rows = rows;
            Mapping = new NameMappings(rows, options);
            OccA = occA;
            OccB = occB;
            OneSided = OneSidedChoiceResolver.Normalize(options.OneSided);
            Rebuildables = NormalizeRebuildables(options.Rebuildables);
            RebuildableCloud = options.Rebuildables?.Cloud;
            Winners = WinnersByPath(options.Winners);
            draft = new Draft(rows.Count);   // about one action per row on a first sync; avoids regrowing a huge list
        }

        public string PairId { get; }
        public PlanOptions Options { get; }
        public HashSet<string> HeldParents { get; }
        public NameMappings Mapping { get; }
        public IReadOnlyList<PlanRow> Rows { get; }

        /// <summary>The one-sided first-sync choices, longest prefix first (sorted once, not per row).</summary>
        public IReadOnlyList<OneSidedChoice> OneSided { get; }

        /// <summary>The first-sync rebuildable folders to stop syncing, deduplicated, collapsed and sorted once.</summary>
        public IReadOnlyList<string> Rebuildables { get; }

        /// <summary>The cloud side whose copy of a rebuildable folder is removed, derived from the pair's endpoints.</summary>
        public Side? RebuildableCloud { get; }

        /// <summary>True when the path is one of the rebuildable folders or lies under one.</summary>
        public bool UnderRebuildable(string? path)
        {
            if (path is null || Rebuildables.Count == 0) return false;
            foreach (var folder in Rebuildables)
                if (PairPath.IsSelfOrUnder(path, folder)) return true;
            return false;
        }

        /// <summary>The user's winners (rule 32) by case-insensitive path, built once.</summary>
        public IReadOnlyDictionary<string, Side?> Winners { get; }

        public void TruncateDraft(int count)
        {
            draft.RemoveRange(count, draft.Count - count);
            index = null;
        }

        /// <summary>Draft action id → the folder moves (by action id) that rebased it; a redirect follows only these.</summary>
        public Dictionary<string, HashSet<string>> RebasedBy { get; } = new(StringComparer.Ordinal);

        /// <summary>Unhashed current paths per side whose cross-row collision could not be confirmed (for HashRequests).</summary>
        public List<(Side Side, string Path)> HashNeeds { get; } = new();
        public IReadOnlyDictionary<string, (int Row, ItemState Item)> OccA { get; }
        public IReadOnlyDictionary<string, (int Row, ItemState Item)> OccB { get; }

        /// <summary>
        /// Differing folders (own name changed case) that already raised the CaseOnlyNames Info this run. Rows are
        /// ordered parent first, so a descendant can tell that the topmost such folder covers its own difference.
        /// </summary>
        public HashSet<string> NoticedDifferingFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<Notice> Notices { get; } = new();
        public List<SyncAction> DeferredPlanning { get; } = new();

        /// <summary>The notices already raised, keyed as storage identifies them (pair, path ignoring case, rule, reason).</summary>
        public HashSet<Notice> NoticeKeys { get; } = new(NoticeIdentity.Instance);
        public List<string> Held { get; } = new();
        public HashSet<string> FailedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The running draft; replacing it (passes B, A and C) drops the index built over the previous one.</summary>
        public Draft Draft
        {
            get => draft;
            set
            {
                draft = value;
                index = null;
            }
        }

        /// <summary>The index over <see cref="Draft"/>, built on first use (most tables never need a label).</summary>
        public DraftIndex Index => index ??= DraftIndex.Over(draft, RebasedBy);

        /// <summary>The index if it is built (so <see cref="Append"/> keeps it current), else null.</summary>
        public DraftIndex? BuiltIndex => index;

        private Draft draft;
        private DraftIndex? index;
        public int MaxLabelAttempts => Options.MaxLabelAttempts;
        public IReadOnlyDictionary<string, (int Row, ItemState Item)> Occupancy(Side side) => side == Side.A ? OccA : OccB;
    }

    /// <summary>Notice identity as storage keeps it: pair, path ignoring case, rule and reason key.</summary>
    private sealed class NoticeIdentity : IEqualityComparer<Notice>
    {
        public static readonly NoticeIdentity Instance = new();

        public bool Equals(Notice? x, Notice? y) => ReferenceEquals(x, y) || (x is not null && y is not null &&
            x.Rule == y.Rule && string.Equals(x.PairId, y.PairId, StringComparison.Ordinal) &&
            string.Equals(x.ReasonKey, y.ReasonKey, StringComparison.Ordinal) && PairPath.Same(x.Path, y.Path));

        public int GetHashCode(Notice n) => HashCode.Combine(n.Rule, n.ReasonKey,
            StringComparer.OrdinalIgnoreCase.GetHashCode(n.Path));
    }

    /// <summary>A side and a path compared as <see cref="PairPath.Same"/> does.</summary>
    private sealed class PathKeyComparer : IEqualityComparer<(Side Side, string Path)>
    {
        public static readonly PathKeyComparer Instance = new();

        public bool Equals((Side Side, string Path) x, (Side Side, string Path) y) =>
            x.Side == y.Side && PairPath.Same(x.Path, y.Path);

        public int GetHashCode((Side Side, string Path) key) =>
            HashCode.Combine(key.Side, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path));
    }
}
