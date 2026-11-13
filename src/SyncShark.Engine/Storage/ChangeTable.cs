using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>The one owner of change_work, its issue-work rows and source state (section 3.8), called through
/// StateStore's ordering. Membership of a WorkSelection and the dirty-evidence answer are decided only here. Methods
/// taking a transaction join the caller's composite transaction; the others run their own short one. No Storage
/// transaction crosses provider I/O.</summary>
internal sealed partial class ChangeTable
{
    private const long BeforeFirstWork = 0L;
    private const string GapReason = nameof(SourceObligation.LocalSkippedCoverage);

    private static readonly string[] ExactProbes = [StateSchema.EvidencePathExact, StateSchema.EvidenceOldPathExact];
    private static readonly string[] BelowProbes = [StateSchema.EvidencePathBelow, StateSchema.EvidenceOldPathBelow];

    private readonly SqliteConnection _connection;
    // The pair's open finite opportunity, so a held or claimed candidate is not offered twice within it.
    private readonly Dictionary<string, Pass> _passes = new(StringComparer.Ordinal);

    private readonly bool _writable;
    private readonly Dictionary<string, (long Generation, long Epoch, bool Any)> _evidence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalPosition?> _positions = new(StringComparer.Ordinal);

    internal ChangeTable(SqliteConnection connection, bool writable)
    {
        _connection = connection;
        _writable = writable;
        if (!writable) return;
        connection.CreateFunction(StateSchema.LocalEvidenceTouchedFunction, (string kind) =>
        {
            if (kind == StateSchema.LocalPositionKind) _positions.Clear();
            else _evidence.Clear();
            return 0;
        });
        using var command = StorageCommand.Create(connection, null, StateSchema.CreateLocalEvidenceTriggers);
        command.ExecuteNonQuery();
    }

    /// <summary>The side's current fence, or null when the pair has no source row.</summary>
    internal (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
        ReadSource(string pairId, Side side) =>
        ReadPair(null, pairId) is { } pair
            ? Read<(long, SourceProvenance?, string?, SourceObligation?, long)?>(null, StateSchema.SourceFence,
                static reader => (reader.GetInt64(StateSchema.SourceEpoch),
                    StateEncoding.ReadOptionalEnvelope<SourceProvenance>(reader, StateSchema.SourceProvenanceJson),
                    Text(reader, StateSchema.SourceCursor),
                    StateEncoding.ReadOptionalEnum<SourceObligation>(reader, StateSchema.SourceRepairReason),
                    reader.GetInt64(StateSchema.SourceRepairRevision)),
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamSide, (int)side)).FirstOrDefault()
            : null;

    /// <summary>The obligation a lost interval installs: Bootstrap before the pair's first completion, NormalRepair
    /// after it. First-run behavior itself comes from first_repair_done, never from this reason.</summary>
    private SourceObligation LostIntervalObligation(PairRow pair) =>
        pair.FirstRepairDone ? SourceObligation.NormalRepair : SourceObligation.Bootstrap;

    /// <summary>The lost-interval answer for one pair, from one pair-fence read.</summary>
    internal SourceObligation LostIntervalObligation(string pairId) =>
        ReadPair(null, pairId) is { } pair ? LostIntervalObligation(pair)
            : throw new InvalidOperationException();

    /// <summary>Whether either source owes a Repair, from one pair-fence read.</summary>
    internal bool Obligated(string pairId, SqliteTransaction? transaction = null) =>
        ReadPair(transaction, pairId) is { } pair && Obligated(pair.Row, transaction);

    /// <summary>The one live answer for owed Repair (D-1105-5): no live work before the pair's first completed Repair
    /// or without both source rows; Local skipped-coverage or lost-interval debt refuses only effects that write Local,
    /// because a Local edit inside that interval is not dirty evidence; any other debt admits.</summary>
    internal LiveRefusal ReadLiveRefusal(string pairId) =>
        ReadPair(null, pairId) is { } pair ? ReadLiveRefusal(pair) : LiveRefusal.None;

    /// <summary>The volume's committed journal position, or null before its first base.</summary>
    internal JournalPosition? ReadJournal(string volumeKey) => ReadPosition(null, volumeKey);

    /// <summary>The Local reconciliation debt's revision when that debt is a skipped interval; null otherwise. It is
    /// source responsibility only and never evidence about content.</summary>
    internal long? ReadLocalGap(string pairId, long localEpoch) =>
        ReadPair(null, pairId) is { } pair && ReadSourceRow(null, pair.Row, Side.Local) is { } local &&
        local.Epoch == localEpoch ? LocalGap(local) : null;

    private static long? LocalGap(SourceRow local) => local.Reason == GapReason ? local.Revision : null;

    internal bool ClassifyLocalFinish(SqliteTransaction transaction, string pairId, long epoch, long revision, long? gap)
    {
        if (ReadPair(transaction, pairId) is not { } pair || ReadSourceRow(transaction, pair.Row, Side.Local) is not { } local ||
            local.Epoch != epoch || local.Revision < revision)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        if (local.Revision == revision && LocalGap(local) == gap) return true;
        if (local.Revision > revision && local.Reason is not null) return false;
        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
    }

