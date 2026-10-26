namespace Massdex.SyncShark.V2;

/// <summary>Planner decision table: baseline rows, no-baseline rows and the user's winner (planner.md section 5).</summary>
public static partial class Planner
{
    private static void FromBaseline(Context ctx, PlanRow row, RowState sA, RowState sB)
    {
        var key = row.Base!.ObjectKey;
        if (row.A is not null && row.B is not null)
            BaselineBothPresent(ctx, row, sA, sB, key);
        else
            BaselineOneSided(ctx, row, sA, sB, key);
    }

    private static void BaselineBothPresent(Context ctx, PlanRow row, RowState sA, RowState sB, string key)
    {
        if (sA == RowState.Replaced || sB == RowState.Replaced)
        {
            ReplacedAgainstOther(ctx, row, sA, sB, key);
            return;
        }
        if (sA == RowState.Same && sB == RowState.Same)
            return;                                                                       // rule 1
        if (sB == RowState.Same)
        {
            ChangedAgainstSame(ctx, row, Side.A, sA, key);
            return;
        }
        if (sA == RowState.Same)
        {
            ChangedAgainstSame(ctx, row, Side.B, sB, key);
            return;
        }
        if (!DropboxNameMapping.SamePlace(row.A!, row.B!))
        {
            TwoPlaceCopy(ctx, row, sA, sB, key);
            return;
        }
        var same = Content.SameContent(row.A!, row.B!);
        if (same == true && sA == RowState.Moved && sB == RowState.Moved &&
            PairPath.Same(row.A!.Path, row.Base!.A.Path) && PairPath.Same(row.B!.Path, row.Base.B.Path) &&
            !PairPath.SameLeafLocation(row.A.Path, row.B.Path))
        {
            ResolveCaseConflict(ctx, row, key);
            return;
        }
        if (same == true)
            AdoptAction(ctx, row, 3, Side.B, row.B!.Path, row.A!, row.B!, key);           // rule 3
        else if (same is null)
            SkipUnconfirmedRow(ctx, row);                                                 // hashing failed or pending
        else if (ctx.Options.FirstSync)
            FirstSyncClash(ctx, row, row.A!, row.B!, key);                                // rule 4/5
        else
            KeepBothPair(ctx, row, key, row.A!, row.B!, true);                            // rule 27
    }

    private static void ReplacedAgainstOther(Context ctx, PlanRow row, RowState sA, RowState sB, string key)
    {
        var replaced = sA == RowState.Replaced ? Side.A : Side.B;
        var other = Opposite(replaced);
        var sOther = replaced == Side.A ? sB : sA;
        var curReplaced = replaced == Side.A ? row.A! : row.B!;
        var curOther = other == Side.A ? row.A! : row.B!;
        if (sOther != RowState.Same)
        {
            Hold(ctx, row, 29, PlanNotices.NeedDecision);                                 // rule 29
            if (curReplaced.Kind == ItemKind.Folder || curOther.Kind == ItemKind.Folder)
                HoldFolderFootprints(ctx, row);
            return;
        }
        Emit(ctx, row, 26, ActionKind.Delete, other, curOther.Path, null, null, curOther, true, key); // rule 26
        CreateOn(ctx, row, 26, other, curOther.Path, curReplaced, key);
    }

