using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Massdex.SyncShark;

public sealed class SyncAuthorityDropboxMonitor : IAsyncDisposable, IDisposable
{
    private const int MaximumContinuationPages = 100_000;
    public string PairId => _pair.Id;
    private readonly SyncPair _pair;
    private readonly ISyncRemoteChangeFeed _feed;
    private readonly ISyncRemoteStore _remote;
    private readonly SyncAuthorityDatabase _authority;
    private readonly string _rootBindingId;
    private readonly string _folderId;
    private readonly string _endpointLabel;
    private readonly Func<string, Task> _onDurableHint;
    private readonly Func<string, Task> _onHealthChanged;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private SyncAuthorityMonitorWriter? _writer;
    private int _disposed;
    private string? _lastFailure;

    internal static Func<int, CancellationToken, Task>? BeforeCommitForTest { get; set; }
    internal static TimeSpan? FailureRetryDelayForTest { get; set; }

    public SyncAuthorityDropboxMonitor(SyncPair pair, ISyncRemoteStore remote, ISyncRemoteChangeFeed feed,
        SyncAuthorityDatabase authority, string rootBindingId, Func<string, Task> onDurableHint,
        TimeSpan? pollInterval = null, Func<string, Task>? onHealthChanged = null,
        string? folderId = null, string? endpointLabel = null)
    {
        _pair = pair;
        _remote = remote;
        _feed = feed;
        _authority = authority;
        _rootBindingId = rootBindingId;
        _folderId = folderId ?? pair.DropboxFolderId;
        _endpointLabel = endpointLabel ?? SyncProviders.DisplayName(pair.Provider);
        _onDurableHint = onDurableHint;
        _onHealthChanged = onHealthChanged ?? (_ => Task.CompletedTask);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
        if (_pollInterval < TimeSpan.FromMilliseconds(10) || _pollInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public string CursorId => "monitor-dropbox-" + Hash(_pair.Id, _rootBindingId)[..32];
    internal SyncPair Pair => _pair;

    /// <summary>
    /// Safe, bounded text for the most recent handled pump failure. It is published by this
    /// monitor's own fault boundary and retained across disposal so an absorbed error is never
    /// presented as success.
    /// </summary>
    public string? LastFailureReason => Volatile.Read(ref _lastFailure);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_pump is not null) throw new InvalidOperationException($"The {_endpointLabel} monitor is already active.");
        await _remote.VerifyRootIdentityAsync(cancellationToken);
        var identityJson = JsonSerializer.Serialize(new { folderId = _folderId });
        var retained = await _authority.LoadMonitorCursorAsync(CursorId, cancellationToken);
        if (retained is null)
        {
            var cursor = await _feed.GetLatestCursorAsync(cancellationToken);
            await _authority.RegisterMonitorCursorAsync(CursorId, _pair.Id, _rootBindingId,
                "dropbox_cursor", 1, identityJson, cursor, cancellationToken);
        }
        else if (retained.PairId != _pair.Id || retained.RootBindingId != _rootBindingId ||
                 retained.Kind != "dropbox_cursor" || retained.Generation != 1 ||
                 !string.Equals(retained.SourceIdentityJson, identityJson, StringComparison.Ordinal) ||
                 string.IsNullOrWhiteSpace(retained.CursorValue))
            throw new InvalidOperationException($"The retained {_endpointLabel} monitor cursor does not match this root.");
        _writer=await _authority.BeginMonitorWriterAsync(CursorId,_pair.Id,_rootBindingId,
            "dropbox_cursor",1,identityJson,cancellationToken);
        _pump = PumpAsync(_stop.Token, retained?.Status == "unavailable");
    }

