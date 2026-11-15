namespace Massdex.SyncShark.Engine;

/// <summary>How a repeated name-clash refusal stands against its stored issue (B-123): already covered, the issue
/// continues but the member needs attaching, or a fresh raise.</summary>
public enum ClashRefusal { Covered, Attach, Raise }
