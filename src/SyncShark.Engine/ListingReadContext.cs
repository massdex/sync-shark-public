namespace Massdex.SyncShark.Engine;

/// <summary>Attempt completion and joined cleanup bound to one actual listing enumeration.</summary>
public sealed record ListingReadContext(Func<RequestAttempt> BeginAttempt,
    Func<RequestAttempt, CancellationToken, Task<bool>> CompleteAttemptAsync, Func<ValueTask> EndAsync);
