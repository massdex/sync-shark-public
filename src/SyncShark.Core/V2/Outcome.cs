namespace Massdex.SyncShark.V2;

/// <summary>How an applied action ended.</summary>
// Refused means no effect occurred; the owning notice retains the cause and retry authority.
public enum Outcome { Done, Skipped, Failed, Uncertain, Refused }
