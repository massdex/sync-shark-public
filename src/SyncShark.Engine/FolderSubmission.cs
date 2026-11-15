namespace Massdex.SyncShark.Engine;

/// <summary>
/// Durable certainty for one provider submission. Member links belong to the effect rows,
/// keyed by submission ID and wire ordinal, rather than paths or enclosing roots.
/// Storage advances this snapshot with exact identity, generation and state comparison.
/// </summary>
public sealed record FolderSubmission(
    string SubmissionId,
    string PairId,
    long Generation,
    FolderSubmission.SubmissionPhase Phase,
    string? JobId,
    TextKey? Cause)
{
    /// <summary>Receipt certainty, not permission to replay an effect or settle a mapping.</summary>
    public enum SubmissionPhase
    {
        Armed,
        Accepted,
        Terminal,
        Unknown
    }
}
