using Massdex.SyncShark.Engine.Dropbox;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Storage;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>Owns bilateral root admission for initial Repair, selected resolution and admitted live or recovery work.</summary>
internal static class PairSafety
{
    internal static void RequireRunnable(PairSession session) =>
        RequireRunnable(session.Host, session.Registration, session.Pair, session.ExpectedGeneration);

    internal static void RequireRunnable(EngineHost host, object registration, PairConfig pair, long generation)
    {
        host.RequireGeneration(registration, pair.PairId, generation);
        RequireCurrent(host.Store, pair, generation);
        var admission = host.Admission(pair.PairId, pair);
        if (admission == EngineHost.PairAdmission.Disabled)
            throw new EngineRequestException(TextKey.PairDisabled, (TextArg.Pair, pair.PairId));
        if (admission == EngineHost.PairAdmission.Paused)
            throw new Massdex.SyncShark.V2.PairPausedException(pair.PairId);
        if (pair.Roots is null)
            throw Refuse(host.Store, pair, generation, TextKey.RootUnbound, null, null);
        if (admission == EngineHost.PairAdmission.AwaitingDecision)
            throw new EngineRequestException(TextKey.PairAwaitingDecision, (TextArg.Pair, pair.PairId));
    }

    internal static void AdmitRoots(PairSession session, ScanReservation reservation, RootBinding observed)
    {
        reservation.Require(session.Scans, session.Pair.PairId);
        RequireCurrent(session);
        AdmitObserved(session, observed);
    }

    internal static async Task CheckRootsAsync(PairSession session, CancellationToken ct)
    {
        RequireRunnable(session);
        var observed = await session.ReadRootsAsync(includeDrive: false, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        RequireCurrent(session);
        AdmitObserved(session, observed.Roots);
    }

    private static void AdmitObserved(PairSession session, RootBinding observed)
    {
        CompareRoots(session.Store, session.Pair, session.ExpectedGeneration, observed, binding: false);
        var issue = RootIssue(session.Store, session.Pair.PairId);
        if (issue is not null)
            session.Store.ReconcileRootIdentity(session.Pair.PairId, session.ExpectedGeneration, session.Pair.Roots,
                issue, StateChange.Empty with { Close = [(string.Empty, new[] { IssueKind.RootIdentity })] });
    }

    /// <summary>Reads one root, mapping a classified adapter failure to the one root refusal. Cancellation and a
    /// stale authority are not root absence, and a failed refusal write keeps the original failure.</summary>
    internal static async Task<T> ReadRootAsync<T>(IStateStore store, PairConfig expected, long expectedGeneration,
        Side side, Func<CancellationToken, Task<T>> read, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(read);
        ct.ThrowIfCancellationRequested();
        try { return await read(ct).ConfigureAwait(false); }
        catch (Exception error) when (!ct.IsCancellationRequested && RootReadCause(side, error) is { } cause)
        {
            Exception refusal;
            try { refusal = Refuse(store, expected, expectedGeneration, cause, side, error); }
            catch (Exception stateError) { throw new AggregateException(error, stateError); }
            throw refusal;
        }
    }

    private static TextKey? RootReadCause(Side side, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (error is OperationCanceledException or LiveAdmissionStaleException) return null;
        return side switch
        {
            Side.Local => LocalErrors.RootReadCause(error),
            Side.Dropbox => DropboxErrors.RootReadCause(error),
            _ => throw new ArgumentOutOfRangeException(nameof(side))
        };
    }

    internal static void ValidateConfigurationRoots(IStateStore store, PairConfig expected,
        long expectedGeneration, RootBinding observed, bool binding)
    {
        RequireCurrent(store, expected, expectedGeneration);
        CompareRoots(store, expected, expectedGeneration, observed, binding);
    }

    internal static void RequireCurrent(PairSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Host.RequireGeneration(session.Registration, session.Pair.PairId, session.ExpectedGeneration);
        RequireCurrent(session.Store, session.Pair, session.ExpectedGeneration);
    }

    private static void RequireCurrent(IStateStore store, PairConfig expected, long generation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(expected);
        var current = store.ReadPairExecution(expected.PairId);
        if (current is null || current.Value.Generation != generation || current.Value.Pair.Roots != expected.Roots ||
            !SameScope(current.Value.Pair, expected.LocalRoot, expected.DropboxRoot, expected.ConnectionId))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
    }

    private static bool SameScope(PairConfig pair, string local, string dropbox, string connection) =>
        PathNames.Same(PathNames.NormalizeLocalPath(pair.LocalRoot), PathNames.NormalizeLocalPath(local)) &&
        PathNames.Same(PathNames.TrimDropboxRoot(pair.DropboxRoot), PathNames.TrimDropboxRoot(dropbox)) &&
        string.Equals(pair.ConnectionId, connection, StringComparison.Ordinal);

    private static void CompareRoots(IStateStore store, PairConfig pair, long generation,
        RootBinding observed, bool binding)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (!observed.IsValid())
            throw new InvalidDataException(Text.Get(TextKey.StateRootBindingInvalid, (TextArg.Pair, pair.PairId)));
        if (!SameScope(pair, observed.LocalRoot, observed.DropboxRoot, observed.ConnectionId))
            throw Refuse(store, pair, generation, TextKey.RootConfiguredScopeChanged, null, null);
        if (binding && pair.Roots is null) return;
        if (pair.Roots is not { } saved)
            throw Refuse(store, pair, generation, TextKey.RootUnbound, null, null);
        if (!SameScope(pair, saved.LocalRoot, saved.DropboxRoot, saved.ConnectionId))
            throw Refuse(store, pair, generation, TextKey.RootConfiguredScopeChanged, null, null);
        if (saved.Local != observed.Local)
            throw Refuse(store, pair, generation, TextKey.RootLocalIdentityChanged, Side.Local, null);
        if (saved.Dropbox.AccountId != observed.Dropbox.AccountId ||
            saved.Dropbox.RootNamespaceId != observed.Dropbox.RootNamespaceId ||
            saved.Dropbox.HomeNamespaceId != observed.Dropbox.HomeNamespaceId)
            throw Refuse(store, pair, generation, TextKey.RootDropboxScopeChanged, Side.Dropbox, null);
        if (saved.Dropbox.FolderId != observed.Dropbox.FolderId)
            throw Refuse(store, pair, generation, TextKey.RootDropboxFolderChanged, Side.Dropbox, null);
    }