    // One atomic page acquisition: position check, member fences, notices, repairs and journal advance must commit or roll back together.
    /// <summary>Atomically checks the expected position and every member's persistent fence, inserts the ordered
    /// notices, applies each member's requested repair, and advances the journal. Null is a
    /// definitive stale rollback with no page changes; a value is the committed work watermark, including zero.
    /// Invalid inputs and genuine failures throw. The caller's gate, not this check, proves membership complete.
    /// </summary>
    internal long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
        IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision,
            bool LostInterval)> members,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeKey);
        using var transaction = _connection.BeginTransaction();
        if (ReadPosition(transaction, volumeKey) != expected) return null;
        var fences = new Dictionary<string, (PairRow Pair, SourceRow Source,
            SourceObligation? Obligation)>(members.Count, StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (ReadPair(transaction, member.PairId) is not { } pair || pair.Generation != member.Generation ||
                ReadSourceRow(transaction, pair.Row, Side.Local) is not { } source ||
                source.Epoch != member.Epoch || source.Revision != member.Revision)
                return null;
            // A lost interval raises the source-obligation owner's answer: Bootstrap before the first completion,
            // NormalRepair after it. Configuration and gap ownership belong to their own transitions.
            fences.Add(member.PairId, (pair, source,
                member.LostInterval ? LostIntervalObligation(pair) : (SourceObligation?)null));
        }
        var workId = Watermark(transaction);
        using (var insert = WorkInsert(transaction))
        {
            foreach (var (pairId, notice) in notices)
            {
                if (notice.Side != Side.Local || !fences.TryGetValue(pairId, out var fence))
                    throw new ArgumentException(null, nameof(notices));
                workId = checked(workId + 1);
                AddWork(insert, workId, fence.Pair.Row, fence.Pair.Generation, fence.Source.Epoch, volumeKey, notice,
                    null, workId, null, null);
            }
        }
        foreach (var (pair, source, requested) in fences.Values)
        {
            if (requested is null) continue;
            Advance(transaction, pair.Row, Side.Local, pair.Generation, source.Epoch, source.Revision,
                source.Provenance, source.Cursor, source.Epoch, checked(source.Revision + 1), source.Provenance,
                source.Cursor, AfterLoss(source, requested.Value));
        }
        Require(expected is null
            ? Run(transaction, StateSchema.JournalInsert, (StateSchema.ParamVolume, volumeKey),
                (StateSchema.ParamJournal, Signed(next.JournalId)), (StateSchema.ParamNext, Signed(next.NextUsn)))
            : Run(transaction, StateSchema.JournalAdvance, (StateSchema.ParamVolume, volumeKey),
                (StateSchema.ParamJournal, Signed(next.JournalId)), (StateSchema.ParamNext, Signed(next.NextUsn)),
                (StateSchema.ParamExpectedJournal, Signed(expected.JournalId)),
                (StateSchema.ParamExpectedNext, Signed(expected.NextUsn))));
        transaction.Commit();
        return workId;
    }

    /// <summary>Enumerates the notices synchronously inside one transaction, so a failed read rolls back every
    /// notice and the cursor. Returns the committed work watermark; after a lost reply, reread ReadSource. A stale
    /// fence throws LiveAdmissionStaleException and changes nothing.</summary>
    internal long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch, long expectedRevision,
        SourceProvenance provenance, string? expectedCursor, IEnumerable<ChangeNotice> notices, string cursor)
    {
        using var transaction = _connection.BeginTransaction();
        var bound = StateEncoding.Envelope(provenance);
        if (ReadPair(transaction, pairId) is not { } pair || pair.Generation != expectedGeneration ||
            ReadSourceRow(transaction, pair.Row, Side.Dropbox) is not { } source || source.Epoch != expectedEpoch ||
            source.Revision != expectedRevision || source.Cursor != expectedCursor || source.Provenance != bound)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var workId = Watermark(transaction);
        using (var insert = WorkInsert(transaction))
        {
            foreach (var notice in notices)
            {
                if (notice.Side != Side.Dropbox) throw new ArgumentException(null, nameof(notices));
                if (notice.Kind == ChangeKind.Deleted)
                    Run(transaction, StateSchema.AppendDeletedHistory,
                        (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamPath, PathNames.Key(notice.Path)));
                workId = checked(workId + 1);
                AddWork(insert, workId, pair.Row, expectedGeneration, expectedEpoch, provenance.NamespaceId, notice,
                    null, workId, null, null);
            }
        }
        Advance(transaction, pair.Row, Side.Dropbox, expectedGeneration, expectedEpoch, expectedRevision, bound,
            expectedCursor, expectedEpoch, expectedRevision, bound, cursor, source.Reason);
        transaction.Commit();
        return workId;
    }

    /// <summary>Requires exactly one row. Advancing the Local epoch over retained acquired work promotes the
    /// obligation to LocalSkippedCoverage.</summary>
    internal void AdvanceSource(SqliteTransaction transaction, string pairId, Side side, long expectedGeneration,
        long expectedEpoch, long expectedRevision, SourceProvenance? expectedProvenance, string? expectedCursor,
        long nextEpoch, long nextRevision, SourceProvenance? provenance, string? cursor,
        SourceObligation? obligation)
    {
        // Only AcquireDropboxPage may move a cursor without invalidating the Repair's durable revision fence.
        if (side == Side.Dropbox)
            nextRevision = Math.Max(nextRevision,
                nextEpoch != expectedEpoch || cursor != expectedCursor || !Equals(provenance, expectedProvenance)
                    ? checked(expectedRevision + 1) : expectedRevision);
        Advance(transaction, RequirePair(transaction, pairId).Row, side, expectedGeneration, expectedEpoch,
            expectedRevision, StateEncoding.OptionalEnvelope(expectedProvenance), expectedCursor, nextEpoch,
            nextRevision, StateEncoding.OptionalEnvelope(provenance), cursor, obligation?.ToString());
    }

    /// <summary>The reason a Local source owes after a continuity loss, from the one precedence owner.</summary>
    private static string AfterLoss(SourceRow source, SourceObligation lost) => SourceObligations.Owed(
        source.Reason is null ? null : Enum.Parse<SourceObligation>(source.Reason), lost).ToString();

    internal void RequireRepairAfterLoss(SqliteTransaction transaction, string pairId, long expectedGeneration)
    {
        var pair = RequirePair(transaction, pairId);
        if (pair.Generation != expectedGeneration || ReadSourceRow(transaction, pair.Row, Side.Local) is not { } source)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        // Even existing debt must invalidate completion of a Repair that preceded this loss.
        Advance(transaction, pair.Row, Side.Local, expectedGeneration, source.Epoch, source.Revision,
            source.Provenance, source.Cursor, source.Epoch, checked(source.Revision + 1), source.Provenance,
            source.Cursor, AfterLoss(source, LostIntervalObligation(pair)));
    }

    /// <summary>Requires exactly one row, so an earlier attempt never clears a newer epoch or revision.</summary>
    internal void FinishObligation(SqliteTransaction transaction, string pairId, Side side, long expectedGeneration,
        long expectedEpoch, long expectedRevision) =>
        Require(Run(transaction, StateSchema.FinishObligation,
            (StateSchema.ParamPairRow, RequirePair(transaction, pairId).Row), (StateSchema.ParamSide, (int)side),
            (StateSchema.ParamEpoch, expectedEpoch), (StateSchema.ParamRevision, expectedRevision),
            (StateSchema.ParamGeneration, expectedGeneration)));

    /// <summary>Installs a new pair's two source rows; existing rows are kept.</summary>
    internal void SeedPairSources(SqliteTransaction transaction, string pairId) =>
        Run(transaction, StateSchema.SeedPairSources, (StateSchema.ParamPair, pairId),
            (StateSchema.ParamBootstrap, nameof(SourceObligation.Bootstrap)),
            (StateSchema.ParamNormal, nameof(SourceObligation.NormalRepair)));

    /// <summary>Whether Acquired-origin Local work exists at the generation; issue-origin work never counts.</summary>
    internal bool HasCurrentLocalWork(SqliteTransaction transaction, string pairId, long generation) =>
        Read(transaction, StateSchema.HasCurrentLocalWork,
            static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
            (StateSchema.ParamPairRow, RequirePair(transaction, pairId).Row),
            (StateSchema.ParamGeneration, generation)).First();

    /// <summary>Marks retained Local work as skipped coverage before a generation change hides it; call only after
    /// HasCurrentLocalWork returned true in the same transaction. Requires exactly one row.</summary>
    internal void TransferLocalEvidenceGap(SqliteTransaction transaction, string pairId, long expectedGeneration,
        long expectedEpoch, long expectedRevision) =>
        Require(Run(transaction, StateSchema.TransferLocalEvidenceGap, (StateSchema.ParamGapReason, GapReason),
            (StateSchema.ParamPairRow, RequirePair(transaction, pairId).Row), (StateSchema.ParamEpoch, expectedEpoch),
            (StateSchema.ParamRevision, expectedRevision), (StateSchema.ParamGeneration, expectedGeneration)));

    /// <summary>The queue snapshot: pending Acquired rows for both current sources at or below the pass watermark.
    /// A stale pair or a refused live pass reports nothing, matching the null SelectWork that would follow.</summary>
    internal (long Local, long Dropbox) CountWork(string pairId, long expectedGeneration, long through)
    {
        if (ReadPair(null, pairId) is not { } pair || pair.Generation != expectedGeneration) return (0, 0);
        var local = ReadSourceRow(null, pair.Row, Side.Local);
        var dropbox = ReadSourceRow(null, pair.Row, Side.Dropbox);
        if (local is null || dropbox is null || ReadLiveRefusal(pair, local, dropbox) == LiveRefusal.Pair)
            return (0, 0);
        return Read(null, StateSchema.CountWork, static reader =>
                (reader.GetInt64(StateSchema.QueueLocalCount), reader.GetInt64(StateSchema.QueueDropboxCount)),
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamThrough, through), (StateSchema.ParamLocalEpoch, local.Epoch),
            (StateSchema.ParamRemoteEpoch, dropbox.Epoch)).First();
    }

    /// <summary>The next runnable finite selection of Acquired work from the two current-source partitions, or null.
    /// Held candidates are skipped within the opportunity; its owner calls EndWorkPass on every exit so a later
    /// wake captures a fresh pass without retiring unfinished work.</summary>
    internal WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (ReadPair(null, pairId) is not { } pair || pair.Generation != expectedGeneration ||
            ReadLiveRefusal(pair) == LiveRefusal.Pair)
        {
            return null;
        }
        long? observedThrough = null;
        var pass = _passes.GetValueOrDefault(pairId) is { } open && open.Generation == expectedGeneration
            ? open.Scope is null ? open with { Scope = scope, Through = observedThrough ??= Watermark(null) } : open
            : new Pass(expectedGeneration, BeforeFirstWork, observedThrough ??= Watermark(null), scope);
        var merged = false;
        while (Rows(null, StateSchema.SelectWork, (StateSchema.ParamPairRow, pair.Row),
                   (StateSchema.ParamGeneration, expectedGeneration), (StateSchema.ParamAfter, pass.After),
                   (StateSchema.ParamThrough, pass.Through), (StateSchema.ParamTake, StateSchema.OneRow)).FirstOrDefault()
               is { } candidate)
        {
            pass = pass with { After = candidate.WorkId };
            if (pass.Attempted?.Contains(candidate.WorkId) == true) continue;
            if (CoalescePathWork(pairId, pair, candidate, pass, ref observedThrough) is > 0 and var retired)
            {
                _passes[pairId] = pass;
                AccountAttachment(pairId, -retired);
                merged = true;
                continue;
            }
            BootstrapRename(pairId, pair, candidate, pass.Through);
            if (Held(pair.Row, candidate.WorkId) ||
                Earliest(pair.Row, pairId, candidate, candidate.WorkId).WorkId != candidate.WorkId)
                continue;
            _passes[pairId] = pass;
            return Selected(pairId, candidate, pass.Through);
        }
        if (merged) _passes[pairId] = pass;
        return null;
    }

    // One custody transaction: validate the contiguous segment, transfer its debt, then delete those exact sources.
    // Both walks share the same fenced key and bounds; only the committed count reaches pass accounting.
    private long CoalescePathWork(string pairId, PairRow pair, WorkRow candidate, Pass pass, ref long? observedThrough)
    {
        if (candidate.Side != Side.Local || candidate.Identity is not null || candidate.Parent is not null ||
            candidate.OldKey is not null || candidate.DiscoveryRequired || candidate.Notice is null ||
            candidate.Acquisition is null || candidate.Obligation is not null) return 0;
        var through = observedThrough ??= Watermark(null);
        WorkRow? Next(SqliteTransaction? transaction, long after) => Rows(transaction, StateSchema.PeerPathPage,
            Evidence(pair.Row, pair.Generation, candidate.Side, candidate.SourceEpoch, candidate.Domain,
                (StateSchema.ParamJoinKey, candidate.Key), (StateSchema.ParamAfter, after),
                (StateSchema.ParamThrough, through), (StateSchema.ParamTake, StateSchema.OneRow))).FirstOrDefault();
        if (Next(null, candidate.WorkId) is not { } successor ||
            TransferableWork(null, pair.Row, candidate, null, pathOnly: true) is null ||
            TransferableWork(null, pair.Row, successor, null, pathOnly: true) is null ||
            candidate.Before is not null && successor.Before is not null &&
            !string.Equals(candidate.Before, successor.Before, StringComparison.Ordinal)) return 0;
        try
        {
            using var transaction = _connection.BeginTransaction();
            var selection = ToSelection(pairId, candidate, pass.Through);
            pair = RequireCurrent(transaction, selection);
            var first = RequireAnchor(transaction, pair.Row, selection);
            if (TransferBound(transaction, pair, first, pass, through) is not { } bound) return 0;
            through = bound;
            ChangeNotice? notice = null;
            string? before = null;
            var acquisition = BeforeFirstWork;
            var newest = first;
            long validated = 0;
            for (var row = first; row is not null && row.WorkId <= through; row = Next(transaction, row.WorkId))
            {
                if (TransferableWork(transaction, pair.Row, row, null, pathOnly: true) is not { } content ||
                    before is not null && row.Before is not null && !string.Equals(before, row.Before, StringComparison.Ordinal)) break;
                notice = content.Notice;
                before ??= row.Before;
                acquisition = Math.Max(acquisition, row.Acquisition!.Value);
                newest = row;
                validated++;
            }
            if (newest.WorkId == first.WorkId) return 0;
            long deleted = 0;
            try
            {
                Require(Run(transaction, StateSchema.MergeTransferredWork, (StateSchema.ParamPairRow, pair.Row),
                    (StateSchema.ParamWork, newest.WorkId), (StateSchema.ParamAcquisition, acquisition),
                    (StateSchema.ParamBefore, before), (StateSchema.ParamNotice, StateEncoding.Envelope(notice!))));
                for (var row = first; row is not null && row.WorkId < newest.WorkId; row = Next(transaction, row.WorkId))
                {
                    Require(Run(transaction, StateSchema.DeleteWork, (StateSchema.ParamPairRow, pair.Row),
                        (StateSchema.ParamWork, row.WorkId)));
                    deleted++;
                }
                if (deleted != validated - StateSchema.OneRow) throw new InvalidOperationException();
                transaction.Commit();
            }
            catch (SqliteException error) when (IntegrityRefusal(error))
            {
                throw RollbackMutation(transaction, selection, error);
            }
            return deleted;
        }
        // Only a certified rollback permits ordinary selection; disposal and non-certified failures still escape.
        catch (ItemStoreMutationException) { return 0; }
    }

    private long? TransferBound(SqliteTransaction transaction, PairRow pair, WorkRow first, Pass pass, long through)
    {
        foreach (var episode in Read(transaction, StateSchema.ItemEpisodesAtPath,
            static reader => reader.GetInt64(StateSchema.EpisodePathWork),
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamSide, (int)Side.Local),
            (StateSchema.ParamPath, first.Key)))
            if (episode >= first.WorkId) through = Math.Min(through, episode - StateSchema.OneRow);
        if (pass.Claims is { } claims)
            foreach (var claim in claims.Values)
                if (FindAnchor(transaction, pair.Row, claim) is { } anchor &&
                    (Owned(claim, anchor, first) || claim.Side == Side.Local && claim.Key == first.Key ||
                     claim.Side == Side.Dropbox && claim.Peer?.Key == first.Key)) return null;
        // EndSelection adds every suspended dependent to Attempted; only prerequisites need a separate walk.
        foreach (var protectedId in (pass.Attempted ?? Enumerable.Empty<long>())
            .Concat(pass.Suspended?.Values ?? Enumerable.Empty<long>()))
        {
            if (protectedId < first.WorkId || protectedId > through) continue;
            var protectedRow = Rows(transaction, StateSchema.Anchor, (StateSchema.ParamAnchor, protectedId),
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, pair.Generation),
                (StateSchema.ParamEpoch, first.SourceEpoch)).FirstOrDefault();
            if (protectedRow is { Side: Side.Local } && protectedRow.Domain == first.Domain && protectedRow.Key == first.Key)
                through = Math.Min(through, protectedId - StateSchema.OneRow);
        }
        if (first.WorkId > through || Read(transaction, StateSchema.LocalEchoPending,
            static reader => reader.GetBoolean(StateSchema.FirstColumn), (StateSchema.ParamPairRow, pair.Row),
            (StateSchema.ParamLocalKey, first.Key), (StateSchema.ParamDropboxKey, first.Key),
            (StateSchema.ParamIsFolder, false)).First()) return null;
        return through;
    }

    /// <summary>Registers an endpoint unbound by a committed replacement in the caller's transaction, and asks the
    /// active pass for a repeat.</summary>
    internal void RegisterReleasedEndpoint(SqliteTransaction transaction, string pairId,
        Side side, string path, string id)
    {
        var pair = RequirePair(transaction, pairId);
        AddEndpoint(transaction, pair, side, ReadSourceRow(transaction, pair.Row, side)?.Epoch ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged), path, id);
        if (_passes.TryGetValue(pairId, out var pass) && pass.Generation == pair.Generation) pass.Released = true;
    }

    /// <summary>Fences one temporary turn in the caller's transaction (the pair at its generation, the turn's side at
    /// its epoch, each read once) and registers a successor as ordinary endpoint work that requests no repeat. Returns
    /// the pair row and both sources' fences for the retry-state CAS.</summary>
    internal (long Pair, ((long Epoch, long Revision) Local, (long Epoch, long Revision) Dropbox) Fences) RetainTemporary(
        SqliteTransaction transaction, string pairId, long expectedGeneration, Side side, long sourceEpoch,
        (Side Side, string Path, string? Id)? successor)
    {
        if (ReadPair(transaction, pairId) is not { } pair || pair.Generation != expectedGeneration)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var fences = ReadRetryFences(transaction, pair.Row);
        if ((side == Side.Local ? fences.Local : fences.Dropbox).Epoch != sourceEpoch)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        if (successor is { } endpoint) AddEndpoint(transaction, pair, side, sourceEpoch, endpoint.Path, endpoint.Id);
        return (pair.Row, fences);
    }

    /// <summary>Both sources' epoch and revision at the caller's pair row, one read each, for a retry-state CAS. A
    /// missing source row is stale.</summary>
    internal ((long Epoch, long Revision) Local, (long Epoch, long Revision) Dropbox) ReadRetryFences(
        SqliteTransaction transaction, long pair) =>
        ReadSourceRow(transaction, pair, Side.Local) is { } local &&
        ReadSourceRow(transaction, pair, Side.Dropbox) is { } dropbox
            ? ((local.Epoch, local.Revision), (dropbox.Epoch, dropbox.Revision))
            : throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);

    // The shared insertion: ordinary endpoint work requiring fresh selection. A repeat is the caller's request.
    private void AddEndpoint(SqliteTransaction transaction, PairRow pair, Side side, long epoch, string path,
        string? id)
    {
        var domain = Domain(Roots(pair), side) ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var workId = checked(Watermark(transaction) + 1);
        using var insert = WorkInsert(transaction);
        AddWork(insert, workId, pair.Row, pair.Generation, epoch, domain,
            new ChangeNotice(side, ChangeKind.Changed, path, null, id, null, false, false),
            null, workId, null, null);
    }

    private void BootstrapRename(string pairId, PairRow pair, WorkRow work, long through)
    {
        if (work.Notice is null || work.Identity is null || ReadRenameContinuation(null, pair.Row, work.WorkId) is not null ||
            Read(null, StateSchema.ItemEpisodeByWork, static reader => reader.GetInt64(StateSchema.EpisodeByWorkIssueId),
                (StateSchema.ParamWork, work.WorkId)).Any()) return;
        var issueId = Read(null, StateSchema.WorkIssue, static reader => Number(reader, StateSchema.WorkIssueId),
            (StateSchema.ParamWork, work.WorkId)).FirstOrDefault();
        if (issueId is null || IssueTable.ById(_connection, pairId, issueId.Value) is not { } issue ||
            !IssuePolicy.RenameClashMatches(issue, work.Side, work.Key, work.Identity)) return;
        var notice = StateEncoding.DecodeEnvelope<ChangeNotice>(work.Notice)!;
        var selection = ToSelection(pairId, work, through);
        var incoming = SelectionBaseline(null, selection);
        if (incoming is null || incoming.IdOn(work.Side) != work.Identity || incoming.KeyOn(work.Side) == work.Key) return;
        var opposite = PathNames.Other(work.Side);
        var occupant = issue.Subject.Items.FirstOrDefault(item => item.Side == opposite);
        if (occupant is null) return;
        var destination = Read(null, opposite == Side.Local ? StateSchema.SelectSyncedByLocalId : StateSchema.SelectSyncedByDropboxId,
            ReadSaved, (StateSchema.ParamPair, pairId), (StateSchema.ParamId, StateEncoding.IdFor(opposite, occupant.Id))).FirstOrDefault();
        if (destination is null) return;
        using (var snapshot = _connection.BeginTransaction(deferred: true))
        {
            var owners = RenameDestinations.All(_connection, snapshot, pairId, incoming,
                RenameTarget(snapshot, selection, incoming));
            if (owners.Count != 1 || owners[0].Binding != destination) return;
            snapshot.Commit();
        }
        RegisterRenameContinuation(selection, incoming, destination, issue);
    }

    internal WorkSelection RegisterDiscoveredRename(string pairId, long generation, RenameCandidate candidate)
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequirePair(transaction, pairId);
        var source = ReadSourceRow(transaction, pair.Row, candidate.Side);
        var domain = Domain(Roots(pair), candidate.Side);
        if (pair.Generation != generation || source is null || domain is null)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var saved = Read(transaction, StateSchema.SelectSyncedByLocalKey, ReadSaved,
            (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, candidate.Synced.LocalKey)).FirstOrDefault();
        if (saved != candidate.Synced || candidate.Moved.Id != saved.IdOn(candidate.Side))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        var key = PathNames.KeyFor(candidate.Side, candidate.Moved.Path);
        var existing = Rows(transaction, StateSchema.FindRenameWork,
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, generation),
            (StateSchema.ParamSide, (int)candidate.Side), (StateSchema.ParamEpoch, source.Epoch),
            (StateSchema.ParamDomain, domain), (StateSchema.ParamIdentity, Identity(candidate.Side, candidate.Moved.Id)),
            (StateSchema.ParamPath, key)).FirstOrDefault();
        if (existing is not null) return Selected(pairId, existing, Watermark(transaction), transaction);
        var id = checked(Watermark(transaction) + 1);
        using var insert = WorkInsert(transaction);
        var notice = new ChangeNotice(candidate.Side, ChangeKind.Renamed, candidate.Moved.Path,
            candidate.Synced.PathOn(candidate.Side), candidate.Moved.Id, candidate.Moved, false, false);
        AddWork(insert, id, pair.Row, generation, source.Epoch, domain, notice, candidate.Synced, id, null, null);
        var selection = new WorkSelection(pairId, generation, source.Epoch, candidate.Side, domain,
            candidate.Moved.Id, key, id, id);
        selection = selection with
            { Obligation = AcquiredObligation(domain, notice, SelectionBaseline(transaction, selection)) };
        transaction.Commit();
        return selection;
    }

    internal RenameContinuation? ReadRenameContinuation(WorkSelection selection, SqliteTransaction? transaction = null)
    {
        var pair = RequireCurrent(transaction, selection);
        RequireAnchor(transaction, pair.Row, selection);
        return ReadRenameContinuation(transaction, pair.Row, selection.AnchorWorkId);
    }

    private RenameContinuation? ReadRenameContinuation(SqliteTransaction? transaction, long pair, long work) =>
        Read(transaction, StateSchema.ReadRenameContinuation,
            static reader => StateEncoding.ReadOptionalEnvelope<RenameContinuation>(reader, StateSchema.FirstColumn),
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, work)).FirstOrDefault();

    private SyncedEntry RenameTarget(SqliteTransaction transaction, WorkSelection selection, SyncedEntry incoming)
    {
        var other = PathNames.Other(selection.Side);
        var counterpart = PathNames.MappedDestination(other, selection.Key, this,
            (owner, side, key) => owner.Read(transaction,
                side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey,
                ReadSaved, (StateSchema.ParamPair, selection.PairId), (StateSchema.ParamTargetKey, key)).FirstOrDefault());
        var local = selection.Side == Side.Local ? selection.Key : counterpart;
        var dropbox = selection.Side == Side.Dropbox ? selection.Key : counterpart;
        return incoming with { LocalPath = local, LocalKey = PathNames.Key(local),
            DropboxPath = dropbox, DropboxKey = PathNames.DropboxKey(dropbox) };
    }

    internal IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> ReconcileRenameContinuation(
        WorkSelection selection, SyncedEntry incoming)
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequireCurrent(transaction, selection);
        var anchor = RequireAnchor(transaction, pair.Row, selection);
        if (anchor.Notice is null || SelectionBaseline(transaction, selection) != incoming)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        var owners = RenameDestinations.All(_connection, transaction, selection.PairId, incoming,
            RenameTarget(transaction, selection, incoming));
        var current = ReadRenameContinuation(transaction, pair.Row, anchor.WorkId);
        var linked = Read(transaction, StateSchema.ReadRenameRemoval, static reader => reader.GetString(StateSchema.FirstColumn),
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, anchor.WorkId)).Any();
        if (current?.Clash is null && !linked)
        {
            var next = owners.Count == 1 ? new RenameContinuation(owners[0].Binding, null) : null;
            if (next != current)
                Require(Run(transaction, StateSchema.ReplaceRenameContinuation,
                    (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, anchor.WorkId),
                    (StateSchema.ParamExpected, StateEncoding.OptionalEnvelope(current)),
                    (StateSchema.ParamNotice, StateEncoding.OptionalEnvelope(next))));
        }
        transaction.Commit();
        return owners;
    }

    internal void RegisterRenameContinuation(WorkSelection selection, SyncedEntry incoming,
        SyncedEntry destination, Issue? clash)
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequireCurrent(transaction, selection);
        var anchor = RequireAnchor(transaction, pair.Row, selection);
        if (anchor.Notice is null || SelectionBaseline(transaction, selection) != incoming)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        if (clash is not null) RequireIssue(transaction, pair.Row, clash);
        var saved = Read(transaction, StateSchema.SelectSyncedByLocalKey, ReadSaved,
            (StateSchema.ParamPair, selection.PairId), (StateSchema.ParamTargetKey, destination.LocalKey)).FirstOrDefault();
        if (saved != destination) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        var next = new RenameContinuation(destination, clash);
        var current = ReadRenameContinuation(transaction, pair.Row, anchor.WorkId);
        if (current is not null && (current.Destination != destination ||
            (current.Clash is null ? clash is not null : clash is null || !current.Clash.Matches(clash))))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        if (current is null)
            Require(Run(transaction, StateSchema.SaveRenameContinuation,
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, anchor.WorkId),
                (StateSchema.ParamNotice, StateEncoding.Envelope(next))));
        transaction.Commit();
    }

    internal void SettleRenameContinuation(SqliteTransaction transaction, WorkSelection selection, StateChange change)
    {
        var pair = RequireCurrent(transaction, selection);
        var anchor = RequireAnchor(transaction, pair.Row, selection);
        var continuation = ReadRenameContinuation(transaction, pair.Row, anchor.WorkId);
        if (continuation is null) return;
        var renamed = change.Renames.Any(rename => rename.After.IdOn(selection.Side) == selection.Identity &&
            rename.After.KeyOn(selection.Side) == selection.Key);
        var clashed = continuation.Clash is null && change.Raise.Any(issue => issue.Kind == IssueKind.NameClash) &&
            !Read(transaction, StateSchema.ReadRenameRemoval, static reader => reader.GetString(StateSchema.FirstColumn),
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, anchor.WorkId)).Any();
        if (!renamed && !clashed) return;
        if (continuation.Clash is { } clash) IssueTable.CloseExact(_connection, transaction, selection.PairId, clash);
        Run(transaction, StateSchema.ReleaseCompletedRenameRemoval,
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, anchor.WorkId));
        Require(Run(transaction, StateSchema.ClearRenameContinuation,
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, anchor.WorkId),
            (StateSchema.ParamNotice, StateEncoding.Envelope(continuation))));
    }

    internal bool EndWorkPass(string pairId) =>
        _passes.Remove(pairId, out var pass) && (pass.NetRetired > 0 || pass.Released);

    internal void RecordRetirement(string pairId)
    {
        if (_passes.TryGetValue(pairId, out var pass)) pass.NetRetired++;
    }

    /// <summary>The selection's rows in work order; keyset paged so no reader outlives a page. A source-less anchor
    /// projects its issue member and never decodes a NULL notice.</summary>
    internal IEnumerable<PendingChange> ReadWork(WorkSelection selection)
    {
        var pair = RequireCurrent(null, selection);
        var anchor = RequireAnchor(null, pair.Row, selection);
        if (anchor.Notice is null)
        {
            yield return ProjectIssueWork(selection, anchor);
            yield break;
        }
        PendingChange Decode(SqliteDataReader reader) => new(reader.GetInt64(StateSchema.WorkId), selection.PairId,
            selection.PairGeneration, selection.SourceEpoch,
            StateEncoding.ReadEnvelope<ChangeNotice>(reader, StateSchema.WorkNoticeJson),
            StateEncoding.ReadOptionalEnvelope<SyncedEntry>(reader, StateSchema.WorkBeforeJson))
        { IsResidual = !reader.IsDBNull(StateSchema.WorkParentAnchor),
            DiscoveryRequired = StateEncoding.ReadBool(reader, StateSchema.WorkDiscoveryRequired) };
        if (selection.Identity is null)
        {
            var row = Read(null, StateSchema.ReadPathSelection, Decode, Membership(pair.Row, selection, anchor))
                .FirstOrDefault();
            if (row is not null) yield return row;
            yield break;
        }
        var after = BeforeFirstWork;
        while (Read(null, ReadIdentity(anchor), Decode, Membership(pair.Row, selection, anchor,
                   (StateSchema.ParamAfter, after), (StateSchema.ParamTake, StateSchema.OneRow))).FirstOrDefault() is { } change)
        {
            after = change.WorkId;
            yield return change;
            anchor = RequireAnchor(null, pair.Row, selection);
        }
    }

    /// <summary>Unresolved selections at any of the owner-supplied ancestor keys, including their old paths and
    /// registered residual aliases, so they take precedence over dependent descendants across restart. A residual
    /// answers with the parent selection that still owns it.</summary>
    internal IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long? coveredThrough = null)
    {
        var seen = new HashSet<long>();
        var through = Watermark(null);
        foreach (var (row, _) in AncestorRows(pairId, expectedGeneration, side, ancestorKeys, coveredThrough))
        {
            var owner = Earliest(row.PairRow, pairId, ParentOwner(row.PairRow, row), through);
            if (owner.WorkId <= through && seen.Add(owner.WorkId) &&
                (coveredThrough is not null || !Held(owner.PairRow, owner.WorkId)))
                yield return Selected(pairId, owner, through);
        }
    }

    internal AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> keys, long coveredThrough)
    {
        var news = AncestorNews.None;
        string? unbound = null;
        string? savedKey = null;
        SyncedEntry? saved = null;
        foreach (var (row, key) in AncestorRows(pairId, expectedGeneration, side, keys, coveredThrough))
        {
            if (row.Key != key || row.OldKey is not null || row.Parent is not null || row.Obligation is not null ||
                row.Notice is null || Read(null, StateSchema.WorkIssue,
                    static reader => reader.GetInt64(StateSchema.FirstColumn), (StateSchema.ParamWork, row.WorkId)).Any())
                return new(AncestorNews.Changed);
            var notice = StateEncoding.DecodeEnvelope<ChangeNotice>(row.Notice);
            if (notice is not { Kind: ChangeKind.Changed, OldPath: null, IdentityDeleted: false,
                    Observed: { IsFolder: true, Skip: null } observed })
                return new(AncestorNews.Changed);
            if (savedKey != key)
            {
                savedKey = key;
                saved = Read(null, side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey,
                    ReadSaved, (StateSchema.ParamPair, pairId), (StateSchema.ParamTargetKey, key)).FirstOrDefault();
            }
            if (saved is null) unbound ??= key;
            else if (!saved.IsFolder || (saved.IdOn(side)) != observed.Id) return new(AncestorNews.Changed);
            news = AncestorNews.Restated;
        }
        return unbound is null ? new(news) : new(AncestorNews.Unbound, unbound);
    }

    private IEnumerable<(WorkRow Row, string Key)> AncestorRows(string pairId, long expectedGeneration,
        Side side, IReadOnlyList<string> keys, long? coveredThrough)
    {
        if (ReadPair(null, pairId) is not { } pair || pair.Generation != expectedGeneration ||
            ReadSourceRow(null, pair.Row, side) is not { } source || Domain(Roots(pair), side) is not { } domain)
            yield break;
        foreach (var key in keys)
            foreach (var probe in ExactProbes)
            {
                var rows = Rows(null, probe, Evidence(pair.Row, expectedGeneration, side, source.Epoch, domain,
                    (StateSchema.ParamJoinKey, key))).ToList();
                foreach (var row in rows)
                {
                    // Coverage belongs to the arrival, not its possibly older owner.
                    if (row.WorkId <= coveredThrough) continue;
                    yield return (row, key);
                }
            }
    }

    internal Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection)
    {
        var pair = RequireCurrent(null, selection);
        var anchor = RequireAnchor(null, pair.Row, selection);
        var other = PathNames.Other(selection.Side);
        var otherEpoch = ReadSourceRow(null, pair.Row, other)!.Epoch;
        var otherDomain = Domain(Roots(pair), other)!;
        return (side, key, identity) =>
        {
            var source = side == selection.Side ? selection.SourceEpoch : otherEpoch;
            var domain = side == selection.Side ? selection.Domain : otherDomain;
            foreach (var probe in ExactProbes)
                if (Rows(null, probe, Evidence(pair.Row, selection.PairGeneration, side,
                    source, domain, (StateSchema.ParamJoinKey, key)))
                    .Any(row => !Owned(selection, anchor, row))) return true;
            return identity is not null && UnownedIdentity(pair.Row, selection, anchor, side, source, domain, identity);
        };
    }

    // Acquired same-identity work the selection does not own, at any acquisition.
    private bool UnownedIdentity(long pair, WorkSelection selection, WorkRow anchor, Side side, long source,
        string domain, string identity) => Rows(null, StateSchema.EvidenceIdentity,
            Evidence(pair, selection.PairGeneration, side, source, domain,
                (StateSchema.ParamIdentity, StateEncoding.IdFor(side, identity))))
        .Any(row => !Owned(selection, anchor, row));

    internal Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection)
    {
        var pair = RequireCurrent(null, selection);
        var pass = _passes.GetValueOrDefault(selection.PairId) is { Scope: not null } active ? active : null;
        var responsibility = pass?.LocalEchoResponsibility ?? ReadLocalEchoResponsibility(pair.Row);
        if (pass is not null) pass.LocalEchoResponsibility = responsibility;
        var roots = Roots(pair);
        return (saved, through) => !responsibility(saved) && CanSettleLocalEcho(selection, saved, through, roots);
    }

    private Func<SyncedEntry, bool> ReadLocalEchoResponsibility(long pairRow)
    {
        var keys = new PrefixSet();
        void Add(string? path)
        {
            if (path is null) return;
            var key = PathNames.Key(path);
            keys.Add(key);
        }
        foreach (var row in Read(null, StateSchema.LocalEchoResponsibilities, reader =>
            (Path: reader.IsDBNull(0) ? null : reader.GetString(0),
             Subject: reader.IsDBNull(StateSchema.EchoSubject) ? null : reader.GetString(StateSchema.EchoSubject),
             Saved: reader.IsDBNull(StateSchema.EchoSaved) ? null : reader.GetString(StateSchema.EchoSaved)), (StateSchema.ParamPairRow, pairRow)))
        {
            Add(row.Path);
            if (row.Subject is not null)
                foreach (var item in System.Text.Json.JsonSerializer.Deserialize<IssueSubject>(row.Subject)!.Items) Add(item.Path);
            if (row.Saved is not null)
            {
                var saved = StateEncoding.DecodeEnvelope<SyncedEntry>(row.Saved);
                Add(saved?.LocalPath);
                Add(saved?.DropboxPath);
            }
        }
        // Issues raised mid-pass cannot invalidate unchanged Local-only settlement; the next pass sees them.
        // Folder effects retain their acquired work; pending effects and source/work fences are checked fresh below.
        return saved => keys.ResponsibilityOverlaps(saved.LocalKey, saved.IsFolder) ||
            saved.LocalKey != saved.DropboxKey && keys.ResponsibilityOverlaps(saved.DropboxKey, saved.IsFolder);
    }

    private bool CanSettleLocalEcho(WorkSelection selection, SyncedEntry saved, long throughWorkId, RootBinding? roots)
    {
        // The whole call holds StateStore's _lock, so these fences cannot change before this guard returns.
        var pair = ReadPair(null, selection.PairId);
        if (pair is null || pair.Generation != selection.PairGeneration)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var selectedSource = ReadSourceRow(null, pair.Row, selection.Side);
        if (selectedSource?.Epoch != selection.SourceEpoch)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var anchor = RequireAnchor(null, pair.Row, selection);
        if (selection.Side != Side.Local) return false;
        var source = ReadSourceRow(null, pair.Row, Side.Dropbox);
        if (OwesRepair(selectedSource) || OwesRepair(source) || anchor.Parent is not null || anchor.Obligation is not null ||
            source is not { Cursor: not null, Provenance: not null } ||
            string.IsNullOrEmpty(source.Cursor) || Domain(roots, Side.Dropbox) is not { } domain ||
            Read(null, StateSchema.WorkIssue, static reader => reader.GetInt64(StateSchema.FirstColumn),
                (StateSchema.ParamWork, anchor.WorkId)).Any() ||
            Read(null, StateSchema.WorkHasChildren, static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
                (StateSchema.ParamParent, anchor.WorkId)).First()) return false;
        if (Rows(null, StateSchema.PeerPathPage,
            Evidence(pair.Row, selection.PairGeneration, Side.Dropbox, source.Epoch, domain,
                (StateSchema.ParamJoinKey, saved.DropboxKey), (StateSchema.ParamAfter, BeforeFirstWork),
                (StateSchema.ParamThrough, throughWorkId), (StateSchema.ParamTake, StateSchema.OneRow))).Any()) return false;
        return !Read(null, StateSchema.LocalEchoPending,
            static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamLocalKey, saved.LocalKey),
            (StateSchema.ParamDropboxKey, saved.DropboxKey), (StateSchema.ParamIsFolder, saved.IsFolder)).First();
    }

    /// <summary>Starts a selection with its identity's current saved binding. Only this boundary replaces an old
    /// preimage; admission still refuses changes that happen after this comparison. Returns the selection that runs,
    /// keyed from the preimage written here, or null when already attempted.</summary>
    internal WorkSelection? BeginSelection(WorkSelection selection)
    {
        _passes.TryGetValue(selection.PairId, out var pass);
        if (Attempted(pass, selection)) return null;
        using var transaction = _connection.BeginTransaction();
        var pair = RequireCurrent(transaction, selection);
        var anchor = RequireAnchor(transaction, pair.Row, selection);
        var baseline = SelectionBaseline(transaction, selection);
        var before = StateEncoding.OptionalEnvelope(baseline);
        if (anchor.Before != before)
            RequireSome(Run(transaction, selection.Identity is null ? StateSchema.SaveBeforePath
                : SaveBeforeIdentity(anchor), Membership(pair.Row, selection, anchor,
                    (StateSchema.ParamNextBefore, before))));
        transaction.Commit();
        // The attempt that may end temporarily is the one with this preimage, so its run-exclusion key is fixed here.
        var begun = Keyed(selection, anchor, baseline);
        if (pass is null)
            _passes[selection.PairId] = pass = new Pass(selection.PairGeneration, BeforeFirstWork, selection.ThroughWorkId, null);
        pass.Suspended?.Remove(selection.AnchorWorkId);
        if (pass.Scope is not null) (pass.Attempted ??= []).Add(selection.AnchorWorkId);
        (pass.Claims ??= [])[selection.AnchorWorkId] = begun;
        return begun;
    }

    internal bool WorkAttempted(WorkSelection selection) =>
        _passes.TryGetValue(selection.PairId, out var pass) && pass.Generation == selection.PairGeneration &&
        Attempted(pass, selection);

    private static bool Attempted(Pass? pass, WorkSelection selection) =>
        pass?.Attempted?.Contains(selection.AnchorWorkId) == true &&
        (pass.Suspended is not { } suspended || !suspended.TryGetValue(selection.AnchorWorkId, out var dependency) ||
            pass.Completed?.Contains(dependency) != true);

    internal void RequireDiscovery(WorkSelection selection, long workId)
    {
        using var transaction = _connection.BeginTransaction();
        var pair = RequireCurrent(transaction, selection);
        var anchor = RequireAnchor(transaction, pair.Row, selection);
        RequireSome(Run(transaction, selection.Identity is null ? StateSchema.RequirePathDiscovery
            : StateSchema.RequireIdentityDiscovery, Membership(pair.Row, selection, anchor,
                (StateSchema.ParamWork, workId))));
        transaction.Commit();
    }

    internal void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer)
    {
        if (_passes.GetValueOrDefault(selection.PairId)?.Claims is not { } claims ||
            !claims.TryGetValue(selection.AnchorWorkId, out var claimed) || claimed != selection with { Peer = claimed.Peer, Obligation = claimed.Obligation })
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        claims[selection.AnchorWorkId] = claimed with { Peer = peer };
    }

    internal void EndSelection(WorkSelection selection, WorkSelection? prerequisite = null)
    {
        if (_passes.GetValueOrDefault(selection.PairId) is not { } pass)
        {
            if (prerequisite is not null) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return;
        }
        if (prerequisite is not null)
        {
            if (pass.Claims is not { } claims || !claims.TryGetValue(selection.AnchorWorkId, out var claimed) ||
                claimed != selection with { Peer = claimed.Peer, Obligation = claimed.Obligation } || prerequisite.PairId != selection.PairId ||
                prerequisite.PairGeneration != selection.PairGeneration || prerequisite.AnchorWorkId == selection.AnchorWorkId)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var pair = RequireCurrent(null, prerequisite);
            RequireAnchor(null, pair.Row, prerequisite);
            (pass.Suspended ??= [])[selection.AnchorWorkId] = prerequisite.AnchorWorkId;
            (pass.Attempted ??= []).Add(selection.AnchorWorkId);
        }
        else pass.Suspended?.Remove(selection.AnchorWorkId);
        pass.Claims?.Remove(selection.AnchorWorkId);
        if (pass.Scope is null && pass.Claims?.Count == 0 && pass.Suspended?.Count is not > 0)
            _passes.Remove(selection.PairId);
    }

    internal void RetireCoveredWork(SqliteTransaction transaction, string pairId, long pair,
        long generation, long localEpoch, long dropboxEpoch, long through)
    {
        var arguments = new List<(string Name, object? Value)>
        {
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, generation), (StateSchema.ParamThrough, through)
        };
        var index = 0;
        if (_passes.GetValueOrDefault(pairId)?.Claims is { Count: > 0 } claims)
        {
            var current = RequirePair(transaction, pairId);
            var roots = Roots(current);
            foreach (var selection in claims.Values)
            {
                if (FindAnchor(transaction, pair, selection) is not { } anchor) continue;
                var peerSide = PathNames.Other(selection.Side);
                var peerSource = ReadSourceRow(transaction, pair, peerSide);
                var own = Membership(pair, selection, anchor,
                    selection.Identity is null ? (StateSchema.ParamIdentity, null) : (StateSchema.ParamPath, selection.Key));
                var peer = Evidence(pair, selection.PairGeneration, peerSide, peerSource?.Epoch ?? BeforeFirstWork,
                    Domain(roots, peerSide)!, (StateSchema.ParamJoinKey, selection.Peer?.Key),
                    (StateSchema.ParamThrough, selection.Peer?.ThroughWorkId ?? BeforeFirstWork),
                    (StateSchema.ParamBefore, anchor.Before));
                arguments.AddRange(own.Select(arg => (StateSchema.ClaimParameter(index, arg.Name), arg.Value)));
                arguments.AddRange(peer.Select(arg => (StateSchema.ClaimParameter(index,
                    StateSchema.PeerParameter(arg.Name)), arg.Value)));
                index++;
            }
        }
        using var retire = StorageCommand.Create(_connection, transaction, StateSchema.RetireUnclaimedWork(index),
            [.. arguments, (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamEpoch, localEpoch)]);
        retire.ExecuteNonQuery();
        retire.Parameters[StateSchema.ParamSide].Value = (int)Side.Dropbox;
        retire.Parameters[StateSchema.ParamEpoch].Value = dropboxEpoch;
        retire.ExecuteNonQuery();
    }

    internal SyncedEntry? SelectionBaseline(SqliteTransaction? transaction, WorkSelection selection) =>
        Read(transaction, selection.Identity is null
            ? selection.Side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey
            : selection.Side == Side.Local ? StateSchema.SelectSyncedByLocalId : StateSchema.SelectSyncedByDropboxId,
            ReadSaved, (StateSchema.ParamPair, selection.PairId),
            selection.Identity is null ? (StateSchema.ParamTargetKey, (object?)selection.Key) :
                (StateSchema.ParamId, StateEncoding.IdFor(selection.Side, selection.Identity))).FirstOrDefault();

    // One baseline advance: every branch mutates the same after and affected accumulator, then writes one expected-state update.
    internal void AdvanceBefore(SqliteTransaction transaction, string pairId, WorkSelection selection,
        StateChange change)
    {
        if (selection.PairId != pairId)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (change.Record.Count == 0 && change.Renames.Count == 0 &&
            change.ForgetSelf.Count == 0 && change.ForgetSubtrees.Count == 0 && change.ForgetBothSubtrees.Count == 0) return;
        var pair = RequireCurrent(transaction, selection);
        var anchor = RequireAnchor(transaction, pair.Row, selection);
        var before = anchor.Before is null ? null : StateEncoding.DecodeEnvelope<SyncedEntry>(anchor.Before);
        var after = before;
        var affected = false;
        bool Selected(SyncedEntry row) => selection.Identity is { } identity
            ? row.IdOn(selection.Side) == identity : row.KeyOn(selection.Side) == selection.Key;
        foreach (var rename in change.Renames)
        {
            if (rename.Before is null) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
            if (!Selected(rename.Before) && !Selected(rename.After)) continue;
            after = Selected(rename.After) ? rename.After : null;
            affected = true;
        }
        if (after is not null && (change.StaleBindings.Contains(after) ||
            change.ForgetBothSubtrees.Any(item => PathNames.IsSelfOrUnderKey(after.LocalKey, item.LocalKey) &&
                PathNames.IsSelfOrUnderKey(after.DropboxKey, item.DropboxKey)) ||
            change.ForgetSelf.Any(item => after.KeyOn(item.Side) == item.Key) ||
            change.ForgetSubtrees.Any(item => PathNames.IsSelfOrUnderKey(after.KeyOn(item.Side), item.Key))))
        {
            after = null;
            affected = true;
        }
        foreach (var record in change.Record)
        {
            if (Selected(record)) { after = record; affected = true; }
            else if (after is not null && (record.LocalKey == after.LocalKey || record.DropboxKey == after.DropboxKey))
            { after = null; affected = true; }
        }
        if (!affected) return;
        if (SelectionBaseline(transaction, selection) != before)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        var nextBefore = StateEncoding.OptionalEnvelope(after);
        foreach (var peer in OwnedPeers(transaction, pair, selection, anchor, selection.Peer))
            RequireSome(Run(transaction, peer.Identity is null ? StateSchema.SaveBeforePath : SaveBeforeIdentity(peer),
                Membership(pair.Row, ToSelection(pairId, peer, peer.WorkId), peer, (StateSchema.ParamNextBefore, nextBefore))));
        RequireSome(Run(transaction, selection.Identity is null ? StateSchema.SaveBeforePath : SaveBeforeIdentity(anchor),
            Membership(pair.Row, selection, anchor, (StateSchema.ParamNextBefore, nextBefore))));
    }

    /// <summary>Retires exact membership, which releases its registered residuals in the same commit. Later arrivals,
    /// issue-owned work and unrelated dirty evidence stay.</summary>
    // Reason (standards 1.3): one committed completion job. The integrity handler must sit inside this transaction's
    // lifetime and the post-commit bookkeeping outside it, and the steps share the transaction and the retired count;
    // extracting them would only pass that state across a boundary. Size is reviewed in the structural ledger.
    internal bool CompleteWork(WorkSelection selection, SelectionPeer? peer)
    {
        EndSelection(selection);
        var retired = 0;
        // B-204: the integrity certificate covers only Retire, owned-peer retirement and the commit inside the
        // transaction. Its disposal and the pass bookkeeping stay outside, so a later failure is never certified rollback.
        using (var transaction = _connection.BeginTransaction())
        {
            var pair = RequireCurrent(transaction, selection);
            var anchor = RequireAnchor(transaction, pair.Row, selection);
            if (anchor.Parent is { } owner && WorkExists(pair.Row, owner, transaction) ||
                Read(transaction, StateSchema.WorkIssue, static reader => reader.GetInt64(StateSchema.FirstColumn),
                    (StateSchema.ParamWork, anchor.WorkId)).Any() ||
                // B-203: an anchor a live episode still names is that episode's responsibility, retained, not retired.
                Read(transaction, StateSchema.ItemEpisodeByWork, static reader => reader.GetInt64(StateSchema.FirstColumn),
                    (StateSchema.ParamWork, anchor.WorkId)).Any()) return false;
            try
            {
                retired = Retire(transaction, pair.Row, selection, anchor);
                foreach (var row in OwnedPeers(transaction, pair, selection, anchor, peer))
                {
                    var notice = StateEncoding.DecodeEnvelope<ChangeNotice>(row.Notice!)!;
                    var residual = notice.LocalContentDirty && anchor.Before is not null &&
                        Read(transaction, StateSchema.WorkHasChildren,
                            static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
                            (StateSchema.ParamParent, anchor.WorkId)).First();
                    if (!NeedsOwnSelection(row, notice, residual) && ReadRenameContinuation(transaction, pair.Row, row.WorkId) is null)
                    {
                        var deleted = Run(transaction, StateSchema.DeleteWork,
                            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, row.WorkId));
                        Require(deleted);
                        retired += deleted;
                    }
                }
                transaction.Commit();
            }
            catch (SqliteException error) when (IntegrityRefusal(error))
            {
                throw RollbackMutation(transaction, selection, error);
            }
        }
        if (_passes.TryGetValue(selection.PairId, out var pass))
        {
            pass.NetRetired += retired;
            if (pass.Suspended is { Count: > 0 }) (pass.Completed ??= []).Add(selection.AnchorWorkId);
            pass.Deletions?.Clear();
            pass.DirectDeletions?.Clear();
        }
        return true;
    }

    private static bool IntegrityRefusal(SqliteException error) =>
        error.SqliteErrorCode == SQLitePCL.raw.SQLITE_CONSTRAINT;

    private static ItemStoreMutationException RollbackMutation(
        SqliteTransaction transaction, WorkSelection selection, SqliteException error)
    {
        try { transaction.Rollback(); }
        catch (Exception rollback) { throw new AggregateException(error, rollback); }
        return new ItemStoreMutationException(selection, error.SqliteErrorCode, error.SqliteExtendedErrorCode, error);
    }

    private IEnumerable<WorkRow> OwnedPeers(SqliteTransaction transaction, PairRow pair,
        WorkSelection selection, WorkRow anchor, SelectionPeer? peer)
    {
        var side = PathNames.Other(selection.Side);
        if (peer is null || ReadSourceRow(transaction, pair.Row, side) is not { } source ||
            Domain(Roots(pair), side) is not { } domain) yield break;
        var after = BeforeFirstWork;
        while (Rows(transaction, StateSchema.OwnedPeerPathPage,
            Evidence(pair.Row, selection.PairGeneration, side, source.Epoch, domain,
                (StateSchema.ParamJoinKey, peer.Key), (StateSchema.ParamAfter, after),
                (StateSchema.ParamThrough, peer.ThroughWorkId), (StateSchema.ParamTake, StateSchema.OneRow),
                (StateSchema.ParamBefore, anchor.Before)))
            .FirstOrDefault() is { } row)
        {
            after = row.WorkId;
            if (_passes.TryGetValue(selection.PairId, out var pass) && pass.Claims is { } claims && claims.Values.Any(claim =>
                FindAnchor(transaction, pair.Row, claim) is { } claimed && Owned(claim, claimed, row))) continue;
            yield return row;
        }
    }

    // One registration pass: inserts, moves, notice refreshes and associations share one transaction and one insert command.
    /// <summary>Registers only the unfinished branches under the parent and associates them with the exact committed
    /// issue. It never retires the parent; repeated notices refresh one residual without erasing dirty evidence.
    /// </summary>
    internal bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished)
    {
        if (exactIssue.Kind == IssueKind.Info) return false;
        using var transaction = _connection.BeginTransaction();
        var pair = RequireCurrent(transaction, parent);
        RequireIssue(transaction, pair.Row, exactIssue);
        var episode = IssuePolicy.IsLocalAccessRetryCause(exactIssue.Cause)
            ? Rows(transaction, StateSchema.EpisodeWork, (StateSchema.ParamPairRow, pair.Row),
                (StateSchema.ParamIssue, exactIssue.Id)).FirstOrDefault() ??
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged) : null;
        var anchor = RequireAnchor(transaction, pair.Row, parent);
        var added = AttachIssueWork(transaction, pair, parent, anchor, exactIssue, episode, unfinished);
        transaction.Commit();
        AccountAttachment(parent.PairId, added);
        return true;
    }

    private long AttachIssueWork(SqliteTransaction transaction, PairRow pair, WorkSelection parent,
        WorkRow anchor, Issue exactIssue, WorkRow? episode,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished, bool transfer = false)
    {
        ChangeNotice? episodeNotice = null;
        var subject = StateEncoding.Envelope(exactIssue.Subject);
        var workId = Watermark(transaction);
        var firstWorkId = workId;
        using (var insert = WorkInsert(transaction))
        {
            foreach (var (notice, isFolder, before) in unfinished)
            {
                if (notice.Side != parent.Side) throw new ArgumentException(null, nameof(unfinished));
                if (!transfer && episode is not null && (!notice.LocalContentDirty || episode.Notice is not null &&
                    (episodeNotice ??= StateEncoding.DecodeEnvelope<ChangeNotice>(episode.Notice)) is { } held &&
                    MergeContent(held, notice) is { } mergedEpisode &&
                    (mergedEpisode.LocalContentDirty, mergedEpisode.Usn) == (held.LocalContentDirty, held.Usn))) continue;
                var role = Role(isFolder);
                var owner = episode?.WorkId ?? parent.AnchorWorkId;
                var key = RegistrationKey(parent.Domain, episode is null ? notice : notice with { OldPath = null },
                    role, episode is null ? before : null);
                var stored = episode is not null ? null :
                    IssueRegistration(transaction, pair.Row, exactIssue.Id, notice);
                stored ??= Rows(transaction, StateSchema.RegistrationByParent,
                    (StateSchema.ParamParent, owner), (StateSchema.ParamObligation, key)).FirstOrDefault();
                if (stored is null && episode is not null && anchor.WorkId > owner && notice.Id == anchor.Identity &&
                    PathNames.KeyFor(notice.Side, notice.Path) == anchor.Key) stored = anchor;
                if (stored is null)
                {
                    workId = checked(workId + 1);
                    AddWork(insert, workId, pair.Row, parent.PairGeneration, parent.SourceEpoch, parent.Domain, notice,
                        before, anchor.Acquisition, owner, key);
                }
                else
                {
                    var transferable = transfer ? TransferableWork(transaction, pair.Row, stored, owner) : null;
                    if (transfer && (!SameFences(stored, parent) || stored.Domain != parent.Domain || transferable is null))
                        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
                    if (!SameFences(stored, parent) || episode is not null && stored.Parent != owner)
                    {
                        var nextParent = episode is null ? stored.Parent : owner;
                        var nextKey = episode is null ? stored.Obligation : key;
                        Require(Run(transaction, StateSchema.MoveRegistration,
                            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, stored.WorkId),
                            (StateSchema.ParamGeneration, parent.PairGeneration), (StateSchema.ParamEpoch, parent.SourceEpoch),
                            (StateSchema.ParamDomain, parent.Domain), (StateSchema.ParamParent, nextParent),
                            (StateSchema.ParamObligation, nextKey)));
                        stored = stored with { PairGeneration = parent.PairGeneration, SourceEpoch = parent.SourceEpoch,
                            Domain = parent.Domain, Parent = nextParent, Obligation = nextKey };
                    }
                    var incoming = transferable is { } content
                        ? StateEncoding.Envelope(stored.Acquisition > anchor.Acquisition
                            ? MergeContent(notice, content.Notice) : MergeContent(content.Notice, notice))
                        : RegisteredNotice(stored, notice);
                    if (transfer)
                    {
                        Require(Run(transaction, StateSchema.MergeTransferredWork,
                            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, stored.WorkId),
                            (StateSchema.ParamAcquisition, anchor.Acquisition),
                            (StateSchema.ParamBefore, StateEncoding.OptionalEnvelope(before)),
                            (StateSchema.ParamNotice, incoming)));
                    }
                    else if (stored.Notice != incoming)
                        Require(Run(transaction, StateSchema.UpdateRegisteredNotice,
                            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamWork, stored.WorkId),
                            (StateSchema.ParamParent, stored.Parent), (StateSchema.ParamNotice, incoming)));
                }
                if (episode is null)
                    Associate(transaction, pair.Row, stored?.WorkId ?? workId, notice.Side,
                        PathNames.KeyFor(notice.Side, notice.Path), Identity(notice.Side, notice.Id), exactIssue.Id,
                        subject);
            }
        }
        return workId - firstWorkId;
    }

    private void AccountAttachment(string pairId, long added)
    {
        if (!_passes.TryGetValue(pairId, out var pass)) return;
        pass.NetRetired -= added;
        pass.Deletions?.Clear();
        pass.DirectDeletions?.Clear();
    }

    /// <summary>Transfers selected pre-episode content debt to its exact newer same-item wait without retrying it.</summary>
    // One atomic custody transition: validation, registration and exact retirement share the transaction;
    // pass accounting must remain outside its commit. Iteration retains only one selected row at a time.
    internal bool TransferOlderEpisodeWork(WorkSelection selection, ItemRetryCandidate expected)
    {
        long added = 0;
        var retired = 0;
        try
        {
            using (var transaction = _connection.BeginTransaction())
            {
                var pair = RequireCurrent(transaction, selection);
                var anchor = FindAnchor(transaction, pair.Row, selection);
                if (anchor is null) return false;
                if (selection.Side != Side.Local || selection.Identity is not { Length: > 0 } identity ||
                    expected.PairId != selection.PairId || expected.Dispatch != ItemRetryDispatch.OrdinaryItem ||
                    expected.Token.PairGeneration != selection.PairGeneration || expected.Token.SourceEpoch != selection.SourceEpoch ||
                    selection.AnchorWorkId >= expected.Token.WorkId ||
                    !expected.Token.Subject.Items.Any(item => item.Side == Side.Local && item.Id == identity &&
                        PathNames.Key(item.Path) == selection.Key) ||
                    !IssueTable.TryClaimItemRetry(_connection, transaction, pair.Row, expected, null)) return false;
                var episode = Rows(transaction, StateSchema.EpisodeWork, (StateSchema.ParamPairRow, pair.Row),
                    (StateSchema.ParamIssue, expected.Token.IssueId)).Single();
                if (episode.Domain != selection.Domain || episode.Side != selection.Side || episode.Key != selection.Key ||
                    episode.Identity != identity) return false;
                var issue = Read(transaction, StateSchema.SelectIssueById, IssueTable.ReadIssue,
                    (StateSchema.ParamPair, selection.PairId), (StateSchema.ParamId, expected.Token.IssueId)).Single();
                if (IssuePolicy.UnknownMove(issue.Kind, issue.Subject) ||
                    Read(transaction, StateSchema.LocalEchoPending, static reader => reader.GetBoolean(StateSchema.FirstColumn),
                        (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamLocalKey, selection.Key),
                        (StateSchema.ParamDropboxKey, selection.Key), (StateSchema.ParamIsFolder, false)).First()) return false;
                SyncedEntry? earliestBefore = null;
                // One row back: the bound stays strictly older than the episode (inclusive through the previous work id).
                var bounded = selection with { ThroughWorkId = Math.Min(selection.ThroughWorkId, episode.WorkId - StateSchema.OneRow) };
                var after = BeforeFirstWork;
                while (Rows(transaction, ReadIdentity(anchor), Membership(pair.Row, bounded, anchor,
                           (StateSchema.ParamAfter, after), (StateSchema.ParamTake, StateSchema.OneRow))).FirstOrDefault() is { } row)
                {
                    after = row.WorkId;
                    // A different location or owner keeps its reconciliation; never normalize it into content debt.
                    if (row.Key != selection.Key || TransferableWork(transaction, pair.Row, row, null) is not { } content)
                    {
                        if (row.WorkId == selection.AnchorWorkId) return false;
                        continue;
                    }
                    earliestBefore ??= content.Before;
                    added += AttachIssueWork(transaction, pair, ToSelection(selection.PairId, row, row.WorkId), row,
                        issue, episode, [(content.Notice, false, earliestBefore)], transfer: true);
                    retired += Retire(transaction, pair.Row, ToSelection(selection.PairId, row, row.WorkId), row);
                }
                if (retired == 0) return false;
                transaction.Commit();
            }
        }
        catch (LiveAdmissionStaleException) { return false; }
        AccountAttachment(selection.PairId, added - retired);
        EndSelection(selection);
        return true;
    }

    private (ChangeNotice Notice, SyncedEntry? Before)? TransferableWork(
        SqliteTransaction? transaction, long pair, WorkRow row, long? parent, bool pathOnly = false)
    {
        if (row.Notice is null || row.Acquisition is null || row.Parent != parent || row.OldKey is not null ||
            row.DiscoveryRequired) return null;
        var notice = StateEncoding.DecodeEnvelope<ChangeNotice>(row.Notice);
        var before = row.Before is null ? null : StateEncoding.DecodeEnvelope<SyncedEntry>(row.Before);
        return notice is { Kind: ChangeKind.Changed, OldPath: null, IdentityDeleted: false } &&
            notice.Observed is not ({ IsFolder: true } or { Skip: not null }) &&
            (!pathOnly || row.Side == Side.Local && row.Identity is null && row.Obligation is null &&
                notice.Id is null && notice.Observed is { IsFolder: false, Skip: null }) &&
            before is not { IsFolder: true } && (before is null ||
                before.LocalKey == row.Key && (pathOnly ? row.Identity is null : before.LocalId == row.Identity)) &&
            Read(transaction, StateSchema.TransferableWork, static reader => reader.GetBoolean(StateSchema.FirstColumn),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, row.WorkId)).First()
            ? (notice, before) : null;
    }

    // Only dirty inputs carry content provenance; unknown dirty evidence cannot become a suppressible own echo.
    internal static ChangeNotice MergeContent(ChangeNotice prior, ChangeNotice incoming)
    {
        if (!prior.LocalContentDirty) return incoming;
        var usn = !incoming.LocalContentDirty ? prior.Usn :
            prior.Usn is { } previous && incoming.Usn is { } next ? Math.Max(previous, next) : (ulong?)null;
        return incoming.LocalContentDirty && incoming.Usn == usn ? incoming :
            incoming with { LocalContentDirty = true, Usn = usn };
    }

    // One dirty answer: the cached fast path and every probe accumulate one dirty flag and evidence version.
    /// <summary>The single dirty answer over applicable Acquired-origin Local work, inside or outside any selection.
    /// A null boundary or unknown identity is not clean, path-only evidence counts as dirty, and an unresolved source
    /// gap alone is never dirty. A content write of the full identity is the engine's own echo only when its record
    /// is at or before the boundary saved for that identity at the recorded path, in the current journal.</summary>
    internal (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(string pairId,
        long expectedGeneration, long localSourceEpoch, string nativeDomain, string? fullIdentity,
        string? currentPathKey, string? recordedPathKey)
    {
        var cached = _evidence.TryGetValue(pairId, out var evidence) &&
            evidence.Generation == expectedGeneration && evidence.Epoch == localSourceEpoch;
        if (cached && !evidence.Any) return (false, Position(nativeDomain), BeforeFirstWork);
        if (ReadPair(null, pairId) is not { } pair || pair.Generation != expectedGeneration ||
            ReadSourceRow(null, pair.Row, Side.Local)?.Epoch != localSourceEpoch)
            return (false, null, BeforeFirstWork);
        if (_writable && !cached)
        {
            var any = Read(null, StateSchema.AnyLocalEvidence,
                static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn),
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, expectedGeneration),
                (StateSchema.ParamEpoch, localSourceEpoch)).First();
            _evidence[pairId] = (expectedGeneration, localSourceEpoch, any);
            if (!any) return (false, Position(nativeDomain), BeforeFirstWork);
        }
        var (dirty, version, after) = (false, BeforeFirstWork, BeforeFirstWork);
        ulong? ownUsn = null;
        var seen = new HashSet<long>();
        // Only a USN-carrying content write of the full identity can be the engine's own; the rest is dirty as read.
        // Every counted notice comes from a row selected by that identity, and a work row's identity is its notice's.
        void Count(ChangeNotice notice)
        {
            if (!notice.LocalContentDirty) return;
            if (notice.Usn is { } usn && fullIdentity is not null)
                ownUsn = Math.Max(ownUsn ?? 0, usn);
            else dirty = true;
        }
        while (fullIdentity is not null && Read<(long Work, long Version, ChangeNotice Notice)?>(null,
                   StateSchema.DirtyIdentity,
                   static reader => (reader.GetInt64(StateSchema.DirtyWorkId),
                       reader.GetInt64(StateSchema.DirtyAcquisitionWorkId),
                       StateEncoding.ReadEnvelope<ChangeNotice>(reader, StateSchema.DirtyNoticeJson)),
                   (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, expectedGeneration),
                   (StateSchema.ParamEpoch, localSourceEpoch), (StateSchema.ParamDomain, nativeDomain),
                   (StateSchema.ParamIdentity, StateEncoding.IdFor(Side.Local, fullIdentity)),
                   (StateSchema.ParamAfter, after), (StateSchema.ParamTake, StateSchema.OneRow)).FirstOrDefault() is { } fact)
        {
            after = fact.Work;
            seen.Add(after);
            Count(fact.Notice);
            version = Math.Max(version, fact.Version);
        }
        (long Version, bool PathOnly, ChangeNotice? Notice) Fact(SqliteDataReader reader)
        {
            var identity = IdentityText(reader, StateSchema.WorkIdentity);
            if (fullIdentity is not null && identity is not null && identity != fullIdentity)
                return (BeforeFirstWork, false, null);
            var known = !seen.Add(reader.GetInt64(StateSchema.WorkId));
            return (reader.GetInt64(StateSchema.WorkAcquisitionWorkId), !known && identity is null,
                known || identity is null ? null : StateEncoding.ReadEnvelope<ChangeNotice>(reader, StateSchema.WorkNoticeJson));
        }
        foreach (var key in new[] { currentPathKey, recordedPathKey }.Distinct())
        {
            if (key is null) continue;
            var (low, high) = PathNames.DescendantBounds(key);
            var scope = Evidence(pair.Row, expectedGeneration, Side.Local, localSourceEpoch, nativeDomain,
                (StateSchema.ParamJoinKey, key), (StateSchema.ParamLow, low), (StateSchema.ParamHigh, high));
            foreach (var probe in ExactProbes.Concat(BelowProbes))
            {
                foreach (var row in Read(null, probe, Fact, scope))
                {
                    dirty |= row.PathOnly;
                    if (row.Notice is not null) Count(row.Notice);
                    version = Math.Max(version, row.Version);
                }
            }
        }
        var position = ReadPosition(null, nativeDomain);
        if (!dirty && ownUsn is { } own && !OwnWrite(pair.Row, recordedPathKey, fullIdentity!, own, position))
            dirty = true;
        return (dirty, position, version);
    }

    // The saved row still holds the identity whose publication wrote the boundary, the boundary is in the current
    // journal (a recreated journal has a new id) and that journal has been read past it. Anything else stays dirty.
    private bool OwnWrite(long pairRow, string? recordedPathKey, string identity, ulong usn, JournalPosition? current)
    {
        if (recordedPathKey is null || current is null) return false;
        var published = Read<JournalPosition?>(null, StateSchema.SyncedLocalPublished,
            static reader => new JournalPosition(Unsigned(reader, StateSchema.PublishedJournal),
                Unsigned(reader, StateSchema.PublishedUsn)),
            (StateSchema.ParamPairRow, pairRow), (StateSchema.ParamTargetKey, recordedPathKey),
            (StateSchema.ParamLocalId, StateEncoding.LocalId(identity))).FirstOrDefault();
        return published is not null && published.JournalId == current.JournalId && usn <= published.NextUsn &&
            published.NextUsn < current.NextUsn;
    }

    private JournalPosition? Position(string volume)
    {
        if (!_positions.TryGetValue(volume, out var position))
            _positions[volume] = position = ReadPosition(null, volume);
        return position;
    }

    /// <summary>Mints coverage only from each side's already committed terminal acquisition.</summary>
    internal LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox)
    {
        var pair = RequireCoverage(selection, local, dropbox);
        var watermark = Watermark(null);
        if (local.ThroughWorkId > watermark || dropbox.ThroughWorkId > watermark ||
            ReadSourceRow(null, pair.Row, Side.Dropbox)?.Cursor != dropbox.Cursor ||
            Domain(Roots(pair), Side.Local) is not { } volume || ReadPosition(null, volume) is not { } position ||
            position.JournalId != local.Checkpoint.JournalId ||
            Signed(position.NextUsn) < local.Checkpoint.NextUsn)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        return new LiveCoverage(local, dropbox);
    }

    /// <summary>Throws LiveAdmissionStaleException unless the selection stays within its own source's terminal
    /// watermark and no relevant unselected identity, old-path, descendant, residual or issue evidence falls in the
    /// footprint. Unrelated progress passes.</summary>
    internal void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
        IReadOnlyList<LiveEffectScope> footprint, Func<string, bool, bool> heldOverlaps)
    {
        var pair = RequireCoverage(selection, coverage.Local, coverage.Dropbox);
        if (selection.SourceEpoch != EpochOn(coverage, selection.Side))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var anchor = RequireAnchor(null, pair.Row, selection);
        var definition = StateEncoding.DecodePair(pair.Definition);
        var scopes = footprint.Select(scope => (scope.Side, Key: scope.KeyIn(definition), scope.IncludesDescendants))
            .ToArray();
        if (!Covers(scopes, selection.Side, selection.Key))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
        var baseline = ValidateSelectionBinding(pair, selection, anchor);
        bool? plainDeletion = null;
        for (var index = 0; index < scopes.Length; index++)
            ValidateScopeEvidence(pair, selection, anchor, coverage, definition.Roots, scopes[index],
                footprint[index].ExactEvidence, footprint[index].FolderPlan, baseline, heldOverlaps, ref plainDeletion);
        // B-101: a path selection's saved identity acquired elsewhere is a reported move, not a deletion (D19).
        if (LaterIdentityEvidence(pair.Row, selection, anchor) ||
            selection.Identity is null && baseline?.IdOn(selection.Side) is { Length: > 0 } saved && UnownedIdentity(pair.Row,
                selection, anchor, selection.Side, selection.SourceEpoch, selection.Domain, saved) ||
            PendingPath(pair) is { } pending &&
            Covers(scopes, Side.Local, PathNames.Key(pending)))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
    }

    private SyncedEntry? ValidateSelectionBinding(PairRow pair, WorkSelection selection, WorkRow anchor)
    {
        var baseline = SelectionBaseline(null, selection);
        var moved = baseline is not null && baseline.KeyOn(selection.Side) != selection.Key;
        var atPath = moved ? Read(null,
            selection.Side == Side.Local ? StateSchema.SelectSyncedByLocalKey : StateSchema.SelectSyncedByDropboxKey,
            ReadSaved, (StateSchema.ParamPair, selection.PairId), (StateSchema.ParamTargetKey, selection.Key)).FirstOrDefault() : baseline;
        // A rename may still have its saved binding at the old path, but cannot take another binding's target.
        if (StateEncoding.OptionalEnvelope(baseline) != anchor.Before)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        if (moved && atPath is not null && atPath != baseline)
        {
            using var snapshot = _connection.BeginTransaction(deferred: true);
            var continuation = ReadRenameContinuation(snapshot, pair.Row, anchor.WorkId);
            var owners = continuation is null ? [] : RenameDestinations.All(_connection, snapshot, selection.PairId,
                baseline, RenameTarget(snapshot, selection, baseline!));
            if (continuation is null || owners.Count != 1 || owners[0].Binding != continuation.Destination)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionDestinationOccupied);
            snapshot.Commit();
        }
        return baseline;
    }

    // One ordered decision over one footprint scope: hold, exact and descendant evidence share one anchor and coverage.
    private void ValidateScopeEvidence(PairRow pair, WorkSelection selection, WorkRow anchor, LiveCoverage coverage,
        RootBinding? roots, (Side Side, string Key, bool IncludesDescendants) scope, bool exactEvidence, bool folderPlan,
        SyncedEntry? baseline, Func<string, bool, bool> heldOverlaps, ref bool? plainDeletion)
    {
        var epoch = EpochOn(coverage, scope.Side);
        var domain = Domain(roots, scope.Side) ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var key = scope.Key;
        var held = heldOverlaps(key, scope.IncludesDescendants);
        ItemRetryCandidate? newerEpisode = null;
        if (held || InUseBlocks(pair.Row, selection, scope.Side, key, out newerEpisode))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged,
                Massdex.SyncShark.Engine.Text.Get(TextKey.StaleFootprintHeld, (TextArg.Path, key), (TextArg.Side, scope.Side.ToString())))
                { WaitKey = held || scope.Side != Side.Local ? null : key,
                    NewerEpisode = newerEpisode is null ? null : (selection, newerEpisode) };
        var (low, high) = PathNames.DescendantBounds(key);
        var range = Evidence(pair.Row, selection.PairGeneration, scope.Side, epoch, domain,
            (StateSchema.ParamJoinKey, key), (StateSchema.ParamLow, low), (StateSchema.ParamHigh, high));
        var observedThrough = scope.Side == selection.Side ? selection.ThroughWorkId :
            coverage.Peer?.ThroughWorkId ?? selection.ThroughWorkId;
        RefuseLaterExact(selection, anchor, range, observedThrough);
        if (scope.IncludesDescendants && !exactEvidence)
        {
            plainDeletion ??= PlainDeletion(selection.PairId, anchor, selection.ThroughWorkId);
            var selectedDeletion = plainDeletion.Value;
            // A non-plain deletion still enters its own survivor plan, which classifies this earlier evidence (B-153);
            // outside the saved folder only a plain deletion of both is admitted.
            foreach (var probe in BelowProbes)
                if (Rows(null, probe, range).FirstOrDefault(row => !Owned(selection, anchor, row) &&
                    (row.Acquisition > observedThrough || !selectedDeletion && !folderPlan ||
                     !(baseline is { IsFolder: true } && (PathNames.IsUnderKey(row.Key, baseline.KeyOn(row.Side)) ||
                         row.OldKey is { } old && PathNames.IsUnderKey(old, baseline.KeyOn(row.Side)))) &&
                     (!selectedDeletion || !PlainDeletion(selection.PairId, row, observedThrough)))) is { } blocking)
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged,
                        Massdex.SyncShark.Engine.Text.Get(TextKey.StaleDescendantEvidence, (TextArg.Path, blocking.Key),
                            (TextArg.Count, blocking.Acquisition.ToString() ?? string.Empty),
                            (TextArg.Value, selectedDeletion.ToString()),
                            (TextArg.Name, PlainDeletion(selection.PairId, blocking, observedThrough).ToString())));
        }
    }

    // The one later-evidence rule: unselected exact evidence acquired after what the selection observed is stale.
    private void RefuseLaterExact(WorkSelection selection, WorkRow anchor, (string Name, object? Value)[] range,
        long observedThrough)
    {
        foreach (var probe in ExactProbes)
            if (Rows(null, probe, range).FirstOrDefault(row => !Owned(selection, anchor, row) &&
                row.Acquisition > observedThrough) is { } later)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged,
                    Massdex.SyncShark.Engine.Text.Get(TextKey.StaleEvidenceAfter, (TextArg.Path, later.Key),
                        (TextArg.Count, later.Acquisition.ToString() ?? string.Empty)));
    }

    /// <summary>B-176: ValidateLiveCoverage's later-evidence rule for the footprint's own-side scopes, in footprint
    /// order, against the fixed ThroughWorkId, so it can only refuse what admission of this footprint refuses. Every
    /// other admission answer, and any missing pair, domain or anchor, is left to admission.</summary>
    internal void RefuseLaterEvidence(WorkSelection selection, IReadOnlyList<LiveEffectScope> footprint)
    {
        if (ReadPair(null, selection.PairId) is not { } pair || pair.Generation != selection.PairGeneration ||
            FindAnchor(null, pair.Row, selection) is not { } anchor) return;
        var definition = StateEncoding.DecodePair(pair.Definition);
        if (Domain(definition.Roots, selection.Side) is not { } domain) return;
        foreach (var scope in footprint)
        {
            if (scope.Side != selection.Side) continue;
            var key = scope.KeyIn(definition);
            var (low, high) = PathNames.DescendantBounds(key);
            RefuseLaterExact(selection, anchor, Evidence(pair.Row, selection.PairGeneration, scope.Side,
                selection.SourceEpoch, domain, (StateSchema.ParamJoinKey, key), (StateSchema.ParamLow, low),
                (StateSchema.ParamHigh, high)), selection.ThroughWorkId);
        }
    }

    private bool InUseBlocks(long pair, WorkSelection selection, Side side, string key,
        out ItemRetryCandidate? newerEpisode)
    {
        newerEpisode = null;
        foreach (var refusal in Read(null, StateSchema.ItemEpisodesAtPath, reader =>
        {
            var work = reader.GetInt64(StateSchema.EpisodePathWork);
            var newer = work > selection.AnchorWorkId;
            var items = newer ? [] : StateEncoding.ReadIdentifiedItems(reader, StateSchema.EpisodePathSubject);
            // B-198: unidentified subjects do not block, except for the unchanged episode-age predicate.
            var blocked = newer || items.Length > 0 && !items.Any(item => item.Side == selection.Side &&
                (selection.Identity is not null ? item.Id == selection.Identity :
                    PathNames.KeyFor(item.Side, item.Path) == selection.Key));
            var same = newer && side == Side.Local && selection.Side == Side.Local && key == selection.Key &&
                selection.Identity is { Length: > 0 } && !reader.IsDBNull(StateSchema.EpisodePathIdentity) &&
                Equals(reader.GetValue(StateSchema.EpisodePathIdentity), StateEncoding.IdFor(Side.Local, selection.Identity));
            return (Blocked: blocked, Same: same, Work: work, Issue: reader.GetInt64(StateSchema.EpisodePathIssue));
        }, (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side), (StateSchema.ParamPath, key)))
        {
            if (!refusal.Blocked) continue;
            if (refusal.Same)
            {
                var candidate = IssueTable.ReadItemRetryState(_connection, null, selection.PairId, pair,
                    [refusal.Issue])[refusal.Issue];
                if (candidate?.Token.WorkId == refusal.Work) newerEpisode = candidate;
            }
            // The first blocker preserves refusal precedence; another same-path episode may own the capture.
            return true;
        }
        return false;
    }

    // One row ensure: both cases bind first-write-wins facts, verify the stored row and associate the issue in the same transaction.
    /// <summary>The id of the source-less row for this issue lifecycle, or of the live selection's row when one is
    /// supplied, with its issue association. An existing row must match every immutable fact; disagreement is a
    /// conflict, not success.</summary>
    internal long EnsureIssueWork(SqliteTransaction transaction, string pairId, long expectedGeneration,
        long sourceEpoch, long issueId, string path, IssueSubject subject, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending)
    {
        if ((dispatch == ItemRetryDispatch.LocalRecovery) != expectedPending is not null)
            throw new ArgumentException(null, nameof(expectedPending));
        var pair = RequirePair(transaction, pairId);
        if (pair.Generation != expectedGeneration)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var bound = StateEncoding.Envelope(subject);
        if (selection is not null)
        {
            RequireCurrent(transaction, selection);
            var anchor = RequireAnchor(transaction, pair.Row, selection);
            Require(Run(transaction, StateSchema.BindLiveRetryWork, (StateSchema.ParamDispatch, dispatch.ToString()),
                (StateSchema.ParamPendingOperation, expectedPending?.OperationId),
                (StateSchema.ParamPendingGeneration, expectedPending?.Generation),
                (StateSchema.ParamWork, anchor.WorkId), (StateSchema.ParamPairRow, pair.Row),
                (StateSchema.ParamGeneration, expectedGeneration), (StateSchema.ParamEpoch, sourceEpoch),
                (StateSchema.ParamOrigin, StateSchema.AcquiredWorkOrigin)));
            Associate(transaction, pair.Row, anchor.WorkId, anchor.Side, anchor.Key,
                StateEncoding.IdFor(anchor.Side, anchor.Identity), issueId, bound);
            return anchor.WorkId;
        }
        var item = subject.Items.Where(member => PathNames.Same(member.Path, path)).MinBy(static row => row.Side) ??
            throw new ArgumentException(null, nameof(path));
        var domain = Domain(Roots(pair), item.Side) ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var (key, pathKey) = (StateEncoding.IssueWorkKey(issueId, subject), PathNames.KeyFor(item.Side, path));
        var identity = Identity(item.Side, item.Id);
        Run(transaction, StateSchema.EnsureIssueWork, (StateSchema.ParamWork, checked(Watermark(transaction) + 1)),
            (StateSchema.ParamJoinKey, key), (StateSchema.ParamDispatch, dispatch.ToString()),
            (StateSchema.ParamPendingOperation, expectedPending?.OperationId),
            (StateSchema.ParamPendingGeneration, expectedPending?.Generation), (StateSchema.ParamPairRow, pair.Row),
            (StateSchema.ParamGeneration, expectedGeneration), (StateSchema.ParamEpoch, sourceEpoch),
            (StateSchema.ParamSide, (int)item.Side), (StateSchema.ParamDomain, domain),
            (StateSchema.ParamPath, pathKey), (StateSchema.ParamIdentity, identity), (StateSchema.ParamBefore, null));
        var work = Read<(long Id, bool Same)?>(transaction, StateSchema.IssueWorkByKey, reader =>
                (reader.GetInt64(StateSchema.IssueWorkId),
                    reader.GetString(StateSchema.IssueWorkDispatchKind) == dispatch.ToString() &&
                    Text(reader, StateSchema.IssueWorkPendingOperationId) == expectedPending?.OperationId &&
                    Number(reader, StateSchema.IssueWorkPendingGeneration) == expectedPending?.Generation &&
                    reader.GetInt64(StateSchema.IssueWorkPairGeneration) == expectedGeneration &&
                    reader.GetInt64(StateSchema.IssueWorkSourceEpoch) == sourceEpoch &&
                    reader.GetInt32(StateSchema.IssueWorkSide) == (int)item.Side &&
                    reader.GetString(StateSchema.IssueWorkDomain) == domain &&
                    reader.GetString(StateSchema.IssueWorkPathKey) == pathKey &&
                    IdentityText(reader, StateSchema.IssueWorkIdentity) == IdentityString(item.Id)),
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamJoinKey, key)).First();
        if (work is not { Same: true }) throw new InvalidOperationException();
        Associate(transaction, pair.Row, work.Value.Id, item.Side, pathKey, identity, issueId, bound);
        return work.Value.Id;
    }

    /// <summary>Deletes source-less work after its episode clears, releasing children even if an ordinary Failure remains.</summary>
    internal void SettleIssueWork(SqliteTransaction transaction, ItemRetryCandidate expected) =>
        Require(Run(transaction, StateSchema.DeleteSettledIssueWork,
            (StateSchema.ParamPairRow, RequirePair(transaction, expected.PairId).Row),
            (StateSchema.ParamWork, expected.Token.WorkId),
            (StateSchema.ParamJoinKey, StateEncoding.IssueWorkKey(expected.Token.IssueId, expected.Token.Subject))));

    /// <summary>Settles only the candidate's Acquired row after verified completion and association clearance;
    /// later arrivals remain pending. Retained or transferred responsibility must not call this method.</summary>
    internal void SettleAcquiredWork(SqliteTransaction transaction, ItemRetryCandidate expected)
    {
        var pair = RequirePair(transaction, expected.PairId);
        var anchor = Rows(transaction, StateSchema.Anchor, (StateSchema.ParamAnchor, expected.Token.WorkId),
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, expected.Token.PairGeneration),
            (StateSchema.ParamEpoch, expected.Token.SourceEpoch)).FirstOrDefault() ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var selection = ToSelection(expected.PairId, anchor, expected.Token.WorkId);
        RequireCurrent(transaction, selection);
        if (!NeedsOwnSelection(anchor)) Retire(transaction, pair.Row, selection, anchor);
    }

    /// <summary>Moves Issue-origin applicability to the new fences; Acquired provenance is never rewritten.</summary>
    internal void RebindIssueWork(SqliteTransaction transaction, ItemRetryCandidate expected, long nextGeneration,
        long nextEpoch) =>
        Require(Run(transaction, StateSchema.RebindIssueWork, (StateSchema.ParamGeneration, nextGeneration),
            (StateSchema.ParamEpoch, nextEpoch),
            (StateSchema.ParamPairRow, RequirePair(transaction, expected.PairId).Row),
            (StateSchema.ParamWork, expected.Token.WorkId),
            (StateSchema.ParamJoinKey, StateEncoding.IssueWorkKey(expected.Token.IssueId, expected.Token.Subject)),
            (StateSchema.ParamOldGeneration, expected.Token.PairGeneration),
            (StateSchema.ParamOldEpoch, expected.Token.SourceEpoch)));

    /// <summary>Carries every in-use lifecycle across a generation change, after both sources advanced in the caller's
    /// transaction. Source-less work on an unchanged domain follows the new fences. Otherwise the lifecycle ends:
    /// Acquired provenance is never rewritten and the retained work answers for it, and source-less work bound to
    /// another domain is removed so a new report can bind afresh. The Failure itself stays visible.</summary>
    internal void TransferItemEpisodes(SqliteTransaction transaction, string pairId, RootBinding? roots,
        long nextGeneration, long nextLocalEpoch, long nextDropboxEpoch, Side? side = null)
    {
        var pair = RequirePair(transaction, pairId);
        var issues = Read(transaction, StateSchema.PairItemEpisodes,
            static reader => reader.GetInt64(StateSchema.FirstColumn), (StateSchema.ParamPairRow, pair.Row),
            (StateSchema.ParamSide, side is null ? null : (int?)side)).ToList();
        if (issues.Count == 0) return;
        foreach (var candidate in IssueTable.ReadItemRetryState(_connection, transaction, pairId, pair.Row, issues).Values)
        {
            if (candidate is null) continue;
            var work = Rows(transaction, StateSchema.Anchor, (StateSchema.ParamAnchor, candidate.Token.WorkId),
                (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, candidate.Token.PairGeneration),
                (StateSchema.ParamEpoch, candidate.Token.SourceEpoch)).First();
            if (work.Notice is null && work.Domain == Domain(roots, work.Side))
            {
                RebindIssueWork(transaction, candidate, nextGeneration,
                    work.Side == Side.Local ? nextLocalEpoch : nextDropboxEpoch);
                continue;
            }
            Require(IssueTable.ClearItemEpisode(_connection, transaction, pair.Row, candidate.Token));
            if (work.Notice is null) SettleIssueWork(transaction, candidate);
        }
    }

    /// <summary>Carries the old generation's Local dirty evidence to the new one, one ordinary row per dirty row, so
    /// the next Repair sees the edit. The caller has already advanced both sources, so the old generation and epoch
    /// are read explicitly. Another volume's evidence and a history reset keep nothing.</summary>
    internal void TransferLocalDirtyEvidence(SqliteTransaction transaction, string pairId, RootBinding? roots,
        long expectedGeneration, long expectedLocalEpoch, long nextGeneration, long nextLocalEpoch)
    {
        var pair = RequirePair(transaction, pairId);
        if (Domain(roots, Side.Local) is not { } domain) return;
        var dirty = Read<(ChangeNotice Notice, long Acquisition)>(transaction, StateSchema.LocalDirtyEvidence,
            static reader => (StateEncoding.ReadEnvelope<ChangeNotice>(reader, StateSchema.LocalDirtyNoticeJson),
                reader.GetInt64(StateSchema.LocalDirtyAcquisitionWorkId)),
            (StateSchema.ParamPairRow, pair.Row), (StateSchema.ParamGeneration, expectedGeneration),
            (StateSchema.ParamEpoch, expectedLocalEpoch), (StateSchema.ParamDomain, domain))
            .Where(row => row.Notice.LocalContentDirty).ToList();
        if (dirty.Count == 0) return;
        var workId = Watermark(transaction);
        using var insert = WorkInsert(transaction);
        foreach (var row in dirty)
        {
            workId = checked(workId + 1);
            AddWork(insert, workId, pair.Row, nextGeneration, nextLocalEpoch, domain, row.Notice, null,
                row.Acquisition, null, null);
        }
    }

    /// <summary>Follows the same pending operation to its next generation in the advancing transaction.</summary>
    internal void RefreshPendingRetryBinding(SqliteTransaction transaction, ItemRetryCandidate expected,
        long nextPendingGeneration, string pendingJson) =>
        Require(Run(transaction, StateSchema.RefreshPendingRetryBinding,
            (StateSchema.ParamNextPendingGeneration, nextPendingGeneration),
            (StateSchema.ParamPairRow, RequirePair(transaction, expected.PairId).Row),
            (StateSchema.ParamWork, expected.Token.WorkId),
            (StateSchema.ParamPendingOperation, expected.PendingOperationId),
            (StateSchema.ParamPendingGeneration, expected.PendingGeneration),
            (StateSchema.ParamGeneration, expected.Token.PairGeneration),
            (StateSchema.ParamPendingJson, pendingJson)));

    private static void Require(int affected)
    {
        if (affected != 1) throw new InvalidOperationException();
    }

    private static void RequireSome(int affected)
    {
        if (affected < 1) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
    }

    // The journal's unsigned values keep their exact bits in SQLite's signed INTEGER.
    internal static long Signed(ulong value) => unchecked((long)value);

    private static ulong Unsigned(SqliteDataReader reader, int column) => unchecked((ulong)reader.GetInt64(column));

    private static string? Text(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetString(column);

    private static long? Number(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetInt64(column);

    private static string? IdentityText(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : Convert.ToString(reader.GetValue(column), CultureInfo.InvariantCulture);

    private static string? IdentityString(string? id) => string.IsNullOrEmpty(id) ? null : id;

    internal static object? Identity(Side side, string? id) => StateEncoding.IdFor(side, IdentityString(id));

    private static WorkRow ReadWorkRow(SqliteDataReader reader) => new(reader.GetInt64(StateSchema.WorkId),
        reader.GetInt64(StateSchema.WorkPairId), reader.GetInt64(StateSchema.WorkPairGeneration),
        reader.GetInt64(StateSchema.WorkSourceEpoch), (Side)reader.GetInt32(StateSchema.WorkSide),
        reader.GetString(StateSchema.WorkDomain), reader.GetString(StateSchema.WorkPathKey),
        Text(reader, StateSchema.WorkOldPathKey), IdentityText(reader, StateSchema.WorkIdentity), Text(reader, StateSchema.WorkNoticeJson),
        Text(reader, StateSchema.WorkBeforeJson), Number(reader, StateSchema.WorkAcquisitionWorkId),
        Number(reader, StateSchema.WorkParentAnchor), Text(reader, StateSchema.WorkObligationKey),
        StateEncoding.ReadBool(reader, StateSchema.WorkDiscoveryRequired));

    private static WorkSelection ToSelection(string pairId, WorkRow row, long through) =>
        new(pairId, row.PairGeneration, row.SourceEpoch, row.Side, row.Domain, row.Identity, row.Key, row.WorkId,
            through) { Obligation = row.Obligation };

    // A selection handed to the Executor carries its responsibility key; Storage's own membership probes never read
    // it, so only these projections decode and encode an acquired anchor's key, once per returned selection.
    // The preimage is the current baseline, the one BeginSelection would write; BeginSelection rekeys if it changes.
    private WorkSelection Selected(string pairId, WorkRow row, long through, SqliteTransaction? transaction = null)
    {
        var selection = ToSelection(pairId, row, through);
        return row.Obligation is not null || row.Notice is null ? selection
            : Keyed(selection, row, SelectionBaseline(transaction, selection));
    }

    // An acquired anchor's key from the given preimage; a residual keeps its stored key.
    private static WorkSelection Keyed(WorkSelection selection, WorkRow row, SyncedEntry? before) =>
        row.Obligation is not null || row.Notice is not { } json ||
        StateEncoding.DecodeEnvelope<ChangeNotice>(json) is not { } notice ? selection
            : selection with { Obligation = AcquiredObligation(row.Domain, notice, before) };

    // An acquired anchor names the obligation a registration of its own notice would carry, its kind read as
    // Row.IsFolder reads it (observed, then saved), so a new row of a retained responsibility compares equal.
    private static string AcquiredObligation(string domain, ChangeNotice notice, SyncedEntry? before) =>
        RegistrationKey(domain, notice, Role(notice.Observed?.IsFolder ?? before?.IsFolder ?? false), before);

    private static ObligationRole Role(bool isFolder) => isFolder ? ObligationRole.Subtree : ObligationRole.Content;

    private static string RegistrationKey(string domain, ChangeNotice notice, ObligationRole role,
        SyncedEntry? before) => StateEncoding.ObligationKey(notice.Side, domain, role,
        IdentityString(notice.Id), PathNames.KeyFor(notice.Side, notice.Path),
        notice.OldPath is null ? null : PathNames.KeyFor(notice.Side, notice.OldPath), before);

    private static (string Name, object? Value)[] Membership(long pair, WorkSelection selection, WorkRow anchor,
        params (string Name, object? Value)[] extra) =>
    [
        (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, selection.PairGeneration),
        (StateSchema.ParamSide, (int)selection.Side), (StateSchema.ParamEpoch, selection.SourceEpoch),
        (StateSchema.ParamDomain, selection.Domain), (StateSchema.ParamAnchor, selection.AnchorWorkId),
        (StateSchema.ParamThrough, selection.ThroughWorkId), (StateSchema.ParamParent, anchor.Parent),
        (StateSchema.ParamObligation, anchor.Obligation), (StateSchema.ParamBefore, anchor.Before),
        selection.Identity is null ? (StateSchema.ParamPath, selection.Key)
            : (StateSchema.ParamIdentity, StateEncoding.IdFor(selection.Side, selection.Identity)),
        .. extra
    ];

    private static (string Name, object? Value)[] Evidence(long pair, long generation, Side side, long epoch,
        string domain, params (string Name, object? Value)[] keys) =>
    [
        (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, generation),
        (StateSchema.ParamSide, (int)side), (StateSchema.ParamEpoch, epoch), (StateSchema.ParamDomain, domain),
        (StateSchema.ParamWatermark, BeforeFirstWork), .. keys
    ];

    private static bool Covers((Side Side, string Key, bool IncludesDescendants)[] scopes, Side side, string key) =>
        scopes.Any(scope => scope.Side == side &&
            (scope.Key == key || scope.IncludesDescendants && PathNames.IsUnderKey(key, scope.Key)));

    private bool PlainDeletion(string pairId, WorkRow row, long through)
    {
        var first = Earliest(row.PairRow, pairId, row, through);
        var pass = _passes.GetValueOrDefault(pairId);
        var cache = pass is null ? null : (pass.Deletions ??= []);
        var key = (first.WorkId, through);
        if (cache is not null && cache.TryGetValue(key, out var prior)) return prior;
        var selection = ToSelection(pairId, first, through);
        var (result, restatement) = DeletionGroup(selection, pass);
        if (!result && restatement is not null && SelectionBaseline(null, selection) is { IsFolder: true } saved &&
            restatement == saved.IdOn(first.Side))
        {
            var other = PathNames.Other(first.Side);
            var pair = RequirePair(null, pairId);
            if (ReadSourceRow(null, pair.Row, other) is { } source && Domain(Roots(pair), other) is { } domain)
                foreach (var counterpart in Rows(null, StateSchema.EvidencePathExact,
                    Evidence(pair.Row, pair.Generation, other, source.Epoch, domain,
                        (StateSchema.ParamJoinKey, saved.KeyOn(other)))))
                {
                    if (counterpart.Identity is not null && counterpart.Identity != saved.IdOn(other) ||
                        counterpart.Acquisition > through) continue;
                    var earliest = Earliest(pair.Row, pairId, counterpart, through);
                    if (!DeletionGroup(ToSelection(pairId, earliest, through), pass).Deleted) continue;
                    result = true;
                    break;
                }
        }
        if (cache is not null) cache[key] = result;
        return result;
    }

    private (bool Deleted, string? Restatement) DeletionGroup(WorkSelection selection, Pass? pass)
    {
        var cache = pass is null ? null : (pass.DirectDeletions ??= []);
        var key = (selection.AnchorWorkId, selection.ThroughWorkId);
        if (cache is not null && cache.TryGetValue(key, out var prior)) return prior;
        PendingChange? last = null;
        var dirty = false;
        foreach (var member in ReadWork(selection))
        {
            last = member;
            dirty |= member.Notice.LocalContentDirty;
        }
        var deleted = last?.Notice is { Kind: ChangeKind.Deleted } or { IdentityDeleted: true };
        if (!deleted && last?.Notice is { Side: Side.Local, Kind: ChangeKind.Renamed } moved && pass?.Scope is { } scope)
            deleted = scope.LeavesScope(moved.Path, null,
                moved.Observed?.IsFolder ?? last?.Before?.IsFolder ?? SelectionBaseline(null, selection)?.IsFolder ?? false,
                (Owner: this, selection.PairId), SavedByKey);
        var restatement = !dirty && last?.Notice is { Kind: ChangeKind.Changed, OldPath: null,
            IdentityDeleted: false, Observed: { IsFolder: true, Skip: null } } ? last.Notice.Id : null;
        var result = (deleted && !dirty, restatement);
        if (cache is not null) cache[key] = result;
        return result;
    }

    // A selection's own rows and the residuals registered under it are not unselected evidence.
    private static bool Owned(WorkSelection selection, WorkRow anchor, WorkRow row) =>
        row.WorkId == selection.AnchorWorkId || row.Parent == selection.AnchorWorkId ||
        selection.Identity is not null && row.Side == selection.Side && row.Identity == selection.Identity &&
        row.WorkId > selection.AnchorWorkId && row.WorkId <= selection.ThroughWorkId && row.Parent == anchor.Parent &&
        row.Obligation == anchor.Obligation && (row.Before is null || row.Before == anchor.Before);

    // A peer's discovery walk and unconsumed dirty evidence still need its own selection.
    private static bool NeedsOwnSelection(WorkRow work, ChangeNotice? notice = null, bool residual = false) =>
        work.DiscoveryRequired || notice?.Observed is { IsFolder: true } || residual && notice?.LocalContentDirty == true;

    private static RootBinding? Roots(PairRow pair) => StateEncoding.DecodePair(pair.Definition).Roots;

    // The native domains the adapters report: the Local volume serial and the Dropbox root namespace.
    private static string? Domain(RootBinding? roots, Side side) =>
        roots is null ? null
            : side == Side.Local ? roots.LocalVolumeKey()
            : roots.Dropbox.RootNamespaceId;

    private static long EpochOn(LiveCoverage coverage, Side side) =>
        side == Side.Local ? coverage.Local.SourceEpoch : coverage.Dropbox.SourceEpoch;

    // Every SelectSynced* read returns the same saved-entry row shape.
    private static SyncedEntry ReadSaved(SqliteDataReader reader) =>
        StateEncoding.ReadSynced(reader, reader.GetString(StateSchema.SyncedLocalKey), StateSchema.SyncedLocalPath);

    private IEnumerable<T> Read<T>(SqliteTransaction? transaction, string sql, Func<SqliteDataReader, T> decode,
        params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(_connection, transaction, sql, arguments);
        using var reader = command.ExecuteReader();
        while (reader.Read()) yield return decode(reader);
    }

    private IEnumerable<WorkRow> Rows(SqliteTransaction? transaction, string sql,
        params (string Name, object? Value)[] arguments) => Read(transaction, sql, ReadWorkRow, arguments);

    private int Run(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(_connection, transaction, sql, arguments);
        return command.ExecuteNonQuery();
    }

    private PairRow? ReadPair(SqliteTransaction? transaction, string pairId) =>
        Read(transaction, StateSchema.PairFence, reader => new PairRow(pairId,
            reader.GetInt64(StateSchema.PairFenceId), reader.GetInt64(StateSchema.PairFenceExecutionGeneration),
            StateEncoding.ReadBool(reader, StateSchema.PairFenceFirstRepairDone),
            reader.GetString(StateSchema.PairFenceDefJson)), (StateSchema.ParamPair, pairId)).FirstOrDefault();

    private PairRow RequirePair(SqliteTransaction? transaction, string pairId) =>
        ReadPair(transaction, pairId) ?? throw new InvalidOperationException();

    private SourceRow? ReadSourceRow(SqliteTransaction? transaction, long pair, Side side) =>
        Read(transaction, StateSchema.SourceFence, static reader => new SourceRow(
            reader.GetInt64(StateSchema.SourceEpoch), Text(reader, StateSchema.SourceProvenanceJson),
            Text(reader, StateSchema.SourceCursor), Text(reader, StateSchema.SourceRepairReason),
            reader.GetInt64(StateSchema.SourceRepairRevision)),
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamSide, (int)side)).FirstOrDefault();

    private JournalPosition? ReadPosition(SqliteTransaction? transaction, string volumeKey) =>
        Read(transaction, StateSchema.JournalRead, static reader => new JournalPosition(
            Unsigned(reader, StateSchema.JournalId), Unsigned(reader, StateSchema.JournalNextUsn)),
            (StateSchema.ParamVolume, volumeKey)).FirstOrDefault();

    private long Watermark(SqliteTransaction? transaction) =>
        Read(transaction, StateSchema.WorkWatermark,
            static reader => reader.GetInt64(StateSchema.FirstColumn)).First();

    private static bool OwesRepair(SourceRow? source) => source is not { Reason: null };
    /// <summary>Whether either source owes a Repair, at the caller's already-resolved pair row.</summary>
    internal bool Obligated(long pair, SqliteTransaction? transaction = null) =>
        OwesRepair(ReadSourceRow(transaction, pair, Side.Local)) ||
        OwesRepair(ReadSourceRow(transaction, pair, Side.Dropbox));

    private LiveRefusal ReadLiveRefusal(PairRow pair) => ReadLiveRefusal(pair,
        ReadSourceRow(null, pair.Row, Side.Local), ReadSourceRow(null, pair.Row, Side.Dropbox));

    private LiveRefusal ReadLiveRefusal(PairRow pair, SourceRow? local, SourceRow? dropbox)
    {
        // An owed, running or failed Repair never refuses the pair by itself (B-140B); missing source state does.
        if (local is null || dropbox is null) return LiveRefusal.Pair;
        // A lost Local journal interval hides Local edits exactly as skipped coverage does (Q-R6-2).
        return LocalGap(local) is null && local.Reason != LostIntervalObligation(pair).ToString()
            ? LiveRefusal.None : LiveRefusal.LocalWrites;
    }

    internal bool HasPendingWork(SqliteTransaction transaction, long pair) => Read(transaction, StateSchema.PairHasPendingWork,
        static reader => StateEncoding.ReadBool(reader, StateSchema.FirstColumn), (StateSchema.ParamPairRow, pair)).First();

    private bool WorkExists(long pair, long work, SqliteTransaction? transaction) => Read(transaction, StateSchema.WorkDispatch,
        static reader => reader.GetString(StateSchema.DispatchWorkOrigin),
        (StateSchema.ParamWork, work), (StateSchema.ParamPairRow, pair)).Any();

    // An in-use lifecycle waits for its real opportunity; any other association follows the issue's own hold.
    private bool Held(long pair, long work)
    {
        if (Read(null, StateSchema.ItemEpisodeByWork,
                static reader => reader.GetInt64(StateSchema.EpisodeByWorkIssueId),
                (StateSchema.ParamWork, work)).Any())
            return true;
        if (ReadRenameContinuation(null, pair, work) is { Clash: { } captured } &&
            Read(null, StateSchema.RenameClashCurrent, IssueTable.ReadIssue,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, work)).FirstOrDefault() is { } current &&
            !current.Ignored && !current.Dismissed && current.Matches(captured)) return false;
        return Read(null, StateSchema.WorkIssue, static reader => Number(reader, StateSchema.WorkIssueId),
                (StateSchema.ParamWork, work)).FirstOrDefault() is { } issue &&
            Read(null, StateSchema.ValidateIssue, static reader => IssuePolicy.Holds(
                    (IssueKind)reader.GetInt32(StateSchema.ValidIssueKind),
                    StateEncoding.ReadEnum<TextKey>(reader, StateSchema.ValidIssueCause),
                    StateEncoding.Decode<IssueSubject>(reader.GetString(StateSchema.ValidIssueSubjectJson))),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issue)).FirstOrDefault();
    }

    // A known-identity group answers with its first member, so one group is one selection.
    private WorkRow Earliest(long pair, string pairId, WorkRow row, long through)
    {
        if (row.Identity is null) return row;
        var group = ToSelection(pairId, row, through) with { AnchorWorkId = BeforeFirstWork + 1 };
        var after = BeforeFirstWork;
        while (Rows(null, ReadIdentity(row), Membership(pair, group, row,
            (StateSchema.ParamAfter, after), (StateSchema.ParamTake, StateSchema.OneRow))).FirstOrDefault() is { } first)
        {
            if (first.WorkId == row.WorkId || !Held(pair, first.WorkId)) return first;
            after = first.WorkId;
        }
        return row;
    }

    private WorkRow ParentOwner(long pair, WorkRow row)
    {
        while (row.Parent is { } parent && Rows(null, StateSchema.Anchor, (StateSchema.ParamAnchor, parent),
                   (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, row.PairGeneration),
                   (StateSchema.ParamEpoch, row.SourceEpoch)).FirstOrDefault() is { } owner)
            row = owner;
        return row;
    }

    private PairRow RequireCurrent(SqliteTransaction? transaction, WorkSelection selection) =>
        ReadPair(transaction, selection.PairId) is { } pair && pair.Generation == selection.PairGeneration &&
        ReadSourceRow(transaction, pair.Row, selection.Side)?.Epoch == selection.SourceEpoch
            ? pair : throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);

    private WorkRow RequireAnchor(SqliteTransaction? transaction, long pair, WorkSelection selection) =>
        FindAnchor(transaction, pair, selection) ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);

    private WorkRow? FindAnchor(SqliteTransaction? transaction, long pair, WorkSelection selection)
    {
        var anchor = Rows(transaction, StateSchema.Anchor, (StateSchema.ParamAnchor, selection.AnchorWorkId),
            (StateSchema.ParamPairRow, pair), (StateSchema.ParamGeneration, selection.PairGeneration),
            (StateSchema.ParamEpoch, selection.SourceEpoch)).FirstOrDefault();
        if (anchor is not null && (anchor.Side != selection.Side || anchor.Domain != selection.Domain ||
            anchor.Identity != selection.Identity || anchor.Key != selection.Key))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        return anchor;
    }

    private PairRow RequireCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox)
    {
        var ownThrough = selection.Side == Side.Local ? local.ThroughWorkId : dropbox.ThroughWorkId;
        if (ReadPair(null, selection.PairId) is not { } pair || pair.Generation != selection.PairGeneration ||
            local.PairId != selection.PairId || dropbox.PairId != selection.PairId ||
            local.PairGeneration != pair.Generation || dropbox.PairGeneration != pair.Generation ||
            selection.ThroughWorkId > ownThrough ||
            ReadSourceRow(null, pair.Row, Side.Local) is not { } localSource ||
            ReadSourceRow(null, pair.Row, Side.Dropbox) is not { } dropboxSource ||
            ReadLiveRefusal(pair, localSource, dropboxSource) == LiveRefusal.Pair ||
            localSource.Epoch != local.SourceEpoch || dropboxSource.Epoch != dropbox.SourceEpoch)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        return pair;
    }

    private void RequireIssue(SqliteTransaction transaction, long pair, Issue exact)
    {
        var subject = JsonSerializer.Serialize(exact.Subject);
        if (!Read(transaction, StateSchema.ValidateIssue, reader =>
                (IssueKind)reader.GetInt32(StateSchema.ValidIssueKind) == exact.Kind &&
                StateEncoding.ReadEnum<TextKey>(reader, StateSchema.ValidIssueCause) == exact.Cause &&
                reader.GetString(StateSchema.ValidIssuePath) == exact.Path &&
                reader.GetString(StateSchema.ValidIssueSubjectJson) == subject,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, exact.Id)).FirstOrDefault())
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
    }

    // Same-identity work acquired after the selection is relevant wherever it now sits, inside the footprint or not.
    private bool LaterIdentityEvidence(long pair, WorkSelection selection, WorkRow anchor)
    {
        var after = selection.ThroughWorkId;
        while (selection.Identity is not null && Rows(null, StateSchema.PendingByIdentity,
                   (StateSchema.ParamSide, (int)selection.Side), (StateSchema.ParamDomain, selection.Domain),
                   (StateSchema.ParamIdentity, StateEncoding.IdFor(selection.Side, selection.Identity)),
                   (StateSchema.ParamAfterPair, pair), (StateSchema.ParamAfter, after),
                   (StateSchema.ParamTake, StateSchema.OneRow)).FirstOrDefault() is { } row && row.PairRow == pair)
        {
            if (row.Acquisition > selection.ThroughWorkId && !Owned(selection, anchor, row)) return true;
            after = row.WorkId;
        }
        return false;
    }

    private string? PendingPath(PairRow pair) => StateEncoding.DecodePending(Read(null, StateSchema.PendingRead,
            static reader => Text(reader, StateSchema.FirstColumn), (StateSchema.ParamPairRow, pair.Row))
        .FirstOrDefault()) switch
    {
        CurrentPending current => current.Value.Path,
        LegacyPending legacy => legacy.Path,
        _ => null
    };

    private PendingChange ProjectIssueWork(WorkSelection selection, WorkRow row)
    {
        var issue = Read(null, StateSchema.WorkIssue, static reader => Number(reader, StateSchema.WorkIssueId),
            (StateSchema.ParamWork, row.WorkId)).FirstOrDefault() ?? throw new InvalidDataException();
        var members = Read(null, StateSchema.ReadIssueMembers, reader =>
                Number(reader, StateSchema.MemberWorkId) == row.WorkId
                    // B-198: the projection reads only the item's side and path, so an item with no file id still decodes.
                    ? StateEncoding.ReadSubjectItems(reader, StateSchema.MemberSubjectJson) : null,
                (StateSchema.ParamIssue, issue)).ToList();
        var item = members.OfType<IReadOnlyList<IssueItem>>().SelectMany(static items => items)
                .FirstOrDefault(member => member.Side == row.Side &&
                    PathNames.KeyFor(member.Side, member.Path) == row.Key) ??
            throw new InvalidDataException();
        return new PendingChange(row.WorkId, selection.PairId, selection.PairGeneration, selection.SourceEpoch,
            new ChangeNotice(row.Side, ChangeKind.Changed, item.Path, null, row.Identity, null, false, false), null);
    }

    private void Advance(SqliteTransaction transaction, long pair, Side side, long generation, long epoch,
        long revision, string? expectedProvenance, string? expectedCursor, long nextEpoch, long nextRevision,
        string? provenance, string? cursor, string? reason) =>
        Require(Run(transaction, StateSchema.AdvanceSource, (StateSchema.ParamNextEpoch, nextEpoch),
            (StateSchema.ParamProvenance, provenance), (StateSchema.ParamCursor, cursor),
            (StateSchema.ParamGapReason, GapReason), (StateSchema.ParamReason, reason),
            (StateSchema.ParamNextRevision, nextRevision), (StateSchema.ParamPairRow, pair),
            (StateSchema.ParamSide, (int)side), (StateSchema.ParamEpoch, epoch),
            (StateSchema.ParamExpectedProvenance, expectedProvenance),
            (StateSchema.ParamExpectedCursor, expectedCursor),
            (StateSchema.ParamRevision, revision), (StateSchema.ParamGeneration, generation)));

    private int Retire(SqliteTransaction transaction, long pair, WorkSelection selection, WorkRow anchor)
    {
        if (ReadRenameContinuation(transaction, pair, anchor.WorkId) is { } continuation)
        {
            if (continuation.Clash is not null || Read(transaction, StateSchema.ReadRenameRemoval,
                    static reader => reader.GetString(StateSchema.FirstColumn),
                    (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, anchor.WorkId)).Any()) return 0;
            Require(Run(transaction, StateSchema.ClearRenameContinuation,
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, anchor.WorkId),
                (StateSchema.ParamNotice, StateEncoding.Envelope(continuation))));
        }
        var retired = Run(transaction, selection.Identity is null ? StateSchema.CompletePathSelection
            : CompleteIdentity(anchor), Membership(pair, selection, anchor));
        RequireSome(retired);
        return retired;
    }

    private void Associate(SqliteTransaction transaction, long pair, long work, Side side, string pathKey,
        object? identity, long issue, string subject)
    {
        var current = MemberIssue(transaction, work);
        if (current is null)
            Run(transaction, StateSchema.InsertIssueMember, (StateSchema.ParamIssue, issue),
                (StateSchema.ParamPairRow, pair), (StateSchema.ParamWork, work), (StateSchema.ParamSide, (int)side),
                (StateSchema.ParamPath, pathKey), (StateSchema.ParamIdentity, identity),
                (StateSchema.ParamSubject, subject), (StateSchema.ParamSkip, null));
        else if (current.Value.Issue != issue || current.Value.Subject != subject)
            Require(Run(transaction, StateSchema.ReassociateWork, (StateSchema.ParamNextIssue, issue),
                (StateSchema.ParamSubject, subject), (StateSchema.ParamWork, work),
                (StateSchema.ParamExpectedIssue, current.Value.Issue)));
    }

    // One command is reused for a whole page: the pair and statement stay fixed while only row values change.
    private SqliteCommand WorkInsert(SqliteTransaction transaction) => StorageCommand.Create(_connection, transaction,
        StateSchema.InsertWork, (StateSchema.ParamId, null), (StateSchema.ParamPairRow, null),
        (StateSchema.ParamGeneration, null), (StateSchema.ParamEpoch, null), (StateSchema.ParamSide, null),
        (StateSchema.ParamDomain, null), (StateSchema.ParamPath, null), (StateSchema.ParamOldPath, null),
        (StateSchema.ParamIdentity, null), (StateSchema.ParamNotice, null), (StateSchema.ParamBefore, null),
        (StateSchema.ParamAcquisition, null), (StateSchema.ParamParent, null), (StateSchema.ParamObligation, null));

    private static void AddWork(SqliteCommand insert, long workId, long pair, long generation, long epoch,
        string domain, ChangeNotice notice, SyncedEntry? before, long? acquisition, long? parent, string? obligation)
    {
        void Set(string name, object? value) => insert.Parameters[name].Value = value ?? DBNull.Value;
        Set(StateSchema.ParamId, workId);
        Set(StateSchema.ParamPairRow, pair);
        Set(StateSchema.ParamGeneration, generation);
        Set(StateSchema.ParamEpoch, epoch);
        Set(StateSchema.ParamSide, (int)notice.Side);
        Set(StateSchema.ParamDomain, domain);
        Set(StateSchema.ParamPath, PathNames.KeyFor(notice.Side, notice.Path));
        Set(StateSchema.ParamOldPath, notice.OldPath is null ? null : PathNames.KeyFor(notice.Side, notice.OldPath));
        Set(StateSchema.ParamIdentity, Identity(notice.Side, notice.Id));
        Set(StateSchema.ParamNotice, StateEncoding.Envelope(notice));
        Set(StateSchema.ParamBefore, StateEncoding.OptionalEnvelope(before));
        Set(StateSchema.ParamAcquisition, acquisition);
        Set(StateSchema.ParamParent, parent);
        Set(StateSchema.ParamObligation, obligation);
        insert.ExecuteNonQuery();
    }

    private sealed record Pass(long Generation, long After, long Through, PathScope? Scope)
    {
        internal Dictionary<long, WorkSelection>? Claims { get; set; }
        internal long NetRetired { get; set; }
        internal bool Released { get; set; }
        internal Func<SyncedEntry, bool>? LocalEchoResponsibility { get; set; }
        internal HashSet<long>? Attempted { get; set; }
        internal Dictionary<long, long>? Suspended { get; set; }
        internal HashSet<long>? Completed { get; set; }
        internal Dictionary<(long Work, long Through), bool>? Deletions { get; set; }
        internal Dictionary<(long Work, long Through), (bool Deleted, string? Restatement)>? DirectDeletions { get; set; }
    }

    private sealed record PairRow(string Id, long Row, long Generation, bool FirstRepairDone, string Definition);

    private sealed record SourceRow(long Epoch, string? Provenance, string? Cursor, string? Reason, long Revision);

    private sealed record WorkRow(long WorkId, long PairRow, long PairGeneration, long SourceEpoch, Side Side,
        string Domain, string Key, string? OldKey, string? Identity, string? Notice, string? Before, long? Acquisition,
        long? Parent, string? Obligation, bool DiscoveryRequired);

    /// <summary>Covered when the open name clash at the path continues this subject and already registers the member
    /// with its current fences and evidence; Attach when it continues but the member is missing or stale; else Raise.
    /// Reads only the issue at the path and the member's own registration rows.</summary>
    internal (ClashRefusal State, Issue? Existing) ClashRefusalState(WorkSelection selection, string path,
        IssueSubject subject, (ChangeNotice Notice, bool IsFolder, SyncedEntry? Before) work)
    {
        using var transaction = _connection.BeginTransaction(deferred: true);
        var pair = RequireCurrent(transaction, selection);
        RequireAnchor(transaction, pair.Row, selection);
        var existing = IssueTable.AtPath(_connection, transaction, selection.PairId, path, IssueKind.NameClash);
        if (existing is null || existing.Dismissed ||
            !IssueTable.Continues(existing, TextKey.ClashNameTaken, subject) || IssueTable.Refreshes(existing, subject))
            return (ClashRefusal.Raise, null);
        if (work.Notice.Side != selection.Side) throw new ArgumentException(null, nameof(work));
        var covered = IssueRegistration(transaction, pair.Row, existing.Id, work.Notice) is { } stored &&
            SameFences(stored, selection) && RegisteredNotice(stored, work.Notice) == stored.Notice &&
            MemberIssue(transaction, stored.WorkId) == (existing.Id, StateEncoding.Envelope(existing.Subject));
        return (covered ? ClashRefusal.Covered : ClashRefusal.Attach, existing);
    }

    private WorkRow? IssueRegistration(SqliteTransaction transaction, long pair, long issue, ChangeNotice notice) =>
        Rows(transaction, StateSchema.Registration, (StateSchema.ParamPairRow, pair), (StateSchema.ParamIssue, issue),
            (StateSchema.ParamSide, (int)notice.Side),
            (StateSchema.ParamPath, PathNames.KeyFor(notice.Side, notice.Path)),
            (StateSchema.ParamIdentity, Identity(notice.Side, notice.Id))).FirstOrDefault();

    private static bool SameFences(WorkRow stored, WorkSelection parent) =>
        stored.PairGeneration == parent.PairGeneration && stored.SourceEpoch == parent.SourceEpoch;

    // The notice a registration carries once this notice is attached: dirty evidence is merged, never erased.
    private static string RegisteredNotice(WorkRow stored, ChangeNotice notice)
    {
        var incoming = StateEncoding.Envelope(notice);
        if (stored.Notice == incoming) return incoming;
        var merged = MergeContent(StateEncoding.DecodeEnvelope<ChangeNotice>(stored.Notice!) ??
            throw new InvalidDataException(), notice);
        return ReferenceEquals(merged, notice) ? incoming : StateEncoding.Envelope(merged);
    }

    private (long? Issue, string Subject)? MemberIssue(SqliteTransaction transaction, long work) =>
        Read<(long? Issue, string Subject)?>(transaction, StateSchema.WorkIssue, static reader => (
                Number(reader, StateSchema.WorkIssueId), reader.GetString(StateSchema.WorkIssueSubjectJson)),
            (StateSchema.ParamWork, work)).FirstOrDefault();

    // B-135: a top-level group seeks work_identity; a residual's parent and obligation name one row exactly.
    private static string IdentityShape(WorkRow anchor, string topLevel, string residual) =>
        anchor.Parent is null ? topLevel : residual;

    private static string ReadIdentity(WorkRow anchor) =>
        IdentityShape(anchor, StateSchema.ReadIdentitySelection, StateSchema.ReadResidualIdentitySelection);

    private static string CompleteIdentity(WorkRow anchor) =>
        IdentityShape(anchor, StateSchema.CompleteIdentitySelection, StateSchema.CompleteResidualIdentitySelection);

    private static string SaveBeforeIdentity(WorkRow anchor) =>
        IdentityShape(anchor, StateSchema.SaveBeforeIdentity, StateSchema.SaveBeforeResidualIdentity);

    // B-133: the executor asks this after its ancestor scheduling and before observing, so excluded work does not
    // spend a provider read only to settle as out of scope.
    /// <summary>Completes the selection through CompleteWork when every member names only excluded paths
    /// (PathScope.ExcludesChange) and owes nothing else, and returns whether it did. The caller has already checked
    /// that the anchor key is excluded. Rename custody, residuals, discovery, Local dirty evidence, a recorded protecting
    /// member at or under a Local member path and a refused completion return false and keep the ordinary path.</summary>
    internal bool CompleteOutOfScope(WorkSelection selection, PathScope scope)
    {
        // Cheapest evidence first: each member's own flags, then the identity baseline (one read), then the walk.
        Dictionary<(Side, string), SyncedEntry?>? saved = null;
        var baselinePending = selection.Identity is not null;
        var probed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in ReadWork(selection))
        {
            if (member.IsResidual || member.DiscoveryRequired ||
                member.Notice is { Side: Side.Local, LocalContentDirty: true }) return false;
            if (baselinePending)
            {
                if (!scope.ExcludesBinding(SelectionBaseline(null, selection))) return false;
                baselinePending = false;
            }
            var state = (Owner: this, selection.PairId, Saved: saved ??= new());
            if (!scope.ExcludesBinding(member.Before) || !scope.ExcludesChange(member.Notice, state, SavedOnce))
                return false;
            // P2-1: a recorded protecting member at or under a Local path is released only by the proving walk.
            if (member.Notice is { Side: Side.Local } local &&
                (UnreadableAtOrUnder(selection.PairId, local.Path, probed) ||
                    local.OldPath is { } old && UnreadableAtOrUnder(selection.PairId, old, probed))) return false;
        }
        if (ReadRenameContinuation(selection) is not null) return false;
        if (selection.Identity is not null)
            return (!baselinePending || scope.ExcludesBinding(SelectionBaseline(null, selection))) &&
                CompleteWork(selection, null);
        // A path selection's baseline is its anchor's binding by key, which the walk has usually read already.
        var baseline = saved is not null && saved.TryGetValue((selection.Side, selection.Key), out var known)
            ? known : SelectionBaseline(null, selection);
        return scope.ExcludesBinding(baseline) && CompleteWork(selection, null);
    }

    // A key already probed in this decision answered no, or the decision would have returned.
    private bool UnreadableAtOrUnder(string pairId, string path, HashSet<string> probed)
    {
        var key = PathNames.Key(path);
        return probed.Add(key) && IssueTable.UnreadableAtOrUnder(_connection, pairId, key);
    }

    private static SyncedEntry? SavedOnce(
        (ChangeTable Owner, string PairId, Dictionary<(Side, string), SyncedEntry?> Saved) state, Side side, string key)
    {
        if (!state.Saved.TryGetValue((side, key), out var entry))
            state.Saved[(side, key)] = entry = SavedByKey((state.Owner, state.PairId), side, key);
        return entry;
    }

    private static SyncedEntry? SavedByKey((ChangeTable Owner, string PairId) state, Side side, string key) =>
        state.Owner.Read(null, side == Side.Local ? StateSchema.SelectSyncedByLocalKey
                : StateSchema.SelectSyncedByDropboxKey, ReadSaved, (StateSchema.ParamPair, state.PairId),
            (StateSchema.ParamTargetKey, key)).FirstOrDefault();
}