    private async Task PumpAsync(CancellationToken cancellationToken, bool unavailable)
    {
        var continuationCursors = new HashSet<string>(StringComparer.Ordinal);
        var continuationPages = 0;
        var consecutiveFailures = 0;
        var writer=_writer ?? throw new InvalidOperationException("The monitor writer was not bound.");
        SyncAuthorityMonitorBatch? pendingBatch=null;
        var pendingHasMore=false;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if(pendingBatch is { } retry)
                {
                    await _authority.CommitMonitorBatchAsync(writer,retry,cancellationToken);
                    pendingBatch=null;
                    Volatile.Write(ref _lastFailure,null);consecutiveFailures=0;
                    if(unavailable){unavailable=false;await NotifyAsync(_onHealthChanged);}
                    if(retry.Events.Count>0)await NotifyAsync(_onDurableHint);
                    if(!pendingHasMore)await Task.Delay(_pollInterval,cancellationToken);
                    continue;
                }
                var cursor = writer.Cursor ??
                             throw new InvalidOperationException($"The active {_endpointLabel} cursor has no value.");
                SyncRemoteChangePage page;
                try
                {
                    page = await _feed.ContinueChangesAsync(cursor, cancellationToken);
                }
                catch (DropboxApiException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
                {
                    // The provider rejected the retained cursor and requires a full re-read. The
                    // reset event and the fresh cursor commit together in one authority
                    // transaction, so the retained cursor is never advanced on its own and no
                    // missed-change claim is made. A provider or database failure inside this
                    // recovery stays in this handled iteration and reaches the shared fault
                    // boundary below instead of escaping the pump.
                    continuationPages = 0;
                    continuationCursors.Clear();
                    var fresh = await _feed.GetLatestCursorAsync(cancellationToken);
                    const string evidence = "{\"reason\":\"dropbox cursor reset; full reconciliation required\"}";
                    var sourceKey = Hash(CursorId, "cursor-reset", fresh);
                    var reset = new SyncAuthorityMonitorEventInput("event-" + Hash(CursorId, sourceKey)[..32],
                        sourceKey, null, "cursor_reset", evidence);
                    if (BeforeCommitForTest is { } resetCommit) await resetCommit(1, cancellationToken);
                    pendingHasMore=true;
                    pendingBatch=_authority.PrepareMonitorBatch(writer,[reset],cursor,fresh);
                    await _authority.CommitMonitorBatchAsync(writer,pendingBatch,cancellationToken);
                    pendingBatch=null;
                    Volatile.Write(ref _lastFailure, null);
                    consecutiveFailures = 0;
                    if (unavailable)
                    {
                        unavailable = false;
                        await NotifyAsync(_onHealthChanged);
                    }
                    await NotifyAsync(_onDurableHint);
                    continue;
                }
                if (page.HasMore)
                {
                    if (continuationPages == 0) continuationCursors.Add(Hash(cursor));
                    if (++continuationPages > MaximumContinuationPages ||
                        !continuationCursors.Add(Hash(page.Cursor)))
                        throw new InvalidDataException(
                            $"The {_endpointLabel} change feed cycled or exceeded its bounded continuation sequence.");
                }
                else
                {
                    continuationPages = 0;
                    continuationCursors.Clear();
                }
                var events = page.Entries.Select((item, index) =>
                {
                    var sourceKey = Hash(cursor, index.ToString(), item.EventKind,
                        item.RelativePath ?? "<pair>", item.EvidenceJson);
                    return new SyncAuthorityMonitorEventInput("event-" + Hash(CursorId, sourceKey)[..32],
                        sourceKey, item.RelativePath, "dropbox_" + item.EventKind, item.EvidenceJson);
                }).ToArray();
                if (BeforeCommitForTest is { } beforeCommit) await beforeCommit(events.Length, cancellationToken);
                pendingHasMore=page.HasMore;
                pendingBatch=_authority.PrepareMonitorBatch(writer,events,cursor,page.Cursor);
                await _authority.CommitMonitorBatchAsync(writer,pendingBatch,cancellationToken);
                pendingBatch=null;
                Volatile.Write(ref _lastFailure, null);
                consecutiveFailures = 0;
                if (unavailable)
                {
                    unavailable = false;
                    await NotifyAsync(_onHealthChanged);
                }
                if (events.Length > 0) await NotifyAsync(_onDurableHint);
                if (!page.HasMore) await Task.Delay(_pollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SyncMonitorWriterReplacedException exception)
            {
                Volatile.Write(ref _lastFailure,SafeFailureText(exception));
                await NotifyAsync(_onHealthChanged);
                return;
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _lastFailure, SafeFailureText(exception));
                continuationPages = 0;
                continuationCursors.Clear();
                consecutiveFailures++;
                if (!unavailable)
                {
                    unavailable = true;
                    try
                    {
                        await _authority.SetMonitorCursorStatusAsync(writer, "unavailable",
                            CancellationToken.None);
                    }
                    catch { }
                    await NotifyAsync(_onHealthChanged);
                }
                await DelayBeforeRetryAsync(consecutiveFailures, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_pump is null) return;
        // A failed pump must not escape disposal and must not be concealed as success: the safe
        // failure text stays readable for the owner.
        try
        {
            await _pump;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Volatile.Write(ref _lastFailure, SafeFailureText(exception)); }
    }

    private async Task DelayBeforeRetryAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        // Bounded exponential backoff: a failing provider or database consumer is retried, but the
        // monitor never spins or waits indefinitely inside a handled fault.
        var delay = FailureRetryDelayForTest ?? TimeSpan.FromMilliseconds(
            Math.Min(30_000, 500 * Math.Pow(2, Math.Min(consecutiveFailures - 1, 6))));
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
        else await Task.Yield();
    }

    private static string SafeFailureText(Exception exception)
    {
        var text = SyncActivityText.Failure(exception);
        return text.Length <= 400 ? text : text[..400];
    }

    private async Task NotifyAsync(Func<string, Task> callback)
    {
        try { await callback(_pair.Id); }
        catch { }
    }

    private static string Hash(params string[] parts) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)))).ToLowerInvariant();
}
