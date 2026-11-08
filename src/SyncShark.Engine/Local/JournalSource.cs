namespace Massdex.SyncShark.Engine.Local;

/// <summary>One journal owner per active volume: classifies native pages and atomically publishes notices,
/// obligations and position. The host owns membership and serializes source operations and resource disposal.
/// The lifecycle stays together because its phases share one durable position and one uncommitted interval.</summary>
public sealed class JournalSource
{
    /// <summary>The immutable acquisition fences and generation-lived classification resources of one member.</summary>
    public sealed record Member(PairConfig Pair, long Generation, long Epoch, long Revision,
        UsnPaths Paths, DriveEvidence? Drive = null);

    private enum PageOutcome
    {
        Progress,
        Idle,
        Restarted,
        Stopped
    }

    private readonly string _volumeKey;
    private readonly string _rootPath;
    private readonly IStateStore _store;
    private readonly object _membershipGate;
    private readonly Func<IReadOnlyList<Member>?> _members;
    private readonly Action<IReadOnlyList<Member>, IReadOnlyList<string>?> _refreshMembers;
    // This vector covers the whole uncommitted interval, not merely the most recently read page.
    private IReadOnlyList<Member>? _classified;
    private JournalPosition? _position;
    private JournalPosition? _read;
    private JournalPosition? _replayEnd;
    private readonly int _pageBufferBytes;
    private ulong? _retentionSpan;
    private CancellationTokenSource? _stop;
    private Task? _run;
    private bool _acknowledged;
    // Recovery belongs to the volume's current members, not a refused member's old generation.
    private bool _refusedCatchUp;
    private ulong _firstUsn;
    private ulong _lowestValidUsn;
    private long _entryObservations;

    /// <summary>The source lifetime task, observed by its host without polling availability.</summary>
    public Task Completion => _run ?? Task.CompletedTask;
    internal CancellationToken LifetimeCancellation => _stop?.Token ?? CancellationToken.None;
    internal Action<string>? WorkCommitted { get; set; }

    /// <summary>Checks drive evidence and journal retention, then captures only a successful finite acquisition
    /// under the current member fence.</summary>
    public async Task<LocalCatchUp> CatchUpForPairAsync(string pairId, long generation, CancellationToken ct,
        IProgress<RunEvent>? progress = null)
    {
        var before = _entryObservations;
        progress?.Report(RunEvent.Stage(TextKey.StageJournal));
        if (!await CompareAsync(ct).ConfigureAwait(false)) throw RefuseCatchUp();
        var position = await CaptureFiniteAsync(null, ct, progress is null ? null : () =>
            progress.Report(RunEvent.Stage(TextKey.StageJournal, count: _entryObservations - before,
                inProgress: true))).ConfigureAwait(false) ?? throw RefuseCatchUp();
        progress?.Report(RunEvent.Stage(TextKey.StageJournal, ended: true, count: _entryObservations - before));
        lock (_membershipGate)
        {
            var member = _members()?.SingleOrDefault(value => value.Pair.PairId == pairId);
            if (member is null || member.Generation != generation || !Available)
                throw RefuseCatchUp();
            return new LocalCatchUp(pairId, generation, member.Epoch,
                new NtfsUsnCheckpoint(position.JournalId, checked((long)_firstUsn), checked((long)position.NextUsn),
                    checked((long)_lowestValidUsn)), _store.ReadWorkWatermark());
        }
    }

    /// <summary>Borrows the host's actual volume gate and immutable snapshot accessor. Null membership means
    /// closed. Refresh installs authoritative source fences for candidate IDs, or all members when IDs are null;
    /// it supports reentrant use under the same gate and never drains or performs native I/O.</summary>
    public JournalSource(string volumeKey, string rootPath, IStateStore store, JournalPosition? position,
        object membershipGate, Func<IReadOnlyList<Member>?> members,
        Action<IReadOnlyList<Member>, IReadOnlyList<string>?> refreshMembers, int pageBufferBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(membershipGate);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentNullException.ThrowIfNull(refreshMembers);
        _volumeKey = volumeKey;
        _rootPath = rootPath;
        _store = store;
        _position = position ?? store.ReadJournal(volumeKey);
        _read = _position;
        _membershipGate = membershipGate;
        _members = members;
        _refreshMembers = refreshMembers;
        _pageBufferBytes = pageBufferBytes;
    }

