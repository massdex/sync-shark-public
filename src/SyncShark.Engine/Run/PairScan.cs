using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Run;

/// <summary>The disposable pair-wide listing of one real Repair (ROOTID 5.1): both side listings stream into the
/// supplied sinks and the skipped facts they collect. A side's listing retries its own transient page reads, so
/// pages already ingested stay in the running scan; a read that cannot finish aborts both sinks and never restarts
/// the acquisition, except an invalid saved discovery cursor falls back to fresh Dropbox discovery.</summary>
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

    /// <summary>The joined remote acquisition cursor; Repair publishes only after its second root admission.</summary>
    public string? DeletedHistoryCursor { get; private set; }

    /// <summary>Streams both listings; returns null when both finished, else the incomplete listing's reason.</summary>
    public async Task<string?> RunAsync(TaskSet tasks, RunLog log, bool includeDeleted, long baselineCount, CancellationToken ct)
    {
        // The local walk never yields asynchronously, so it needs its own task or it would finish before
        // the Dropbox listing starts.
        var localTask = Task.Run(() => ListAsync(localSide, localSink, _localSkipped, _localSkippedFolders, false, log, baselineCount, ct));
        tasks.Add(localTask);
        var dropboxTask = ListDropboxAsync(includeDeleted, log, baselineCount, ct);
        tasks.Add(dropboxTask);
        try
        {
            await Task.WhenAll(localTask, dropboxTask);
            Observations = localTask.Result + dropboxTask.Result;
            return null;
        }
        catch (Exception error)
        {
            // Abort both writers on failure; only an incomplete inventory returns a summary.
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
            if (error is not InventoryIncompleteException) throw;
            log.Failed(error);
            return error.Message;
        }
    }

    private async Task<long> ListDropboxAsync(bool includeDeleted, RunLog log, long baselineCount, CancellationToken ct)
    {
        var cursor = historyCursor;
        string? terminal = null;
        long seen;
        try
        {
            seen = await ListAsync(dropboxSide, _dropboxSink, _dropboxSkipped, _dropboxSkippedFolders,
                includeDeleted, log, baselineCount, ct, cursor, value => terminal = value);
        }
        catch (ListingCursorResetException) when (cursor is not null)
        {
            // Join queued delta events before replacing their remote inventory, leaving Local untouched.
            await _dropboxSink.CompleteAsync();
            store.ResetDeletedHistory(pairId, scanToken, discardNames: true);
            _dropboxSink = store.OpenScan(pairId, Side.Dropbox, batchRows);
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
            _dropboxSink = store.OpenScan(pairId, Side.Dropbox, batchRows);
            _dropboxSkipped.Clear();
            _dropboxSkippedFolders.Clear();
            seen = await ListAsync(dropboxSide, _dropboxSink, _dropboxSkipped, _dropboxSkippedFolders,
                false, log, baselineCount, ct);
        }
        if (includeDeleted) DeletedHistoryCursor = terminal;
        return seen;
    }

    private static async Task<long> ListAsync(ISide side, IScanSink sink,
        List<(string Path, TextKey Cause, IssueSubject Subject)> skipped, List<(Side Side, string Key)> skippedFolders,
        bool includeDeleted, RunLog log, long baselineCount, CancellationToken ct,
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
            Progress = (path, completedUnits) =>
            {
                currentPath = path;
                units = completedUnits;
                log.Observe(new ListingProgress(side.Side, path, units, seen, baselineCount));
            }
        };
        await foreach (var item in side.ReadAllAsync(string.Empty, options, ct))
        {
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
        return seen;
    }

    private static async Task AbortAsync(IScanSink sink) => await sink.AbortAsync().ConfigureAwait(false);
}
