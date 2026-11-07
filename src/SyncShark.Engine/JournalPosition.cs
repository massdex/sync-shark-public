namespace Massdex.SyncShark.Engine;

/// <summary>Durable journal identity and next position for one volume (section 4.1).</summary>
public sealed record JournalPosition(ulong JournalId, ulong NextUsn);
