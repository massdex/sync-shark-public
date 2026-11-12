using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>The disposable pair-wide listing of one real Repair (ROOTID 5.1): both side listings stream into the
/// supplied sinks and the skipped facts they collect. A side's listing retries its own transient page reads, so
/// pages already ingested stay in the running scan; a read that cannot finish aborts both sinks and never restarts
/// the acquisition.</summary>
internal sealed class PairScan(ISide localSide, ISide dropboxSide, IScanSink localSink, IScanSink dropboxSink,
    IStateStore store, string pairId, Guid scanToken, string? historyCursor, int batchRows)
{
    private IScanSink _dropboxSink = dropboxSink;
    private readonly List<(string Path, TextKey Cause, IssueSubject Subject)> _localSkipped = [];
    private readonly List<(string Path, TextKey Cause, IssueSubject Subject)> _dropboxSkipped = [];
    private readonly List<(Side Side, string Key)> _localSkippedFolders = [];
    private readonly List<(Side Side, string Key)> _dropboxSkippedFolders = [];

    /// <summary>Skipped local entries in listing order, with the notice each names.</summary>
    public IReadOnlyList<(string Path, TextKey Cause, IssueSubject Subject)> LocalSkipped => _localSkipped;
    /// <summary>Skipped Dropbox entries in listing order, with the notice each names.</summary>
    public IReadOnlyList<(string Path, TextKey Cause, IssueSubject Subject)> DropboxSkipped => _dropboxSkipped;
    /// <summary>Skipped local folder keys in join space.</summary>
    public IReadOnlyList<(Side Side, string Key)> LocalSkippedFolders => _localSkippedFolders;
    /// <summary>Skipped Dropbox folder keys in join space.</summary>
    public IReadOnlyList<(Side Side, string Key)> DropboxSkippedFolders => _dropboxSkippedFolders;

    public long Observations { get; private set; }
    public string? DeletedHistoryCursor { get; private set; }
    /// <summary>A scoped scan's one side whose listing ended early while the other finished; null otherwise.</summary>
    public Side? Unlisted { get; private set; }
    /// <summary>A scoped scan's listing failure, kept whole so its provider cause reaches the folder's notice.</summary>
    public InventoryIncompleteException? Incomplete { get; private set; }

    // One fan-out: both listing tasks, their abort path and the combined incomplete result are one run acquisition.
    /// <summary>Streams both listings; returns null when both finished, else the incomplete listing's reason.</summary>
    public async Task<string?> RunAsync(TaskSet tasks, RunLog log, bool includeDeleted, long baselineCount, CancellationToken ct, string? scope = null, Action<string, string>? directoryAcquired = null)
    {
        // The local walk never yields asynchronously, so it needs its own task or it would finish before
        // the Dropbox listing starts.
        var localTask = Task.Run(() => ListAsync(localSide, localSink, _localSkipped, _localSkippedFolders, false, log, baselineCount, ct, scope, directoryAcquired));
        tasks.Add(localTask);
        var dropboxTask = scope is null ? ListDropboxAsync(includeDeleted, log, baselineCount, ct)
            : ListAsync(dropboxSide, _dropboxSink, _dropboxSkipped, _dropboxSkippedFolders, includeDeleted, log, baselineCount, ct, scope);
        tasks.Add(dropboxTask);
        try
        {
            await Task.WhenAll(localTask, dropboxTask);
            var (local, dropbox) = (localTask.Result, dropboxTask.Result);
            Observations = local.Seen + dropbox.Seen;
            Unlisted = (local.Incomplete, dropbox.Incomplete) switch
            {
                (not null, null) => Side.Local,
                (null, not null) => Side.Dropbox,
                _ => null
            };
            Incomplete = local.Incomplete ?? dropbox.Incomplete;
            if (Incomplete is not { } incomplete) return null;
            log.Failed(incomplete);
            return incomplete.Message;
        }
        catch (Exception error)
        {
            // Abort both writers on failure; only Repair's incomplete inventory returns a summary.
            // An abort failure is reported, never substituted for the listing's own diagnosis.
            var aborts = Task.WhenAll(AbortAsync(localSink), AbortAsync(_dropboxSink));
            try
            {
                await aborts;
            }
            catch (Exception abortError)
            {
                log.Failed(aborts.Exception ?? abortError);
            }
            // A disk-full in the listing or either abort is never reduced to an incomplete inventory.
            if (error is InventoryIncompleteException && ResourceFailures.Find(error) is null &&
                ResourceFailures.Find(aborts.Exception) is not null)
                throw new AggregateException(error, aborts.Exception!);
            if (scope is not null || error is not InventoryIncompleteException || ResourceFailures.Find(error) is not null) throw;
            log.Failed(error);
            return error.Message;
        }
    }

    private async Task<(long Seen, InventoryIncompleteException? Incomplete)> ListDropboxAsync(bool includeDeleted, RunLog log, long baselineCount, CancellationToken ct)
    {
        var cursor = historyCursor;
        string? terminal = null;
        (long Seen, InventoryIncompleteException? Incomplete) seen;
        try
        {
            seen = await ListAsync(dropboxSide, _dropboxSink, _dropboxSkipped, _dropboxSkippedFolders,
                includeDeleted, log, baselineCount, ct, cursor: cursor, acquired: value => terminal = value);
        }
        catch (ListingCursorResetException) when (cursor is not null)
        {
            // Join queued delta events before replacing their remote inventory, leaving Local untouched.
            await _dropboxSink.CompleteAsync();
            store.ResetDeletedHistory(pairId, scanToken, discardNames: true);
            _dropboxSink = store.OpenScan(pairId, scanToken, Side.Dropbox, batchRows);
            _dropboxSkipped.Clear();
            _dropboxSkippedFolders.Clear();
            cursor = null;
            seen = await ListAsync(dropboxSide, _dropboxSink, _dropboxSkipped, _dropboxSkippedFolders,
                includeDeleted, log, baselineCount, ct, acquired: value => terminal = value);
        }
        if (cursor is not null)
        {
            // Continuation refreshes history; only the new full listing supplies current live inventory/case.
            store.ResetDeletedHistory(pairId, scanToken, discardNames: false);
            _dropboxSink = store.OpenScan(pairId, scanToken, Side.Dropbox, batchRows);
            _dropboxSkipped.Clear();
            _dropboxSkippedFolders.Clear();
            seen = await ListAsync(dropboxSide, _dropboxSink, _dropboxSkipped, _dropboxSkippedFolders,
                false, log, baselineCount, ct);
        }
        if (includeDeleted) DeletedHistoryCursor = terminal;
        return seen;
    }

    // One side listing: the option callbacks, local add routine and sink completion share the same seen and current counters.
    private static async Task<(long Seen, InventoryIncompleteException? Incomplete)> ListAsync(ISide side, IScanSink sink,
        List<(string Path, TextKey Cause, IssueSubject Subject)> skipped, List<(Side Side, string Key)> skippedFolders,
        bool includeDeleted, RunLog log, long baselineCount, CancellationToken ct, string? scope = null, Action<string, string>? directoryAcquired = null,
        string? cursor = null, Action<string>? acquired = null)
    {
        long seen = 0;
        long units = 0;
        var currentPath = string.Empty;
        var key = side.Side == Side.Local ? TextKey.StageLocalWalk : TextKey.StageDropboxListing;
        log.Event(RunEvent.Stage(key));
        var options = new ListOptions(true, false, IncludeDeleted: includeDeleted)
        {
            Events = log.Event,
            Cursor = cursor,
            CursorAcquired = acquired,
            DirectoryAcquired = side.Side == Side.Local ? directoryAcquired : null,
            Progress = (path, completedUnits) =>
            {
                currentPath = path;
                units = completedUnits;
                log.Observe(new ListingProgress(side.Side, path, units, seen, baselineCount));
            }
        };
        Entry? root = null;
        if (scope is not null)
        {
            root = await side.ReadOneAsync(scope, ct).ConfigureAwait(false);
            if (root is not null) await AddAsync(new Listed(root.Path, root));
        }
        InventoryIncompleteException? incomplete = null;
        // A folder batch only adds, so what a side listed before its listing ended is still saved and synced.
        try
        {
            if (scope is null || root is { IsFolder: true, Skip: null })
                await foreach (var item in side.ReadAllAsync(scope ?? string.Empty, options, ct)) await AddAsync(item);
        }
        catch (InventoryIncompleteException error) when (scope is not null) { incomplete = error; }
        async ValueTask AddAsync(Listed item)
        {
            if (scope is not null && !PathNames.IsSelfOrUnder(item.Path, scope))
                throw new InventoryIncompleteException(Text.Get(TextKey.FailChanged, (TextArg.Path, scope)), null);
            await sink.AddAsync(item, ct);
            if (item.Entry is not null) seen++;
            currentPath = item.Path;
            log.Observe(new ListingProgress(side.Side, currentPath, units, seen, baselineCount));
            if (item.Entry is { Skip: { } skip } entry)
            {
                skipped.Add((item.Path, Enum.Parse<TextKey>(skip),
                    new IssueSubject([new IssueItem(side.Side, item.Path, entry.Id)], null)));
                if (entry.IsFolder)
                    skippedFolders.Add((side.Side, PathNames.KeyFor(side.Side, item.Path)));
            }
        }
        log.Event(RunEvent.Stage(key, ended: true, count: seen));
        var saveKey = side.Side == Side.Local ? TextKey.StageLocalSave : TextKey.StageDropboxSave;
        log.Event(RunEvent.Stage(saveKey));
        await sink.CompleteAsync();
        log.Event(RunEvent.Stage(saveKey, ended: true, count: seen));
        log.Observe(new ListingProgress(side.Side, currentPath, units, seen, baselineCount));
        log.FinishListing(side.Side);
        return (seen, incomplete);
    }

    private static async Task AbortAsync(IScanSink sink) => await sink.AbortAsync().ConfigureAwait(false);
}
