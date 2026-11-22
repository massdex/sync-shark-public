namespace Massdex.SyncShark.Engine.Dropbox;

/// <summary>DropboxFeed operations for the dropbox module.</summary>
public sealed class DropboxFeed
{
    // The feed asks for the whole tree with deletions so a tombstone is never mistaken for an unread item.
    private static readonly ListOptions FeedOptions = new(Recursive: true, IncludeOutOfScope: false, IncludeDeleted: true);

    /// <summary>Live read of the pair's change feed. A caught-up page with no changes waits on the one provider
    /// longpoll instead of returning so a caller's next request cannot hot-poll a quiet root.</summary>
    public async Task<ChangePage> ReadAsync(DropboxApi api, PairConfig pair, string? cursor, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(pair);
        var listing = new DropboxListing();
        var current = string.IsNullOrWhiteSpace(cursor)
            ? await listing.CaptureCursorAsync(api, pair, FeedOptions, ct).ConfigureAwait(false)
            : cursor!;
        var cycle = new ContinuationCycle();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await listing.ReadChangePageAsync(api, pair, current, ct).ConfigureAwait(false);
            if (page.Changes.Count > 0) return page;
            if (!page.CaughtUp)
            {
                // Advancing continuation with no entries: a repeated cursor is a classified source failure.
                if (string.IsNullOrWhiteSpace(page.Cursor) || cycle.Repeats(current, page.Cursor!))
                    throw SourceFailure(pair);
                current = page.Cursor!;
                continue;
            }
            // Caught up with no changes: wait for the provider signal, one outstanding longpoll, then continue.
            if (string.IsNullOrWhiteSpace(page.Cursor)) throw SourceFailure(pair);
            current = page.Cursor!;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (await api.LongpollAsync(current, ct).ConfigureAwait(false)) break;
            }
        }
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

    private static InventoryIncompleteException SourceFailure(PairConfig pair) =>
        new(Text.Get(TextKey.ReadDropboxFailed, (TextArg.Path, pair.DropboxRoot), (TextArg.Message, "")), null);
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