    private static Issue? RootIssue(IStateStore store, string pairId) =>
        store.Issues(pairId, [IssueKind.RootIdentity]).SingleOrDefault(issue => issue.Path == string.Empty);

    private static Exception Refuse(IStateStore store, PairConfig pair, long generation,
        TextKey cause, Side? side, Exception? error)
    {
        RequireCurrent(store, pair, generation);
        var issue = RootIssue(store, pair.PairId);
        var subject = new IssueSubject([], side);
        store.ReconcileRootIdentity(pair.PairId, generation, pair.Roots, issue,
            StateChange.Empty with { Raise = [(IssueKind.RootIdentity, cause, string.Empty, subject)] });
        var refusal = new EngineRequestException(cause, (TextArg.Pair, pair.PairId),
            (TextArg.Side, side?.ToString() ?? string.Empty),
            (TextArg.Message, error is null ? side?.ToString() ?? string.Empty :
                Text.Get(Text.ReadFailedOn(side!.Value), (TextArg.Path, string.Empty),
                    (TextArg.Message, error.Message))));
        return refusal;
    }

    internal static Task<ResolutionOutcome> ResolveSelectedAsync(PairSession session, Issue issue,
        ResolutionChoice choice, string? newName, IReadOnlyList<SyncIgnoreRule>? keepRules,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ct.ThrowIfCancellationRequested();
        RequireRunnable(session);
        RequireIssue(session, issue);
        return session.Syncer.ResolveAsync(issue, choice, newName, keepRules, ct);
    }

