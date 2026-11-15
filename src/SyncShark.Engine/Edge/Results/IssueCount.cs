namespace Massdex.SyncShark.Engine.Edge.Results;

/// <summary>One group of IssuesResult.ByCause.</summary>
public sealed record IssueCount(TextKey Cause, IssueKind Kind, int Count);
