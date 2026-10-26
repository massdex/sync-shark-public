namespace Massdex.SyncShark.V2;

/// <summary>A durable result with the outcome computed by Storage, after all persistence coercions.</summary>
internal sealed record CommittedAction(SyncAction Action, ActionResult Result, Outcome Outcome);
