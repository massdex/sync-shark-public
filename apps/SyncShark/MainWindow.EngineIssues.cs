using System.Globalization;
using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Edge;

namespace Massdex.SyncShark;

/// <summary>Engine issue projection (section 12.1, LOCAL-IN-USE D3): the one UI seam that keeps the existing
/// DecisionRow notice rows in step with the Engine's authoritative issue list and retry views.</summary>
public partial class MainWindow
{
    // The row association: the exact issue id owns the one row that shows it, never a match on rendered text.
    private readonly Dictionary<long, DecisionRow> _engineIssueRows = [];
    private readonly Dictionary<long, Issue> _engineIssues = [];
    private bool _engineIgnored;

    /// <summary>Reprojects the selected pair's notices from the authoritative issue list. An existing row keeps its
    /// exact issue id and takes the shared renderer's cause and guidance; an id now naming a different issue record
    /// replaces the row whole so a stale Path or Outcome is never shown; a new issue gets a row; only an issue
    /// absent from the list (a successful matching closure) removes its row. A status or CLOSE event alone removes
    /// nothing because only this list decides what is current.</summary>
    internal void ApplyEngineIssues(IReadOnlyList<Issue> issues, IReadOnlyDictionary<long, IssueRetryView> retryViews)
    {
        var pair = _pairSet.Pairs.FirstOrDefault(item =>
            string.Equals(item.Id, _pairSet.SelectedPairId, StringComparison.Ordinal));
        ApplyEngineIssues(pair?.Id, pair?.Name, issues, retryViews);
    }

    internal void ApplyEngineIssues(string? pairId, string? pairName, IReadOnlyList<Issue> issues,
        IReadOnlyDictionary<long, IssueRetryView> retryViews)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(retryViews);
        if (pairId is not null && _engineIssueRows.Values.Any(row => row.PairId != pairId))
        {
            foreach (var row in _engineIssueRows.Values) ConflictRows.Remove(row);
            _engineIssueRows.Clear();
        }
        _engineIssues.Clear();
        var positions = RowPositions();
        var current = new HashSet<long>();
        foreach (var issue in issues)
        {
            _engineIssues[issue.Id] = issue;
            current.Add(issue.Id);
            var view = IssuePresentation.View(retryViews, issue);
            DecisionRow? candidate = _engineIssueRows.TryGetValue(issue.Id, out var existing)
                ? existing.Path == issue.Path &&
                    existing.Outcome == IssuePolicy.PublicKind(issue.Kind, issue.Cause).ToString()
                    ? existing with
                    {
                        Reason = IssuePresentation.Render(issue, view), NoticeOptions = IssuePresentation.Options(issue)
                    }
                    : NewRow(existing.PairId, existing.PairName, issue, view)
                : pairId is null || pairName is null ? null : NewRow(pairId, pairName, issue, view);
            if (candidate is null) continue;
            _engineIssueRows[issue.Id] = candidate;
            if (positions.TryGetValue(issue.Id, out var index)) ConflictRows[index] = candidate;
            else { positions[issue.Id] = ConflictRows.Count; ConflictRows.Add(candidate); }
        }
        CloseRows(current);
        foreach (var closed in _engineIssueRows.Keys.Where(issueId => !current.Contains(issueId)).ToArray())
            _engineIssueRows.Remove(closed);
    }

    /// <summary>The exact issue id a projected row carries, or null for a row this projection did not create.</summary>
    internal static long? EngineIssueId(DecisionRow row) =>
        long.TryParse(row.ConflictId, NumberStyles.None, CultureInfo.InvariantCulture, out var issueId) ? issueId : null;

    // The issue list records no provider, so the row keeps the neutral unrecorded label instead of claiming one.
    private static DecisionRow NewRow(string pairId, string pairName, Issue issue, IssueRetryView view) =>
        new(pairId, pairName, issue.Path, IssuePolicy.PublicKind(issue.Kind, issue.Cause).ToString(),
            IssuePresentation.Render(issue, view),
            Connection: ActivityCloud.UnrecordedLabel,
            ConflictId: issue.Id.ToString(CultureInfo.InvariantCulture),
            NoticeOptions: IssuePresentation.Options(issue));

    /// <summary>One index of the notice list by exact issue id, so an update never scans the list per issue.</summary>
    private Dictionary<long, int> RowPositions()
    {
        var positions = new Dictionary<long, int>();
        for (var index = default(int); index < ConflictRows.Count; index++)
            if (EngineIssueId(ConflictRows[index]) is { } issueId) positions[issueId] = index;
        return positions;
    }

    /// <summary>Removes only the rows whose exact issue id is no longer current; legacy notices stay untouched.</summary>
    private void CloseRows(HashSet<long> current)
    {
        for (var index = default(int); index < ConflictRows.Count;)
            if (EngineIssueId(ConflictRows[index]) is { } issueId && !current.Contains(issueId))
                ConflictRows.RemoveAt(index);
            else index++;
    }

}
