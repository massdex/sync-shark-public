namespace Massdex.SyncShark.Engine;

/// <summary>The classified effect of one Delete or Publish (C-10). Callers read Outcome, never void return or path absence;
/// only a verified permitted removal may authorize publication or deletion settlement. On NoEffect, Retained, Unknown or
/// WrongRestored, Final names only a positively verified restored or current entry and authorizes no baseline or forget.
/// OperationId is absent when no pending note exists, as for free-target publication.</summary>
public sealed record RemovalResult(RemovalOutcome Outcome, string? OperationId, Entry? Final, TextKey? Cause);
