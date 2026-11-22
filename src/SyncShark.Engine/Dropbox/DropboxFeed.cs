namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DropboxFeed operations for the dropbox module.</summary>
public sealed class DropboxFeed
{
    // The feed asks for the whole tree with deletions so a tombstone is never mistaken for an unread item.
    private static readonly ListOptions FeedOptions = new(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: true);

    /// <summary>Read operation on the supplied facts.</summary>
    public async Task<ChangePage> ReadAsync(DropboxApi api, PairConfig pair, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        var listing = new DropboxListing();
        // A pair without a cursor takes a fresh base with get_latest_cursor before resuming from it.
        var from = cursor ?? await listing.CaptureCursorAsync(api, pair, FeedOptions, ct).ConfigureAwait(false);
        return await listing.ReadChangePageAsync(api, pair, from, ct).ConfigureAwait(false);
    }

    /// <summary>Maps decoded entries to ordered change notices. Scope, ignore and exclusion routing happens at intake,
    /// not here, so a change under a no-keep exclusion is never dropped (BC20).</summary>
    public static IReadOnlyList<Change> ToChanges(IReadOnlyList<Listed> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var changes = new List<Change>(entries.Count);
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
    private string? _checkpoint;
    private long _steps;

    /// <summary>True when next repeats the previous cursor or the retained checkpoint.</summary>
    public bool Repeats(string previous, string next)
    {
        if (string.Equals(previous, next, StringComparison.Ordinal)) return true;
        _steps++;
        if ((_steps & (_steps - 1)) == 0)
        {
            _checkpoint = next;
            return false;
        }
        return string.Equals(_checkpoint, next, StringComparison.Ordinal);
    }
}