    /// <summary>The last durably committed journal boundary; acquisition resumes here after a restart.</summary>
    public JournalPosition? Position => _position;

    /// <summary>False after a native or durable failure; committed progress remains real even when a later
    /// refresh fails. Closed membership separately prevents a successful catch-up.</summary>
    public bool Available { get; private set; } = true;

    internal void Invalidate()
    {
        Available = false;
        DiscardRead();
    }

    private IOException Unavailable(NtfsUsnPageStatus status, int? nativeError)
    {
        Invalidate();
        return new IOException(Text.Get(TextKey.JournalSourceFailed,
            (TextArg.Reason, status.ToString()),
            (TextArg.Value, nativeError?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty)));
    }

    /// <summary>Checks drive evidence and journal retention. A missing or recreated journal publishes a fresh base
    /// with every member's recovery responsibility, never a clean listing. The pair entries call it themselves; no
    /// caller owes this step.</summary>
    private Task<bool> CompareAsync(CancellationToken ct) => Task.Run(() => Compare(ct), ct);

    /// <summary>Consumes immediately available pages and acknowledges their durable boundary. Closed or empty
    /// membership and unavailable acquisition return null rather than spinning or claiming coverage.</summary>
    public async Task<JournalPosition?> CatchUpAsync(CancellationToken ct)
    {
        while (Available)
        {
            var outcome = await ReadPageAsync(blocking: false, ct, frozen: null).ConfigureAwait(false);
            if (outcome is PageOutcome.Stopped) return null;
            if (outcome is PageOutcome.Progress or PageOutcome.Restarted) continue;
            ct.ThrowIfCancellationRequested();
            if (CommitRead(out _, frozen: null, refreshStale: true)) return _position;
        }
        return null;
    }

