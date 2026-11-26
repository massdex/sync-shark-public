namespace Massdex.SyncShark.Engine;

/// <summary>A provider observation; an invalid job does not imply any particular member outcome.</summary>
public abstract record JobReply
{
    public sealed record Running : JobReply;
    public sealed record Invalid : JobReply;
    public sealed record Terminal(IReadOnlyList<(Entry? Entry, TextKey? Cause, FailureDisposition? Failure)> Members) : JobReply
    {
        /// <summary>Zero-based indices into Members whose selected ID was observed absent, not proof this request removed it. Reindex when projecting a subset.</summary>
        public IReadOnlySet<int> AbsentOrdinals { get; init; } = System.Collections.Frozen.FrozenSet<int>.Empty;
        /// <summary>A refused member's safe provider facts (route, HTTP status, Dropbox's error tag), for its failure's log line.</summary>
        public string? Refusal { get; init; }
    }
}
