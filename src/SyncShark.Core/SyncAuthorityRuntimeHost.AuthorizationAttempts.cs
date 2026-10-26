namespace Massdex.SyncShark;

/// <summary>
/// One safe readonly view of the single pending interactive authorization attempt. It carries the
/// ephemeral attempt identity, the provider, the connect/reauthorize action, the existing account for a
/// reauthorization (null for a new connection), the UTC start and the current phase. OAuth state, the
/// PKCE verifier, the authorization URL, the returned code and every credential value stay inside the
/// provider flow and are never part of this view.
/// </summary>
public sealed record SyncPendingAuthorizationSnapshot(string AttemptId, string Provider, string Action,
    string? AccountId, DateTimeOffset StartedUtc, string Status)
{
    public const string AwaitingAuthorizationPhase = "awaiting-authorization";
    public const string CancellationRequestedPhase = "cancellation-requested";
}

/// <summary>The two interactive authorization actions the shared attempt identity can describe.</summary>
public static class SyncAuthorizationAction
{
    public const string Connect = "connect";
    public const string Reauthorize = "reauthorize";
}

// GCLI-G3/AUTH1: one small process-memory authorization-attempt controller owned by the existing host.
// Exactly one attempt may be pending per host across both providers; the identity is an ephemeral
// opaque GUID derived from nothing but randomness, so it is independent of the OAuth state, verifier,
// URL, code and credentials. No new host, transport, service, listener, journal, database, schema,
// credential target, OAuth protocol or trust boundary is introduced: the existing provider flows are
// unchanged and only report their lifetime through this controller.
public sealed partial class SyncAuthorityRuntimeHost
{
    private readonly object _authorizationAttemptGate = new();
    private AuthorizationAttempt? _pendingAuthorization;

    /// <summary>
    /// Raised when the single pending authorization attempt appears, changes phase or ends. Handlers
    /// must not block: the running application posts to its dispatcher instead of waiting here.
    /// </summary>
    public event Action? AuthorizationAttemptChanged;

    /// <summary>The pending attempt as a safe readonly snapshot, or null when nothing is pending.</summary>
    public SyncPendingAuthorizationSnapshot? PendingAuthorization
    {
        get { lock (_authorizationAttemptGate) return _pendingAuthorization?.Snapshot(); }
    }

    /// <summary>
    /// Registers this interactive flow as the host's one pending attempt. A second attempt is rejected
    /// without disturbing the first. The caller token (a direct CLI Ctrl+C or an equivalent caller
    /// boundary) is linked to the attempt, so caller cancellation keeps its existing meaning.
    /// </summary>
    internal AuthorizationAttempt BeginAuthorizationAttempt(string provider, string action, string? accountId,
        CancellationToken callerToken)
    {
        var attempt = new AuthorizationAttempt(this, SyncProviders.Normalize(provider), action, accountId, callerToken);
        lock (_authorizationAttemptGate)
        {
            if (_pendingAuthorization is not null)
                throw new InvalidOperationException("Finish or cancel the waiting connection first.");
            _pendingAuthorization = attempt;
        }
        attempt.NotifyChanged();
        return attempt;
    }

    internal void ReleaseAuthorizationAttempt(AuthorizationAttempt attempt)
    {
        lock (_authorizationAttemptGate)
            if (ReferenceEquals(_pendingAuthorization, attempt)) _pendingAuthorization = null;
    }

    /// <summary>
    /// Captures exactly the attempt named by <paramref name="attemptId"/> before any asynchronous work,
    /// so a stale, wrong or finished identity can never cancel a later attempt. Returns null when no
    /// pending attempt carries that exact identity.
    /// </summary>
    internal AuthorizationAttemptLease? CaptureAuthorizationAttempt(string attemptId)
    {
        if (string.IsNullOrWhiteSpace(attemptId)) return null;
        lock (_authorizationAttemptGate)
            return _pendingAuthorization is { } current &&
                   string.Equals(current.AttemptId, attemptId, StringComparison.Ordinal)
                ? new AuthorizationAttemptLease(current) : null;
    }

