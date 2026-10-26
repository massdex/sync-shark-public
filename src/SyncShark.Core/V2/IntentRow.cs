namespace Massdex.SyncShark.V2;

/// <summary>One unresolved intent with the state it was streamed with, so a scan need not re-read it per row.</summary>
public sealed record IntentRow(SyncAction Action, string State);
