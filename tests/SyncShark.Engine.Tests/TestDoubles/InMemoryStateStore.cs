using Massdex.SyncShark.Engine.Storage;
using FolderSubmissionPhase = Massdex.SyncShark.Engine.FolderSubmission.SubmissionPhase;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>An in-memory IStateStore for Executor and Run checks.</summary>
/// <remarks>Mirrors Storage exactly: Commit order of section 3.5, issue lifecycle of 2.4, scan writer and reader of
/// 3.3/3.4. Every member takes one gate, as the real store serialises its writes.</remarks>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PairConfig> _pairs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepairSummary> _lastRepair = new(StringComparer.Ordinal);
    private readonly HashSet<string> _firstRepairDone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _limiterLevels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<SyncedEntry>> _synced = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingEffect> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Issue>> _issues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceRow> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JournalPosition> _journal = new(StringComparer.Ordinal);
    private readonly List<WorkRow> _work = [];
    private readonly Dictionary<string, Massdex.SyncShark.V2.FailureRetryOwner.State> _retries =
        new(StringComparer.Ordinal);
    private readonly Dictionary<object, (string PairId, long Generation, Issue Notice)> _pageWaits =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, ItemRetryCandidate> _items = [];
    private readonly Dictionary<(string PairId, string OperationId), FolderOperation> _folders = [];
    private readonly Dictionary<string, SubmissionRecord> _submissions = new(StringComparer.Ordinal);
    private readonly Dictionary<(string PairId, Guid ScanId), ScanRecord> _scans = [];
    private readonly HashSet<(string PairId, Guid ScanId, Side Side, string Key)> _skipTouched = [];
    private readonly Dictionary<(string PairId, Side Side, string Key), (TextKey Cause, IssueSubject Subject)>
        _skipMembers = [];
    private readonly Dictionary<string, HashSet<string>> _deletedNames = new(StringComparer.Ordinal);
    private readonly HashSet<(string PairId, Guid ScanId)> _incrementalHistory = [];
    private readonly HashSet<(string PairId, Guid ScanId)> _resumedHistory = [];
    private readonly Dictionary<string, HashSet<string>> _spentDeletedNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _deletedDelta = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long Generation, string Cursor, HashSet<string> Names)> _deletedHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pass> _passes = new(StringComparer.Ordinal);
    private readonly List<ScanRow> _scan = [];
    private readonly Dictionary<(string PairId, Guid ScanId), List<Issue>> _scanIssues = [];
    private long _nextIssueId;
    private long _nextWorkId;

    public (PairConfig Pair, long Generation)? ReadPairExecution(string pairId)
    {
        lock (_gate)
            return _pairs.TryGetValue(pairId, out var pair) ? (pair, Generation(pairId)) : null;
    }

    public ItemRetryCandidate? ReadItemRetryForSubject(string pairId, long expectedGeneration,
        string path, IssueSubject subject)
    {
        ArgumentNullException.ThrowIfNull(pairId);
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(subject);
        var key = PathNames.KeyFor(Side.Local, path);
        var local = IssueSubject.DistinctItems(subject.Items.Where(item => item.Side == Side.Local));
        if (local.Length != 1 || !PathNames.Comparer.Equals(PathNames.KeyFor(Side.Local, local[0].Path), key))
            throw new ArgumentException(null, nameof(subject));
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            var identity = StateEncoding.LocalId(local[0].Id);
            var ids = _items.Where(entry => entry.Value.Token.Subject.Items.Any(item =>
                        item.Side == Side.Local && Equals(StateEncoding.LocalId(item.Id), identity) &&
                        PathNames.Comparer.Equals(PathNames.KeyFor(Side.Local, item.Path), key)))
                .Select(entry => entry.Key).Distinct().ToArray();
            if (ids.Length == 0) return null;
            if (ids.Length != 1)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var candidate = _items.GetValueOrDefault(ids[0]);
            if (candidate is null || candidate.Token.PairGeneration != expectedGeneration ||
                !candidate.Token.Subject.HasSameItems(subject))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return candidate;
        }
    }

    public long ReadWorkWatermark()
    {
        lock (_gate) return _nextWorkId;
    }

    public void SaveImportedPair(PairConfig pair, bool legacyPaused, IEnumerable<SyncedEntry>? baselines = null)
    {
        lock (_gate)
        {
            if (_pairs.ContainsKey(pair.PairId) || pair.Roots is not null || legacyPaused && pair.Enabled)
                throw new ArgumentException(null, nameof(pair));
            var rows = baselines?.ToList() ?? [];
            if (rows.Select(row => row.LocalKey).Distinct().Count() != rows.Count ||
                rows.Select(row => row.DropboxKey).Distinct().Count() != rows.Count)
                throw new ArgumentException(null, nameof(baselines));
            _synced[pair.PairId] = rows;
            _pairs[pair.PairId] = pair;
            SeedSources(pair.PairId);
            if (legacyPaused)
                Raise(pair.PairId, IssueRows(pair.PairId), IssueKind.Info, TextKey.InfoPairPausedInOldApp,
                    string.Empty, new IssueSubject([], null), DateTimeOffset.UtcNow);
        }
    }

    public void MergeDriveEvidence(string pairId, long expectedGeneration, DriveEvidence acquired)
    {
        ArgumentNullException.ThrowIfNull(acquired);
        lock (_gate)
        {
            if (!_pairs.TryGetValue(pairId, out var current) || Generation(pairId) != expectedGeneration)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            var merged = DriveEvidence.After(current.Drive, acquired);
            if (merged == current.Drive) return;
            _pairs[pairId] = current with { Drive = merged };
        }
    }

    public void ReconcileRootIdentity(string pairId, long expectedGeneration, RootBinding? expectedBinding,
        Issue? expectedIssue, StateChange issueOnlyChange)
    {
        lock (_gate)
        {
            RequireIssueOnly(issueOnlyChange);
            if (issueOnlyChange.FirstRunAcknowledged.Count != 0 ||
                issueOnlyChange.Raise.Any(issue => issue.Kind != IssueKind.RootIdentity || issue.Path != string.Empty) ||
                issueOnlyChange.Close.Any(close => close.Path != string.Empty ||
                    close.Kinds.Any(kind => kind != IssueKind.RootIdentity)) ||
                expectedIssue is not null &&
                    (expectedIssue.Kind != IssueKind.RootIdentity || expectedIssue.Path != string.Empty))
                throw new ArgumentException(null, nameof(issueOnlyChange));
            if (!_pairs.TryGetValue(pairId, out var current) || Generation(pairId) != expectedGeneration)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            if (current.Roots != expectedBinding)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            var issues = IssueRows(pairId);
            var stored = issues.FirstOrDefault(issue => issue.Kind == IssueKind.RootIdentity && issue.Path.Length == 0);
            if (expectedIssue is null
                    ? stored is not null
                    : stored is null || !stored.Matches(expectedIssue))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            if (stored is null && issueOnlyChange.Raise.Count == 0) return;
            ApplyAtomic(pairId, issueOnlyChange);
        }
    }

    public void SavePairDecision(PairConfig expected, long expectedGeneration, PairConfig next,
        Issue? expectedIssue, bool resetHistory, string? reincludedKey)
    {
        lock (_gate)
        {
            if (expected.PairId != next.PairId || expected.LocalRoot != next.LocalRoot ||
                expected.DropboxRoot != next.DropboxRoot || expected.ConnectionId != next.ConnectionId)
                throw new ArgumentException(null, nameof(next));
            if (!_pairs.TryGetValue(expected.PairId, out var current) ||
                Generation(expected.PairId) != expectedGeneration || Encode(current) != Encode(expected))
                throw new InvalidOperationException();
            if (reincludedKey is not null)
            {
                if (resetHistory || expectedIssue is not null ||
                    Encode(next with { Exclusions = expected.Exclusions }) != Encode(expected))
                    throw new ArgumentException(null, nameof(reincludedKey));
            }
            if ((resetHistory || reincludedKey is not null) && (_pending.ContainsKey(expected.PairId) ||
                _folders.Any(entry => entry.Key.PairId == expected.PairId && entry.Value.State.Active)))
                throw new InvalidOperationException();
            var hasEpisode = _retries.TryGetValue(expected.PairId, out var episode);
            var notice = hasEpisode ? RetryNotice(expected.PairId) ?? throw new InvalidOperationException() : null;
            var local = RequireSource(expected.PairId, Side.Local);
            var dropbox = RequireSource(expected.PairId, Side.Dropbox);
            if (notice is not null)
                CloseSupersededRetryNotice(expected.PairId, notice, expectedGeneration, local, dropbox, episode);
            if (expectedIssue is not null && !CloseExact(expected.PairId, expectedIssue))
                throw StaleIssue(expectedIssue.Id);
            if (HasCurrentLocalWork(expected.PairId, expectedGeneration))
                TransferLocalEvidenceGap(expected.PairId, expectedGeneration, local.Epoch, local.Revision);
            if (HasCurrentLocalWork(expected.PairId, expectedGeneration) &&
                local.Obligation != SourceObligation.LocalSkippedCoverage)
                throw new InvalidOperationException();
            _deletedHistory.Remove(expected.PairId);
            _deletedDelta.Remove(expected.PairId);
            if (RootBinding.HistoryIdentityChanged(current, next)) _spentDeletedNames.Remove(expected.PairId);
            _pairs[expected.PairId] = next;
            _generations[expected.PairId] = checked(expectedGeneration + 1);
            Advance(expected.PairId, Side.Local, expectedGeneration + 1, local.Epoch, local.Revision,
                local.Provenance, local.Cursor, local.Epoch + 1, local.Revision + 1, null, null,
                SourceObligations.Owed(local.Obligation, SourceObligation.Configuration));
            Advance(expected.PairId, Side.Dropbox, expectedGeneration + 1, dropbox.Epoch, dropbox.Revision,
                dropbox.Provenance, dropbox.Cursor, dropbox.Epoch + 1, dropbox.Revision + 1, null, null,
                SourceObligations.Owed(dropbox.Obligation, SourceObligation.Configuration));
            if (!resetHistory)
                TransferLocalDirtyEvidence(expected.PairId, next.Roots, expectedGeneration, local.Epoch,
                    expectedGeneration + 1, local.Epoch + 1);
            if (resetHistory)
            {
                _synced.Remove(expected.PairId);
                _firstRepairDone.Remove(expected.PairId);
            }
            if (notice is not null)
                Raise(expected.PairId, IssueRows(expected.PairId), notice.Kind, notice.Cause, notice.Path,
                    notice.Subject, DateTimeOffset.UtcNow);
            ResetScan(expected.PairId);
        }
    }

    public IReadOnlyList<PairConfig> Pairs()
    {
        lock (_gate) return _pairs.Values.OrderBy(pair => pair.PairId, StringComparer.Ordinal).ToList();
    }

    public PairConfig? Pair(string pairId)
    {
        lock (_gate) return _pairs.GetValueOrDefault(pairId);
    }

    public void SavePair(PairConfig pair)
    {
        lock (_gate)
        {
            if (_pairs.TryGetValue(pair.PairId, out var before) && before with { Drive = pair.Drive } != pair)
            {
                _deletedHistory.Remove(pair.PairId);
                _deletedDelta.Remove(pair.PairId);
                if (RootBinding.HistoryIdentityChanged(before, pair))
                {
                    _spentDeletedNames.Remove(pair.PairId);
                    ResetScan(pair.PairId);
                }
            }
            _pairs[pair.PairId] = pair;
            SeedSources(pair.PairId);
        }
    }

    public void RemovePair(string pairId)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId)) throw new InvalidOperationException();
            if (_pending.ContainsKey(pairId)) throw new InvalidOperationException();
            if (_folders.Where(entry => entry.Key.PairId == pairId).Any(entry =>
                    entry.Value.State.Active ||
                    entry.Value.State.Phase is FolderRemovalPhase.Unknown or FolderRemovalPhase.Retained &&
                    entry.Value.State.RetirementDisposition is null))
                throw new InvalidOperationException();
            foreach (var entry in _folders.Where(entry => entry.Key.PairId == pairId))
                foreach (var item in entry.Value.Items)
                {
                    var submission = item.SubmissionId is { } id ? _submissions.GetValueOrDefault(id) : null;
                    if (item.StageAllocation is not null || item.Stage is not null ||
                        submission is { } linked && linked.State.Phase is FolderSubmissionPhase.Armed or
                            FolderSubmissionPhase.Accepted)
                        throw new InvalidOperationException();
                    if (entry.Value.State.RetirementDisposition is null &&
                        (item.EffectState is FolderRemovalItem.EffectStatus.Armed or
                                FolderRemovalItem.EffectStatus.Accepted or FolderRemovalItem.EffectStatus.Unknown ||
                            submission is { } receipt && receipt.State.Phase is FolderSubmissionPhase.Armed
                                or FolderSubmissionPhase.Accepted or FolderSubmissionPhase.Unknown))
                        throw new InvalidOperationException();
                }
            _pairs.Remove(pairId);
            _generations.Remove(pairId);
            _lastRepair.Remove(pairId);
            _firstRepairDone.Remove(pairId);
            _synced.Remove(pairId);
            _pending.Remove(pairId);
            foreach (var issue in IssueRows(pairId)) _items.Remove(issue.Id);
            _issues.Remove(pairId);
            _scan.RemoveAll(row => row.PairId == pairId);
            _sources.Remove(SourceKey(pairId, Side.Local));
            _sources.Remove(SourceKey(pairId, Side.Dropbox));
            _retries.Remove(pairId);
            foreach (var scan in _scans.Keys.Where(key => key.PairId == pairId).ToArray()) _scans.Remove(scan);
            _deletedNames.Remove(pairId);
            _deletedHistory.Remove(pairId);
            _deletedDelta.Remove(pairId);
            _spentDeletedNames.Remove(pairId);
            _resumedHistory.RemoveWhere(key => key.PairId == pairId);
            _incrementalHistory.RemoveWhere(key => key.PairId == pairId);
            _work.RemoveAll(row => row.PairId == pairId);
            foreach (var member in _skipMembers.Keys.Where(key => key.PairId == pairId).ToList())
                _skipMembers.Remove(member);
            foreach (var key in _folders.Keys.Where(key => key.PairId == pairId).ToList())
                _folders.Remove(key);
            foreach (var key in _submissions.Where(entry => entry.Value.State.PairId == pairId)
                         .Select(entry => entry.Key).ToList())
                _submissions.Remove(key);
        }
    }

    public bool FirstRepairDone(string pairId)
    {
        lock (_gate) return _firstRepairDone.Contains(pairId);
    }

    // Seeds a completed first Repair, as the SQLite checks do with first_repair_done.
    internal void MarkFirstRepairDone(string pairId)
    {
        lock (_gate) _firstRepairDone.Add(pairId);
    }

    public SourceObligation LostIntervalObligation(string pairId)
    {
        lock (_gate) return _firstRepairDone.Contains(pairId)
            ? SourceObligation.NormalRepair : SourceObligation.Bootstrap;
    }

    public RepairSummary? LastRepair(string pairId)
    {
        lock (_gate) return _lastRepair.GetValueOrDefault(pairId);
    }

    public void MarkRepairLogFailed(string pairId, string runId)
    {
        lock (_gate)
            if (_lastRepair.TryGetValue(pairId, out var summary) && summary.RunId == runId)
                _lastRepair[pairId] = summary with { LogFailed = true };
    }

    /// <summary>Progress only, never completion: the durable flag moves solely through the finished-repair overload.</summary>
    public void FinishRepair(string pairId, RepairSummary summary)
    {
        lock (_gate) _lastRepair[pairId] = summary with { Completed = false };
    }

    public bool FinishRepair(string pairId, RepairSummary summary, long expectedGeneration, Guid scanToken,
        LocalCatchUp local, DropboxCatchUp dropbox)
    {
        lock (_gate)
        {
            var scan = ReadScan(pairId, scanToken);
            var localEpoch = scan.LocalEpoch;
            var dropboxEpoch = scan.DropboxEpoch;
            var localRevision = scan.LocalRevision;
            var dropboxRevision = scan.DropboxRevision;
            if (scan.Scope is not null || !summary.Completed || scan.Generation != expectedGeneration || local.PairId != pairId || dropbox.PairId != pairId ||
                local.PairGeneration != expectedGeneration || dropbox.PairGeneration != expectedGeneration ||
                local.SourceEpoch != localEpoch || dropbox.SourceEpoch != dropboxEpoch ||
                scan.ThroughWorkId > local.ThroughWorkId || scan.ThroughWorkId > dropbox.ThroughWorkId ||
                RequireSource(pairId, Side.Dropbox) is not { } remote ||
                remote.Epoch != dropboxEpoch || remote.Revision != dropboxRevision)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            var domain = Domain(pairId, Side.Local) ?? throw new InvalidOperationException();
            var position = _journal.GetValueOrDefault(domain);
            if (position is null || position.JournalId != local.Checkpoint.JournalId ||
                position.NextUsn < checked((ulong)local.Checkpoint.NextUsn))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            RequireGeneration(pairId, expectedGeneration);
            var currentLocal = RequireSource(pairId, Side.Local);
            if (currentLocal.Epoch != localEpoch || currentLocal.Revision < localRevision ||
                currentLocal.Revision == localRevision && ReadLocalGap(pairId, localEpoch) != scan.LocalGapRevision ||
                currentLocal.Revision > localRevision && currentLocal.Obligation is null)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            if (currentLocal.Revision > localRevision) return false;
            var hasEpisode = _retries.ContainsKey(pairId);
            var notice = hasEpisode ? RetryNotice(pairId) ?? throw new InvalidOperationException() : null;
            _lastRepair[pairId] = summary;
            _firstRepairDone.Add(pairId);
            FinishObligation(pairId, Side.Local, expectedGeneration, localEpoch, localRevision);
            FinishObligation(pairId, Side.Dropbox, expectedGeneration, dropboxEpoch, dropboxRevision);
            _retries.Remove(pairId);
            if (notice is not null)
                CloseFinishedRetryNotice(pairId, notice, expectedGeneration);
            RetireCoveredWork(pairId, expectedGeneration, Side.Local, localEpoch, scan.ThroughWorkId);
            RetireCoveredWork(pairId, expectedGeneration, Side.Dropbox, dropboxEpoch, scan.ThroughWorkId);
            return true;
        }
    }

    public void ForgetAllSynced(string pairId)
    {
        lock (_gate)
        {
            _synced.Remove(pairId);
            _firstRepairDone.Remove(pairId);
        }
    }

    public int? LimiterLevel(string connectionId)
    {
        lock (_gate) return _limiterLevels.TryGetValue(connectionId, out var level) ? level : null;
    }

    public void SaveLimiterLevel(string connectionId, int level)
    {
        lock (_gate) _limiterLevels[connectionId] = level;
    }

    public SyncedEntry? SyncedByKey(string pairId, Side side, string key)
    {
        lock (_gate)
        {
            var rows = SyncedRows(pairId);
            return side == Side.Local
                ? rows.FirstOrDefault(row => row.LocalKey == key)
                : rows.FirstOrDefault(row => row.DropboxKey == key);
        }
    }

    public SyncedEntry? SyncedById(string pairId, Side side, string id)
    {
        lock (_gate)
        {
            var rows = SyncedRows(pairId);
            return side == Side.Local
                ? rows.FirstOrDefault(row => row.LocalId == id)
                : rows.FirstOrDefault(row => row.DropboxId == id);
        }
    }

    public IReadOnlyList<SyncedEntry> SyncedBindingsById(string pairId, Side side, string id)
    {
        lock (_gate) return SyncedRows(pairId).Where(row => row.IdOn(side) == id).ToArray();
    }

    public RenameContinuation? ReadRenameContinuation(WorkSelection selection)
    {
        lock (_gate) { RequireCurrent(selection); return RequireAnchor(selection).RenameContinuation; }
    }

    public void RegisterRenameContinuation(WorkSelection selection, SyncedEntry incoming, SyncedEntry destination, Issue? clash)
    {
        lock (_gate)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            var baseline = selection.Identity is null ? SyncedByKey(selection.PairId, selection.Side, selection.Key) :
                SyncedById(selection.PairId, selection.Side, selection.Identity);
            if (anchor.Notice is null || baseline != incoming ||
                SyncedByKey(selection.PairId, Side.Local, destination.LocalKey) != destination)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
            if (clash is not null) RequireIssue(selection.PairId, clash);
            var next = new RenameContinuation(destination, clash);
            if (anchor.RenameContinuation is { } current && (current.Destination != destination ||
                (current.Clash is null ? clash is not null : clash is null || !current.Clash.Matches(clash))))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            anchor.RenameContinuation = next;
        }
    }

    public IReadOnlyList<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> RenameDestinationOwners(
        string pairId, SyncedEntry? before, SyncedEntry after)
    {
        lock (_gate) return RenameOwners(SyncedRows(pairId), before, after).ToArray();
    }

    private static IEnumerable<(SyncedEntry Binding, bool LocalKeyMatched, bool DropboxKeyMatched)> RenameOwners(
        IEnumerable<SyncedEntry> rows, SyncedEntry? before, SyncedEntry after) =>
        rows.Where(row => row.LocalKey != before?.LocalKey)
            .Select(row => (Binding: row, LocalKeyMatched: row.LocalKey == after.LocalKey,
                DropboxKeyMatched: row.DropboxKey == after.DropboxKey))
            .Where(owner => owner.LocalKeyMatched || owner.DropboxKeyMatched);

    public void Commit(string pairId, StateChange change, Action<Issue>? committedIssue = null) =>
        Commit(pairId, change, null, committedIssue);

    public void Commit(string pairId, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue)
    {
        List<Issue> raised;
        lock (_gate)
        {
            var advance = RenameMembers(pairId, selection, change);
            raised = ApplyAtomic(pairId, change);
            foreach (var (row, after) in advance) row.Before = after;
            if (selection is not null && RequireAnchor(selection) is { RenameContinuation: { } continuation } anchor &&
                change.Renames.Any(rename => rename.After.IdOn(selection.Side) == selection.Identity &&
                    rename.After.KeyOn(selection.Side) == selection.Key))
            {
                if (continuation.Clash is { } clash) CloseExact(pairId, clash);
                foreach (var operation in _folders.Where(item => item.Key.PairId == pairId).Select(item => item.Value))
                    if (operation.RenameWork == anchor.WorkId && operation.State is { Active: false, Phase: FolderRemovalPhase.Complete })
                        operation.RenameWork = null;
                anchor.RenameContinuation = null;
            }
        }
        if (committedIssue is not null)
            foreach (var issue in raised) committedIssue(issue);
    }

    public Task CommitAsync(string pairId, StateChange change)
    {
        if (QueueableRecord(change)) return CommitRecordAsync(pairId, change, null);
        Commit(pairId, change);
        return Task.CompletedTask;
    }

    public Task CommitRecordAsync(string pairId, StateChange change, Action<Issue>? committedIssue)
    {
        if (change.Raise.Count != 0 || change.Record.Any(entry => entry.IsFolder))
        {
            Commit(pairId, change, committedIssue);
            return Task.CompletedTask;
        }
        StateStore.RequireQueueableRecord(change);
        // The real store only batches its queue; committing now has the same visible effect.
        Commit(pairId, change, committedIssue);
        return Task.CompletedTask;
    }

    public PendingRead Pending(string pairId)
    {
        lock (_gate) return _pending.TryGetValue(pairId, out var effect) ? new CurrentPending(effect) : new NoPending();
    }

    /// <summary>The convenience the old double offered; the current note must be empty and already armed.</summary>
    public void SetPending(string pairId, PendingEffect effect)
    {
        lock (_gate)
        {
            if (_pending.ContainsKey(pairId)) throw new InvalidOperationException("A pending effect already exists.");
            _pending[pairId] = effect;
        }
    }

    public void ArmPending(string pairId, PendingEffect value)
    {
        lock (_gate)
        {
            if (value.Generation != 0 || value.Phase != PendingPhase.Armed)
                throw new ArgumentException(null, nameof(value));
            if (!_pairs.ContainsKey(pairId)) throw new InvalidOperationException();
            RequirePendingSaved(pairId, value);
            if (_pending.ContainsKey(pairId)) throw new InvalidOperationException();
            _pending[pairId] = value;
        }
    }

    public void AdvancePending(string pairId, PendingEffect expected, PendingEffect next)
    {
        lock (_gate)
        {
            if (next.OperationId != expected.OperationId || next.Generation != checked(expected.Generation + 1) ||
                next.Kind != expected.Kind || next.Path != expected.Path || next.Losing != expected.Losing ||
                next.Saved != expected.Saved || next.Source != expected.Source || next.Stage != expected.Stage ||
                next.PermanentAllowed != expected.PermanentAllowed)
                throw new ArgumentException(null, nameof(next));
            if (!_pending.TryGetValue(pairId, out var current) || current != expected)
                throw new InvalidOperationException();
            _pending[pairId] = next;
            foreach (var issue in _items.Keys.ToList())
                if (_items[issue] is { Dispatch: ItemRetryDispatch.LocalRecovery } candidate &&
                    candidate.PendingOperationId == expected.OperationId &&
                    candidate.PendingGeneration == expected.Generation)
                {
                    _items[issue] = candidate with { PendingGeneration = next.Generation };
                    var work = _work.FirstOrDefault(row => row.PairId == pairId && row.WorkId == candidate.Token.WorkId);
                    if (work is not null) work.PendingGeneration = next.Generation;
                }
        }
    }

    public void SettlePending(string pairId, PendingEffect expected, StateChange change, Action<Issue>? committedIssue = null)
    {
        var raised = SettlePendingCore(pairId, expected, change);
        if (committedIssue is not null)
            foreach (var issue in raised) committedIssue(issue);
    }

    public void SettlePending(string pairId, PendingEffect expected, StateChange change, WorkSelection? selection, Action<Issue>? committedIssue)
    {
        List<Issue> raised;
        lock (_gate)
        {
            var advance = RenameMembers(pairId, selection, change);
            raised = SettlePendingCore(pairId, expected, change);
            foreach (var (row, after) in advance) row.Before = after;
        }
        if (committedIssue is not null)
            foreach (var issue in raised) committedIssue(issue);
    }

    private List<Issue> SettlePendingCore(string pairId, PendingEffect expected, StateChange change)
    {
        lock (_gate)
        {
            if (change.Renames.Count != 0 || change.ForgetSubtrees.Count != 0 || change.ForgetBothSubtrees.Count != 0)
                throw new ArgumentException(null, nameof(change));
            if (change.FirstRunAcknowledged.Count != 0 &&
                (expected is not ({ Kind: PendingKind.LocalDelete, Phase: PendingPhase.Removed } or
                    { Kind: PendingKind.LocalReplace, Phase: PendingPhase.Published }) ||
                 change.FirstRunAcknowledged.Any(item => item.Side != Side.Local ||
                     item.Path != expected.Losing.Expected.Path || item.Id != expected.Losing.Expected.Id)))
                throw new ArgumentException(null, nameof(change));
            foreach (var (side, key) in change.ForgetSelf)
                if (expected.Phase != PendingPhase.Removed || expected.Kind != PendingKind.LocalDelete ||
                    (expected.Saved is { } saved ? key != saved.KeyOn(side) :
                        side != Side.Local || key != PathNames.Key(expected.Losing.Expected.Path)))
                    throw new ArgumentException(null, nameof(change));
            foreach (var entry in change.Record)
                if (expected.Phase != PendingPhase.Published || expected.Kind != PendingKind.LocalReplace ||
                    expected.Stage is null || expected.Source is null || entry.LocalPath != expected.Path ||
                    entry.LocalId != expected.Stage.FileId || entry.LocalSize != expected.Stage.Size ||
                    entry.DropboxId != expected.Source.Id || entry.DropboxContentHash != expected.Stage.ContentHash)
                    throw new ArgumentException(null, nameof(change));
            if (!_pending.TryGetValue(pairId, out var current) || current != expected)
                throw new InvalidOperationException();
            RequirePendingSaved(pairId, expected);
            var raised = ApplyAtomic(pairId, change);
            _pending.Remove(pairId);
            return raised;
        }
    }

    public void SettleLegacyPending(string pairId, string rawToken, StateChange issueOnlyChange)
    {
        lock (_gate)
        {
            RequireIssueOnly(issueOnlyChange);
            throw new NotSupportedException();
        }
    }

    public JournalPosition? ReadJournal(string volumeKey)
    {
        lock (_gate) return _journal.GetValueOrDefault(volumeKey);
    }

    public long? AcquireJournalPage(string volumeKey, JournalPosition? expected, JournalPosition next,
        IReadOnlyList<(string PairId, long Generation, long Epoch, long Revision,
            bool LostInterval)> members,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices)
    {
        ArgumentException.ThrowIfNullOrEmpty(volumeKey);
        lock (_gate)
        {
            if (_journal.GetValueOrDefault(volumeKey) != expected) return null;
            var fences = new Dictionary<string, (long Epoch, long Revision,
                SourceObligation? Obligation)>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                if (!_pairs.ContainsKey(member.PairId) || Generation(member.PairId) != member.Generation ||
                    !_sources.TryGetValue(SourceKey(member.PairId, Side.Local), out var source) ||
                    source.Epoch != member.Epoch || source.Revision != member.Revision)
                    return null;
                fences.Add(member.PairId, (source.Epoch, source.Revision,
                    member.LostInterval ? LostIntervalObligation(member.PairId) : (SourceObligation?)null));
            }
            foreach (var (pairId, notice) in notices)
            {
                if (notice.Side != Side.Local || !fences.TryGetValue(pairId, out var fence))
                    throw new ArgumentException(null, nameof(notices));
                AddWork(pairId, checked(Generation(pairId)), fence.Epoch, Side.Local, volumeKey, notice, null,
                    null, null);
            }
            foreach (var (pairId, fence) in fences)
            {
                if (fence.Obligation is null)
                    continue;
                Advance(pairId, Side.Local, Generation(pairId), fence.Epoch, fence.Revision, null, null,
                    fence.Epoch, checked(fence.Revision + 1), null, null,
                    SourceObligations.Owed(RequireSource(pairId, Side.Local).Obligation, fence.Obligation.Value));
            }
            _journal[volumeKey] = next;
            return _nextWorkId;
        }
    }

    public (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
        ReadSource(string pairId, Side side)
    {
        lock (_gate)
            return _sources.TryGetValue(SourceKey(pairId, side), out var source)
                ? (source.Epoch, source.Provenance, source.Cursor, source.Obligation, source.Revision) : null;
    }

    public bool Obligated(string pairId)
    {
        lock (_gate)
            return !_sources.TryGetValue(SourceKey(pairId, Side.Local), out var local) || local.Obligation is not null ||
                !_sources.TryGetValue(SourceKey(pairId, Side.Dropbox), out var dropbox) || dropbox.Obligation is not null;
    }

    public LiveRefusal ReadLiveRefusal(string pairId)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId)) return LiveRefusal.None;
            if (!_sources.TryGetValue(SourceKey(pairId, Side.Local), out var local) ||
                !_sources.ContainsKey(SourceKey(pairId, Side.Dropbox)) || !FirstRepairDone(pairId) && Obligated(pairId))
                return LiveRefusal.Pair;
            return ReadLocalGap(pairId, local.Epoch) is null && local.Obligation != LostIntervalObligation(pairId)
                ? LiveRefusal.None : LiveRefusal.LocalWrites;
        }
    }

    public long? ReadLocalGap(string pairId, long localEpoch)
    {
        lock (_gate)
            return _sources.TryGetValue(SourceKey(pairId, Side.Local), out var source) &&
                source.Epoch == localEpoch && source.Obligation == SourceObligation.LocalSkippedCoverage
                    ? source.Revision : null;
    }

    public long AcquireDropboxPage(string pairId, long expectedGeneration, long expectedEpoch,
        long expectedRevision, SourceProvenance provenance, string? expectedCursor,
        IEnumerable<ChangeNotice> notices, string cursor)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId) || Generation(pairId) != expectedGeneration ||
                !_sources.TryGetValue(SourceKey(pairId, Side.Dropbox), out var source) ||
                source.Epoch != expectedEpoch || source.Revision != expectedRevision ||
                source.Cursor != expectedCursor || source.Provenance != provenance)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            foreach (var notice in notices)
            {
                if (notice.Side != Side.Dropbox) throw new ArgumentException(null, nameof(notices));
                if (notice.Kind == ChangeKind.Deleted)
                {
                    if (!_deletedDelta.TryGetValue(pairId, out var names)) _deletedDelta[pairId] = names = [];
                    var key = PathNames.DropboxKey(notice.Path);
                    if (!DeletedNameSpent(pairId, key)) names.Add(key);
                }
                AddWork(pairId, expectedGeneration, expectedEpoch, Side.Dropbox, provenance.NamespaceId, notice,
                    null, null, null);
            }
            Advance(pairId, Side.Dropbox, expectedGeneration, expectedEpoch, expectedRevision, provenance,
                expectedCursor, expectedEpoch, expectedRevision, provenance, cursor, source.Obligation);
            return _nextWorkId;
        }
    }

    public void RequireRepairForOwedHistory(string pairId, long expectedGeneration)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            if (DeletedHistoryOwed(pairId) && !Obligated(pairId))
                RequireRepairAfterLoss(pairId, expectedGeneration);
        }
    }

    public void RequireRepairAfterLoss(string pairId, long expectedGeneration)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            var source = ReadSource(pairId, Side.Local) ??
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            Advance(pairId, Side.Local, expectedGeneration, source.Epoch, source.Revision, source.Provenance,
                source.Cursor, source.Epoch, checked(source.Revision + 1), source.Provenance, source.Cursor,
                SourceObligations.Owed(source.Obligation, LostIntervalObligation(pairId)));
        }
    }

    public void AdvanceSource(string pairId, Side side, long expectedGeneration,
        long expectedEpoch, long expectedRevision, SourceProvenance? expectedProvenance, string? expectedCursor,
        long nextEpoch, long nextRevision, SourceProvenance? provenance, string? cursor,
        SourceObligation? obligation)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            if (side == Side.Dropbox)
                nextRevision = Math.Max(nextRevision,
                    nextEpoch != expectedEpoch || cursor != expectedCursor || !Equals(provenance, expectedProvenance)
                        ? checked(expectedRevision + 1) : expectedRevision);
            Advance(pairId, side, expectedGeneration, expectedEpoch, expectedRevision, expectedProvenance,
                expectedCursor, nextEpoch, nextRevision, provenance, cursor, obligation);
            if (nextEpoch == expectedEpoch) return;
            foreach (var candidate in _items.Values.Where(item => item.PairId == pairId).ToArray())
            {
                var work = _work.Single(row => row.WorkId == candidate.Token.WorkId);
                if (work.Side != side) continue;
                if (work.Notice is null && work.Domain == Domain(pairId, side))
                {
                    work.SourceEpoch = nextEpoch;
                    _items[candidate.Token.IssueId] = candidate with
                    { Token = candidate.Token with { SourceEpoch = nextEpoch } };
                }
                else
                {
                    _items.Remove(candidate.Token.IssueId);
                    if (work.Notice is null) _work.Remove(work);
                }
            }
        }
    }

    public WorkSelection? SelectWork(string pairId, long expectedGeneration, PathScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId) || Generation(pairId) != expectedGeneration ||
                ReadLiveRefusal(pairId) == LiveRefusal.Pair)
            {
                return null;
            }
            var pass = _passes.TryGetValue(pairId, out var open) && open.Generation == expectedGeneration
                ? open : new Pass(expectedGeneration, 0, _nextWorkId, scope);
            while (NextCandidate(pairId, pass) is { } candidate)
            {
                pass = pass with { After = candidate.WorkId };
                if (pass.Attempted.Contains(candidate.WorkId)) continue;
                BootstrapRename(pairId, candidate, pass.Through);
                if (Held(pairId, candidate) || Earliest(pairId, candidate, pass.Through).WorkId != candidate.WorkId)
                    continue;
                _passes[pairId] = pass;
                return ToSelection(pairId, candidate, pass.Through);
            }
            return null;
        }
    }

    public bool EndWorkPass(string pairId)
    {
        lock (_gate) return _passes.Remove(pairId, out var pass) && (pass.NetRetired > 0 || pass.Released);
    }

    public IEnumerable<PendingChange> ReadWork(WorkSelection selection)
    {
        List<PendingChange> result = [];
        lock (_gate)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            if (anchor.Notice is null)
            {
                var item = anchor.Subject.Items.FirstOrDefault(member => member.Side == anchor.Side &&
                    PathNames.KeyFor(member.Side, member.Path) == anchor.PathKey) ??
                    throw new InvalidOperationException();
                result.Add(new PendingChange(anchor.WorkId, selection.PairId, selection.PairGeneration,
                    selection.SourceEpoch,
                    new ChangeNotice(anchor.Side, ChangeKind.Changed, item.Path, null, anchor.Identity, null,
                        false, false), null));
                return result;
            }
            foreach (var row in Membership(selection, anchor))
                result.Add(new PendingChange(row.WorkId, selection.PairId, selection.PairGeneration,
                    selection.SourceEpoch, row.Notice!, row.Before) { IsResidual = row.Parent is not null, DiscoveryRequired = row.DiscoveryRequired });
            return result;
        }
    }

    public IEnumerable<WorkSelection> PendingAncestors(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long? coveredThrough = null)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId) || Generation(pairId) != expectedGeneration ||
                !_sources.TryGetValue(SourceKey(pairId, side), out var source))
                return [];
            var through = _nextWorkId;
            var seen = new HashSet<long>();
            var result = new List<WorkSelection>();
            foreach (var key in ancestorKeys)
                foreach (var row in _work.Where(work => !work.IssueOrigin && work.PairId == pairId &&
                             work.PairGeneration == expectedGeneration && work.Side == side &&
                             work.SourceEpoch == source.Epoch &&
                             (work.PathKey == key || work.OldPathKey == key)))
                {
                    if (row.WorkId <= coveredThrough) continue;
                    var owner = ParentOwner(row);
                    if (owner.WorkId <= through && seen.Add(owner.WorkId) &&
                        (coveredThrough is not null || !Held(pairId, owner) && !DeletionBlocked(pairId, owner)))
                        result.Add(ToSelection(pairId, owner, through));
                }
            return result;
        }
    }

    private bool PlainDeletion(WorkRow row)
    {
        var (deleted, restatement) = DeletionGroup(row);
        if (deleted || restatement is null) return deleted;
        var saved = row.Identity is null ? SyncedByKey(row.PairId, row.Side, row.PathKey) :
            SyncedById(row.PairId, row.Side, row.Identity);
        if (saved is not { IsFolder: true } || restatement.Id != saved.IdOn(row.Side)) return false;
        var other = PathNames.Other(row.Side);
        return _work.Any(counterpart => !counterpart.IssueOrigin && counterpart.PairId == row.PairId &&
            counterpart.PairGeneration == row.PairGeneration && counterpart.Side == other &&
            counterpart.SourceEpoch == RequireSource(row.PairId, other).Epoch &&
            counterpart.PathKey == saved.KeyOn(other) && (counterpart.Identity is null || counterpart.Identity == saved.IdOn(other)) &&
            DeletionGroup(counterpart).Deleted);
    }

    private (bool Deleted, ChangeNotice? Restatement) DeletionGroup(WorkRow row)
    {
        var first = Earliest(row.PairId, row, _nextWorkId);
        var group = Membership(ToSelection(row.PairId, first, _nextWorkId), first).ToArray();
        var last = group.LastOrDefault();
        if (last?.Notice is not { } notice || group.Any(member => member.Notice?.LocalContentDirty == true)) return (false, null);
        if (notice.Kind == ChangeKind.Deleted || notice.IdentityDeleted) return (true, null);
        if (notice is { Kind: ChangeKind.Changed, OldPath: null, Observed: { IsFolder: true, Skip: null } })
            return (false, notice);
        var saved = first.Identity is null ? SyncedByKey(row.PairId, first.Side, first.PathKey) :
            SyncedById(row.PairId, first.Side, first.Identity);
        return (notice is { Side: Side.Local, Kind: ChangeKind.Renamed } &&
            _passes.GetValueOrDefault(row.PairId)?.Scope?.LeavesScope(notice.Path, null,
                notice.Observed?.IsFolder ?? last.Before?.IsFolder ?? saved?.IsFolder ?? false,
                (Owner: this, row.PairId), static (state, side, key) => state.Owner.SyncedByKey(state.PairId, side, key)) == true, null);
    }

    private bool DeletionBlocked(string pairId, WorkRow owner)
    {
        if (!PlainDeletion(owner)) return false;
        var saved = owner.Identity is null ? SyncedByKey(pairId, owner.Side, owner.PathKey) :
            SyncedById(pairId, owner.Side, owner.Identity);
        return _work.Any(row => row.PairId == pairId && row.PairGeneration == owner.PairGeneration &&
            row.SourceEpoch == RequireSource(pairId, row.Side).Epoch && !row.IssueOrigin &&
            (PathNames.IsUnderKey(row.PathKey, saved?.KeyOn(row.Side) ?? owner.PathKey) ||
             row.OldPathKey is not null && PathNames.IsUnderKey(row.OldPathKey, saved?.KeyOn(row.Side) ?? owner.PathKey)) &&
            !PlainDeletion(row));
    }

    private List<(WorkRow Row, SyncedEntry? After)> RenameMembers(string pairId, WorkSelection? selection, StateChange change)
    {
        List<(WorkRow, SyncedEntry?)> result = [];
        if (selection is null) return result;
        if (selection.PairId != pairId) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        RequireCurrent(selection);
        var anchor = RequireAnchor(selection);
        var after = anchor.Before;
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
        if (!affected) return result;
        var current = selection.Identity is null ? SyncedByKey(pairId, selection.Side, selection.Key) :
            SyncedById(pairId, selection.Side, selection.Identity);
        if (current != anchor.Before) throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
        result.AddRange(Membership(selection, anchor).Concat(_work.Where(row => PeerOwned(selection, anchor, row, selection.Peer)))
            .Select(row => (row, after)));
        return result;
    }

    public AncestorAnswer LaterAncestorNews(string pairId, long expectedGeneration, Side side,
        IReadOnlyList<string> ancestorKeys, long coveredThrough)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId) || Generation(pairId) != expectedGeneration ||
                !_sources.TryGetValue(SourceKey(pairId, side), out var source)) return new(AncestorNews.None);
            var news = AncestorNews.None;
            string? unbound = null;
            foreach (var key in ancestorKeys)
                foreach (var row in _work.Where(work => !work.IssueOrigin && work.PairId == pairId &&
                    work.PairGeneration == expectedGeneration && work.Side == side && work.SourceEpoch == source.Epoch &&
                    work.WorkId > coveredThrough && (work.PathKey == key || work.OldPathKey == key)))
                {
                    if (row.PathKey != key || row.OldPathKey is not null || row.Parent is not null ||
                        row.ObligationKey is not null || row.IssueId != 0 ||
                        row.Notice is not { Kind: ChangeKind.Changed, OldPath: null, IdentityDeleted: false,
                            Observed: { IsFolder: true, Skip: null } observed }) return new(AncestorNews.Changed);
                    var saved = SyncedByKey(pairId, side, key);
                    if (saved is null) unbound ??= key;
                    else if (!saved.IsFolder || (side == Side.Local ? saved.LocalId : saved.DropboxId) != observed.Id)
                        return new(AncestorNews.Changed);
                    news = AncestorNews.Restated;
                }
            return unbound is null ? new(news) : new(AncestorNews.Unbound, unbound);
        }
    }

    public Func<Side, string, string?, bool> CreateSeparateWorkProbe(WorkSelection selection)
    {
        lock (_gate)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            var other = PathNames.Other(selection.Side);
            var otherEpoch = RequireSource(selection.PairId, other).Epoch;
            var otherDomain = Domain(selection.PairId, other);
            return (side, key, identity) =>
            {
                lock (_gate)
                {
                    var epoch = side == selection.Side ? selection.SourceEpoch : otherEpoch;
                    var domain = side == selection.Side ? selection.Domain : otherDomain;
                    return _work.Any(row => !row.IssueOrigin && row.PairId == selection.PairId &&
                        row.PairGeneration == selection.PairGeneration && row.SourceEpoch == epoch &&
                        row.Side == side && row.Domain == domain &&
                        (row.PathKey == key || row.OldPathKey == key || identity is not null && row.Identity == identity) && !Owned(selection, anchor, row));
                }
            };
        }
    }

    public Func<SyncedEntry, long, bool> CreateLocalEchoProbe(WorkSelection selection)
    {
        string[] paths;
        lock (_gate)
        {
            var pass = _passes.GetValueOrDefault(selection.PairId);
            paths = pass?.LocalEchoResponsibility ?? Issues(selection.PairId).Where(issue => !issue.Dismissed || _items.ContainsKey(issue.Id))
                .SelectMany(issue => issue.Subject.Items.Select(item => item.Path).Prepend(issue.Path))
                .Concat(_folders.Where(entry => entry.Key.PairId == selection.PairId).Select(entry => entry.Value.State)
                    .Where(folder => folder.Active || folder.Phase is FolderRemovalPhase.Unknown or FolderRemovalPhase.Retained &&
                        folder.RetirementDisposition is null)
                    .SelectMany(folder => new[] { folder.RootEntry.Path, folder.SavedRoot?.LocalPath, folder.SavedRoot?.DropboxPath })
                    .OfType<string>()).Select(PathNames.Key).ToArray();
            if (pass is not null) pass.LocalEchoResponsibility = paths;
        }
        return (saved, through) => !paths.Any(key => PathNames.ResponsibilityOverlaps(saved.LocalKey, key, saved.IsFolder) ||
            PathNames.ResponsibilityOverlaps(saved.DropboxKey, key, saved.IsFolder)) && CanSettleLocalEcho(selection, saved, through);
    }

    private bool CanSettleLocalEcho(WorkSelection selection, SyncedEntry saved, long throughWorkId)
    {
        lock (_gate)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            var source = ReadSource(selection.PairId, Side.Dropbox);
            if (selection.Side != Side.Local || Obligated(selection.PairId) ||
                source is not { Provenance: not null, Cursor: not null } remote || string.IsNullOrEmpty(remote.Cursor) ||
                anchor.Parent is not null || anchor.ObligationKey is not null || anchor.IssueId != 0 ||
                _work.Any(row => row.Parent == anchor.WorkId || row.PairId == selection.PairId &&
                    row.PairGeneration == selection.PairGeneration && row.Side == Side.Dropbox &&
                    row.SourceEpoch == remote.Epoch && row.Domain == Domain(selection.PairId, Side.Dropbox) &&
                    row.PathKey == saved.DropboxKey && row.WorkId <= throughWorkId && !row.IssueOrigin)) return false;
            bool Touches(string? path) => path is not null &&
                (PathNames.ResponsibilityOverlaps(saved.LocalKey, PathNames.Key(path), saved.IsFolder) ||
                 PathNames.ResponsibilityOverlaps(saved.DropboxKey, PathNames.Key(path), saved.IsFolder));
            return !(_pending.TryGetValue(selection.PairId, out var pending) &&
                (Touches(pending.Path) || Touches(pending.Saved?.LocalPath) || Touches(pending.Saved?.DropboxPath)));
        }
    }

    public void ClaimSelectionPeer(WorkSelection selection, SelectionPeer peer)
    {
        lock (_gate)
        {
            if (_passes.GetValueOrDefault(selection.PairId)?.Claims is not { } claims ||
                !claims.TryGetValue(selection.AnchorWorkId, out var claimed) || claimed != selection with { Peer = claimed.Peer })
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            claims[selection.AnchorWorkId] = claimed with { Peer = peer };
        }
    }

    public void EndSelection(WorkSelection selection)
    {
        lock (_gate)
        {
            if (_passes.GetValueOrDefault(selection.PairId) is not { } pass) return;
            pass.Claims.Remove(selection.AnchorWorkId);
            if (pass.Scope is null && pass.Claims.Count == 0) _passes.Remove(selection.PairId);
        }
    }
    public void RequireDiscovery(WorkSelection selection, long workId)
    {
        lock (_gate)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            var row = Membership(selection, anchor).SingleOrDefault(row => row.WorkId == workId) ??
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            row.DiscoveryRequired = true;
        }
    }

    public void BeginSelection(WorkSelection selection)
    {
        lock (_gate)
        {
            if (_passes.TryGetValue(selection.PairId, out var pass) && pass.Attempted.Contains(selection.AnchorWorkId))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            var before = selection.Identity is null ? SyncedByKey(selection.PairId, selection.Side, selection.Key) :
                SyncedById(selection.PairId, selection.Side, selection.Identity);
            foreach (var row in Membership(selection, anchor).ToArray()) row.Before = before;
            if (pass is null)
                _passes[selection.PairId] = pass = new Pass(selection.PairGeneration, 0, selection.ThroughWorkId, null);
            if (pass.Scope is not null) pass.Attempted.Add(selection.AnchorWorkId);
            pass.Claims[selection.AnchorWorkId] = selection;
        }
    }

    private static bool NeedsOwnSelection(WorkRow work, ChangeNotice? notice = null, bool residual = false) =>
        work.RenameContinuation is not null || work.DiscoveryRequired || notice?.Observed is { IsFolder: true } || residual && notice?.LocalContentDirty == true;

    public bool CompleteWork(WorkSelection selection, SelectionPeer? peer)
    {
        lock (_gate)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            if (anchor.IssueId != 0 || anchor.Parent is { } owner && WorkExists(anchor.PairId, owner)) return false;
            var members = Membership(selection, anchor).Select(row => row.WorkId).ToHashSet();
            var retired = _work.RemoveAll(row => members.Contains(row.WorkId) && row.RenameContinuation is null);
            if (peer is not null)
            {
                var peers = _work.Where(row => PeerOwned(selection, anchor, row, peer) &&
                    !NeedsOwnSelection(row, row.Notice,
                        anchor.Before is not null && _work.Any(child => child.Parent == anchor.WorkId)))
                    .Select(row => row.WorkId).ToHashSet();
                retired += _work.RemoveAll(row => peers.Contains(row.WorkId));
            }
            if (_passes.TryGetValue(selection.PairId, out var pass)) pass.NetRetired += retired;
            return true;
        }
    }

    public bool AttachIssueWork(WorkSelection parent, Issue exactIssue,
        IEnumerable<(ChangeNotice Notice, bool IsFolder, SyncedEntry? Before)> unfinished)
    {
        if (exactIssue.Kind == IssueKind.Info) return false;
        lock (_gate)
        {
            RequireCurrent(parent);
            RequireIssue(parent.PairId, exactIssue);
            var episode = IssuePolicy.IsLocalAccessRetryCause(exactIssue.Cause)
                ? _work.SingleOrDefault(row => row.WorkId == _items.GetValueOrDefault(exactIssue.Id)?.Token.WorkId) ??
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged) : null;
            var anchor = RequireAnchor(parent);
            foreach (var (notice, isFolder, before) in unfinished)
            {
                if (notice.Side != parent.Side) throw new ArgumentException(null, nameof(unfinished));
                if (episode is not null && (!notice.LocalContentDirty || episode.Notice is { } priorEpisode &&
                    Massdex.SyncShark.Engine.Storage.ChangeTable.MergeContent(priorEpisode, notice) is { } mergedEpisode &&
                    (mergedEpisode.LocalContentDirty, mergedEpisode.Usn) ==
                    (priorEpisode.LocalContentDirty, priorEpisode.Usn))) continue;
                var role = isFolder ? ObligationRole.Subtree : ObligationRole.Content;
                var owner = episode?.WorkId ?? parent.AnchorWorkId;
                var key = RegistrationKey(parent, episode is null ? notice : notice with { OldPath = null },
                    role, episode is null ? before : null);
                var stored = episode is not null ? null : _work.FirstOrDefault(row => !row.IssueOrigin && row.IssueId == exactIssue.Id &&
                    row.Side == notice.Side && row.PathKey == PathNames.KeyFor(notice.Side, notice.Path) &&
                    row.Identity == notice.Id && row.PairId == parent.PairId);
                stored ??= _work.FirstOrDefault(row => row.Parent == owner && row.ObligationKey == key);
                if (stored is null && episode is not null && anchor.WorkId > owner && notice.Id == anchor.Identity &&
                    PathNames.KeyFor(notice.Side, notice.Path) == anchor.PathKey) stored = anchor;
                if (stored is null)
                {
                    stored = AddWork(parent.PairId, parent.PairGeneration, parent.SourceEpoch, notice.Side, parent.Domain,
                        notice, before, owner, key);
                    if (_passes.TryGetValue(parent.PairId, out var pass)) pass.NetRetired--;
                }
                else
                {
                    if (stored.PairGeneration != parent.PairGeneration || stored.SourceEpoch != parent.SourceEpoch ||
                        episode is not null && stored.Parent != owner)
                    {
                        stored.PairGeneration = parent.PairGeneration;
                        stored.SourceEpoch = parent.SourceEpoch;
                        stored.Domain = parent.Domain;
                        if (episode is not null)
                        {
                            stored.Parent = owner;
                            stored.ObligationKey = key;
                        }
                    }
                    stored.Notice = Massdex.SyncShark.Engine.Storage.ChangeTable.MergeContent(stored.Notice!, notice);
                }
                if (episode is null) stored.IssueId = exactIssue.Id;
            }
            return true;
        }
    }

    public (bool Dirty, JournalPosition? SourceBoundary, long EvidenceVersion) ReadLocalDirty(
        string pairId, long expectedGeneration, long localSourceEpoch, string nativeDomain,
        string? fullIdentity, string? currentPathKey, string? recordedPathKey)
    {
        lock (_gate)
        {
            if (!_pairs.ContainsKey(pairId) || Generation(pairId) != expectedGeneration ||
                !_sources.TryGetValue(SourceKey(pairId, Side.Local), out var source) ||
                source.Epoch != localSourceEpoch)
                return (false, null, 0);
            var dirty = false;
            long version = 0;
            foreach (var row in _work.Where(work => !work.IssueOrigin && work.PairId == pairId &&
                         work.PairGeneration == expectedGeneration && work.Side == Side.Local &&
                         work.SourceEpoch == localSourceEpoch && work.Domain == nativeDomain))
            {
                if (fullIdentity is not null && row.Identity is not null && row.Identity != fullIdentity) continue;
                var keys = new[] { currentPathKey, recordedPathKey }.Where(key => key is not null).ToArray();
                var matches = fullIdentity is not null && row.Identity == fullIdentity ||
                    keys.Any(key => row.PathKey == key || PathNames.IsUnderKey(row.PathKey, key!) ||
                        row.OldPathKey is not null && (row.OldPathKey == key ||
                            PathNames.IsUnderKey(row.OldPathKey, key!)));
                if (!matches) continue;
                dirty |= row.Identity is null || row.Notice?.LocalContentDirty == true;
                version = Math.Max(version, row.Acquisition);
            }
            return (dirty, _journal.GetValueOrDefault(nativeDomain), version);
        }
    }

    public PendingRead ReadPendingForFolderRecovery(string pairId, FolderRemovalState operation)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue((pairId, operation.OperationId), out var stored) ||
                stored.State != operation || !operation.Active)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            return _pending.TryGetValue(pairId, out var effect) ? new CurrentPending(effect) : new NoPending();
        }
    }

    public void ValidatePendingForFolderRecovery(string pairId, FolderRemovalState operation,
        PendingRead expectedPending)
    {
        lock (_gate)
        {
            if (!_folders.TryGetValue((pairId, operation.OperationId), out var stored) ||
                stored.State != operation || !operation.Active ||
                (Pending(pairId) != expectedPending))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        }
    }

    public LiveCoverage CaptureLiveCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox)
    {
        lock (_gate)
        {
            RequireCoverage(selection, local, dropbox);
            if (local.ThroughWorkId > _nextWorkId || dropbox.ThroughWorkId > _nextWorkId ||
                RequireSource(selection.PairId, Side.Dropbox).Cursor != dropbox.Cursor ||
                Domain(selection.PairId, Side.Local) is not { } volume ||
                _journal.GetValueOrDefault(volume) is not { } position ||
                position.JournalId != local.Checkpoint.JournalId ||
                position.NextUsn < checked((ulong)local.Checkpoint.NextUsn))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            return new LiveCoverage(local, dropbox);
        }
    }

    public void ValidateLiveCoverage(WorkSelection selection, LiveCoverage coverage,
        IReadOnlyList<LiveEffectScope> footprint)
    {
        lock (_gate)
        {
            RequireCoverage(selection, coverage.Local, coverage.Dropbox);
            if (selection.SourceEpoch != (selection.Side == Side.Local
                    ? coverage.Local.SourceEpoch : coverage.Dropbox.SourceEpoch))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var anchor = RequireAnchor(selection);
            var definition = _pairs[selection.PairId];
            if (!footprint.Any(scope => scope.Side == selection.Side &&
                    (Massdex.SyncShark.Engine.Storage.ChangeTable.ScopeKey(definition, scope) == selection.Key ||
                     scope.IncludesDescendants && PathNames.IsUnderKey(selection.Key,
                         Massdex.SyncShark.Engine.Storage.ChangeTable.ScopeKey(definition, scope)))))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.FootprintChanged);
            var atPath = SyncedByKey(selection.PairId, selection.Side, selection.Key);
            var baseline = selection.Identity is null
                ? atPath : SyncedById(selection.PairId, selection.Side, selection.Identity);
            if (baseline != anchor.Before || baseline is not null && baseline.KeyOn(selection.Side) != selection.Key &&
                atPath is not null && atPath != baseline)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
            var held = HeldKinds(selection.PairId);
            foreach (var scope in footprint)
            {
                var epoch = scope.Side == Side.Local ? coverage.Local.SourceEpoch : coverage.Dropbox.SourceEpoch;
                var key = Massdex.SyncShark.Engine.Storage.ChangeTable.ScopeKey(definition, scope);
                if (held.Keys.Any(heldKey => PathNames.IsSelfOrUnderKey(key, heldKey) ||
                        scope.IncludesDescendants && PathNames.IsUnderKey(heldKey, key)) ||
                    EpisodeAtSelection(selection, scope.Side, key))
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
                foreach (var row in _work.Where(work => !work.IssueOrigin && work.PairId == selection.PairId &&
                             work.PairGeneration == selection.PairGeneration && work.Side == scope.Side &&
                             work.SourceEpoch == epoch &&
                             (work.PathKey == key || work.OldPathKey == key ||
                                 scope.IncludesDescendants && !scope.ExactEvidence && (PathNames.IsUnderKey(work.PathKey, key) ||
                                     work.OldPathKey is not null && PathNames.IsUnderKey(work.OldPathKey, key))) &&
                             !Owned(selection, anchor, work) &&
                             (work.Acquisition > (scope.Side == selection.Side ? selection.ThroughWorkId :
                                 coverage.Peer?.ThroughWorkId ?? selection.ThroughWorkId) ||
                              scope.IncludesDescendants && !scope.ExactEvidence &&
                              (PathNames.IsUnderKey(work.PathKey, key) || work.OldPathKey is not null &&
                                  PathNames.IsUnderKey(work.OldPathKey, key)) &&
                              (!PlainDeletion(anchor) || !PlainDeletion(work)))))
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            }
            if (LaterIdentityEvidence(selection, anchor))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
            if (_pending.TryGetValue(selection.PairId, out var pending) &&
                Covers(footprint, Side.Local, PathNames.Key(pending.Path)))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
        }
    }

    /// <summary>Same-identity work after the selection's watermark that the selection does not own.</summary>
    private bool LaterIdentityEvidence(WorkSelection selection, WorkRow anchor)
    {
        if (selection.Identity is null) return false;
        var after = selection.ThroughWorkId;
        while (_work.Where(row => !row.IssueOrigin && row.PairId == selection.PairId &&
                   row.Side == selection.Side && row.Domain == selection.Domain &&
                   row.Identity == selection.Identity && row.WorkId > after)
               .OrderBy(row => row.WorkId).FirstOrDefault() is { } row)
        {
            if (row.Acquisition > selection.ThroughWorkId && !Owned(selection, anchor, row)) return true;
            after = row.WorkId;
        }
        return false;
    }

    private bool EpisodeAtSelection(WorkSelection selection, Side side, string key) =>
        _items.Values.Any(candidate => candidate.PairId == selection.PairId &&
            _work.Any(row => row.WorkId == candidate.Token.WorkId && row.Side == side && row.PathKey == key) &&
            (candidate.Token.WorkId > selection.AnchorWorkId ||
             !candidate.Token.Subject.Items.Any(item => item.Side == selection.Side &&
                (selection.Identity is not null ? item.Id == selection.Identity :
                    PathNames.KeyFor(item.Side, item.Path) == selection.Key))));

    private static bool Covers(IReadOnlyList<LiveEffectScope> footprint, Side side, string key) =>
        footprint.Any(scope => scope.Side == side &&
            PathNames.KeyFor(side, scope.NativePath) is var root &&
            (root == key || scope.IncludesDescendants && PathNames.IsUnderKey(key, root)));

    private void RequireExpectedIssue(string pairId, Issue? expected)
    {
        if (expected is not null && IssueById(pairId, expected.Id)?.Matches(expected) != true)
            throw StaleIssue(expected.Id);
    }

    private static EngineRequestException StaleIssue(long id) => new(TextKey.ResolveStale,
        (TextArg.Id, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    public Massdex.SyncShark.V2.FailureRetryOwner.State? ReadPrerequisiteRetry(string pairId, Issue? expected = null)
    {
        lock (_gate)
        {
            RequireExpectedIssue(pairId, expected);
            return _retries.TryGetValue(pairId, out var state)
                ? state : (Massdex.SyncShark.V2.FailureRetryOwner.State?)null;
        }
    }

    public IEnumerable<(long PairRow, string PairId, long Generation,
        Massdex.SyncShark.V2.FailureRetryOwner.State State)> ReadPrerequisiteRetries(
        long? afterTicks, long? afterPairRow, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        var ticks = afterTicks ?? StateSchema.BeforeFirstTicks;
        var row = afterPairRow ?? StateSchema.BeforeFirstPairRow;
        lock (_gate)
            return _retries.OrderBy(entry => entry.Value.RetryAfter.UtcTicks)
                .ThenBy(entry => PairRow(entry.Key))
                .Select(entry => (PairRow(entry.Key), entry.Key, Generation(entry.Key), entry.Value))
                .Where(entry => entry.Item4.RetryAfter.UtcTicks > ticks ||
                    entry.Item4.RetryAfter.UtcTicks == ticks && entry.Item1 > row)
                .Take(pageSize).ToList();
    }

    public void RecordPrerequisiteFailure(string pairId, long expectedGeneration,
        long localEpoch, long dropboxEpoch, long localRevision, long dropboxRevision,
        Massdex.SyncShark.V2.FailureRetryOwner.State? expected,
        Massdex.SyncShark.V2.FailureRetryOwner.State next, StateChange failureNotice)
    {
        lock (_gate)
        {
            RequireRetryNotice(failureNotice);
            RequireGeneration(pairId, expectedGeneration);
            var local = RequireSource(pairId, Side.Local);
            var dropbox = RequireSource(pairId, Side.Dropbox);
            if (local.Epoch != localEpoch || local.Revision != localRevision ||
                dropbox.Epoch != dropboxEpoch || dropbox.Revision != dropboxRevision ||
                _retries.GetValueOrDefault(pairId) != expected && _retries.ContainsKey(pairId) ||
                !_retries.ContainsKey(pairId) && expected is not null)
                throw new InvalidOperationException();
            ApplyAtomic(pairId, failureNotice);
            _retries[pairId] = next;
        }
    }

    public void CompleteRecomposition(string pairId, long expectedGeneration, long localEpoch, long dropboxEpoch,
        long localRevision, long dropboxRevision, Massdex.SyncShark.V2.FailureRetryOwner.State expected)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            var local = RequireSource(pairId, Side.Local);
            var dropbox = RequireSource(pairId, Side.Dropbox);
            if (local.Epoch != localEpoch || local.Revision != localRevision || dropbox.Epoch != dropboxEpoch ||
                dropbox.Revision != dropboxRevision || ReadPrerequisiteRetry(pairId) != expected || Obligated(pairId))
                throw new InvalidOperationException();
            var notice = RetryNotice(pairId) ?? throw new InvalidOperationException();
            _retries.Remove(pairId);
            CloseFinishedRetryNotice(pairId, notice, expectedGeneration);
        }
    }

    public Issue RecordPrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        StateChange failureNotice)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_gate)
        {
            RequireRetryNotice(failureNotice);
            RequireGeneration(pairId, expectedGeneration);
            ApplyAtomic(pairId, failureNotice);
            var notice = RetryNotice(pairId) ?? throw new InvalidOperationException();
            _pageWaits[operation] = (pairId, expectedGeneration, notice);
            return notice;
        }
    }

    public void CompletePrerequisitePageWait(string pairId, long expectedGeneration, object operation,
        Issue expectedNotice)
    {
        lock (_gate)
        {
            if (!_pageWaits.TryGetValue(operation, out var wait) || wait.PairId != pairId ||
                wait.Generation != expectedGeneration || wait.Notice != expectedNotice)
                throw new InvalidOperationException();
            RequireGeneration(pairId, expectedGeneration);
            if (!_pageWaits.Any(other => !ReferenceEquals(other.Key, operation) &&
                    other.Value.PairId == pairId && other.Value.Notice.Id == expectedNotice.Id) &&
                !_retries.ContainsKey(pairId))
                CloseRetryProjection(pairId, expectedNotice.Id);
            _pageWaits.Remove(operation);
        }
    }

    public ItemRetryCandidate RecordInUseFailure(string pairId, long expectedGeneration, long sourceEpoch,
        string path, IssueSubject subject, TextKey cause, WorkSelection? selection,
        ItemRetryDispatch dispatch, PendingEffect? expectedPending, ItemRetryToken? expectedEpisode,
        Guid registeredAttemptId)
    {
        lock (_gate)
        {
            if (!IssuePolicy.IsLocalAccessRetryCause(cause) || registeredAttemptId == Guid.Empty ||
                path.Length == 0)
                throw new ArgumentException(null, nameof(cause));
            RequireGeneration(pairId, expectedGeneration);
            if (dispatch == ItemRetryDispatch.LocalRecovery &&
                (expectedPending is null || !_pending.TryGetValue(pairId, out var pending) ||
                 pending != expectedPending || expectedPending.Phase is PendingPhase.Unknown or PendingPhase.Retained))
                throw new InvalidOperationException();
            if (expectedEpisode is not null)
            {
                var claimed = new ItemRetryCandidate(expectedEpisode, pairId, dispatch,
                    expectedPending?.OperationId, expectedPending?.Generation);
                if (!TryClaimLocked(claimed, registeredAttemptId)) throw new InvalidOperationException();
                var existingIssue = IssueRows(pairId).FirstOrDefault(issue => issue.Id == expectedEpisode.IssueId) ??
                    throw new InvalidOperationException();
                if (existingIssue.Cause != cause)
                    Reclassify(existingIssue, cause);
            }
            var issues = IssueRows(pairId);
            var issueId = Raise(pairId, issues, IssueKind.Failure, cause, path, subject, DateTimeOffset.UtcNow);
            var issue = issues.First(stored => stored.Id == issueId);
            if (issue.Cause != cause || !issue.Subject.HasSameItems(subject)) throw new InvalidOperationException();
            var existing = _items.GetValueOrDefault(issue.Id);
            if (expectedEpisode is null ? existing is not null : existing is null ||
                existing.Token with { Subject = expectedEpisode!.Subject } != expectedEpisode ||
                !existing.Token.Subject.HasSameItems(expectedEpisode.Subject))
                throw new InvalidOperationException();
            var work = existing?.Token.WorkId ?? EnsureIssueWork(pairId, expectedGeneration, sourceEpoch, issue.Id, path, issue.Subject,
                selection, dispatch, expectedPending);
            var result = existing ?? new ItemRetryCandidate(new ItemRetryToken(issue.Id,
                Guid.NewGuid().ToString(), 0, work, expectedGeneration, sourceEpoch, issue.Subject), pairId,
                dispatch, expectedPending?.OperationId, expectedPending?.Generation);
            if (existing is null) _items[issue.Id] = result;
            else if (!TryClaimLocked(result, registeredAttemptId)) throw new InvalidOperationException();
            return result;
        }
    }

    public bool TryClaimItemRetry(ItemRetryCandidate expected, Guid registeredAttemptId)
    {
        if (registeredAttemptId == Guid.Empty) throw new ArgumentException(null, nameof(registeredAttemptId));
        lock (_gate) return TryClaimLocked(expected, registeredAttemptId);
    }

    public void CompleteItemAttempt(ItemRetryCandidate expected, Guid registeredAttemptId, StateChange change,
        PendingEffect? completedPending, WorkSelection? selection, Action<Issue>? committedIssue = null)
    {
        List<Issue> raised;
        if (registeredAttemptId == Guid.Empty) throw new ArgumentException(null, nameof(registeredAttemptId));
        lock (_gate)
        {
            RequireGeneration(expected.PairId, expected.Token.PairGeneration);
            var advance = RenameMembers(expected.PairId, selection, change);
            if (completedPending is not null)
            {
                if (change.Raise.Any(issue => issue.Kind != IssueKind.Info) ||
                    completedPending is not ({ Kind: PendingKind.LocalDelete, Phase: PendingPhase.Removed } or
                        { Kind: PendingKind.LocalReplace, Phase: PendingPhase.Published }) ||
                    !expected.Token.Subject.Items.Contains(new IssueItem(Side.Local,
                        completedPending.Losing.Expected.Path, completedPending.Losing.Expected.Id)))
                    throw new ArgumentException(null, nameof(completedPending));
                if (!_pending.TryGetValue(expected.PairId, out var current) || current != completedPending)
                    throw new InvalidOperationException();
                if (expected.Dispatch == ItemRetryDispatch.LocalRecovery)
                {
                    if (expected.PendingOperationId != completedPending.OperationId ||
                        expected.PendingGeneration is not { } generation || generation > completedPending.Generation)
                        throw new InvalidOperationException();
                    expected = expected with { PendingGeneration = completedPending.Generation };
                }
                else if (expected.Dispatch != ItemRetryDispatch.OrdinaryItem)
                    throw new ArgumentException(null, nameof(completedPending));
            }
            if (!TryClaimLocked(expected, registeredAttemptId)) throw new InvalidOperationException();
            var failure = change.Raise.Any(issue => issue.Kind == IssueKind.Failure &&
                IssuePolicy.IsLocalAccessRetryCause(issue.Cause));
            if (failure)
            {
                RequireIssueOnly(change);
                var reported = change.Raise.Single(issue => issue.Kind == IssueKind.Failure &&
                    IssuePolicy.IsLocalAccessRetryCause(issue.Cause) &&
                    issue.Subject.HasSameItems(expected.Token.Subject));
                var issue = IssueRows(expected.PairId).FirstOrDefault(stored =>
                    stored.Id == expected.Token.IssueId) ?? throw new InvalidOperationException();
                Reclassify(issue, reported.Cause);
                _items[issue.Id] = expected with
                {
                    Token = expected.Token with { Revision = checked(expected.Token.Revision + 1) }
                };
                raised = ApplyAtomic(expected.PairId, change);
            }
            else
            {
                if (completedPending is null && expected.Dispatch == ItemRetryDispatch.LocalRecovery &&
                    change.Raise.All(issue => issue.Kind == IssueKind.Info))
                    throw new ArgumentException(null, nameof(completedPending));
                var retained = _items[expected.Token.IssueId];
                _items.Remove(expected.Token.IssueId);
                try
                {
                    raised = completedPending is not null
                        ? SettlePendingCore(expected.PairId, completedPending, change)
                        : ApplyAtomic(expected.PairId, change);
                    foreach (var (row, after) in advance) row.Before = after;
                }
                catch
                {
                    _items[expected.Token.IssueId] = retained;
                    throw;
                }
                var sourceLess = _work.Any(row => row.WorkId == expected.Token.WorkId && row.IssueOrigin);
                if (sourceLess || selection is null && change.Raise.All(issue => issue.Kind == IssueKind.Info) &&
                    change.Close.Any(closure => closure.Kinds.Contains(IssueKind.Failure) &&
                        expected.Token.Subject.Items.Any(item => item.Path == closure.Path)))
                {
                    _work.RemoveAll(row => row.WorkId == expected.Token.WorkId && (sourceLess || !NeedsOwnSelection(row)));
                    if (sourceLess && _passes.TryGetValue(expected.PairId, out var pass)) pass.NetRetired++;
                }
            }
        }
        if (committedIssue is not null)
            foreach (var issue in raised) committedIssue(issue);
    }

    public IReadOnlyDictionary<long, ItemRetryCandidate?> ReadItemRetryState(string pairId,
        IReadOnlyList<long> issueIds, Issue? expected = null)
    {
        lock (_gate)
        {
            RequireExpectedIssue(pairId, expected);
            return issueIds.Distinct().ToDictionary(id => id, id => _items.GetValueOrDefault(id));
        }
    }

    public FolderRemovalItem InsertFolderMember(string pairId, string operationId, long expectedGeneration,
        FolderRemovalItem.ProvenanceKind provenance, string path, Entry? observed, Entry? selected)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (provenance is not (FolderRemovalItem.ProvenanceKind.Unexpected or FolderRemovalItem.ProvenanceKind.BlockingParent) ||
            observed is not null && observed.Path != path || selected is not null &&
            (selected.Path != path || selected.IsFolder || selected.Size < 0 || selected.Skip is not null ||
                string.IsNullOrEmpty(selected.Id) || string.IsNullOrEmpty(selected.Revision) ||
                string.IsNullOrEmpty(selected.ContentHash)))
            throw new ArgumentException(null, nameof(provenance));
        lock (_gate)
        {
            var operation = RequireFolder(pairId, operationId);
            if (!operation.State.Active || operation.State.Generation != expectedGeneration)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
            var key = PathNames.Key(path);
            var root = PathNames.Key(operation.State.RootEntry.Path);
            if (!PathNames.IsSelfOrUnderKey(key, root) &&
                !(provenance == FolderRemovalItem.ProvenanceKind.BlockingParent && PathNames.IsUnderKey(root, key)))
                throw new ArgumentException(null, nameof(path));
            var existing = operation.Items.FirstOrDefault(item => item.Provenance == provenance && PathNames.Key(item.Path) == key);
            if (existing is not null)
            {
                if (existing.Path != path || existing.Expected != observed || selected is not null && existing.Selected != selected)
                    throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
                return existing;
            }
            var ordinal = checked(operation.Items.Max(item => item.Ordinal) + 1);
            var result = new FolderRemovalItem(operationId, ordinal, 0, provenance, path,
                PathNames.KeyFor(operation.State.DeletingSide, path), observed, null, FolderVerdict.Survives,
                false, FolderRemovalItem.FollowUpStatus.None, false, FolderEffectKind.None,
                FolderRemovalItem.EffectStatus.None, 0, null, null, null,
                selected is null ? FolderRemovalItem.RecoveryPhase.Planned : FolderRemovalItem.RecoveryPhase.RecoverySelected,
                selected, null, null, null, null);
            operation.Items.Add(result);
            return result;
        }
    }

    public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct, WorkSelection renameWork)
    {
        lock (_gate)
        {
            if (renameWork.PairId != pairId || ReadRenameContinuation(renameWork)?.Destination != plan.Folder.Synced)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.BaselineChanged);
            var operation = BeginFolderRemoval(pairId, plan, pageSize, ct);
            _folders[(pairId, operation)].RenameWork = renameWork.AnchorWorkId;
            return operation;
        }
    }

    public string? RenameRemoval(WorkSelection selection)
    {
        lock (_gate)
        {
            RequireCurrent(selection);
            RequireAnchor(selection);
            return _folders.Where(item => item.Key.PairId == selection.PairId && item.Value.RenameWork == selection.AnchorWorkId)
                .Select(item => item.Key.OperationId).SingleOrDefault();
        }
    }

    public long? RenameRemovalWork(string pairId, string operationId)
    {
        lock (_gate) return _folders[(pairId, operationId)].RenameWork;
    }

    public string BeginFolderRemoval(string pairId, FolderPlan plan, int pageSize, CancellationToken ct)
    {
        lock (_gate)
        {
            var root = plan.Folder.EntryOn(plan.DeletingFrom) ?? throw new ArgumentException(null, nameof(plan));
            if (!root.IsFolder || root.Path.Length == 0) throw new ArgumentException(null, nameof(plan));
            if (!_pairs.ContainsKey(pairId)) throw new InvalidOperationException();
            var operationId = Guid.NewGuid().ToString();
            var operation = new FolderOperation
            {
                State = new FolderRemovalState(operationId, 0, FolderRemovalPhase.Prepared, plan.DeletingFrom,
                    root, plan.Folder.EntryOn(plan.DeletingFrom == Side.Local ? Side.Dropbox : Side.Local),
                    plan.Folder.Synced, plan.Folder.Context, null, true, null, null)
            };
            AddItem(operation, 0, plan.Folder.Key, root.Path, root, plan.Folder.Synced,
                plan.DeleteWhole ? FolderVerdict.Removed : FolderVerdict.Survives, false, plan.DeleteWhole);
            var previous = 0L;
            foreach (var entry in plan.FrozenEvidence(pageSize, ct))
            {
                if (entry.Ordinal <= previous) throw new InvalidOperationException();
                previous = entry.Ordinal;
                AddItem(operation, entry.Ordinal, entry.Key, entry.Entry?.Path ??
                        entry.Saved?.PathOn(plan.DeletingFrom) ?? throw new InvalidDataException(),
                    entry.Entry, entry.Saved, entry.Verdict, entry.FollowUp,
                    !plan.DeleteWhole && entry.SelectedBranch);
            }
            _folders.Add((pairId, operationId), operation);
            return operationId;
        }
    }

    public FolderRemovalState FolderRemoval(string pairId, string operationId)
    {
        lock (_gate) return RequireFolder(pairId, operationId).State;
    }

    public IEnumerable<FolderRemovalState> ActiveFolderRemovals(string pairId, CancellationToken ct)
    {
        List<FolderRemovalState> result = [];
        lock (_gate)
            result.AddRange(_folders.Where(entry => entry.Key.PairId == pairId && entry.Value.State.Active)
                .OrderBy(entry => entry.Key.OperationId, StringComparer.Ordinal).Select(entry => entry.Value.State));
        foreach (var operation in result)
        {
            ct.ThrowIfCancellationRequested();
            yield return operation;
        }
    }

    public IEnumerable<FolderRemovalEffect> ReadyFolderEffects(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_gate)
        {
            var operation = RequireFolder(pairId, operationId);
            return operation.Items
                .Where(item => !item.IsTerminal && item.EffectState == FolderRemovalItem.EffectStatus.Ready &&
                    item.Ordinal > afterOrdinal)
                .OrderByDescending(item => PathNames.Depth(item.PolicyKey))
                .ThenBy(item => item.PolicyKey, StringComparer.Ordinal)
                .ThenBy(item => item.Ordinal)
                .Take(pageSize)
                .Select(item => new FolderRemovalEffect(operationId, item.Ordinal, item.EffectGeneration,
                    item.EffectKind, item.Expected))
                .ToList();
        }
    }

    public IEnumerable<FolderRemovalItem> PendingFolderFollowUps(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_gate)
            return RequireFolder(pairId, operationId).Items
                .Where(item => item.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending &&
                    item.Ordinal > afterOrdinal)
                .OrderBy(item => item.Ordinal).Take(pageSize).ToList();
    }

    public void CompleteFolderFollowUp(string pairId, string operationId, long ordinal, long expectedGeneration)
    {
        lock (_gate)
        {
            var operation = RequireFolder(pairId, operationId);
            if (!operation.State.Active) throw new InvalidOperationException();
            var index = operation.Items.FindIndex(item => item.Ordinal == ordinal &&
                item.Generation == expectedGeneration &&
                item.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending);
            if (index < 0) throw new InvalidOperationException();
            operation.Items[index] = operation.Items[index] with
            {
                FollowUpState = FolderRemovalItem.FollowUpStatus.Done,
                Generation = checked(expectedGeneration + 1)
            };
        }
    }

    public IEnumerable<FolderRemovalItem> FolderRemovalItems(string pairId, string operationId,
        long afterOrdinal, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_gate)
            return RequireFolder(pairId, operationId).Items
                .Where(item => item.Ordinal > afterOrdinal).OrderBy(item => item.Ordinal).Take(pageSize).ToList();
    }

    public FolderRemovalItem? FolderRemovalItemByKey(string pairId, string operationId, string key)
    {
        lock (_gate)
        {
            var operation = RequireFolder(pairId, operationId);
            return operation.Items.Where(item => PathNames.KeyFor(operation.State.DeletingSide, item.Path) == key)
                .OrderBy(item => item.Ordinal).FirstOrDefault();
        }
    }

    public void AdvanceFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRemovalPhase expectedPhase, FolderRemovalState next)
    {
        lock (_gate)
        {
            var current = RequireFolder(pairId, operationId).State;
            if (!current.Active || current.Generation != expectedGeneration ||
                current.Phase != expectedPhase || next.Generation != checked(expectedGeneration + 1) ||
                !next.Active || next.Phase == FolderRemovalPhase.Complete ||
                next with
                {
                    Generation = current.Generation, Phase = current.Phase, Cursor = current.Cursor,
                    Cause = current.Cause
                } != current)
                throw new InvalidOperationException();
            RequireFolder(pairId, operationId).State = next;
        }
    }

    public void SettleFolderRemovalItem(string pairId, string operationId, long ordinal,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange)
    {
        if (next.Ordinal != ordinal) throw new InvalidOperationException();
        if (!SettleFolderRemovalItems(pairId, operationId, [(expectedGeneration, next, verifiedChange)])[0])
            throw new FolderStateRefusedException();
    }

    public IReadOnlyList<bool> SettleFolderRemovalItems(string pairId, string operationId,
        IReadOnlyList<(long ExpectedGeneration, FolderRemovalItem Next, StateChange Change)> items)
    {
        lock (_gate)
        {
            var operation = RequireFolder(pairId, operationId);
            var members = operation.Items.ToList();
            var synced = SyncedRows(pairId).ToList();
            var issues = IssueRows(pairId).ToList();
            var scan = _scan.ToList();
            var spent = new HashSet<string>(_spentDeletedNames.GetValueOrDefault(pairId) ?? [], StringComparer.Ordinal);
            var settled = new bool[items.Count];
            HashSet<(Side Side, string Path, string Id)> released = [];
            for (var at = 0; at < items.Count; at++)
            {
                var (expectedGeneration, next, verifiedChange) = items[at];
                var index = members.FindIndex(item => item.Ordinal == next.Ordinal);
                if (index < 0) throw new InvalidOperationException();
                var current = members[index];
                try
                {
                    ValidateFolderSettlement(pairId, operationId, current, expectedGeneration, next, verifiedChange, members, synced);
                }
                catch (FolderStateRefusedException)
                {
                    continue;
                }
                ApplyCommit(synced, issues, scan, spent, pairId, verifiedChange, DateTimeOffset.UtcNow, released);
                if (next.EffectState == FolderRemovalItem.EffectStatus.Settled &&
                    current.EffectState == FolderRemovalItem.EffectStatus.Verified &&
                    operation.State.DeletingSide == Side.Local && current.EffectKind != FolderEffectKind.StateOnly)
                    spent.Add(PathNames.DropboxKey(current.Path));
                members[index] = next;
                settled[at] = true;
            }
            PublishReleased(pairId, released);
            operation.Items.Clear();
            operation.Items.AddRange(members);
            _spentDeletedNames[pairId] = spent;
            _synced[pairId] = synced;
            _issues[pairId] = issues;
            _scan.Clear();
            _scan.AddRange(scan);
            return settled;
        }
    }

    private void ValidateFolderSettlement(string pairId, string operationId, FolderRemovalItem current,
        long expectedGeneration, FolderRemovalItem next, StateChange verifiedChange,
        IReadOnlyList<FolderRemovalItem> members, IReadOnlyList<SyncedEntry> synced)
    {
        var operation = RequireFolder(pairId, operationId);
        if (!operation.State.Active) throw new FolderStateRefusedException();
        var settling = next.EffectState == FolderRemovalItem.EffectStatus.Settled &&
            current.EffectState != FolderRemovalItem.EffectStatus.Settled;
        if (current.Generation != expectedGeneration) throw new FolderStateRefusedException();
        if (current.IsTerminal && !next.IsTerminal ||
            next.Generation != checked(expectedGeneration + 1) ||
            next with
            {
                Generation = current.Generation, EffectGeneration = current.EffectGeneration,
                EffectState = current.EffectState, Phase = current.Phase, Selected = current.Selected,
                StageAllocation = current.StageAllocation, Stage = current.Stage, Result = current.Result,
                Cause = current.Cause
            } != current ||
            next.EffectGeneration != (settling ? checked(current.EffectGeneration + 1) :
                current.EffectGeneration) ||
            !settling && next.EffectState != current.EffectState)
            throw new InvalidOperationException();
        if (settling && (next.StageAllocation is not null || next.Stage is not null ||
            next.Phase != FolderRemovalItem.RecoveryPhase.Settled ||
            current.EffectState is not (FolderRemovalItem.EffectStatus.Verified or
                FolderRemovalItem.EffectStatus.NoEffect) && current.EffectKind != FolderEffectKind.StateOnly))
            throw new InvalidOperationException();
        if (current.StageAllocation is not null && next.StageAllocation is null &&
            current.Phase is not (FolderRemovalItem.RecoveryPhase.DiscardArmed or
                FolderRemovalItem.RecoveryPhase.AllocateArmed))
            throw new InvalidOperationException();
        ValidateFolderChange(operation, current.Path, current.Saved, verifiedChange, members, synced);
    }

    public void CompleteFolderRemoval(string pairId, string operationId, long expectedGeneration,
        StateChange verifiedRootChange, WorkSelection? selection)
    {
        lock (_gate)
        {
            var operation = RequireFolder(pairId, operationId);
            var current = operation.State;
            if (!current.Active || current.Generation != expectedGeneration ||
                current.Phase is not (FolderRemovalPhase.Checking or FolderRemovalPhase.Recovering) ||
                operation.Items.Any(item => item.EffectKind != FolderEffectKind.None &&
                    item.EffectState != FolderRemovalItem.EffectStatus.Settled ||
                    item.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending ||
                    item.StageAllocation is not null || item.Stage is not null))
                throw new FolderStateRefusedException();
            foreach (var item in operation.Items)
                if (item.Verdict == FolderVerdict.Survives || item.Result is not null ||
                    item.Phase is FolderRemovalItem.RecoveryPhase.Unknown or FolderRemovalItem.RecoveryPhase.Conflict)
                    throw new FolderStateRefusedException();
            foreach (var submission in _submissions.Values.Where(record => record.State.PairId == pairId &&
                         record.Effects.Any(effect => effect.OperationId == operationId)))
                if (submission.State.Phase != FolderSubmissionPhase.Terminal)
                    throw new FolderStateRefusedException();
            ValidateFolderChange(operation, current.RootEntry.Path, current.SavedRoot, verifiedRootChange,
                operation.Items, SyncedRows(pairId));
            var advance = RenameMembers(pairId, selection, verifiedRootChange);
            ApplyAtomic(pairId, verifiedRootChange);
            foreach (var (row, after) in advance) row.Before = after;
            operation.State = current with
            {
                Active = false, Phase = FolderRemovalPhase.Complete,
                Generation = checked(expectedGeneration + 1), Cause = null
            };
        }
    }

    public void RetireFolderRemoval(string pairId, string operationId, long expectedGeneration,
        FolderRetirementDisposition disposition, StateChange issueOnlyAcknowledgement)
    {
        lock (_gate)
        {
            RequireIssueOnly(issueOnlyAcknowledgement);
            var operation = RequireFolder(pairId, operationId);
            var current = operation.State;
            if (!current.Active || current.Generation != expectedGeneration) throw new InvalidOperationException();
            var noEffect = operation.Items.All(item => item.EffectState is FolderRemovalItem.EffectStatus.None or
                FolderRemovalItem.EffectStatus.Ready or FolderRemovalItem.EffectStatus.NoEffect);
            if (disposition != (noEffect ? FolderRetirementDisposition.NoEffect :
                    FolderRetirementDisposition.Acknowledged))
                throw new ArgumentException(null, nameof(disposition));
            if (operation.Items.Any(item => item.StageAllocation is not null || item.Stage is not null))
                throw new InvalidOperationException();
            ApplyAtomic(pairId, issueOnlyAcknowledgement);
            operation.State = current with
            {
                Active = false,
                Phase = current.Phase == FolderRemovalPhase.Unknown
                    ? FolderRemovalPhase.Unknown : FolderRemovalPhase.Retained,
                RetirementDisposition = disposition,
                Generation = checked(expectedGeneration + 1)
            };
        }
    }

    public string ArmFolderSubmission(string pairId, IReadOnlyList<FolderRemovalEffect> effects)
    {
        if (effects.Count == 0) throw new ArgumentOutOfRangeException(nameof(effects));
        lock (_gate)
        {
            var keys = new List<string>(effects.Count);
            Side? side = null;
            foreach (var effect in effects)
            {
                var operation = RequireFolder(pairId, effect.OperationId);
                var index = operation.Items.FindIndex(item => item.Ordinal == effect.ItemOrdinal);
                if (index < 0) throw new InvalidOperationException();
                var item = operation.Items[index];
                side ??= operation.State.DeletingSide;
                if (!operation.State.Active || operation.State.DeletingSide != side ||
                    item.IsTerminal || item.SubmissionId is not null ||
                    item.EffectState != FolderRemovalItem.EffectStatus.Ready ||
                    item.EffectGeneration != effect.EffectGeneration || item.Expected != effect.Expected ||
                    item.EffectKind != effect.Kind ||
                    effect.Kind is not (FolderEffectKind.RecursiveFolder or FolderEffectKind.ConditionalFile) ||
                    effect.Expected is null)
                    throw new InvalidOperationException();
                keys.Add(PathNames.KeyFor(operation.State.DeletingSide, effect.Expected.Path));
            }
            keys.Sort(StringComparer.Ordinal);
            for (var index = 1; index < keys.Count; index++)
                if (PathNames.IsSelfOrUnderKey(keys[index], keys[index - 1])) throw new InvalidOperationException();
            var submissionId = Guid.NewGuid().ToString();
            var record = new SubmissionRecord
            {
                State = new FolderSubmission(submissionId, pairId, 0, FolderSubmissionPhase.Armed,
                    null, null)
            };
            for (var index = 0; index < effects.Count; index++)
            {
                var effect = effects[index];
                var operation = RequireFolder(pairId, effect.OperationId);
                var itemIndex = operation.Items.FindIndex(item => item.Ordinal == effect.ItemOrdinal);
                var item = operation.Items[itemIndex];
                record.Effects.Add(new FolderRemovalEffect(effect.OperationId, item.Ordinal,
                    checked(item.EffectGeneration + 1), item.EffectKind, item.Expected));
                operation.Items[itemIndex] = item with
                {
                    SubmissionId = submissionId,
                    SubmissionMember = index,
                    EffectState = FolderRemovalItem.EffectStatus.Armed,
                    EffectGeneration = checked(item.EffectGeneration + 1),
                    Phase = FolderRemovalItem.RecoveryPhase.DeleteArmed,
                    Generation = checked(item.Generation + 1)
                };
            }
            _submissions.Add(submissionId, record);
            return submissionId;
        }
    }

    public FolderSubmission FolderSubmission(string submissionId)
    {
        lock (_gate) return RequireSubmission(submissionId).State;
    }

    public IReadOnlyList<FolderRemovalEffect> FolderSubmissionEffects(string submissionId)
    {
        lock (_gate) return RequireSubmission(submissionId).Effects.ToList();
    }

    public IEnumerable<FolderSubmission> PendingFolderSubmissions(string pairId, CancellationToken ct, bool progressOnly = false)
    {
        List<FolderSubmission> result = [];
        lock (_gate)
            result.AddRange(_submissions.Values.Where(record => record.State.PairId == pairId &&
                    record.State.Phase == FolderSubmissionPhase.Accepted && (!progressOnly || record.State.Cause is null))
                .OrderBy(record => record.State.SubmissionId, StringComparer.Ordinal)
                .Select(record => record.State));
        foreach (var submission in result)
        {
            ct.ThrowIfCancellationRequested();
            yield return submission;
        }
    }

    public bool HasFolderJobProgress(string pairId, CancellationToken ct) =>
        PendingFolderSubmissions(pairId, ct).Any(submission => submission.Cause is null);

    public void AdvanceFolderSubmission(FolderSubmission expected, FolderSubmission next,
        IReadOnlyList<FolderMemberResult> results)
    {
        lock (_gate)
        {
            var record = RequireSubmission(expected.SubmissionId);
            if (record.State != expected) throw new InvalidOperationException();
            AdvanceSubmission(record, expected, next, results);
            ReconcileSubmission(expected, next);
        }
    }

    public void RecordFolderJobObservation(FolderSubmission expected,
        FolderJobResult? observation, TextKey? failureCause)
    {
        lock (_gate)
        {
            if ((observation is null) == (failureCause is null) ||
                expected.Phase != FolderSubmissionPhase.Accepted)
                throw new ArgumentException(null, nameof(observation));
            var record = RequireSubmission(expected.SubmissionId);
            if (record.State != expected) throw new FolderStateRefusedException();
            var next = expected;
            IReadOnlyList<FolderMemberResult> results = [];
            if (observation is FolderJobResult.Terminal terminal)
            {
                next = expected with { Phase = FolderSubmissionPhase.Terminal, Cause = null };
                results = terminal.Results;
            }
            else if (observation is FolderJobResult.InProgress)
            {
                // A successful observation supersedes any earlier failure, resuming the progress cadence.
                next = expected with { Cause = null };
            }
            else if (failureCause == TextKey.FailDropboxDeleteJobInvalid)
                next = expected with { Cause = failureCause };
            else if (observation is null && !InvalidContribution(expected))
                next = expected with { Cause = failureCause };
            if (next != expected)
                AdvanceSubmission(record, expected, next with { Generation = checked(expected.Generation + 1) },
                    results);
            ReconcileSubmission(expected, record.State);
        }
    }

    private void ReconcileSubmission(FolderSubmission before, FolderSubmission after)
    {
        if (!InvalidContribution(before) && !InvalidContribution(after)) return;
        var issues = IssueRows(before.PairId);
        foreach (var path in _folders.Where(entry => entry.Key.PairId == before.PairId &&
                     entry.Value.State.Active && entry.Value.Items.Any(item => item.SubmissionId == before.SubmissionId))
                     .Select(entry => entry.Value.State.RootEntry.Path).Distinct(StringComparer.Ordinal))
            ReconcileInvalidFolderJob(before.PairId, path,
                issues.FirstOrDefault(issue => issue.Kind == IssueKind.Failure && issue.Path == path),
                issues, DateTimeOffset.UtcNow);
        _issues[before.PairId] = issues;
    }

    public bool HasInvalidFolderJob(string pairId, string path)
    {
        lock (_gate)
            return _folders.Where(entry => entry.Key.PairId == pairId && entry.Value.State.Active &&
                    entry.Value.State.RootEntry.Path == path)
                .Any(entry => entry.Value.Items.Any(item => item.SubmissionId is { } id &&
                    _submissions.TryGetValue(id, out var record) && record.State.PairId == pairId &&
                    record.State.Phase == FolderSubmissionPhase.Accepted &&
                    record.State.Cause == TextKey.FailDropboxDeleteJobInvalid));
    }

    public IReadOnlyList<Issue> Issues(string pairId, IReadOnlyList<IssueKind>? kinds = null)
    {
        lock (_gate)
            return IssueRows(pairId)
                .Where(issue => kinds is null || kinds.Contains(issue.Kind))
                .OrderBy(issue => issue.Id).ToList();
    }

    public (IReadOnlyList<(string PairId, Issue Issue)> Page, int Total) OpenIssuePage(string? pairId,
        string? account, string? search, IReadOnlyList<IssueKind>? kinds, int limit, int offset, bool ignored = false)
    {
        lock (_gate)
        {
            var rows = _issues
                .Where(pair => pairId is null || pair.Key == pairId)
                .Where(pair => account is null ||
                    _pairs.TryGetValue(pair.Key, out var config) && config.ConnectionId == account)
                .SelectMany(pair => pair.Value.Where(issue => !issue.Dismissed && issue.Ignored == ignored)
                    .Select(issue => (PairId: pair.Key, Issue: issue)))
                .Where(row => kinds is null || kinds.Contains(row.Issue.Kind))
                .Where(row => string.IsNullOrEmpty(search) || PathNames.Contains(row.Issue.Path, search))
                .OrderBy(row => row.Issue.Id).ToArray();
            return (rows.Skip(offset).Take(limit).ToArray(), rows.Length);
        }
    }

    public (IReadOnlyList<(string PairId, Issue Issue)> Rows, int Total) OpenIssueSnapshot(
        IReadOnlyList<IssueKind>? kinds, int pageSize)
    {
        const bool ignored = false;
        lock (_gate)
        {
            var rows = _issues
                .SelectMany(pair => pair.Value.Where(issue => !issue.Dismissed && issue.Ignored == ignored)
                    .Select(issue => (PairId: pair.Key, Issue: issue)))
                .Where(row => kinds is null || kinds.Contains(row.Issue.Kind))
                .OrderBy(row => row.Issue.Id).ToArray();
            return (rows, rows.Length);
        }
    }
    public IEnumerable<Issue> OpenIssuesUnder(string pairId, string scopeKey, IReadOnlyList<IssueKind>? kinds = null) =>
        Issues(pairId, kinds).Where(issue => issue.IsUnder(scopeKey));

    public long SyncedCount(string pairId)
    {
        lock (_gate) return SyncedRows(pairId).Count;
    }

    /// <summary>Counts only non-dismissed issues, matching the durable store.</summary>
    public IReadOnlyDictionary<IssueKind, int> IssueCounts(string pairId, bool? ignored = null)
    {
        lock (_gate)
            return IssueRows(pairId).Where(issue => !issue.Dismissed && (ignored is null || issue.Ignored == ignored)).GroupBy(issue => issue.Kind)
                .ToDictionary(group => group.Key, group => group.Count());
    }

    public bool SetIssueIgnored(string pairId, Issue expected, bool ignored)
    {
        lock (_gate)
        {
            var rows = IssueRows(pairId);
            var index = rows.FindIndex(issue => issue.Id == expected.Id);
            if (index < 0 || !rows[index].Matches(expected)) return false;
            rows[index] = rows[index] with { Ignored = ignored };
            return true;
        }
    }

    public Issue? IssueById(string pairId, long id)
    {
        lock (_gate) return IssueRows(pairId).FirstOrDefault(issue => issue.Id == id);
    }

    public IssueKind? HeldKind(string pairId, Row row, IReadOnlySet<long>? exceptIssues)
    {
        lock (_gate) return ProjectIssues(pairId).HeldKind(row, exceptIssues);
    }

    public bool HeldCovers(string pairId, string key, IReadOnlySet<long>? exceptIssues)
    {
        lock (_gate) return ProjectIssues(pairId).Covering(key, exceptIssues) is not null;
    }

    public bool HeldByOtherIssue(string pairId, Row row, long selectedIssueId)
    {
        lock (_gate) return ProjectIssues(pairId).HeldByOtherIssue(row, selectedIssueId);
    }

    public Issue? HoldingIssue(string pairId, Row row)
    {
        lock (_gate) return ProjectIssues(pairId).HoldingIssue(row);
    }

    public IReadOnlyDictionary<string, IssueSubject> RetainedFirstRun(string pairId)
    {
        lock (_gate) return ProjectIssues(pairId).Retained;
    }

    private IssueFacts ProjectIssues(string pairId)
    {
        var facts = new IssueFacts(pairId, PairRow(pairId));
        foreach (var issue in IssueRows(pairId)) facts.Replace(issue.Id, issue);
        return facts;
    }

    private IReadOnlyDictionary<string, IssueKind> HeldKinds(string pairId, IReadOnlySet<long>? retryIssues = null)
    {
        lock (_gate)
        {
            var kinds = new Dictionary<string, IssueKind>(StringComparer.Ordinal);
            foreach (var issue in IssueRows(pairId))
            {
                if (!IssuePolicy.Holds(issue.Kind, issue.Cause)) continue;
                if (IssuePolicy.Retried(issue.Kind, issue.Id, retryIssues)) continue;
                if (issue.Kind == IssueKind.Failure && issue.Subject.Items.Count == 0)
                {
                    // A subject-less Failure names only its path, so it holds both side keys (2.1).
                    kinds.TryAdd(PathNames.Key(issue.Path), issue.Kind);
                    kinds.TryAdd(PathNames.DropboxKey(issue.Path), issue.Kind);
                    continue;
                }
                foreach (var item in issue.Subject.Items)
                    kinds.TryAdd(item.Side == Side.Local ? PathNames.Key(item.Path) : PathNames.DropboxKey(item.Path),
                        issue.Kind);
            }
            return kinds;
        }
    }

    public void ReplaceSkipped(string pairId, Guid scanToken,
        IEnumerable<(string Path, TextKey Cause, IssueSubject Subject)> skipped)
    {
        lock (_gate)
        {
            var scan = ReadScan(pairId, scanToken);
            if (scan.Scope is not null) throw new InvalidOperationException();
            var wanted = skipped.GroupBy(entry => entry.Path, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            foreach (var path in IssueRows(pairId).Where(issue => issue.Kind == IssueKind.Skipped)
                         .Select(issue => issue.Path).Distinct().ToList())
                if (!wanted.ContainsKey(path))
                    foreach (var side in new[] { Side.Local, Side.Dropbox })
                        if (!_skipTouched.Contains((pairId, scanToken, side, PathNames.KeyFor(side, path))))
                            MergeSkip(pairId, path, side, null);
            foreach (var (path, entries) in wanted)
                foreach (var side in new[] { Side.Local, Side.Dropbox })
                {
                    if (_skipTouched.Contains((pairId, scanToken, side, PathNames.KeyFor(side, path)))) continue;
                    var naming = entries.Where(candidate =>
                        candidate.Subject.Items.Any(item => item.Side == side)).ToList();
                    var skip = naming.Count == 0 ? null :
                        ((TextKey Cause, IssueSubject Subject)?)(naming[0].Cause, IssueSubject.FromDistinct(
                            naming.SelectMany(candidate => candidate.Subject.Items)
                                .Where(item => item.Side == side),
                            naming.Select(candidate => candidate.Subject.Incoming)
                                .FirstOrDefault(incoming => incoming is not null)));
                    MergeSkip(pairId, path, side, skip);
                }
        }
    }

    public void ObserveSkipped(string pairId, long expectedGeneration, Side side, string path,
        (TextKey Cause, IssueSubject Subject)? currentSkip)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            if (side == Side.Local && !IssuePolicy.ProtectsSubtree(currentSkip?.Cause.ToString()) &&
                _skipMembers.TryGetValue((pairId, side, PathNames.Key(path)), out var stored) &&
                IssuePolicy.ProtectsSubtree(stored.Cause.ToString())) return;
            foreach (var scan in _scans.Where(item => item.Key.PairId == pairId && item.Value.Generation == expectedGeneration))
                if (scan.Value.Scope is null || PathNames.IsSelfOrUnderKey(PathNames.KeyFor(side, path), PathNames.Key(scan.Value.Scope)))
                    _skipTouched.Add((pairId, scan.Key.ScanId, side, PathNames.KeyFor(side, path)));
            MergeSkip(pairId, path, side, currentSkip);
        }
    }

    public bool RetryNoticeProtected(string pairId, long issueId)
    {
        lock (_gate)
        {
            var issue = IssueRows(pairId).FirstOrDefault(stored => stored.Id == issueId);
            return issue is not null && RetainedByOwner(pairId, issue);
        }
    }

    public void DismissIssue(string pairId, long id)
    {
        lock (_gate)
        {
            var issues = IssueRows(pairId);
            var index = issues.FindIndex(issue => issue.Id == id);
            if (index < 0) return;
            if (issues[index].Kind == IssueKind.Info) issues.RemoveAt(index);
            else issues[index] = issues[index] with { Dismissed = true };
        }
    }

    public void KeepFolderAlways(string pairId, long expectedGeneration, Issue expected,
        IReadOnlyList<Massdex.SyncShark.SyncIgnoreRule> keepRules)
    {
        lock (_gate)
        {
            if (expected.Kind != IssueKind.Info || expected.Cause != TextKey.InfoIgnoredFolderKept ||
                keepRules.Count == 0)
                throw new ArgumentException(null, nameof(expected));
            var normalized = SyncIgnorePolicy.Normalize(keepRules);
            RequireGeneration(pairId, expectedGeneration);
            var current = _pairs[pairId];
            var union = SyncIgnorePolicy.Normalize((current.IgnoredFolderKeepRules ?? [])
                    .SelectMany(SyncIgnorePolicy.Parse).Concat(normalized).ToArray())
                .Select(SyncIgnorePolicy.Format).ToArray();
            if (!CloseExact(pairId, expected))
            {
                if (IssueById(pairId, expected.Id) is not null ||
                    !(current.IgnoredFolderKeepRules ?? []).SequenceEqual(union))
                    throw new InvalidOperationException();
                return;
            }
            _pairs[pairId] = current with { IgnoredFolderKeepRules = union };
        }
    }

    /// <summary>Starts one process-local scan token; discovery is decided by Repair, not by a saved row.</summary>
    public Guid BeginScan(string pairId, Guid scanId, long expectedGeneration, bool includeDeleted, string? scope = null)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            var local = RequireSource(pairId, Side.Local);
            var dropbox = RequireSource(pairId, Side.Dropbox);
            var gap = ReadLocalGap(pairId, local.Epoch);
            if (scanId == Guid.Empty || _scans.ContainsKey((pairId, scanId)) || includeDeleted && scope is not null ||
                scope is null && _scans.Any(item => item.Key.PairId == pairId && item.Value.Scope is null))
                throw new InvalidOperationException();
            var token = scanId;
            _scans[(pairId, scanId)] = new ScanRecord(token, expectedGeneration, includeDeleted,
                !FirstRepairDone(pairId), _nextWorkId, local.Epoch, local.Revision, dropbox.Epoch, dropbox.Revision, gap) { Scope = scope };
            if (includeDeleted) _deletedNames.Remove(pairId);
            _scanIssues[(pairId, scanId)] = IssueRows(pairId).ToList();
            return token;
        }
    }

    public bool HasDeletedNames(string pairId, Guid scanId)
    {
        lock (_gate)
            return ReadScan(pairId, scanId).Discovery &&
                ((_deletedNames.TryGetValue(pairId, out var names) && names.Any(key => !DeletedNameSpent(pairId, key))) ||
                 (_deletedDelta.TryGetValue(pairId, out var delta) && delta.Any(key => !DeletedNameSpent(pairId, key))));
    }

    public bool DeletedNameExact(string pairId, Guid scanId, string key)
    {
        lock (_gate)
            return !DeletedNameSpent(pairId, key) &&
                ((ReadScan(pairId, scanId).Discovery &&
                  _deletedNames.TryGetValue(pairId, out var names) && names.Contains(key)) ||
                 (_deletedDelta.TryGetValue(pairId, out var delta) && delta.Contains(key)) ||
                 (!_resumedHistory.Contains((pairId, scanId)) && _deletedHistory.TryGetValue(pairId, out var history) && history.Names.Contains(key)));
    }

    public bool DeletedNameSpent(string pairId, string key)
    {
        lock (_gate) return _spentDeletedNames.TryGetValue(pairId, out var spent) && spent.Contains(key);
    }

    public ScanRecord ReadScan(string pairId, Guid scanToken)
    {
        lock (_gate)
        {
            if (!_scans.TryGetValue((pairId, scanToken), out var scan))
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            RequireGeneration(pairId, scan.Generation);
            return scan;
        }
    }

    public bool DeletedHistoryOwed(string pairId)
    {
        lock (_gate) return !_deletedHistory.ContainsKey(pairId);
    }

    public bool SavedDeletedNameExact(string pairId, string key)
    {
        lock (_gate) return !DeletedNameSpent(pairId, key) && _deletedHistory.TryGetValue(pairId, out var history) &&
            (history.Names.Contains(key) || _deletedDelta.TryGetValue(pairId, out var delta) && delta.Contains(key));
    }

    public string? ContinueDeletedHistory(string pairId, Guid scanToken)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, ReadScan(pairId, scanToken).Generation);
            if (!ReadScan(pairId, scanToken).Discovery || !_deletedHistory.TryGetValue(pairId, out var history)) return null;
            _incrementalHistory.Add((pairId, scanToken));
            return history.Cursor;
        }
    }

    public string? RestoreDeletedHistory(string pairId, Guid scanToken, Action<long>? restored = null)
    {
        string cursor;
        long count;
        bool blocks;
        lock (_gate)
        {
            var scan = ReadScan(pairId, scanToken);
            RequireGeneration(pairId, scan.Generation);
            if (!scan.Discovery || !_deletedHistory.TryGetValue(pairId, out var history) ||
                history.Generation != scan.Generation) return null;
            _deletedNames[pairId] = new HashSet<string>(history.Names.Where(key => !DeletedNameSpent(pairId, key)), StringComparer.Ordinal);
            _resumedHistory.Add((pairId, scanToken));
            (cursor, count, blocks) = (history.Cursor, _deletedNames[pairId].Count, history.Names.Count != 0);
        }
        // One whole-set step: the double reports its count once, outside its lock, like the store's per-block report.
        if (blocks) restored?.Invoke(count);
        return cursor;
    }

    public void ResetDeletedHistory(string pairId, Guid scanToken, bool discardNames)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, ReadScan(pairId, scanToken).Generation);
            if (!ReadScan(pairId, scanToken).Discovery) throw new InvalidOperationException();
            _scan.RemoveAll(item => item.PairId == pairId && item.ScanId == scanToken && item.Side == Side.Dropbox);
            if (discardNames)
            {
                _deletedNames.Remove(pairId);
                _deletedHistory.Remove(pairId);
                _resumedHistory.Remove((pairId, scanToken));
                _incrementalHistory.Remove((pairId, scanToken));
            }
        }
    }

    public void SaveDeletedHistory(string pairId, Guid scanToken, string cursor)
    {
        lock (_gate)
        {
            var scan = ReadScan(pairId, scanToken);
            RequireGeneration(pairId, scan.Generation);
            if (_incrementalHistory.Contains((pairId, scanToken)))
            {
                var history = _deletedHistory[pairId];
                _deletedHistory[pairId] = (history.Generation, cursor, history.Names);
                return;
            }
            if (!scan.Discovery) return;
            var names = new HashSet<string>(_deletedNames.GetValueOrDefault(pairId) ?? [], StringComparer.Ordinal);
            if (_deletedDelta.Remove(pairId, out var delta)) names.UnionWith(delta);
            names.RemoveWhere(key => DeletedNameSpent(pairId, key));
            _deletedNames[pairId] = names;
            _deletedHistory[pairId] = (scan.Generation, cursor, new HashSet<string>(names, StringComparer.Ordinal));
        }
    }

    public IScanSink OpenScan(string pairId, Guid scanId, Side side, int batchRows)
    {
        lock (_gate) return new ScanSink(this, pairId, side, ReadScan(pairId, scanId).Token);
    }

    public Task EndScanAsync(string pairId, Guid scanId, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        lock (_gate)
        {
            _incrementalHistory.Remove((pairId, scanId));
            _resumedHistory.Remove((pairId, scanId));
            _scans.Remove((pairId, scanId));
            _scanIssues.Remove((pairId, scanId));
            _scan.RemoveAll(row => row.PairId == pairId && row.ScanId == scanId);
            _skipTouched.RemoveWhere(row => row.PairId == pairId && row.ScanId == scanId);
            if (!_scans.Keys.Any(key => key.PairId == pairId)) _deletedNames.Remove(pairId);
        }
        return Task.CompletedTask;
    }

    public bool CloseScanIssue(string pairId, Guid scanId, Issue expected)
    {
        lock (_gate)
        {
            if (ReadScan(pairId, scanId).Scope is not null) throw new InvalidOperationException();
            if (!_scanIssues[(pairId, scanId)].Contains(expected) || !IssueRows(pairId).Contains(expected)) return false;
            ApplyAtomic(pairId, StateChange.Empty with
            {
                FirstRunAcknowledged = expected.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None
                    ? [] : expected.Subject.Items.Where(item => item.Side == Side.Local).ToArray(),
                Close = [(expected.Path, new[] { expected.Kind })]
            });
            return !IssueRows(pairId).Any(issue => issue.Id == expected.Id);
        }
    }

    public FolderPlan OpenFolderPlan(string pairId, Row folder, Side deletingFrom) =>
        throw new NotSupportedException();

    public bool UnreadableTouches(string pairId, string localKey, bool isFolder)
    {
        lock (_gate)
            return ProtectingMembers(pairId).Any(key => PathNames.IsSelfOrUnderKey(localKey, key) ||
                isFolder && PathNames.IsUnderKey(key, localKey));
    }

    public IReadOnlyList<string> UnreadableUnder(string pairId, string localKey)
    {
        lock (_gate)
            return ProtectingMembers(pairId).Where(key => PathNames.IsSelfOrUnderKey(key, localKey)).ToList();
    }

    public bool ReleaseUnreadable(string pairId, long expectedGeneration, string localKey, string acquiredId)
    {
        lock (_gate)
        {
            RequireGeneration(pairId, expectedGeneration);
            var key = (pairId, Side.Local, localKey);
            if (!_skipMembers.TryGetValue(key, out var member) || !IssuePolicy.ProtectsSubtree(member.Cause.ToString()) ||
                member.Subject.Items[0].Id != acquiredId) return false;
            foreach (var scan in _scans.Where(item => item.Key.PairId == pairId && item.Value.Generation == expectedGeneration))
                if (scan.Value.Scope is null || PathNames.IsSelfOrUnderKey(localKey, PathNames.Key(scan.Value.Scope)))
                    _skipTouched.Add((pairId, scan.Key.ScanId, Side.Local, localKey));
            MergeSkip(pairId, member.Subject.Items[0].Path, Side.Local, null);
            return true;
        }
    }

    public bool ForgetTouchesUnreadable(string pairId, IReadOnlyList<(Side Side, string Key)> roots, CancellationToken ct)
    {
        lock (_gate)
            return PathNames.DisjointRoots(roots).Any(root => root.Side == Side.Local
                ? UnreadableTouches(pairId, root.Key, true)
                : SyncedRows(pairId).Any(saved => PathNames.IsSelfOrUnderKey(saved.DropboxKey, root.Key) &&
                    UnreadableTouches(pairId, saved.LocalKey, saved.IsFolder)));
    }

    private IEnumerable<string> ProtectingMembers(string pairId) => _skipMembers
        .Where(member => member.Key.PairId == pairId && member.Key.Side == Side.Local &&
            IssuePolicy.ProtectsSubtree(member.Value.Cause.ToString()))
        .Select(member => member.Key.Key).ToList();

    public IEnumerable<Row> ScanRows(string pairId, Guid scanId, Func<Row, RowContext> context)
    {
        List<RowParts> parts;
        lock (_gate)
        {
            ReadScan(pairId, scanId);
            parts = BuildRows(SyncedRows(pairId), _scan.Where(row => row.PairId == pairId && row.ScanId == scanId).ToList());
        }
        foreach (var part in parts)
        {
            // Context is built from the row's own paths, so the placeholder is never read (as ScanReader).
            var row = new Row(part.Key, part.Local, part.Dropbox, part.Synced, part.Extra, null, null!);
            if (_scans.GetValueOrDefault((pairId, scanId))?.Scope is { } scope && !PathNames.IsSelfOrUnderKey(row.Key, PathNames.Key(scope))) continue;
            yield return row with { Context = context(row) };
        }
    }

    public IReadOnlyList<RenameCandidate> ScanRenames(string pairId, Guid scanId)
    {
        lock (_gate)
        {
            ReadScan(pairId, scanId);
            var candidates = new List<RenameCandidate>();
            foreach (var scan in _scan.Where(row => row.PairId == pairId && row.ScanId == scanId))
                foreach (var synced in SyncedRows(pairId))
                {
                    if (scan.Side == Side.Local)
                    {
                        if (scan.Id.Length != 0 && synced.LocalId == scan.Id && scan.Path != synced.LocalPath)
                            candidates.Add(new RenameCandidate(Side.Local, LocalEntry(scan), synced));
                    }
                    else if (scan.Id.Length != 0 && synced.DropboxId == scan.Id && scan.Path != synced.DropboxPath)
                        candidates.Add(new RenameCandidate(Side.Dropbox, DropboxEntry(scan), synced));
                }
            return candidates.OrderBy(candidate => Depth(candidate.Moved.Path))
                .ThenBy(candidate => candidate.Moved.Path, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>Reads the side's scan entry by its own key rather than the mapped join key.</summary>
    public Entry? ScanEntry(string pairId, Guid scanId, Side side, string key)
    {
        lock (_gate)
        {
            ReadScan(pairId, scanId);
            return _scan.Where(row => row.PairId == pairId && row.ScanId == scanId && row.Side == side && PathNames.Key(row.Path) == key)
                .Select(row => new Entry(row.Path, row.IsFolder, row.Size, new DateTimeOffset(row.ModifiedTicks, TimeSpan.Zero),
                    row.Id, row.Revision, row.ContentHash, row.Skip)).FirstOrDefault();
        }
    }

    public bool InScan(string pairId, Guid scanId, Side side, string key)
    {
        lock (_gate)
        {
            ReadScan(pairId, scanId);
            return _scan.Any(row => row.PairId == pairId && row.ScanId == scanId && row.Side == side && row.Key == key);
        }
    }

    public void Dispose()
    {
    }

    private long Generation(string pairId) => _generations.GetValueOrDefault(pairId);

    private long PairRow(string pairId) => Pairs().ToList().FindIndex(pair => pair.PairId == pairId);

    private static string SourceKey(string pairId, Side side) => pairId + "\n" + (int)side;

    private static string Encode(PairConfig pair) => System.Text.Json.JsonSerializer.Serialize(pair);

    private List<SyncedEntry> SyncedRows(string pairId) => _synced.TryGetValue(pairId, out var rows) ? rows : [];

    // Materialize on first use: ReplaceSkipped/ObserveSkipped/Raise mutate this list before any commit.
    private List<Issue> IssueRows(string pairId)
    {
        if (!_issues.TryGetValue(pairId, out var rows)) _issues[pairId] = rows = [];
        return rows;
    }

    private IEnumerable<SourceRow> Sources(string pairId)
    {
        if (_sources.TryGetValue(SourceKey(pairId, Side.Local), out var local)) yield return local;
        if (_sources.TryGetValue(SourceKey(pairId, Side.Dropbox), out var dropbox)) yield return dropbox;
    }

    private SourceRow RequireSource(string pairId, Side side) =>
        _sources.TryGetValue(SourceKey(pairId, side), out var source) ? source :
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);

    private void RequireGeneration(string pairId, long expectedGeneration)
    {
        if (!_pairs.ContainsKey(pairId) || Generation(pairId) != expectedGeneration)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
    }

    private void SeedSources(string pairId)
    {
        var reason = _firstRepairDone.Contains(pairId) ? SourceObligation.NormalRepair : SourceObligation.Bootstrap;
        _sources.TryAdd(SourceKey(pairId, Side.Local), new SourceRow(Side.Local, 0, null, null, reason, 1));
        _sources.TryAdd(SourceKey(pairId, Side.Dropbox), new SourceRow(Side.Dropbox, 0, null, null, reason, 1));
    }

    private void RequirePendingSaved(string pairId, PendingEffect effect)
    {
        if (!StateStore.IsCrossedLocalReplace(effect))
        {
            RequireSaved(pairId, effect.Losing.Expected, effect.Saved);
            return;
        }
        RequireSaved(pairId, effect.Losing.Expected, null);
        var saved = effect.Saved!;
        var rows = SyncedRows(pairId);
        if (rows.FirstOrDefault(row => row.DropboxKey == saved.DropboxKey) != saved ||
            rows.FirstOrDefault(row => row.DropboxId == saved.DropboxId) != saved)
            throw new InvalidOperationException();
    }

    private void RequireSaved(string pairId, Entry expected, SyncedEntry? saved)
    {
        var rows = SyncedRows(pairId);
        var byKey = rows.FirstOrDefault(row => row.LocalKey == PathNames.Key(expected.Path));
        var byId = rows.FirstOrDefault(row => row.LocalId == expected.Id);
        if (byKey != saved || byId != saved) throw new InvalidOperationException();
    }

    private bool HasCurrentLocalWork(string pairId, long generation) =>
        _work.Any(row => !row.IssueOrigin && row.PairId == pairId && row.PairGeneration == generation &&
            row.Side == Side.Local);

    private void TransferLocalEvidenceGap(string pairId, long generation, long epoch, long revision)
    {
        var source = RequireSource(pairId, Side.Local);
        if (source.Epoch != epoch || source.Revision != revision ||
            !HasCurrentLocalWork(pairId, generation))
            throw new InvalidOperationException();
        source.Obligation = SourceObligation.LocalSkippedCoverage;
    }

    private void Advance(string pairId, Side side, long generation, long expectedEpoch, long expectedRevision,
        SourceProvenance? expectedProvenance, string? expectedCursor, long nextEpoch, long nextRevision,
        SourceProvenance? provenance, string? cursor, SourceObligation? obligation)
    {
        var source = RequireSource(pairId, side);
        if (Generation(pairId) != generation || source.Epoch != expectedEpoch ||
            source.Revision != expectedRevision || source.Provenance != expectedProvenance ||
            source.Cursor != expectedCursor || nextEpoch < source.Epoch ||
            nextEpoch != source.Epoch && nextRevision != checked(source.Revision + 1))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var reason = side == Side.Local &&
            (source.Obligation == SourceObligation.LocalSkippedCoverage ||
             nextEpoch > source.Epoch && HasCurrentLocalWork(pairId, generation))
                ? SourceObligation.LocalSkippedCoverage
                : obligation;
        _sources[SourceKey(pairId, side)] = new SourceRow(side, nextEpoch, provenance, cursor, reason,
            nextRevision);
    }

    private void TransferLocalDirtyEvidence(string pairId, RootBinding? roots, long expectedGeneration,
        long expectedEpoch, long nextGeneration, long nextEpoch)
    {
        if (roots is null) return;
        var domain = roots.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var dirty = _work.Where(row => !row.IssueOrigin && row.PairId == pairId &&
            row.PairGeneration == expectedGeneration && row.Side == Side.Local &&
            row.SourceEpoch == expectedEpoch && row.Domain == domain &&
            row.Notice is { LocalContentDirty: true }).OrderBy(row => row.WorkId).ToList();
        foreach (var row in dirty)
            AddWork(pairId, nextGeneration, nextEpoch, Side.Local, domain, row.Notice!, null, null, null,
                acquisition: row.Acquisition);
    }

    private WorkRow AddWork(string pairId, long generation, long epoch, Side side, string domain,
        ChangeNotice notice, SyncedEntry? before, long? parent, string? obligation, long? acquisition = null)
    {
        var row = new WorkRow
        {
            WorkId = ++_nextWorkId,
            PairId = pairId,
            PairGeneration = generation,
            SourceEpoch = epoch,
            Side = side,
            Domain = domain,
            Notice = notice,
            Before = before,
            Acquisition = acquisition ?? _nextWorkId,
            Parent = parent,
            ObligationKey = obligation,
            PathKey = PathNames.KeyFor(side, notice.Path),
            OldPathKey = notice.OldPath is null ? null : PathNames.KeyFor(side, notice.OldPath),
            Identity = notice.Id
        };
        _work.Add(row);
        return row;
    }

    private WorkRow? NextCandidate(string pairId, Pass pass)
    {
        var local = RequireSource(pairId, Side.Local).Epoch;
        var dropbox = RequireSource(pairId, Side.Dropbox).Epoch;
        return _work.Where(row => !row.IssueOrigin && row.PairId == pairId &&
                row.PairGeneration == pass.Generation && row.WorkId > pass.After &&
                row.WorkId <= pass.Through &&
                (row.Side == Side.Local ? row.SourceEpoch == local : row.SourceEpoch == dropbox) &&
                (row.Parent is null || !WorkExists(pairId, row.Parent.Value)))
            .OrderBy(row => row.WorkId).FirstOrDefault();
    }

    private bool WorkExists(string pairId, long workId) =>
        _work.Any(row => row.PairId == pairId && row.WorkId == workId);

    private void BootstrapRename(string pairId, WorkRow work, long through)
    {
        if (work.Notice is null || work.Identity is null || work.RenameContinuation is not null ||
            _items.Values.Any(candidate => candidate.Token.WorkId == work.WorkId)) return;
        var issue = IssueRows(pairId).FirstOrDefault(value => value.Id == work.IssueId);
        if (issue is null || !IssuePolicy.RenameClashMatches(issue, work.Side, work.PathKey, work.Identity)) return;
        var incoming = SyncedById(pairId, work.Side, work.Identity);
        if (incoming is null || incoming.KeyOn(work.Side) == work.PathKey) return;
        var opposite = PathNames.Other(work.Side);
        var occupant = issue.Subject.Items.FirstOrDefault(item => item.Side == opposite);
        if (occupant is null || SyncedById(pairId, opposite, occupant.Id) is not { } destination) return;
        var after = incoming with
        {
            LocalPath = work.Side == Side.Local ? work.Notice.Path : occupant.Path,
            LocalKey = PathNames.Key(work.Side == Side.Local ? work.Notice.Path : occupant.Path),
            DropboxPath = work.Side == Side.Dropbox ? work.Notice.Path : occupant.Path,
            DropboxKey = PathNames.DropboxKey(work.Side == Side.Dropbox ? work.Notice.Path : occupant.Path)
        };
        var owners = RenameDestinationOwners(pairId, incoming, after);
        if (owners.Count == 1 && owners[0].Binding == destination)
            RegisterRenameContinuation(ToSelection(pairId, work, through), incoming, destination, issue);
    }

    private bool Held(string pairId, WorkRow row)
    {
        if (_items.Values.Any(candidate => candidate.Token.WorkId == row.WorkId)) return true;
        var issue = row.IssueId == 0 ? null : IssueRows(pairId).FirstOrDefault(stored => stored.Id == row.IssueId);
        if (issue is { Ignored: false, Dismissed: false } && row.RenameContinuation?.Clash is { } captured &&
            issue.Matches(captured)) return false;
        return issue is not null && IssuePolicy.Holds(issue.Kind, issue.Cause);
    }

    private WorkRow Earliest(string pairId, WorkRow row, long through)
    {
        if (row.Identity is null) return row;
        var group = ToSelection(pairId, row, through) with { AnchorWorkId = 1 };
        return Membership(group, row).FirstOrDefault(first => first.WorkId == row.WorkId || !Held(pairId, first)) ?? row;
    }

    private WorkRow ParentOwner(WorkRow row)
    {
        while (row.Parent is { } parent && _work.FirstOrDefault(stored => stored.PairId == row.PairId &&
                   stored.WorkId == parent && stored.PairGeneration == row.PairGeneration &&
                   stored.SourceEpoch == row.SourceEpoch) is { } owner)
            row = owner;
        return row;
    }

    private WorkRow RequireAnchor(WorkSelection selection) =>
        _work.FirstOrDefault(row => row.WorkId == selection.AnchorWorkId && row.PairId == selection.PairId &&
            row.PairGeneration == selection.PairGeneration && row.SourceEpoch == selection.SourceEpoch) ??
        throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);

    private void RequireCurrent(WorkSelection selection)
    {
        if (!_pairs.ContainsKey(selection.PairId) || Generation(selection.PairId) != selection.PairGeneration ||
            !_sources.TryGetValue(SourceKey(selection.PairId, selection.Side), out var source) ||
            source.Epoch != selection.SourceEpoch)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
    }

    private void RequireCoverage(WorkSelection selection, LocalCatchUp local, DropboxCatchUp dropbox)
    {
        var ownThrough = selection.Side == Side.Local ? local.ThroughWorkId : dropbox.ThroughWorkId;
        if (!_pairs.ContainsKey(selection.PairId) || Generation(selection.PairId) != selection.PairGeneration ||
            local.PairId != selection.PairId || dropbox.PairId != selection.PairId ||
            local.PairGeneration != selection.PairGeneration ||
            dropbox.PairGeneration != selection.PairGeneration ||
            selection.ThroughWorkId > ownThrough ||
            ReadLiveRefusal(selection.PairId) == LiveRefusal.Pair ||
            RequireSource(selection.PairId, Side.Local).Epoch != local.SourceEpoch ||
            RequireSource(selection.PairId, Side.Dropbox).Epoch != dropbox.SourceEpoch)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
    }

    private static bool Owned(WorkSelection selection, WorkRow anchor, WorkRow row) =>
        row.WorkId == selection.AnchorWorkId || row.Parent == selection.AnchorWorkId ||
        selection.Identity is not null && row.Side == selection.Side && row.Identity == selection.Identity &&
        row.WorkId > selection.AnchorWorkId && row.WorkId <= selection.ThroughWorkId &&
        row.Parent == anchor.Parent && row.ObligationKey == anchor.ObligationKey &&
        (row.Before is null || row.Before == anchor.Before);

    private bool PeerOwned(WorkSelection selection, WorkRow anchor, WorkRow row, SelectionPeer? peer) =>
        peer is not null && !row.IssueOrigin && row.PairId == selection.PairId &&
        row.PairGeneration == selection.PairGeneration && row.Side != selection.Side &&
        row.SourceEpoch == RequireSource(selection.PairId, row.Side).Epoch &&
        row.Domain == Domain(selection.PairId, row.Side) && row.PathKey == peer.Key &&
        row.WorkId <= peer.ThroughWorkId && row.OldPathKey is null &&
        (row.Parent is not { } parent || !_work.Any(work => work.PairId == row.PairId && work.WorkId == parent)) &&
        row.IssueId == 0 && (row.Before is null || row.Before == anchor.Before) &&
        !_work.Any(child => child.Parent == row.WorkId);

    private static WorkSelection ToSelection(string pairId, WorkRow row, long through) => new(pairId,
        row.PairGeneration, row.SourceEpoch, row.Side, row.Domain, row.Identity, row.PathKey, row.WorkId, through);

    private static string RegistrationKey(WorkSelection selection, ChangeNotice notice, ObligationRole role,
        SyncedEntry? before) => string.Join("\n", notice.Side, selection.Domain, role, notice.Id ?? string.Empty,
        PathNames.KeyFor(notice.Side, notice.Path),
        notice.OldPath is null ? string.Empty : PathNames.KeyFor(notice.Side, notice.OldPath),
        before?.LocalKey ?? string.Empty, before?.DropboxKey ?? string.Empty);

    private IEnumerable<WorkRow> Membership(WorkSelection selection, WorkRow anchor) =>
        _work.Where(row => !row.IssueOrigin && row.PairId == selection.PairId &&
            row.PairGeneration == selection.PairGeneration && row.Side == selection.Side &&
            row.SourceEpoch == selection.SourceEpoch && row.Domain == selection.Domain &&
            row.WorkId >= selection.AnchorWorkId && row.WorkId <= selection.ThroughWorkId &&
            row.Parent == anchor.Parent && row.ObligationKey == anchor.ObligationKey &&
            (row.Before is null || row.Before == anchor.Before) &&
            (selection.Identity is null
                ? row.Identity is null && row.WorkId == selection.AnchorWorkId && row.PathKey == selection.Key
                : row.Identity == selection.Identity))
            .OrderBy(row => row.WorkId);

    private void RequireIssueOnly(StateChange change)
    {
        if (change.Record.Count != 0 || change.StaleBindings.Count != 0 || change.ForgetBothSubtrees.Count != 0 || change.Renames.Count != 0 || change.ForgetSelf.Count != 0 ||
            change.ForgetSubtrees.Count != 0)
            throw new ArgumentException(null, nameof(change));
    }

    private void RequireRetryNotice(StateChange change)
    {
        RequireIssueOnly(change);
        if (change.Raise.Count != 1 || change.Close.Count != 0 || change.FirstRunAcknowledged.Count != 0 ||
            change.Raise[0].Kind != IssueKind.Failure ||
            change.Raise[0].Cause != TextKey.RequiredRepairRetry || change.Raise[0].Path != string.Empty)
            throw new ArgumentException(null, nameof(change));
    }

    private Issue? RetryNotice(string pairId) =>
        IssueRows(pairId).FirstOrDefault(issue => issue.Kind == IssueKind.Failure &&
            issue.Cause == TextKey.RequiredRepairRetry && issue.Path.Length == 0);

    private void RequireIssue(string pairId, Issue exact)
    {
        if (IssueRows(pairId).FirstOrDefault(issue => issue.Id == exact.Id) is not { } stored ||
            !stored.Matches(exact))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.RelevantEvidenceChanged);
    }

    private bool CloseExact(string pairId, Issue expected)
    {
        var issues = IssueRows(pairId);
        var stored = issues.FirstOrDefault(issue => issue.Id == expected.Id);
        if (stored is null || !stored.Matches(expected)) return false;
        return DeleteIssue(issues, pairId, stored);
    }

    private void CloseSupersededRetryNotice(string pairId, Issue notice, long expectedGeneration,
        SourceRow local, SourceRow dropbox, Massdex.SyncShark.V2.FailureRetryOwner.State expected)
    {
        var current = RequireSource(pairId, Side.Local);
        var remote = RequireSource(pairId, Side.Dropbox);
        if (current.Epoch != local.Epoch || current.Revision != local.Revision ||
            remote.Epoch != dropbox.Epoch || remote.Revision != dropbox.Revision ||
            _retries.GetValueOrDefault(pairId) != expected)
            throw new InvalidOperationException();
        CloseRetryProjection(pairId, notice.Id);
    }

    private void CloseFinishedRetryNotice(string pairId, Issue notice, long expectedGeneration)
    {
        if (Generation(pairId) != expectedGeneration || _retries.ContainsKey(pairId)) throw new InvalidOperationException();
        RequireSourceClear(pairId);
        CloseRetryProjection(pairId, notice.Id);
    }

    private void CloseRetryProjection(string pairId, long issueId)
    {
        var issues = IssueRows(pairId);
        if (issues.RemoveAll(issue => issue.Id == issueId && issue.Kind == IssueKind.Failure &&
                issue.Cause == TextKey.RequiredRepairRetry && issue.Path.Length == 0) == 0) return;
        _items.Remove(issueId);
        if (HasInvalidFolderJob(pairId, string.Empty))
            ReconcileInvalidFolderJob(pairId, string.Empty, null, issues, DateTimeOffset.UtcNow);
    }

    private void RequireSourceClear(string pairId)
    {
        if (Sources(pairId).Any(source => source.Obligation is not null)) throw new InvalidOperationException();
    }

    private void FinishObligation(string pairId, Side side, long generation, long epoch, long revision)
    {
        var source = RequireSource(pairId, side);
        if (Generation(pairId) != generation || source.Epoch != epoch || source.Revision != revision)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        source.Obligation = null;
    }

    private void RetireCoveredWork(string pairId, long generation, Side side, long epoch, long through)
    {
        var failures = IssueRows(pairId).Where(issue => issue.Kind == IssueKind.Failure)
            .Select(issue => PathNames.Key(issue.Path)).ToHashSet(StringComparer.Ordinal);
        var dirty = _work.Where(row => side == Side.Local && row.Side == Side.Local && !row.IssueOrigin &&
                row.PairId == pairId && row.WorkId <= through && row.PairGeneration <= generation &&
                row.Notice?.LocalContentDirty == true && failures.Contains(row.PathKey))
            .GroupBy(row => (row.Domain, row.Identity, row.PathKey)).Select(group => group.Max(row => row.WorkId)).ToHashSet();
        var claimed = new HashSet<long>();
        if (_passes.GetValueOrDefault(pairId)?.Claims is { } claims)
            foreach (var selection in claims.Values)
            {
                var anchor = _work.FirstOrDefault(row => row.WorkId == selection.AnchorWorkId && row.PairId == pairId);
                if (anchor is null) continue;
                claimed.UnionWith(Membership(selection, anchor).Concat(_work.Where(row =>
                    row.Parent == anchor.WorkId || PeerOwned(selection, anchor, row, selection.Peer))).Select(row => row.WorkId));
            }
        _work.RemoveAll(row => row.RenameContinuation is null && !row.IssueOrigin && row.PairId == pairId && row.WorkId <= through &&
            !claimed.Contains(row.WorkId) &&
            row.PairGeneration <= generation && row.Side == side && row.IssueId == 0 && !dirty.Contains(row.WorkId) &&
            !_pending.ContainsKey(pairId) && !Obligated(pairId) &&
            !_folders.Values.Any(operation => operation.State.Active ||
                operation.State.Phase is FolderRemovalPhase.Unknown or FolderRemovalPhase.Retained &&
                operation.State.RetirementDisposition is null));
    }

    private long EnsureIssueWork(string pairId, long generation, long sourceEpoch, long issueId, string path,
        IssueSubject subject, WorkSelection? selection, ItemRetryDispatch dispatch, PendingEffect? expectedPending)
    {
        if (dispatch == ItemRetryDispatch.LocalRecovery != (expectedPending is not null))
            throw new ArgumentException(null, nameof(expectedPending));
        if (Generation(pairId) != generation)
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        if (selection is not null)
        {
            RequireCurrent(selection);
            var anchor = RequireAnchor(selection);
            if (anchor.SourceEpoch != sourceEpoch) throw new InvalidOperationException();
            anchor.Dispatch = dispatch;
            anchor.PendingOperationId = expectedPending?.OperationId;
            anchor.PendingGeneration = expectedPending?.Generation;
            anchor.IssueId = issueId;
            anchor.Subject = subject;
            return anchor.WorkId;
        }
        var item = subject.Items.Where(member => PathNames.Same(member.Path, path)).MinBy(row => row.Side) ??
            throw new ArgumentException(null, nameof(path));
        var domain = Domain(pairId, item.Side) ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        var key = StateEncoding.IssueWorkKey(issueId, subject);
        var pathKey = PathNames.KeyFor(item.Side, path);
        var stored = _work.FirstOrDefault(row => row.PairId == pairId && row.IssueWorkKey == key);
        if (stored is not null)
        {
            if (stored.PairGeneration != generation || stored.SourceEpoch != sourceEpoch ||
                stored.Dispatch != dispatch || stored.PendingOperationId != expectedPending?.OperationId ||
                stored.PendingGeneration != expectedPending?.Generation || stored.Side != item.Side ||
                stored.Domain != domain || stored.PathKey != pathKey ||
                (stored.Identity ?? string.Empty) != item.Id)
                throw new InvalidOperationException();
            stored.IssueId = issueId;
            return stored.WorkId;
        }
        var row = new WorkRow
        {
            WorkId = ++_nextWorkId,
            PairId = pairId,
            PairGeneration = generation,
            SourceEpoch = sourceEpoch,
            Side = item.Side,
            Domain = domain,
            Notice = null,
            Before = null,
            Acquisition = _nextWorkId,
            IssueId = issueId,
            IssueOrigin = true,
            IssueWorkKey = key,
            Dispatch = dispatch,
            PendingOperationId = expectedPending?.OperationId,
            PendingGeneration = expectedPending?.Generation,
            Subject = subject,
            PathKey = pathKey,
            Identity = item.Id
        };
        _work.Add(row);
        return row.WorkId;
    }

    private string? Domain(string pairId, Side side) =>
        _pairs.TryGetValue(pairId, out var pair) && pair.Roots is { } roots
            ? side == Side.Local
                ? roots.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : roots.Dropbox.RootNamespaceId
            : null;

    private bool TryClaimLocked(ItemRetryCandidate expected, Guid registeredAttemptId)
    {
        if (!_pairs.ContainsKey(expected.PairId) ||
            Generation(expected.PairId) != expected.Token.PairGeneration)
            return false;
        var issue = IssueRows(expected.PairId).FirstOrDefault(stored =>
            stored.Id == expected.Token.IssueId);
        if (issue is null || issue.Kind != IssueKind.Failure ||
            !IssuePolicy.IsLocalAccessRetryCause(issue.Cause) ||
            !_items.TryGetValue(issue.Id, out var candidate) ||
            candidate.Token.EpisodeId != expected.Token.EpisodeId ||
            candidate.Token.Revision != expected.Token.Revision ||
            !candidate.Token.Subject.HasSameItems(expected.Token.Subject))
            return false;
        var work = _work.FirstOrDefault(row => row.PairId == expected.PairId &&
            row.WorkId == expected.Token.WorkId);
        if (work is null || work.PairGeneration != expected.Token.PairGeneration ||
            work.SourceEpoch != expected.Token.SourceEpoch || work.Dispatch != expected.Dispatch ||
            work.PendingOperationId != expected.PendingOperationId ||
            work.PendingGeneration != expected.PendingGeneration ||
            !_sources.TryGetValue(SourceKey(expected.PairId, work.Side), out var source) ||
            source.Epoch != expected.Token.SourceEpoch)
            return false;
        if (work.Parent is { } parent && WorkExists(expected.PairId, parent)) return false;
        if (expected.Dispatch == ItemRetryDispatch.LocalRecovery &&
            (!_pending.TryGetValue(expected.PairId, out var pending) ||
             pending.OperationId != expected.PendingOperationId ||
             pending.Generation != expected.PendingGeneration))
            return false;
        return true;
    }

    private void Reclassify(Issue issue, TextKey cause)
    {
        if (issue.Cause == cause) return;
        if (!IssuePolicy.IsLocalAccessRetryCause(cause) || !IssuePolicy.IsLocalAccessRetryCause(issue.Cause))
            throw new InvalidOperationException();
        foreach (var rows in _issues.Values)
        {
            var at = rows.FindIndex(stored => stored.Id == issue.Id);
            if (at < 0) continue;
            rows[at] = rows[at] with { Cause = cause, At = DateTimeOffset.UtcNow, Dismissed = false, Ignored = false };
            return;
        }
    }

    private void AddItem(FolderOperation operation, long ordinal, string key, string path, Entry? entry,
        SyncedEntry? saved, FolderVerdict verdict, bool followUp, bool selected)
    {
        var kind = !selected ? FolderEffectKind.None : entry is null ? FolderEffectKind.StateOnly :
            entry.IsFolder ? FolderEffectKind.RecursiveFolder : FolderEffectKind.ConditionalFile;
        operation.Items.Add(new FolderRemovalItem(operation.State.OperationId, ordinal, 0,
            FolderRemovalItem.ProvenanceKind.Planned, path, key, entry, saved, verdict, followUp,
            followUp ? FolderRemovalItem.FollowUpStatus.Pending : FolderRemovalItem.FollowUpStatus.None,
            selected, kind,
            selected ? FolderRemovalItem.EffectStatus.Ready : FolderRemovalItem.EffectStatus.None, 0, null, null,
            null, FolderRemovalItem.RecoveryPhase.Planned, null, null, null, null, null));
    }

    private FolderOperation RequireFolder(string pairId, string operationId) =>
        _folders.TryGetValue((pairId, operationId), out var operation) ? operation :
            throw new InvalidOperationException();

    private SubmissionRecord RequireSubmission(string submissionId) =>
        _submissions.TryGetValue(submissionId, out var record) ? record :
            throw new InvalidOperationException();

    private bool InvalidContribution(FolderSubmission submission) =>
        submission.Phase == FolderSubmissionPhase.Accepted &&
        submission.Cause == TextKey.FailDropboxDeleteJobInvalid;

    private void AdvanceSubmission(SubmissionRecord record, FolderSubmission expected, FolderSubmission next,
        IReadOnlyList<FolderMemberResult> results)
    {
        if (next.SubmissionId != expected.SubmissionId || next.PairId != expected.PairId ||
            next.Generation != checked(expected.Generation + 1) ||
            expected.Phase == FolderSubmissionPhase.Terminal ||
            expected.JobId is not null && next.JobId != expected.JobId ||
            next.Phase == FolderSubmissionPhase.Armed ||
            next.Phase == FolderSubmissionPhase.Accepted && string.IsNullOrEmpty(next.JobId) ||
            next.Phase != FolderSubmissionPhase.Terminal && results.Count != 0)
            throw new ArgumentException(null, nameof(next));
        if (next.Phase != expected.Phase)
        {
            var terminal = next.Phase == FolderSubmissionPhase.Terminal;
            var outcomes = terminal ? results.ToLookup(result => result.Ordinal) : null;
            for (var index = 0; index < record.Effects.Count; index++)
            {
                var effect = record.Effects[index];
                var operation = RequireFolder(expected.PairId, effect.OperationId);
                if (!operation.State.Active) throw new InvalidOperationException();
                var itemIndex = operation.Items.FindIndex(item => item.Ordinal == effect.ItemOrdinal);
                var item = operation.Items[itemIndex];
                if (item.IsTerminal) continue;
                if (item.SubmissionId != expected.SubmissionId || item.SubmissionMember != index ||
                    item.EffectState is not (FolderRemovalItem.EffectStatus.Armed or
                        FolderRemovalItem.EffectStatus.Accepted or FolderRemovalItem.EffectStatus.Unknown))
                    throw new InvalidOperationException();
                FolderMemberResult? result = null;
                if (terminal)
                {
                    using var candidates = outcomes![index].GetEnumerator();
                    if (results.Count == record.Effects.Count && candidates.MoveNext()) result = candidates.Current;
                    if (result is null || candidates.MoveNext())
                        result = new FolderMemberResult.Unknown(index, next.Cause);
                }
                var state = next.Phase == FolderSubmissionPhase.Accepted
                    ? FolderRemovalItem.EffectStatus.Accepted
                    : result is FolderMemberResult.Receipt ? FolderRemovalItem.EffectStatus.Verified
                    : result is FolderMemberResult.KnownNoEffect ? FolderRemovalItem.EffectStatus.NoEffect
                    : FolderRemovalItem.EffectStatus.Unknown;
                var phase = result is FolderMemberResult.Receipt ? FolderRemovalItem.RecoveryPhase.Deleted
                    : state == FolderRemovalItem.EffectStatus.Unknown
                        ? FolderRemovalItem.RecoveryPhase.Unknown : item.Phase;
                operation.Items[itemIndex] = item with
                {
                    EffectState = state,
                    EffectResult = result,
                    EffectGeneration = checked(item.EffectGeneration + 1),
                    Generation = checked(item.Generation + 1),
                    Phase = phase,
                    Cause = next.Cause
                };
            }
        }
        record.State = next;
    }

    private void ValidateFolderChange(FolderOperation operation, string path, SyncedEntry? saved,
        StateChange change, IReadOnlyList<FolderRemovalItem> members, IReadOnlyList<SyncedEntry> synced)
    {
        if (change.Renames.Count != 0 || change.ForgetBothSubtrees.Count != 0) throw new ArgumentException(null, nameof(change));
        foreach (var record in change.Record)
            if (record.PathOn(operation.State.DeletingSide) != path)
                throw new ArgumentException(null, nameof(change));
        foreach (var root in change.ForgetSelf.Concat(change.ForgetSubtrees))
            if (root.Key != (saved?.KeyOn(root.Side) ?? PathNames.KeyFor(root.Side, path)))
                throw new ArgumentException(null, nameof(change));
        // The model mirrors Storage's transaction guard because it cannot execute the SQL-backed owner.
        if (change.Record.Count == 0 && change.ForgetSelf.Count == 0 && change.ForgetSubtrees.Count == 0) return;
        if (saved is null)
        {
            foreach (var record in change.Record)
                if (Find(Side.Local, record.LocalKey) is not null || Find(Side.Dropbox, record.DropboxKey) is not null)
                    throw new FolderStateRefusedException();
            foreach (var root in change.ForgetSelf)
                if (Find(root.Side, root.Key) is not null) throw new FolderStateRefusedException();
        }
        if (change.ForgetSubtrees.Count == 0)
        {
            if (saved is not null && !Verify(saved) && change.Record.Count != 0) throw new FolderStateRefusedException();
            return;
        }
        var scope = PathNames.KeyFor(operation.State.DeletingSide, path);
        var roots = change.ForgetSelf.Select(root => (root.Side, root.Key, Subtree: false))
            .Concat(change.ForgetSubtrees.Select(root => (root.Side, root.Key, Subtree: true))).ToArray();
        var counts = new long[roots.Length];
        foreach (var item in members.Where(item => PathNames.IsSelfOrUnderKey(
            PathNames.KeyFor(operation.State.DeletingSide, item.Path), scope)))
        {
            if (item.Saved is not { } before || !roots.Any(root => Matches(before, root)) || !Verify(before)) continue;
            for (var index = 0; index < roots.Length; index++)
            {
                if (!Matches(before, roots[index])) continue;
                if (item.Verdict != FolderVerdict.Removed || item.FollowUpState == FolderRemovalItem.FollowUpStatus.Pending ||
                    item.StageAllocation is not null || item.Stage is not null) throw new InvalidOperationException();
                counts[index]++;
            }
        }
        for (var index = 0; index < roots.Length; index++)
            if (synced.LongCount(row => Matches(row, roots[index])) != counts[index])
                throw new FolderStateRefusedException();
        if (saved is not null && change.Record.Count != 0) Verify(saved);

        bool Verify(SyncedEntry before)
        {
            var local = Find(Side.Local, before.LocalKey);
            var dropbox = Find(Side.Dropbox, before.DropboxKey);
            if (local is null && dropbox is null) return false;
            if (local != before || dropbox != before) throw new FolderStateRefusedException();
            return true;
        }

        SyncedEntry? Find(Side side, string key) => synced.FirstOrDefault(entry =>
            PathNames.Comparer.Equals(entry.KeyOn(side), key));

        static bool Matches(SyncedEntry entry, (Side Side, string Key, bool Subtree) root) => root.Subtree
            ? PathNames.IsSelfOrUnderKey(entry.KeyOn(root.Side), root.Key)
            : PathNames.Comparer.Equals(entry.KeyOn(root.Side), root.Key);
    }

    /// <summary>One issue per path: each side keeps its own member and the issue projects the survivors.</summary>
    private void MergeSkip(string pairId, string path, Side side, (TextKey Cause, IssueSubject Subject)? observed)
    {
        var issues = IssueRows(pairId);
        var index = issues.FindIndex(issue => issue.Path == path && issue.Kind == IssueKind.Skipped);
        var existing = index >= 0 ? issues[index] : null;
        var survivors = new List<(TextKey Cause, IssueSubject Subject)>(2);
        foreach (var listing in new[] { Side.Local, Side.Dropbox })
        {
            var memberKey = (pairId, listing, PathNames.KeyFor(listing, path));
            var member = _skipMembers.TryGetValue(memberKey, out var stored)
                ? stored : ((TextKey Cause, IssueSubject Subject)?)null;
            var wanted = listing == side
                ? (observed is { } value ? SideSlice(value.Cause, value.Subject, listing) : null)
                : member;
            if (wanted is { } keep)
            {
                survivors.Add(keep);
                _skipMembers[memberKey] = keep;
            }
            else _skipMembers.Remove(memberKey);
        }
        if (survivors.Count == 0)
        {
            if (existing is not null) issues.RemoveAt(index);
            return;
        }
        // A still-current displayed cause survives; otherwise the first surviving side in listing order wins.
        var displayed = existing?.Cause;
        var cause = displayed is { } current && survivors.Any(skip => skip.Cause == current)
            ? current : survivors[0].Cause;
        var subject = new IssueSubject(
            IssueSubject.DistinctItems(survivors.SelectMany(skip => skip.Subject.Items)),
            survivors.Select(skip => skip.Subject.Incoming).FirstOrDefault(incoming => incoming is not null));
        // The projection goes through the one Raise owner, exactly as IssueTable.MergeSkip does.
        Raise(pairId, issues, IssueKind.Skipped, cause, path, subject, DateTimeOffset.UtcNow);
    }

    private static (TextKey Cause, IssueSubject Subject)? SideSlice(TextKey cause, IssueSubject subject, Side side)
    {
        var items = IssueSubject.DistinctItems(subject.Items.Where(item => item.Side == side));
        return items.Length == 0 ? null : (cause, new IssueSubject(items, subject.Incoming));
    }

    private void ResetScan(string pairId, bool completed = false)
    {
        _resumedHistory.RemoveWhere(key => key.PairId == pairId);
        _incrementalHistory.RemoveWhere(key => key.PairId == pairId);
        _scan.RemoveAll(row => row.PairId == pairId);
        _skipTouched.RemoveWhere(entry => entry.PairId == pairId);
        if (completed) foreach (var scan in _scans.Keys.Where(key => key.PairId == pairId).ToArray()) _scans.Remove(scan);
        else
        {
            foreach (var scan in _scans.Keys.Where(key => key.PairId == pairId).ToArray()) _scans.Remove(scan);
            _deletedNames.Remove(pairId);
        }
    }

    private static bool QueueableRecord(StateChange change) =>
        change.Record.Count != 0 && !change.Record.Any(entry => entry.IsFolder) &&
        change.Renames.Count == 0 && change.ForgetSelf.Count == 0 && change.ForgetSubtrees.Count == 0 && change.ForgetBothSubtrees.Count == 0 &&
        change.Raise.Count == 0 && !change.Close.Any(closure =>
            closure.Kinds.Any(kind => !IssueClosure.AfterHeldInSync.Contains(kind)));

    /// <summary>Applies one change on copies and assigns them only after it succeeds, as one transaction.</summary>
    private List<Issue> ApplyAtomic(string pairId, StateChange change)
    {
        var synced = SyncedRows(pairId).ToList();
        var issues = IssueRows(pairId).ToList();
        var scan = _scan.ToList();
        HashSet<(Side Side, string Path, string Id)> released = [];
        var spent = new HashSet<string>(_spentDeletedNames.GetValueOrDefault(pairId) ?? [], StringComparer.Ordinal);
        var raised = ApplyCommit(synced, issues, scan, spent, pairId, change, DateTimeOffset.UtcNow, released);
        PublishReleased(pairId, released);
        _spentDeletedNames[pairId] = spent;
        _synced[pairId] = synced;
        _issues[pairId] = issues;
        _scan.Clear();
        _scan.AddRange(scan);
        return raised;
    }

    private List<Issue> ApplyCommit(List<SyncedEntry> synced, List<Issue> issues, List<ScanRow> scan,
        HashSet<string> spent, string pairId, StateChange change, DateTimeOffset now, HashSet<(Side Side, string Path, string Id)> released)
    {
        // Section 3.5 order: renames, forgets, records, acknowledged first-run, closes, raises.
        foreach (var rename in change.Renames) ApplyRename(synced, scan, pairId, rename);

        void Retire(Predicate<SyncedEntry> matches, bool releaseEndpoints = false)
        {
            synced.RemoveAll(entry =>
            {
                if (!matches(entry)) return false;
                if (releaseEndpoints && StateCommitter.ReleasedSide(entry, change) is { } side)
                    released.Add((side, entry.PathOn(side), entry.IdOn(side)));
                if (!change.Record.Any(record => record.LocalKey == entry.LocalKey) &&
                    !change.Renames.Any(rename => rename.After.LocalKey == entry.LocalKey))
                    spent.Add(PathNames.DropboxKey(entry.LocalPath));
                return true;
            });
        }
        foreach (var (side, key) in change.ForgetSelf)
        {
            Retire(row => SideKey(row, side) == key, releaseEndpoints: true);
            if (side == Side.Local) spent.Add(PathNames.DropboxKey(key));
        }

        foreach (var (side, key) in change.ForgetSubtrees)
            Retire(row => IsSelfOrDescendant(SideKey(row, side), key));

        foreach (var (localKey, dropboxKey) in change.ForgetBothSubtrees)
            Retire(row => PathNames.IsSelfOrUnderKey(row.LocalKey, localKey) &&
                PathNames.IsSelfOrUnderKey(row.DropboxKey, dropboxKey));

        foreach (var before in change.StaleBindings)
        {
            if (!change.Record.Any(entry => entry.IsRelocationOf(before)))
                throw new ArgumentException(null, nameof(change));
            Retire(row => row == before);
        }
        foreach (var entry in change.Record)
        {
            Retire(row => row.LocalKey == entry.LocalKey || row.DropboxKey == entry.DropboxKey, releaseEndpoints: true);
            synced.Add(entry);
        }

        foreach (var item in change.FirstRunAcknowledged)
        {
            if (item.Side != Side.Local) throw new ArgumentException(null, nameof(change));
            var at = issues.FindIndex(issue => issue.Path == item.Path && issue.Kind == IssueKind.Failure);
            if (at < 0 || issues[at].Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None ||
                !issues[at].Subject.Items.Contains(item))
                continue;
            issues[at] = issues[at] with
            {
                Subject = issues[at].Subject with
                {
                    FirstRunDeletion = IssueSubject.FirstRunDeletionKind.None
                }
            };
        }

        foreach (var (path, kinds) in change.Close)
            foreach (var kind in kinds)
                if (issues.FirstOrDefault(issue => issue.Path == path && issue.Kind == kind) is { } closed)
                    DeleteIssue(issues, pairId, closed);

        var raised = new List<Issue>();
        foreach (var (kind, cause, path, subject) in change.Raise)
        {
            var issueId = Raise(pairId, issues, kind, cause, path, subject, now);
            raised.Add(issues.First(issue => issue.Id == issueId));
        }
        return raised;
    }


    private void PublishReleased(string pairId, HashSet<(Side Side, string Path, string Id)> released)
    {
        foreach (var endpoint in released)
            if (!_pairs.ContainsKey(pairId) || !_sources.ContainsKey(SourceKey(pairId, endpoint.Side)) ||
                Domain(pairId, endpoint.Side) is null)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        foreach (var endpoint in released)
        {
            AddWork(pairId, Generation(pairId), RequireSource(pairId, endpoint.Side).Epoch, endpoint.Side,
                Domain(pairId, endpoint.Side)!,
                new ChangeNotice(endpoint.Side, ChangeKind.Changed, endpoint.Path, null, endpoint.Id, null, false, false),
                null, null, null);
            if (_passes.TryGetValue(pairId, out var pass) && pass.Generation == Generation(pairId)) pass.Released = true;
        }
    }

    private static void ApplyRename(List<SyncedEntry> synced, List<ScanRow> scan, string pairId, RenameApplied rename)
    {
        var before = rename.Before;
        var after = rename.After;
        var hasCounterpart = StateCommitter.Counterpart(rename, out var counterpartSide, out var counterpartOldPath,
            out var counterpartNewPath);
        var owners = RenameOwners(synced, before, after).ToArray();
        if (owners.Length != 0)
        {
            var side = owners.Any(owner => owner.LocalKeyMatched) ? Side.Local : Side.Dropbox;
            throw new PathOccupiedException(after.PathOn(side), side);
        }
        if (before is null)
        {
            synced.Add(after);
            // A folder move with no saved record still rebases the descendants already saved under it (as StateCommitter).
            if (after.IsFolder && hasCounterpart)
            {
                if (counterpartSide == Side.Local)
                    RebaseLocal(synced, PathNames.Key(counterpartOldPath), PathNames.Key(counterpartNewPath),
                        counterpartOldPath, counterpartNewPath);
                else
                    RebaseDropbox(synced, PathNames.DropboxKey(counterpartOldPath),
                        PathNames.DropboxKey(counterpartNewPath), counterpartOldPath, counterpartNewPath);
            }
        }
        else
        {
            var index = synced.FindIndex(row => row.LocalKey == before.LocalKey);
            if (index >= 0)
                synced[index] = synced[index] with
                {
                    LocalKey = after.LocalKey,
                    LocalPath = after.LocalPath,
                    DropboxKey = after.DropboxKey,
                    DropboxPath = after.DropboxPath,
                    LocalId = after.LocalId,
                    DropboxId = after.DropboxId,
                };
            if (before.IsFolder)
            {
                RebaseLocal(synced, before.LocalKey, after.LocalKey, before.LocalPath, after.LocalPath);
                RebaseDropbox(synced, before.DropboxKey, after.DropboxKey, before.DropboxPath, after.DropboxPath);
            }
        }

        if (hasCounterpart)
            MoveScanSubtree(scan, pairId, counterpartSide, counterpartOldPath, counterpartNewPath);
    }

    private static void RebaseLocal(List<SyncedEntry> synced, string oldKey, string newKey, string oldPath, string newPath)
    {
        for (var index = 0; index < synced.Count; index++)
        {
            var row = synced[index];
            if (!IsDescendant(row.LocalKey, oldKey)) continue;
            synced[index] = row with
            {
                LocalKey = newKey + row.LocalKey[oldKey.Length..],
                LocalPath = newPath + row.LocalPath[oldPath.Length..],
            };
        }
    }

    private static void RebaseDropbox(List<SyncedEntry> synced, string oldKey, string newKey, string oldPath, string newPath)
    {
        for (var index = 0; index < synced.Count; index++)
        {
            var row = synced[index];
            if (!IsDescendant(row.DropboxKey, oldKey)) continue;
            synced[index] = row with
            {
                DropboxKey = newKey + row.DropboxKey[oldKey.Length..],
                DropboxPath = newPath + row.DropboxPath[oldPath.Length..],
            };
        }
    }

    private static void MoveScanSubtree(List<ScanRow> scan, string pairId, Side side, string oldPath, string newPath)
    {
        var oldOwnKey = PathNames.Key(oldPath);
        var newOwnKey = PathNames.Key(newPath);
        var oldKey = side == Side.Local ? oldOwnKey : PathNames.DropboxKey(oldPath);
        var newKey = side == Side.Local ? newOwnKey : PathNames.DropboxKey(newPath);
        for (var index = 0; index < scan.Count; index++)
        {
            var row = scan[index];
            if (row.PairId != pairId || row.Side != side || !IsSelfOrDescendant(row.OwnKey, oldOwnKey)) continue;
            scan[index] = row with
            {
                OwnKey = newOwnKey + row.OwnKey[oldOwnKey.Length..],
                Key = newKey + row.Key[oldKey.Length..],
                Path = newPath + row.Path[oldPath.Length..],
            };
        }
    }

    /// <summary>Continues an identical issue, refreshes a changed proof or marker, and otherwise reopens or inserts
    /// it unless its owner still retains the row (IssueTable.Raise).</summary>
    private long Raise(string pairId, List<Issue> issues, IssueKind kind, TextKey cause, string path,
        IssueSubject subject, DateTimeOffset now)
    {
        var index = issues.FindIndex(issue => issue.Path == path && issue.Kind == kind);
        var existing = index >= 0 ? issues[index] : null;
        if (existing is not null && kind == IssueKind.Failure && SharesLocalItem(existing.Subject, subject))
        {
            // Unfinished first-run work outlives the failure carrying it; a different local item inherits nothing.
            if (subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.None ||
                existing.Subject.FirstRunDeletion == IssueSubject.FirstRunDeletionKind.PreserveWithHistoryNotice)
                subject = subject with { FirstRunDeletion = existing.Subject.FirstRunDeletion };
            if (cause is TextKey.SkipUnreadable or TextKey.FailFirstSyncCheckWaiting) cause = existing.Cause;
        }
        if (existing is not null && existing.Cause == cause && existing.Subject.HasSameItems(subject))
        {
            // A continuing issue keeps its stored items and order but refreshes the local proof and marker.
            if (existing.Subject.LocalHash != subject.LocalHash ||
                existing.Subject.FirstRunDeletion != subject.FirstRunDeletion)
            {
                existing = existing with
                {
                    Subject = existing.Subject with
                    {
                        LocalHash = subject.LocalHash, FirstRunDeletion = subject.FirstRunDeletion
                    }
                };
                issues[index] = existing;
            }
            return existing.Id;
        }
        var invalid = kind == IssueKind.Failure && HasInvalidFolderJob(pairId, path);
        var protectedIncoming = kind == IssueKind.Failure &&
            (cause == TextKey.RequiredRepairRetry || IssuePolicy.IsLocalAccessRetryCause(cause) ||
             subject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None);
        if (invalid && !protectedIncoming)
        {
            ReconcileInvalidFolderJob(pairId, path, existing, issues, now);
            return issues.First(issue => issue.Path == path && issue.Kind == kind).Id;
        }
        // Reporting the failed attempt refreshes the same notice; nothing else re-causes a row its owner still holds.
        if (existing is not null &&
            (existing.Cause, cause) is not (TextKey.RequiredRepairRetry, TextKey.RequiredRepairRetry) &&
            existing.Cause != TextKey.FailDropboxDeleteJobInvalid &&
            HoldsResponsibility(pairId, existing))
            return existing.Id;
        if (existing is not null)
        {
            ReplaceExact(issues, existing, cause, subject, now);
            return existing.Id;
        }
        var issue = new Issue(++_nextIssueId, kind, cause, path, subject, now, false);
        issues.Add(issue);
        return issue.Id;
    }

    private void ReplaceExact(List<Issue> issues, Issue existing, TextKey cause, IssueSubject subject,
        DateTimeOffset now)
    {
        var at = issues.FindIndex(issue => issue.Id == existing.Id);
        if (at < 0) return;
        issues[at] = existing with { Cause = cause, Subject = subject, At = now, Dismissed = false, Ignored = false };
        _items.Remove(existing.Id);
    }

    private void ReconcileInvalidFolderJob(string pairId, string path, Issue? issue, List<Issue> issues,
        DateTimeOffset now)
    {
        var invalid = HasInvalidFolderJob(pairId, path);
        if (issue is not null && (HasFirstRunWork(issue) ||
            (issue.Cause != TextKey.FailDropboxDeleteJobInvalid && HoldsResponsibility(pairId, issue)))) return;
        if (!invalid)
        {
            if (issue is { Cause: TextKey.FailDropboxDeleteJobInvalid } cleared)
                DeleteIssue(issues, pairId, cleared);
            return;
        }
        if (issue is { Cause: TextKey.FailDropboxDeleteJobInvalid }) return;
        var root = ActiveFolderRoot(pairId, path) ?? throw new InvalidOperationException();
        var subject = new IssueSubject(
            [new IssueItem(root.DeletingSide, root.RootEntry.Path, root.RootEntry.Id)], null);
        if (issue is not null)
            ReplaceExact(issues, issue, TextKey.FailDropboxDeleteJobInvalid, subject, now);
        else
            issues.Add(new Issue(++_nextIssueId, IssueKind.Failure, TextKey.FailDropboxDeleteJobInvalid, path,
                subject, now, false));
    }

    private FolderRemovalState? ActiveFolderRoot(string pairId, string path) =>
        _folders.Where(entry => entry.Key.PairId == pairId && entry.Value.State.Active &&
            entry.Value.State.RootEntry.Path == path)
            .OrderBy(entry => entry.Key.OperationId, StringComparer.Ordinal)
            .Select(entry => entry.Value.State).FirstOrDefault();

    private bool RetainedByOwner(string pairId, Issue issue) =>
        HasFirstRunWork(issue) || HoldsResponsibility(pairId, issue);

    private static bool HasFirstRunWork(Issue issue) => issue.Kind == IssueKind.Failure &&
        issue.Subject.FirstRunDeletion != IssueSubject.FirstRunDeletionKind.None;

    private static bool MayHoldResponsibility(Issue issue) => issue.Kind == IssueKind.Failure &&
        (issue.Cause is TextKey.RequiredRepairRetry or TextKey.FailDropboxDeleteJobInvalid ||
         IssuePolicy.IsLocalAccessRetryCause(issue.Cause));

    private bool HoldsResponsibility(string pairId, Issue issue)
    {
        if (!MayHoldResponsibility(issue)) return false;
        if (issue.Cause == TextKey.FailDropboxDeleteJobInvalid) return HasInvalidFolderJob(pairId, issue.Path);
        if (issue.Cause == TextKey.RequiredRepairRetry) return RetryNoticeDebt(pairId, issue);
        return _items.ContainsKey(issue.Id) && _items[issue.Id] is not null;
    }

    private bool RetryNoticeDebt(string pairId, Issue issue) =>
        issue is { Kind: IssueKind.Failure, Cause: TextKey.RequiredRepairRetry } && issue.Path.Length == 0 &&
        (Sources(pairId).Any(source => source.Obligation is not null) || _retries.ContainsKey(pairId));

    private bool DeleteIssue(List<Issue> issues, string pairId, Issue issue)
    {
        if (issue.Kind != IssueKind.Failure)
        {
            foreach (var listing in new[] { Side.Local, Side.Dropbox })
                _skipMembers.Remove((pairId, listing, PathNames.KeyFor(listing, issue.Path)));
            issues.Remove(issue);
            return true;
        }
        var invalid = HasInvalidFolderJob(pairId, issue.Path);
        if (HasFirstRunWork(issue) || (issue.Cause == TextKey.FailDropboxDeleteJobInvalid
            ? invalid : HoldsResponsibility(pairId, issue))) return false;
        issues.Remove(issue);
        _items.Remove(issue.Id);
        if (invalid) ReconcileInvalidFolderJob(pairId, issue.Path, null, issues, issue.At);
        return true;
    }

    private static bool SharesLocalItem(IssueSubject left, IssueSubject right) =>
        left.Items.Any(item => item.Side == Side.Local && right.Items.Contains(item));

    private void AddScan(string pairId, Guid scanId, Side side, Listed item)
    {
        lock (_gate)
        {
            ReadScan(pairId, scanId);
            var ownKey = PathNames.Key(item.Path);
            if (item.Entry is not { } entry)
            {
                if (side == Side.Dropbox && _incrementalHistory.Contains((pairId, scanId)))
                {
                    if (!_deletedDelta.TryGetValue(pairId, out var delta))
                        _deletedDelta[pairId] = delta = new HashSet<string>(StringComparer.Ordinal);
                    if (!DeletedNameSpent(pairId, ownKey)) delta.Add(ownKey);
                }
                else if (side == Side.Dropbox && _scans.TryGetValue((pairId, scanId), out var discovery) && discovery.Discovery)
                {
                    if (!_deletedNames.TryGetValue(pairId, out var names))
                        _deletedNames[pairId] = names = new HashSet<string>(StringComparer.Ordinal);
                    if (!DeletedNameSpent(pairId, ownKey)) names.Add(ownKey);
                }
                _scan.RemoveAll(row => row.PairId == pairId && row.ScanId == scanId && row.Side == side && IsSelfOrDescendant(row.OwnKey, ownKey));
                return;
            }
            if (side == Side.Dropbox && _resumedHistory.Contains((pairId, scanId)) && _deletedNames.TryGetValue(pairId, out var deleted)) deleted.Remove(ownKey);
            if (!entry.IsFolder)
                _scan.RemoveAll(row => row.PairId == pairId && row.ScanId == scanId && row.Side == side && IsDescendant(row.OwnKey, ownKey));
            var scanned = new ScanRow(pairId, scanId, side, ownKey,
                side == Side.Local ? ownKey : PathNames.DropboxKey(item.Path), item.Path, entry.IsFolder,
                entry.Size, entry.Modified.UtcTicks, entry.Id, entry.Revision, entry.ContentHash, entry.Skip);
            var index = _scan.FindIndex(row => row.PairId == pairId && row.ScanId == scanId && row.Side == side && row.OwnKey == ownKey);
            if (index >= 0) _scan[index] = scanned;
            else _scan.Add(scanned);
        }
    }

    private static List<RowParts> BuildRows(List<SyncedEntry> synced, List<ScanRow> scan)
    {
        var localKeys = new HashSet<string>(synced.Select(row => row.LocalKey), StringComparer.Ordinal);
        var dropboxKeys = new HashSet<string>(synced.Select(row => row.DropboxKey), StringComparer.Ordinal);
        var localScan = scan.Where(row => row.Side == Side.Local).ToList();
        var dropboxScan = scan.Where(row => row.Side == Side.Dropbox).ToList();
        var rows = new List<(string Key, bool Synced, RowParts Parts)>();

        foreach (var entry in synced)
            rows.Add((entry.LocalKey, true, Group(entry.LocalKey, entry,
                localScan.Where(row => row.Key == entry.LocalKey).ToList(),
                dropboxScan.Where(row => row.Key == entry.DropboxKey).ToList())));

        var freeKeys = localScan.Where(row => !localKeys.Contains(row.Key)).Select(row => row.Key)
            .Union(dropboxScan.Where(row => !dropboxKeys.Contains(row.Key)).Select(row => row.Key), StringComparer.Ordinal);
        foreach (var key in freeKeys)
            rows.Add((key, false, Group(key, null,
                localScan.Where(row => row.Key == key && !localKeys.Contains(row.Key)).ToList(),
                dropboxScan.Where(row => row.Key == key && !dropboxKeys.Contains(row.Key)).ToList())));

        return rows.OrderBy(row => row.Key, StringComparer.Ordinal).ThenByDescending(row => row.Synced)
            .Select(row => row.Parts).ToList();
    }

    private static RowParts Group(string key, SyncedEntry? synced, List<ScanRow> local, List<ScanRow> dropbox)
    {
        // The decoder takes the first distinct own key per side; every further one becomes an Extra candidate.
        var localRows = local.OrderBy(row => row.OwnKey, StringComparer.Ordinal).ToList();
        var dropboxRows = dropbox.OrderBy(row => row.OwnKey, StringComparer.Ordinal).ToList();
        Entry? firstLocal = null;
        Entry? firstDropbox = null;
        var extra = new List<Entry>();
        var seenLocal = new HashSet<string>(StringComparer.Ordinal);
        var seenDropbox = new HashSet<string>(StringComparer.Ordinal);
        for (var l = 0; l < Math.Max(localRows.Count, 1); l++)
            for (var d = 0; d < Math.Max(dropboxRows.Count, 1); d++)
            {
                if (l < localRows.Count && seenLocal.Add(localRows[l].OwnKey))
                {
                    var item = LocalEntry(localRows[l]);
                    if (firstLocal is null) firstLocal = item;
                    else extra.Add(item);
                }
                if (d < dropboxRows.Count && seenDropbox.Add(dropboxRows[d].OwnKey))
                {
                    var item = DropboxEntry(dropboxRows[d]);
                    if (firstDropbox is null) firstDropbox = item;
                    else extra.Add(item);
                }
            }
        return new RowParts(key, firstLocal, firstDropbox, synced, extra);
    }

    private static Entry LocalEntry(ScanRow row) => new(row.Path, row.IsFolder, row.Size,
        new DateTimeOffset(row.ModifiedTicks, TimeSpan.Zero), row.Id, "", "", row.Skip);

    private static Entry DropboxEntry(ScanRow row) => new(row.Path, row.IsFolder, row.Size,
        new DateTimeOffset(row.ModifiedTicks, TimeSpan.Zero), row.Id, row.Revision, row.ContentHash, row.Skip);

    private static string SideKey(SyncedEntry row, Side side) => side == Side.Local ? row.LocalKey : row.DropboxKey;

    private static bool IsSelfOrDescendant(string ownKey, string key) => ownKey == key || IsDescendant(ownKey, key);

    private static bool IsDescendant(string ownKey, string key) =>
        ownKey.StartsWith(key + PathNames.PathSeparator, StringComparison.Ordinal);

    private static int Depth(string path) => path.Split(PathNames.PathSeparator).Length;

    private sealed class SourceRow(Side side, long epoch, SourceProvenance? provenance, string? cursor,
        SourceObligation? obligation, long revision)
    {
        public Side Side { get; } = side;
        public long Epoch { get; set; } = epoch;
        public SourceProvenance? Provenance { get; set; } = provenance;
        public string? Cursor { get; set; } = cursor;
        public SourceObligation? Obligation { get; set; } = obligation;
        public long Revision { get; set; } = revision;
    }

    private sealed class WorkRow
    {
        public long WorkId { get; init; }
        public required string PairId { get; init; }
        public long PairGeneration { get; set; }
        public long SourceEpoch { get; set; }
        public Side Side { get; init; }
        public string Domain { get; set; } = string.Empty;
        public ChangeNotice? Notice { get; set; }
        public SyncedEntry? Before { get; set; }
        public long Acquisition { get; init; }
        public long? Parent { get; set; }
        public string? ObligationKey { get; set; }
        public long IssueId { get; set; }
        public bool IssueOrigin { get; init; }
        public bool DiscoveryRequired { get; set; }
        public RenameContinuation? RenameContinuation { get; set; }
        public string? IssueWorkKey { get; init; }
        public ItemRetryDispatch Dispatch { get; set; }
        public string? PendingOperationId { get; set; }
        public long? PendingGeneration { get; set; }
        public string PathKey { get; init; } = string.Empty;
        public string? OldPathKey { get; init; }
        public string? Identity { get; init; }
        public IssueSubject Subject { get; set; } = new([], null);
    }

    private sealed class FolderOperation
    {
        public long? RenameWork { get; set; }
        public FolderRemovalState State { get; set; } = null!;
        public List<FolderRemovalItem> Items { get; } = [];
    }

    private sealed class SubmissionRecord
    {
        public FolderSubmission State { get; set; } = null!;
        public List<FolderRemovalEffect> Effects { get; } = [];
    }

    private sealed record Pass(long Generation, long After, long Through, PathScope? Scope)
    {
        internal long NetRetired { get; set; }
        internal bool Released { get; set; }
        internal string[]? LocalEchoResponsibility { get; set; }
        internal HashSet<long> Attempted { get; } = [];
        internal Dictionary<long, WorkSelection> Claims { get; } = [];
    }

    private sealed record ScanRow(string PairId, Guid ScanId, Side Side, string OwnKey, string Key, string Path, bool IsFolder,
        long Size, long ModifiedTicks, string Id, string Revision, string ContentHash, string? Skip);

    private sealed record RowParts(string Key, Entry? Local, Entry? Dropbox, SyncedEntry? Synced, List<Entry> Extra);

    private sealed class ScanSink(InMemoryStateStore store, string pairId, Side side, Guid token) : IScanSink
    {
        public Task AddAsync(Listed item, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            store.AddScan(pairId, token, side, item);
            return Task.CompletedTask;
        }

        public Task CompleteAsync() => Task.CompletedTask;

        public Task AbortAsync()
        {
            lock (store._gate)
                if (store._scans.TryGetValue((pairId, token), out var scan))
                    store._scans.Remove((pairId, token));
            return Task.CompletedTask;
        }
    }
}