    /// <summary>Catches up before blocking, including after discarding a stale uncommitted interval.
    /// Session pause does not stop acquisition.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (await CatchUpAsync(ct).ConfigureAwait(false) is null) return;
            var blocking = true;
            while (Available && !ct.IsCancellationRequested)
            {
                var outcome = await ReadPageAsync(blocking, ct, frozen: null).ConfigureAwait(false);
                if (outcome is PageOutcome.Stopped) break;
                blocking = outcome is PageOutcome.Idle;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            Available = false;
            throw;
        }
    }

    /// <summary>Starts one background acquisition. The host observes and joins its lifetime through StopAsync.</summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (_run is { IsCompleted: false }) return Task.CompletedTask;
        _stop?.Dispose();
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _run = RunAsync(_stop.Token);
        return Task.CompletedTask;
    }

    /// <summary>Cancels and joins acquisition, publishing only still-valid irrelevant progress without rereading
    /// a stale suffix. Intentional cancellation does not make the next start permanently unavailable.</summary>
    public async Task StopAsync()
    {
        try
        {
            if (_stop is { } stop)
            {
                await stop.CancelAsync().ConfigureAwait(false);
                if (_run is { } run)
                {
                    try { await run.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
        }
        finally
        {
            _stop?.Dispose();
            _stop = null;
            _run = null;
            if (Available) CommitRead(out _);
            else DiscardRead();
        }
    }

    internal async Task<LocalCatchUp> CheckpointForPairAsync(string pairId, long generation, CancellationToken ct)
    {
        // A failed source needs a new pre-list base, never a replacement base after this Repair's listing.
        if (!Available) throw Stale();
        if (!await CompareAsync(ct).ConfigureAwait(false)) throw Stale();
        IReadOnlyList<Member> captured;
        lock (_membershipGate)
            captured = _members()?.ToArray() ?? throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        var position = await CheckpointAfterRepairAsync(captured, ct).ConfigureAwait(false) ??
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        lock (_membershipGate)
        {
            var member = _members()?.SingleOrDefault(value => value.Pair.PairId == pairId);
            if (member is null || member.Generation != generation || !Available)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            return new LocalCatchUp(pairId, generation, member.Epoch,
                new NtfsUsnCheckpoint(position.JournalId, checked((long)_firstUsn), checked((long)position.NextUsn),
                    checked((long)_lowestValidUsn)), _store.ReadWorkWatermark());
        }
    }

    /// <summary>Uses a current open snapshot captured after joined Repair effects, not its pre-list completion
    /// token. Only this checkpoint's acknowledged page consequences update a private working copy; foreign changes
    /// refuse without retry. Earlier committed pages remain durable on refusal.</summary>
    public Task<JournalPosition?> CheckpointAfterRepairAsync(IReadOnlyList<Member> frozen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        return CaptureFiniteAsync(frozen, ct);
    }

    private async Task<JournalPosition?> CaptureFiniteAsync(IReadOnlyList<Member>? frozen, CancellationToken ct,
        Action? observed = null)
    {
        var fences = frozen is null ? null : new List<Member>(frozen);
        lock (_membershipGate)
        {
            if (_members() is not { Count: > 0 } current || fences is not null && !SameAcquisition(fences, current))
            {
                DiscardRead();
                return null;
            }
            // An obsolete ordinary prefix predates this checkpoint; never bless it with the new entry vector.
            if (_classified is not null && !SameAcquisition(_classified, fences ?? current)) DiscardRead();
        }
        ct.ThrowIfCancellationRequested();
        if (!NtfsUsnJournal.TryReadState(_rootPath, out var journalId, out var firstUsn, out var throughNextUsn,
                out var lowestValidUsn, out _, out _, out var nativeError))
            throw Unavailable(NtfsUsnPageStatus.Unavailable, nativeError);
        if ((_read ?? _position) is not { } reading || reading.JournalId != journalId ||
            !InRetention(reading, firstUsn, throughNextUsn, lowestValidUsn))
        {
            DiscardRead();
            return null;
        }
        var outcome = PageOutcome.Idle;
        while (Available && _read!.NextUsn < throughNextUsn &&
               (outcome = await ReadPageAsync(blocking: false, ct, fences, throughNextUsn).ConfigureAwait(false)) is
               PageOutcome.Progress)
        {
            observed?.Invoke();
        }
        ct.ThrowIfCancellationRequested();
        if (!Available || _replayEnd is not null || outcome is PageOutcome.Stopped ||
            _read?.NextUsn != throughNextUsn && (fences is not null || outcome is not PageOutcome.Idle))
        {
            DiscardRead();
            return null;
        }
        if (CommitRead(out var staleStorage, fences, refreshStale: fences is null)) return _position;
        return fences is null && staleStorage && CommitRead(out _) ? _position : null;
    }

    private bool Compare(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<Member>? members;
        lock (_membershipGate) members = _members();
        if (members is null || members.Count == 0) return false;
        foreach (var member in members)
        {
            if (member.Drive is not { } current) continue;
            if (DriveEvidence.Change(member.Pair.Drive, current) is not { } cause) continue;
            _store.Commit(member.Pair.PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.DriveChange, cause, string.Empty, new IssueSubject([], null))]
            });
        }
        if (!NtfsUsnJournal.TryReadState(_rootPath, out var journalId, out var firstUsn, out var nextUsn,
                out var lowestValidUsn, out var maximumSize, out var allocationDelta, out var nativeError))
            throw Unavailable(NtfsUsnPageStatus.Unavailable, nativeError);
        _firstUsn = firstUsn;
        _lowestValidUsn = lowestValidUsn;
        ApplyRetention(maximumSize, allocationDelta);
        if (!Available || _position is not { } saved || saved.JournalId != journalId ||
            !InRetention(saved, firstUsn, nextUsn, lowestValidUsn))
        {
            PageOutcome outcome;
            do
            {
                outcome = Restart(ct);
            }
            while (outcome is PageOutcome.Progress);
            return outcome is PageOutcome.Restarted;
        }
        _read ??= saved;
        Available = true;
        return true;
    }

    private static bool InRetention(JournalPosition saved, ulong firstUsn, ulong nextUsn, ulong lowestValidUsn) =>
        saved.NextUsn >= Math.Max(firstUsn, lowestValidUsn) && saved.NextUsn <= nextUsn;

    // The native retained span, not a tuned delay, limits irrelevant progress; unusable limits commit each page.
    private void ApplyRetention(ulong maximumSize, ulong allocationDelta) =>
        _retentionSpan = maximumSize > allocationDelta ? maximumSize - allocationDelta : null;

    private PageOutcome Restart(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<Member>? members;
        lock (_membershipGate) members = _members();
        if (members is null || members.Count == 0) return PageOutcome.Stopped;
        if (!NtfsUsnJournal.TryReadState(_rootPath, out var journalId, out var firstUsn, out var nextUsn, out var lowestValidUsn,
                out var maximumSize, out var allocationDelta, out var nativeError))
            throw Unavailable(NtfsUsnPageStatus.Unavailable, nativeError);
        _firstUsn = firstUsn;
        _lowestValidUsn = lowestValidUsn;
        ApplyRetention(maximumSize, allocationDelta);
        ct.ThrowIfCancellationRequested();
        var repair = members.Select(member => member.Pair.PairId).ToHashSet(StringComparer.Ordinal);
        if (TryPublish(new JournalPosition(journalId, nextUsn), members,
                Array.Empty<(string PairId, ChangeNotice Notice)>(), repair, frozen: null, out var staleStorage))
        {
            Available = true;
            return PageOutcome.Restarted;
        }
        RestartRead(members, staleStorage);
        return PageOutcome.Progress;
    }

    private async Task<PageOutcome> ReadPageAsync(bool blocking, CancellationToken ct, List<Member>? frozen,
        ulong? throughNextUsn = null)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<Member> classified;
        lock (_membershipGate)
        {
            var current = _members();
            if (current is null || current.Count == 0) return PageOutcome.Stopped;
            if (frozen is not null && (!SameAcquisition(frozen, current) || !FencesCurrent(frozen)))
                return PageOutcome.Stopped;
            if (_classified is not null && !SameAcquisition(_classified, current))
            {
                if (frozen is not null) return PageOutcome.Stopped;
                DiscardRead();
                blocking = false;
            }
            classified = _classified ?? frozen ?? current;
            _classified = classified;
        }
        if ((_read ?? _position) is not { } reading)
            return frozen is null ? Restart(ct) : PageOutcome.Stopped;
        foreach (var member in classified)
            member.Paths.BeginPage();

        var page = await NtfsUsnJournal.ReadChangePageAsync(_rootPath, reading.JournalId, reading.NextUsn,
            _replayEnd?.NextUsn ?? throughNextUsn, waitForChange: blocking && _replayEnd is null,
            record: (fileId, parentFileId, usn, timestamp, reason, attributes) =>
            {
                ++_entryObservations;
                foreach (var member in classified)
                    member.Paths.Observe(fileId, parentFileId, usn, timestamp, reason, attributes);
            }, _pageBufferBytes, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        switch (page.Status)
        {
            case NtfsUsnPageStatus.Complete when page.ConsumedNextUsn == reading.NextUsn:
                if (_replayEnd is not null) throw Unavailable(NtfsUsnPageStatus.NoProgress, page.NativeError);
                return RetainOrCommit(new JournalPosition(reading.JournalId, page.ConsumedNextUsn),
                    classified, frozen, PageOutcome.Idle);
            case NtfsUsnPageStatus.Complete:
            case NtfsUsnPageStatus.More:
                return await ClassifyAsync(page, classified, reading, frozen, ct).ConfigureAwait(false);
            case NtfsUsnPageStatus.HistoryLost:
            case NtfsUsnPageStatus.JournalChanged:
                return frozen is null ? Restart(ct) : PageOutcome.Stopped;
            default:
                throw Unavailable(page.Status, page.NativeError);
        }
    }

    private async Task<PageOutcome> ClassifyAsync(NtfsUsnPage page, IReadOnlyList<Member> classified, JournalPosition reading,
        List<Member>? frozen, CancellationToken ct)
    {
        var next = new JournalPosition(reading.JournalId, page.ConsumedNextUsn);
        if (next.NextUsn <= reading.NextUsn)
            throw Unavailable(NtfsUsnPageStatus.NoProgress, page.NativeError);
        lock (_membershipGate)
        {
            if (!SameAcquisition(classified, _members()))
            {
                DiscardRead();
                return frozen is null ? PageOutcome.Progress : PageOutcome.Stopped;
            }
        }
        var notices = new List<(string PairId, ChangeNotice Notice)>();
        var repair = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await CollectAsync(classified, ct, notices, repair).ConfigureAwait(false);
        }
        catch (NtfsLinkEnumerationChangedException)
        {
            if (_replayEnd is not null) throw Unavailable(NtfsUsnPageStatus.Unavailable, page.NativeError);
            _replayEnd = next;
            _read = reading;
            return PageOutcome.Progress;
        }
        _replayEnd = null;
        ct.ThrowIfCancellationRequested();
        if (notices.Count == 0 && repair.Count == 0)
            return RetainOrCommit(next, classified, frozen, PageOutcome.Progress);
        if (TryPublish(next, classified, notices, repair, frozen, out var staleStorage))
        {
            foreach (var pairId in notices.Select(value => value.PairId).Union(repair, StringComparer.Ordinal))
                WorkCommitted?.Invoke(pairId);
            return PageOutcome.Progress;
        }
        if (frozen is not null) return PageOutcome.Stopped;
        RestartRead(classified, staleStorage);
        return PageOutcome.Progress;
    }

    // The failed-item receiver must preserve remaining notices; NeedsRepair is independent of that failure.
    internal Func<Member, ItemFailedException, CancellationToken, Task>? ReportItemFailureAsync { get; set; }

    private async Task CollectAsync(IReadOnlyList<Member> members, CancellationToken ct,
        List<(string PairId, ChangeNotice Notice)> notices, HashSet<string> repair)
    {
        var failures = new List<(Member Member, ItemFailedException Error)>();
        foreach (var member in members)
        {
            foreach (var notice in member.Paths.Resolve(ct, error => failures.Add((member, error))))
                notices.Add((member.Pair.PairId, notice));
            if (member.Paths.NeedsRepair) repair.Add(member.Pair.PairId);
        }
        foreach (var (member, failed) in failures)
        {
            if (ReportItemFailureAsync is not { } report) throw failed;
            await report(member, failed, ct).ConfigureAwait(false);
        }
    }

    private PageOutcome RetainOrCommit(JournalPosition next, IReadOnlyList<Member> classified,
        List<Member>? frozen, PageOutcome outcome)
    {
        lock (_membershipGate)
        {
            if (!SameAcquisition(classified, _members()))
            {
                DiscardRead();
                return frozen is null ? PageOutcome.Progress : PageOutcome.Stopped;
            }
            _read = next;
        }
        if (_retentionSpan is { } span && _position is { } position && next.NextUsn >= position.NextUsn &&
            next.NextUsn - position.NextUsn < span)
            return outcome;
        return CommitRead(out _, frozen, refreshStale: frozen is null) ? outcome :
            frozen is null ? PageOutcome.Progress : PageOutcome.Stopped;
    }

    private bool CommitRead(out bool staleStorage, List<Member>? frozen = null, bool refreshStale = false)
    {
        staleStorage = false;
        IReadOnlyList<Member> classified;
        lock (_membershipGate)
        {
            var current = _members();
            var expected = _classified ?? frozen ?? current;
            if (expected is null || expected.Count == 0 || !SameAcquisition(expected, current) ||
                frozen is not null && (!SameAcquisition(frozen, current) || !FencesCurrent(frozen)))
            {
                DiscardRead();
                return false;
            }
            if (_read is not { } read) return false;
            if (read == _position && _acknowledged && !_refusedCatchUp) return true;
            classified = expected;
        }
        if (TryPublish(_read!, classified, Array.Empty<(string PairId, ChangeNotice Notice)>(),
                repair: null, frozen, out staleStorage)) return true;
        if (refreshStale) RestartRead(classified, staleStorage);
        else DiscardRead();
        return false;
    }

    // Keep admission, commit and authoritative candidate adoption under one gate so a foreign transition cannot
    // be mistaken for this page's consequences. Native I/O and ordinary stale refresh are outside this span.
    private bool TryPublish(JournalPosition next, IReadOnlyList<Member> classified,
        IEnumerable<(string PairId, ChangeNotice Notice)> notices, IReadOnlySet<string>? repair,
        List<Member>? frozen, out bool staleStorage)
    {
        staleStorage = false;
        lock (_membershipGate)
        {
            var installed = _members();
            if (!SameAcquisition(classified, installed)) return false;
            var fences = classified.Select(member => (member.Pair.PairId, member.Generation, member.Epoch,
                member.Revision,
                LostInterval: repair?.Contains(member.Pair.PairId) == true)).ToArray();
            long? result;
            try
            {
                result = _store.AcquireJournalPage(_volumeKey, _position, next, fences, notices);
            }
            catch (Exception)
            {
                // Readback preserves durable progress, but position equality alone cannot certify this failure.
                Available = false;
                _position = _store.ReadJournal(_volumeKey);
                DiscardRead();
                throw;
            }
            if (result is null)
            {
                staleStorage = true;
                return false;
            }
            _position = next;
            _acknowledged = true;
            DiscardRead();
            var candidates = fences.Where(member => member.LostInterval)
                .Select(member => member.PairId).ToArray();
            if (candidates.Length != 0)
            {
                try
                {
                    _refreshMembers(installed!, candidates);
                    if (frozen is not null && !AdoptCandidates(frozen, candidates)) return false;
                }
                catch (Exception)
                {
                    Available = false;
                    throw;
                }
            }
            if (_refusedCatchUp)
            {
                _refusedCatchUp = false;
                foreach (var member in _members()!) WorkCommitted?.Invoke(member.Pair.PairId);
            }
            return true;
        }
    }

    // Reuse the host's K authoritative reads; the private working copy never rereads or guesses revisions.
    private bool AdoptCandidates(List<Member> frozen, IReadOnlyList<string> candidates)
    {
        var current = _members();
        if (current is null) return false;
        var byPair = new Dictionary<string, Member>(current.Count, StringComparer.Ordinal);
        foreach (var member in current)
            if (!byPair.TryAdd(member.Pair.PairId, member)) return false;
        var candidateIds = candidates.ToHashSet(StringComparer.Ordinal);
        var updated = new List<Member>(frozen.Count);
        foreach (var member in frozen)
        {
            if (candidateIds.Contains(member.Pair.PairId))
            {
                if (!byPair.TryGetValue(member.Pair.PairId, out var after) || after.Epoch != member.Epoch)
                    return false;
                updated.Add(member with { Revision = after.Revision });
            }
            else updated.Add(member);
        }
        if (!SameAcquisition(updated, current)) return false;
        for (var i = 0; i < frozen.Count; i++) frozen[i] = updated[i];
        return true;
    }

    private void RestartRead(IReadOnlyList<Member> expected, bool staleStorage)
    {
        DiscardRead();
        if (!staleStorage) return;
        _refreshMembers(expected, null);
        _position = _store.ReadJournal(_volumeKey);
        _acknowledged = false;
        _read = _position;
    }

    private void DiscardRead()
    {
        _replayEnd = null;
        _read = _position;
        _classified = null;
    }

    private static bool SameAcquisition(IReadOnlyList<Member> expected, IReadOnlyList<Member>? current)
    {
        if (current is null || expected.Count != current.Count) return false;
        var byPair = new Dictionary<string, Member>(current.Count, StringComparer.Ordinal);
        foreach (var member in current)
            if (!byPair.TryAdd(member.Pair.PairId, member)) return false;
        foreach (var before in expected)
        {
            if (!byPair.Remove(before.Pair.PairId, out var after)) return false;
            if (after.Generation != before.Generation || after.Epoch != before.Epoch ||
                after.Revision != before.Revision || !ReferenceEquals(after.Paths, before.Paths) || !ReferenceEquals(after.Pair, before.Pair) ||
                !ReferenceEquals(after.Drive, before.Drive)) return false;
        }
        return true;
    }

    private LiveAdmissionStaleException RefuseCatchUp()
    {
        lock (_membershipGate) _refusedCatchUp = true;
        return Stale();
    }

    private static LiveAdmissionStaleException Stale() =>
        new(LiveAdmissionStaleReason.CoverageChanged);

    private bool FencesCurrent(IReadOnlyList<Member> fences)
    {
        foreach (var member in fences)
        {
            if (_store.ReadSource(member.Pair.PairId, Side.Local) is not { } source ||
                source.Epoch != member.Epoch || source.Revision != member.Revision) return false;
        }
        return true;
    }

}