    internal static Task<T> ExecuteLiveAsync<T>(IReadOnlyList<LiveRootRequest> requests,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0) throw new ArgumentException(null, nameof(requests));
        var sessions = new PairSession[requests.Count];
        var admissions = new ExecutionRequest[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var session = request.Session;
            RequireRunnable(session);
            request.Selection.RequireFor(session.Pair.PairId, session.ExpectedGeneration);
            ValidateLive(request);
            sessions[index] = session;
            admissions[index] = Request(session, LockKeys.ForScopes(session.Pair, request.Footprint));
        }
        return ExecuteCoreAsync(sessions, admissions, () =>
        {
            foreach (var request in requests) ValidateLive(request);
        }, execute, ct);
    }

    private static void ValidateLive(LiveRootRequest request)
    {
        var session = request.Session;
        if (session.Store.Obligated(session.Pair.PairId))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        session.Store.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
    }

    internal static Task<T> ExecuteRecoveryAsync<T>(PairSession session,
        FolderRemovalState operation, Func<IAdmittedExecution, CancellationToken, Task<T>> execute,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return ExecuteRecoveryAsync(session, [operation], execute, ct);
    }

    internal static Task<T> ExecuteRecoveryAsync<T>(PairSession session, FolderRemovalState operation,
        FolderRemovalItem member, Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        RequireRunnable(session);
        var pending = session.Store.ReadPendingForFolderRecovery(session.Pair.PairId, operation);
        var footprint = LockKeys.ForRecovery(session.Pair, operation, pending, member.Stage?.Path, member.StageAllocation?.Path);
        void Validate()
        {
            session.Store.ValidatePendingForFolderRecovery(session.Pair.PairId, operation, pending);
            // Two provenance rows can share a key, so the exact member is read by its ordinal.
            var current = session.Store.FolderRemovalItems(session.Pair.PairId, operation.OperationId,
                checked(member.Ordinal - 1), StateSchema.OneRow).SingleOrDefault();
            if (current is null || current.Ordinal != member.Ordinal || current.Generation != member.Generation ||
                current.Stage != member.Stage || current.StageAllocation != member.StageAllocation)
                throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
        }
        Validate();
        return ExecuteCoreAsync([session], [Request(session, footprint)], Validate, execute, ct);
    }

    internal static Task<T> ExecuteRecoveryAsync<T>(PairSession session,
        IReadOnlyList<FolderRemovalState> operations,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (operations.Count == 0) throw new ArgumentException(null, nameof(operations));
        RequireRunnable(session);
        var pending = session.Store.ReadPendingForFolderRecovery(session.Pair.PairId, operations[0]);
        var footprint = new List<PathLockScope>();
        foreach (var operation in operations)
        {
            session.Store.ValidatePendingForFolderRecovery(session.Pair.PairId, operation, pending);
            footprint.AddRange(LockKeys.ForRecovery(session.Pair, operation, pending));
        }
        return ExecuteCoreAsync([session], [Request(session, footprint)], () =>
        {
            foreach (var operation in operations)
                session.Store.ValidatePendingForFolderRecovery(session.Pair.PairId, operation, pending);
        }, execute, ct);
    }

    private static ExecutionRequest Request(PairSession session, IReadOnlyList<PathLockScope> footprint)
    {
        var resources = session.Host.BorrowGeneration(session.Registration, session.Pair.PairId,
            session.ExpectedGeneration);
        if (!ReferenceEquals(resources.Admission, session.Admission))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.CoverageChanged);
        return new ExecutionRequest(session.Pair.PairId, session.ExpectedGeneration, resources.AccountOrderKey,
            session.Admission, session.Priority, footprint);
    }

    private static async Task<T> ExecuteCoreAsync<T>(IReadOnlyList<PairSession> sessions,
        IReadOnlyList<ExecutionRequest> requests, Action validate,
        Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ct.ThrowIfCancellationRequested();
        bool AcceptGrant()
        {
            foreach (var session in sessions)
                if (!session.Host.TryHoldGeneration(session.Registration)) return false;
            return true;
        }
        await using var execution = await AdmittedExecution.AcquireAsync(requests, AcceptGrant, ct, validate).ConfigureAwait(false);
        var checkedPairs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            RequireCurrent(session);
            if (!checkedPairs.Add(session.Pair.PairId)) continue;
            var observed = await session.ReadRootsAsync(includeDrive: false, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            RequireCurrent(session);
            AdmitObserved(session, observed.Roots);
        }
        validate();
        ct.ThrowIfCancellationRequested();
        foreach (var session in sessions) RequireCurrent(session);
        return await execute(execution, ct).ConfigureAwait(false);
    }

    // Visibility is not resolution authority; subject membership is a set and timestamps compare by instant.
    internal static string IssueFingerprint(Issue issue) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(issue)));

    internal static Issue ForReview(Issue issue) => issue with { Reviewed = IssueFingerprint(issue) };

    internal static void RequireIssue(Issue current, string reviewed)
    {
        if (!string.Equals(IssueFingerprint(current), reviewed, StringComparison.Ordinal))
            throw new EngineRequestException(TextKey.ResolveStale,
                (TextArg.Id, current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static void RequireIssue(PairSession session, Issue expected)
    {
        var current = session.Store.IssueById(session.Pair.PairId, expected.Id);
        if (current is null || !current.Matches(expected))
            throw new LiveAdmissionStaleException(LiveAdmissionStaleReason.SelectionChanged);
    }

    internal static ISelectedExecutionBoundary SelectedBoundaryFor(PairSession session) => new SelectedBoundary(session);

    private sealed class SelectedBoundary(PairSession session) : ISelectedExecutionBoundary
    {
        public Task<T> ExecuteAsync<T>(Issue expected, IReadOnlyList<LiveEffectScope> footprint,
            Func<IAdmittedExecution, CancellationToken, Task<T>> execute, CancellationToken ct)
        {
            RequireRunnable(session);
            var request = Request(session, LockKeys.ForScopes(session.Pair, footprint));
            return ExecuteCoreAsync([session], [request], () => RequireIssue(session, expected), execute, ct);
        }
    }
}
