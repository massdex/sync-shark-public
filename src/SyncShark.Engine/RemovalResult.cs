namespace Massdex.SyncShark.Engine;

/// <summary>The classified effect of one Delete or Publish (C-10). Callers read Outcome, never void return or path absence;
/// only a verified permitted removal may authorize publication or deletion settlement. On NoEffect, Retained, Unknown or
/// WrongRestored, Final names only a positively verified restored or current entry and authorizes no baseline or forget.
/// OperationId is absent when no pending note exists, as for free-target publication.</summary>
public sealed record RemovalResult(RemovalOutcome Outcome, string? OperationId, Entry? Final, TextKey? Cause)
{
    /// <summary>The journal position of the last record a synchronous Local publication wrote for Final, or null when
    /// none is proved; the settlement stores it so the dirty owner can recognize this write's own echo.</summary>
    public JournalPosition? LocalPublished { get; init; }

    /// <summary>The complete selected Local content from a definite no-effect refusal; never durable authority.</summary>
    public Entry? ObservedLocal { get; init; }
}
