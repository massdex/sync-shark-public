namespace Massdex.SyncShark.Engine;

/// <summary>One in-use item's exact retained responsibility (section 12.1): the issue, its lifecycle and CAS revision,
/// the retained work, and the pair generation/source epoch and subject the responsibility was recorded under.
/// Immutable applicability for the claim and expected-state checks; it carries no deadline, attempt count or other
/// timing state.</summary>
public sealed record ItemRetryToken(long IssueId, string EpisodeId, long Revision, long WorkId,
    long PairGeneration, long SourceEpoch, IssueSubject Subject);
