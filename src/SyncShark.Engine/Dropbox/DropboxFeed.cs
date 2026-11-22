namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DropboxFeed operations for the dropbox module.</summary>
public sealed class DropboxFeed
{
    // The feed asks for the whole tree with deletions so a tombstone is never mistaken for an unread item.
    private static readonly ListOptions FeedOptions = new(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: true);

    private readonly DropboxListing listing = new();
    private DropboxProtocol.ProviderConstraint? nextPoll;

    public async Task<ChangePage> ReadAsync(DropboxApi api, PairConfig pair, string? cursor, CancellationToken ct)
    {
        var current = cursor ?? await listing.CaptureCursorAsync(api, pair, FeedOptions, ct).ConfigureAwait(false);
        while (true)
        {
            var page = await listing.ReadChangePageAsync(api, pair, current, ct).ConfigureAwait(false);
            if (page.Changes.Count != 0 || page.Cursor != cursor) return page;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (nextPoll is { } floor)
                {
                    var remaining = floor.Duration - api.Clock.GetElapsedTime(floor.StartedTimestamp);
                    if (remaining > TimeSpan.Zero) await api.WaitAsync(remaining, ct).ConfigureAwait(false);
                }
                var (changes, backoff) = await api.LongpollAsync(current, ct).ConfigureAwait(false);
                nextPoll = DropboxProtocol.LongpollConstraintFor(pair.Roots!.Dropbox.AccountId, pair.PairId,
                    backoff, api.Clock.GetTimestamp());
                if (changes) break;
            }
        }
    }

    public static IReadOnlyList<Change> ToChanges(IEnumerable<Listed> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var changes = new List<Change>();
        foreach (var listed in entries)
        {
            var entry = listed.Entry;
            changes.Add(entry is null
                ? new Change(Side.Dropbox, ChangeKind.Deleted, listed.Path, null, null, null, false, false)
                : new Change(Side.Dropbox, ChangeKind.Changed, listed.Path, null, entry.Id, entry, false, false));
        }
        return changes;
    }
}

/// <summary>Detects a repeating change continuation in constant space, shared by every continuation reader.</summary>
public sealed class ContinuationCycle
{
    private string? _start;
    private string? _checkpoint;
    private long _steps;

    /// <summary>True when next repeats the previous cursor or the retained checkpoint.</summary>
    public bool Repeats(string previous, string next)
    {
        _start ??= previous;
        if (previous == next || next == _start || next == _checkpoint) return true;
        _steps++;
        if ((_steps & (_steps - 1)) == 0)
            _checkpoint = next;
        return false;
    }
}