    /// <summary>
    /// Requests cancellation of exactly the named pending attempt. Provider, account and
    /// current-attempt aliases are never accepted: only the exact captured attempt identity is.
    /// </summary>
    public bool RequestAuthorizationAttemptCancellation(string attemptId) =>
        CaptureAuthorizationAttempt(attemptId)?.RequestCancellation() == true;

    internal void NotifyAuthorizationAttemptChanged() => AuthorizationAttemptChanged?.Invoke();
}

internal sealed class AuthorizationAttempt : IDisposable
{
    private readonly SyncAuthorityRuntimeHost _owner;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationTokenRegistration _callerRegistration;
    private readonly TaskCompletionSource<bool> _terminal =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancellationDelivered;
    private int _committed;
    private int _disposed;

    internal AuthorizationAttempt(SyncAuthorityRuntimeHost owner, string provider, string action, string? accountId,
        CancellationToken callerToken)
    {
        _owner = owner;
        Provider = provider;
        Action = action;
        AccountId = accountId;
        AttemptId = Guid.NewGuid().ToString("N");
        StartedUtc = DateTimeOffset.UtcNow;
        Token = _cancellation.Token;
        // A caller token never replaces this attempt's own identity; it is linked so caller
        // cancellation keeps working exactly as it did before this controller existed.
        _callerRegistration = callerToken.CanBeCanceled
            ? callerToken.Register(static state => ((CancellationTokenSource)state!).Cancel(), _cancellation)
            : default;
    }

    internal string AttemptId { get; }
    internal string Provider { get; }
    internal string Action { get; }
    internal string? AccountId { get; }
    internal DateTimeOffset StartedUtc { get; }
    internal CancellationToken Token { get; }

    /// <summary>Completes when the attempt ends; the result is true only when it committed its result.</summary>
    internal Task<bool> Terminal => _terminal.Task;

    internal SyncPendingAuthorizationSnapshot Snapshot() => new(AttemptId, Provider, Action, AccountId, StartedUtc,
        Volatile.Read(ref _cancellationDelivered) == 0
            ? SyncPendingAuthorizationSnapshot.AwaitingAuthorizationPhase
            : SyncPendingAuthorizationSnapshot.CancellationRequestedPhase);

    // The first exact-target request wins. The cancellation is delivered outside every host lock, so no
    // cancellation callback can run while a lock is held. A delivered cancellation is never disposed
    // away underneath a concurrent delivery: Dispose leaves that source alone once it was delivered.
    internal bool RequestCancellation()
    {
        if (Interlocked.CompareExchange(ref _cancellationDelivered, 1, 0) != 0) return false;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// The provider flow committed its verified connection. A later cancellation must report the real
    /// terminal result and must never disconnect or undo an already completed account.
    /// </summary>
    internal void MarkCommitted() => Interlocked.Exchange(ref _committed, 1);

    internal void NotifyChanged() => _owner.NotifyAuthorizationAttemptChanged();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // The terminal outcome is published before the identity is released, so a cancellation that
        // arrives inside this window still observes the real result instead of a stale identity.
        _terminal.TrySetResult(Volatile.Read(ref _committed) != 0);
        _callerRegistration.Dispose();
        if (Volatile.Read(ref _cancellationDelivered) == 0)
            try { _cancellation.Dispose(); }
            catch (ObjectDisposedException) { /* The linked caller token cancelled the same source. */ }
        _owner.ReleaseAuthorizationAttempt(this);
        NotifyChanged();
    }
}

/// <summary>
/// The exact captured attempt a cancellation command may act on. It carries no provider, account or
/// current-attempt alias, and it cannot register, start or replace an attempt.
/// </summary>
internal sealed class AuthorizationAttemptLease(AuthorizationAttempt attempt)
{
    internal string AttemptId => attempt.AttemptId;
    internal string Provider => attempt.Provider;
    internal string Action => attempt.Action;
    internal Task<bool> Terminal => attempt.Terminal;
    internal bool RequestCancellation() => attempt.RequestCancellation();
}
