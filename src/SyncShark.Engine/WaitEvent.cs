namespace Massdex.SyncShark.Engine;

/// <summary>A provider wait reported by the limiter (D33).</summary>
public sealed record WaitEvent(TextKey Reason, TimeSpan Wait);
