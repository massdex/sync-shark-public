using System.Globalization;
using Massdex.SyncShark.Engine.Edge.Results;
using Massdex.SyncShark.Engine.Run;

namespace Massdex.SyncShark.Engine.Edge;

/// <summary>The Engine host's four read commands (bug #92). They read the store the host already owns, so they take
/// no product lock; each read goes through Storage and only its rows are decoded, rendered and retried.</summary>
public static class EngineHostReads
{
    // Notice kinds that ask the user to decide; Info and Skipped only inform.
    private static readonly IReadOnlyList<IssueKind> ProblemKinds =
        [IssueKind.Failure, IssueKind.Tie, IssueKind.NameClash, IssueKind.DriveChange, IssueKind.RootIdentity];

    public static async Task<object> ReadAsync(EngineOperations operations, EngineHostReadKind kind,
        SyncCliInvocation invocation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(invocation);
        ct.ThrowIfCancellationRequested();
        return kind switch
        {
            EngineHostReadKind.AutoSyncStatus => await StatusAsync(operations, ct).ConfigureAwait(false),
            EngineHostReadKind.AutoSyncHistory => History(operations, invocation),
            EngineHostReadKind.ConflictsList => Conflicts(operations, invocation),
            EngineHostReadKind.AttentionList => Attention(operations),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
    }

    // The host's own transient status view, exactly as `engine host status` reports it.
    private static async Task<HostResult> StatusAsync(EngineOperations operations, CancellationToken ct)
    {
        var (status, detail) = await operations.HostStatusAsync(ct).ConfigureAwait(false);
        return new HostResult(status?.ToString() ?? string.Empty,
            IssuePresentation.RenderHostStatus(status, detail, null));
    }

    // The Engine store retains one Repair summary per pair. The legacy --after cursor has no Engine equivalent, so
    // the latest summary is always returned and an explicit warning says why (never silently ignored, never refused).
    private static SyncCliRunListData History(EngineOperations operations, SyncCliInvocation invocation)
    {
        // The registry requires --pair for this row before any runtime exists.
        var last = operations.RetainedRepair(invocation.PairId!);
        var runs = new List<SyncCliRunHistoryData>();
        if (last is not null && invocation.Limit > 0)
            runs.Add(new SyncCliRunHistoryData(last.RunId, Text.Get(TextKey.ResultRunKindRepair), last.Started,
                last.Finished, last.Completed ? Text.Get(TextKey.ResultRunCompleted) :
                    last.IncompleteReason ?? Text.Get(TextKey.ResultRunIncomplete)));
        return new SyncCliRunListData(runs.Count, runs)
        {
            Warning = invocation.Value(SyncCliRegistry.HistoryCursorOption) is null
                ? null : Text.Get(TextKey.ResultHistoryNoCursor)
        };
    }

    // One bounded page of open issues; only the page is decoded, rendered and retried, and the total is separate.
    private static SyncCliConflictListData Conflicts(EngineOperations operations, SyncCliInvocation invocation)
    {
        // An unknown pair is refused by its existing target error before any page is read.
        if (invocation.PairId is { } pairId) _ = operations.Pair(pairId);
        var search = string.IsNullOrEmpty(invocation.Search) ? null : invocation.Search;
        var (page, total) = operations.OpenIssuePage(invocation.PairId, invocation.AccountId, search, null,
            invocation.Limit, invocation.Offset, invocation.Flag(EngineCliCatalog.Flag(EngineOptionKey.Ignored)));
        var retry = RetryViews(operations, [page]);
        var items = page.Select(row => new SyncCliConflictData(row.Issue.Id.ToString(CultureInfo.InvariantCulture),
            row.PairId, row.Issue.Path, 0, null, null, null, null, null, null,
            IssuePolicy.PublicKind(row.Issue.Kind, row.Issue.Cause).ToString(),
            IssuePresentation.Render(row.Issue, IssuePresentation.View(retry.GetValueOrDefault(row.PairId), row.Issue)),
            Text.Get(TextKey.ResultConflictOpen))
        {
            Options = IssuePresentation.Options(row.Issue)
        }).ToArray();
        return new SyncCliConflictListData(items, invocation.Limit, invocation.Offset, items.Length,
            invocation.Offset + items.Length < total, total);
    }

    // Pairs whose open issues need a decision. Storage walks every problem notice and counts it inside one
    // read-only snapshot, so a reused issue id can neither hide a notice nor make the count disagree; the retry
    // projections run once per page after that snapshot is disposed, and nothing renders inside it.
    private static SyncCliAttentionData Attention(EngineOperations operations)
    {
        var pageSize = operations.PageTake;
        var (rows, total) = operations.OpenIssueSnapshot(ProblemKinds, pageSize);
        var retry = RetryViews(operations, rows.Chunk(pageSize));
        var items = rows.Select(row => new SyncCliAttentionItemData(row.PairId,
            IssuePolicy.PublicKind(row.Issue.Kind, row.Issue.Cause).ToString(),
            IssuePresentation.Render(row.Issue,
                IssuePresentation.View(retry.GetValueOrDefault(row.PairId), row.Issue)), true)).ToArray();
        return new SyncCliAttentionData(rows.Select(row => row.PairId).Distinct().ToArray(), [], total)
        {
            Items = items,
            PendingLabel = Text.Get(TextKey.ResultAttentionNoticeCount)
        };
    }

    // Retry views batched per pair per page, outside the snapshot; every page's map is kept, so a pair spanning
    // pages still finds the view of the notice it renders.
    private static Dictionary<string, Dictionary<long, IssueRetryView>> RetryViews(EngineOperations operations,
        IEnumerable<IEnumerable<(string PairId, Issue Issue)>> pages)
    {
        var views = new Dictionary<string, Dictionary<long, IssueRetryView>>(StringComparer.Ordinal);
        foreach (var page in pages)
        foreach (var group in page.GroupBy(row => row.PairId))
        {
            if (!views.TryGetValue(group.Key, out var merged)) views[group.Key] = merged = [];
            foreach (var (id, view) in operations.ReadIssueRetryViews(group.Key,
                group.Select(row => row.Issue.Id).ToArray())) merged[id] = view;
        }
        return views;
    }
}
