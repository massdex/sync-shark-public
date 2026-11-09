namespace Massdex.SyncShark.Engine;

/// <summary>Identifies one operation attempt and hands its completed disposition to one consumer.</summary>
public sealed class RequestAttempt
{
    private readonly object _gate = new();
    private (RequestResult Result, Exception? Error, TimeSpan? ProviderWait, bool NoEffectProven)? _completion;
    private long? _providerWaitStartedTimestamp;
    private bool _claimed;

    public RequestAttempt(object operation, AttemptOwner owner)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Operation = operation;
        Owner = owner;
    }

    public object Operation { get; }
    public AttemptOwner Owner { get; }

    /// <summary>Whether this terminal result belongs to the request-level retry episode.</summary>
    public static bool RetriesAtRequest(AttemptOwner owner, RequestResult? result) =>
        owner == AttemptOwner.Request;

    public (RequestResult Result, Exception? Error, TimeSpan? ProviderWait, bool NoEffectProven)? Completion
    {
        get
        {
            lock (_gate)
            {
                return _completion;
            }
        }
    }

    public long? ProviderWaitStartedTimestamp
    {
        get
        {
            lock (_gate)
            {
                return _providerWaitStartedTimestamp;
            }
        }
    }

    /// <summary>Publishes the first complete disposition without replacing an earlier result.</summary>
    public bool TryComplete(RequestResult result, Exception? error, TimeSpan? providerWait,
        bool noEffectProven, long? providerWaitStartedTimestamp)
    {
        lock (_gate)
        {
            if (_completion.HasValue)
            {
                return false;
            }

            _completion = (result, error, providerWait, noEffectProven);
            _providerWaitStartedTimestamp = providerWaitStartedTimestamp;
            return true;
        }
    }

    public bool TryComplete(RequestResult result, Exception? error, TimeSpan? providerWait, bool noEffectProven) =>
        TryComplete(result, error, providerWait, noEffectProven, null);

    /// <summary>Claims a published completion once; an early claim leaves the attempt unclaimed.</summary>
    public bool TryClaimCompletion()
    {
        lock (_gate)
        {
            if (!_completion.HasValue || _claimed)
            {
                return false;
            }

            _claimed = true;
            return true;
        }
    }

    /// <summary>
    /// Claims the one published completion and returns it, or null when none is published or it is already
    /// claimed. The claim and the read are one atomic owner answer.
    /// </summary>
    public (RequestResult Result, Exception? Error, TimeSpan? ProviderWait, bool NoEffectProven)? ClaimResult()
    {
        lock (_gate)
        {
            if (!_completion.HasValue || _claimed) return null;
            _claimed = true;
            return _completion;
        }
    }
}
