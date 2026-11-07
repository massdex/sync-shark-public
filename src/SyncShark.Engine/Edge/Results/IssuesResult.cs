using System.Globalization;

namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>Section 9.3: ResultIssueGroup per cause, each followed by its issues as ResultIssueLine, each issue's own
/// cause sentence, Retry guidance once per Failure group and the resolve choices for a drive-change notice.</summary>
public sealed record IssuesResult(IReadOnlyList<Issue> Issues, IReadOnlyList<IssueCount> ByCause) : IEngineCliData
{
    /// <summary>The exact pair these notices belong to; its id fills the guidance sentences.</summary>
    public string? PairId { get; init; }

    /// <summary>Run's current retry presentation for each issue on this page; a missing entry is unavailable.</summary>
    public IReadOnlyDictionary<long, IssueRetryView>? RetryViews { get; init; }

    /// <summary>Groups the issues by cause and kind, counting every occurrence; groups keep first-appearance order.</summary>
    public static IssuesResult From(IReadOnlyList<Issue> issues)
    {
        var counts = new Dictionary<(TextKey Cause, IssueKind Kind), int>();
        var order = new List<(TextKey Cause, IssueKind Kind)>();
        foreach (var issue in issues)
        {
            var key = (issue.Cause, issue.Kind);
            if (!counts.TryGetValue(key, out var count)) order.Add(key);
            counts[key] = count + 1;
        }
        return new(issues, order.Select(key =>
            new IssueCount(key.Cause, key.Kind, counts[key])).ToArray());
    }

    /// <summary>Groups the exact pair's issues and keeps that pair for the command guidance.</summary>
    public static IssuesResult From(string pairId, IReadOnlyList<Issue> issues) =>
        From(issues) with { PairId = pairId };

    /// <summary>One group line per cause, each followed by its issue lines, cause text and applicable guidance.</summary>
    public string RenderHuman()
    {
        var lookup = Issues.ToLookup(issue => (issue.Cause, issue.Kind));
        return string.Join(Environment.NewLine, ByCause.SelectMany(group =>
            Lines(group, lookup[(group.Cause, group.Kind)])));
    }

    public int ExitCode => (int)SyncCliExitCode.Success;

    private IEnumerable<string> Lines(IssueCount group, IEnumerable<Issue> issues)
    {
        var pairId = PairId;
        yield return Text.Get(TextKey.ResultIssueGroup, (TextArg.Kind, group.Kind.ToString()),
            (TextArg.Cause, group.Cause.ToString()), (TextArg.Count, group.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var issue in issues)
        {
            var id = issue.Id.ToString(CultureInfo.InvariantCulture);
            yield return Text.Get(TextKey.ResultIssueLine, (TextArg.Id, id),
                (TextArg.Path, issue.Path), (TextArg.At, issue.At.ToString(CultureInfo.InvariantCulture)));
            yield return CauseText(issue, id);
            if (pairId is not null)
            {
                var choice = issue.Ignored ? ResolutionChoice.Unignore : ResolutionChoice.Ignore;
                var option = IssuePresentation.Options(issue).Single(option => option.Choice == choice.ToString());
                yield return Text.Get(TextKey.ResultIssueVisibilityChoice, (TextArg.Name, option.Label),
                    (TextArg.Pair, pairId), (TextArg.Id, id), (TextArg.Value, issue.Reviewed ?? string.Empty),
                    (TextArg.Choice, EngineCliCatalog.OptionValue(EngineOptionKey.Choice, choice.ToString())));
                yield return IssuePresentation.ResolutionConfirmation(issue, choice);
            }
            if (issue.Kind == IssueKind.DriveChange && pairId is not null)
                yield return Text.Get(TextKey.ResultDriveChoices, (TextArg.Pair, pairId),
                    (TextArg.Id, id), (TextArg.Value, issue.Reviewed ?? string.Empty));
        }
        if (group.Kind == IssueKind.Failure && pairId is not null)
            yield return Text.Get(TextKey.ResultFailureRetry, (TextArg.Pair, pairId));
    }

    // Keep-both locations and in-use guidance share the UI renderer; other templates keep this listing's
    // operands ({Id}, {Pair}) so a template's placeholder never renders as a literal.
    private string CauseText(Issue issue, string id) => issue.Cause == TextKey.InfoKeptBoth ||
        IssuePolicy.IsLocalAccessRetryCause(issue.Cause)
        ? IssuePresentation.Render(issue, ViewFor(issue))
        : Text.Get(issue.Cause, (TextArg.Path, issue.Path), (TextArg.Message, ""), (TextArg.Id, id),
            (TextArg.Pair, PairId ?? ""));

    private IssueRetryView ViewFor(Issue issue) => IssuePresentation.View(RetryViews, issue);
}
