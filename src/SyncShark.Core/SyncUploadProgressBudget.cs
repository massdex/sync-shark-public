using System.Globalization;

namespace Massdex.SyncShark;

// Bounded progress accounting for one resumable upload session. A transient 308 response is the only
// server-authoritative progress signal, so a session that repeats, regresses or omits an
// acknowledgement must not be able to keep a writer resending content forever. The session identity
// stays in private memory and is never written to a message, a checkpoint or a report.
internal sealed class SyncUploadProgressBudget
{
    private readonly int _maximumNoProgress;
    private readonly int _maximumRestarts;
    private string? _sessionId;
    private long _highestAcknowledgedOffset;
    private bool _sessionStarted;
    private int _restarts;
    private int _noProgress;

    internal SyncUploadProgressBudget(int maximumNoProgress = 8, int maximumRestarts = 2)
    {
        if (maximumNoProgress < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumNoProgress),
                "A progress budget must allow at least one acknowledgement.");
        if (maximumRestarts < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRestarts),
                "A progress budget cannot allow a negative number of restarts.");
        _maximumNoProgress = maximumNoProgress;
        _maximumRestarts = maximumRestarts;
    }

    // The first session establishes this sequence's high-water mark. Re-notifying the same session
    // never resets it. A different session is a restart: the mark is reset for that new session and
    // the bounded restart allowance is consumed.
    internal void BeginSession(string sessionId, long acknowledgedOffset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_sessionStarted)
        {
            _sessionStarted = true;
            _sessionId = sessionId;
            _highestAcknowledgedOffset = acknowledgedOffset;
            _noProgress = 0;
            return;
        }
        if (string.Equals(_sessionId, sessionId, StringComparison.Ordinal))
        {
            if (acknowledgedOffset > _highestAcknowledgedOffset)
            {
                _highestAcknowledgedOffset = acknowledgedOffset;
                _noProgress = 0;
            }
            return;
        }
        _restarts++;
        if (_restarts > _maximumRestarts)
            throw new IOException("The upload exceeded its bounded allowance of " +
                _maximumRestarts.ToString(CultureInfo.InvariantCulture) +
                " new resumable sessions without completing. Reconcile the retained session before retrying.");
        _sessionId = sessionId;
        _highestAcknowledgedOffset = acknowledgedOffset;
        _noProgress = 0;
    }

    // Every acknowledgement must strictly advance the high-water mark. Anything else is counted, and
    // that bounded count is what ends a resend loop instead of letting it continue.
    internal void Observe(long acknowledgedOffset)
    {
        if (acknowledgedOffset > _highestAcknowledgedOffset)
        {
            _highestAcknowledgedOffset = acknowledgedOffset;
            _noProgress = 0;
            return;
        }
        _noProgress++;
        if (_noProgress >= _maximumNoProgress)
            throw new IOException("The upload received " +
                _maximumNoProgress.ToString(CultureInfo.InvariantCulture) +
                " acknowledgements in a row that did not advance the acknowledged offset. " +
                "Reconcile the retained session before retrying.");
    }
}
