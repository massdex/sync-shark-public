namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>Reads changes for a pair from the Dropbox cursor feed.</summary>
public sealed class DropboxFeed
{
    // The feed asks for the whole tree with deletions so a tombstone is never mistaken for an unread item.
    private static readonly ListOptions FeedOptions = new(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: true);

    private readonly DropboxListing listing = new();
    private DropboxProtocol.ProviderConstraint? nextPoll;

    /// <summary>Returns changes or an advanced cursor, long-polling while neither changes.</summary>
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
                (bool changes, TimeSpan? backoff) result;
                try { result = await api.LongpollAsync(current, ct).ConfigureAwait(false); }
                catch (Exception error) when (!ct.IsCancellationRequested && DropboxListing.IsUnreadPage(error))
                {
                    throw DropboxListing.UnreadPage(pair.DropboxRoot, error);
                }
                nextPoll = DropboxProtocol.LongpollConstraintFor(pair.Roots!.Dropbox.AccountId, pair.PairId,
                    result.backoff, api.Clock.GetTimestamp());
                if (result.changes) break;
            }
        }
    }

    /// <summary>Converts listed entries to changes, preserving order and treating tombstones as deletions.</summary>
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
