using Massdex.SyncShark.V2;

namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>The pair's serial Dropbox feed (AUTOSYNC 3.2/3.10.1, engine 5.7): cursor continuation, ordered page
/// handoff in one source transaction, one outstanding longpoll, provider backoff and one shared listener-reconnect
/// deadline. It owns no inventory, Repair cursor, retry formula or polling timer and makes no sync decisions.</summary>
/// <remarks>The reconnect and wake owners live in Run, so this adapter receives their exact operations as delegates;
/// the module direction (Dropbox depends on Contracts only) is unchanged and no second reconnect loop is added.</remarks>
internal sealed class DropboxFeed(
    IStateStore store,
    PairConfig pair,
    long pairGeneration,
    PathScope scope,
    DropboxApi api,
    DropboxListing listing,
    FailureRetryOwner.BackoffSettings backoff,
    TimeProvider time,
    Func<(string AccountId, string RouteName, string SourceIdentity), TimeSpan,
        Func<CancellationToken, Task>, IDisposable> scheduleReconnect,
    Func<Action> openOutage,
    string sourceIdentity)
{
    /// <summary>Establishes the current source base, respecting the shared listener reconnect wait.</summary>
    internal async Task EnsureSourceBaseAsync(CancellationToken ct)
    {
        await EnterFiniteAsync(ct).ConfigureAwait(false);
        try { await EnsureCurrentAsync(ct, finite: true).ConfigureAwait(false); }
        finally { EndFiniteOutage(); _sourceGate.Release(); }
    }

    internal async Task<DropboxCatchUp> CatchUpAsync(CancellationToken ct)
    {
        await EnterFiniteAsync(ct).ConfigureAwait(false);
        try
        {
            if (!await EnsureCurrentAsync(ct, finite: true).ConfigureAwait(false) ||
                !await DrainAsync(ct, finite: true).ConfigureAwait(false) || _terminal is null)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
            return _terminal;
        }
        finally { EndFiniteOutage(); _sourceGate.Release(); }
    }

    internal void ValidateSourceBase(SourceProvenance? provenance, string? cursor)
    {
        if (pair.Roots?.Dropbox is not { } binding || provenance is null || cursor is null || !Matches(provenance, binding))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
    }

    private const int SingleHolder = 0 + 1;

    private readonly SemaphoreSlim _sourceGate = new(SingleHolder);
    private readonly object _turnGate = new();
    private CancellationTokenSource? _backgroundTurn;
    private int _finiteWaiters;

    private async Task EnterFiniteAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _finiteWaiters);
        try
        {
            Task waiting;
            Task cancellation;
            lock (_turnGate)
            {
                waiting = _sourceGate.WaitAsync(ct);
                cancellation = _backgroundTurn?.CancelAsync() ?? Task.CompletedTask;
            }
            await waiting.ConfigureAwait(false);
            try { await cancellation.ConfigureAwait(false); }
            catch { _sourceGate.Release(); throw; }
        }
        finally { Interlocked.Decrement(ref _finiteWaiters); }
    }
    private DropboxCatchUp? _terminal;
    private long _committedWatermark;
    private bool _pageHadNotices;
    internal Action? WorkCommitted { get; set; }

    internal static readonly ListOptions FeedOptions =
        new(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: true);

    private readonly FailureRetryOwner.Session _session = FailureRetryOwner.SessionFor(sourceIdentity);
    private IDisposable? _reconnect;
    private ContinuationCycle _cycle = new();
    // The Run owner's one outage contribution, opened by the first transient failure and closed on recovery or exit.
    private Action? _outage;
    private bool _running;

    /// <summary>Runs serial acquisition until cancellation or until the stored source no longer belongs to this
    /// pair generation. A cursor signalled by a longpoll is continued before the next wait.</summary>
    internal async Task RunAsync(CancellationToken ct)
    {
        lock (_turnGate) _running = true;
        try
        {
            var drain = true;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await _sourceGate.WaitAsync(ct).ConfigureAwait(false);
                using var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
                try
                {
                    lock (_turnGate)
                    {
                        _backgroundTurn = turn;
                        if (Volatile.Read(ref _finiteWaiters) != 0) turn.Cancel();
                    }
                    if (!await EnsureCurrentAsync(turn.Token).ConfigureAwait(false)) return;
                    if (drain && !await DrainAsync(turn.Token).ConfigureAwait(false)) continue;
                    drain = await LongpollAsync(turn.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (turn.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    drain = true;
                }
                finally
                {
                    lock (_turnGate) _backgroundTurn = null;
                    _sourceGate.Release();
                }
            }
        }
        finally
        {
            WithdrawReconnect();
            lock (_turnGate) _running = false;
            EndOutage();
        }
    }

    /// <summary>Reads the committed cursor, or installs a new epoch at a fresh cursor when the binding, arguments or
    /// stored provenance no longer match. A newer pair generation ends this feed instead of being adopted here.</summary>
    private async Task<bool> EnsureCurrentAsync(CancellationToken ct, bool finite = false)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var binding = pair.Roots?.Dropbox
                ?? throw new EngineRequestException(TextKey.RootUnbound, (TextArg.Pair, pair.PairId));
            var source = store.ReadSource(pair.PairId, Side.Dropbox);
            if (source is { } current)
            {
                if (current.Provenance is { } generation && generation.PairGeneration != pairGeneration) return false;
                if (current.Provenance is { } installed && current.Cursor is not null && Matches(installed, binding))
                    return true;
            }
            await InstallAsync(binding, source, ct, finite).ConfigureAwait(false);
        }
    }

    /// <summary>The stored provenance is identical at capture and continuation for the current binding and arguments;
    /// a mismatch needs a new source epoch, never an empty tree.</summary>
    private bool Matches(SourceProvenance provenance, DropboxRootIdentity binding) =>
        string.Equals(provenance.AccountId, binding.AccountId, StringComparison.Ordinal) &&
        string.Equals(provenance.NamespaceId, binding.RootNamespaceId, StringComparison.Ordinal) &&
        string.Equals(provenance.RootIdentity, binding.FolderId, StringComparison.Ordinal) &&
        string.Equals(provenance.ConfiguredRoot, PathNames.TrimDropboxRoot(pair.DropboxRoot),
            StringComparison.Ordinal) &&
        provenance.Recursive && provenance.IncludeDeleted && provenance.IncludeMountedFolders &&
        provenance.IncludeNonDownloadableFiles && provenance.PairGeneration == pairGeneration;

    /// <summary>Captures a deletion-aware cursor and commits it with a new source epoch and the normal recovery
    /// obligation; a transient capture failure waits through the shared listener reconnect deadline.</summary>
    private async Task InstallAsync(DropboxRootIdentity binding,
        (long Epoch, SourceProvenance? Provenance, string? Cursor, SourceObligation? Obligation, long Revision)?
            source, CancellationToken ct, bool finite)
    {
        var attempt = new RequestAttempt(this, AttemptOwner.Listener);
        string cursor;
        try
        {
            cursor = await listing.CaptureCursorAsync(FeedOptions, attempt, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await WaitForReconnectAsync(error, attempt, api.AdmissionIdentity(DropboxProtocol.ListFolderGetLatestCursor), ct, finite)
                .ConfigureAwait(false);
            return;
        }
        var epoch = source?.Epoch ?? 0L;
        var revision = source?.Revision ?? 0L;
        var nextEpoch = checked(epoch + 1);
        var nextRevision = checked(revision + 1);
        var provenance = new SourceProvenance(binding.AccountId, binding.RootNamespaceId, binding.FolderId,
            PathNames.TrimDropboxRoot(pair.DropboxRoot), true, true, true, true, pairGeneration, nextEpoch);
        store.AdvanceSource(pair.PairId, Side.Dropbox, pairGeneration, epoch, revision,
            source?.Provenance, source?.Cursor, nextEpoch, nextRevision, provenance, cursor,
            source?.Obligation ?? store.LostIntervalObligation(pair.PairId));
        Accepted();
        WorkCommitted?.Invoke();
    }

    /// <summary>Continues the committed cursor page by page until a terminal page. False means the attempt was
    /// interrupted or fenced, so acquisition resumes from the authoritative cursor.</summary>
    private async Task<bool> DrainAsync(CancellationToken ct, bool finite = false)
    {
        _cycle = new ContinuationCycle();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var source = store.ReadSource(pair.PairId, Side.Dropbox);
            if (source is not { } current || current.Provenance is not { } provenance ||
                current.Cursor is not { } cursor || provenance.PairGeneration != pairGeneration)
                return false;
            var attempt = new RequestAttempt(this, AttemptOwner.Listener);
            DropboxPage page;
            try
            {
                page = await listing.ReadChangePageAsync(cursor, attempt, ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                if (InvalidateReset(error)) return false;
                await WaitForReconnectAsync(error, attempt, api.AdmissionIdentity(DropboxProtocol.ListFolderContinue), ct, finite)
                    .ConfigureAwait(false);
                return false;
            }
            var hasMore = page.HasMore;
            var next = page.Cursor;
            await using (page)
            {
                if (!Commit(page, current.Epoch, current.Revision, provenance, cursor)) return false;
            }
            Accepted();
            if (_pageHadNotices) WorkCommitted?.Invoke();
            if (!hasMore)
            {
                _terminal = new DropboxCatchUp(pair.PairId, pairGeneration, current.Epoch, next, _committedWatermark);
                return true;
            }
            if (_cycle.Repeats(cursor, next)) throw SourceFailure();
        }
    }

    /// <summary>Replays the sealed page into the one source transaction; the spool is read only here, once. A lost
    /// acknowledgment is resolved from the stored position, never by replaying the page.</summary>
    private bool Commit(DropboxPage page, long epoch, long revision, SourceProvenance provenance,
        string expectedCursor)
    {
        _pageHadNotices = false;
        try
        {
            _committedWatermark = store.AcquireDropboxPage(pair.PairId, pairGeneration, epoch, revision, provenance, expectedCursor,
                Notices(page), page.Cursor);
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var current = store.ReadSource(pair.PairId, Side.Dropbox);
            if (current is not { } now) return false;
            // The commit may have succeeded before its acknowledgment was lost; the page's own cursor proves it.
            if (now.Cursor is { } committed && string.Equals(committed, page.Cursor, StringComparison.Ordinal))
            {
                _committedWatermark = store.ReadWorkWatermark();
                return true;
            }
            if (now.Epoch != epoch || now.Revision != revision || !Equals(now.Provenance, provenance)) return false;
            if (!string.Equals(now.Cursor, expectedCursor, StringComparison.Ordinal)) return false;
            throw;
        }
    }

    /// <summary>Streams the page's relevant notices in provider order. A tombstone keeps its path as an
    /// identity-safe hint and is never filtered by scope.</summary>
    private IEnumerable<ChangeNotice> Notices(DropboxPage page)
    {
        foreach (var listed in page.ReadEntries())
        {
            var entry = listed.Entry;
            if (entry is not null && scope.Excludes(null, listed.Path, entry.IsFolder)) continue;
            _pageHadNotices = true;
            yield return new ChangeNotice(Side.Dropbox,
                entry is null ? ChangeKind.Deleted : ChangeKind.Changed, listed.Path, null, entry?.Id, entry,
                false, false);
        }
    }

    /// <summary>Waits once for provider changes at the committed cursor. True asks the loop to continue the cursor
    /// before waiting again; the returned backoff governs the next wait.</summary>
    private async Task<bool> LongpollAsync(CancellationToken ct)
    {
        var source = store.ReadSource(pair.PairId, Side.Dropbox);
        if (source is not { } current || current.Provenance is not { } provenance ||
            current.Cursor is not { } cursor || provenance.PairGeneration != pairGeneration)
            return true;
        var attempt = new RequestAttempt(this, AttemptOwner.Listener);
        bool changes;
        try
        {
            changes = await api.LongpollAsync(cursor, attempt, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (InvalidateReset(error)) return true;
            await WaitForReconnectAsync(error, attempt, api.AdmissionIdentity(DropboxProtocol.ListFolderLongpoll), ct)
                .ConfigureAwait(false);
            return true;
        }
        Accepted();
        return changes;
    }

    private bool InvalidateReset(Exception error)
    {
        if (!DropboxErrors.IsCursorReset(error)) return false;
        var source = store.ReadSource(pair.PairId, Side.Dropbox);
        if (source is not { } current || current.Provenance?.PairGeneration != pairGeneration) return true;
        store.AdvanceSource(pair.PairId, Side.Dropbox, pairGeneration, current.Epoch, current.Revision,
            current.Provenance, current.Cursor, checked(current.Epoch + 1), checked(current.Revision + 1),
            null, null, current.Obligation ?? store.LostIntervalObligation(pair.PairId));
        _terminal = null;
        Accepted();
        WorkCommitted?.Invoke();
        return true;
    }

    /// <summary>Registers one listener reconnect for a transient failed attempt through the shared owner, after that
    /// attempt's single completion claim; permanent failures and unknown writes are never resent.</summary>
    private async Task WaitForReconnectAsync(Exception error, RequestAttempt attempt,
        (string AccountId, string RouteName, string SourceIdentity) identity, CancellationToken ct, bool finite = false)
    {
        ct.ThrowIfCancellationRequested();
        if (attempt.Completion is not { } settled || settled.Result != RequestResult.TransientFailure) throw error;
        if (!attempt.TryClaimCompletion()) return;
        _ = openOutage;
        lock (_turnGate) _outage ??= () => { };
        var providerWait = FailureRetryOwner.RemainingProviderObservation(
            settled.ProviderWait, attempt.ProviderWaitStartedTimestamp, time);
        var wait = FailureRetryOwner.AdoptedListenerWait(_session, providerWait, backoff, Random.Shared.NextDouble);
        if (finite)
        {
            // Direct and startup callers have no running listener-deadline dispatcher.
            await Task.Delay(wait, time, ct).ConfigureAwait(false);
            return;
        }
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = scheduleReconnect(identity, wait, _ =>
        {
            gate.TrySetResult();
            return Task.CompletedTask;
        });
        _reconnect = registration;
        try
        {
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
            if (ReferenceEquals(_reconnect, registration)) _reconnect = null;
        }
    }

    /// <summary>An accepted response or committed page resets the listener stage, withdraws its reconnect and closes
    /// the outage the first failure opened.</summary>
    private void Accepted()
    {
        FailureRetryOwner.AdoptedListenerSucceeded(_session);
        WithdrawReconnect();
        EndOutage();
    }

    /// <summary>Closes the Run owner's outage contribution once; repeated calls cost one null check.</summary>
    private void EndOutage()
    {
        Action? close;
        lock (_turnGate) { close = _outage; _outage = null; }
        close?.Invoke();
    }

    /// <summary>The background loop owns the contribution while it runs, so a finite-only caller ends it on exit.</summary>
    private void EndFiniteOutage()
    {
        lock (_turnGate) if (_running) return;
        EndOutage();
    }

    private void WithdrawReconnect()
    {
        _reconnect?.Dispose();
        _reconnect = null;
    }

    /// <summary>Brent's constant-space cycle detection over the continuation chain; a repeated nonterminal cursor is
    /// a classified source failure, never a page-count cutoff.</summary>
    private InventoryIncompleteException SourceFailure() =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, pair.DropboxRoot), (TextArg.Message, string.Empty)),
            null);
}