    private static void HoldFolderFootprints(Context ctx, PlanRow row)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[] { row.A?.Path, row.B?.Path, row.Base?.A.Path, row.Base?.B.Path })
        {
            if (path is null) continue;
            paths.Add(path);
            paths.Add(ctx.Mapping.TargetPath(Side.A, path));
            paths.Add(ctx.Mapping.TargetPath(Side.B, path));
        }
        ctx.Held.AddRange(paths);
    }

    /// <summary>Both sides changed to different places: copy each to the other side (rules 10 and 11).</summary>
    private static void TwoPlaceCopy(Context ctx, PlanRow row, RowState sA, RowState sB, string key)
    {
        var rule = sA == RowState.Moved && sB == RowState.Moved ? 11 : 10;
        CreateOn(ctx, row, rule, Side.B, row.A!.Path, row.A, key);
        CreateOn(ctx, row, rule, Side.A, row.B!.Path, row.B, PlanIds.Split(key, Side.B));
        AddNotice(ctx, rule, NoticeKind.KeepBoth, PrimaryPath(row), PlanNotices.KeepBoth);
        AttachConflictVersions(ctx, rule, PrimaryPath(row), new(row.A!, row.B!, row.Base,
            ctx.Mapping.TargetPath(Side.A, row.B!.Path), ctx.Mapping.TargetPath(Side.B, row.A!.Path)));
    }

    private static void BaselineOneSided(Context ctx, PlanRow row, RowState sA, RowState sB, string key)
    {
        if (row.A is null && row.B is null)
        {
            Emit(ctx, row, 15, ActionKind.Forget, Side.B, row.Base!.A.Path, null, null, null, false, key); // rule 15
            return;
        }
        var present = row.A is not null ? Side.A : Side.B;
        var cur = present == Side.A ? row.A! : row.B!;
        var absent = Opposite(present);
        switch (present == Side.A ? sA : sB)
        {
            case RowState.Same:
                // Decision 69: history replaces the Recovery copy only for a delete on a local target whose
                // opposite side is Dropbox, and only when the current hash exactly equals the deleted
                // (baseline) Dropbox side's hash. Every other target (Dropbox, google-drive, local/local)
                // keeps the recovery-first delete, as does a missing or unknown hash.
                Emit(ctx, row, 6, ActionKind.Delete, present, cur.Path, null, null, cur,
                    !CanUseDropboxHistory(ctx, present, cur, DeletedSideHash(row, present)), key); // rule 6
                return;
            case RowState.Edited:
            case RowState.MovedEdited:
                CreateOn(ctx, row, 7, absent, cur.Path, cur, key);                        // rule 7
                AddNotice(ctx, 7, NoticeKind.EditBeatsDelete, cur.Path, PlanNotices.EditBeatsDelete);
                return;
            case RowState.Moved:
                CreateOn(ctx, row, 8, absent, cur.Path, cur, key);                        // rule 8
                AddNotice(ctx, 8, NoticeKind.Info, cur.Path, PlanNotices.MovedVersionKept);
                return;
            case RowState.Replaced:
                CreateOn(ctx, row, 26, absent, cur.Path, cur, key);                       // rule 26 (T deleted)
                return;
            default:
                throw new InvalidOperationException("Planner: unhandled row");
        }
    }

    /// <summary>
    /// Decision 69 (extended to ordinary baseline propagation): true only when the current local file's content
    /// hash is known and exactly equals the hash of the Dropbox revision that was deleted. The executor re-observes
    /// and hashes the file under an exclusive handle before deleting, so a stale or missing hash never skips Recovery.
    /// </summary>
    private static bool MatchesDeletedRevision(ItemState cur, string? revisionHash) =>
        cur.Kind == ItemKind.File && Content.SameHash(cur.Hash, revisionHash) == true;

    // Only a local target can rely on the other side's Dropbox history. Cloud targets retain app Recovery
    // provenance (PreserveAsync is metadata-only); local/local and unknown providers must preserve the bytes.
    private static bool CanUseDropboxHistory(Context ctx, Side target, ItemState cur, string? revisionHash) =>
        string.Equals(target == Side.A ? ctx.Options.ProviderA : ctx.Options.ProviderB,
            ProviderIds.Local, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(target == Side.A ? ctx.Options.ProviderB : ctx.Options.ProviderA,
            ProviderIds.Dropbox, StringComparison.OrdinalIgnoreCase) &&
        MatchesDeletedRevision(cur, revisionHash);

    /// <summary>The content hash of the baseline side that disappeared.</summary>
    private static string? DeletedSideHash(PlanRow row, Side present) =>
        present == Side.A ? row.Base?.B.Hash : row.Base?.A.Hash;

    // Local bytes only Recovery can restore; a cloud target keeps the deleted revision in its own history.
    private static bool IsLocalTarget(Context ctx, Side target) =>
        string.Equals(target == Side.A ? ctx.Options.ProviderA : ctx.Options.ProviderB,
            ProviderIds.Local, StringComparison.OrdinalIgnoreCase);

    /// <summary>The hash inside a prepare stamp ("objectId|h:hash"); null for folder, version or metadata stamps.</summary>
    private static string? PreparedHash(string stamp)
    {
        var split = stamp.IndexOf('|');
        var kind = split < 0 ? string.Empty : stamp[(split + 1)..];
        return kind.StartsWith("h:", StringComparison.Ordinal) && kind.Length > 2 ? kind[2..] : null;
    }

    /// <summary>The endpoint object id in a prepare stamp ("objectId|stamp"); null unless id and stamp type are valid.</summary>
    private static string? PreparedObjectId(PrepareMatch match, Side side)
    {
        var stamp = side == Side.A ? match.A : match.B;
        var split = stamp.IndexOf('|');
        return split > 0 && ValidStampType(stamp[(split + 1)..]) ? stamp[..split] : null;
    }

    // The kinds Content.SideStamp writes: folder, hash, version, or "m:size:ticks" metadata.
    private static bool ValidStampType(string kind)
    {
        if (kind == "d") return true;
        if (kind.Length <= 2) return false;
        if ((kind[0] == 'h' || kind[0] == 'v') && kind[1] == ':') return true;
        if (!kind.StartsWith("m:", StringComparison.Ordinal)) return false;
        var parts = kind[2..].Split(':');
        return parts.Length == 2 &&
            long.TryParse(parts[0], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _) &&
            long.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    // Emit has no seam for the optional identity, which lets the executor recognize a relocated original.
    // A prepared Delete without a trusted opposite identity never runs path-only: it holds with one notice.
    private static void EmitPreparedDeleteOrHold(Context ctx, PlanRow row, Side target, ItemState victim,
        string? deletionSourceObjectId)
    {
        if (deletionSourceObjectId is null)
        {
            Emit(ctx, row, 6, ActionKind.Hold, target, victim.Path, null, null, null, false, null);
            AddNotice(ctx, 6, NoticeKind.Info, victim.Path, PlanNotices.ContentUnconfirmed);
            return;
        }
        Append(ctx, ctx.Draft, row, new SyncAction("s" + ctx.Draft.Count, 6, ActionKind.Delete, target,
            victim.Path, null, null, victim, true, null) { DeletionSourceObjectId = deletionSourceObjectId });
    }

    private static void WithoutBaseline(Context ctx, PlanRow row, RowState sA, RowState sB)
    {
        if (row.A is not null && row.B is not null)
        {
            if (row.A.Kind != row.B.Kind)
            {
                Hold(ctx, row, 14, new NoticeReason("incoming-name-clash",
                    "Different item kinds occupy the same name. Keep both unchanged, rename the incoming item, or replace the destination with Recovery."));
                AttachConflictVersions(ctx, 14, PrimaryPath(row), new(row.A, row.B, Incoming:
                    NewAction(14, row.A.Kind == ItemKind.Folder ? ActionKind.MkDir : ActionKind.CopyNew,
                        Side.B, row.B.Path, null, row.A, null, false, null)));
                HoldFolderFootprints(ctx, row);
                return;
            }
            var same = Content.SameContent(row.A, row.B);
            if (same == true)
                AdoptAction(ctx, row, 16, Side.B, row.B.Path, row.A, row.B, PlanIds.ObjectKey(ctx.PairId, Side.A, row.A.ObjectId)); // rule 16
            else if (same is null)
                SkipUnconfirmedRow(ctx, row);                                             // hashing failed or pending
            else
                FirstSyncClash(ctx, row, row.A, row.B, null);                             // decision 81: rule 4/5
            return;
        }
        if (row.A is null && row.B is null)
            throw new InvalidOperationException("Planner: unhandled row");
        OneSidedNew(ctx, row);
    }

    /// <summary>
    /// Run hashes ambiguous paths before Decide (HashRequests), so content still unknown here means hashing failed or
    /// is pending: no effect for the row, only an Info notice; never keep-both, newer-wins or Hold (owner decision).
    /// </summary>
    private static void SkipUnconfirmedRow(Context ctx, PlanRow row) =>
        AddNotice(ctx, 30, NoticeKind.Info, PrimaryPath(row), PlanNotices.ContentUnconfirmed);

    /// <summary>
    /// one-sided rows whose path the reviewed prepare saw on both sides are ordinary ongoing sync.
    /// The survivor still at its prepare-time identity/content means the other side's post-prepare disappearance is a
    /// normal deletion to propagate; a changed survivor wins (RQ-043) and is copied back. A local file additionally
    /// needs content proof: a hash-stamped prepare that still matches (identity and bytes) deletes recovery-first, an
    /// unknown hash (or one the prepare never recorded) holds with one notice, and a changed identity or hash is the
    /// copy-back. A prepared Delete also holds when the absent side's prepare stamp carries no valid identity, so a
    /// Delete never runs path-only. Folders carry no hash and propagate unchanged. Runs before first-sync winners and
    /// one-sided choices (Planner.Dispatch), so a copy choice cannot resurrect the survivor and a remove choice cannot
    /// turn an ordinary propagation into a reviewed removal.
    /// </summary>
    private static bool TryPreparedMatch(Context ctx, PlanRow row)
    {
        if (row.Base is not null ||
            ctx.Options.MatchedAtPrepare is not { } matched ||
            (row.A is null) == (row.B is null))
            return false;
        var present = row.A is not null ? Side.A : Side.B;
        var cur = present == Side.A ? row.A! : row.B!;
        // The disappearance is evidence only when the absent side's inventory covered the path (a full first-sync
        // scan always does); otherwise leave the row to the ordinary no-evidence path.
        var absentKnown = present == Side.A ? row.Ev.KnownB || row.Ev.DeletedB : row.Ev.KnownA || row.Ev.DeletedA;
        if (!absentKnown) return false;
        if (!matched.TryGetValue(cur.Path, out var prepareMatch))
            return false;
        var missing = Opposite(present);
        // Trusted opposite identity from "objectId|stamp". A missing or malformed stamp yields no identity, and
        // the prepared Delete then holds with one notice instead of running path-only.
        var deletionSourceObjectId = PreparedObjectId(prepareMatch, missing);
        // Local files need content proof: row.Base is null here, so the prepare stamp's hash is the baseline.
        // Unknown hash (or a stamp that recorded none) holds with one dedup notice. A hash-stamped prepare that
        // still matches proves both identity and content and deletes; a different identity or hash is the RQ-043
        // copy-back below. Folders carry no hash and still propagate.
        if (IsLocalTarget(ctx, present) && cur.Kind == ItemKind.File)
        {
            if (cur.Hash is null || PreparedHash(present == Side.A ? prepareMatch.A : prepareMatch.B) is null)
            {
                Emit(ctx, row, 6, ActionKind.Hold, present, cur.Path, null, null, null, false, null);
                AddNotice(ctx, 6, NoticeKind.Info, cur.Path, PlanNotices.ContentUnconfirmed);
                return true;
            }
            if (prepareMatch.Unchanged(present, cur))
            {
                EmitPreparedDeleteOrHold(ctx, row, present, cur, deletionSourceObjectId);
                return true;
            }
        }
        else if (prepareMatch.Unchanged(present, cur))
        {
            EmitPreparedDeleteOrHold(ctx, row, present, cur, deletionSourceObjectId);
            return true;
        }
        CreateOn(ctx, row, 7, Opposite(present), cur.Path, cur,
            PlanIds.ObjectKey(ctx.PairId, present, cur.ObjectId));
        AddNotice(ctx, 7, NoticeKind.EditBeatsDelete, cur.Path, PlanNotices.EditBeatsDelete);
        return true;
    }

    /// <summary>One side only with no baseline (rules 18/19; for a later deletion).</summary>
    private static void OneSidedNew(Context ctx, PlanRow row)
    {
        var present = row.A is not null ? Side.A : Side.B;
        var cur = present == Side.A ? row.A! : row.B!;
        var deletedOther = present == Side.A ? row.Ev.DeletedB : row.Ev.DeletedA;
        var deletion = present == Side.A ? row.Ev.DeletedAtB : row.Ev.DeletedAtA;
        if (deletedOther && deletion is not null && deletion > cur.Mtime)
        {
            // Decision 78: a later deletion wins only over identical file content. A different
            // hash is an edit to restore, and unknown hashes must never authorize a deletion.
            var revisionHash = present == Side.A ? row.Ev.DeletedHashB : row.Ev.DeletedHashA;
            if (cur.Kind == ItemKind.File && (cur.Hash is null || revisionHash is null))
            {
                SkipUnconfirmedRow(ctx, row);
                return;
            }
            if (cur.Kind != ItemKind.File || MatchesDeletedRevision(cur, revisionHash))
            {
                Emit(ctx, row, 19, ActionKind.Delete, present, cur.Path, null, null, cur,
                    !CanUseDropboxHistory(ctx, present, cur, revisionHash), null, needsReview: true);
                return;
            }
        }
        var rule = deletedOther ? 19 : 18;
        CreateOn(ctx, row, rule, Opposite(present), cur.Path, cur,
            PlanIds.ObjectKey(ctx.PairId, present, cur.ObjectId));                        // rules 18/19
        if (deletedOther)
            AddNotice(ctx, 19, NoticeKind.EditBeatsDelete, cur.Path, PlanNotices.EditBeatsDelete);
    }

    /// <summary>First-sync clash: the newer modification time wins, a tie is held (rules 4 and 5).</summary>
    private static void FirstSyncClash(Context ctx, PlanRow row, ItemState a, ItemState b, string? key)
    {
        if (!ModificationTime.IsPlausible(a.Mtime, ctx.Options.ObservedAt) ||
            !ModificationTime.IsPlausible(b.Mtime, ctx.Options.ObservedAt) ||
            (a.Mtime - b.Mtime).Duration() <= ctx.Options.TieWindow)
        {
            Hold(ctx, row, 5, PlanNotices.NeedDecision);                                  // rule 5
            return;
        }
        var older = a.Mtime <= b.Mtime ? a : b;
        var newer = ReferenceEquals(older, a) ? b : a;
        var olderSide = ReferenceEquals(older, a) ? Side.A : Side.B;
        var effectiveKey = key ?? PlanIds.ObjectKey(ctx.PairId, Opposite(olderSide), newer.ObjectId); // no history: still carry a key
        Emit(ctx, row, 4, ActionKind.Update, olderSide, older.Path, null, newer, older, true, effectiveKey); // rule 4
        AddNotice(ctx, 4, NoticeKind.AutoConflict, older.Path, PlanNotices.AutoConflict);
    }

    /// <summary>The user's chosen winner overrides the table (rule 32).</summary>
    private static void ApplyWinner(Context ctx, PlanRow row, string? key, Side? winner)
    {
        if (winner is null)
            return;
        var winning = winner.Value;
        var wCur = winning == Side.A ? row.A : row.B;
        if (wCur is null)
            return;
        var losing = Opposite(winning);
        var lCur = losing == Side.A ? row.A : row.B;
        var rowKey = key ?? PlanIds.ObjectKey(ctx.PairId, winning, wCur.ObjectId);
        if (lCur is null)
        {
            CreateOn(ctx, row, 32, losing, wCur.Path, wCur, rowKey);                      // rule 32
        }
        else if (Content.SameContent(wCur, lCur) != true)
        {
            if (wCur.Kind != lCur.Kind)
            {
                Emit(ctx, row, 32, ActionKind.Delete, losing, lCur.Path, null, null, lCur, true, rowKey,
                    firstSyncChoice: true);
                CreateOn(ctx, row, 32, losing, lCur.Path, wCur, rowKey);
            }
            else
            {
                Emit(ctx, row, 32, ActionKind.Update, losing, lCur.Path, null, wCur, lCur, true, rowKey);
            }
        }
        else
        {
            return;
        }
        AddNotice(ctx, 32, NoticeKind.AutoConflict, lCur?.Path ?? wCur.Path, PlanNotices.AutoConflict);
    }
}
